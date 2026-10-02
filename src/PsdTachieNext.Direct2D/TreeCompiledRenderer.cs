using System.Numerics;
using System.Runtime.InteropServices;
using PsdTachieNext.Core;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PsdTachieNext.Direct2D;

/// <summary>
/// Bounded, transactional P1 tree renderer. Caller owns a live document lease and an idle,
/// exclusive context. Output is borrowed until the next successful update/clear/dispose.
/// Pure pass-through groups share the backdrop; normal groups form isolated composites.
/// This is not a claim of equivalence with all Photoshop/YMM4 drawing semantics.
/// </summary>
public sealed class TreeCompiledRenderer : IDisposable
{
    public const string Profile = "tree-rgb8-d2d-v1";
    private static readonly PixelFormat ColorFormat = new(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
    private static readonly PixelFormat MaskFormat = new(Format.A8_UNorm, AlphaMode.Premultiplied);
    private static readonly IReadOnlyDictionary<string, BlendMode> Blends = new Dictionary<string, BlendMode>(StringComparer.Ordinal)
    {
        ["mul "] = BlendMode.Multiply, ["scrn"] = BlendMode.Screen,
        ["dark"] = BlendMode.Darken, ["lite"] = BlendMode.Lighten,
        ["idiv"] = BlendMode.ColorBurn,
        ["lbrn"] = BlendMode.LinearBurn, ["dkCl"] = BlendMode.DarkerColor,
        ["lgCl"] = BlendMode.LighterColor, ["div "] = BlendMode.ColorDodge,
        ["lddg"] = BlendMode.LinearDodge, ["over"] = BlendMode.Overlay,
        ["sLit"] = BlendMode.SoftLight, ["hLit"] = BlendMode.HardLight,
        ["vLit"] = BlendMode.VividLight, ["lLit"] = BlendMode.LinearLight,
        ["pLit"] = BlendMode.PinLight, ["hMix"] = BlendMode.HardMix,
        ["diff"] = BlendMode.Difference, ["smud"] = BlendMode.Exclusion,
        ["hue "] = BlendMode.Hue, ["sat "] = BlendMode.Saturation,
        ["colr"] = BlendMode.Color, ["lum "] = BlendMode.Luminosity,
        ["fsub"] = BlendMode.Subtract, ["fdiv"] = BlendMode.Division
    };
    private readonly ID2D1DeviceContext context;
    private readonly SharedDocumentLease? source;
    private readonly CompiledManifest manifest;
    private System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<int>> children;
    private readonly long maxOutputBytes, maxUploadBytes, deviceEpoch;
    private readonly int maxGraphObjects;
    private readonly List<IDisposable> transient = [];
    private RenderPlan plan = null!;
    private PreparedAppearanceLease prepared = null!;
    private ID2D1Bitmap1? output;
    private AppearanceKey? currentKey;
    private bool disposed;
    private long uploadedThisUpdate;
    public ID2D1Bitmap1? Output => output;
    public long CompositionCount { get; private set; }
    public long UploadCount { get; private set; }
    public long LastUploadBytes { get; private set; }
    public int LiveGraphObjects => transient.Count;
    public int PeakGraphObjects { get; private set; }
    public int LiveTemporaryBitmaps { get; private set; }

    public TreeCompiledRenderer(ID2D1DeviceContext context, SharedDocumentLease source,
        long maxOutputBytes = 256L * 1024 * 1024, long maxUploadBytes = 128L * 1024 * 1024,
        int maxGraphObjects = 4096, long deviceEpoch = 0)
        : this(context, source.Manifest, maxOutputBytes, maxUploadBytes, maxGraphObjects, deviceEpoch)
    { this.source = source; }

    public TreeCompiledRenderer(ID2D1DeviceContext context, PreparedAppearanceLease prepared,
        long maxOutputBytes = 256L * 1024 * 1024, long maxUploadBytes = 128L * 1024 * 1024,
        int maxGraphObjects = 4096, long deviceEpoch = 0)
        : this(context, prepared.Plan.Manifest, maxOutputBytes, maxUploadBytes, maxGraphObjects, deviceEpoch) { }

    private TreeCompiledRenderer(ID2D1DeviceContext context, CompiledManifest manifest,
        long maxOutputBytes, long maxUploadBytes, int maxGraphObjects, long deviceEpoch)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (maxOutputBytes <= 0 || maxUploadBytes <= 0 || maxGraphObjects <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.context = context; this.manifest = manifest;
        this.maxOutputBytes = maxOutputBytes; this.maxUploadBytes = maxUploadBytes;
        this.maxGraphObjects = maxGraphObjects; this.deviceEpoch = deviceEpoch;
        children = RenderPlan.Create(manifest).Children;
    }

    public bool Update(IEnumerable<int>? enabledNodeIds = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ValidateSupported();
        var nextPlan = RenderPlan.Create(manifest, enabledNodeIds);
        if (nextPlan.UploadBytes > maxUploadBytes) throw new CacheCapacityException("Active uploads exceed the admitted working set.");
        using var ready = PreparedAppearanceLease.Prepare(source ?? throw new InvalidOperationException("This renderer requires prepared input."), nextPlan);
        return UpdatePrepared(ready);
    }

    /// <summary>GPU-only path. Missing blocks throw; no source/disk fallback is permitted.</summary>
    public bool UpdatePrepared(PreparedAppearanceLease ready)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ReferenceEquals(ready.Plan.Manifest, manifest)) throw new ArgumentException("Prepared generation mismatch.", nameof(ready));
        ValidateSupported();
        if (ready.Plan.UploadBytes > maxUploadBytes) throw new CacheCapacityException("Active uploads exceed the admitted working set.");
        plan = ready.Plan; prepared = ready; children = plan.Children;
        var key = AppearanceKey.Create(manifest, plan.ActiveNodeIds, Profile, deviceEpoch: deviceEpoch);
        if (output is not null && key == currentKey) { prepared = null!; return false; }

