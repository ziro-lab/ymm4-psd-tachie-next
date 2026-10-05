using System.Collections.Immutable;
using PsdTachieNext.Core;

internal static class VisibilityCases
{
    internal static void Run(Action<string, Action> test, Action<bool> check, string root)
    {
        test("C-prefix-defaults-radio-parent-scope-and-literal-markers", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(Store(root,
                new(null, "parent", false, Group: true), new(0, "*same", true), new(0, "*same", true),
                new(0, "!forced", false), new(null, "*first", false), new(null, "*second", false),
                new(null, "ordinary-a", true), new(null, "ordinary-b", true),
                new(null, "!", false), new(null, "*", false), new(null, "!?", false)));
            var state = PsdPrefixVisibility.Create(doc.Notation);
            check(state.EnabledNodeIds.SequenceEqual([1, 3, 4, 6, 7]));
            check(!doc.Manifest.Nodes[3].DefaultVisible && !doc.Manifest.Nodes[4].DefaultVisible);
            check(RenderPlan.CreatePrefixVisibility(doc.Manifest, state).ActiveNodeIds.SequenceEqual([4, 6, 7]));
            check(ReferenceEquals(state, state.SetVisible(1, false)));
            check(ReferenceEquals(state, state.SetVisible(3, false)));
            check(ReferenceEquals(state, state.SetVisible(6, true)));
            check(state.SetVisible(9, true).IsLocallyVisible(9)); // bare marker is ordinary
            check(pool.Snapshot().BlockReadCount == 0 && pool.Snapshot().ResidentDecodedBytes == 0);
        });
        test("C-prefix-switch-retains-hidden-child-choice-and-prepares-only-effective-blocks", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(Store(root,
                new(null, "parent", false, Group: true), new(0, "*same", true),
                new(0, "*same", false), new(0, "!forced", false)));
            var original = PsdPrefixVisibility.Create(doc.Notation);
            var switched = original.SetVisible(2, true);
            check(original.IsLocallyVisible(1) && !original.IsLocallyVisible(2));
            check(!switched.IsLocallyVisible(1) && switched.IsLocallyVisible(2));
            var hidden = RenderPlan.CreatePrefixVisibility(doc.Manifest, switched);
            check(hidden.ActiveNodeIds.IsEmpty && hidden.RequiredBlockIds.IsEmpty);
            check(AppearanceKey.Create(doc.Manifest, hidden.ActiveNodeIds, "prefix") ==
                AppearanceKey.Create(doc.Manifest, RenderPlan.CreatePrefixVisibility(doc.Manifest, original).ActiveNodeIds, "prefix"));
            var shown = switched.SetVisible(0, true);
            var plan = RenderPlan.CreatePrefixVisibility(doc.Manifest, shown);
            check(plan.ActiveNodeIds.SequenceEqual([0, 2, 3]));
            check(plan.RequiredBlockIds.SequenceEqual([1, 2]) && plan.UploadBytes == 8);
            using (var ready = PreparedAppearanceLease.Prepare(doc, plan))
            {
                check(ReferenceEquals(shown, ready.Plan.PrefixVisibility));
                check(ready.GetBlock(1).Span.SequenceEqual(new byte[] { 2, 0, 0, 255 }));
                check(ready.GetBlock(2).Span.SequenceEqual(new byte[] { 3, 0, 0, 255 }));
                check(pool.Snapshot().BlockReadCount == 2);
            }
            var reshown = shown.SetVisible(0, false).SetVisible(0, true);
            check(reshown.EnabledNodeIds.SequenceEqual(shown.EnabledNodeIds));
            check(!doc.Manifest.Nodes[0].DefaultVisible && !doc.Manifest.Nodes[2].DefaultVisible);
        });
        test("C-prefix-force-visible-does-not-bypass-clipping-base-or-zero-opacity", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(Store(root, new(null, "!clip", false, Clipping: true),
                new(null, "base", false), new(null, "!zero", false, Opacity: 0)));
            var state = PsdPrefixVisibility.Create(doc.Notation);
            check(state.IsLocallyVisible(0) && state.IsLocallyVisible(2));
            var hidden = RenderPlan.CreatePrefixVisibility(doc.Manifest, state);
            check(hidden.ActiveNodeIds.IsEmpty && hidden.RequiredBlockIds.IsEmpty);
            var shown = RenderPlan.CreatePrefixVisibility(doc.Manifest, state.SetVisible(1, true));
            check(shown.ActiveNodeIds.SequenceEqual([0, 1]) && shown.RequiredBlockIds.SequenceEqual([0, 1]));
        });
        test("C-prefix-state-rejects-foreign-generation-and-invalid-ids", () =>
        {
            using var pool = new SharedDocumentPool(128, 2);
            using var a = pool.Acquire(Store(root, new Spec(null, "*a", true)));
            using var b = pool.Acquire(Store(root, new Spec(null, "*b", true)));
            var state = PsdPrefixVisibility.Create(a.Notation);
            Reject<ArgumentException>(() => RenderPlan.CreatePrefixVisibility(b.Manifest, state), check);
            foreach (var id in new[] { -1, int.MinValue, 1 })
            {
                Reject<ArgumentOutOfRangeException>(() => state.IsLocallyVisible(id), check);
                Reject<ArgumentOutOfRangeException>(() => state.SetVisible(id, true), check);
            }
            check(state.IsLocallyVisible(0) && pool.Snapshot().BlockReadCount == 0);
        });
        test("C-prefix-unimplemented-flips-and-token-edges-reject-before-block-read", () =>
        {
            (string Name, PsdNotationUnsupportedReason Reason)[] names =
            [
                ("body:flipx", PsdNotationUnsupportedReason.FlipNotation),
                ("body:flipy", PsdNotationUnsupportedReason.FlipNotation),
                ("body:flipxy", PsdNotationUnsupportedReason.FlipNotation),
                ("body:flipx:flipy", PsdNotationUnsupportedReason.FlipNotation),
                (":flipx", PsdNotationUnsupportedReason.FlipNotation),
                ("flipx", PsdNotationUnsupportedReason.TokenOnlyName),
                ("*flipxy", PsdNotationUnsupportedReason.TokenOnlyName),
                ("flipx:flipy", PsdNotationUnsupportedReason.FlipNotation | PsdNotationUnsupportedReason.TokenOnlyName)
            ];
            foreach (var row in names)
            foreach (var visible in new[] { true, false })
            {
                using var pool = new SharedDocumentPool(128, 1);
                using var doc = pool.Acquire(Store(root, new(null, "ordinary", true), new(null, row.Name, visible)));
                UnsupportedPsdNotationException? failure = null;
                try { _ = PsdPrefixVisibility.Create(doc.Notation); }
                catch (UnsupportedPsdNotationException ex) { failure = ex; }
                check(failure?.Reasons == row.Reason);
                var diagnostic = PreparationDiagnostic.From(failure);
                check(diagnostic?.Recovery == PreparationRecovery.UnsupportedNotation);
                check(!diagnostic!.Message.Contains("RGB8") && !diagnostic.Action.Contains("RGB8"));
                if (row.Reason.HasFlag(PsdNotationUnsupportedReason.FlipNotation)) check(diagnostic.Message.Contains("反転記法"));
                if (row.Reason.HasFlag(PsdNotationUnsupportedReason.TokenOnlyName)) check(diagnostic.Message.Contains("トークンだけの名前"));
                check(pool.Snapshot().BlockReadCount == 0);
            }
            var pixelProfile = PreparationDiagnostic.From(new NotSupportedException("Unsupported depth"));
            check(pixelProfile?.Recovery == PreparationRecovery.UnsupportedSource && pixelProfile.Action.Contains("RGB8"));
        });
    }

    private static void Reject<T>(Action action, Action<bool> check) where T : Exception
    {
        var rejected = false; try { action(); } catch (T) { rejected = true; }
        check(rejected);
    }

    private sealed record Spec(int? Parent, string Name, bool Visible, bool Group = false,
        bool Clipping = false, byte Opacity = 255);

    private static string Store(string root, params Spec[] specs)
    {
        var dir = Path.Combine(root, Guid.NewGuid().ToString("N")); using var writer = new CompiledStoreWriter(dir);
        var orders = new Dictionary<int, int>();
        var nodes = specs.Select((s, id) =>
        {
            var parent = s.Parent ?? -1; var order = orders.GetValueOrDefault(parent); orders[parent] = order + 1;
            var block = s.Group ? (int?)null : writer.AddBlock(BlockFormat.Bgra8Straight, 1, 1,
                new byte[] { (byte)id, 0, 0, 255 }, compress: false);
            return new LayerNode(id, s.Parent, order, s.Group ? NodeKind.Group : NodeKind.Layer, s.Name,
                new(0, 0, 1, 1), s.Visible ? (byte)0 : (byte)2, s.Visible, s.Group ? "pass" : "norm",
                s.Opacity, s.Clipping, block, null, []);
        }).ToImmutableArray();
        var source = new SourceFingerprint(CompiledFormat.Hash(System.Text.Encoding.UTF8.GetBytes(dir)), 26);
        writer.Complete(dir, new(1, CompiledFormat.CompilerId, CompiledFormat.Generation(source), source,
            1, 1, 1, 8, 3, null, null, nodes, writer.Blocks));
        return dir;
    }
}
