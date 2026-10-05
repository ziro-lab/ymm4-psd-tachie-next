using PsdTachieNext.Core;
using Spec = FlipCases.Spec;

internal static class AppearanceStackCases
{
    internal static void Run(Action<string, Action> test, Action<bool> check, string root)
    {
        test("stack-eye-only-mouth-only-preserve-clothes-off-and-immutable-owners", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(FlipCases.Store(root,
                new(null, "eye", false, PsdId: 11), new(null, "mouth", false, PsdId: 12),
                new(null, "clothes", true, PsdId: 13), new(null, "hair", true, PsdId: 14)));
            var index = PsdLayerReferenceIndex.Read(doc);
            var @base = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 2, false).Settings!;
            var eye = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 0, true).Settings!;
            var mouth = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 1, true).Settings!;
            var before = new[] { @base.Json, eye.Json, mouth.Json };
            var stack = new PsdAppearanceStack(@base, [new(2, eye), new(5, mouth)]);
            var result = stack.Resolve("asset", index);
            check(result.Succeeded && result.State!.EnabledNodeIds.SequenceEqual([0, 1, 3]));
            check(before.SequenceEqual(new[] { @base.Json, eye.Json, mouth.Json }));
            check(eye.OwnedOrigins("asset", index).SetEquals([0]) && mouth.OwnedOrigins("asset", index).SetEquals([1]));
            var absentEye = eye.Inherit("asset", index, 0).Settings!;
            check(new PsdAppearanceStack(@base, [new(2, absentEye), new(5, mouth)]).Resolve("asset", index).State!.EnabledNodeIds.SequenceEqual([1, 3]));
            check(stack.Equals(new PsdAppearanceStack(@base, [new(5, mouth), new(2, eye)])));
            check(result.Settings != eye && result.Settings != mouth && pool.Snapshot().BlockReadCount == 4);
        });
        test("stack-higher-layer-priority-null-inherit-explicit-None-and-tie-rejection", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(FlipCases.Store(root, new Spec(null, "eye", true, PsdId: 10)));
            var index = PsdLayerReferenceIndex.Read(doc);
            var off = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 0, false).Settings!;
            var on = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 0, true).Settings!;
            check(new PsdAppearanceStack(null, [new(8, off), new(2, on)]).Resolve("asset", index).State!.EnabledNodeIds.IsEmpty);
            check(new PsdAppearanceStack(null, [new(2, off), new(8, on)]).Resolve("asset", index).State!.EnabledNodeIds.SequenceEqual([0]));
            check(!new PsdAppearanceStack(null, [new(2, off), new(2, on)]).Resolve("asset", index).Succeeded);
            check(new PsdAppearanceStack(null, [new(2, off), new(2, off)]).Resolve("asset", index).Succeeded);
            var x = on.WithFlip("asset", index, PsdFlipState.X).Settings!;
            var none = off.WithFlip("asset", index, PsdFlipState.None).Settings!;
            check(new PsdAppearanceStack(x, [new(2, off)]).Resolve("asset", index).State!.FlipState == PsdFlipState.X);
            check(new PsdAppearanceStack(x, [new(2, none)]).Resolve("asset", index).State!.FlipState == PsdFlipState.None);
            check(!new PsdAppearanceStack(null, [new(2, x), new(2, none)]).Resolve("asset", index).Succeeded);
        });
        test("stack-independent-radio-groups-and-direction-domain-priority", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(FlipCases.Store(root,
                new(null, "eyes", true, Group: true, PsdId: 1), new(0, "*open", true, PsdId: 2), new(0, "*closed", false, PsdId: 3),
                new(null, "mouth", true, Group: true, PsdId: 4), new(3, "*rest", true, PsdId: 5), new(3, "*smile", false, PsdId: 6)));
            var index = PsdLayerReferenceIndex.Read(doc);
            var eye = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 2, true).Settings!;
            var mouth = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 5, true).Settings!;
            var result = new PsdAppearanceStack(null, [new(2, eye), new(2, mouth)]).Resolve("asset", index);
            check(result.Succeeded && result.State!.EnabledNodeIds.SequenceEqual([0, 2, 3, 5]));
            var open = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 1, true).Settings!;
            check(new PsdAppearanceStack(null, [new(2, eye), new(5, open), new(6, mouth)]).Resolve("asset", index).State!.EnabledNodeIds.SequenceEqual([0, 1, 3, 5]));
            check(!new PsdAppearanceStack(null, [new(2, eye), new(2, open)]).Resolve("asset", index).Succeeded);
        });
        test("stack-repair-foreign-unknown-all-or-nothing-never-mutates-input", () =>
        {
            using var pool = new SharedDocumentPool(128, 1);
            using var doc = pool.Acquire(FlipCases.Store(root, new Spec(null, "eye", false, PsdId: 1)));
            var index = PsdLayerReferenceIndex.Read(doc);
            var good = PsdAppearanceSettings.Create("asset").SetVisible("asset", index, 0, true).Settings!;
            var foreign = PsdAppearanceSettings.Create("other");
            var unknown = PsdAppearanceSettings.FromJson(good.Json.Replace("\"version\":1", "\"version\":999", StringComparison.Ordinal));
            foreach (var bad in new[] { foreign, unknown })
            {
                var result = new PsdAppearanceStack(null, [new(2, good), new(5, bad)]).Resolve("asset", index);
                check(!result.Succeeded && result.State is null && ReferenceEquals(result.Settings, bad));
                check(good.Resolve("asset", index).State!.EnabledNodeIds.SequenceEqual([0]));
            }
        });
    }
}
