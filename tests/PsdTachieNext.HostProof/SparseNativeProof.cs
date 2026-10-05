using System.Collections;
using System.Collections.Immutable;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;
using NativeJson = YukkuriMovieMaker.Json.Json;

namespace PsdTachieNext.HostProof;

// Test-only tool: obtains the actual public host history. No fabricated TimelineToolInfo.
public sealed class SparseNativeTool : IToolPlugin
{
    public string Name => "PSD sparse native validation";
    public Type ViewModelType => typeof(SparseNativeVm);
    public Type ViewType => typeof(SparseNativeView);
    public bool AllowMultipleInstances => false;
    public string DefaultGroupName => YukkuriMovieMaker.Resources.Localization.Texts.ToolGroupUtilityName;
}
public sealed class SparseNativeView : UserControl
{ public SparseNativeView() => Content = new TextBlock { Text = "Synthetic appearance persistence validation" }; }
public sealed class SparseNativeVm : ITimelineToolViewModel
{
    public void SetTimelineToolInfo(TimelineToolInfo info) => SparseNativeProof.Accept(info);
}

internal static class SparseNativeProof
{
    private static bool scheduled;
    private static TimelineToolInfo? info;
    private static int infoReceipts;
    private static readonly List<object> infoSignals = [];
    private static readonly object gate = new();
    private static readonly List<object> traffic = [];
    private static readonly Dictionary<CompiledTachieSource, Snapshot> latest = new(ReferenceEqualityComparer.Instance);
    private sealed record Snapshot(CompiledTachieSource Source, CompiledItemParameter Parameter, RequestStamp Stamp,
        string? Generation, int[] Active, PsdFlipState? Flip, long Pointer, long Compositions);
    internal static void Accept(TimelineToolInfo next)
    {
        info = next;
        var receipt = Interlocked.Increment(ref infoReceipts);
        lock (gate) if (infoSignals.Count < 64) infoSignals.Add(new { receipt, atUtc=DateTime.UtcNow,
            timelineId=next.Timeline?.ID, timelineName=next.Timeline?.Name, itemCount=next.Timeline?.Items.Count });
    }
    internal static bool Schedule()
    {
        var output = Environment.GetEnvironmentVariable("PSD_NEXT_SPARSE_NATIVE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return false;
        if (scheduled) return true; scheduled = true;
        Application.Current.Dispatcher.BeginInvoke(new Action(() => _ = Run(output)), DispatcherPriority.Normal);
        return true;
    }
    private static object? Public(object? value, string name) => value?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
    private static void Invoke(object value, string method, string argument)
        => value.GetType().GetMethod(method, [typeof(string)])!.Invoke(value, [argument]);
    private static TachieItem[] Items(object main) => (Public(Public(main, "ActiveTimelineViewModel"), "Items") as IEnumerable)?
        .Cast<object>().Select(vm => Public(vm, "Item")).OfType<TachieItem>()
        .Where(item => item.TachieItemParameter is CompiledItemParameter).ToArray() ?? [];
    private static async Task Until(Func<bool> condition, string reason, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException(reason); await Task.Delay(50); }
    }
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<object>(); var snapshots = new List<object>(); int assertions = 0;
        string status = "FAIL_OR_BLOCKED"; string? error = null; int historyEvents = 0; int historyCommands = 0;
        bool liveProjectOpened = false;
        void Log(string message) => File.AppendAllText(Path.Combine(output,"sparse-native-stages.log"),DateTime.UtcNow.ToString("O")+" "+message+Environment.NewLine);
        object? main = null; Window? window = null;
        SourceAssetRef? fixtureReference = null; Guid? fixtureTimelineId = null;
        var fixtureMarker = "SparseProof-"+Guid.NewGuid().ToString("N");
        var bindings = new List<object>();
        PropertyChangedEventHandler? mainBindingObserver = null;
        TachieItem[] FixtureItems() => fixtureReference is null || main is null ? [] : Items(main)
            .Where(item => item.Remark==fixtureMarker && ((CompiledItemParameter)item.TachieItemParameter).Source?.AssetIdentity==fixtureReference.AssetIdentity).ToArray();
        void CaptureBinding(string label)
        {
            if(bindings.Count>=64)return;
            try
            {
                var live=FixtureItems();var provided=info;
                bindings.Add(new {label,atUtc=DateTime.UtcNow,infoReceipts,expectedTimelineId=fixtureTimelineId,
                    providedTimelineId=provided?.Timeline?.ID,providedTimelineName=provided?.Timeline?.Name,
                    providedItemCount=provided?.Timeline?.Items.Count,
                    providedFixtureCount=provided?.Timeline?.Items.OfType<TachieItem>().Count(item=>item.Remark==fixtureMarker),
                    liveFixtureCount=live.Length,ownsLiveFixture=live.Length==1 && provided?.Timeline?.Items.Contains(live[0])==true,
                    projectPath=Public(Public(main,"ProjectFilePath"),"Value")?.ToString()});
            }
            catch(Exception ex){bindings.Add(new {label,error=ex.GetType().FullName});}
        }
        var observedWindows = new HashSet<string>();
        var windowObserver = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        windowObserver.Tick += (_,_) =>
        {
            foreach(Window observed in Application.Current.Windows)
            {
                var identity=observed.Title+" | "+observed.DataContext?.GetType().FullName+" | content="+observed.Content?.GetType().FullName;
                if(observedWindows.Add(identity))Log("observed-window "+identity);
            }
        };
        windowObserver.Start();
        var path = Path.Combine(output, "synthetic.psd"); var projectPath = Path.Combine(output, "seed.ymmp");
        var names = new[] { "*base", "*A:flipx", "*C:flipy", "*D:flipxy", "*B:flipx:flipy", "authored", "inherited" };
        var defaults = new[] { true, false, false, false, false, false, false };
        using var preparation = new SourcePreparationService(new CompiledAssetRepository(Path.Combine(output, "compiled-cache")));
        preparation.ManualHints = true;
        using var referencePool = new SharedDocumentPool(4096, 2);
        CompiledTachieSource.ProofPreparation = preparation;
        CompiledTachieSource.ProofHostUpdate = (source, description) =>
        {
            if (description.Tachie.ItemParameter is not CompiledItemParameter parameter) return;
            var plan = source.ProofDisplayedPlan;
            lock (gate)
            {
                var snapshot = new Snapshot(source, parameter, source.RefreshRequest, source.CurrentGeneration,
                    plan?.ActiveNodeIds.ToArray() ?? [], plan?.FlipState, source.Output.NativePointer.ToInt64(), source.CompositionCount);
                latest[source] = snapshot;
                traffic.Add(new { kind = "Update", frame = description.TimelinePosition.Frame, usage = description.Usage.ToString(),
                    snapshot.Stamp, snapshot.Generation, snapshot.Active, snapshot.Flip, snapshot.Pointer, snapshot.Compositions,
                    parameterJson = parameter.Appearance?.Json, parameterRevision = parameter.RefreshRevision });
            }
        };
        CompiledTachieSource.ProofLifecycle = (source, phase) =>
        { lock (gate) traffic.Add(new { kind = "Lifecycle", phase, stamp = source.RefreshRequest, generation = source.CurrentGeneration }); };
        EventHandler observer = (_, _) => historyEvents++;
        try
        {
            await Until(() =>
            {
                window = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.DataContext?.GetType().FullName == "YukkuriMovieMaker.ViewModels.MainViewModel");
                main = window?.DataContext; return main is not null;
            }, "Main view model not available");
            mainBindingObserver=(_,e)=>
            {if(e.PropertyName is "ActiveTimelineViewModel" or "ProjectFilePath")CaptureBinding("main-public-notification:"+e.PropertyName);};
            if(main is INotifyPropertyChanged notifyingMain)notifyingMain.PropertyChanged+=mainBindingObserver;
            Write("public-tool-menu-before-open.json",MenuObservation(main!));
            Log("await-native-startup-ready (read-only; no modal acceptance)");
            await Until(() => !Application.Current.Windows.Cast<Window>().Any(w =>
                    w.DataContext?.GetType().FullName == "YukkuriMovieMaker.ViewModels.AboutViewModel")
                && ValidationToolRegistered(main!),
                "Native startup incomplete: finish dedicated host startup with the supported input runtime before retrying", 60);
            Log("native-startup-ready");
            PsdFixture.Write(path, layerIds: [11,12,13,14,15,16,17], simpleLayerNames: names, simpleLayerVisible: defaults);
            var reference = SourceAssetRef.Create(path);
            fixtureReference=reference;
            var character = new Character { Name = "Sparse synthetic " + Guid.NewGuid().ToString("N"), TachieType = typeof(CompiledTachiePlugin),
                TachieCharacterParameter = new CompiledCharacterParameter(), TachieDefaultFaceParameter = new CompiledFaceParameter(),
                TachieDefaultItemParameter = new CompiledItemParameter { Source = reference } };
            var seed = new TachieItem(character) { Frame = 0, Length = 300, Layer = 0, Remark=fixtureMarker,
                TachieItemParameter = new CompiledItemParameter { Source = reference } };
            var timeline = new Timeline { Name = "Sparse synthetic", Items = ImmutableList.Create<IItem>(seed) };
            fixtureTimelineId=timeline.ID;
            timeline.VideoInfo.Width = 64; timeline.VideoInfo.Height = 32; timeline.VideoInfo.FPS = 30;
            timeline.RefreshTimelineLengthAndMaxLayer();
            var project = new Project([character], projectPath); project.Timelines.Clear(); project.Timelines.Add(timeline);
            NativeJson.Save(project, projectPath, null);
            CaptureBinding("before-open-project");
            Log("native-open-start");
            Invoke(main!, "OpenProject", projectPath);
            Log("native-open-returned");
            CaptureBinding("open-project-returned");
            await Until(()=>FixtureItems().Length==1 && string.Equals(Public(Public(main,"ProjectFilePath"),"Value")?.ToString(),projectPath,StringComparison.OrdinalIgnoreCase),
                "Expected synthetic project path/marker/source did not become live");
            liveProjectOpened = true;
            Log("live-item-open info="+(info is not null));
            CaptureBinding("expected-live-fixture-open");
            Write("public-tool-menu-observation.json",MenuObservation(main!));
            await Until(()=>OpenValidationTool(main!),"Synthetic tool menu registration did not become available");
            Log("public-test-tool-open-requested");
            CaptureBinding("public-test-tool-open-requested");
            await Until(() => FixtureItems().Length == 1 && info?.Timeline is not null && info.UndoRedoManager is not null,
                "Synthetic project or public TimelineToolInfo blocked (no modal accepted)");
            Log("public-tool-info-received");
            Log("await-public-tool-current-timeline");
            await Until(() => FixtureItems().Length == 1 && info?.Timeline?.Items.Contains(FixtureItems().Single()) == true,
                "Public TimelineToolInfo did not rebind to the opened live synthetic item");
            Log("public-tool-current-timeline-received");
            CaptureBinding("public-tool-current-timeline-received");
            var item = FixtureItems().Single();
            var manager = info!.UndoRedoManager;
            Check(info.Timeline!.Items.Contains(item), "actual tool timeline owns the live item");
            manager.HistoryChanged += observer;
            var originalSource = await Ready(item, [0], PsdFlipState.None, "baseline");
            var originalSession = originalSource.Stamp.SessionId; var originalPointer = originalSource.Pointer;
            using var asset = await preparation.PrepareAsync(reference, preparation.Revision(path));
            using var document = referencePool.Acquire(asset.Directory);
            var index = PsdLayerReferenceIndex.Read(document);
            async Task Edit(string label, Func<PsdAppearanceSettings, PsdAppearanceResolution> edit, int[] expected, PsdFlipState flip)
            {
                var before = (CompiledItemParameter)item.TachieItemParameter;
                var revised = edit(before.Appearance ?? PsdAppearanceSettings.Create(reference.AssetIdentity));
                Check(revised.Succeeded, label + " codec resolved");
                var revision = before.RefreshRevision;
                Check(AppearanceEditBridge.Commit(item, before, revision, revised, manager), label + " native commit");
                var after = await Ready(item, expected, flip, label);
                Check(after.Stamp.SessionId == originalSession && after.Pointer == originalPointer, label + " same Source/output lifetime");
                Check(!AppearanceEditBridge.Commit(item, before, revision, revised, manager), label + " stale edit rejected");
            }
            await Edit("ordinary-on", settings => settings.SetVisible(reference.AssetIdentity,index,5,true), [0,5], PsdFlipState.None);
            var ordinaryJson = ((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json;
            await History(CommandType.Undo, window!); await Ready(item,[0],PsdFlipState.None,"ordinary-undo");
            Check(((CompiledItemParameter)item.TachieItemParameter).Appearance is null, "one standard Undo returns source-only baseline");
            await History(CommandType.Redo, window!); await Ready(item,[0,5],PsdFlipState.None,"ordinary-redo");
            Check(((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json == ordinaryJson, "one standard Redo exact JSON");
            await Edit("flip-X", s=>s.WithFlip(reference.AssetIdentity,index,PsdFlipState.X), [0,5],PsdFlipState.X);
            await Edit("X-A", s=>s.SetVisible(reference.AssetIdentity,index,1,true), [1,5],PsdFlipState.X);
            await Edit("flip-Y", s=>s.WithFlip(reference.AssetIdentity,index,PsdFlipState.Y), [0,5],PsdFlipState.Y);
            await Edit("Y-common-B", s=>s.SetVisible(reference.AssetIdentity,index,4,true), [4,5],PsdFlipState.Y);
            await Edit("return-X-B-priority", s=>s.WithFlip(reference.AssetIdentity,index,PsdFlipState.X), [4,5],PsdFlipState.X);
            await Edit("X-A-Y-B-memory", s=>s.SetVisible(reference.AssetIdentity,index,1,true), [1,5],PsdFlipState.X);
            var splitJson = ((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json;
            await History(CommandType.Undo,window!); await Ready(item,[4,5],PsdFlipState.X,"mask-undo");
            await History(CommandType.Redo,window!); await Ready(item,[1,5],PsdFlipState.X,"mask-redo");
            Check(((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json == splitJson, "mask single Undo/Redo exact JSON");
            await Edit("Y-B-memory", s=>s.WithFlip(reference.AssetIdentity,index,PsdFlipState.Y), [4,5],PsdFlipState.Y);
            var savedJson = ((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json;
            var save = Path.Combine(output,"saved.ymmp"); Invoke(main!,"SaveProject",save);
            await Until(()=>File.Exists(save),"native save did not finish");
            var stored = JObject.Parse(File.ReadAllText(save))["Timelines"]![0]!["Items"]![0]!["TachieItemParameter"]!;
            Check(stored["Appearance"]!.Value<string>() == savedJson, "live native save exact appearance text");
            Check(JToken.DeepEquals(stored["Source"],JObject.FromObject(reference)), "live native save logical source");
            Invoke(main!,"OpenProject",save);
            await Until(()=>FixtureItems().Length==1 && !ReferenceEquals(FixtureItems().Single(),item),"native reopen did not replace the live item");
            CaptureBinding("native-reopened-fixture");
            var oldItem = item; item = FixtureItems().Single();
            var reopened = await Ready(item,[4,5],PsdFlipState.Y,"native-reopen");
            Check(reopened.Stamp.SessionId != originalSession, "reopen constructs a fresh Source lifetime");
            Check(((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json == savedJson, "native reopen exact saved intent");
            Check(!ReferenceEquals(oldItem,item), "native reopened item identity");
            manager.HistoryChanged -= observer; manager = info!.UndoRedoManager; manager.HistoryChanged += observer;
            defaults[5] = true; defaults[6] = true;
            PsdFixture.Write(path, layerIds:[11,12,13,14,15,16,17],simpleLayerNames:names,simpleLayerVisible:defaults);
            preparation.Invalidate(path);
            var changed = await Ready(item,[4,5,6],PsdFlipState.Y,"new-source-defaults", reopened.Generation);
            Check(((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json == savedJson,"new default does not author inherited part");
            PsdFixture.Write(path,simpleLayerNames:names,simpleLayerVisible:defaults);preparation.Invalidate(path);
            await Until(()=>changed.Source.PreparationDiagnostic?.Recovery==PreparationRecovery.RepairAppearance,"missing ID did not enter repair");
            Check(changed.Source.CurrentGeneration==changed.Generation, "ambiguous repair cannot publish a guessed generation");
            Check(((CompiledItemParameter)item.TachieItemParameter).Appearance!.Json == savedJson,"repair retains full saved intent");
            var repairSave = Path.Combine(output,"repair-preserved.ymmp");Invoke(main!,"SaveProject",repairSave);
            await Until(()=>File.Exists(repairSave),"repair save not written");
            Check(JObject.Parse(File.ReadAllText(repairSave))["Timelines"]![0]!["Items"]![0]!["TachieItemParameter"]!["Appearance"]!.Value<string>()==savedJson,
                "unresolved appearance survives live save");
            status = "PASS_NATIVE_SPARSE_SAVE_UNDO_REDO";

            async Task<Snapshot> Ready(TachieItem target,int[] expected,PsdFlipState flip,string label,string? excludedGeneration=null)
            {
                Snapshot? ready = null;
                await Until(()=>
                {
                    lock(gate) ready=latest.Values.LastOrDefault(s=>ReferenceEquals(s.Parameter,target.TachieItemParameter)
                        && s.Source.PreparationState==PreparationState.DisplayedCurrent && s.Active.SequenceEqual(expected)
                        && s.Flip==flip && (excludedGeneration is null || s.Generation!=excludedGeneration));
                    return ready is not null;
                },label+" did not reach expected actual paused Source output");
                snapshots.Add(new { label, ready!.Stamp, ready.Generation, ready.Active, ready.Flip, ready.Pointer, ready.Compositions,
                    json=ready.Parameter.Appearance?.Json, historyEvents, undoable=info?.UndoRedoManager.IsUndoable,redoable=info?.UndoRedoManager.IsRedoable });
                Log(label+" ready "+ready.Stamp);
                return ready!;
            }
            async Task History(CommandType commandType, Window target)
            {
                var command = CommandSettings.Default.GetCommand(commandType);
                Check(command is not null && command.CanExecute(null,target),"standard "+commandType+" CanExecute");
                command!.Execute(null,target); historyCommands++; await Task.Delay(100);
            }
        }
        catch(Exception ex) { CaptureBinding("failure-boundary");error=ex.ToString(); }
        finally
        {
            windowObserver.Stop();
            if(main is INotifyPropertyChanged notifyingMain && mainBindingObserver is not null)
                notifyingMain.PropertyChanged-=mainBindingObserver;
            if(info?.UndoRedoManager is { } manager) manager.HistoryChanged-=observer;
            CompiledTachieSource.ProofHostUpdate=null;CompiledTachieSource.ProofLifecycle=null;CompiledTachieSource.ProofPreparation=null;
            Write("sparse-native-results.json",new { status,assertions,error,checks,snapshots,historyEvents,traffic,bindings,infoSignals,
                actualHost=true,actualLiveProject=liveProjectOpened,actualToolManager=info?.UndoRedoManager is not null,
                standardUndoCommands=historyCommands>0,historyCommands,physicalInputVerified=false,
                sourceHead=Environment.GetEnvironmentVariable("SOURCE_HEAD"),
                productAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CompiledTachieSource).Assembly.Location))).ToLowerInvariant(),
                proofAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SparseNativeProof).Assembly.Location))).ToLowerInvariant(),
                hostAssembly=typeof(Project).Assembly.GetName().Version?.ToString(),
                openBoundaries=new[]{"native copy/split","preparation blocked-edit ABA","repair candidate UI","physical input and visual acceptance","H-A2"}});
            File.WriteAllText(Path.Combine(output,"sparse-native-complete.txt"),status);
        }
        void Check(bool condition,string label) { assertions++;checks.Add(new {label,passed=condition});if(!condition)throw new InvalidOperationException(label); }
        void Write(string name,object value)=>File.WriteAllText(Path.Combine(output,name),System.Text.Json.JsonSerializer.Serialize(value,new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    }

    // Fixed test-tool menu only; public surface used in Lab #133. No private model acquisition.
    private static bool ValidationToolRegistered(object main)
    {
        if(Public(main,"ToolMenuItems") is not IEnumerable roots)return false;
        return roots.Cast<object>().Any(entry=>Visit(entry,0));
        static bool Visit(object entry,int depth)
        {
            if(depth>8)return false;
            var label=(Public(entry,"Header")??Public(entry,"Title")??Public(entry,"Name"))?.ToString();
            if(label=="PSD sparse native validation")return true;
            return (Public(entry,"Children")??Public(entry,"Items")) is IEnumerable children
                && children.Cast<object>().Any(child=>Visit(child,depth+1));
        }
    }
    private static bool OpenValidationTool(object main)
    {
        var root=Public(main,"ToolMenuItems") as IEnumerable;
        if(root is null)return false;
        foreach(var entry in root.Cast<object>()) if(Visit(entry,0))return true;
        return false;
        static bool Visit(object entry,int depth)
        {
            if(depth>8)return false;
            var label=(Public(entry,"Header")??Public(entry,"Title")??Public(entry,"Name"))?.ToString();
            if(label=="PSD sparse native validation")
            {
                var command=entry as ICommand ?? Public(entry,"Command") as ICommand;
                var argument=Public(entry,"CommandParameter");
                if(command?.CanExecute(argument)==true){command.Execute(argument);return true;}
                // Native run 07 observed this exact public tool container and writable UI surface.
                // This opens only our test tool; history still arrives through SetTimelineToolInfo.
                var visible=entry.GetType().GetProperty("IsVisible",BindingFlags.Public|BindingFlags.Instance);
                if(entry.GetType().FullName=="YukkuriMovieMaker.ViewModels.ToolAreaViewModel" && visible?.SetMethod?.IsPublic==true)
                { visible.SetValue(entry,true);return true; }
                throw new InvalidOperationException("Synthetic public tool-open surface unavailable.");
            }
            var children=(Public(entry,"Children")??Public(entry,"Items")) as IEnumerable;
            if(children is not null)foreach(var child in children.Cast<object>())if(Visit(child,depth+1))return true;
            return false;
        }
    }
    private static object MenuObservation(object main)
    {
        var rows=new List<object>();
        var roots=Public(main,"ToolMenuItems") as IEnumerable;
        if(roots is not null)foreach(var entry in roots.Cast<object>())Visit(entry,0);
        var menuProperty=main.GetType().GetProperty("ToolMenuItems",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
        return new { rootType=roots?.GetType().FullName,menuPropertyDeclared=menuProperty is not null,
            menuGetterPublic=menuProperty?.GetMethod?.IsPublic,rows };
        void Visit(object entry,int depth)
        {
            if(depth>4||rows.Count>=100)return;
            var values=new List<object>();
            foreach(var property in entry.GetType().GetProperties(BindingFlags.Public|BindingFlags.Instance))
            {
                // Do not evaluate arbitrary getters or ViewModel while diagnosing a tool's activation order.
                if(property.Name is not ("DetailedTitle" or "Title" or "Header" or "Name" or "Id" or "IsActive" or "IsVisible" or "ViewModelType"))continue;
                if(property.GetIndexParameters().Length!=0)continue;
                try { var value=property.GetValue(entry);values.Add(new {property.Name,type=value?.GetType().FullName,text=value?.ToString()}); }
                catch(Exception ex) { values.Add(new {property.Name,error=ex.GetType().Name}); }
            }
            rows.Add(new { depth,type=entry.GetType().FullName,values });
            if((Public(entry,"Children")??Public(entry,"Items")) is IEnumerable children)
                foreach(var child in children.Cast<object>())Visit(child,depth+1);
        }
    }
}
