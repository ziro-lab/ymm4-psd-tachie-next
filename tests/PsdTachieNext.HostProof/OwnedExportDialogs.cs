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
                    var save=root.FindFirst(TreeScope.Descendants,new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                        new PropertyCondition(AutomationElement.AutomationIdProperty,"1")));
                    if(current.Path is not null&&save?.Current.Name.Contains("保存")==true)
                    {
                        var edit=root.FindFirst(TreeScope.Descendants,new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit),
                            new PropertyCondition(AutomationElement.AutomationIdProperty,"1001")));
                        if(edit?.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)==true)
                        {
                            ((ValuePattern)pattern).SetValue(current.Path);
                            log("native-save-value "+((ValuePattern)pattern).Current.Value);
                            log("native-save-dialog "+Path.GetFileName(current.Path));((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        }
                    }
                    else if(current.ExpectedError&&save?.Current.Name=="OK")
                    {
                        var texts=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text))
                            .Cast<AutomationElement>().Select(x=>x.Current.Name).ToArray();
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
