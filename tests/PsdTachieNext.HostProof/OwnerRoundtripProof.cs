using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PsdTachieNext.Compiler;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;

namespace PsdTachieNext.HostProof;

/// <summary>Two live owners, then native live SaveProject/OpenProject in ONE host.
/// Hooks observe actual host Update only; no direct source drawing, Seek or private player access.</summary>
internal static class OwnerRoundtripProof
{
    private static bool scheduled;
    private static readonly object gate=new();
    private static readonly List<Update> updates=[];
    private sealed record Update(CompiledTachieSource Source,CompiledItemParameter Parameter,long Frame,
        string Usage,string? Generation,long Pointer,long Compositions,Guid Session);
    private sealed class PreparationGate
    {
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal static bool Schedule(string output)
    {
        if(scheduled)return true; scheduled=true;
        Application.Current.Dispatcher.BeginInvoke(new Action(()=>_=Run(output)),DispatcherPriority.Normal);
        return true;
    }
    private static object? Public(object? value,string name)=>value?.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.Instance)?.GetValue(value);
    private static void Invoke(object value,string method,params object[] arguments)
        => value.GetType().GetMethod(method,arguments.Select(a=>a.GetType()).ToArray())!.Invoke(value,arguments);
    private static TachieItem[] Items(object? main)=>(Public(Public(main,"ActiveTimelineViewModel"),"Items") as IEnumerable)?.Cast<object>()
        .Select(vm=>Public(vm,"Item")).OfType<TachieItem>().Where(x=>x.TachieItemParameter is CompiledItemParameter).ToArray()??[];
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        void Log(string message){lock(gate)File.AppendAllText(Path.Combine(output,"owner-stages.log"),DateTime.UtcNow.ToString("O")+" "+message+Environment.NewLine);}
        void Write(string name,object value)=>File.WriteAllText(Path.Combine(output,name),System.Text.Json.JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
        var assertions=0; var phases=new List<object>(); object? saved=null; var status="FAIL"; string? error=null;
        void Check(bool condition,string reason){assertions++;if(!condition)throw new InvalidOperationException(reason);}
        var currentGate=new PreparationGate();
        using var preparation=new SourcePreparationService(new CompiledAssetRepository(Path.Combine(output,"compiled-cache")));
        preparation.BeforeSnapshot=async token=>
        {
            var captured=Volatile.Read(ref currentGate); Log("snapshot-entered"); captured.Entered.TrySetResult();
            await captured.Release.Task.WaitAsync(token);
        };
        CompiledTachieSource.ProofPreparation=preparation;
        CompiledTachieSource.ProofHostUpdate=(source,description)=>
        {
            if(description.Tachie.ItemParameter is not CompiledItemParameter parameter)return;
            lock(gate)updates.Add(new(source,parameter,description.TimelinePosition.Frame,description.Usage.ToString(),
                source.CurrentGeneration,source.Output.NativePointer.ToInt64(),source.CompositionCount,source.RefreshRequest.SessionId));
        };
        CompiledTachieSource.ProofHostRequest=description=>Log("request "+description.Usage+" UI="+Application.Current.Dispatcher.CheckAccess());
        CompiledTachieSource.ProofReady=(stamp,tickets)=>Log("ready "+stamp+" tickets="+tickets);
        var dialogTimer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(250) };
        var observedWindows=new HashSet<string>();
        dialogTimer.Tick+=(_,_)=>
        {
            foreach(Window window in Application.Current.Windows)
            {
                var description=window.Title+" DataContext="+window.DataContext?.GetType().FullName;
                if(observedWindows.Add(description))Log("window "+description);
                if(window.Title=="確認" && LiveRefreshProof.AcceptSyntheticProjectSettings(window,Log))break;
            }
        };
        dialogTimer.Start(); Log("start");
        try
        {
            object? main=null; Window? hostWindow=null;
            await Until(()=>
            {
                hostWindow=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.DataContext?.GetType().FullName=="YukkuriMovieMaker.ViewModels.MainViewModel");
                main=hostWindow?.DataContext;return main is not null;
            });
            var projectPath=Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_PROJECT")??throw new InvalidOperationException("Synthetic project path missing.");
            var synthetic=Path.Combine(output,"synthetic.psd");
            var inputHash=SHA256.HashData(File.ReadAllBytes(projectPath));
            var initial=await Phase("initial",currentGate,main!,hostWindow!,synthetic);
            phases.Add(initial.Evidence);Write("owner-initial-results.json",initial.Evidence);
            Check(initial.Items.Select(x=>x.CharacterName).Distinct().Count()==2,"Two distinct characters must be live.");
            Check(preparation.CompilationCount==1,"Shared original must compile once.");

