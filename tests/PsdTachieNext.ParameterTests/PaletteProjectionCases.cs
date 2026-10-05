using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;

internal static class PaletteProjectionCases
{
    internal static void Run(string output)
    {
        var root = Path.Combine(Path.GetDirectoryName(output)!, "palette-projection"); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "synthetic-radio.psd");
        PsdFixture.Write(source, layerIds: [1, 2, 3, 4, 5],
            simpleLayerNames: ["*base", "*A:flipx", "*C:flipy", "*D:flipxy", "*B:flipx:flipy"],
            simpleLayerVisible: [true, false, false, false, false]);
        var compiled = new PsdCompiler().Compile(source, Path.Combine(root, "cache"));
        using var pool = new SharedDocumentPool(4096, 1); using var doc = pool.Acquire(compiled);
        var index = PsdLayerReferenceIndex.Read(doc);
        var assertions = 0;
        void Check(bool value) { assertions++; if (!value) throw new InvalidOperationException("Current-direction palette projection failed."); }
        var settings = PsdAppearanceSettings.Create("synthetic");
        foreach (var id in new[] { 1, 4, 1 })
        {
            var edit = settings.SetVisible("synthetic", index, id, true); Check(edit.Succeeded); settings = edit.Settings!;
        }
        var json = settings.Json; var state = settings.Resolve("synthetic", index).State!;
        // Reproduction: the retained X/Y choices are both locally true, but only one is selected now.
        Check(state.IsLocallyVisible(1) && state.IsLocallyVisible(4));
        Check(PsdPaletteViewModel.SelectedOrigins(state.WithFlip(PsdFlipState.X)).SetEquals([1]));
        Check(PsdPaletteViewModel.SelectedOrigins(state.WithFlip(PsdFlipState.Y)).SetEquals([4]));
        Check(PsdPaletteViewModel.SelectedOrigins(state.WithFlip(PsdFlipState.None)).SetEquals([0]));
        Check(PsdPaletteViewModel.SelectedOrigins(state.WithFlip(PsdFlipState.XY)).SetEquals([0]));
        Check(settings.Json == json && state.IsLocallyVisible(1) && state.IsLocallyVisible(4));
        Check(state.RadioSelectionScopes[4] == PsdDirectionScope.Y);
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { total = 1, assertions, failed = 0,
            reproducedOldLocalProjection = true, physicalInput = false, nativeHost = false }));
        Console.WriteLine($"PALETTE PROJECTION SUMMARY: {assertions} assertions; 0 failures.");
    }
}
