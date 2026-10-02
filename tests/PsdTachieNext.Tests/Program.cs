using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;

var results = new List<object>(); var failed = 0; var assertions = 0;
var root = Path.Combine(Path.GetTempPath(), "psd-next-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    foreach (var psb in new[] { false, true })
    foreach (var rle in new[] { false, true })
    foreach (var groups in new[] { false, true })
    {
        Case($"source-roundtrip-psb={psb}-rle={rle}-groups={groups}", () =>
        {
            var source = Path.Combine(root, Guid.NewGuid() + ".psd");
            PsdFixture.Write(source, psb, rle, groups);
            var originalHash = CompiledFormat.Hash(File.ReadAllBytes(source));
            var directory = new PsdCompiler().Compile(source, Path.Combine(root, "cache"));
            Equal(originalHash, CompiledFormat.Hash(File.ReadAllBytes(source)));
            File.Delete(source); // Reader must not need the original, parser, or its stream.
            using var doc = new CompiledDocument(directory);
            Equal(0L, doc.BlockReadCount); Equal(psb ? 2 : 1, doc.Manifest.PsdVersion);
            Equal(8, doc.Manifest.Width); Equal(groups ? 4 : 2, doc.Manifest.Nodes.Length);
            var node = doc.Manifest.Nodes.Single(n => n.Name == (groups ? "目/％" : "visible"));
            Equal(new PixelRect(3, -2, 8, 4), node.Bounds);
            Sequence(PsdFixture.Pixels(), doc.ReadBlock(node.PixelBlockId!.Value));
            // RGB at alpha=0 survives too: canonical storage is straight BGRA, not premultiplied.
            Equal((byte)11, doc.ReadBlock(node.PixelBlockId.Value)[0]);
            if (groups)
            {
                Equal(NodeKind.Group, doc.Manifest.Nodes[0].Kind); Equal("pass", doc.Manifest.Nodes[0].BlendMode);
                Equal((byte)191, doc.Manifest.Nodes[0].Opacity); Equal(0, node.ParentId!.Value);
                Equal("mul ", node.BlendMode); Equal((byte)173, node.Opacity); True(node.Clipping);
                Equal((byte)255, node.Mask!.DefaultColor);
                Sequence(PsdFixture.MaskPixels(), doc.ReadBlock(node.Mask.Planes.Single().BlockId));
                Equal("luni", node.ExtraTags.Single().Key);
                True(doc.ReadBlock(node.ExtraTags.Single().BlockId).Length > 0);
                True(doc.Manifest.Nodes.Single(n => n.Name == "body").ParentId is null);
            }
            True(!doc.Manifest.Nodes.Single(n => n.Name == (groups ? "eye" : "hidden")).DefaultVisible);
            doc.VerifyAll();
            Equal(2, Directory.GetFiles(directory).Length);
        });
    }

    Case("exact-block-roundtrip-and-random-access", () =>
    {
        var (dir, m) = Store(); using var doc = new CompiledDocument(dir);
        Equal(0L, doc.BlockReadCount);
        Sequence(new byte[] { 1, 2, 3, 4 }, doc.ReadBlock(0)); Equal(1L, doc.BlockReadCount);
        Sequence(Enumerable.Repeat((byte)7, 4096).ToArray(), doc.ReadBlock(1));
        Equal(BlockCodec.Raw, m.Blocks[0].Codec); Equal(BlockCodec.Brotli, m.Blocks[1].Codec);
        Equal(2L, doc.BlockReadCount);
    });
    Case("fingerprint-stable-across-location", () =>
    {
        var a = Path.Combine(root, "a.psd"); var b = Path.Combine(root, "b.psd"); PsdFixture.Write(a); File.Copy(a, b);
        var compiler = new PsdCompiler(); var cache = Path.Combine(root, "same-source");
        using var x = new CompiledDocument(compiler.Compile(a, cache)); using var y = new CompiledDocument(compiler.Compile(b, cache));
        Equal(x.Manifest.GenerationId, y.Manifest.GenerationId);
        PsdFixture.Write(a, seed: 42); using var z = new CompiledDocument(compiler.Compile(a, cache));
        True(x.Manifest.GenerationId != z.Manifest.GenerationId);
        // Old generation remains valid while the new one exists.
        x.VerifyAll();
    });
    Case("cache-deletion-and-rebuild-preserve-layer-ref", () =>
    {
        var source = Path.Combine(root, "rebuild.psd"); PsdFixture.Write(source);
        var compiler = new PsdCompiler(); var dir = compiler.Compile(source, Path.Combine(root, "rebuild"));
        LayerRef reference;
        using (var d = new CompiledDocument(dir)) reference = new(d.Manifest.Source.Sha256, 0, d.Manifest.Nodes[0].Name);
        Directory.Delete(dir, true);
        using var rebuilt = new CompiledDocument(compiler.Compile(source, Path.Combine(root, "rebuild")));
        Equal(LayerResolution.Found, LayerReferences.Resolve(rebuilt.Manifest, reference, out _));
        PsdFixture.Write(source, seed: 55);
        using var changed = new CompiledDocument(compiler.Compile(source, Path.Combine(root, "rebuild")));
        Equal(LayerResolution.SourceChanged, LayerReferences.Resolve(changed.Manifest, reference, out _));
    });
    Case("reject-unsupported-depth-without-publishing", () =>
    {
        var source = Path.Combine(root, "depth.psd"); PsdFixture.Write(source);
        var b = File.ReadAllBytes(source); b[23] = 16; File.WriteAllBytes(source, b);
        var cache = Path.Combine(root, "bad-depth");
        Throws<NotSupportedException>(() => new PsdCompiler().Compile(source, cache));
        Equal(0, Directory.GetFileSystemEntries(cache).Length);
    });
    Case("cancelled-compilation-cleans-staging", () =>
    {
        var source = Path.Combine(root, "cancel.psd"); PsdFixture.Write(source); var cache = Path.Combine(root, "cancel-cache");
        Throws<OperationCanceledException>(() => new PsdCompiler().Compile(source, cache, new CancellationToken(true)));
        Equal(0, Directory.GetFileSystemEntries(cache).Length);
    });
    Case("source-limit-fails-before-parser", () =>
    {
        var source = Path.Combine(root, "limit.psd"); PsdFixture.Write(source);
        Throws<InvalidDataException>(() => new PsdCompiler(new() { MaxSourceBytes = 30 }).Compile(source, Path.Combine(root, "limit")));
    });
    Case("manifest-tamper-rejected", () =>
    {
        var (dir, _) = Store(); File.AppendAllText(Path.Combine(dir, "manifest.json"), " ");
        Throws<InvalidDataException>(() => { using var _ = new CompiledDocument(dir); });
    });
    Case("raw-pixel-corruption-rejected", () =>
    {
        var (dir, m) = Store(); var bytes = File.ReadAllBytes(Path.Combine(dir, "pixels.bin"));
        bytes[(int)m.Blocks[0].Offset] ^= 1; File.WriteAllBytes(Path.Combine(dir, "pixels.bin"), bytes);
        using var d = new CompiledDocument(dir); Throws<InvalidDataException>(() => d.ReadBlock(0));
        // Corrupt unused block does not force every block to be decoded on open.
        Sequence(Enumerable.Repeat((byte)7, 4096).ToArray(), d.ReadBlock(1));
    });
    Case("compressed-corruption-rejected", () =>
    {
        var (dir, m) = Store(); var bytes = File.ReadAllBytes(Path.Combine(dir, "pixels.bin"));
        bytes[(int)m.Blocks[1].Offset] ^= 127; File.WriteAllBytes(Path.Combine(dir, "pixels.bin"), bytes);
        using var d = new CompiledDocument(dir); Throws<InvalidDataException>(() => d.ReadBlock(1));
    });
    Case("truncated-store-rejected", () =>
    {
        var (dir, _) = Store(); using (var f = File.OpenWrite(Path.Combine(dir, "pixels.bin"))) f.SetLength(41);
        Throws<InvalidDataException>(() => { using var _ = new CompiledDocument(dir); });
    });
    Case("trailing-store-data-rejected", () =>
    {
        var (dir, _) = Store(); using (var f = new FileStream(Path.Combine(dir, "pixels.bin"), FileMode.Append)) f.WriteByte(0);
        Throws<InvalidDataException>(() => { using var _ = new CompiledDocument(dir); });
    });
    foreach (var mutation in new[] { "schema", "parent", "block-size", "block-offset", "generation", "reference", "unknown-codec" })
    {
        Case("untrusted-index-" + mutation, () =>
        {
            var (dir, _) = Store(); var path = Path.Combine(dir, "manifest.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            switch (mutation)
            {
                case "schema": json["SchemaVersion"] = 99; break;
                case "parent": json["Nodes"]![0]!["ParentId"] = 0; break;
                case "block-size": json["Blocks"]![0]!["RawLength"] = int.MaxValue; break;
                case "block-offset": json["Blocks"]![0]!["Offset"] = -1; break;
                case "generation": json["GenerationId"] = new string('0', 64); break;
                case "reference": json["Nodes"]![0]!["PixelBlockId"] = 500; break;
                case "unknown-codec": json["Blocks"]![0]!["Codec"] = 200; break;
            }
            var manifest = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()); File.WriteAllBytes(path, manifest);
            using (var f = File.OpenWrite(Path.Combine(dir, "pixels.bin"))) { f.Position = 8; f.Write(Convert.FromHexString(CompiledFormat.Hash(manifest))); }
            Throws<InvalidDataException>(() => { using var _ = new CompiledDocument(dir); });
        });
    }
    Case("cache-reuse-eviction-and-reload", () =>
    {
        var (dir, _) = Store(); using var doc = new CompiledDocument(dir); using var cache = new BlockCache(doc, 4096);
        using (var a = cache.Acquire(0)) Equal(4, a.Memory.Length);
        using (var a = cache.Acquire(0)) Equal(4, a.Memory.Length);
        Equal(1L, doc.BlockReadCount); Equal(1L, cache.HitCount);
        using (var b = cache.Acquire(1)) Equal(4096L, cache.ResidentBytes);
        True(cache.ResidentBytes <= 4096);
        using (var a = cache.Acquire(0)) Sequence(new byte[] { 1, 2, 3, 4 }, a.Memory.ToArray());
        Equal(3L, doc.BlockReadCount); cache.Trim(); Equal(0L, cache.ResidentBytes);
    });
    Case("cache-borrowers-prevent-eviction", () =>
    {
        var (dir, _) = Store(); using var doc = new CompiledDocument(dir); using var cache = new BlockCache(doc, 4096);
        using var a = cache.Acquire(0); using var b = cache.Acquire(0);
        Throws<CacheCapacityException>(() => cache.Acquire(1)); Equal(4L, cache.ResidentBytes);
        cache.Trim(); Equal(4L, cache.ResidentBytes); a.Dispose(); cache.Trim(); Equal(4L, cache.ResidentBytes);
        b.Dispose(); cache.Trim(); Equal(0L, cache.ResidentBytes); b.Dispose();
    });
    Case("cache-disposal-defers-borrowed-buffer-release", () =>
    {
        var (dir, _) = Store(); using var doc = new CompiledDocument(dir); var cache = new BlockCache(doc, 4096);
        var lease = cache.Acquire(0); cache.Dispose(); Equal(4L, cache.ResidentBytes); Equal(4, lease.Memory.Length);
        Throws<ObjectDisposedException>(() => cache.Acquire(0)); lease.Dispose(); Equal(0L, cache.ResidentBytes);
        Throws<ObjectDisposedException>(() => { _ = lease.Memory; }); cache.Dispose();
    });
    Case("cache-single-flight-concurrent-readers", () =>
    {
        var (dir, _) = Store(); using var doc = new CompiledDocument(dir); using var cache = new BlockCache(doc, 4096);
        Parallel.For(0, 64, _ => { using var lease = cache.Acquire(0); if (lease.Memory.Span[0] != 1) throw new Exception("Wrong pixel."); });
        Equal(1L, doc.BlockReadCount); Equal(63L, cache.HitCount); Equal(4L, cache.ResidentBytes);
    });
    Case("cache-oversized-admission-fails-without-loading", () =>
    {
        var (dir, _) = Store(); using var doc = new CompiledDocument(dir); using var cache = new BlockCache(doc, 4);
        Throws<CacheCapacityException>(() => cache.Acquire(1)); Equal(0L, doc.BlockReadCount);
    });
    Case("appearance-key-tracks-only-render-inputs", () =>
    {
        var (_, m) = Store();
        var a = AppearanceKey.Create(m, [0, 1], "profile-v1"); Equal(a, AppearanceKey.Create(m, [0, 1], "profile-v1"));
        True(a != AppearanceKey.Create(m, [1, 0], "profile-v1")); True(a != AppearanceKey.Create(m, [0], "profile-v1"));
        True(a != AppearanceKey.Create(m, [0, 1], "profile-v2")); True(a != AppearanceKey.Create(m, [0, 1], "profile-v1", flipState: 1));
        True(a != AppearanceKey.Create(m, [0, 1], "profile-v1", deviceEpoch: 1));
        Throws<ArgumentException>(() => AppearanceKey.Create(m, [0, 0], "profile-v1"));
    });
    Case("layer-reference-never-guesses-by-name", () =>
    {
        var (_, m) = Store();
        Equal(LayerResolution.Found, LayerReferences.Resolve(m, new(m.Source.Sha256, 0, "duplicate"), out var node)); Equal(0, node!.Id);
        Equal(LayerResolution.Found, LayerReferences.Resolve(m, new(m.Source.Sha256, 1, "duplicate"), out node)); Equal(1, node!.Id);
        Equal(LayerResolution.NameMismatch, LayerReferences.Resolve(m, new(m.Source.Sha256, 0, "other"), out _));
        Equal(LayerResolution.Missing, LayerReferences.Resolve(m, new(m.Source.Sha256, 50, "duplicate"), out _));
        Equal(LayerResolution.SourceChanged, LayerReferences.Resolve(m, new(new string('a', 64), 0, "duplicate"), out _));
    });
    Case("disposed-document-rejects-read", () =>
    {
        var (dir, _) = Store(); var doc = new CompiledDocument(dir); doc.Dispose(); doc.Dispose();
        Throws<ObjectDisposedException>(() => doc.ReadBlock(0));
    });
}
finally { Directory.Delete(root, recursive: true); }

