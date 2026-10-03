using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PsdTachieNext.Compiler;
using PsdTachieNext.Ymm4;
using YukkuriMovieMaker.Plugin.FileWriter;
using YukkuriMovieMaker.Player.Video;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;

namespace PsdTachieNext.HostProof;

internal static class NormalExportProof
{
    private static bool scheduled;
    private static readonly object gate=new();
    private sealed class Stage(string name,SourcePreparationService service)
    {
        internal readonly string Name=name;internal readonly SourcePreparationService Service=service;
        internal readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously),NextFrame=new(TaskCreationOptions.RunContinuationsAsynchronously),ContinueFrame=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly HashSet<CompiledTachieSource> Sources=[];
        internal readonly List<object> Requests=[],Frames=[],Errors=[];
        internal readonly List<double> Disposals=[];
        internal readonly Stopwatch Clock=Stopwatch.StartNew();
        internal CompiledItemParameter? NextParameter;
        internal string? File;
        internal int Cancellations;
        internal bool ImmediateCancel;
        internal int NativeErrorDialogs;
    }
    internal static bool Schedule(string output)
    {
        if(scheduled)return true;scheduled=true;
        Application.Current.Dispatcher.BeginInvoke(new Action(()=>_=Run(output)),DispatcherPriority.Normal);return true;
    }
    private static object? Public(object? value,string name)=>value?.GetType().GetProperty(name,BindingFlags.Public|BindingFlags.Instance)?.GetValue(value);
    private static object? Value(object? value,string name)=>Public(Public(value,name),"Value");
    private static void Set(object value,string name,object setting)=>Public(value,name)!.GetType().GetProperty("Value")!.SetValue(Public(value,name),setting);
    private static void Execute(object value,string name){var command=(ICommand)Public(value,name)!;if(!command.CanExecute(null))throw new InvalidOperationException(name+" cannot execute");command.Execute(null);}
    private static IEnumerable<object> ViewModels(object? main)=>Application.Current.Windows.Cast<Window>().Select(w=>w.DataContext)
        .Concat((Public(main,"ChildWindowViewModels") as IEnumerable)?.Cast<object>()??[]).Append(Value(main,"ModalViewModel")).OfType<object>();
    private static object? Find(object main,string type)=>ViewModels(main).FirstOrDefault(x=>x.GetType().FullName=="YukkuriMovieMaker.ViewModels."+type);
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);var cases=new List<object>();var services=new List<SourcePreparationService>();var assertions=0;string status="FAIL",error="";Stage? stage=null;object? nativeProgress=null;var errorPresentationOpen=false;
        void Log(string value){lock(gate){if(value.StartsWith("native-output-error-dialog")&&stage is { } current)current.NativeErrorDialogs++;File.AppendAllText(Path.Combine(output,"export-stages.log"),DateTime.UtcNow.ToString("O")+" "+value+Environment.NewLine);}}
        void Write(string name,object value)=>File.WriteAllText(Path.Combine(output,name),JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));
        void Check(bool value,string reason){assertions++;Log("assert "+assertions+" "+(value?"PASS ":"FAIL ")+reason);if(!value)throw new InvalidOperationException(reason);}
        using var preview=new SourcePreparationService(new CompiledAssetRepository(Path.Combine(output,"preview-cache")));
        CompiledTachieSource.ProofPreparation=preview;CompiledTachieSource.ProofPrefetch=null;
        CompiledTachieSource.ProofLifecycle=(source,eventName)=>
        {
            var current=Volatile.Read(ref stage);if(current is null||!source.UsesPreparation(current.Service))return;
            lock(gate){if(eventName=="export-start")current.Sources.Add(source);else if(eventName=="dispose-requested"&&current.Sources.Contains(source))current.Disposals.Add(current.Clock.Elapsed.TotalSeconds);}
                Log(current.Name+" "+eventName);
        };
        CompiledTachieSource.ProofHostRequest=description=>
        {
            var current=Volatile.Read(ref stage);if(current is null||description.Usage!=TimelineSourceUsage.Exporting)return;
            lock(gate)current.Requests.Add(new{frame=description.TimelinePosition.Frame,usage=description.Usage.ToString(),onUi=Application.Current.Dispatcher.CheckAccess()});
            if(current.Name=="reference-clear"&&description.TimelinePosition.Frame>0&&!current.NextFrame.Task.IsCompleted)
            {current.NextParameter=(CompiledItemParameter)description.Tachie.ItemParameter;current.NextFrame.TrySetResult();current.ContinueFrame.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();}
        };
        CompiledTachieSource.ProofHostUpdate=(source,description)=>
        {
            var current=Volatile.Read(ref stage);if(current is null||description.Usage!=TimelineSourceUsage.Exporting||!source.UsesPreparation(current.Service))return;
            lock(gate)current.Frames.Add(new{frame=description.TimelinePosition.Frame,generation=source.CurrentGeneration,stamp=source.RefreshRequest,pointer=source.Output.NativePointer.ToInt64()});
        };
        CompiledTachieSource.ProofHostError=(source,description,exception)=>
        {
            var current=Volatile.Read(ref stage);if(current is null||description.Usage!=TimelineSourceUsage.Exporting||!source.UsesPreparation(current.Service))return;
            lock(gate)current.Errors.Add(new{frame=description.TimelinePosition.Frame,type=exception.GetType().FullName,message=exception.ToString(),generationPreserved=source.CurrentGeneration is not null,sourceState=source.PreparationState.ToString()});
            Log(current.Name+" source-error "+exception);
        };
        using var dialogsStop=new CancellationTokenSource();
        var automation=OwnedExportDialogs.Run(()=>{lock(gate)return(stage?.File,stage?.Errors.Count>0);},Log,dialogsStop.Token);
        var observedFeedback=new HashSet<Window>();
        var confirmations=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
        confirmations.Tick+=(_,_)=>
        {
            foreach(Window window in Application.Current.Windows.Cast<Window>().ToArray())
            {
                if(window.Title=="確認")LiveRefreshProof.AcceptSyntheticProjectSettings(window,Log);
                var current=Volatile.Read(ref stage);
                if(current is null||window.DataContext?.GetType().FullName!="YukkuriMovieMaker.ViewModels.FeedbackViewModel"
                    ||window.Title!="例外が発生しました"||observedFeedback.Contains(window))continue;
                lock(gate)if(current.Errors.Count==0)continue;
                var texts=ReadVisibleTexts(window);
                if(!texts.Any(text=>text.Contains("動画ファイルの出力に失敗しました。")&&text.Contains("PR-A H-A2 synthetic")))continue;
                observedFeedback.Add(window);
                Write(current.Name+"-native-feedback.json",new{window.Title,dataContext=window.DataContext.GetType().FullName,texts,feedbackSent=false,clipboardUsed=false});
                Log("native-output-error-dialog public-wpf-feedback "+current.Name);
                // Close only the owned synthetic output error; never invoke Send or Copy.
                window.Close();
            }
        };confirmations.Start();
        try
        {
            object? main=null;Window? window=null;var project=Environment.GetEnvironmentVariable("PSD_NEXT_LIVE_PROOF_PROJECT")!;
            await Until(()=>{window=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.DataContext?.GetType().FullName=="YukkuriMovieMaker.ViewModels.MainViewModel");main=window?.DataContext;return Equals(Value(main,"ProjectFilePath"),project)&&Public(main,"ActiveTimelineViewModel") is not null;});
            // Clearing the logical fixture reference is destructive to this input copy;
            // keep it last so later cases cannot accidentally export that cleared fixture.
            foreach(var name in new[]{"cold","failure","cancel","reference-clear"})
            {
                CompiledTachieSource.ProofPreparation=preview;stage=null;
                var command=CommandSettings.Default[CommandType.OutputVideo]??throw new InvalidOperationException("Native OutputVideo unavailable");
                await Until(()=>command.CanExecute(null,window!));command.Execute(null,window!);
                object? config=null;await Until(()=>{config=Find(main!,"Mp4ConfigViewModel");return config is not null;});
                // The native [0,4) request returned frames 0..3 and four decoded frames.
                // Use five as the exclusive endpoint for this five-frame fixture.
                Set(config!,"EncodeFrom",0);Set(config!,"EncodeTo",5);
                Write(name+"-native-config.json",new{encodeFrom=Value(config,"EncodeFrom"),encodeTo=Value(config,"EncodeTo"),projectVideo=Public(Public(main,"ActiveTimelineViewModel"),"Timeline") is Timeline timeline?new{timeline.VideoInfo.Width,timeline.VideoInfo.Height,timeline.VideoInfo.FPS}:null});
                var plugin=(IVideoFileWriterPlugin)Value(config,"SelectedVideoFileWriterPlugin")!;
                Check(plugin.GetType().Assembly.GetName().Name!.StartsWith("YukkuriMovieMaker"),"Use only the official bundled writer.");
                Check(!plugin.NeedDownloadResources(),"Writer requires resources/dependencies; stop.");
                Check(plugin.OutputPathMode==VideoFileWriterOutputPath.File,"Default native writer must output a file.");
                var extension=plugin.GetFileExtention().TrimStart('.');Check(extension.All(char.IsAsciiLetterOrDigit),"Unexpected native extension");
                var service=new SourcePreparationService(new CompiledAssetRepository(Path.Combine(output,name+"-cold-cache")));services.Add(service);
                var current=new Stage(name,service){File=Path.Combine(output,name+"."+extension)};Volatile.Write(ref stage,current);
                service.BeforeSnapshot=async token=>
                {
                    current.Entered.TrySetResult();
                    try{await current.Release.Task.WaitAsync(token);}catch(OperationCanceledException){Interlocked.Increment(ref current.Cancellations);throw;}
                    if(name=="failure")throw new InvalidDataException("HA2_SYNTHETIC_PREPARATION_FAILURE");
                };
                CompiledTachieSource.ProofPreparation=service;Log(name+" native-output-command writer="+plugin.GetType().FullName);Execute(config!,"OutputCommand");
                await current.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                object? progress=null;await Until(()=>{progress=Find(main!,"ProgressViewModel");return progress is not null;});
                nativeProgress=progress;
                var nativeToken=(CancellationToken)Public(progress!,"CancellationToken")!;
                lock(gate){Check(current.Sources.Count>0,"Cold service must belong to a real exporting Source");Check(current.Frames.Count==0,"No Source Update may return before readiness");}
                Check(service.CompilationCount==0,"Preview must not warm the export cache");
                for(var beat=0;beat<5;beat++)await Task.Delay(50);Check(!Equals(Value(progress,"IsCompleted"),true),"Native output must remain pending during cold gate");
                if(name=="cancel")
                {
                    var cancelAt=current.Clock.Elapsed.TotalSeconds;Execute(progress!,"CancelCommand");await Until(()=>nativeToken.IsCancellationRequested);
                    await Task.Delay(3000);lock(gate)current.ImmediateCancel=current.Disposals.Count>0;
                    Write("cancel-wait-boundary.json",new{nativeCancellationRequested=nativeToken.IsCancellationRequested,userCancelFlag=Value(progress,"IsCancellationRequested"),cancelAt,
                        observationMilliseconds=3000,sourceDisposeWithinThreeSeconds=current.ImmediateCancel,preparationWaitCanceled=current.Cancellations>0,
                        nativeCompletedBeforeTestRelease=Value(progress,"IsCompleted"),sourceFramesBeforeTestRelease=current.Frames.Count,compilerCountBeforeTestRelease=service.CompilationCount,manualSourceDispose=false});
                    // A bounded test release is explicitly not evidence that native Cancel interrupted Update.
                    if(!current.ImmediateCancel)Log("cancel boundary OPEN: releasing test gate after 3s; no native Dispose while waiting");
                }
                Log(name+" test-gate-release at="+current.Clock.Elapsed.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                current.Release.TrySetResult();
                if(name=="reference-clear")
                {
                    await current.NextFrame.Task.WaitAsync(TimeSpan.FromSeconds(30));current.NextParameter!.Source=null;current.ContinueFrame.TrySetResult();
                }
                await Until(()=>Equals(Value(progress,"IsCompleted"),true),TimeSpan.FromSeconds(60));
                lock(gate)Write(name+"-terminal-before-assertions.json",new{current.File,current.Requests,current.Frames,current.Errors,current.Disposals,completed=Value(progress,"IsCompleted"),message=Public(progress,"Message"),nativeCancel=nativeToken.IsCancellationRequested,userCancelFlag=Value(progress,"IsCancellationRequested")});
                Write(name+"-visible-terminal-ui.json",VisibleText());
                var progressClosed=false;
                if(name=="cold")
                {
                    lock(gate){Check(current.Errors.Count==0&&current.Frames.Count>=5,"Cold job must render all requested frames without Source errors");}
                    Check(File.Exists(current.File),"Native media must exist at the exact owned output path; UIA readback alone is insufficient");
                    var pixels=await Decode(current.File!);Check(pixels.Length==64*32*4*5,"Native media must decode to five complete frames");
                    for(var frame=0;frame<5;frame++)
                    {
                        long green=0,red=0,blue=0;for(var y=10;y<22;y++)for(var x=20;x<44;x++){var offset=((frame*32+y)*64+x)*4;blue+=pixels[offset];green+=pixels[offset+1];red+=pixels[offset+2];}
                        Check(green>0&&green>red*1.2&&green>blue*1.2,"Every decoded frame must contain the known green synthetic sprite");
                    }
                    // Terminal token state was also canceled without invoking CancelCommand.
                    // Exact completed media and Source errors, not that token alone, decide success.
                    Check(service.CompilationCount==1,"Normal cold output must compile once");
                }
                else if(name is "failure" or "reference-clear")
                {
                    lock(gate){Check(current.Errors.Count>0,"Native job must receive preparation/frozen-reference error");Check(current.Frames.Count<5,"Error must stop subsequent Source frames");}
                    if(File.Exists(current.File)&&new FileInfo(current.File).Length>0)
                    {
                        byte[] partial=[];string mediaError="";
                        try{partial=await Decode(current.File!);}catch(InvalidDataException ex){mediaError=ex.Message;}
                        Write(name+"-media.json",new{decodedBytes=partial.Length,decoderError=mediaError});
                        Check(partial.Length<64*32*4*5,"Failed job must not produce a complete five-frame video");
                    }
                    // Native progress stays open at completion. Closing that completed
                    // public view model allows the outer output command to present its error.
                    Log(name+" close-completed-native-progress-before-error-observation");
                    Execute(progress!,"CloseCommand");progressClosed=true;
                    // IsCompleted means terminal, not successful. Require native error presentation too.
                    try{await Until(()=>current.NativeErrorDialogs>0,TimeSpan.FromSeconds(10));}
                    catch(TimeoutException)
                    {
                        errorPresentationOpen=true;
                        Write(name+"-error-presentation-open.json",new{status="OPEN",required="Native output error presentation",observedOwnedStandardErrorDialogs=current.NativeErrorDialogs,visibleUi=VisibleText()});
                        Log(name+" error presentation OPEN: no matching owned standard dialog observed; do not count Source error as native presentation");
                    }
                    if(current.NativeErrorDialogs>0)Check(true,"Native writer presents its output error");
                }
                else
                {
                    Check(nativeToken.IsCancellationRequested,"Native cancellation must remain requested");
                    if(File.Exists(current.File)&&new FileInfo(current.File).Length>0)
                    {
                        byte[] partial=[];string mediaError="";
                        try{partial=await Decode(current.File!);}catch(InvalidDataException ex){mediaError=ex.Message;}
                        Write("cancel-media-after-test-release.json",new{decodedBytes=partial.Length,decoderError=mediaError,waitInterruptedByNativeCancel=current.ImmediateCancel});
                        Check(partial.Length<64*32*4*5,"Canceled job must not produce a complete five-frame video");
                    }
                }
                object evidence;lock(gate)evidence=new{name,writer=plugin.GetType().FullName,completed=Value(progress,"IsCompleted"),message=Public(progress,"Message"),nativeCancel=nativeToken.IsCancellationRequested,
                    current.ImmediateCancel,current.Cancellations,current.NativeErrorDialogs,compilerCount=service.CompilationCount,current.Requests,current.Frames,current.Errors,current.Disposals,
                    outputFile=Path.GetFileName(current.File),exists=File.Exists(current.File),bytes=File.Exists(current.File)?new FileInfo(current.File).Length:0};
                cases.Add(evidence);Write(name+"-job.json",evidence);if(!progressClosed)Execute(progress!,"CloseCommand");
                await Until(()=>Find(main!,"ProgressViewModel") is null);current.File=null;stage=null;
                service.Dispose();
            }
            var cancelEvidence=File.ReadAllText(Path.Combine(output,"cancel-wait-boundary.json"));
            var nativeCancelProved=cancelEvidence.Contains("\"sourceDisposeWithinThreeSeconds\": true")&&cancelEvidence.Contains("\"preparationWaitCanceled\": true");
            status=nativeCancelProved&&!errorPresentationOpen?"PASS_NORMAL_WRITER_WITH_NATIVE_CANCEL":"PARTIAL_NORMAL_WRITER_BOUNDARIES_OPEN";
        }
        catch(Exception ex)
        {
            error=ex.ToString();
            if(assertions==0)status="BLOCKED_BEFORE_NATIVE_OUTPUT";
            else if(stage is { } blocked){lock(gate)if(blocked.Requests.Count==0)status="BLOCKED_BEFORE_EXPORT_SOURCE";}
            Log(status+" "+error);
        }
        finally
        {
            stage?.Release.TrySetResult();stage?.ContinueFrame.TrySetResult();confirmations.Stop();dialogsStop.Cancel();
            if(stage is not null&&nativeProgress is not null)
            {
                try{await Until(()=>Equals(Value(nativeProgress,"IsCompleted"),true),TimeSpan.FromSeconds(10));Log("harness-cleanup-native-terminal-after-gate-release");}
                catch(TimeoutException){Log("harness-cleanup-native-terminal-NOT-PROVEN");}
            }
            if(stage is { } active)lock(gate)Write("active-stage-on-exit.json",new{active.Name,active.File,active.Requests,active.Frames,active.Errors,active.Disposals,active.Cancellations,compilerCount=active.Service.CompilationCount,nativeCompleted=Value(nativeProgress,"IsCompleted"),nativeMessage=Public(nativeProgress,"Message")});
            try{await automation;}catch(OperationCanceledException){}catch(Exception ex){status="FAIL";error+="\nOwned dialog automation: "+ex;}
            CompiledTachieSource.ProofPreparation=null;CompiledTachieSource.ProofLifecycle=null;CompiledTachieSource.ProofHostRequest=null;CompiledTachieSource.ProofHostUpdate=null;CompiledTachieSource.ProofHostError=null;
            foreach(var service in services)service.Dispose();
            Write("export-results.json",new{status,assertions,error,hostVersion=typeof(Project).Assembly.GetName().Version?.ToString(),sourceHead=Environment.GetEnvironmentVariable("SOURCE_HEAD"),
                productAssemblySha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(CompiledTachieSource).Assembly.Location))).ToLowerInvariant(),cases,
                errorPresentationOpen,customWriterUsed=false,privateHostMethodsCalled=false,userAssetsUsed=false,policyChanged=false});
            File.WriteAllText(Path.Combine(output,"export-complete.txt"),status);
        }
    }
    private static object[] VisibleText()
    {
        var result=new List<object>();
        foreach(Window window in Application.Current.Windows)
        {
            var texts=ReadVisibleTexts(window);
            result.Add(new{window.Title,dataContext=window.DataContext?.GetType().FullName,texts});
        }
        return result.ToArray();
    }
    private static string[] ReadVisibleTexts(DependencyObject root)
    {
        var texts=new List<string>();var pending=new Stack<DependencyObject>();pending.Push(root);var visited=0;
        while(pending.Count>0&&visited++<5000)
        {
            var node=pending.Pop();
            if(node is UIElement {IsVisible:false})continue;
            var text=node switch{TextBlock block=>block.Text,TextBox box=>box.Text,_=>null};
            if(!string.IsNullOrWhiteSpace(text))texts.Add(text);
            for(var index=0;index<VisualTreeHelper.GetChildrenCount(node);index++)pending.Push(VisualTreeHelper.GetChild(node,index));
        }
        return texts.ToArray();
    }
    private static async Task<byte[]> Decode(string path)
    {
        var executable=Path.Combine(AppContext.BaseDirectory,"Resources","bin","x64","ffmpeg","ffmpeg.exe");
        if(!File.Exists(executable))throw new InvalidOperationException("Official bundled FFmpeg missing; no download is permitted");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-v","error","-i",path,"-f","rawvideo","-pix_fmt","bgra","pipe:1"})start.ArgumentList.Add(argument);
        using var process=Process.Start(start)!;using var bytes=new MemoryStream();var errors=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try{await process.StandardOutput.BaseStream.CopyToAsync(bytes,timeout.Token);await process.WaitForExitAsync(timeout.Token);}
        catch(OperationCanceledException){if(!process.HasExited)process.Kill(entireProcessTree:true);throw new TimeoutException("Owned bundled FFmpeg decoder timed out");}
        if(process.ExitCode!=0)throw new InvalidDataException(await errors);return bytes.ToArray();
    }
    private static async Task Until(Func<bool> condition,TimeSpan? timeout=null)
    {var timer=Stopwatch.StartNew();while(!condition()){if(timer.Elapsed>(timeout??TimeSpan.FromSeconds(30)))throw new TimeoutException("Native output condition timed out");await Task.Delay(50);}}
}
