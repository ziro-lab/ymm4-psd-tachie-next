using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.ViewModels.CommandParameter;

namespace PsdTachieNext.HostProof;

/// <summary>Public character-row selection and native AddTachieItem command in a real paused host.
/// Observes host Update and actual preview pixels; no direct product Update/draw/Seek calls.</summary>
internal static class SelectedPrefetchProof
{
    private static bool scheduled;
    private static readonly object gate=new();
    private sealed record Update(CompiledTachieSource Source,CompiledItemParameter Parameter,long Frame,string Usage,
        string? Generation,long Pointer,long Compositions);
    private sealed class BlockedStage
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Entries,Cancellations;
    }
    internal static bool Schedule(string output)
    {
        if(scheduled)return true;scheduled=true;
        Application.Current.Dispatcher.BeginInvoke(new Action(()=>_=Run(output)),DispatcherPriority.Normal);return true;
    }
    private static object? Public(object? value,string name)=>value?.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.Instance)?.GetValue(value);
    private static void SetValue(object property,object value)=>property.GetType().GetProperty("Value")!.SetValue(property,value);
    private static TachieItem[] Items(object active)=>(Public(active,"Items") as IEnumerable)?.Cast<object>()
        .Select(vm=>Public(vm,"Item")).OfType<TachieItem>().ToArray()??[];
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);var updates=new List<Update>();var phases=new List<object>();
        PsdFixture.Write(Path.Combine(output,"synthetic-b.psd"),seed:77);
        PsdFixture.Write(Path.Combine(output,"synthetic-unused.psd"),seed:99);
        void Log(string message){lock(gate)File.AppendAllText(Path.Combine(output,"selected-stages.log"),DateTime.UtcNow.ToString("O")+" "+message+Environment.NewLine);}
        void Write(string name,object value)=>File.WriteAllText(Path.Combine(output,name),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
        var assertions=0;var status="FAIL";string? error=null;BlockedStage? blocked=null;
        void Check(bool condition,string reason){assertions++;if(!condition)throw new InvalidOperationException(reason);}
        using var preparation=new SourcePreparationService(new CompiledAssetRepository(Path.Combine(output,"compiled-cache")));
        using var prefetch=CompiledTachieSource.CreateSelectedPrefetch(preparation);
        preparation.BeforeSnapshot=async token=>
        {
            var stage=Volatile.Read(ref blocked);if(stage is null)return;
            Interlocked.Increment(ref stage.Entries);stage.Entered.TrySetResult();Log("snapshot-blocked");
            try{await stage.Release.Task.WaitAsync(token);}
            catch(OperationCanceledException){Interlocked.Increment(ref stage.Cancellations);Log("speculative-waiter-cancelled");throw;}
        };
        CompiledTachieSource.ProofPreparation=preparation;CompiledTachieSource.ProofPrefetch=prefetch;
        CompiledTachieSource.ProofHostUpdate=(source,description)=>
        {
            if(description.Tachie.ItemParameter is not CompiledItemParameter parameter)return;
            lock(gate)updates.Add(new(source,parameter,description.TimelinePosition.Frame,description.Usage.ToString(),
                source.CurrentGeneration,source.Output.NativePointer.ToInt64(),source.CompositionCount));
        };
        var dialogs=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
        dialogs.Tick+=(_,_)=>{foreach(Window window in Application.Current.Windows)
            if(window.Title=="確認"&&LiveRefreshProof.AcceptSyntheticProjectSettings(window,Log))break;};
        dialogs.Start();Log("start");
        try
        {
            var project=Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_PROJECT")!;
            object? main=null,active=null;Window? window=null;
            await Until(()=>
            {
                window=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.DataContext?.GetType().FullName=="YukkuriMovieMaker.ViewModels.MainViewModel");
                main=window?.DataContext;active=Public(main,"ActiveTimelineViewModel");
                var names=(Public(active,"Characters") as IEnumerable)?.Cast<Character>().Select(c=>c.Name).ToArray()??[];
                return active is not null&&Public(active,"PrimarySerifInputRow") is not null
                    &&Equals(Public(Public(main,"ProjectFilePath"),"Value"),project)
                    &&new[]{"PR-A selected synthetic A","PR-A selected synthetic B","PR-A selected synthetic unused","PR-A selected synthetic none"}.All(names.Contains)
                    &&Equals(Public(HostPreparationBridge.Diagnostics,"mainAttached"),true);
            });
            Log("synthetic-project-and-production-bridge-attached");
            var originalHash=SHA256.HashData(File.ReadAllBytes(project));
            var characters=((IEnumerable)Public(active,"Characters")!).Cast<Character>().ToDictionary(c=>c.Name);
            var a=characters["PR-A selected synthetic A"];var b=characters["PR-A selected synthetic B"];
            var unused=characters["PR-A selected synthetic unused"];var nonTarget=characters["PR-A selected synthetic none"];
            var row=Public(active,"PrimarySerifInputRow")!;var rowCharacter=Public(row,"Character")!;
            var aPath=((CompiledItemParameter)a.TachieDefaultItemParameter).Source!.Path;
            var bPath=((CompiledItemParameter)b.TachieDefaultItemParameter).Source!.Path;
            var unusedPath=((CompiledItemParameter)unused.TachieDefaultItemParameter).Source!.Path;
            void Select(Character character)
            {
                SetValue(rowCharacter,character);
                Check(ReferenceEquals(Public(rowCharacter,"Value"),character),"Public row Character setter must retain the selected character.");
            }
            Check(Items(active!).Length==0,"Synthetic startup project must have no placed item.");
            Select(nonTarget);await Task.Delay(150);Check(!prefetch.Snapshot().Ready,"Non-target selection must clear prefetch.");
            window!.Activate();await Task.Delay(250);
            var before=PreviewPixelCapture.Capture(window);before.Save(Path.Combine(output,"first-before.png"));

            var cold=new BlockedStage();Volatile.Write(ref blocked,cold);
            var selectTimer=Stopwatch.StartNew();Select(a);selectTimer.Stop();
            await cold.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Check(ReferenceEquals(Public(Public(active,"CurrentCharacter"),"Value"),a),"Row A must drive the public CurrentCharacter observable.");
            var heartbeatTimer=Stopwatch.StartNew();for(var i=0;i<5;i++)await Task.Delay(50);heartbeatTimer.Stop();
            Check(!cold.Release.Task.IsCompleted&&prefetch.Snapshot().Preparing,"Selection must keep dispatcher responsive while blocked.");
            var prewarm=Stopwatch.StartNew();cold.Release.TrySetResult();Volatile.Write(ref blocked,null);
            await Until(()=>prefetch.Snapshot().Ready&&prefetch.Snapshot().SelectedPath==aPath);prewarm.Stop();
            Check(CompiledTachieSource.ProofDocumentSnapshot.ResidentDecodedBytes==128,"A must retain only its default required BGRA block.");
            lock(gate)Check(updates.Count==0,"Selection must not perform GPU host Update before placement.");
            var promotedBefore=prefetch.Snapshot().Promoted;var compilesBefore=preparation.CompilationCount;
            var placement=Stopwatch.StartNew();AddTachie(main!,window,a,0,Log);
            await Until(()=>Rendered(active!,a.Name,updates));placement.Stop();await Task.Delay(300);
            Check(prefetch.Snapshot().Promoted>promotedBefore,"First actual host source must promote the selected ready lease.");
            Check(preparation.CompilationCount==compilesBefore,"First placement must reuse precompiled content.");
            var after=PreviewPixelCapture.Capture(window);after.Save(Path.Combine(output,"first-after.png"));
            Check(before.Width==after.Width&&before.Height==after.Height&&before.PreviewHandle==after.PreviewHandle,"Preview geometry must remain stable.");
            var changed=Enumerable.Range(0,before.Width*before.Height).Where(i=>!before.Pixels.AsSpan(i*4,3).SequenceEqual(after.Pixels.AsSpan(i*4,3))).ToArray();
            Check(changed.Length>0,"First placement must become visible in actual preview pixels.");
            Check(changed.All(i=>i%before.Width>before.Width/3&&i%before.Width<before.Width*2/3
                &&i/before.Width>before.Height/3&&i/before.Width<before.Height*2/3),"Changed preview pixels must be in the synthetic sprite band.");
            phases.Add(new{phase="row-selection-prefetch-first-placement",selectionSetterMs=selectTimer.Elapsed.TotalMilliseconds,
                blockedUiHeartbeats=5,heartbeatElapsedMs=heartbeatTimer.Elapsed.TotalMilliseconds,warmAfterReleaseMs=prewarm.Elapsed.TotalMilliseconds,
                placementToReadyMs=placement.Elapsed.TotalMilliseconds,changedPreviewPixels=changed.Length,promoted=prefetch.Snapshot().Promoted,
                compilerCount=preparation.CompilationCount,pool=CompiledTachieSource.ProofDocumentSnapshot});Write("first-placement.json",phases[^1]);

            Select(nonTarget);await prefetch.ShutdownCompletion;
            var rapid=new BlockedStage();Volatile.Write(ref blocked,rapid);Select(b);
            await rapid.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var rapidTimer=Stopwatch.StartNew();for(var i=0;i<128;i++)Select(i%2==0?a:b);rapidTimer.Stop();
            Check(!rapid.Release.Task.IsCompleted,"Rapid switching must not wait for background snapshot.");
            rapid.Release.TrySetResult();Volatile.Write(ref blocked,null);
            await Until(()=>prefetch.Snapshot().Ready&&prefetch.Snapshot().SelectedPath==bPath);
            Check(preparation.CompilationCount==2,"Rapid A/B switching must compile only distinct content once.");
            phases.Add(new{phase="rapid-row-switch",selectionCalls=128,selectionLoopMs=rapidTimer.Elapsed.TotalMilliseconds,
                finalPath=Path.GetFileName(prefetch.Snapshot().SelectedPath),compilerCount=preparation.CompilationCount,
                speculativeSnapshotCancellations=rapid.Cancellations,snapshot=prefetch.Snapshot()});Write("rapid-selection.json",phases[^1]);

            Select(nonTarget);await prefetch.ShutdownCompletion;
            var priority=new BlockedStage();Volatile.Write(ref blocked,priority);Select(a);
            await priority.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            AddTachie(main!,window,b,1,Log); // Native command can place B while input row still selects A.
            await Until(()=>priority.Entries>=2&&Items(active!).Any(item=>item.CharacterName==b.Name));
            await Until(()=>prefetch.Snapshot().SelectedPath is null);
            var starts=prefetch.Snapshot().Started;
            Select(b);Select(nonTarget);Select(a);await Task.Delay(250);
            Check(prefetch.Snapshot().Started==starts&&!prefetch.Snapshot().Ready,"Selections must defer during actual blocked admission.");
            Check(priority.Cancellations>0,"Actual placement must retire the blocked speculative waiter.");
            priority.Release.TrySetResult();Volatile.Write(ref blocked,null);
            await Until(()=>Rendered(active!,a.Name,updates)&&Rendered(active!,b.Name,updates));
            await Until(()=>prefetch.Snapshot().Ready&&prefetch.Snapshot().SelectedPath==aPath);
            Check(preparation.CompilationCount==2,"Actual B must reuse compiled content after speculative cancellation.");
            phases.Add(new{phase="actual-use-priority",speculativeCancellations=priority.Cancellations,
                startsWhileActualBlocked=starts,startsAfterActual=prefetch.Snapshot().Started,
                compilerCount=preparation.CompilationCount,pool=CompiledTachieSource.ProofDocumentSnapshot});Write("actual-priority.json",phases[^1]);

            Select(nonTarget);await prefetch.ShutdownCompletion;await Task.Delay(250);
            var actualBytes=CompiledTachieSource.ProofDocumentSnapshot.ResidentDecodedBytes;
            Check(actualBytes==256,"Two actual appearances must remain independently leased.");
            var actualBefore=Latest(active!,updates);var retainedPixels=PreviewPixelCapture.Capture(window);
            retainedPixels.Save(Path.Combine(output,"expiry-before.png"));
            var wall=Stopwatch.StartNew();Select(unused);
            await Until(()=>prefetch.Snapshot().Ready&&prefetch.Snapshot().SelectedPath==unusedPath);
            Check(prefetch.Snapshot().RetainedDecodedBytes==128&&CompiledTachieSource.ProofDocumentSnapshot.ResidentDecodedBytes==actualBytes+128,"Unused selected source must add 128 decoded bytes.");
            var unusedSource=((CompiledItemParameter)unused.TachieDefaultItemParameter).Source!;
            string generationPath;
            using(var persisted=await preparation.PrepareAsync(unusedSource,preparation.Revision(unusedPath)))generationPath=Path.Combine(persisted.Directory,"manifest.json");
            await Task.Delay(TimeSpan.FromSeconds(29)-wall.Elapsed);
            Check(prefetch.Snapshot().Ready,"Unused appearance must remain at real elapsed 29 seconds.");
            var beforeExpiry=CompiledTachieSource.ProofDocumentSnapshot;
            await Until(()=>!prefetch.Snapshot().Ready&&CompiledTachieSource.ProofDocumentSnapshot.ResidentDecodedBytes==actualBytes,TimeSpan.FromSeconds(8));
            wall.Stop();Check(wall.Elapsed>=TimeSpan.FromSeconds(30)&&wall.Elapsed<TimeSpan.FromSeconds(36),"Real timer expiry must occur after 30 seconds within its scheduling margin.");
            Check(File.Exists(generationPath),"Expiry must preserve the persistent compiled generation.");
            var actualAfter=Latest(active!,updates);
            Check(actualBefore.Select(u=>u.Source).SequenceEqual(actualAfter.Select(u=>u.Source))&&actualBefore.Select(u=>u.Pointer).SequenceEqual(actualAfter.Select(u=>u.Pointer)),"Expiry must retain actual Source and output ownership.");
            Check(actualAfter.All(u=>u.Frame==0&&u.Generation is not null),"Paused actual appearances must stay rendered at Frame 0.");
            var retainedAfter=PreviewPixelCapture.Capture(window);retainedAfter.Save(Path.Combine(output,"expiry-after.png"));
            Check(retainedPixels.Pixels.AsSpan().SequenceEqual(retainedAfter.Pixels),"Unused expiry must preserve every actual preview pixel.");
            phases.Add(new{phase="real-time-unused-expiry",elapsedSeconds=wall.Elapsed.TotalSeconds,before=beforeExpiry,
                after=CompiledTachieSource.ProofDocumentSnapshot,retainedPrefetchBytes=prefetch.Snapshot().RetainedDecodedBytes,
                compiledGenerationPreserved=true,actualPreviewPixelsUnchanged=true});Write("real-expiry.json",phases[^1]);

            var budgets=await Task.Run(()=>SelectedPrefetchBudgetProof.Run(output));
            Check(budgets.All(result=>result.Passed),"All synthetic size/budget controls must pass.");
            Write("budget-results.json",budgets);phases.Add(new{phase="non-gui-budget-controls",results=budgets});
            Check(originalHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(project))),"Original synthetic project must remain unchanged.");
            Check(HostPreparationBridge.LastError is null,"Host completion bridge must have no error.");
            status="PASS_REAL_HOST_SELECTED_PREFETCH";Log(status);
        }
        catch(Exception ex){error=ex.ToString();Log("failure "+error);}
        finally
        {
            blocked?.Release.TrySetResult();dialogs.Stop();
            prefetch.Dispose();await prefetch.ShutdownCompletion;
            Write("selected-results.json",new{status,assertions,error,hostVersion=typeof(Project).Assembly.GetName().Version?.ToString(),
                sourceHead=Environment.GetEnvironmentVariable("SOURCE_HEAD"),
                productAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CompiledTachieSource).Assembly.Location))).ToLowerInvariant(),
                policy=new{unusedSeconds=30,maximumRequiredDecodedBytes=16L*1024*1024},compilerCount=preparation.CompilationCount,
                phases,bridge=HostPreparationBridge.Diagnostics,selectionDriver="public primary-row Character.Value",
                placementDriver="native public RoutedCommand AddTachieItem",mouseKeyboardVerified=false,hA2Verified=false,
                updates=updates.Select(u=>new{u.Frame,u.Usage,u.Generation,u.Pointer,u.Compositions,path=Path.GetFileName(u.Parameter.Source?.Path)})});
            CompiledTachieSource.ProofHostUpdate=null;CompiledTachieSource.ProofPrefetch=null;CompiledTachieSource.ProofPreparation=null;
            File.WriteAllText(Path.Combine(output,"selected-complete.txt"),status);
        }
    }
    private static void AddTachie(object main,Window target,Character character,int layer,Action<string> log)
    {
        var command=YukkuriMovieMaker.Settings.CommandSettings.Default[YukkuriMovieMaker.Settings.CommandType.AddTachieItem]
            ??throw new InvalidOperationException("Public AddTachieItem command is unavailable in this host.");
        log("native-command "+command.Name+" "+command.Text);
        var parameter=new AddCharacterItemCommandParameter(0,layer,character);
        if(!command.CanExecute(parameter,target))throw new InvalidOperationException("Native AddTachieItem cannot execute for the synthetic character.");
        log("native-add-start "+character.Name);command.Execute(parameter,target);log("native-add-return "+character.Name);
    }
    private static bool Rendered(object active,string name,List<Update> updates)
    {
        var item=Items(active).SingleOrDefault(i=>i.CharacterName==name);if(item is null)return false;
        lock(gate)return updates.Any(u=>ReferenceEquals(u.Parameter,item.TachieItemParameter)&&u.Generation is not null&&u.Usage=="Paused"&&u.Frame==0);
    }
    private static Update[] Latest(object active,List<Update> updates)
    {lock(gate)return Items(active).OrderBy(i=>i.CharacterName).Select(item=>updates.Last(u=>ReferenceEquals(u.Parameter,item.TachieItemParameter)&&u.Generation is not null&&u.Usage=="Paused")).ToArray();}
    private static async Task Until(Func<bool> condition,TimeSpan? timeout=null)
    {var watch=Stopwatch.StartNew();while(!condition()){if(watch.Elapsed>(timeout??TimeSpan.FromSeconds(30)))throw new TimeoutException("Selected prefetch host condition timed out.");await Task.Delay(50);}}
}
