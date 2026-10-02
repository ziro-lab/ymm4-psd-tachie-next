using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using PsdParser;
using PsdParser.AdditionalLayerInformations;
using PsdTachieNext.Core;

namespace PsdTachieNext.Compiler;

public sealed class PsdCompiler(FormatLimits? limits = null)
{
    private readonly FormatLimits limits = limits ?? new();
    internal Action? AfterLayerCompiled { get; set; }

    /// <summary>
    /// Compiles a stable disk snapshot to a new directory, then atomically publishes it.
    /// Existing generations are never overwritten. The caller chooses when old generations can be retired.
    /// </summary>
    public string Compile(string sourcePath, string cacheRoot, CancellationToken cancellationToken = default)
    {
        cacheRoot = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        using var snapshot = CreateSnapshot(sourcePath, cacheRoot, cancellationToken);
        return CompileSnapshot(snapshot, cacheRoot, cancellationToken);
    }

    public SourceSnapshot CreateSnapshot(string sourcePath, string snapshotRoot, CancellationToken cancellationToken = default,
        long maximumSnapshotBytes = long.MaxValue)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        Directory.CreateDirectory(snapshotRoot);
        var staging = Path.Combine(snapshotRoot, ".snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var snapshot = Path.Combine(staging, "source.psd");
        try
        {
            SourceFingerprint fingerprint;
            using (var input = OpenOriginal(sourcePath))
            {
                if (input.Length < 26 || input.Length > limits.MaxSourceBytes)
                    throw new InvalidDataException("Source exceeds configured bounds.");
                using var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024]; long total = 0; int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total = checked(total + count);
                    if (total > maximumSnapshotBytes) throw new CacheCapacityException("元PSDのsnapshotがdisk容量予算を超えます。不要なSourceを閉じて再読込してください。");
                    if (total > limits.MaxSourceBytes) throw new InvalidDataException("Source grew beyond the limit.");
                    hash.AppendData(buffer, 0, count); output.Write(buffer, 0, count);
                }
                fingerprint = new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), total);
                output.Flush(flushToDisk: true);
            }
            ValidateHeader(snapshot);
            return new SourceSnapshot(sourcePath, snapshot, fingerprint);
        }
        catch { Directory.Delete(staging, true); throw; }
    }

    /// <summary>Consumes an owned snapshot without copying it again. Caller retains it until completion.</summary>
    public string CompileSnapshot(SourceSnapshot source, string cacheRoot, CancellationToken cancellationToken = default,
        long maximumOutputBytes = long.MaxValue, Action<long>? admitOutput = null)
    {
        source.ThrowIfDisposed();
        cacheRoot = Path.GetFullPath(cacheRoot);
        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(cacheRoot, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var snapshot = source.Path;
            var fingerprint = source.Fingerprint;
            var generation = CompiledFormat.Generation(fingerprint);
            using (var writer = new CompiledStoreWriter(staging, limits, maximumOutputBytes, admitOutput))
            using (var raw = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var psd = new PsdFile(snapshot))
            {
                var h = psd.Header;
                raw.Position = 26;
                var colorData = ReadSection(raw, writer);
                var resources = ReadSection(raw, writer);
                var nodes = new List<LayerNode>();
                var parents = new Stack<int>();
                var orders = new Dictionary<int, int>();
                var records = psd.LayerAndMaskInformationSection.LayerInfo.Items;
                if (records.Length > limits.MaxNodes * 2L) throw new InvalidDataException("Too many PSD layer records.");
                // PSD records are bottom-to-top; traverse reversed to recover group/header order.
                foreach (var pair in records.Reverse())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var r = pair.Record;
                    var divider = r.AdditionalLayerInformations.OfType<SectionDividerSetting>().FirstOrDefault();
                    if (divider?.Type == SectionDividerSetting.LsctType.BoundingSectionDivider)
                    {
                        if (parents.Count == 0) throw new InvalidDataException("Unbalanced PSD group boundary.");
                        parents.Pop(); continue;
                    }
                    var kind = divider?.Type is SectionDividerSetting.LsctType.OpenedFolder or SectionDividerSetting.LsctType.ClosedFolder
                        ? NodeKind.Group : NodeKind.Layer;
                    var bounds = Rect(r.Left, r.Top, r.Right, r.Bottom);
                    var parent = parents.Count == 0 ? (int?)null : parents.Peek();
                    var orderKey = parent ?? -1;
                    var order = orders.GetValueOrDefault(orderKey); orders[orderKey] = order + 1;
                    if (nodes.Count >= limits.MaxNodes) throw new InvalidDataException("Too many nodes.");
                    var id = nodes.Count;
                    int? pixel = null;
                    if (kind == NodeKind.Layer && bounds.Width > 0 && bounds.Height > 0)
                    {
                        CompiledFormat.ByteLength(BlockFormat.Bgra8Straight, bounds.Width, bounds.Height, limits);
                        var bytes = pair.Image.Read();
                        pixel = writer.AddBlock(BlockFormat.Bgra8Straight, bounds.Width, bounds.Height, bytes);
                    }
                    MaskInfo? mask = null;
                    var md = r.LayerMaskAndAdjustmentLayerData;
                    if (md.Size > 0)
                    {
                        var rect = Rect(md.Left, md.Top, md.Right, md.Bottom);
                        var planes = new List<MaskPlane>();
                        foreach (var channel in pair.Image.ChannelImages.Where(c => (int)c.ChannelId < -1))
                        {
                            if (channel.ChannelId != ChannelId.UserSuppliedLayerMask)
                                throw new NotSupportedException("P0 does not interpret real-user-mask channel -3. No lossy fallback is used.");
                            var length = CompiledFormat.ByteLength(BlockFormat.Gray8, rect.Width, rect.Height, limits);
                            var bytes = new byte[length]; var row = new byte[rect.Width];
                            for (var y = 0; y < rect.Height; y++)
                            { cancellationToken.ThrowIfCancellationRequested(); channel.ReadLine(row, y); row.CopyTo(bytes, y * rect.Width); }
                            planes.Add(new((int)channel.ChannelId, writer.AddBlock(BlockFormat.Gray8, rect.Width, rect.Height, bytes)));
                        }
                        mask = new(rect, md.DefaultColor, (int)md.Flags, md.MaskParameters, md.MaskDensity, md.MaskFeather,
                            (int)md.RealFlags, md.RealUserMaskBackground,
                            Rect(md.MaskLeft, md.MaskTop, md.MaskRight, md.MaskBottom), planes.ToImmutableArray());
                    }
                    var extras = new List<ExtraTag>();
                    foreach (var tag in r.AdditionalLayerInformations)
                    {
                        if (tag.Length == 0) continue;
                        if (tag.Length < 0 || tag.Length > limits.MaxExtraTagBytes || tag.Position < 0 ||
                            tag.Position > raw.Length - tag.Length) throw new InvalidDataException("Extra tag exceeds source/size bounds.");
                        raw.Position = tag.Position;
                        var bytes = new byte[checked((int)tag.Length)]; raw.ReadExactly(bytes);
                        extras.Add(new(tag.KeyName, writer.AddBlock(BlockFormat.Metadata, 0, 0, bytes)));
                    }
                    var name = r.AdditionalLayerInformations.OfType<UnicodeLayerName>().FirstOrDefault()?.Name ?? r.LayerName;
                    var blend = kind == NodeKind.Group ? divider?.BlendMode ?? r.BlendMode : r.BlendMode;
                    nodes.Add(new(id, parent, order, kind, name, bounds, (byte)r.LayerFlags,
                        (((byte)r.LayerFlags) & 2) == 0, FourCC((int)blend), r.Opacity, r.Clipping,
                        pixel, mask, extras.ToImmutableArray()));
                    if (kind == NodeKind.Group) parents.Push(id);
                    else AfterLayerCompiled?.Invoke();
                }
                if (parents.Count != 0) throw new InvalidDataException("Unclosed PSD group.");
                var manifest = new CompiledManifest(CompiledFormat.Version, CompiledFormat.CompilerId, generation,
                    fingerprint, h.Version, h.Width, h.Height, h.Depth, (int)h.ColorMode, colorData, resources,
                    nodes.ToImmutableArray(), writer.Blocks);
                writer.Complete(staging, manifest);
            }
            cancellationToken.ThrowIfCancellationRequested();
            source.VerifyOriginal(cancellationToken);
            using (var verify = new CompiledDocument(staging, limits)) verify.VerifyAll();
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(cacheRoot, generation + "-" + Guid.NewGuid().ToString("N"));
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private void ValidateHeader(string path)
    {
        using var file = File.OpenRead(path); Span<byte> h = stackalloc byte[26]; file.ReadExactly(h);
        var version = BinaryPrimitives.ReadUInt16BigEndian(h[4..]);
        var height = BinaryPrimitives.ReadInt32BigEndian(h[14..]); var width = BinaryPrimitives.ReadInt32BigEndian(h[18..]);
        if (!h[..4].SequenceEqual("8BPS"u8) || version is not (1 or 2) ||
            BinaryPrimitives.ReadUInt16BigEndian(h[22..]) != 8 || BinaryPrimitives.ReadUInt16BigEndian(h[24..]) != 3)
            throw new NotSupportedException("P0 accepts RGB8 PSD/PSB only; no implicit depth/color conversion.");
        if (height <= 0 || width <= 0 || height > limits.MaxDimension || width > limits.MaxDimension)
            throw new InvalidDataException("Canvas exceeds configured bounds.");
    }
    private int? ReadSection(Stream source, CompiledStoreWriter writer)
    {
        Span<byte> lengthBytes = stackalloc byte[4]; source.ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length == 0) return null;
        if (length > limits.MaxBlockBytes || length > source.Length - source.Position)
            throw new InvalidDataException("Metadata section exceeds bounds.");
        var bytes = new byte[checked((int)length)]; source.ReadExactly(bytes);
        return writer.AddBlock(BlockFormat.Metadata, 0, 0, bytes);
    }
    private PixelRect Rect(int left, int top, int right, int bottom)
    {
        var w = (long)right - left; var h = (long)bottom - top;
        if (w < 0 || h < 0 || w > limits.MaxDimension || h > limits.MaxDimension)
            throw new InvalidDataException("Invalid PSD rectangle.");
        return new(left, top, (int)w, (int)h);
    }
    private static string FourCC(int value) => new([(char)((value >> 24) & 255), (char)((value >> 16) & 255),
        (char)((value >> 8) & 255), (char)(value & 255)]);
    internal static string HashStream(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024]; int count;
        while ((count = stream.Read(buffer)) != 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    internal static FileStream OpenOriginal(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) is 32 or 33)
        { throw new SourceChangedDuringPreparationException("元PSDが保存・更新処理で使用中です。", error); }
    }
}
