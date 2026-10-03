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
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int length);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,UIntPtr wParam,IntPtr lParam,uint flags,uint timeout,out UIntPtr result);
    [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window,uint message,UIntPtr wParam,string lParam,uint flags,uint timeout,out UIntPtr result);
    private static string ReplaceOwnedFilename(AutomationElement edit,string path)
    {
        var handle=(IntPtr)edit.Current.NativeWindowHandle;
        GetWindowThreadProcessId(handle,out var process);
        var kind=new StringBuilder(100);GetClassName(handle,kind,100);
        if(handle==IntPtr.Zero||process!=(uint)Environment.ProcessId||kind.ToString()!="Edit")
            throw new InvalidOperationException("Filename native edit ownership/class was not established; no messages sent.");
        // Documented edit selection/replacement messages; no global keyboard input or host internals.
        if(SendMessageTimeout(handle,0x00B1,UIntPtr.Zero,(IntPtr)(-1),0x0002,2000,out _)==IntPtr.Zero
            ||SendMessageTimeout(handle,0x00C2,(UIntPtr)1,path,0x0002,2000,out _)==IntPtr.Zero)
            throw new InvalidOperationException("Owned filename edit message timed out or failed.");
        var actual=new StringBuilder(path.Length+100);GetWindowText(handle,actual,actual.Capacity);return actual.ToString();
    }
    internal static Task Run(Func<(string? Path,bool ExpectedError)> state,Action<string> log,CancellationToken token)=>Task.Run(async()=>
    {
        var submitted=new HashSet<(IntPtr Handle,string Path)>();
        var observed=new Dictionary<IntPtr,string>();
        var associationDeclined=false;
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
                    var body=System.Text.RegularExpressions.Regex.Replace(string.Join(" ",texts),@"\s+"," ").Trim();
                    var signature=System.Text.Json.JsonSerializer.Serialize(new{handle=handle.ToInt64(),title=root.Current.Name,enabled=root.Current.IsEnabled,buttons,texts});
                    if(!observed.TryGetValue(handle,out var previous)||previous!=signature)
                    {observed[handle]=signature;log("native-dialog-state "+signature);}
                    // A new isolated executable path can trigger association setup even when
                    // another validation host is initialized. Decline only this exact prompt;
                    // accepting it would change normal system file associations.
                    const string associationPrompt="YMM4用の拡張子がゆっくりMovieMaker4に関連付けられていません。 以下の拡張子を関連付けしますか？ - .ymmp: プロジェクトファイル - .ymmt: テンプレートファイル - .ymme: プラグインファイル 関連付けると、各ファイルをダブルクリックしてYMM4を起動できるようになります。";
                    if(root.Current.IsEnabled&&root.Current.Name=="確認"&&body==associationPrompt)
                    {
                        var decline=root.FindFirst(TreeScope.Descendants,new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                            new PropertyCondition(AutomationElement.AutomationIdProperty,"7")));
                        if(decline?.Current.IsEnabled==true&&decline.Current.Name.Contains("いいえ")
                            &&submitted.Add((handle,"association-decline")))
                        {associationDeclined=true;log("native-isolated-association-declined");((InvokePattern)decline.GetCurrentPattern(InvokePattern.Pattern)).Invoke();}
                        return true;
                    }
                    const string associationNotice="今後、関連付けしたい場合は ヘルプ(H) → YMM4用拡張子の関連付け → 登録する を実行してください。";
                    if(associationDeclined&&root.Current.IsEnabled&&root.Current.Name=="通知"&&body==associationNotice)
                    {
                        var acknowledge=root.FindFirst(TreeScope.Descendants,new AndCondition(
                            new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                            new PropertyCondition(AutomationElement.AutomationIdProperty,"2")));
                        if(acknowledge?.Current.IsEnabled==true&&acknowledge.Current.Name=="OK"
                            &&submitted.Add((handle,"association-decline-notice")))
                        {log("native-isolated-association-notice-acknowledged");((InvokePattern)acknowledge.GetCurrentPattern(InvokePattern.Pattern)).Invoke();}
                        return true;
                    }
                    var save=root.FindFirst(TreeScope.Descendants,new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                        new PropertyCondition(AutomationElement.AutomationIdProperty,"1")));
                    if(current.Path is not null&&save?.Current.Name.Contains("保存")==true
                        &&root.Current.IsEnabled&&save.Current.IsEnabled&&!submitted.Contains((handle,current.Path)))
                    {
                        var edits=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit))
                            .Cast<AutomationElement>().Select(x=>new{name=x.Current.Name,id=x.Current.AutomationId,
                                parent=TreeWalker.ControlViewWalker.GetParent(x)?.Current.AutomationId,
                                value=x.TryGetCurrentPattern(ValuePattern.Pattern,out var value)?((ValuePattern)value).Current.Value:null}).ToArray();
                        log("native-save-edit-inventory "+System.Text.Json.JsonSerializer.Serialize(edits));
                        // Resolve the filename host rather than relying on an edit ID being
                        // globally unique across a common dialog's provider tree.
                        var filenameHost=root.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty,"FileNameControlHost"));
                        if(filenameHost is null)throw new InvalidOperationException("Owned Save filename host was not found; do not submit an ambiguous dialog.");
                        var matches=filenameHost.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit))
                            .Cast<AutomationElement>().ToArray();
                        if(matches.Length!=1)throw new InvalidOperationException("Owned Save filename edit is not unique; do not submit.");
                        var edit=matches[0];
                        if(edit?.Current.IsEnabled==true&&edit.TryGetCurrentPattern(ValuePattern.Pattern,out var pattern)==true)
                        {
                            edit.SetFocus();
                            log("native-save-filename-focused "+edit.Current.HasKeyboardFocus);
                            if(!edit.Current.HasKeyboardFocus)throw new InvalidOperationException("Owned Save filename editor did not receive focus; do not submit.");
                            var nativeValue=ReplaceOwnedFilename(edit,current.Path);
                            log("native-save-edit-text "+nativeValue);
                            if(!string.Equals(nativeValue,current.Path,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Native filename text differs; do not submit.");
                            var actual=((ValuePattern)pattern).Current.Value;
                            log("native-save-value "+actual);
                            if(!string.Equals(actual,current.Path,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Owned Save filename did not accept the requested path.");
                            save.SetFocus();
                            log("native-save-button-focused "+save.Current.HasKeyboardFocus);
                            // A provider may keep the dialog handle alive but disabled after submission.
                            // Never rewrite its value or submit again while the normal job starts.
                            submitted.Add((handle,current.Path));
                            log("native-save-dialog "+Path.GetFileName(current.Path));((InvokePattern)save.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                            log("native-save-submit-returned "+Path.GetFileName(current.Path));
                        }
                    }
                    else if(current.ExpectedError)
                    {
                        if(texts.Any(text=>text.Contains("動画")&&text.Contains("出力")))
                        {
                            var ok=root.FindAll(TreeScope.Descendants,new AndCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button),
                                new PropertyCondition(AutomationElement.NameProperty,"OK"))).Cast<AutomationElement>()
                                .Where(x=>x.Current.IsEnabled).ToArray();
                            if(ok.Length==1&&submitted.Add((handle,"output-error")))
                            {log("native-output-error-dialog "+string.Join(" | ",texts));((InvokePattern)ok[0].GetCurrentPattern(InvokePattern.Pattern)).Invoke();}
                        }
                    }
                }
                catch(ElementNotAvailableException){}catch(InvalidOperationException ex){log("dialog-provider "+ex.Message);}
                return true;
            };
            EnumWindows(callback,IntPtr.Zero);await Task.Delay(100,token);
        }
    },token);
}
