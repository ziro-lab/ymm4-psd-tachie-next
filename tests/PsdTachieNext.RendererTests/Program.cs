using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using PsdTachieNext.Core;
using PsdTachieNext.Compiler;
using PsdTachieNext.Direct2D;
using PsdTachieNext.Tests;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

var results = new List<object>(); var failed = 0; var assertions = 0;
var root = Path.Combine(Path.GetTempPath(), "psd-next-render-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    using var d3d = D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
    using var dxgi = d3d.QueryInterface<IDXGIDevice>();
    using var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
    using var device = factory.CreateDevice(dxgi);
    using var context = device.CreateDeviceContext(DeviceContextOptions.None);

    Case("normal-layers-position-and-source-over-exact-pixels", () =>
    {
        var dir = Store(); using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        using var renderer = new FlatCompiledRenderer(context, source);
        True(renderer.Update());
        Bytes(new byte[] { 127,0,128,255, 0,127,128,255, 127,0,128,255, 127,0,128,255 }, renderer.Readback());
        Equal(0, renderer.LiveTemporaryBitmaps);
    });
    Case("selection-changes-and-unchanged-state-reuses-output", () =>
    {
        var dir = Store(); using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        using var renderer = new FlatCompiledRenderer(context, source);
        True(renderer.Update([2])); Bytes(Repeat(255,0,0,255), renderer.Readback());
        var pointer = renderer.Output!.NativePointer; var reads = pool.Snapshot().BlockReadCount;
        True(!renderer.Update([2])); Equal(pointer, renderer.Output.NativePointer); Equal(reads, pool.Snapshot().BlockReadCount);
        True(renderer.Update([0])); Bytes(Repeat(0,0,128,128), renderer.Readback()); Equal(2L, renderer.CompositionCount);
        True(renderer.Update([])); Bytes(new byte[16], renderer.Readback());
    });
    Case("input-selection-order-does-not-change-psd-stack-order", () =>
    {
        var dir = Store(); using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        using var renderer = new FlatCompiledRenderer(context, source);
        True(renderer.Update([2,0,1])); var pixels = renderer.Readback();
        True(!renderer.Update([1,2,0])); Bytes(pixels, renderer.Readback()); Equal(1L, renderer.CompositionCount);
    });
    Case("layer-opacity-exact-pixels", () =>
    {
        var dir = Store(redAlpha: 255, redOpacity: 128); using var pool = new SharedDocumentPool(4096, 2);
        using var source = pool.Acquire(dir); using var renderer = new FlatCompiledRenderer(context, source);
        renderer.Update([0,2]); Bytes(Repeat(127,0,128,255), renderer.Readback());
    });
    Case("transparent-rgb-does-not-leak-into-output-or-mutate-cache", () =>
    {
        var dir = Store(redAlpha: 0); using var pool = new SharedDocumentPool(4096, 2);
        using var source = pool.Acquire(dir); using var renderer = new FlatCompiledRenderer(context, source);
        renderer.Update([0]); Bytes(new byte[16], renderer.Readback());
        using var block = source.AcquireBlock(source.Manifest.Nodes[0].PixelBlockId!.Value);
        Equal((byte)255, block.Memory.Span[2]); Equal((byte)0, block.Memory.Span[3]);
    });
    Case("compiled-psd-renders-after-original-is-deleted", () =>
    {
        var sourcePath = Path.Combine(root, "source.psd"); PsdFixture.Write(sourcePath);
        var dir = new PsdCompiler().Compile(sourcePath, Path.Combine(root, "cache")); File.Delete(sourcePath);
        using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        using var renderer = new FlatCompiledRenderer(context, source); renderer.Update();
        var expected = new byte[8 * 4 * 4]; var raw = PsdFixture.Pixels();
        // Expected fixture placement is X=3,Y=-2; only a 5x2 region lies on the canvas.
        for (var y = 0; y < 2; y++) for (var x = 3; x < 8; x++)
        {
            var s = ((y + 2) * 8 + x - 3) * 4; var d = (y * 8 + x) * 4; var alpha = raw[s + 3];
            for (var c = 0; c < 3; c++) expected[d + c] = (byte)((raw[s + c] * alpha + 127) / 255);
            expected[d + 3] = alpha;
        }
        Bytes(expected, renderer.Readback());
    });
    Case("context-state-restored", () =>
    {
        using var foreignTarget = context.CreateBitmap(new SizeI(2, 2), new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        context.Target = foreignTarget; context.Transform = Matrix3x2.CreateTranslation(9, 11);
        context.Dpi = new(120,144); context.UnitMode = UnitMode.Dips; context.PrimitiveBlend = PrimitiveBlend.Copy;
        try
        {
            var dir = Store(); using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
            using var renderer = new FlatCompiledRenderer(context, source); renderer.Update();
            using var target = context.Target;
            Equal(foreignTarget.NativePointer, target!.NativePointer); Equal(Matrix3x2.CreateTranslation(9,11), context.Transform);
            Equal(120f, context.Dpi.Width); Equal(144f, context.Dpi.Height);
            Equal(UnitMode.Dips, context.UnitMode); Equal(PrimitiveBlend.Copy, context.PrimitiveBlend);
        }
        finally
        { context.Target = null; context.Transform = Matrix3x2.Identity; context.Dpi = new(96,96); context.PrimitiveBlend = PrimitiveBlend.SourceOver; }
    });
    Case("corrupt-new-layer-keeps-previous-completed-output", () =>
    {
        var dir = Store(); long offset;
        using (var doc = new CompiledDocument(dir)) offset = doc.Manifest.Blocks[0].Offset;
        var bin = Path.Combine(dir, "pixels.bin"); var bytes = File.ReadAllBytes(bin); bytes[(int)offset] ^= 1; File.WriteAllBytes(bin, bytes);
        using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        using var renderer = new FlatCompiledRenderer(context, source); renderer.Update([2]);
        var pointer = renderer.Output!.NativePointer;
        Throws<InvalidDataException>(() => renderer.Update([0])); Equal(pointer, renderer.Output.NativePointer);
        Equal(1L, renderer.CompositionCount); Equal(0, renderer.LiveTemporaryBitmaps);
        Bytes(Repeat(255,0,0,255), renderer.Readback());
    });
    Case("unsupported-blend-rejected-not-silently-rendered", () =>
    {
        var dir = Store(blend: "mul "); using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        using var renderer = new FlatCompiledRenderer(context, source);
        Throws<NotSupportedException>(() => renderer.Update()); True(renderer.Output is null); Equal(0L, pool.Snapshot().BlockReadCount);
    });
    Case("output-limit-and-invalid-selection-rejected", () =>
    {
        var dir = Store(); using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        using (var small = new FlatCompiledRenderer(context, source, maxOutputBytes: 4))
            Throws<CacheCapacityException>(() => small.Update());
        using var renderer = new FlatCompiledRenderer(context, source); renderer.Update([2]);
        var pointer = renderer.Output!.NativePointer;
        Throws<ArgumentException>(() => renderer.Update([100])); Equal(pointer, renderer.Output.NativePointer);
        Throws<ArgumentException>(() => renderer.Update([2,2])); Equal(1L, renderer.CompositionCount);
    });
    Case("clear-and-dispose-release-output-and-can-recompose", () =>
    {
        var dir = Store(); using var pool = new SharedDocumentPool(4096, 2); using var source = pool.Acquire(dir);
        var renderer = new FlatCompiledRenderer(context, source); renderer.Update([2]); var output = renderer.Output!;
        renderer.Clear(); True(renderer.Output is null); Equal(IntPtr.Zero, output.NativePointer);
        renderer.Update([2]); Equal(2L, renderer.CompositionCount); output = renderer.Output!;
        renderer.Dispose(); renderer.Dispose(); Equal(IntPtr.Zero, output.NativePointer);
        Throws<ObjectDisposedException>(() => renderer.Update());
    });
}
catch (Exception ex) { failed++; results.Add(new { name = "device-or-harness", status = "FAIL", error = ex.ToString() }); }
finally { Directory.Delete(root, true); }
var outputPath = args.Length == 1 ? args[0] : "renderer-results.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(new
{
    schema = "psd-next.renderer-proof.v1", total = results.Count, failed, assertions, tolerance = 0,
    backend = "Direct2D on D3D11 WARP (software rasterizer; not a physical-GPU benchmark)",
    profile = FlatCompiledRenderer.Profile, runtime = RuntimeInformation.FrameworkDescription,
    sourceHead = Environment.GetEnvironmentVariable("SOURCE_HEAD"), checkoutSha = Environment.GetEnvironmentVariable("GITHUB_SHA"), results
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RENDERER SUMMARY: {results.Count - failed}/{results.Count} cases passed; {assertions} assertions; {failed} failures; pixel tolerance=0.");
return failed == 0 ? 0 : 1;

void Case(string name, Action body)
{
    try { body(); results.Add(new { name, status = "PASS" }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
}
void True(bool value) { assertions++; if (!value) throw new Exception("Assertion failed."); }
void Equal<T>(T expected, T actual) { assertions++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
void Bytes(byte[] expected, byte[] actual)
{
    assertions++;
    if (!expected.AsSpan().SequenceEqual(actual)) throw new Exception($"Pixels differ: expected {Convert.ToHexString(expected)}, actual {Convert.ToHexString(actual)}");
}
void Throws<T>(Action action) where T : Exception
{ assertions++; try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
byte[] Repeat(byte b, byte g, byte r, byte a) => new byte[] { b,g,r,a, b,g,r,a, b,g,r,a, b,g,r,a };
string Store(byte redAlpha = 128, byte redOpacity = 255, string blend = "norm")
{
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); using var writer = new CompiledStoreWriter(dir);
    var red = writer.AddBlock(BlockFormat.Bgra8Straight, 2, 2, Repeat(0,0,255,redAlpha), compress: false);
    var green = writer.AddBlock(BlockFormat.Bgra8Straight, 1, 1, new byte[] { 0,255,0,255 }, compress: false);
    var blue = writer.AddBlock(BlockFormat.Bgra8Straight, 2, 2, Repeat(255,0,0,255), compress: false);
    var nodes = ImmutableArray.Create(
        new LayerNode(0, null, 0, NodeKind.Layer, "red", new(0,0,2,2), 0, true, blend, redOpacity, false, red, null, []),
        new LayerNode(1, null, 1, NodeKind.Layer, "green", new(1,0,1,1), 0, true, "norm", 255, false, green, null, []),
        new LayerNode(2, null, 2, NodeKind.Layer, "blue", new(0,0,2,2), 0, true, "norm", 255, false, blue, null, []));
    var source = new SourceFingerprint(CompiledFormat.Hash(new byte[26]), 26);
    var manifest = new CompiledManifest(1, CompiledFormat.CompilerId, CompiledFormat.Generation(source), source,
        1, 2, 2, 8, 3, null, null, nodes, writer.Blocks);
    writer.Complete(dir, manifest); return dir;
}
