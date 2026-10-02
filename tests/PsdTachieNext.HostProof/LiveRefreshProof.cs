using System.Collections;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using PsdTachieNext.Compiler;
using PsdTachieNext.Tests;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using NativeJson = YukkuriMovieMaker.Json.Json;

namespace PsdTachieNext.HostProof;

/// <summary>Product integration regression: normal project open, real host Update, no direct draw/seek calls.</summary>
internal static class LiveRefreshProof
{
    private static bool scheduled;
    private static readonly object gate = new();
    private static readonly List<ObservedUpdate> updates = [];
    private sealed record ObservedUpdate(CompiledTachieSource Source, CompiledItemParameter Parameter,
        long Frame, string Usage, string? Generation, long Compositions, long Pointer);
    internal static bool Schedule()
    {
        var output = Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) return false;
        if (Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_SCENARIO") == "OwnerRoundtrip")
            return OwnerRoundtripProof.Schedule(output);
        if (Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_SCENARIO") == "SelectedPrefetch")
            return SelectedPrefetchProof.Schedule(output);
        if (Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_SCENARIO") == "NormalExport")
            return NormalExportProof.Schedule(output);
        if (scheduled) return true; scheduled = true;
        Application.Current.Dispatcher.BeginInvoke(new Action(() => _ = Run(output)), DispatcherPriority.Normal);
        return true;
    }
    private static object? Public(object? value, string name) => value?.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public)?.GetValue(value);
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        void Log(string message) { lock(gate) File.AppendAllText(Path.Combine(output,"live-stages.log"),DateTime.UtcNow.ToString("O")+" "+message+Environment.NewLine); }
        Log("start");
        var dialogTimer=new DispatcherTimer { Interval=TimeSpan.FromSeconds(1) };
        dialogTimer.Tick+=(_,_)=>
        {
            foreach(Window window in Application.Current.Windows)
                if(window.Title=="確認")
                {
                    if(AcceptSyntheticProjectSettings(window,Log)) break;
                }
        };
        dialogTimer.Start();
        var assertions = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var preparation = new SourcePreparationService(new CompiledAssetRepository(Path.Combine(output,"cold-cache")));
        preparation.BeforeSnapshot = async token => { Log("snapshot-entered"); entered.TrySetResult(); await release.Task.WaitAsync(token); };
        _ = Task.Run(async()=> { await entered.Task; await Task.Delay(TimeSpan.FromSeconds(30)); if(release.TrySetResult())Log("watchdog-released-preparation"); });
        CompiledTachieSource.ProofPreparation = preparation;
        CompiledTachieSource.ProofHostRequest = description => Log("request "+description.Usage+" UI="+Application.Current.Dispatcher.CheckAccess());
        CompiledTachieSource.ProofReady = (stamp,tickets) => Log("ready "+stamp+" tickets="+tickets);
        CompiledTachieSource.ProofHostUpdate = (source, description) =>
        {
            if (description.Tachie.ItemParameter is not CompiledItemParameter parameter) return;
            lock (gate) updates.Add(new(source,parameter,description.TimelinePosition.Frame,description.Usage.ToString(),
                source.CurrentGeneration,source.CompositionCount,source.Output.NativePointer.ToInt64()));
        };
        string status = "FAIL"; string? error = null; object? details = null;
        try
        {
            object? main = null;
            await Until(() =>
            {
                main = Application.Current.Windows.Cast<Window>().Select(w=>w.DataContext)
                    .FirstOrDefault(vm=>vm?.GetType().FullName=="YukkuriMovieMaker.ViewModels.MainViewModel");
                return main is not null;
            });
            var supplied = Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_PROJECT");
            var sourcePath = Path.Combine(string.IsNullOrWhiteSpace(supplied)?output:Path.GetDirectoryName(supplied)!,"synthetic.psd");
            var path=string.IsNullOrWhiteSpace(supplied)?Path.Combine(output,"synthetic.ymmp"):supplied;
            if (string.IsNullOrWhiteSpace(supplied))
            {
            PsdFixture.Write(sourcePath, seed:57);
            var character = new Character { Name="PR-A live synthetic", TachieType=typeof(CompiledTachiePlugin),
                TachieCharacterParameter=new CompiledCharacterParameter(),
                TachieDefaultItemParameter=new CompiledItemParameter { File=sourcePath },
                TachieDefaultFaceParameter=new CompiledFaceParameter() };
            var seed = new TachieItem(character) { Frame=0,Length=300,Layer=0,
                TachieItemParameter=new CompiledItemParameter { File=sourcePath } };
            var timeline = new Timeline { Name="PR-A live stopped", Items=ImmutableList.Create<IItem>(seed) };
            timeline.VideoInfo.Width=64; timeline.VideoInfo.Height=32; timeline.VideoInfo.FPS=30;
            timeline.VideoInfo.BackgroundColor=System.Windows.Media.Colors.Black; timeline.RefreshTimelineLengthAndMaxLayer();
            var project=new Project(new[]{character},path); project.Timelines.Clear(); project.Timelines.Add(timeline);
            NativeJson.Save(project,path,null);
            Log("open-project-start visible="+Application.Current.MainWindow?.IsVisible);
            main!.GetType().GetMethod("OpenProject",[typeof(string)])!.Invoke(main,[path]);
            Log("open-project-returned");
            }
            else Log("existing-project-launch "+path);
            var inputHash=SHA256.HashData(File.ReadAllBytes(path));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(180)); // Allows the user to resolve first-open character selection.
            await Task.Delay(1500); // Let project-open's initial temporary Source/VM reconstruction finish while CPU remains blocked.
            object? active=null; TachieItem? item=null;
            await Until(()=>
            {
                active=Public(main,"ActiveTimelineViewModel");
                item=(Public(active,"Items") as IEnumerable)?.Cast<object>().Select(vm=>Public(vm,"Item"))
                    .OfType<TachieItem>().SingleOrDefault(x=>x.TachieItemParameter is CompiledItemParameter p&&p.File==sourcePath);
                lock(gate) return item is not null && updates.Any(u=>ReferenceEquals(u.Parameter,item.TachieItemParameter)&&u.Usage=="Paused");
            });
            var original=(CompiledItemParameter)item!.TachieItemParameter;
            var json=JsonConvert.SerializeObject(original); ObservedUpdate baseline;
            lock(gate) baseline=updates.Last(u=>ReferenceEquals(u.Parameter,original)&&u.Usage=="Paused");
            Log("baseline session="+baseline.Source.RefreshRequest.SessionId+" parameter="+System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(original));
            var savedBefore=Public(main,"IsSaved");
            Check(baseline.Generation is null); Check(!release.Task.IsCompleted);
            // Dispatcher heartbeat while preparation is deliberately blocked: operations stay available.
            var heartbeats=0; for(var i=0;i<5;i++){await Task.Delay(50);heartbeats++;}
            Check(heartbeats==5 && !release.Task.IsCompleted);
            var hostWindow=Application.Current.Windows.Cast<Window>().Single(w=>ReferenceEquals(w.DataContext,main));
            hostWindow.Activate(); await Task.Delay(300);
            var beforePixels=PreviewPixelCapture.Capture(hostWindow); beforePixels.Save(Path.Combine(output,"preview-before.png"));
            var beforeReplacement=HostPreparationBridge.ReplacementCount;
            release.TrySetResult();
            await Until(()=>
            {
                lock(gate) return updates.Any(u=>ReferenceEquals(u.Source,baseline.Source)&&u.Generation is not null
                    &&u.Usage=="Paused") && !ReferenceEquals(item.TachieItemParameter,original);
            });
            ObservedUpdate final;
            lock(gate) final=updates.Last(u=>ReferenceEquals(u.Source,baseline.Source)&&u.Generation is not null);
            Check(final.Frame==baseline.Frame && final.Frame==0);
            Check(final.Pointer==baseline.Pointer && final.Compositions==1);
            Check(!ReferenceEquals(item.TachieItemParameter,original));
            Check(JsonConvert.SerializeObject(item.TachieItemParameter)==json);
            Check(((CompiledItemParameter)item.TachieItemParameter).Source==original.Source);
            Check(preparation.CompilationCount==1);
            Check(HostPreparationBridge.ReplacementCount>beforeReplacement && HostPreparationBridge.LastError is null);
            Check(inputHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))));
            Check(ReferenceEquals(active,Public(main,"ActiveTimelineViewModel")));
            await Task.Delay(250); Check(Equals(savedBefore,Public(main,"IsSaved")));
            var afterPixels=PreviewPixelCapture.Capture(hostWindow); afterPixels.Save(Path.Combine(output,"preview-after.png"));
            Check(beforePixels.Width==afterPixels.Width && beforePixels.Height==afterPixels.Height && beforePixels.PreviewHandle==afterPixels.PreviewHandle);
            var changedPixels=Enumerable.Range(0,beforePixels.Width*beforePixels.Height).Where(i=>
                !beforePixels.Pixels.AsSpan(i*4,3).SequenceEqual(afterPixels.Pixels.AsSpan(i*4,3))).ToArray();
            Check(changedPixels.Length>0);
            var bounds=new { left=changedPixels.Min(i=>i%beforePixels.Width),top=changedPixels.Min(i=>i/beforePixels.Width),
                right=changedPixels.Max(i=>i%beforePixels.Width),bottom=changedPixels.Max(i=>i/beforePixels.Width) };
            // The tiny synthetic sprite is at the scene center; UI/border changes cannot satisfy this.
            Check(bounds.left>beforePixels.Width/3 && bounds.right<beforePixels.Width*2/3
                && bounds.top>beforePixels.Height/3 && bounds.bottom<beforePixels.Height*2/3);
            details=new { baselineFrame=baseline.Frame,finalFrame=final.Frame, stablePointer=final.Pointer,
                realHostUpdates=updates.Count, compilerCount=preparation.CompilationCount,
                parameterReplacements=HostPreparationBridge.ReplacementCount-beforeReplacement,
                oldIdentity=original.Source!.AssetIdentity,newIdentity=((CompiledItemParameter)item.TachieItemParameter).Source!.AssetIdentity,
                savedBefore,savedAfter=Public(main,"IsSaved"),
                hostDescriptionUsesReplacement=ReferenceEquals(final.Parameter,item.TachieItemParameter),
                liveDirtyFlagVerified=false,audioVerified=false,previewPixelsVerified=true,
                previewWidth=beforePixels.Width,previewHeight=beforePixels.Height,changedPixels=changedPixels.Length,changedBounds=bounds,
                beforePixelSha256=Convert.ToHexString(SHA256.HashData(beforePixels.Pixels)).ToLowerInvariant(),
                afterPixelSha256=Convert.ToHexString(SHA256.HashData(afterPixels.Pixels)).ToLowerInvariant() };
            status="PASS_REAL_PLAYER_ASYNC_REFRESH";
            void Check(bool value){assertions++;if(!value)throw new InvalidOperationException("Assertion "+assertions+" failed");}
        }
        catch(Exception e){error=e.ToString();}
        finally
        {
            dialogTimer.Stop();
            release.TrySetResult(); CompiledTachieSource.ProofHostUpdate=null; CompiledTachieSource.ProofPreparation=null;
            CompiledTachieSource.ProofHostRequest=null;
            CompiledTachieSource.ProofReady=null;
            lock(gate) File.WriteAllText(Path.Combine(output,"live-results.json"),System.Text.Json.JsonSerializer.Serialize(new{
                status,assertions,error,hostVersion=typeof(Project).Assembly.GetName().Version?.ToString(),
                productAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CompiledTachieSource).Assembly.Location))).ToLowerInvariant(),details,
                bridgeError=HostPreparationBridge.LastError?.ToString(),bridge=HostPreparationBridge.Diagnostics,
                updates=updates.Select(u=>new{u.Frame,u.Usage,u.Generation,u.Compositions,u.Pointer,parameterIdentity=u.Parameter.Source?.AssetIdentity,
                    parameterRuntimeId=System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(u.Parameter),sourceSession=u.Source.RefreshRequest.SessionId})
            },new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(output,"live-complete.txt"),status);
        }
    }
    private static IEnumerable<string> Texts(DependencyObject root)
    {
        if(root is System.Windows.Controls.TextBlock text)yield return text.Text;
        if(root is System.Windows.Controls.Button button)yield return "BUTTON="+button.Content;
        if(root is System.Windows.Media.Visual || root is System.Windows.Media.Media3D.Visual3D)
            for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
                foreach(var value in Texts(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return value;
    }
    internal static bool AcceptSyntheticProjectSettings(Window window, Action<string> log)
    {
        var texts=Texts(window).ToArray(); log("confirmation "+string.Join(" | ",texts));
        // Used only by the isolated synthetic-project regressions, never by normal plugin startup.
        if (!texts.Contains("プロジェクトファイルの設定で上書き")) return false;
        var radio=Descendants(window).OfType<System.Windows.Controls.RadioButton>()
            .SingleOrDefault(x=>Texts(x).Contains("プロジェクトファイルの設定で上書き"));
        var ok=Descendants(window).OfType<System.Windows.Controls.Button>().SingleOrDefault(x=>Equals(x.Content,"OK"));
        if(radio is null || ok?.Command is not { } command) return false;
        radio.IsChecked=true;
        if(!command.CanExecute(ok.CommandParameter)) return false;
        log("synthetic-project-settings-selected"); command.Execute(ok.CommandParameter); return true;
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        if(root is System.Windows.Media.Visual || root is System.Windows.Media.Media3D.Visual3D)
            for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
                foreach(var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private static async Task Until(Func<bool> condition)
    {
        var until=DateTime.UtcNow+TimeSpan.FromSeconds(30);
        while(!condition()){if(DateTime.UtcNow>until)throw new TimeoutException("Live host observation timed out");await Task.Delay(50);}
    }
}
