using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace PsdTachieNext.Core;

public enum BlockFormat { Bgra8Straight, Gray8, Metadata }
public enum BlockCodec { Raw, Brotli }
public enum NodeKind { Layer, Group }

public sealed record PixelRect(int X, int Y, int Width, int Height);
public sealed record SourceFingerprint(string Sha256, long Length);
public sealed record ExtraTag(string Key, int BlockId);
public sealed record MaskPlane(int ChannelId, int BlockId);
public sealed record MaskInfo(
    PixelRect Bounds, byte DefaultColor, int Flags, byte Parameters,
    byte Density, double Feather, int RealFlags, byte RealDefaultColor,
    PixelRect RealBounds, ImmutableArray<MaskPlane> Planes);
public sealed record LayerNode(
    int Id, int? ParentId, int Order, NodeKind Kind, string Name,
    PixelRect Bounds, byte OriginalFlags, bool DefaultVisible,
    string BlendMode, byte Opacity, bool Clipping,
    int? PixelBlockId, MaskInfo? Mask, ImmutableArray<ExtraTag> ExtraTags);
public sealed record BlockInfo(
    int Id, BlockFormat Format, int Width, int Height,
    long Offset, int StoredLength, int RawLength, BlockCodec Codec, string Sha256);
public sealed record CompiledManifest(
    int SchemaVersion, string CompilerId, string GenerationId,
    SourceFingerprint Source, int PsdVersion, int Width, int Height,
    int Depth, int ColorMode, int? ColorDataBlockId, int? ImageResourcesBlockId,
    ImmutableArray<LayerNode> Nodes, ImmutableArray<BlockInfo> Blocks);

/// <summary>Explicit admission limits, not silent quality reduction. Tune before accepting larger assets.</summary>
public sealed record FormatLimits
{
    public long MaxSourceBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxManifestBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxBlockBytes { get; init; } = 256 * 1024 * 1024;
    public int MaxNodes { get; init; } = 65536;
    public int MaxBlocks { get; init; } = 262144;
    public int MaxDimension { get; init; } = 300000;
    public long MaxStoreBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    public int MaxExtraTagBytes { get; init; } = 16 * 1024 * 1024;
}

public static class CompiledFormat
{
    public const int Version = 1;
    public const string CompilerId = "psd-tachie-next/p0.1;parser=ff3aee18a95e5fb6e868585a5b0ad15f46decd89";
    public const int HeaderSize = 40;
    public static ReadOnlySpan<byte> Magic => "PSNTBLK1"u8;
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    public static string Generation(SourceFingerprint source) => Hash(Encoding.UTF8.GetBytes(
        $"{Version}\n{CompilerId}\nbgra8-straight-v1\n{source.Sha256}\n{source.Length}"));
    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static int ByteLength(BlockFormat format, int width, int height, FormatLimits limits)
    {
        if (format == BlockFormat.Metadata)
        {
            if (width != 0 || height != 0) throw new InvalidDataException("Metadata blocks have no pixel dimensions.");
            return 0;
        }
        if (format is not (BlockFormat.Bgra8Straight or BlockFormat.Gray8) ||
            width <= 0 || height <= 0 || width > limits.MaxDimension || height > limits.MaxDimension)
            throw new InvalidDataException("Invalid pixel block dimensions or format.");
        var length = checked((long)width * height * (format == BlockFormat.Bgra8Straight ? 4 : 1));
        if (length > limits.MaxBlockBytes) throw new InvalidDataException("Pixel block exceeds the configured limit.");
        return checked((int)length);
    }

