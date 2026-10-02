namespace PsdTachieNext.Core;

/// <summary>One Source export episode, not a native writer job ID. No host/GPU objects or I/O.
/// Invalidation never joins the rendering owner or preparation worker. Retired episodes stay isolated.</summary>
public sealed class ExportRequestScope
{
    private readonly object gate=new();
    private readonly SourceAssetRef source;
    private readonly string path;
    private readonly long revision,settings;
    private RequestStamp? request;
    private int invalidated;
    public ExportRequestScope(SourceAssetRef source,long revision,long settingsRevision)
    {source.Validate();this.source=source;path=Path.GetFullPath(source.Path);this.revision=revision;settings=settingsRevision;}
    public void Bind(RequestStamp stamp)
    {
        lock(gate)
        {
            if(request is not null)throw new InvalidOperationException("Export request is already bound.");
            if(stamp.SourceRevision!=revision||stamp.SettingsRevision!=settings)throw new InvalidOperationException("Export request does not match its captured revisions.");
            request=stamp;
        }
    }
    public void Invalidate(string changedPath,long changedRevision)
    {
        if(changedRevision>revision&&string.Equals(path,Path.GetFullPath(changedPath),
            OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))Interlocked.Exchange(ref invalidated,1);
    }
    public void RejectChange()=>Interlocked.Exchange(ref invalidated,1);
    public void Validate(SourceAssetRef? currentSource,RequestStamp stamp,long currentRevision,long currentSettings)
    {
        bool matches;lock(gate)matches=request==stamp&&source==currentSource&&revision==currentRevision&&settings==currentSettings;
        if(!matches)RejectChange();
        if(Volatile.Read(ref invalidated)!=0)
            throw new IOException("動画出力中の素材参照・revision・要求が変更されました。出力を中断して再実行してください。");
    }
}
