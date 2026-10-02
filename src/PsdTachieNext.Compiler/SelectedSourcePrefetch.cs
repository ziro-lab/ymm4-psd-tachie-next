using PsdTachieNext.Core;

namespace PsdTachieNext.Compiler;

public sealed record PrefetchSnapshot(string? SelectedPath, bool Preparing, bool Ready, long RetainedDecodedBytes,
    long Started, long Promoted, string? Error);

/// <summary>One selected source only. Selection never reads files or waits for work.
/// Owns speculative CPU leases; promotion transfers ownership to the real consumer.
/// Expiry releases memory/read pins, never deletes the persistent compiled generation.</summary>
public sealed class SelectedSourcePrefetch : IDisposable
{
    private sealed class Entry(SourceAssetRef source,long revision,long selectedAt)
    {
        internal readonly SourceAssetRef Source=source;
        internal readonly long Revision=revision,SelectedAt=selectedAt;
        internal readonly CancellationTokenSource Cancellation=new();
        private readonly object cancellationGate=new();
        private bool finished;
        internal Task Work=Task.CompletedTask;
        internal PreparedAppearanceLease? Ready;
        internal IDisposable? Watch;
        internal long Bytes;
        internal void Cancel(){lock(cancellationGate)if(!finished)Cancellation.Cancel();}
        internal void Finish(){lock(cancellationGate){finished=true;Cancellation.Dispose();}}
    }
    private readonly object gate=new();
    private readonly SourcePreparationService preparation;
    private readonly SharedDocumentPool pool;
    private readonly TimeProvider clock;
    private readonly TimeSpan unusedLifetime;
    private readonly long maximumDecodedBytes;
    private readonly ITimer timer;
    private readonly HashSet<Task> pending=[];
    private Entry? current;
    private SourceAssetRef? deferred;
    private int actualPreparations;
    private bool disposed;
    private long started,promoted;
    private Exception? error;

