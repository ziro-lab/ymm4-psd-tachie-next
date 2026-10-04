using System.IO;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;
using PsdTachieNext.Ymm4;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var host=Path.GetFullPath(Environment.GetEnvironmentVariable("YMM4_COMPONENT_HOST") ?? throw new InvalidOperationException("Set YMM4_COMPONENT_HOST to the authorized validation host."));
        AssemblyLoadContext.Default.Resolving+=(context,name)=>
        {
            var file=Path.Combine(host,name.Name+".dll");
            return File.Exists(file)?context.LoadFromAssemblyPath(file):null;
        };
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.BeginInvoke(new Action(()=>_=Run(app,args[0])),DispatcherPriority.Normal);
        app.Run();
    }
    private static async Task Run(Application app,string output)
    {
        var checks=new List<object>();var states=new List<object>();var assertions=0;string? error=null;
        var result="FAIL_WPF_COMPONENT_LIFETIME";
        try
        {
            using var a=new PsdPaletteViewModel();using var b=new PsdPaletteViewModel();
            var view=new PsdPaletteView{DataContext=a};
            Check(!view.IsLoaded && Released(a),"pre-load DataContext suspends A subscriptions/interests");
            // Managed WPF presentation source; WS_POPUP without WS_VISIBLE. No user input or YMM4.
            using var presentation=new HwndSource(new HwndSourceParameters("Synthetic palette lifetime component")
                {Width=400,Height=300,WindowStyle=unchecked((int)0x80000000)});
            presentation.RootVisual=view;
            view.Measure(new Size(400,300));view.Arrange(new Rect(0,0,400,300));
            await Until(()=>view.IsLoaded && Active(a),"actual WPF Loaded A");
            Capture("loaded-A",view,a,b);
            Check(view.IsLoaded && Active(a),"real Loaded activates and subscribes A");
            view.DataContext=b;
            await Until(()=>Released(a)&&Active(b),"loaded DataContext A to B");
            Capture("loaded-A-to-B",view,a,b);
            Check(Released(a) && Active(b),"loaded swap releases A before B subscription");
            view.DataContext=a;
            await Until(()=>Active(a)&&Released(b),"loaded DataContext B to A");
            Check(Active(a)&&Released(b),"loaded swap releases B before A subscription");
            presentation.RootVisual=null;
            await Until(()=>!view.IsLoaded&&Released(a),"actual WPF Unloaded A");
            Capture("unloaded-A",view,a,b);
            Check(!view.IsLoaded&&Released(a)&&Released(b),"real Unloaded releases all current subscriptions/interests");
            presentation.RootVisual=view;
            await Until(()=>view.IsLoaded&&Active(a),"actual WPF reload A");
            Check(view.IsLoaded&&Active(a),"reloaded component resumes current VM");
            a.Dispose();
            Check(Released(a),"Dispose while loaded releases subscription/interests");
            a.Resume();
            Check(Released(a),"disposed VM cannot resubscribe on Resume");
            view.DataContext=b;
            await Until(()=>Active(b),"loaded swap away from disposed VM");
            Check(Active(b)&&Released(a),"loaded swap accepts live B and retains disposed A release");
            b.Dispose();
            Check(Released(b),"Dispose current B releases subscription/interests");
            presentation.RootVisual=null;
            await Until(()=>!view.IsLoaded,"final component detach");
            Capture("disposed-unloaded",view,a,b);
            Check(Released(a)&&Released(b),"final detach retains released state");
            result="PASS_WPF_COMPONENT_SUBSCRIPTION_LIFETIME";
        }
        catch(Exception ex){error=ex.ToString();}
        File.WriteAllText(output,System.Text.Json.JsonSerializer.Serialize(new{status=result,assertions,error,checks,states,
            actualWpfLoadedUnloaded=true,hiddenPresentation=true,syntheticComponentOnly=true,
            sourcePreparedLeaseAcquisitionExercised=false,actualHost=false,physicalInput=false,visualAcceptance=false,
            notProven=new[]{"prepared document/asset/watch leases with live TimelineToolInfo","native loaded product view","visual layout"}},
            new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
        app.Shutdown(error is null?0:1);
        void Check(bool pass,string label){assertions++;checks.Add(new{label,passed=pass});if(!pass)throw new InvalidOperationException(label);}
        void Capture(string label,PsdPaletteView view,PsdPaletteViewModel a,PsdPaletteViewModel b)
            =>states.Add(new{label,view.IsLoaded,A=a.ProofLifetime,B=b.ProofLifetime});
    }
    private static bool Active(PsdPaletteViewModel vm)
    {
        var s=JObject.FromObject(vm.ProofLifetime);return s["active"]!.Value<bool>()&&s["subscribed"]!.Value<bool>();
    }
    private static bool Released(PsdPaletteViewModel vm)
    {
        var s=JObject.FromObject(vm.ProofLifetime);return !s["active"]!.Value<bool>()&&!s["subscribed"]!.Value<bool>()
            &&!s["hasDocument"]!.Value<bool>()&&!s["hasAsset"]!.Value<bool>()&&!s["hasWatch"]!.Value<bool>();
    }
    private static async Task Until(Func<bool> condition,string label)
    {
        var deadline=DateTime.UtcNow.AddSeconds(5);
        while(!condition()){if(DateTime.UtcNow>=deadline)throw new TimeoutException(label);await Task.Delay(20);}
    }
}

