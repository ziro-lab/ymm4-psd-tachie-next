using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using PsdTachieNext.Core;

internal static class NotationCases
{
    public static void Run(Action<string, Action> test, Action<bool> check, string root)
    {
        test("C-notation-prefix-and-flip-reference-matrix", () =>
        {
            (string Name, string Display, PsdSelectionMarker Marker, string Base, PsdFlipTargets Flip)[] rows =
            [
                ("body", "body", PsdSelectionMarker.Ordinary, "body", PsdFlipTargets.None),
                ("*mouth", "mouth", PsdSelectionMarker.Radio, "*mouth", PsdFlipTargets.None),
                ("!body", "body", PsdSelectionMarker.ForceVisible, "!body", PsdFlipTargets.None),
                ("*!mouth", "!mouth", PsdSelectionMarker.Radio, "*!mouth", PsdFlipTargets.None),
                ("!*body", "*body", PsdSelectionMarker.ForceVisible, "!*body", PsdFlipTargets.None),
                ("!?", "!?", PsdSelectionMarker.Ordinary, "!?", PsdFlipTargets.None),
                (" *mouth", " *mouth", PsdSelectionMarker.Ordinary, " *mouth", PsdFlipTargets.None),
                ("＊mouth", "＊mouth", PsdSelectionMarker.Ordinary, "＊mouth", PsdFlipTargets.None),
                ("body:flipx", "body", PsdSelectionMarker.Ordinary, "body", PsdFlipTargets.X),
                ("body:flipy", "body", PsdSelectionMarker.Ordinary, "body", PsdFlipTargets.Y),
                ("body:flipxy", "body", PsdSelectionMarker.Ordinary, "body", PsdFlipTargets.XY),
                ("body:flipx:flipy", "body", PsdSelectionMarker.Ordinary, "body", PsdFlipTargets.X | PsdFlipTargets.Y),
                ("*mouth:flipxy:flipx", "mouth", PsdSelectionMarker.Radio, "*mouth", PsdFlipTargets.XY | PsdFlipTargets.X),
                ("body:flipx:flipx", "body", PsdSelectionMarker.Ordinary, "body", PsdFlipTargets.X),
                ("body:flipx:other", "body:flipx:other", PsdSelectionMarker.Ordinary, "body:flipx:other", PsdFlipTargets.None),
                ("body:other:flipx", "body:other", PsdSelectionMarker.Ordinary, "body:other", PsdFlipTargets.X),
                ("body:FlipX", "body:FlipX", PsdSelectionMarker.Ordinary, "body:FlipX", PsdFlipTargets.None),
                ("body:flipx ", "body:flipx ", PsdSelectionMarker.Ordinary, "body:flipx ", PsdFlipTargets.None)
            ];
            var dir = Store(root, rows.Select(r => ((int?)null, NodeKind.Layer, r.Name, false)).ToArray());
            using var pool = new SharedDocumentPool(128, 1); using var lease = pool.Acquire(dir);
            var index = lease.Notation;
            for (var i = 0; i < rows.Length; i++)
            {
                var actual = index.Nodes[i]; var expected = rows[i];
                check(actual.OriginalName == expected.Name && actual.DisplayName == expected.Display);
                check(actual.SelectionMarker == expected.Marker && actual.FlipBaseOriginalName == expected.Base);
                check(actual.FlipTargets == expected.Flip && actual.Diagnostics == PsdNotationDiagnostics.None);
                check(!actual.DefaultVisible && lease.Manifest.Nodes[i].Name == expected.Name);
            }
            check((PsdFlipTargets.X | PsdFlipTargets.Y) != PsdFlipTargets.XY);
            check(pool.Snapshot().BlockReadCount == 0);
        });
        test("C-notation-unresolved-edge-names-preserved-with-diagnostics", () =>
        {
            var names = new[] { "*", "!", ":flipx", "*:flipxy", "flipx", "!flipy", "flipx:flipy", "*flipxy:flipx", "" };
            var issues = new[] { PsdNotationDiagnostics.MarkerWithoutName, PsdNotationDiagnostics.MarkerWithoutName,
                PsdNotationDiagnostics.EmptyFlipBase, PsdNotationDiagnostics.EmptyFlipBase,
                PsdNotationDiagnostics.ReferenceTokenOnlyName, PsdNotationDiagnostics.ReferenceTokenOnlyName,
                PsdNotationDiagnostics.ReferenceTokenOnlyName, PsdNotationDiagnostics.ReferenceTokenOnlyName,
                PsdNotationDiagnostics.None };
            using var pool = new SharedDocumentPool(128, 1);
            using var lease = pool.Acquire(Store(root, names.Select(n => ((int?)null, NodeKind.Layer, n, true)).ToArray()));
            for (var i = 0; i < names.Length; i++)
            {
                check(lease.Notation.Nodes[i].OriginalName == names[i]);
                check(lease.Notation.Nodes[i].DisplayName == names[i]);
                check(lease.Notation.Nodes[i].Diagnostics == issues[i]);
            }
        });
        test("C-notation-hierarchy-duplicates-and-raw-special-characters", () =>
        {
            const string raw = "日本語/%2f\\1~'\"\t";
            var dir = Store(root,
                (null, NodeKind.Group, "*group", false),
                (0, NodeKind.Layer, "*mouth", true),
                (0, NodeKind.Layer, "*mouth", false),
                (0, NodeKind.Layer, "mouth", true),
                (0, NodeKind.Group, "nested", true),
                (4, NodeKind.Layer, "*mouth", true),
                (4, NodeKind.Layer, raw, true),
                (null, NodeKind.Layer, "*mouth", true),
                (null, NodeKind.Group, "*group", true));
            using var pool = new SharedDocumentPool(128, 1); using var lease = pool.Acquire(dir);
            var index = lease.Notation;
            check(index.Children(null).SequenceEqual([0, 7, 8]));
            check(index.Children(0).SequenceEqual([1, 2, 3, 4]));
            check(index.Children(4).SequenceEqual([5, 6]));
            check(index.Children(1).IsEmpty);
            check(index.FindOriginalName(0, "*mouth").SequenceEqual([1, 2]));
            check(index.FindOriginalName(4, "*mouth").SequenceEqual([5]));
            check(index.FindOriginalName(null, "*mouth").SequenceEqual([7]));
            check(index.FindOriginalName(null, "*group").SequenceEqual([0, 8]));
            check(index.FindOriginalName(0, "mouth").SequenceEqual([3]));
            check(index.FindOriginalName(0, "*Mouth").IsEmpty);
            check(index.FindOriginalName(4, raw).SequenceEqual([6]));
            check(index.FindOriginalName(4, "日本語//\\1~'\"\t").IsEmpty);
            check(index.Nodes[6].OriginalName == raw && index.Nodes[6].DisplayName == raw);
            check(index.Nodes[0].Kind == NodeKind.Group && index.Nodes[5].ParentId == 4);
            check(index.Nodes[2].Order == 1 && !index.Nodes[2].DefaultVisible);
            check(lease.Manifest.Nodes[0].Name == "*group" && !lease.Manifest.Nodes[0].DefaultVisible);
            check(pool.Snapshot().BlockReadCount == 0);
        });
        test("C-notation-parallel-borrows-and-identical-copies-share-index", () =>
        {
            var dir = Store(root, (null, NodeKind.Layer, "*mouth:flipx", true));
            var copy = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(dir)) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            using var pool = new SharedDocumentPool(128, 1); using var anchor = pool.Acquire(dir);
            var results = new PsdNotationIndex[64];
            Parallel.For(0, results.Length, i =>
            {
                using var lease = pool.Acquire(i % 2 == 0 ? dir : copy);
                results[i] = lease.Notation;
            });
            check(results.All(n => ReferenceEquals(n, anchor.Notation)));
            check(anchor.Notation.GenerationId == anchor.Manifest.GenerationId);
            check(anchor.Notation.SourceSha256 == anchor.Manifest.Source.Sha256);
            check(pool.Snapshot().ActiveDocuments == 1 && pool.Snapshot().OutstandingLeases == 1);
            check(pool.Snapshot().BlockReadCount == 0 && pool.Snapshot().ResidentDecodedBytes == 0);
        });
        test("C-notation-negative-parent-id-cannot-alias-root", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var lease = pool.Acquire(Store(root, (null, NodeKind.Layer, "root", true)));
            var index = lease.Notation;
            check(index.Children(null).SequenceEqual([0]));
            check(index.FindOriginalName(null, "root").SequenceEqual([0]));
            foreach (var parent in new[] { -1, int.MinValue })
            {
                var childRejected = false; var nameRejected = false;
                try { _ = index.Children(parent); }
                catch (ArgumentOutOfRangeException ex) { childRejected = ex.ParamName == "parentId"; }
                try { _ = index.FindOriginalName(parent, "root"); }
                catch (ArgumentOutOfRangeException ex) { nameRejected = ex.ParamName == "parentId"; }
                check(childRejected); check(nameRejected);
            }
            check(index.Children(null).SequenceEqual([0]));
            check(index.FindOriginalName(null, "root").SequenceEqual([0]));
        });
        test("C-notation-source-change-does-not-reuse-index-or-node-meaning", () =>
        {
            var first = Store(root, [(null, NodeKind.Layer, "*mouth", true)], seed: 1);
            var second = Store(root, [(null, NodeKind.Layer, "!body", true)], seed: 2);
            using var pool = new SharedDocumentPool(128, 2);
            using var a = pool.Acquire(first); using var b = pool.Acquire(second);
            check(!ReferenceEquals(a.Notation, b.Notation));
            check(a.Notation.GenerationId != b.Notation.GenerationId);
            check(a.Notation.Nodes[0].OriginalName == "*mouth" && b.Notation.Nodes[0].OriginalName == "!body");
            check(a.Notation.FindOriginalName(null, "!body").IsEmpty);
            a.Dispose();
            var rejected = false;
            try { _ = a.Notation; } catch (ObjectDisposedException) { rejected = true; }
            check(rejected);
            check(b.Notation.Nodes[0].SelectionMarker == PsdSelectionMarker.ForceVisible);
        });
        test("C-disposed-document-lease-does-not-root-notation", () =>
        {
            var dir = Store(root, (null, NodeKind.Layer, "*mouth", true));
            using var pool = new SharedDocumentPool(128, 1);
            var retained = ReleaseIndex(pool, dir);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            check(!retained.Index.IsAlive);
            check(pool.Snapshot().ActiveDocuments == 0 && pool.Snapshot().OutstandingLeases == 0);
            GC.KeepAlive(retained.Lease);
        });
        test("C-notation-deep-hierarchy-is-indexed-without-recursive-paths", () =>
        {
            var nodes = Enumerable.Range(0, 2000).Select(i =>
                (i == 0 ? (int?)null : i - 1, NodeKind.Group, "*same/name", true)).ToArray();
            using var pool = new SharedDocumentPool(128, 1); using var lease = pool.Acquire(Store(root, nodes));
            check(lease.Notation.Nodes.Length == 2000);
            check(lease.Notation.Children(1998).SequenceEqual([1999]));
            check(lease.Notation.Nodes[1999].ParentId == 1998);
            check(lease.Notation.FindOriginalName(1998, "*same/name").SequenceEqual([1999]));
            check(pool.Snapshot().BlockReadCount == 0);
        });
    }

    private static string Store(string root, params (int? Parent, NodeKind Kind, string Name, bool Visible)[] specs) =>
        Store(root, specs, 0);

    private static string Store(string root, (int? Parent, NodeKind Kind, string Name, bool Visible)[] specs, byte seed)
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); using var writer = new CompiledStoreWriter(dir);
        var orders = new Dictionary<int, int>();
        var nodes = specs.Select((s, i) =>
        {
            var parent = s.Parent ?? -1; var order = orders.GetValueOrDefault(parent); orders[parent] = order + 1;
            return new LayerNode(i, s.Parent, order, s.Kind, s.Name, new(0, 0, 0, 0),
                s.Visible ? (byte)0 : (byte)2, s.Visible, s.Kind == NodeKind.Group ? "pass" : "norm", 255, false, null, null, []);
        }).ToImmutableArray();
        var source = new SourceFingerprint(CompiledFormat.Hash(Enumerable.Repeat(seed, 26).ToArray()), 26);
        writer.Complete(dir, new(1, CompiledFormat.CompilerId, CompiledFormat.Generation(source), source,
            1, 4, 4, 8, 3, null, null, nodes, writer.Blocks));
        return dir;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (SharedDocumentLease Lease, WeakReference Index) ReleaseIndex(SharedDocumentPool pool, string dir)
    {
        var lease = pool.Acquire(dir); var index = new WeakReference(lease.Notation);
        lease.Dispose(); return (lease, index);
    }
}
