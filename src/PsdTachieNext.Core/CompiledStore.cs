using System.Buffers;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PsdTachieNext.Core;

/// <summary>Writes blocks sequentially. Never retains every layer's decoded pixels.</summary>
public sealed class CompiledStoreWriter : IDisposable
{
    private readonly FileStream pixels;
    private readonly FormatLimits limits;
    private readonly List<BlockInfo> blocks = [];
    private bool completed;
    private readonly long maximumOutputBytes;
    private readonly Action<long>? admitOutput;
    public ImmutableArray<BlockInfo> Blocks => blocks.ToImmutableArray();
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true, MaxDepth = 64,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public CompiledStoreWriter(string directory, FormatLimits? limits = null, long maximumOutputBytes = long.MaxValue,
        Action<long>? admitOutput = null)
    {
        this.limits = limits ?? new();
        this.maximumOutputBytes = maximumOutputBytes;
        this.admitOutput = admitOutput;
        Admit(CompiledFormat.HeaderSize);
        Directory.CreateDirectory(directory);
        pixels = new FileStream(Path.Combine(directory, "pixels.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        pixels.Write(new byte[CompiledFormat.HeaderSize]);
    }

    public int AddBlock(BlockFormat format, int width, int height, ReadOnlySpan<byte> bytes, bool compress = true)
    {
        if (completed) throw new InvalidOperationException("Store already completed.");
        if (blocks.Count >= limits.MaxBlocks || bytes.IsEmpty || bytes.Length > limits.MaxBlockBytes)
            throw new InvalidDataException("Block exceeds configured bounds.");
        var expected = CompiledFormat.ByteLength(format, width, height, limits);
        if (format != BlockFormat.Metadata && bytes.Length != expected)
            throw new InvalidDataException("Pixel payload size mismatch.");
        byte[]? compressed = null;
        var length = bytes.Length;
        var codec = BlockCodec.Raw;
        if (compress && bytes.Length >= 64)
        {
            compressed = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
            if (BrotliEncoder.TryCompress(bytes, compressed, out var written, quality: 1, window: 22) && written < bytes.Length)
            { length = written; codec = BlockCodec.Brotli; }
        }
        if (checked(pixels.Position + length) > limits.MaxStoreBytes) throw new InvalidDataException("Pixel store exceeds limit.");
        Admit(checked(pixels.Position + length));
        var id = blocks.Count;
        var info = new BlockInfo(id, format, width, height, pixels.Position, length, bytes.Length, codec, CompiledFormat.Hash(bytes));
        if (codec == BlockCodec.Brotli) pixels.Write(compressed.AsSpan(0, length));
        else pixels.Write(bytes);
        blocks.Add(info);
        return id;
    }

    public void Complete(string directory, CompiledManifest manifest)
    {
        if (completed) throw new InvalidOperationException("Store already completed.");
        if (!manifest.Blocks.SequenceEqual(blocks)) throw new InvalidDataException("Manifest/writer block mismatch.");
        CompiledFormat.Validate(manifest, pixels.Length, limits);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (bytes.Length > limits.MaxManifestBytes) throw new InvalidDataException("Manifest exceeds limit.");
        Admit(checked(pixels.Length + bytes.Length));
        pixels.Position = 0;
        pixels.Write(CompiledFormat.Magic);
        pixels.Write(Convert.FromHexString(CompiledFormat.Hash(bytes)));
        pixels.Flush(flushToDisk: true);
        using (var output = new FileStream(Path.Combine(directory, "manifest.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Flush(flushToDisk: true); }
        completed = true;
    }
    public void Dispose() => pixels.Dispose();
    private void Admit(long bytes)
    {
        if (bytes > maximumOutputBytes) throw new CacheCapacityException("snapshot・使用中世代・新出力の合計がdisk容量予算を超えます。不要なSourceを閉じて再読込してください。");
        admitOutput?.Invoke(bytes);
    }
}

/// <summary>Opens only the manifest/index. Pixel blocks are read and authenticated on demand.</summary>
public sealed class CompiledDocument : IDisposable
{
    private readonly FileStream pixels;
    private readonly object gate = new();
    private bool disposed;
    public CompiledManifest Manifest { get; }
    public long BlockReadCount { get; private set; }
    public long BytesRead { get; private set; }

    public CompiledDocument(string directory, FormatLimits? limits = null)
    {
        limits ??= new();
        var manifestPath = Path.Combine(directory, "manifest.json");
        using var manifestFile = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (manifestFile.Length <= 0 || manifestFile.Length > limits.MaxManifestBytes)
            throw new InvalidDataException("Manifest exceeds configured bounds.");
        var bytes = new byte[checked((int)manifestFile.Length)];
        manifestFile.ReadExactly(bytes);
        pixels = new FileStream(Path.Combine(directory, "pixels.bin"), FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess);
        try
        {
            Span<byte> header = stackalloc byte[CompiledFormat.HeaderSize];
            pixels.ReadExactly(header);
            if (!header[..8].SequenceEqual(CompiledFormat.Magic) ||
                !header[8..].SequenceEqual(Convert.FromHexString(CompiledFormat.Hash(bytes))))
                throw new InvalidDataException("Manifest/store identity mismatch. Recompile the source.");
            Manifest = JsonSerializer.Deserialize<CompiledManifest>(bytes, CompiledStoreWriter.JsonOptions)
                ?? throw new InvalidDataException("Empty manifest.");
            CompiledFormat.Validate(Manifest, pixels.Length, limits);
        }
        catch { pixels.Dispose(); throw; }
    }

    public byte[] ReadBlock(int id)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if ((uint)id >= (uint)Manifest.Blocks.Length) throw new ArgumentOutOfRangeException(nameof(id));
            var b = Manifest.Blocks[id];
            var encoded = new byte[b.StoredLength];
            var consumed = 0;
            while (consumed < encoded.Length)
            {
                var count = RandomAccess.Read(pixels.SafeFileHandle, encoded.AsSpan(consumed), b.Offset + consumed);
                if (count == 0) throw new EndOfStreamException("Truncated pixel block.");
                consumed += count;
            }
            byte[] raw;
            if (b.Codec == BlockCodec.Raw) raw = encoded;
            else
            {
                raw = new byte[b.RawLength];
                using var decoder = new BrotliDecoder();
                var status = decoder.Decompress(encoded, raw, out var read, out var written);
                if (status != OperationStatus.Done || read != encoded.Length || written != raw.Length)
                    throw new InvalidDataException("Malformed/overlong compressed block.");
            }
            if (CompiledFormat.Hash(raw) != b.Sha256) throw new InvalidDataException("Pixel block checksum mismatch.");
            BlockReadCount++; BytesRead += encoded.Length;
            return raw;
        }
    }
    public void VerifyAll()
    {
        for (var i = 0; i < Manifest.Blocks.Length; i++) _ = ReadBlock(i);
    }
    public void Dispose()
    {
        lock (gate) { if (disposed) return; disposed = true; pixels.Dispose(); }
    }
}
