using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace PsdTachieNext.HostProof;

/// <summary>UI Automation of the owned host's standard Save dialog only; no generic confirmation clicks.</summary>
internal static class OwnedExportDialogs
{
    private delegate bool EnumProc(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder name,int length);
    internal static Task Run(Func<(string? Path,bool ExpectedError)> state,Action<string> log,CancellationToken token)=>Task.Run(async()=>
    {
        var submitted=new HashSet<(IntPtr Handle,string Path)>();
        var observed=new Dictionary<IntPtr,string>();
        while(!token.IsCancellationRequested)
        {
            var current=state();
            EnumProc callback=(handle,_)=>
            {
                GetWindowThreadProcessId(handle,out var process);if(process!=(uint)Environment.ProcessId)return true;
                var kind=new StringBuilder(100);GetClassName(handle,kind,100);if(kind.ToString()!="#32770")return true;
                try
                {
                    var root=AutomationElement.FromHandle(handle);
                    var buttons=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button))
                        .Cast<AutomationElement>().Select(x=>new{name=x.Current.Name,id=x.Current.AutomationId,enabled=x.Current.IsEnabled}).ToArray();
                    var texts=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text))
                        .Cast<AutomationElement>().Select(x=>x.Current.Name).ToArray();
                    var signature=System.Text.Json.JsonSerializer.Serialize(new{handle=handle.ToInt64(),title=root.Current.Name,enabled=root.Current.IsEnabled,buttons,texts});
                    if(!observed.TryGetValue(handle,out var previous)||previous!=signature)
                    {observed[handle]=signature;log("native-dialog-state "+signature);}
                    var save=root.FindFirst(TreeScope.Descendants,new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                        new PropertyCondition(AutomationElement.AutomationIdProperty,"1")));
                    if(current.Path is not null&&save?.Current.Name.Contains("保存")==true
                        &&root.Current.IsEnabled&&save.Current.IsEnabled&&!submitted.Contains((handle,current.Path)))
                    {
                        var edit=root.FindFirst(TreeScope.Descendants,new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit),
                            new PropertyCondition(AutomationElement.AutomationIdProperty,"1001")));
                        if(edit?.Current.IsEnabled==true&&edit.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)==true)
                        {
                            ((ValuePattern)pattern).SetValue(current.Path);
                            var actual=((ValuePattern)pattern).Current.Value;
                            log("native-save-value "+actual);
                            if(!string.Equals(actual,current.Path,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Owned Save filename did not accept the requested path.");
                            // A provider may keep the dialog handle alive but disabled after submission.
                            // Never rewrite its value or submit again while the normal job starts.
                            submitted.Add((handle,current.Path));
                            log("native-save-dialog "+Path.GetFileName(current.Path));((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                            log("native-save-submit-returned "+Path.GetFileName(current.Path));
                        }
                    }
                    else if(current.ExpectedError&&save?.Current.Name=="OK")
                    {
                        if(texts.Any(text=>text.Contains("動画")&&text.Contains("出力")))
                        {log("native-output-error-dialog "+string.Join(" | ",texts));((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();}
                    }
                }
                catch(ElementNotAvailableException){}catch(InvalidOperationException ex){log("dialog-provider "+ex.Message);}
                return true;
            };
            EnumWindows(callback,IntPtr.Zero);await Task.Delay(100,token);
        }
    },token);
}
