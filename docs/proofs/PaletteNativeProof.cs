using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Data;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.UndoRedo;
using NativeJson = YukkuriMovieMaker.Json.Json;

namespace PsdTachieNext.HostProof;

// Diagnostic-only public timeline tool. No fabricated TimelineToolInfo or private manager route.
public sealed class PaletteNativeTool : IToolPlugin
{
    public string Name => "PSD palette native checkpoint";
    public Type ViewModelType => typeof(PaletteNativeVm);
    public Type ViewType => typeof(PaletteNativeView);
    public bool AllowMultipleInstances => false;
    public string DefaultGroupName => YukkuriMovieMaker.Resources.Localization.Texts.ToolGroupUtilityName;
}
public sealed class PaletteNativeVm : ITimelineToolViewModel
{ public void SetTimelineToolInfo(TimelineToolInfo info) => PaletteNativeProof.Accept(info); }
public sealed class PaletteNativeView : System.Windows.Controls.UserControl
{ public PaletteNativeView() => Content = new System.Windows.Controls.TextBlock { Text = "Synthetic palette native checkpoint" }; }

internal static class PaletteNativeProof
{
    private static bool scheduled;
    private static TimelineToolInfo? info;
    private static int receipts;
    private static readonly object gate = new();
    private static readonly Dictionary<CompiledTachieSource, Snapshot> latest = new(ReferenceEqualityComparer.Instance);
    private static readonly List<object> traffic = [];
    private sealed record Face(int Layer, CompiledFaceParameter Parameter, long Revision, string? Json);
    private sealed record Snapshot(CompiledTachieSource Source, CompiledItemParameter Parameter, RequestStamp Stamp,
        string? Generation, int[] Active, long Pointer, Face[] Faces);
    internal static void Accept(TimelineToolInfo next) { info = next; receipts++; }
    internal static bool Schedule()
    {
        var output = Environment.GetEnvironmentVariable("PSD_NEXT_PALETTE_NATIVE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return false;
        if (scheduled) return true; scheduled = true;
        Application.Current.Dispatcher.BeginInvoke(new Action(() => _ = Run(output)), DispatcherPriority.Normal);
        return true;
    }
    private static object? Public(object? value, string name)
        => value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
    private static void ProjectCommand(object main, string method, string path)
        => main.GetType().GetMethod(method, [typeof(string)])!.Invoke(main, [path]);
    private static async Task Until(Func<bool> condition, string message, int seconds = 25)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(message); await Task.Delay(50); }
    }
    // Exact public ToolAreaViewModel route already observed in Lab #86/#133 and prior E native run.
    // These are public host API operations, not mouse/key/UIA input or a physical interaction proof.
    private static IEnumerable<object> Tools(object main)
        => (Public(main, "ToolMenuItems") as IEnumerable)?.Cast<object>().SelectMany(entry => Walk(entry, 0)) ?? [];
    private static IEnumerable<object> Walk(object entry, int depth)
    {
        if (depth > 8) yield break;
        yield return entry;
        if ((Public(entry, "Children") ?? Public(entry, "Items")) is IEnumerable children)
            foreach (var child in children.Cast<object>()) foreach (var nested in Walk(child, depth + 1)) yield return nested;
    }
    private static object? Tool(object main, string title)
        => Tools(main).FirstOrDefault(entry => (Public(entry, "Title") ?? Public(entry, "Header"))?.ToString() == title);
    private static bool OpenTool(object main, string title)
    {
        var entry = Tool(main, title); if (entry is null) return false;
        var command = entry as ICommand ?? Public(entry, "Command") as ICommand;
        var argument = Public(entry, "CommandParameter");
        if (command?.CanExecute(argument) == true) { command.Execute(argument); return true; }
        if (entry.GetType().FullName != "YukkuriMovieMaker.ViewModels.ToolAreaViewModel")
            throw new InvalidOperationException("Unexpected public tool container type.");
        var visible = entry.GetType().GetProperty("IsVisible", BindingFlags.Public | BindingFlags.Instance);
        if (visible?.SetMethod?.IsPublic != true) throw new InvalidOperationException("Public tool visibility route is unavailable.");
        visible.SetValue(entry, true);
        // Docking visibility alone initializes the VM without necessarily realizing its View.
        // Use only the exact container's public activation properties; no input simulation.
        foreach(var name in new[] { "IsSelected", "IsActive" })
        {
            var property=entry.GetType().GetProperty(name, BindingFlags.Public|BindingFlags.Instance);
            if(property?.SetMethod?.IsPublic!=true) throw new InvalidOperationException("Public tool activation route unavailable: "+name);
            property.SetValue(entry,true);
        }
        return true;
    }
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<object>(); var snapshots = new List<object>(); var paletteEvents = new List<object>();
        var historyTrace = new List<object>();
        var commandSubscriptions = new List<(IUndoRedoable Item, EventHandler<UndoRedoEventArgs> Handler)>();
        var assertions = 0; var historyCommands = 0; var historyEvents = 0;
        string status = "FAIL_OR_BLOCKED"; string? error = null;
        object? main = null; Window? window = null; PsdPaletteViewModel? palette = null;
        var liveProject = false; var actualProductTool = false;
        var marker = "PaletteNative-" + Guid.NewGuid().ToString("N");
        var publicState = new List<object>();
        DispatcherTimer? stateTimer=null;
        var path = Path.Combine(output, "synthetic-eyes-mouth.psd");
        var seedPath = Path.Combine(output, "seed.ymmp"); var savePath = Path.Combine(output, "palette-saved.ymmp");
        var ids = new[] { 11, 12, 13, 14 }; var names = new[] { "eyes", "mouth", "clothes", "hair" };
        SourceAssetRef? reference = null;
        using var preparation = new SourcePreparationService(new CompiledAssetRepository(Path.Combine(output, "compiled-cache")));
        preparation.ManualHints = true;
        using var referencePool = new SharedDocumentPool(4096, 2);
        CompiledTachieSource.ProofPreparation = preparation;
        CompiledTachieSource.ProofHostUpdate = (source, description) =>
        {
            if (description.Tachie.ItemParameter is not CompiledItemParameter parameter) return;
            var plan = source.ProofDisplayedPlan;
            var faces = description.Tachie.Faces.Where(f => f.FaceParameter is CompiledFaceParameter)
                .Select(f => { var p = (CompiledFaceParameter)f.FaceParameter; return new Face(f.Layer, p, p.RefreshRevision, p.Appearance?.Json); }).ToArray();
            var snap = new Snapshot(source, parameter, source.RefreshRequest, source.CurrentGeneration,
                plan?.ActiveNodeIds.ToArray() ?? [], source.Output.NativePointer.ToInt64(), faces);
            lock (gate)
            {
                latest[source] = snap;
                if (traffic.Count < 512) traffic.Add(new { kind = "Update", frame = description.TimelinePosition.Frame,
                    usage = description.Usage.ToString(), snap.Stamp, snap.Generation, snap.Active, snap.Pointer,
                    itemJson = parameter.Appearance?.Json, itemRevision = parameter.RefreshRevision,
                    faces = faces.Select(f => new { f.Layer, f.Revision, f.Json }) });
            }
        };
        CompiledTachieSource.ProofHostError = (_, description, ex) =>
        { lock (gate) traffic.Add(new { kind = "HostError", usage = description.Usage.ToString(), error = ex.ToString() }); };
        EventHandler historyObserver = (_, _) => { historyEvents++; Trace("manager-HistoryChanged"); };
        EventHandler recordedObserver = (_, _) => Trace("manager-Recorded");
        EventHandler undoObserver = (_, _) => Trace("manager-Undoed");
        EventHandler redoObserver = (_, _) => Trace("manager-Redoed");
        EventHandler managerCommandObserver = (_, _) => Trace("manager-UndoRedoCommandCreated");
        PropertyChangedEventHandler paletteObserver = (_, e) =>
        {
            if (paletteEvents.Count < 2048) paletteEvents.Add(new { atUtc = DateTime.UtcNow, e.PropertyName,
                palette?.Header, palette?.Status, palette?.CanEdit, rows = palette?.Rows.Count });
        };
        IItem[] Fixtures() => info?.Timeline.Items.Where(item => item.Remark.StartsWith(marker, StringComparison.Ordinal)).ToArray() ?? [];
        TachieItem Standing() => Fixtures().OfType<TachieItem>().Single();
        TachieFaceItem Eye() => Fixtures().OfType<TachieFaceItem>().Single(item => item.Remark == marker + "-eye");
        TachieFaceItem Mouth() => Fixtures().OfType<TachieFaceItem>().Single(item => item.Remark == marker + "-mouth");
        CompiledItemParameter StandingParameter() => (CompiledItemParameter)Standing().TachieItemParameter;
        CompiledFaceParameter FaceParameter(TachieFaceItem item) => (CompiledFaceParameter)item.TachieFaceParameter;
        void Trace(string label, string? commandType = null)
        {
            if (historyTrace.Count >= 512) return;
            var items = Fixtures();
            var row = new { label, commandType, atUtc = DateTime.UtcNow, historyEvents, historyCommands,
                undoable = info?.UndoRedoManager.IsUndoable, redoable = info?.UndoRedoManager.IsRedoable,
                bridgeReplacements = HostPreparationBridge.ReplacementCount,
                items = items.Select(item => new { item.Remark, item.Layer,
                    json = item is TachieItem ti ? (ti.TachieItemParameter as CompiledItemParameter)?.Appearance?.Json
                        : item is TachieFaceItem fi ? (fi.TachieFaceParameter as CompiledFaceParameter)?.Appearance?.Json : null,
                    parameterIdentity = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(item is TachieItem t ? t.TachieItemParameter : ((TachieFaceItem)item).TachieFaceParameter) }).ToArray() };
            historyTrace.Add(row);
            Write("history-boundaries.json", historyTrace);
        }
        void RecordState(string label,bool scanViews=false)
        {
            if(label=="heartbeat" && publicState.Count>=128) return;
            var container=main is null?null:Tool(main,new PsdPalettePlugin().Name);
            var roots=Application.Current.Windows.Cast<Window>().ToArray();
            var views=scanViews?roots.Select(root=>new { rootType=root.GetType().FullName,
                isMain=ReferenceEquals(root,window), view=FindPaletteView(root) }).Where(row=>row.view is not null)
                .Select(row=>new { row.rootType,row.isMain,row.view!.IsLoaded,vmType=row.view.DataContext?.GetType().FullName,
                    sameCurrentVm=ReferenceEquals(row.view.DataContext,palette) }).ToArray():null;
            publicState.Add(new {label,atUtc=DateTime.UtcNow,receipts,
                scopeIdentity=info is null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info.Timeline),
                managerIdentity=info is null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(info.UndoRedoManager),
                infoBusy=info?.AsyncAwaitStatus.IsBusy,mainBusy=Public(Public(main,"AsyncAwaitStatus"),"IsBusy"),
                mainIsSaved=Public(main,"IsSaved"),mainTitle=Public(Public(main,"Title"),"Value"),
                selected=info?.Timeline.SelectedItems.Select(item=>item.Remark).ToArray(),
                productVmIdentity=palette is null?0:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(palette),
                product=palette?.ProofLifetime,palette?.CanEdit,palette?.Status,palette?.Header,
                toolVisible=Public(container,"IsVisible"),toolSelected=Public(container,"IsSelected"),toolActive=Public(container,"IsActive"),
                hostOwners=HostPreparationBridge.Diagnostics,hostOwnerError=HostPreparationBridge.LastError?.ToString(),
                roots=roots.Select(root=>new {type=root.GetType().FullName,root.Title,root.IsEnabled,root.IsLoaded,root.IsVisible,
                    dataContextType=root.DataContext?.GetType().FullName}).ToArray(),views});
            Write("public-state.json",publicState);
        }
        try
        {
            await Until(() =>
            {
                window = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.DataContext?.GetType().FullName == "YukkuriMovieMaker.ViewModels.MainViewModel");
                main = window?.DataContext; return main is not null;
            }, "Public main view-model not available");
            Log("await startup readiness; no modal/input accepted");
            stateTimer=new DispatcherTimer(DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(500)};
            stateTimer.Tick+=(_,_)=>{try{RecordState("heartbeat");}catch(Exception ex){Log("state observation failed: "+ex.Message);}};
            stateTimer.Start();
            await Until(() => !Application.Current.Windows.Cast<Window>().Any(w => w.DataContext?.GetType().FullName == "YukkuriMovieMaker.ViewModels.AboutViewModel")
                && Tool(main!, "PSD palette native checkpoint") is not null && Tool(main!, "PSD立ち絵パレット") is not null,
                "Startup/modal/product tool registration blocked", 45);
            PsdFixture.Write(path, layerIds: ids, simpleLayerNames: names, simpleLayerVisible: [false, false, true, true]);
            var sourceHash = Hash(path);
            reference = SourceAssetRef.Create(path);
            using var asset = await preparation.PrepareAsync(reference, preparation.Revision(path));
            using var document = referencePool.Acquire(asset.Directory);
            var index = PsdLayerReferenceIndex.Read(document);
            var baseline = PsdAppearanceSettings.Create(reference.AssetIdentity).SetVisible(reference.AssetIdentity, index, 2, false);
            Check(baseline.Succeeded, "synthetic base clothes explicitly off");
            var character = new Character { Name = "Palette synthetic " + Guid.NewGuid().ToString("N"), TachieType = typeof(CompiledTachiePlugin),
                TachieCharacterParameter = new CompiledCharacterParameter(), TachieDefaultFaceParameter = new CompiledFaceParameter(),
                TachieDefaultItemParameter = new CompiledItemParameter { Source = reference } };
            var standing = new TachieItem(character) { Frame = 0, Length = 300, Layer = 0, Remark = marker + "-standing",
                TachieItemParameter = new CompiledItemParameter { Source = reference, Appearance = baseline.Settings } };
            var eye = new TachieFaceItem(character) { Frame = 0, Length = 300, Layer = 2, Remark = marker + "-eye", TachieFaceParameter = new CompiledFaceParameter() };
            var mouth = new TachieFaceItem(character) { Frame = 0, Length = 300, Layer = 5, Remark = marker + "-mouth", TachieFaceParameter = new CompiledFaceParameter() };
            var timeline = new Timeline { Name = "Synthetic palette native", Items = ImmutableList.Create<IItem>(standing, eye, mouth) };
            timeline.VideoInfo.Width = 64; timeline.VideoInfo.Height = 32; timeline.VideoInfo.FPS = 30;
            timeline.RefreshTimelineLengthAndMaxLayer();
            var project = new Project([character], seedPath); project.Timelines.Clear(); project.Timelines.Add(timeline);
            NativeJson.Save(project, seedPath, null);
            Check(OpenTool(main!, "PSD palette native checkpoint"), "diagnostic public tool opened by API");
            ProjectCommand(main!, "OpenProject", seedPath);
            await Until(() => Fixtures().Length == 3 && StandingParameter().Source?.AssetIdentity == reference.AssetIdentity,
                "Actual public tool Timeline did not rebind to the live synthetic project");
            liveProject = true;
            Check(info!.Timeline.Items.Contains(Standing()) && info.Timeline.Items.Contains(Eye()) && info.Timeline.Items.Contains(Mouth()), "actual public timeline owns all three live items");
            Check(OpenTool(main!, "PSD立ち絵パレット"), "product public tool opened by API");
            await Until(() =>
            {
                palette = Public(Tool(main!, "PSD立ち絵パレット"), "ViewModel") as PsdPaletteViewModel;
                return palette is not null;
            }, "Host-created product palette ViewModel unavailable");
            actualProductTool = true; palette!.PropertyChanged += paletteObserver; info.UndoRedoManager.HistoryChanged += historyObserver;
            info.UndoRedoManager.Recorded += recordedObserver; info.UndoRedoManager.Undoed += undoObserver;
            info.UndoRedoManager.Redoed += redoObserver; info.UndoRedoManager.UndoRedoCommandCreated += managerCommandObserver;
            foreach (var item in Fixtures())
            {
                var name = item.Remark;
                EventHandler<UndoRedoEventArgs> observer = (_, e) => Trace("item-command:" + name, e.Command.GetType().FullName);
                item.UndoRedoCommandCreated += observer; commandSubscriptions.Add((item, observer));
            }
            await Select(Standing(), "standing target");
            Check(palette.Header.Contains("立ち絵", StringComparison.Ordinal) && palette.Header.Contains("Layer 0", StringComparison.Ordinal), "standing target header");
            var original = await Ready([3], "native base");
            var originalSession = original.Stamp.SessionId; var originalPointer = original.Pointer;
            var baselineJson = StandingParameter().Appearance!.Json;
            Trace("baseline-native-open-no-post-load-initialization-setters");
            var standingRow = palette.Rows.Single(row => row.Origin == 3);
            Execute(standingRow.ToggleCommand, "standing hair off product row command");
            await Ready([], "standing hair off");
            Check(StandingParameter().Appearance!.OwnedOrigins(reference.AssetIdentity, index).SetEquals([2, 3]), "standing sparse owner exactly clothes/hair");
            await History(CommandType.Undo); await Ready([3], "standing standard Undo");
            Check(StandingParameter().Appearance!.Json == baselineJson, "standing Undo exact original envelope");
            await History(CommandType.Redo); await Ready([], "standing standard Redo");
            await History(CommandType.Undo); await Ready([3], "standing restored for partial faces");
            Check(StandingParameter().Appearance!.Json == baselineJson, "base exact before partial expressions");

            await Select(Eye(), "eye target");
            Check(palette.Header.Contains("表情", StringComparison.Ordinal) && palette.Header.Contains("Layer 2", StringComparison.Ordinal), "eye target header");
            Execute(palette.Rows.Single(row => row.Origin == 0).ToggleCommand, "eye-only product row command");
            await Ready([0, 3], "eye-only native output", requireEye: true);
            var eyeJson = FaceParameter(Eye()).Appearance!.Json;
            Check(FaceParameter(Eye()).Appearance!.OwnedOrigins(reference.AssetIdentity, index).SetEquals([0]), "eye patch owns only eyes");
            Check(StandingParameter().Appearance!.Json == baselineJson && FaceParameter(Mouth()).Appearance is null, "eye edit cannot author base or mouth");
            await History(CommandType.Undo); await Ready([3], "eye standard Undo");
            Check(FaceParameter(Eye()).Appearance is null, "eye single Undo returns old null parameter default");
            await History(CommandType.Redo); await Ready([0, 3], "eye standard Redo", requireEye: true);
            Check(FaceParameter(Eye()).Appearance!.Json == eyeJson, "eye Redo exact envelope");

            var staleEyeCommand = palette.Rows.Single(row => row.Origin == 0).ToggleCommand;
            await Select(Mouth(), "mouth target");
            Check(!staleEyeCommand.CanExecute(null), "row command from previous selected target is disabled");
            staleEyeCommand.Execute(null);
            Check(FaceParameter(Eye()).Appearance!.Json == eyeJson && FaceParameter(Mouth()).Appearance is null, "stale command cannot write old or new target");
            Execute(palette.Rows.Single(row => row.Origin == 1).ToggleCommand, "mouth-only product row command");
            var combined = await Ready([0, 1, 3], "eye plus mouth native output", requireEye: true, requireMouth: true);
            var mouthJson = FaceParameter(Mouth()).Appearance!.Json;
            Check(FaceParameter(Mouth()).Appearance!.OwnedOrigins(reference.AssetIdentity, index).SetEquals([1]), "mouth patch owns only mouth");
            Check(FaceParameter(Eye()).Appearance!.Json == eyeJson && StandingParameter().Appearance!.Json == baselineJson, "mouth edit preserves eye/base JSON exactly");
            Check(combined.Stamp.SessionId == originalSession && combined.Pointer == originalPointer, "native face edits retain same Source/output lifetime");
            Check(combined.Faces.Any(f => f.Layer == 2 && f.Json == eyeJson) && combined.Faces.Any(f => f.Layer == 5 && f.Json == mouthJson), "actual host Faces input carries both authored patches with correct layer numbers");
            Execute(palette.Rows.Single(row => row.Origin == 1).InheritCommand, "mouth Inherit product row command");
            await Ready([0, 3], "mouth inherited without altering eye", requireEye: true);
            Check(FaceParameter(Mouth()).Appearance!.OwnedOrigins(reference.AssetIdentity, index).IsEmpty, "Inherit removes only mouth ownership");
            await History(CommandType.Undo); await Ready([0, 1, 3], "mouth Inherit Undo", requireEye: true, requireMouth: true);
            Check(FaceParameter(Mouth()).Appearance!.Json == mouthJson, "Inherit Undo exact mouth patch");
            await History(CommandType.Redo); await Ready([0, 3], "mouth Inherit Redo", requireEye: true);
            await History(CommandType.Undo); await Ready([0, 1, 3], "save combined partial expressions", requireEye: true, requireMouth: true);
            Check(FaceParameter(Eye()).Appearance!.Json == eyeJson && StandingParameter().Appearance!.Json == baselineJson, "Inherit/history does not rewrite unrelated contributors");

            ProjectCommand(main!, "SaveProject", savePath);
            await Until(() => File.Exists(savePath) && !info.AsyncAwaitStatus.IsBusy, "Native SaveProject did not finish");
            var stored = JObject.Parse(File.ReadAllText(savePath))["Timelines"]![0]!["Items"]!.Children().ToArray();
            JToken Stored(string suffix) => stored.Single(item => item["Remark"]!.Value<string>() == marker + suffix);
            Check(Stored("-standing")["TachieItemParameter"]!["Appearance"]!.Value<string>() == baselineJson, "native save exact base JSON");
            Check(Stored("-eye")["TachieFaceParameter"]!["Appearance"]!.Value<string>() == eyeJson, "native save exact eye-only JSON");
            Check(Stored("-mouth")["TachieFaceParameter"]!["Appearance"]!.Value<string>() == mouthJson, "native save exact mouth-only JSON");
            Check(JToken.DeepEquals(Stored("-standing")["TachieItemParameter"]!["Source"], JObject.FromObject(reference)), "native save logical source only");
            var oldStanding = Standing(); var oldEye = Eye(); var oldMouth = Mouth();
            RecordState("before-native-OpenProject", scanViews:true);
            Log("invoke native OpenProject saved fixture");
            ProjectCommand(main!, "OpenProject", savePath);
            RecordState("native-OpenProject-returned", scanViews:true);
            await Until(() => Fixtures().Length == 3 && !ReferenceEquals(Standing(), oldStanding)
                && !info!.AsyncAwaitStatus.IsBusy, "Native reopen did not complete new live project scope");
            Check(!ReferenceEquals(Eye(), oldEye) && !ReferenceEquals(Mouth(), oldMouth), "native reopen new face item identities");
            var reopened = await Ready([0, 1, 3], "reopened partial face composition", requireEye:true, requireMouth:true);
            Check(reopened.Stamp.SessionId != originalSession, "native reopen new Source lifetime");
            Check(StandingParameter().Appearance!.Json == baselineJson && FaceParameter(Eye()).Appearance!.Json == eyeJson
                && FaceParameter(Mouth()).Appearance!.Json == mouthJson, "native reopen exact three independent envelopes");
            var reopenedProduct = Public(Tool(main!, new PsdPalettePlugin().Name), "ViewModel") as PsdPaletteViewModel;
            Check(reopenedProduct is not null, "actual host product ViewModel available after reopen");
            Write("reopen-product-state.json", new { sameViewModel=ReferenceEquals(reopenedProduct,palette),
                cached=palette!.ProofLifetime, palette.CanEdit, palette.Status,
                current=reopenedProduct!.ProofLifetime, currentCanEdit=reopenedProduct.CanEdit, currentStatus=reopenedProduct.Status,
                diagnosticScopeBusy=info!.AsyncAwaitStatus.IsBusy, selected=info.Timeline.SelectedItems.Count });
            if(!ReferenceEquals(reopenedProduct,palette))
            {
                palette.PropertyChanged-=paletteObserver;
                palette=reopenedProduct;
                palette.PropertyChanged+=paletteObserver;
            }
            RecordState("current-product-VM-resolved",scanViews:true);
            info.UndoRedoManager.HistoryChanged+=historyObserver;
            foreach(var item in Fixtures())
            {
                if(item is not IUndoRedoable undoable) continue;
                EventHandler<UndoRedoEventArgs> handler=(_,e)=>Trace("reopened-item-command:"+item.Remark,e.Command.GetType().FullName);
                undoable.UndoRedoCommandCreated+=handler;commandSubscriptions.Add((undoable,handler));
            }
            await Select(Eye(), "reopened eye target");
            Check(palette.Rows.Single(row=>row.Origin==0).Owned, "reopened palette eye ownership");
            Execute(palette.Rows.Single(row=>row.Origin==0).ToggleCommand, "reopened eye OFF product command");
            await Ready([1,3], "reopened eye OFF output", requireEye:true, requireMouth:true);
            Check(StandingParameter().Appearance!.Json==baselineJson && FaceParameter(Mouth()).Appearance!.Json==mouthJson,
                "reopened eye edit preserves saved standing and mouth exactly");
            await History(CommandType.Undo); await Ready([0,1,3], "reopened eye single Undo", requireEye:true, requireMouth:true);
            Check(FaceParameter(Eye()).Appearance!.Json==eyeJson && FaceParameter(Mouth()).Appearance!.Json==mouthJson
                && StandingParameter().Appearance!.Json==baselineJson, "reopened eye Undo restores exact three saved envelopes");
            await Select(Mouth(), "reopened mouth target");
            Check(palette.Rows.Single(row=>row.Origin==1).Owned && !palette.Rows.Single(row=>row.Origin==0).Owned,
                "reopened palette mouth-only ownership");
            Execute(palette.Rows.Single(row=>row.Origin==1).ToggleCommand, "reopened mouth OFF product command");
            await Ready([0,3], "reopened mouth OFF output", requireEye:true, requireMouth:true);
            Check(StandingParameter().Appearance!.Json==baselineJson && FaceParameter(Eye()).Appearance!.Json==eyeJson,
                "reopened mouth edit preserves saved standing and eye exactly");
            await History(CommandType.Undo); await Ready([0,1,3], "reopened mouth single Undo", requireEye:true, requireMouth:true);
            Check(FaceParameter(Eye()).Appearance!.Json==eyeJson && FaceParameter(Mouth()).Appearance!.Json==mouthJson
                && StandingParameter().Appearance!.Json==baselineJson, "reopened mouth Undo restores exact three saved envelopes");
            Check(Hash(path)==sourceHash, "synthetic source unchanged after reopen edits");
            RecordState("reopened-edit-Undo-PASS",scanViews:true);
            var finalSave=Path.Combine(output,"reopened-edits-undone.ymmp");
            ProjectCommand(main!,"SaveProject",finalSave);
            await Until(()=>File.Exists(finalSave) && !info.AsyncAwaitStatus.IsBusy,"Final native save did not finish");
            var finalStored=JObject.Parse(File.ReadAllText(finalSave))["Timelines"]![0]!["Items"]!.Children().ToArray();
            Check(finalStored.Single(item=>item["Remark"]!.Value<string>()==marker+"-eye")["TachieFaceParameter"]!["Appearance"]!.Value<string>()==eyeJson
                && finalStored.Single(item=>item["Remark"]!.Value<string>()==marker+"-mouth")["TachieFaceParameter"]!["Appearance"]!.Value<string>()==mouthJson,
                "final native save retains eye/mouth exact restored envelopes");
            Check(Public(main,"IsSaved") is true, "native clean saved state before pending-edit review");
            var originalRemark=Standing().Remark;
            Standing().Remark=originalRemark+"-pending-user-note"; // One unrelated, unrecorded public user edit.
            var pendingRemark=Standing().Remark;
            var pendingBefore=new{isSaved=Public(main,"IsSaved"),info.UndoRedoManager.IsUndoable,info.UndoRedoManager.IsRedoable};
            var historyBeforeHint=historyEvents;
            StandingParameter().NotifyPreparationReady(); // Exact product empty notification; no Record.
            var pendingAfter=new{isSaved=Public(main,"IsSaved"),info.UndoRedoManager.IsUndoable,info.UndoRedoManager.IsRedoable};
            Write("pending-native-dirty-review.json",new{pendingBefore,pendingAfter,pendingRemark,
                historyBeforeHint,historyAfterHint=historyEvents,remarkPreserved=Standing().Remark==pendingRemark,
                standingJsonPreserved=StandingParameter().Appearance!.Json==baselineJson});
            Check(Equals(pendingBefore.isSaved,pendingAfter.isSaved) && pendingBefore.IsUndoable==pendingAfter.IsUndoable
                && pendingBefore.IsRedoable==pendingAfter.IsRedoable && historyEvents==historyBeforeHint,
                "empty hint preserves native public IsSaved and Undo/Redo flags while unrelated edit is pending");
            Check(Standing().Remark==pendingRemark && StandingParameter().Appearance!.Json==baselineJson,
                "empty hint retains unrecorded unrelated user edit and authored settings");
            info.UndoRedoManager.Record(); // Record the user's single edit once; never clear/rollback/retry.
            await History(CommandType.Undo);
            await Ready([0,1,3],"pending unrelated edit single Undo",requireEye:true,requireMouth:true);
            Check(Standing().Remark==originalRemark && FaceParameter(Eye()).Appearance!.Json==eyeJson
                && FaceParameter(Mouth()).Appearance!.Json==mouthJson && StandingParameter().Appearance!.Json==baselineJson,
                "pending user edit survives hint and records as exact one-Undo unit");
            var reviewedSave=Path.Combine(output,"pending-review-undone.ymmp");
            ProjectCommand(main!,"SaveProject",reviewedSave);
            await Until(()=>File.Exists(reviewedSave) && !info.AsyncAwaitStatus.IsBusy,"Pending review final save did not finish");
            RecordState("native-pending-dirty-review-PASS",scanViews:true);
            status="PASS_NATIVE_REOPEN_PRODUCT_EYE_MOUTH_EDIT_UNDO";
            Log(status);

            async Task Select(IItem target, string label)
            {
                info!.Timeline.SelectItem(target);
                await Until(() => palette.CanEdit && palette.Rows.Count == 4 && info.Timeline.SelectedItems.Count == 1
                    && ReferenceEquals(info.Timeline.SelectedItems[0], target), label + " did not prepare actual selected target; state=" + palette.ProofLifetime + "; status=" + palette.Status);
                Check(palette.CanEdit, label + " ready");
                Log(label + " ready " + palette.Header.Replace('\n', ' '));
            }
            async Task<Snapshot> Ready(int[] expected, string label, bool requireEye = false, bool requireMouth = false)
            {
                Snapshot? ready = null;
                await Until(() =>
                {
                    lock (gate) ready = latest.Values.LastOrDefault(s => ReferenceEquals(s.Parameter, Standing().TachieItemParameter)
                        && s.Source.PreparationState == PreparationState.DisplayedCurrent && s.Active.SequenceEqual(expected)
                        && (!requireEye || s.Faces.Any(f => ReferenceEquals(f.Parameter, Eye().TachieFaceParameter)))
                        && (!requireMouth || s.Faces.Any(f => ReferenceEquals(f.Parameter, Mouth().TachieFaceParameter))));
                    return ready is not null;
                }, label + " did not reach actual current paused Source");
                Check(ready!.Active.Contains(3) == expected.Contains(3) && !ready.Active.Contains(2), label + " unrelated hair/clothes preserved");
                snapshots.Add(new { label, ready.Stamp, ready.Generation, ready.Active, ready.Pointer,
                    itemJson = ready.Parameter.Appearance?.Json, faces = ready.Faces.Select(f => new { f.Layer, f.Revision, f.Json }),
                    historyEvents, historyCommands });
                Log(label + " ready " + ready.Stamp); return ready;
            }
            async Task History(CommandType type)
            {
                var command = CommandSettings.Default.GetCommand(type);
                Check(command is not null && command.CanExecute(null, window!), "standard " + type + " CanExecute");
                command!.Execute(null, window!); historyCommands++; await Task.Delay(100);
                Trace("after-standard-" + type);
            }
            void Execute(ICommand command, string label)
            { Check(command.CanExecute(null), label + " CanExecute"); Trace("before-product-command:" + label); command.Execute(null); Trace("after-product-command:" + label); Log(label + " executed"); }
        }
        catch (Exception ex) { error = ex.ToString(); Log("FAIL " + ex.Message); }
        finally
        {
            stateTimer?.Stop();
            try{RecordState("final",scanViews:true);}catch(Exception ex){Log("final state observation failed: "+ex.Message);}
            if (palette is not null) palette.PropertyChanged -= paletteObserver;
            if (info?.UndoRedoManager is { } manager) manager.HistoryChanged -= historyObserver;
            if (info?.UndoRedoManager is { } traced)
            { traced.Recorded -= recordedObserver; traced.Undoed -= undoObserver; traced.Redoed -= redoObserver; traced.UndoRedoCommandCreated -= managerCommandObserver; }
            foreach (var (item, handler) in commandSubscriptions) item.UndoRedoCommandCreated -= handler;
            CompiledTachieSource.ProofHostUpdate = null; CompiledTachieSource.ProofHostError = null; CompiledTachieSource.ProofPreparation = null;
            Write("palette-native-results.json", new { status, assertions, error, checks, snapshots, paletteEvents, traffic, historyTrace,
                actualHost = true, actualLiveProject = liveProject, actualHostCreatedProductTool = actualProductTool, publicState,
                actualTimelineToolInfoReceipts = receipts, actualToolManager = info?.UndoRedoManager is not null,
                historyCommands, historyEvents, physicalClickKeyboardVerified = false, visualAcceptance = false,
                sourceHead = Environment.GetEnvironmentVariable("SOURCE_HEAD"),
                productAssemblySha256 = Hash(typeof(PsdPaletteViewModel).Assembly.Location), driverAssemblySha256 = Hash(typeof(PaletteNativeProof).Assembly.Location),
                notProven = new[] { "physical input/visual acceptance", "full/frame-varying video output", "H-A2", "host pixel screenshot correspondence" } });
            File.WriteAllText(Path.Combine(output, "palette-native-complete.txt"), status);
        }
        void Log(string line) => File.AppendAllText(Path.Combine(output, "palette-native-stages.log"), DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine);
        void Check(bool passed, string label) { assertions++; checks.Add(new { label, passed }); if (!passed) throw new InvalidOperationException(label); }
        void Write(string name, object value) => File.WriteAllText(Path.Combine(output, name), System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static PsdPaletteView? FindPaletteView(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root); var visited = 0;
        while (pending.TryPop(out var node) && visited++ < 10000)
        {
            if (node is PsdPaletteView view) return view;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
        return null;
    }
}

