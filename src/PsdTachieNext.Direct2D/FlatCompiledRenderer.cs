using System.Numerics;
using System.Runtime.InteropServices;
using PsdTachieNext.Core;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PsdTachieNext.Direct2D;

/// <summary>
/// P1's deliberately small renderer: flat, normal-blend RGB8 layers at their original coordinates.
/// Groups, masks, clipping, color resources and unknown draw metadata are rejected, not flattened/dropped.
/// The caller owns the document lease and an exclusive, idle Direct2D context. No YMM4 internal type is used.
/// This class is thread-confined. Output is borrowed until the next successful Update/Clear/Dispose.
/// </summary>
public sealed class FlatCompiledRenderer : IDisposable
{
    public const string Profile = "flat-normal-bgra8-premul-nearest-v1";
    private static readonly PixelFormat PixelFormat = new(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied);
    private readonly ID2D1DeviceContext context;
    private readonly SharedDocumentLease source;
    private readonly long outputLimit;
    private readonly long deviceEpoch;
    private ID2D1Bitmap1? output;
    private AppearanceKey? currentKey;
    private bool disposed;
    public ID2D1Bitmap1? Output => output;
    public long CompositionCount { get; private set; }
    public long UploadCount { get; private set; }
    public int LiveTemporaryBitmaps { get; private set; }

    public FlatCompiledRenderer(ID2D1DeviceContext context, SharedDocumentLease source,
        long maxOutputBytes = 256L * 1024 * 1024, long deviceEpoch = 0)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(source);
        if (maxOutputBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
        this.context = context; this.source = source; outputLimit = maxOutputBytes; this.deviceEpoch = deviceEpoch;
    }

    public bool Update(IEnumerable<int>? enabledLayerIds = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var manifest = source.Manifest;
        ValidateSupported(manifest);
        var enabled = (enabledLayerIds ?? manifest.Nodes.Where(n => n.DefaultVisible).Select(n => n.Id)).ToArray();
        if (enabled.Distinct().Count() != enabled.Length || enabled.Any(id => (uint)id >= (uint)manifest.Nodes.Length))
            throw new ArgumentException("Layer selection must contain unique IDs from this generation.", nameof(enabledLayerIds));
        // Manifest order is top-to-bottom. SourceOver must draw bottom-to-top.
        var ordered = enabled.OrderByDescending(id => manifest.Nodes[id].Order).ToArray();
        var key = AppearanceKey.Create(manifest, ordered, Profile, deviceEpoch: deviceEpoch);
        if (output is not null && key == currentKey) return false;

        using var previousTarget = context.Target;
        var previousTransform = context.Transform;
        var previousDpi = context.Dpi;
        var previousUnitMode = context.UnitMode;
        var previousBlend = context.PrimitiveBlend;
        ID2D1Bitmap1? pending = null;
        var drawing = false;
        try
        {
            pending = context.CreateBitmap(new SizeI(manifest.Width, manifest.Height),
                new BitmapProperties1(PixelFormat, 96, 96, BitmapOptions.Target));
            context.Target = pending;
            context.Dpi = new(96, 96); context.UnitMode = UnitMode.Pixels;
            context.Transform = Matrix3x2.Identity; context.PrimitiveBlend = PrimitiveBlend.SourceOver;
            context.BeginDraw(); drawing = true;
            context.Clear(new Color4(0, 0, 0, 0));
            foreach (var id in ordered)
            {
                var node = manifest.Nodes[id];
                if (node.PixelBlockId is not int blockId || node.Opacity == 0) continue;
                using var block = source.AcquireBlock(blockId);
                // Upload conversion never mutates the shared straight-BGRA cache.
                var premultiplied = block.Memory.ToArray();
                for (var i = 0; i < premultiplied.Length; i += 4)
                {
                    var alpha = premultiplied[i + 3];
                    premultiplied[i] = (byte)((premultiplied[i] * alpha + 127) / 255);
                    premultiplied[i + 1] = (byte)((premultiplied[i + 1] * alpha + 127) / 255);
                    premultiplied[i + 2] = (byte)((premultiplied[i + 2] * alpha + 127) / 255);
                }
                using var bitmap = Upload(node.Bounds, premultiplied);
                LiveTemporaryBitmaps++;
                try
                {
                    context.Transform = Matrix3x2.CreateTranslation(node.Bounds.X, node.Bounds.Y);
                    context.DrawBitmap(bitmap, node.Opacity / 255.0f, InterpolationMode.NearestNeighbor);
                }
                finally { LiveTemporaryBitmaps--; }
            }
            var result = context.EndDraw(); drawing = false; result.CheckError();
            // Do not publish a partially drawn frame or replace the previous key on failure.
            var previous = output;
            output = pending; pending = null; currentKey = key; CompositionCount++;
            previous?.Dispose();
            return true;
        }
        finally
        {
            if (drawing) context.EndDraw();
            context.Target = previousTarget;
            context.Transform = previousTransform; context.Dpi = previousDpi;
            context.UnitMode = previousUnitMode; context.PrimitiveBlend = previousBlend;
            pending?.Dispose();
        }
    }

    private unsafe ID2D1Bitmap1 Upload(PixelRect bounds, byte[] pixels)
    {
        fixed (byte* data = pixels)
        {
            var bitmap = context.CreateBitmap(new SizeI(bounds.Width, bounds.Height), (IntPtr)data,
                checked((uint)bounds.Width * 4), new BitmapProperties1(PixelFormat, 96, 96));
            UploadCount++; return bitmap;
        }
    }

    private void ValidateSupported(CompiledManifest manifest)
    {
        if (checked((long)manifest.Width * manifest.Height * 4) > outputLimit)
            throw new CacheCapacityException("Output exceeds the admitted bitmap size. No resolution reduction is applied.");
        if (manifest.ColorDataBlockId is not null || manifest.ImageResourcesBlockId is not null)
            throw new NotSupportedException("P1 flat renderer does not yet interpret color/image resources.");
        foreach (var node in manifest.Nodes)
        {
            if (node.Kind != NodeKind.Layer || node.ParentId is not null || node.BlendMode != "norm" ||
                node.Clipping || node.Mask is not null || node.ExtraTags.Any(t => t.Key is not ("luni" or "lyid")))
                throw new NotSupportedException("P1 flat renderer requires ordinary flat layers; unsupported draw semantics are not discarded.");
        }
    }

    /// <summary>Test/diagnostic readback only. The eventual YMM4 source consumes Output directly on the GPU.</summary>
    public byte[] Readback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (output is null) throw new InvalidOperationException("No completed output exists.");
        var size = output.PixelSize;
        using var staging = context.CreateBitmap(size,
            new BitmapProperties1(PixelFormat, 96, 96, BitmapOptions.CpuRead | BitmapOptions.CannotDraw));
        staging.CopyFromBitmap(output).CheckError();
        var mapped = staging.Map(MapOptions.Read);
        try
        {
            var rowBytes = checked(size.Width * 4);
            var result = new byte[checked(rowBytes * size.Height)];
            for (var y = 0; y < size.Height; y++)
                Marshal.Copy(mapped.Bits + checked(y * (int)mapped.Pitch), result, y * rowBytes, rowBytes);
            return result;
        }
        finally { staging.Unmap(); }
    }

    public void Clear()
    {
        output?.Dispose(); output = null; currentKey = null;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; Clear();
    }
}