    public SelectedSourcePrefetch(SourcePreparationService preparation,SharedDocumentPool pool,
        TimeSpan unusedLifetime,long maximumDecodedBytes,TimeProvider? clock=null)
    {
        ArgumentNullException.ThrowIfNull(preparation);ArgumentNullException.ThrowIfNull(pool);
        if(unusedLifetime<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(unusedLifetime));
        if(maximumDecodedBytes<=0)throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
        this.preparation=preparation;this.pool=pool;this.unusedLifetime=unusedLifetime;
        this.maximumDecodedBytes=maximumDecodedBytes;this.clock=clock??TimeProvider.System;
        preparation.SourceInvalidated+=Invalidated;
        timer=this.clock.CreateTimer(_=>
        {
            var expiry=ExpireUnusedAsync();
            _=expiry.ContinueWith(t=>_=t.Exception,CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        },null,TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(1));
    }
    private static bool SamePath(string a,string b)=>string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),
        OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    private bool Expired(Entry entry)=>clock.GetElapsedTime(entry.SelectedAt)>=unusedLifetime;
    public bool Matches(SourcePreparationService service,SharedDocumentPool documents)=>ReferenceEquals(preparation,service)&&ReferenceEquals(pool,documents);
    public PrefetchSnapshot Snapshot()
    {
        lock(gate)return new(current?.Source.Path,current is not null&&!current.Work.IsCompleted,
            current?.Ready is not null,current?.Ready is not null?current.Bytes:0,started,promoted,error?.ToString());
    }
    public Task Select(SourceAssetRef? source)
    {
        source?.Validate();var revision=source is null?0:preparation.Revision(source.Path);
        Entry? previous;Task result;
        lock(gate)
        {
            if(disposed)return Task.CompletedTask;
            if(actualPreparations!=0){deferred=source;return Task.CompletedTask;}
            if(source is not null&&current is { } same&&SamePath(same.Source.Path,source.Path)
                &&same.Revision==revision&&!Expired(same))return same.Work;
            previous=current;current=null;error=null;
            if(source is null)result=Task.CompletedTask;
            else
            {
                var entry=new Entry(source,revision,clock.GetTimestamp());current=entry;started++;
                // Factory and watcher creation run off the caller/UI thread. Rapid stale requests
                // check cancellation before enrolling in the existing bounded single-flight service.
                result=entry.Work=Task.Run(()=>Warm(entry));Track(result);
            }
        }
        if(previous is not null)Retire(previous);
        return result;
    }
    private async Task Warm(Entry entry)
    {
        PreparedAppearanceLease? candidate=null;IDisposable? watch=null;
        try
        {
            var token=entry.Cancellation.Token;token.ThrowIfCancellationRequested();
            watch=preparation.Watch(entry.Source.Path);
            lock(gate)if(ReferenceEquals(current,entry)){entry.Watch=watch;watch=null;}
            token.ThrowIfCancellationRequested();
            using var asset=await preparation.PrepareAsync(entry.Source,entry.Revision,token).ConfigureAwait(false);
            candidate=await Task.Run(()=>asset.PrepareAppearanceWithinBudget(pool,maximumDecodedBytes,token:token),token).ConfigureAwait(false);
            if(preparation.Revision(entry.Source.Path)!=entry.Revision)
                throw new SourceChangedDuringPreparationException("Selected source changed during prefetch.");
            lock(gate)
            {
                if(!disposed&&ReferenceEquals(current,entry)&&!token.IsCancellationRequested&&!Expired(entry))
                {entry.Bytes=candidate.Plan.RequiredBlockIds.Sum(id=>candidate.Plan.Manifest.Blocks[id].RawLength);entry.Ready=candidate;candidate=null;}
            }
        }
        catch(Exception ex)
        {
            lock(gate)if(ReferenceEquals(current,entry)){error=ex is OperationCanceledException?null:ex;current=null;}
        }
        finally
        {
            candidate?.Dispose();watch?.Dispose();
            IDisposable? abandoned=null;
            lock(gate)if(!ReferenceEquals(current,entry)){abandoned=entry.Watch;entry.Watch=null;}
            abandoned?.Dispose();entry.Finish();
        }
    }
    /// <summary>Match content path/revision, not the project's independent logical AssetIdentity.</summary>
    public bool TryTakeReady(SourceAssetRef source,long revision,out PreparedAppearanceLease? ready)
    {
        Entry? taken;
        lock(gate)
        {
            taken=current;ready=null;
            if(disposed||taken?.Ready is null||taken.Revision!=revision||Expired(taken)||!SamePath(taken.Source.Path,source.Path))return false;
            ready=taken.Ready;taken.Ready=null;current=null;promoted++;
        }
        Retire(taken);return true;
    }
    /// <summary>Before real use, verify current bytes off-thread even if watcher revision/mtime
    /// has not changed. A speculative result must not bypass the normal input content boundary.</summary>
    public async Task<PreparedAppearanceLease?> TakeVerifiedReadyAsync(SourceAssetRef source,long revision,CancellationToken token=default)
    {
        if(!TryTakeReady(source,revision,out var ready))return null;
        try
        {
            await Task.Run(()=>
            {
                using var original=PsdCompiler.OpenOriginal(source.Path);var fingerprint=ready!.Plan.Manifest.Source;
                if(original.Length!=fingerprint.Length||PsdCompiler.HashStream(original,token)!=fingerprint.Sha256)
                    throw new SourceChangedDuringPreparationException("Original bytes changed after selected-source prefetch.");
            },token).ConfigureAwait(false);
            return ready;
        }
        catch(SourceChangedDuringPreparationException){ready!.Dispose();preparation.Invalidate(source.Path);throw;}
        catch{ready!.Dispose();throw;}
    }
    public sealed class ActualUse : IDisposable
    {
        private SelectedSourcePrefetch? owner;
        public Task Retirement { get; }
        internal ActualUse(SelectedSourcePrefetch owner,Task retirement){this.owner=owner;Retirement=retirement;}
        public void Dispose()=>Interlocked.Exchange(ref owner,null)?.EndActualUse();
    }
    /// <summary>Actual block admission takes priority. Selection remains nonblocking and retains
    /// only its latest deferred value while actual preparations hold this registration.</summary>
    public ActualUse BeginActualUse()
    {
        Entry? previous;lock(gate){actualPreparations++;previous=current;current=null;}
        if(previous is not null)Retire(previous);
        lock(gate)return new(this,Task.WhenAll(pending.ToArray()));
    }
    private void EndActualUse()
    {
        SourceAssetRef? next=null;
        lock(gate)if(--actualPreparations==0){if(!disposed)next=deferred;deferred=null;}
        if(next is not null)_=Select(next);
    }
    internal Task ExpireUnusedAsync()
    {
        Entry? expired=null;
        lock(gate)if(current is { } candidate&&Expired(candidate)){expired=candidate;current=null;}
        if(expired is not null)Retire(expired);
        lock(gate)return Task.WhenAll(pending.ToArray());
    }
    private void Invalidated(string path,long revision,SourceChangeReason reason)
    {
        Entry? previous=null;
        lock(gate)if(current is { } selected&&SamePath(path,selected.Source.Path)&&revision!=selected.Revision){previous=selected;current=null;}
        if(previous is not null)Retire(previous);
    }
    private void Retire(Entry entry)
    {
        PreparedAppearanceLease? ready;IDisposable? watch;
        lock(gate){ready=entry.Ready;entry.Ready=null;watch=entry.Watch;entry.Watch=null;}
        entry.Cancel();
        if(ready is not null||watch is not null)
        {
            var cleanup=Task.Run(()=>{try{ready?.Dispose();}finally{watch?.Dispose();}});
            lock(gate)Track(cleanup);
        }
    }
    // Called under gate. Completed history is removed; no character/request history is retained.
    private void Track(Task task)
    {
        pending.Add(task);
        _=task.ContinueWith(t=>{_=t.Exception;lock(gate)pending.Remove(t);},CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    public Task ShutdownCompletion{get{lock(gate)return Task.WhenAll(pending.ToArray());}}
    public void Dispose()
    {
        Entry? previous;lock(gate){if(disposed)return;disposed=true;previous=current;current=null;deferred=null;}
        timer.Dispose();preparation.SourceInvalidated-=Invalidated;
        if(previous is not null)Retire(previous);
    }
}
