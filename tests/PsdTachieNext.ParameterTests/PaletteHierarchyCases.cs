using System.Text.Json;
using Newtonsoft.Json;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;

internal static class PaletteHierarchyCases
{
    internal static void Run(string output)
    {
        var root = Path.Combine(Path.GetDirectoryName(output)!, "palette-hierarchy");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "synthetic-hierarchy.psd");
        PsdFixture.WritePaletteHierarchy(source);
        var sourceHash = CompiledFormat.Hash(File.ReadAllBytes(source));
        var compiled = new PsdCompiler().Compile(source, Path.Combine(root, "cache"));
        using var pool = new SharedDocumentPool(16384, 1);
        using var document = pool.Acquire(compiled);
        var index = PsdLayerReferenceIndex.Read(document);
        var checks = new List<object>();
        var cases = new List<string>();
        void Check(bool passed, string label)
        { checks.Add(new { label, passed }); if (!passed) throw new InvalidOperationException(label); }
        const string asset = "synthetic-hierarchy";
        PsdAppearanceSettings Edit(PsdAppearanceSettings settings, int id, bool value)
        {
            var result = settings.SetVisible(asset, index, id, value);
            Check(result.Succeeded, "edit succeeds: " + id);
            return result.Settings!;
        }
        PsdAppearanceResolution Compose(PsdAppearanceSettings basis, PsdAppearanceSettings eyes, PsdAppearanceSettings mouth)
        {
            var result = new PsdAppearanceStack(basis, [new(5, mouth), new(2, eyes)]).Resolve(asset, index);
            Check(result.Succeeded, "independent hierarchy stack resolves");
            return result;
        }
        var tree = PsdPaletteTree.Project(index.Notation, new HashSet<int>());
        Check(index.Notation.Nodes.Length == 18 && tree.Length == 14, "18 physical nodes / 14 logical rows, aliases not duplicated");
        Check(tree.Select(e => e.Node.NodeId).SequenceEqual(new[] { 0,1,2,3,4,5,6,7,10,11,12,15,16,17 }), "logical preorder preserved");
        Check(tree.Single(e => e.Node.NodeId == PsdFixture.EyeClosed).Depth == 2
            && tree.Single(e => e.Node.NodeId == PsdFixture.EyeClosed).ParentOrigin == PsdFixture.Eyes, "nested eye parent/depth");
        Check(tree.Single(e => e.Node.NodeId == PsdFixture.MouthSmile).Depth == 2, "nested mouth depth");
        cases.Add("logical-hierarchy");
        var folded = new HashSet<int> { PsdFixture.Eyes, PsdFixture.Face };
        Check(PsdPaletteTree.Project(index.Notation, folded).Length == 8, "ancestor fold excludes six descendants");
        Check(PsdPaletteTree.Project(index.Notation, folded).Any(e => e.Node.NodeId == PsdFixture.HairPaint), "fold retains unrelated root children");
        folded.Remove(PsdFixture.Face);
        Check(PsdPaletteTree.Project(index.Notation, folded).Length == 12, "nested fold retained when ancestor expands");
        folded.Clear();
        Check(PsdPaletteTree.Project(index.Notation, folded).SequenceEqual(tree), "expand restores exact logical order");
        cases.Add("nested-fold");
        var basis = Edit(PsdAppearanceSettings.Create(asset), PsdFixture.Clothes, false);
        var baseJson = basis.Json;
        var force = basis.SetVisible(asset, index, PsdFixture.Base, false);
        Check(!force.Succeeded && force.Settings == basis, "hidden-source force-visible node cannot be unchecked");
        Check(!basis.SetVisible(asset, index, PsdFixture.Body, false).Succeeded, "force-visible group cannot be unchecked");
        cases.Add("force-visible");
        var eye = Edit(PsdAppearanceSettings.Create(asset), PsdFixture.EyeClosed, true);
        var mouth = Edit(PsdAppearanceSettings.Create(asset), PsdFixture.MouthSmile, true);
        var eyeJson = eye.Json; var mouthJson = mouth.Json;
        Check(eye.OwnedOrigins(asset, index).SetEquals([PsdFixture.EyeClosed]), "eye owns exactly one logical origin");
        Check(mouth.OwnedOrigins(asset, index).SetEquals([PsdFixture.MouthSmile]), "mouth owns exactly one logical origin");
        Check(basis.OwnedOrigins(asset, index).SetEquals([PsdFixture.Clothes]), "base owns only explicit clothes hide");
        var combined = Compose(basis, eye, mouth);
        Check(PsdPaletteViewModel.SelectedOrigins(combined.State!).Contains(PsdFixture.EyeClosed)
            && PsdPaletteViewModel.SelectedOrigins(combined.State!).Contains(PsdFixture.MouthSmile), "both current selections projected");
        var plan = RenderPlan.CreateNotationVisibility(document.Manifest, combined.State!);
        Check(plan.ActiveNodeIds.SequenceEqual(new[] { 0,1,3,4,5,7,10,12,15,16,17 }), "independent full active hierarchy oracle");
        Check(!eye.SetVisible(asset, index, PsdFixture.EyeClosed, false).Succeeded, "selected radio cannot be deselected");
        Check(basis.Json == baseJson && eye.Json == eyeJson && mouth.Json == mouthJson, "composition never mutates contributor envelopes");
        cases.Add("eye-mouth-independent");
        foreach (var flip in Enum.GetValues<PsdFlipState>())
        {
            var flipped = basis.WithFlip(asset, index, flip);
            Check(flipped.Succeeded, "base flip accepted: " + flip);
            var result = Compose(flipped.Settings!, eye, mouth);
            var active = RenderPlan.CreateNotationVisibility(document.Manifest, result.State!).ActiveNodeIds;
            var x = flip == PsdFlipState.X;
            Check(active.Contains(x ? PsdFixture.EyeClosedX : PsdFixture.EyeClosed)
                && !active.Contains(x ? PsdFixture.EyeClosed : PsdFixture.EyeClosedX), "correct eye counterpart: " + flip);
            Check(active.Contains(x ? PsdFixture.MouthSmileX : PsdFixture.MouthSmile)
                && !active.Contains(x ? PsdFixture.MouthSmile : PsdFixture.MouthSmileX), "correct mouth counterpart: " + flip);
            Check(!active.Contains(PsdFixture.EyeOpen) && !active.Contains(PsdFixture.MouthRest)
                && !active.Contains(PsdFixture.EyeOpenX) && !active.Contains(PsdFixture.MouthRestX), "exclusive peers excluded: " + flip);
            Check(new[] { PsdFixture.Body, PsdFixture.Base, PsdFixture.Accessory, PsdFixture.HairPaint, PsdFixture.HairLine }.All(active.Contains)
                && !active.Contains(PsdFixture.Clothes), "unrelated ordinary and forced parts retained: " + flip);
            Check(PsdPaletteTree.Project(index.Notation, new HashSet<int>()).SequenceEqual(tree), "orientation does not duplicate or reorder logical tree");
        }
        Check(basis.Json == baseJson && eye.Json == eyeJson && mouth.Json == mouthJson, "flip evaluation never rewrites sparse owners");
        cases.Add("flip-counterparts");
        var hidden = Edit(basis, PsdFixture.Face, false);
        var hiddenResult = Compose(hidden, eye, mouth);
        var hiddenPlan = RenderPlan.CreateNotationVisibility(document.Manifest, hiddenResult.State!);
        Check(hiddenResult.State!.IsLocallyVisible(PsdFixture.EyeClosed)
            && hiddenResult.State.IsLocallyVisible(PsdFixture.MouthSmile), "hidden parent retains child intent");
        Check(!hiddenPlan.IsActive(PsdFixture.EyeClosed) && !hiddenPlan.IsActive(PsdFixture.MouthSmile), "hidden parent suppresses effective descendants");
        Check(hiddenPlan.IsActive(PsdFixture.HairPaint) && hiddenPlan.IsActive(PsdFixture.Base), "hidden parent preserves other roots");
        var restored = hidden.Inherit(asset, index, PsdFixture.Face);
        Check(restored.Succeeded && Compose(restored.Settings!, eye, mouth).State!.EnabledNodeIds.SequenceEqual(combined.State!.EnabledNodeIds), "parent inheritance restores exact children");
        cases.Add("hidden-parent");
        var inherited = mouth.Inherit(asset, index, PsdFixture.MouthSmile);
        Check(inherited.Succeeded && inherited.Settings!.OwnedOrigins(asset, index).IsEmpty, "mouth Inherit removes mouth ownership");
        var inheritedResult = Compose(basis, eye, inherited.Settings!);
        Check(inheritedResult.State!.EnabledNodeIds.Contains(PsdFixture.EyeClosed)
            && inheritedResult.State.EnabledNodeIds.Contains(PsdFixture.MouthRest)
            && !inheritedResult.State.EnabledNodeIds.Contains(PsdFixture.MouthSmile), "mouth Inherit restores rest without touching eyes");
        Check(basis.Json == baseJson && eye.Json == eyeJson && mouth.Json == mouthJson, "Inherit retains original immutable envelopes");
        cases.Add("partial-inherit");
        var savedEye = JsonConvert.DeserializeObject<CompiledFaceParameter>(JsonConvert.SerializeObject(new CompiledFaceParameter { Appearance = eye }))!;
        var savedMouth = JsonConvert.DeserializeObject<CompiledFaceParameter>(JsonConvert.SerializeObject(new CompiledFaceParameter { Appearance = mouth }))!;
        Check(savedEye.Appearance!.Json == eyeJson && savedMouth.Appearance!.Json == mouthJson, "managed parameter JSON retains exact independent patches");
        Check(Compose(basis, savedEye.Appearance!, savedMouth.Appearance!).State!.EnabledNodeIds.SequenceEqual(combined.State!.EnabledNodeIds), "managed roundtrip resolves same hierarchy");
        Check(new PsdAppearanceStack(basis, [new(2, eye), new(5, mouth)]).Resolve(asset, index).State!.EnabledNodeIds.SequenceEqual(combined.State!.EnabledNodeIds), "callback enumeration order independent");
        Check(CompiledFormat.Hash(File.ReadAllBytes(source)) == sourceHash, "synthetic PSD unchanged");
        cases.Add("managed-roundtrip");
        var groupSource = Path.Combine(root, "synthetic-group-counterpart.psd");
        PsdFixture.Write(groupSource, layers: [
            new("face", Divider: 1, Blend: "pass"), new("eyes"), new("End", Divider: 3),
            new("face:flipx", Divider: 1, Blend: "pass"), new("eyes"),
            new("direction-only"), new("End", Divider: 3) ], layerIds: [21,22,23,24,25,26,27]);
        var groupCompiled = new PsdCompiler().Compile(groupSource, Path.Combine(root, "group-cache"));
        using var groupPool = new SharedDocumentPool(4096, 1);
        using var groupDocument = groupPool.Acquire(groupCompiled);
        var groupIndex = PsdLayerReferenceIndex.Read(groupDocument);
        Check(groupIndex.Capture(1).LayerId == 22 && groupIndex.Capture(3).LayerId == 25,
            "same-name counterpart children retain distinct raw PSD IDs");
        var groupTree = PsdPaletteTree.Project(groupIndex.Notation, new HashSet<int>());
        Check(groupTree.Select(e => e.Node.NodeId).SequenceEqual(new[] { 0,1,4 }),
            "flipped group and matched child merge into logical rows");
        Check(groupTree.Single(e => e.Node.NodeId == 4).ParentOrigin == 0
            && groupTree.Single(e => e.Node.NodeId == 4).Depth == 1,
            "direction-only child projects under its logical parent");
        Check(PsdPaletteTree.Project(groupIndex.Notation, new HashSet<int> { 0 }).Length == 1,
            "fold hides descendants from both group counterparts");
        cases.Add("flipped-group-hierarchy");
        Exception? templateError = null;
        var templateChecks = new List<(bool Passed, string Label)>();
        var templateThread = new Thread(() => {
            try
            {
                // No Application, Window, Show or fabricated TimelineToolInfo. XAML/binding smoke only.
                var view = new PsdPaletteView();
                var list = ((System.Windows.Controls.DockPanel)view.Content).Children
                    .OfType<System.Windows.Controls.ListBox>().Single();
                var grid = (System.Windows.Controls.Grid)list.ItemTemplate.LoadContent();
                var toggle = new PaletteCommand(() => { }, () => true);
                var expand = new PaletteCommand(() => { }, () => true);
                var inherit = new PaletteCommand(() => { }, () => false);
                var row = new PsdPaletteRow(5, "eyes", true, false, PsdSelectionMarker.Ordinary,
                    toggle, inherit, 1, 4, true, false, expand);
                grid.DataContext = row;
                grid.Measure(new System.Windows.Size(640, 100));
                grid.Arrange(new System.Windows.Rect(0, 0, 640, 100));
                grid.UpdateLayout();
                var buttons = grid.Children.OfType<System.Windows.Controls.Button>().ToArray();
                var checkbox = grid.Children.OfType<System.Windows.Controls.CheckBox>().Single();
                templateChecks.Add((grid.ColumnDefinitions.Count == 4, "actual row template loads four columns"));
                templateChecks.Add((buttons.Length == 2 && ReferenceEquals(buttons[0].Command, expand),
                    "actual fold button binds group command"));
                templateChecks.Add((buttons[0].Visibility == System.Windows.Visibility.Visible
                    && buttons[0].Content?.ToString() == "▸", "collapsed group disclosure binding"));
                templateChecks.Add((ReferenceEquals(checkbox.Command, toggle) && checkbox.IsChecked is true,
                    "actual row checkbox binds logical visibility command"));
                templateChecks.Add((ReferenceEquals(buttons[1].Command, inherit) && !buttons[1].IsEnabled,
                    "unowned row Inherit binding disabled"));
                templateChecks.Add((grid.Margin.Left == 14, "actual row template binds logical depth"));
            }
            catch (Exception ex) { templateError = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        templateThread.SetApartmentState(ApartmentState.STA);
        templateThread.Start();
        Check(templateThread.Join(TimeSpan.FromSeconds(10)), "bounded non-visible WPF template check");
        if (templateError is not null) throw new InvalidOperationException("Non-visible WPF template failed.", templateError);
        foreach (var check in templateChecks) Check(check.Passed, check.Label);
        cases.Add("non-visible-wpf-template");
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { total = cases.Count, assertions = checks.Count, failed = 0,
            cases, checks, sourceSha256 = sourceHash, physicalNodes = 18, logicalRows = 14, nativeHost = false,
            physicalInput = false, guiLaunched = false, nonVisibleWpfTemplate = true, previewPixelsVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PALETTE HIERARCHY SUMMARY: {cases.Count} cases; {checks.Count} assertions; 0 failures.");
    }
}