var summary = new { schema = "psd-tachie-next.pure-tests.v1", suite = "P0 compiled asset + cache primitives", total = results.Count,
    failed, assertions, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    sourceHead = Environment.GetEnvironmentVariable("GITHUB_SHA"), parserCommit = "ff3aee18a95e5fb6e868585a5b0ad15f46decd89", results };
var outputPath = args.Length == 1 ? args[0] : "test-results.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"SUMMARY: {results.Count - failed}/{results.Count} cases passed; {assertions} assertions; {failed} failures.");
return failed == 0 ? 0 : 1;

void Case(string name, Action body)
{
    try { body(); results.Add(new { name, status = "PASS" }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; results.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
}
void True(bool value) { assertions++; if (!value) throw new Exception("Assertion failed."); }
void Equal<T>(T expected, T actual) { assertions++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
void Sequence(byte[] expected, byte[] actual) { assertions++; if (!expected.AsSpan().SequenceEqual(actual)) throw new Exception("Byte sequences differ."); }
void Throws<T>(Action body) where T : Exception
{
    assertions++; try { body(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name);
}
(string, CompiledManifest) Store()
{
    var dir = Path.Combine(root, "store-" + Guid.NewGuid().ToString("N"));
    using var w = new CompiledStoreWriter(dir);
    var a = w.AddBlock(BlockFormat.Bgra8Straight, 1, 1, new byte[] { 1, 2, 3, 4 });
    var b = w.AddBlock(BlockFormat.Bgra8Straight, 32, 32, Enumerable.Repeat((byte)7, 4096).ToArray());
    var source = new SourceFingerprint(CompiledFormat.Hash(new byte[26]), 26);
    var nodes = ImmutableArray.Create(
        new LayerNode(0, null, 0, NodeKind.Layer, "duplicate", new(0, 0, 1, 1), 0, true, "norm", 255, false, a, null, []),
        new LayerNode(1, null, 1, NodeKind.Layer, "duplicate", new(0, 0, 32, 32), 0, true, "norm", 255, false, b, null, []));
    var manifest = new CompiledManifest(1, CompiledFormat.CompilerId, CompiledFormat.Generation(source), source,
        1, 32, 32, 8, 3, null, null, nodes, w.Blocks);
    w.Complete(dir, manifest); return (dir, manifest);
}