    public static void Validate(CompiledManifest m, long fileLength, FormatLimits limits)
    {
        if (m.SchemaVersion != Version || m.CompilerId != CompilerId)
            throw new InvalidDataException("Unsupported compiled schema/compiler. Recompile the source.");
        if (m.Source is null || !IsHash(m.Source.Sha256) || m.Source.Length < 26 ||
            m.Source.Length > limits.MaxSourceBytes || m.GenerationId != Generation(m.Source))
            throw new InvalidDataException("Invalid source/generation identity.");
        if (m.PsdVersion is not (1 or 2) || m.Depth != 8 || m.ColorMode != 3 ||
            m.Width <= 0 || m.Height <= 0 || m.Width > limits.MaxDimension || m.Height > limits.MaxDimension)
            throw new InvalidDataException("Unsupported document profile. P0 supports RGB8 PSD/PSB only.");
        if (m.Nodes.IsDefault || m.Blocks.IsDefault || m.Nodes.Length > limits.MaxNodes ||
            m.Blocks.Length > limits.MaxBlocks || fileLength > limits.MaxStoreBytes)
            throw new InvalidDataException("Invalid manifest collection/size.");
        long end = HeaderSize;
        for (var i = 0; i < m.Blocks.Length; i++)
        {
            var b = m.Blocks[i] ?? throw new InvalidDataException("Null block.");
            if (b.Id != i || b.Offset != end || b.StoredLength <= 0 || b.RawLength <= 0 ||
                b.RawLength > limits.MaxBlockBytes || b.StoredLength > limits.MaxBlockBytes || !IsHash(b.Sha256) ||
                !Enum.IsDefined(b.Codec)) throw new InvalidDataException("Invalid block index.");
            var required = ByteLength(b.Format, b.Width, b.Height, limits);
            if (b.Format != BlockFormat.Metadata && b.RawLength != required)
                throw new InvalidDataException("Pixel block length mismatch.");
            if (b.Codec == BlockCodec.Raw && b.StoredLength != b.RawLength)
                throw new InvalidDataException("Raw block length mismatch.");
            end = checked(end + b.StoredLength);
            if (end > fileLength) throw new InvalidDataException("Truncated pixel store.");
        }
        if (end != fileLength) throw new InvalidDataException("Unexpected trailing pixel-store data.");
        CheckBlock(m.ColorDataBlockId, BlockFormat.Metadata);
        CheckBlock(m.ImageResourcesBlockId, BlockFormat.Metadata);
        var orders = new Dictionary<int, int>();
        for (var i = 0; i < m.Nodes.Length; i++)
        {
            var n = m.Nodes[i] ?? throw new InvalidDataException("Null node.");
            if (n.Id != i || !Enum.IsDefined(n.Kind) || n.Name is null || n.Name.Length > 32768 ||
                n.BlendMode is not { Length: 4 } || n.ExtraTags.IsDefault)
                throw new InvalidDataException("Invalid node.");
            if (n.ParentId is int parent && (parent < 0 || parent >= i || m.Nodes[parent].Kind != NodeKind.Group))
                throw new InvalidDataException("Invalid parent or cyclic tree.");
            var parentKey = n.ParentId ?? -1;
            var expected = orders.GetValueOrDefault(parentKey);
            if (n.Order != expected) throw new InvalidDataException("Invalid sibling order.");
            orders[parentKey] = expected + 1;
            CheckRect(n.Bounds);
            if (n.DefaultVisible != ((n.OriginalFlags & 2) == 0))
                throw new InvalidDataException("PSD visibility flag mismatch.");
            if (n.Kind == NodeKind.Group && n.PixelBlockId is not null)
                throw new InvalidDataException("A group cannot own a raster block in P0.");
            if (n.Kind == NodeKind.Layer && n.Bounds.Width > 0 && n.Bounds.Height > 0 && n.PixelBlockId is null)
                throw new InvalidDataException("Missing raster block.");
            CheckBlock(n.PixelBlockId, BlockFormat.Bgra8Straight, n.Bounds);
            if (n.Mask is { } mask)
            {
                CheckRect(mask.Bounds); CheckRect(mask.RealBounds);
                if (!double.IsFinite(mask.Feather) || mask.Planes.IsDefault)
                    throw new InvalidDataException("Invalid mask.");
                var channels = new HashSet<int>();
                foreach (var plane in mask.Planes)
                {
                    if (plane is null || plane.ChannelId != -2 || !channels.Add(plane.ChannelId))
                        throw new InvalidDataException("Unsupported/duplicate mask channel.");
                    CheckBlock(plane.BlockId, BlockFormat.Gray8, mask.Bounds);
                }
            }
            foreach (var tag in n.ExtraTags)
            {
                if (tag is null || tag.Key is not { Length: 4 }) throw new InvalidDataException("Invalid extra tag.");
                CheckBlock(tag.BlockId, BlockFormat.Metadata);
                if (m.Blocks[tag.BlockId].RawLength > limits.MaxExtraTagBytes)
                    throw new InvalidDataException("Extra tag exceeds limit.");
            }
        }
        return;
        void CheckBlock(int? id, BlockFormat format, PixelRect? rect = null)
        {
            if (id is null) return;
            if (id < 0 || id >= m.Blocks.Length) throw new InvalidDataException("Missing block reference.");
            var b = m.Blocks[id.Value];
            if (b.Format != format || (rect is not null && (b.Width != rect.Width || b.Height != rect.Height)))
                throw new InvalidDataException("Block reference format/size mismatch.");
        }
        void CheckRect(PixelRect? r)
        {
            if (r is null || r.Width < 0 || r.Height < 0 || r.Width > limits.MaxDimension || r.Height > limits.MaxDimension ||
                (long)r.X + r.Width > int.MaxValue || (long)r.Y + r.Height > int.MaxValue)
                throw new InvalidDataException("Invalid node/mask rectangle.");
        }
    }
}