            var identities=initial.Items.OrderBy(x=>x.CharacterName).Select(x=>((CompiledItemParameter)x.TachieItemParameter).Source).ToArray();
            var savePath=Path.Combine(output,"saved-two.ymmp");
            Log("native-save-start");Invoke(main!,"SaveProject",savePath);Log("native-save-returned");
            await Until(()=>File.Exists(savePath)&&Equals(Public(Public(main,"ProjectFilePath"),"Value"),savePath));
            var savedJson=JObject.Parse(File.ReadAllText(savePath));
            var persisted=savedJson["Timelines"]![0]!["Items"]!.OrderBy(x=>(string?)x["CharacterName"]).ToArray();
            Check(persisted.Length==2,"Native save must contain both live owners.");
            Check(persisted.Select(x=>(string?)x["CharacterName"]).Distinct().Count()==2,"Native save must retain distinct character names.");
            for(var i=0;i<2;i++)Check(JToken.DeepEquals(persisted[i]["TachieItemParameter"]!["Source"],JObject.FromObject(identities[i]!)),"Native save must preserve SourceAssetRef.");
            Check(!File.ReadAllText(savePath).Contains("RefreshRevision")&&!File.ReadAllText(savePath).Contains("GenerationId"),"Runtime refresh/preparation must not enter saved JSON.");
            Check(inputHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(projectPath))),"Input fixture must remain unchanged.");
            Check(Equals(Public(main,"IsSaved"),true),"Native save must mark the live project saved.");
            saved=new { path=Path.GetFileName(savePath),nativeSaveProject=true,owners=persisted.Length,
                sourceReferencesPreserved=true,sha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(savePath))).ToLowerInvariant() };
            Write("owner-save-results.json",saved);

            // CreateProject opens an additional native workflow and did not return in the first
            // bounded trial. Use the already-observed OpenProject seam for a known empty fixture.
            var emptyPath=Path.Combine(output,"empty-between.ymmp");
            var empty=(JObject)savedJson.DeepClone();empty["FilePath"]=emptyPath;empty["Characters"]=new JArray();
            foreach(var timeline in empty["Timelines"]!) { timeline["Items"]=new JArray();timeline["CurrentFrame"]=0; }
            File.WriteAllText(emptyPath,empty.ToString(Formatting.None));
            Log("open-empty-start");Invoke(main!,"OpenProject",emptyPath);Log("open-empty-returned");
            await Until(()=>Items(main).Length==0);
            Check(initial.Items.All(item=>!Items(main).Contains(item)),"Initial owner objects must leave the live scope.");
            var reopenGate=new PreparationGate();Volatile.Write(ref currentGate,reopenGate);
            Log("native-reopen-start");Invoke(main!,"OpenProject",savePath);Log("native-reopen-returned");
            var reopened=await Phase("reopened",reopenGate,main!,hostWindow!,synthetic);
            phases.Add(reopened.Evidence);Write("owner-reopened-results.json",reopened.Evidence);
            var afterRefs=reopened.Items.OrderBy(x=>x.CharacterName).Select(x=>((CompiledItemParameter)x.TachieItemParameter).Source).ToArray();
            Check(identities.SequenceEqual(afterRefs),"Live reopen must retain both persisted source references.");
            Check(!initial.Items.Intersect(reopened.Items).Any(),"Reopen must construct fresh live owner objects.");
            Check(!initial.Sessions.Intersect(reopened.Sessions).Any(),"Reopen must construct fresh Source sessions.");
            Check(preparation.CompilationCount==1,"Saved reopen must reuse identical content cache.");
            status="PASS_REAL_PLAYER_TWO_OWNER_SAVE_REOPEN";
        }
        catch(Exception e){error=e.ToString();Log("failure "+error);}
        finally
        {
            currentGate.Release.TrySetResult();dialogTimer.Stop();
            CompiledTachieSource.ProofHostUpdate=null;CompiledTachieSource.ProofHostRequest=null;
            CompiledTachieSource.ProofReady=null;CompiledTachieSource.ProofPreparation=null;
            Write("owner-results.json",new { status,assertions,error,hostVersion=typeof(Project).Assembly.GetName().Version?.ToString(),
                sourceHead=Environment.GetEnvironmentVariable("SOURCE_HEAD"),
                productAssemblySha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CompiledTachieSource).Assembly.Location))).ToLowerInvariant(),
                compilerCount=preparation.CompilationCount,phases,saved,bridge=HostPreparationBridge.Diagnostics,
                bridgeError=HostPreparationBridge.LastError?.ToString(),audioVerified=false,dedicatedDirtyFlagVerified=false,
                updates=updates.Select(u=>new{u.Frame,u.Usage,u.Generation,u.Pointer,u.Compositions,u.Session,identity=u.Parameter.Source?.AssetIdentity}) });
            File.WriteAllText(Path.Combine(output,"owner-complete.txt"),status);
        }

        async Task<(TachieItem[] Items,Guid[] Sessions,object Evidence)> Phase(string label,PreparationGate blocked,object main,Window window,string path)
        {
            Log(label+" begin");
            // Only bounds a pathological strict/UI request; such a release causes the blocked assertions to fail.
            _=Task.Run(async()=>{await blocked.Entered.Task;await Task.Delay(TimeSpan.FromSeconds(30));if(blocked.Release.TrySetResult())Log(label+" watchdog-released");});
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));await Task.Delay(1500);
            TachieItem[] items=[];
            await Until(()=>
            {
                items=Items(main).OrderBy(x=>x.CharacterName).ToArray();
                lock(gate)return items.Length==2&&items.All(x=>x.TachieItemParameter is CompiledItemParameter p&&p.File==path
                    &&updates.Any(u=>ReferenceEquals(u.Parameter,p)&&u.Usage=="Paused"));
            });
            var originals=items.Select(x=>(CompiledItemParameter)x.TachieItemParameter).ToArray();
            var json=originals.Select(JsonConvert.SerializeObject).ToArray();
            Update[] baseline;lock(gate)baseline=originals.Select(p=>updates.Last(u=>ReferenceEquals(u.Parameter,p)&&u.Usage=="Paused")).ToArray();
            Check(baseline.All(u=>u.Generation is null),label+": both cold-ready outputs must be empty before release.");
            Check(!blocked.Release.Task.IsCompleted,label+": preparation must still be blocked.");
            Check(baseline.Select(u=>u.Session).Distinct().Count()==2,label+": independent Source sessions required.");
            var active=Public(main,"ActiveTimelineViewModel");var savedBefore=Public(main,"IsSaved");
            window.Activate();await Task.Delay(300);
            var before=PreviewPixelCapture.Capture(window);before.Save(Path.Combine(output,label+"-before.png"));
            var replacements=HostPreparationBridge.ReplacementCount;
            blocked.Release.TrySetResult();
            await Until(()=>{lock(gate)return baseline.All(b=>updates.Any(u=>ReferenceEquals(u.Source,b.Source)&&u.Generation is not null&&u.Usage=="Paused"));});
            await Task.Delay(300);
            Update[] final;lock(gate)final=baseline.Select(b=>updates.Last(u=>ReferenceEquals(u.Source,b.Source)&&u.Generation is not null)).ToArray();
            Check(final.All(u=>u.Frame==0)&&baseline.All(u=>u.Frame==0),label+": stopped frame must remain zero.");
            Check(final.Zip(baseline).All(x=>x.First.Pointer==x.Second.Pointer&&x.First.Session==x.Second.Session),label+": Source and Output pointer must survive completion.");
            Check(final.All(u=>u.Compositions==1),label+": both Sources must compose once.");
            Check(items.Select(x=>JsonConvert.SerializeObject(x.TachieItemParameter)).SequenceEqual(json),label+": persisted parameter JSON must remain identical.");
            Check(ReferenceEquals(active,Public(main,"ActiveTimelineViewModel")),label+": timeline VM must remain active.");
            Check(Equals(savedBefore,Public(main,"IsSaved")),label+": refresh must preserve public IsSaved.");
            Check(HostPreparationBridge.ReplacementCount>replacements&&HostPreparationBridge.LastError is null,label+": completion bridge must notify a live owner.");
            var after=PreviewPixelCapture.Capture(window);after.Save(Path.Combine(output,label+"-after.png"));
            Check(before.Width==after.Width&&before.Height==after.Height&&before.PreviewHandle==after.PreviewHandle,label+": preview geometry/handle must remain stable.");
            var changed=Enumerable.Range(0,before.Width*before.Height).Where(i=>!before.Pixels.AsSpan(i*4,3).SequenceEqual(after.Pixels.AsSpan(i*4,3))).ToArray();
            var left=changed.Where(i=>i%before.Width<before.Width/2).ToArray();var right=changed.Where(i=>i%before.Width>=before.Width/2).ToArray();
            Check(left.Length>0&&right.Length>0,label+": both separated character regions must become visible.");
            Check(changed.All(i=>i/before.Width>before.Height/3&&i/before.Width<before.Height*2/3),label+": changed pixels must stay in the sprite band.");
            Check(left.All(i=>i%before.Width>before.Width/6&&i%before.Width<before.Width*2/5)
                &&right.All(i=>i%before.Width>before.Width*3/5&&i%before.Width<before.Width*9/10),label+": changed pixels must match the two synthetic positions.");
            var evidence=new { phase=label,liveOwners=2,frameBefore=0,frameAfter=0,sameSourceSessions=true,sameOutputPointers=true,
                previewPixelsVerified=true,previewWidth=before.Width,previewHeight=before.Height,changedPixels=changed.Length,
                leftChangedPixels=left.Length,rightChangedPixels=right.Length,compilerCount=preparation.CompilationCount,
                parameterReplacements=HostPreparationBridge.ReplacementCount-replacements,savedBefore,savedAfter=Public(main,"IsSaved"),
                sessions=final.Select(u=>u.Session).ToArray(),identities=items.Select(x=>((CompiledItemParameter)x.TachieItemParameter).Source!.AssetIdentity).ToArray(),
                beforePixelSha256=Convert.ToHexString(SHA256.HashData(before.Pixels)).ToLowerInvariant(),
                afterPixelSha256=Convert.ToHexString(SHA256.HashData(after.Pixels)).ToLowerInvariant() };
            Log(label+" PASS changed="+changed.Length+" left="+left.Length+" right="+right.Length);
            return(items,final.Select(u=>u.Session).ToArray(),evidence);
        }
    }
    private static async Task Until(Func<bool> condition)
    { var deadline=DateTime.UtcNow+TimeSpan.FromSeconds(45);while(!condition()){if(DateTime.UtcNow>deadline)throw new TimeoutException("Owner roundtrip condition timed out.");await Task.Delay(50);} }
}
