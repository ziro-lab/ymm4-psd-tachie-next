using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using PsdTachieNext.Core;

var rows = new List<object>(); var failed = 0; var assertions = 0;
var root = Path.Combine(Path.GetTempPath(), "psd-next-runtime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    NotationCases.Run(Case, True, root);
    VisibilityCases.Run(Case, True, root);
    FlipCases.Run(Case, True, root);
    CheckpointCases.Run(Case, True, root);
    SparseAppearanceCases.Run(Case, True, root);
    AppearanceStackCases.Run(Case, True, root);
    Case("same-generation-consumers-share-document-and-blocks", () =>
    {
        var dir = Store(7); using var pool = new SharedDocumentPool(128, 2);
        using var a = pool.Acquire(dir); using var b = pool.Acquire(dir);
        True(ReferenceEquals(a.Manifest, b.Manifest)); Equal(1, pool.Snapshot().ActiveDocuments);
        True(ReferenceEquals(a.Notation, b.Notation)); Equal("same-name", a.Notation.Nodes[0].OriginalName);
        Equal(0L, pool.Snapshot().BlockReadCount);
        using (var x = a.AcquireBlock(0)) using (var y = b.AcquireBlock(0))
        {
            Equal((byte)7, x.Memory.Span[0]); True(x.Memory.Equals(y.Memory));
            Equal(1L, pool.Snapshot().BlockReadCount); Equal(64L, pool.Snapshot().ResidentDecodedBytes);
        }
        a.Dispose(); Equal(1, pool.Snapshot().ActiveDocuments);
        b.Dispose(); Equal(0, pool.Snapshot().ActiveDocuments); Equal(0L, pool.Snapshot().ResidentDecodedBytes);
    });
    Case("identical-compiled-copies-share-even-at-document-limit", () =>
    {
        var dir = Store(8); var copy = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(dir)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        using var pool = new SharedDocumentPool(128, 1); using var a = pool.Acquire(dir); using var b = pool.Acquire(copy);
        True(ReferenceEquals(a.Manifest, b.Manifest)); Equal(1, pool.Snapshot().ActiveDocuments);
        using var x = a.AcquireBlock(0); using var y = b.AcquireBlock(0);
        Equal(1L, pool.Snapshot().BlockReadCount); True(x.Memory.Equals(y.Memory));
    });
    Case("different-generations-remain-isolated", () =>
    {
        var first = Store(9); var second = Store(10); using var pool = new SharedDocumentPool(128, 2);
        using var a = pool.Acquire(first); using var b = pool.Acquire(second);
        True(a.Manifest.GenerationId != b.Manifest.GenerationId); Equal(2, pool.Snapshot().ActiveDocuments);
        using var x = a.AcquireBlock(0); using var y = b.AcquireBlock(0);
        Equal((byte)9, x.Memory.Span[0]); Equal((byte)10, y.Memory.Span[0]);
        Equal(128L, pool.Snapshot().ResidentDecodedBytes);
    });
    Case("document-limit-rejection-preserves-existing-consumer", () =>
    {
        var first = Store(11); var second = Store(12); using var pool = new SharedDocumentPool(128, 1);
        using var a = pool.Acquire(first);
        Throws<CacheCapacityException>(() => pool.Acquire(second)); Equal(1, pool.Snapshot().ActiveDocuments);
        using (var block = a.AcquireBlock(0)) Equal((byte)11, block.Memory.Span[0]);
        a.Dispose(); using var b = pool.Acquire(second); using var next = b.AcquireBlock(0);
        Equal((byte)12, next.Memory.Span[0]);
    });
    Case("block-outlives-source-but-final-release-closes-document", () =>
    {
        var dir = Store(13); using var pool = new SharedDocumentPool(128, 2);
        var a = pool.Acquire(dir); var block = a.AcquireBlock(0); a.Dispose();
        Equal(1, pool.Snapshot().OutstandingLeases); Equal((byte)13, block.Memory.Span[0]);
        Throws<ObjectDisposedException>(() => a.AcquireBlock(0));
        block.Dispose(); Equal(0, pool.Snapshot().ActiveDocuments);
        // The last release closed the file; reopening validates the directory anew.
        using var b = pool.Acquire(dir); Equal(0L, pool.Snapshot().BlockReadCount);
    });
    Case("shutdown-rejects-new-work-without-invalidating-borrowed-memory", () =>
    {
        var dir = Store(14); var pool = new SharedDocumentPool(128, 2);
        var a = pool.Acquire(dir); var block = a.AcquireBlock(0);
        pool.Dispose(); pool.Dispose();
        Throws<ObjectDisposedException>(() => pool.Acquire(dir));
        Throws<ObjectDisposedException>(() => a.AcquireBlock(0)); Equal((byte)14, block.Memory.Span[0]);
        a.Dispose(); block.Dispose(); block.Dispose(); a.Dispose();
        Equal(0, pool.Snapshot().OutstandingLeases); Equal(0, pool.Snapshot().ActiveDocuments);
    });
    Case("parallel-consumers-use-one-pixel-load", () =>
    {
        var dir = Store(15); using var pool = new SharedDocumentPool(128, 2); using var anchor = pool.Acquire(dir);
        Parallel.For(0, 64, _ =>
        {
            using var source = pool.Acquire(dir); using var block = source.AcquireBlock(0);
            if (block.Memory.Span[0] != 15) throw new Exception("Wrong shared pixels.");
        });
        Equal(1, pool.Snapshot().ActiveDocuments); Equal(1, pool.Snapshot().OutstandingLeases);
        Equal(1L, pool.Snapshot().BlockReadCount); Equal(64L, pool.Snapshot().ResidentDecodedBytes);
    });
    Case("trim-preserves-borrows-and-reloads-after-release", () =>
    {
        var dir = Store(16); using var pool = new SharedDocumentPool(128, 2); using var a = pool.Acquire(dir);
        var block = a.AcquireBlock(0); pool.Trim(); Equal(64L, pool.Snapshot().ResidentDecodedBytes);
        block.Dispose(); pool.Trim(); Equal(0L, pool.Snapshot().ResidentDecodedBytes);
        using var again = a.AcquireBlock(0); Equal((byte)16, again.Memory.Span[0]); Equal(2L, pool.Snapshot().BlockReadCount);
    });
    Case("retained-disposed-lease-does-not-root-evicted-pixels", () =>
    {
        var dir = Store(17); using var doc = new CompiledDocument(dir); using var cache = new BlockCache(doc, 128);
        var retained = ReleasedBuffer(cache);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        True(!retained.Buffer.IsAlive); Equal(0L, cache.ResidentBytes);
        Throws<ObjectDisposedException>(() => { _ = retained.Lease.Memory; }); GC.KeepAlive(retained.Lease);
    });
    Case("retained-disposed-shared-lease-does-not-root-pixels", () =>
    {
        var dir = Store(18); using var pool = new SharedDocumentPool(128, 2); using var a = pool.Acquire(dir);
        var retained = ReleasedSharedBuffer(a, pool);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        True(!retained.Buffer.IsAlive); Equal(0L, pool.Snapshot().ResidentDecodedBytes);
        Throws<ObjectDisposedException>(() => { _ = retained.Lease.Memory; }); GC.KeepAlive(retained.Lease);
    });
    Case("invalid-block-does-not-leak-pool-reference", () =>
    {
        var dir = Store(19); using var pool = new SharedDocumentPool(128, 2); using var a = pool.Acquire(dir);
        Throws<ArgumentOutOfRangeException>(() => a.AcquireBlock(100)); Equal(1, pool.Snapshot().OutstandingLeases);
        a.Dispose(); Equal(0, pool.Snapshot().ActiveDocuments);
    });
}
finally { Directory.Delete(root, true); }
var output = args.Length == 1 ? args[0] : "runtime-test-results.json";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(new
{
    schema = "psd-tachie-next.pure-runtime.v1", total = rows.Count, failed, assertions,
    sourceHead = Environment.GetEnvironmentVariable("SOURCE_HEAD"), checkoutSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
    runtime = RuntimeInformation.FrameworkDescription, results = rows
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RUNTIME SUMMARY: {rows.Count - failed}/{rows.Count} cases passed; {assertions} assertions; {failed} failures.");
return failed == 0 ? 0 : 1;

void Case(string name, Action body)
{
    try { body(); rows.Add(new { name, status = "PASS" }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; rows.Add(new { name, status = "FAIL", error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex); }
}
void True(bool value) { assertions++; if (!value) throw new Exception("Assertion failed."); }
void Equal<T>(T expected, T actual) { assertions++; if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
void Throws<T>(Action action) where T : Exception
{ assertions++; try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
string Store(byte seed)
{
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); using var writer = new CompiledStoreWriter(dir);
    var id = writer.AddBlock(BlockFormat.Bgra8Straight, 4, 4, Enumerable.Repeat(seed, 64).ToArray());
    var fingerprint = new SourceFingerprint(CompiledFormat.Hash(Enumerable.Repeat(seed, 26).ToArray()), 26);
    var nodes = ImmutableArray.Create(new LayerNode(0, null, 0, NodeKind.Layer, "same-name", new(0, 0, 4, 4),
        0, true, "norm", 255, false, id, null, []));
    var m = new CompiledManifest(1, CompiledFormat.CompilerId, CompiledFormat.Generation(fingerprint), fingerprint,
        1, 4, 4, 8, 3, null, null, nodes, writer.Blocks);
    writer.Complete(dir, m); return dir;
}
[MethodImpl(MethodImplOptions.NoInlining)]
static (BlockLease Lease, WeakReference Buffer) ReleasedBuffer(BlockCache cache)
{
    var lease = cache.Acquire(0);
    if (!MemoryMarshal.TryGetArray(lease.Memory, out ArraySegment<byte> segment)) throw new Exception("Expected array-backed block.");
    var weak = new WeakReference(segment.Array!); lease.Dispose(); cache.Trim(); return (lease, weak);
}
[MethodImpl(MethodImplOptions.NoInlining)]
static (SharedBlockLease Lease, WeakReference Buffer) ReleasedSharedBuffer(SharedDocumentLease source, SharedDocumentPool pool)
{
    var lease = source.AcquireBlock(0);
    if (!MemoryMarshal.TryGetArray(lease.Memory, out ArraySegment<byte> segment)) throw new Exception("Expected array-backed block.");
    var weak = new WeakReference(segment.Array!); lease.Dispose(); pool.Trim(); return (lease, weak);
}