        using var previousTarget = context.Target;
        var previousTransform = context.Transform; var previousDpi = context.Dpi;
        var previousUnit = context.UnitMode; var previousBlend = context.PrimitiveBlend;
        ID2D1Bitmap1? pending = null; var drawing = false; uploadedThisUpdate = 0;
        try
        {
            var image = BuildSiblings(0, null);
            pending = context.CreateBitmap(new SizeI(manifest.Width, manifest.Height),
                new BitmapProperties1(ColorFormat, 96, 96, BitmapOptions.Target));
            context.Target = pending; context.Dpi = new(96,96); context.UnitMode = UnitMode.Pixels;
            context.Transform = Matrix3x2.Identity; context.PrimitiveBlend = PrimitiveBlend.SourceOver;
            context.BeginDraw(); drawing = true; context.Clear(new Color4(0,0,0,0));
            if (image is not null) context.DrawImage(image);
            var result = context.EndDraw(); drawing = false; result.CheckError();
            var old = output; output = pending; pending = null;
            currentKey = key; CompositionCount++; LastUploadBytes = uploadedThisUpdate;
            old?.Dispose(); return true;
        }
        finally
        {
            if (drawing) context.EndDraw();
            context.Target = previousTarget; context.Transform = previousTransform;
            context.Dpi = previousDpi; context.UnitMode = previousUnit; context.PrimitiveBlend = previousBlend;
            pending?.Dispose();
            for (var i = transient.Count - 1; i >= 0; i--) transient[i].Dispose();
            transient.Clear(); LiveTemporaryBitmaps = 0; prepared = null!;
        }
    }

    private ID2D1Image? BuildSiblings(int slot, ID2D1Image? backdrop)
    {
        var siblings = children[slot];
        for (var i = siblings.Length - 1; i >= 0;)
        {
            var node = manifest.Nodes[siblings[i]];
            var first = i - 1;
            while (first >= 0 && manifest.Nodes[siblings[first]].Clipping) first--;
            // Even a hidden base owns its clipping chain: never rebind clips to the next visible sibling.
            if (plan.IsActive(node.Id))
            {
                if (node.Kind == NodeKind.Group && node.BlendMode == "pass")
                    backdrop = BuildSiblings(node.Id + 1, backdrop);
                else
                {
                    var image = BuildNode(node);
                    if (image is not null)
                    {
                        for (var clip = i - 1; clip > first; clip--)
                        {
                            var cn = manifest.Nodes[siblings[clip]];
                            if (!plan.IsActive(cn.Id)) continue;
                            var ci = BuildNode(cn);
                            if (ci is not null)
                                image = CompositeImages(image, Scale(ci, cn.Opacity), CompositeMode.SourceAtop);
                        }
                        backdrop = Merge(backdrop, Scale(image, node.Opacity), node.BlendMode);
                    }
                }
            }
            i = first;
        }
        return backdrop;
    }

    private ID2D1Image? BuildNode(LayerNode node)
    {
        ID2D1Image? image;
        if (node.Kind == NodeKind.Group) image = BuildSiblings(node.Id + 1, null);
        else
        {
            if (node.PixelBlockId is not int id) return null;
            ReserveUpload(manifest.Blocks[id].RawLength);
            var bytes = prepared.GetBlock(id).ToArray();
            for (var i = 0; i < bytes.Length; i += 4)
                for (var c = 0; c < 3; c++) bytes[i+c] = (byte)((bytes[i+c] * bytes[i+3] + 127) / 255);
            image = Translate(Upload(node.Bounds, bytes, ColorFormat, 4), node.Bounds.X, node.Bounds.Y);
        }
        if (image is null || node.Mask is not { } mask || (mask.Flags & 2) != 0) return image;
        var plane = mask.Planes.Single();
        ReserveUpload(manifest.Blocks[plane.BlockId].RawLength);
        var inverted = (mask.Flags & 4) != 0;
        var maskPixels = prepared.GetBlock(plane.BlockId).ToArray();
        if (inverted) for (var i = 0; i < maskPixels.Length; i++) maskPixels[i] = (byte)(255 - maskPixels[i]);
        var x = checked(mask.Bounds.X + ((mask.Flags & 1) != 0 ? node.Bounds.X : 0));
        var y = checked(mask.Bounds.Y + ((mask.Flags & 1) != 0 ? node.Bounds.Y : 0));
        ID2D1Image maskImage = Translate(Upload(mask.Bounds, maskPixels, MaskFormat, 1), x, y);
        var outside = inverted ? 255 - mask.DefaultColor : mask.DefaultColor;
        if (outside != 0)
        {
            var fill = Own(new Flood(context) { Color = new Vector4(0,0,0,outside / 255f) });
            // SourceCopy would erase the outside value. BoundedSourceCopy replaces only the mask rectangle.
            maskImage = CompositeImages(Image(fill), maskImage, CompositeMode.BoundedSourceCopy);
        }
        return CompositeImages(maskImage, image, CompositeMode.SourceIn);
    }

    private ID2D1Image Merge(ID2D1Image? background, ID2D1Image foreground, string mode)
    {
        if (background is null) return foreground;
        if (mode == "norm") return CompositeImages(background, foreground, CompositeMode.SourceOver);
        var effect = Own(new Vortice.Direct2D1.Effects.Blend(context) { Mode = Blends[mode] });
        effect.SetInput(0, background, true); effect.SetInput(1, foreground, true);
        return Image(effect);
    }
    private ID2D1Image CompositeImages(ID2D1Image background, ID2D1Image foreground, CompositeMode mode)
    {
        // The built-in composite effect has two inputs by default.
        var effect = Own(new Composite(context) { Mode = mode });
        effect.SetInput(0, background, true); effect.SetInput(1, foreground, true);
        return Image(effect);
    }
    private ID2D1Image Scale(ID2D1Image image, byte opacity)
    {
        if (opacity == 255) return image;
        var effect = Own(new Opacity(context) { Value = opacity / 255f });
        effect.SetInput(0, image, true); return Image(effect);
    }
    private ID2D1Image Translate(ID2D1Image image, int x, int y)
    {
        if (x == 0 && y == 0) return image;
        var effect = Own(new AffineTransform2D(context)
        {
            TransformMatrix = Matrix3x2.CreateTranslation(x,y),
            InterPolationMode = AffineTransform2DInterpolationMode.NearestNeighbor
        });
        effect.SetInput(0, image, true); return Image(effect);
    }
    private ID2D1Image Image(ID2D1Effect effect) => Own(effect.Output);
    private T Own<T>(T resource) where T : IDisposable
    {
        if (transient.Count >= maxGraphObjects)
        { resource.Dispose(); throw new CacheCapacityException("Composition graph exceeds its resource budget."); }
        transient.Add(resource); PeakGraphObjects = Math.Max(PeakGraphObjects, transient.Count); return resource;
    }
    private void ReserveUpload(long bytes)
    {
        if (bytes > maxUploadBytes - uploadedThisUpdate)
            throw new CacheCapacityException("Active uploads exceed the admitted working set. No quality reduction is applied.");
        uploadedThisUpdate += bytes;
    }
    private unsafe ID2D1Bitmap1 Upload(PixelRect bounds, ReadOnlySpan<byte> pixels, PixelFormat format, int bytesPerPixel)
    {
        fixed (byte* data = pixels)
        {
            var bitmap = Own(context.CreateBitmap(new SizeI(bounds.Width, bounds.Height), (IntPtr)data,
                checked((uint)(bounds.Width * bytesPerPixel)), new BitmapProperties1(format,96,96)));
            UploadCount++; LiveTemporaryBitmaps++; return bitmap;
        }
    }

    private void ValidateSupported()
    {
        if (checked((long)manifest.Width * manifest.Height * 4) > maxOutputBytes)
            throw new CacheCapacityException("Output exceeds the admitted bitmap size.");
        if (manifest.ColorDataBlockId is not null || manifest.ImageResourcesBlockId is not null)
            throw new NotSupportedException("Color/image-resource interpretation is not proved by this renderer profile.");
        var depths = new int[manifest.Nodes.Length];
        foreach (var n in manifest.Nodes)
        {
            depths[n.Id] = n.ParentId is int p ? depths[p] + 1 : 1;
            if (depths[n.Id] > 64) throw new NotSupportedException("Group nesting exceeds the renderer limit.");
            if (n.BlendMode != "norm" && !Blends.ContainsKey(n.BlendMode) && !(n.Kind == NodeKind.Group && n.BlendMode == "pass"))
                throw new NotSupportedException("Unsupported PSD blend key: " + n.BlendMode);
            if (n.ExtraTags.Any(t => t.Key is not ("luni" or "lyid" or "lsct" or "lsdk")))
                throw new NotSupportedException("Unknown rendering tags cannot be silently ignored.");
            if (n.Clipping && n.BlendMode != "norm")
                throw new NotSupportedException("Non-normal clipping needs a separately proved compositor.");
            if (n.Kind == NodeKind.Group && n.BlendMode == "pass" && (n.Opacity != 255 || n.Mask is not null || n.Clipping))
                throw new NotSupportedException("Masked/translucent/clipped pass-through is not equivalent to an isolated group.");
            if (n.Mask is { } m && (m.Flags & 2) == 0 &&
                ((m.Flags & ~15) != 0 || m.Parameters != 0 || m.Feather != 0 || m.Planes.Length != 1 || m.Planes[0].ChannelId != -2))
                throw new NotSupportedException("Only the validated user raster-mask profile is admitted.");
        }
        foreach (var siblings in children)
        {
            if (siblings.Length == 0) continue;
            if (manifest.Nodes[siblings[^1]].Clipping)
                throw new NotSupportedException("Orphan clipping chain has no structural base.");
            for (var i = 1; i < siblings.Length; i++)
            {
                var n = manifest.Nodes[siblings[i]];
                if (n.Kind == NodeKind.Group && n.BlendMode == "pass" && manifest.Nodes[siblings[i-1]].Clipping)
                    throw new NotSupportedException("A pass-through group is not a closed clipping base.");
            }
        }
    }

    /// <summary>Tests only: production consumes Output without a CPU readback.</summary>
    public byte[] Readback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (output is null) throw new InvalidOperationException("No completed output.");
        var size = output.PixelSize;
        using var staging = context.CreateBitmap(size, new BitmapProperties1(ColorFormat,96,96,BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(output).CheckError(); var mapped = staging.Map(MapOptions.Read);
        try
        {
            var stride = checked(size.Width * 4); var bytes = new byte[checked(stride * size.Height)];
            for (var y = 0; y < size.Height; y++) Marshal.Copy(mapped.Bits + checked(y * (int)mapped.Pitch), bytes, y * stride, stride);
            return bytes;
        }
        finally { staging.Unmap(); }
    }
    public void Clear() { output?.Dispose(); output = null; currentKey = null; }
    public void Dispose() { if (disposed) return; disposed = true; Clear(); }
}
