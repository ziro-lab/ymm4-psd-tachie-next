namespace PsdTachieNext.Core;

public readonly record struct RequestStamp(Guid SessionId, long AssetRequestId, long SourceRevision,
    long SettingsRevision, long AppearanceRevision, long DeviceEpoch);
public enum PreparationState { Empty, Preparing, ReadyToRender, DisplayedCurrent, Failed, Disposed, UpdatingWithPrevious, FailedWithPrevious, UnstableSource }

/// <summary>Per-source CPU candidate ownership; workers never touch host or GPU objects.</summary>
public sealed class SourceSession : IDisposable
{
    private readonly object gate = new();
    private readonly Guid sessionId = Guid.NewGuid();
    private long requestId, deviceEpoch;
    private RequestStamp current;
    private sealed class CancellationOwner
    {
        private readonly object gate = new();
        private CancellationTokenSource? source = new();
        public CancellationToken Token { get { lock (gate) return source!.Token; } }
        public void Cancel() { lock (gate) source?.Cancel(); }
        public void Finish() { lock (gate) { source?.Dispose(); source = null; } }
    }
    private CancellationOwner? cancellation;
    private PreparedAppearanceLease? ready;
    private Exception? error;
    private PreparationState state;
    private Task completion = Task.CompletedTask;
    private bool hasDisplayed;
    private int retryCount;
    public int AutomaticRetryCount { get { lock (gate) return retryCount; } }
    internal Func<CancellationToken, Task>? BeforeRetry { get; set; }
    public PreparationDiagnostic? Diagnostic { get { lock (gate) return PreparationDiagnostic.From(error) is { } diagnostic
        ? diagnostic with { HasPreviousOutput = hasDisplayed } : null; } }
    public Task Completion { get { lock (gate) return completion; } }
    public PreparationState State { get { lock (gate) return state; } }
    public RequestStamp Current { get { lock (gate) return current; } }
    public Exception? Error { get { lock (gate) return error; } }
    // This is an owner notification seam, not a claim that YMM4 subscribes to it.
    public event Action<RequestStamp>? Ready;
    public RequestStamp Request(long sourceRevision, long settingsRevision, long appearanceRevision,
        Func<RequestStamp, CancellationToken, Task<PreparedAppearanceLease>> prepare,
        bool retryUnstable = false, Func<long>? readSourceRevision = null)
    {
        CancellationOwner? previous; PreparedAppearanceLease? old; RequestStamp stamp; CancellationOwner next;
        TaskCompletionSource done;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(state == PreparationState.Disposed, this);
            previous = cancellation; old = ready; ready = null; error = null;
            retryCount = 0;
            cancellation = next = new();
            current = stamp = new(sessionId, ++requestId, sourceRevision, settingsRevision, appearanceRevision, deviceEpoch);
            state = hasDisplayed ? PreparationState.UpdatingWithPrevious : PreparationState.Preparing;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously); completion = done.Task;
        }
        previous?.Cancel(); old?.Dispose();
        _ = CompleteAsync(stamp, next, prepare, done, retryUnstable, readSourceRevision);
        return stamp;
    }
    private async Task CompleteAsync(RequestStamp stamp, CancellationOwner owned,
        Func<RequestStamp, CancellationToken, Task<PreparedAppearanceLease>> prepare, TaskCompletionSource done,
        bool retryUnstable, Func<long>? readSourceRevision)
    {
        PreparedAppearanceLease? result = null;
        try
        {
            var token = owned.Token;
            for (var attempt = 0; ; attempt++)
            {
                try { result = await prepare(stamp, token).ConfigureAwait(false); break; }
                catch (SourceChangedDuringPreparationException) when (retryUnstable && attempt == 0)
                {
                    if (BeforeRetry is { } wait) await wait(token).ConfigureAwait(false);
                    else await Task.Delay(250, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var revision = readSourceRevision?.Invoke() ?? stamp.SourceRevision;
                    lock (gate)
                    {
                        if (state == PreparationState.Disposed || current != stamp) return;
                        current = stamp = stamp with { SourceRevision = revision };
                        retryCount = 1;
                    }
                }
            }
            Action<RequestStamp>? notify = null;
            lock (gate)
            {
                if (state != PreparationState.Disposed && current == stamp && !token.IsCancellationRequested)
                { ready = result; result = null; state = PreparationState.ReadyToRender; notify = Ready; }
            }
            notify?.Invoke(stamp);
        }
        catch (Exception ex)
        {
            lock (gate) if (state != PreparationState.Disposed && current == stamp)
            { error = ex; state = FailureState(ex); }
        }
        finally { result?.Dispose(); owned.Finish(); done.TrySetResult(); } // All exceptions are observed, including canceled/late workers.
    }
    public PreparedAppearanceLease? TakeReady(out RequestStamp stamp)
    {
        lock (gate) { stamp = current; var result = ready; ready = null; return result; }
    }
    /// <summary>CPU retry and post-draw stale rejection share one retry budget for the same logical request.</summary>
    public bool RetryChangedCandidate(RequestStamp stamp, long sourceRevision,
        Func<RequestStamp, CancellationToken, Task<PreparedAppearanceLease>> prepare)
    {
        CancellationOwner next; CancellationOwner? previous; PreparedAppearanceLease? old; TaskCompletionSource done;
        lock (gate)
        {
            if (state == PreparationState.Disposed || current != stamp || retryCount != 0) return false;
            retryCount = 1; previous = cancellation; cancellation = next = new(); old = ready; ready = null; error = null;
            current = stamp = stamp with { SourceRevision = sourceRevision };
            state = hasDisplayed ? PreparationState.UpdatingWithPrevious : PreparationState.Preparing;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously); completion = done.Task;
        }
        previous?.Cancel(); old?.Dispose();
        _ = CompleteAsync(stamp, next, prepare, done, false, null);
        return true;
    }
    public void Fail(RequestStamp stamp, Exception exception)
    {
        lock (gate) if (state != PreparationState.Disposed && current == stamp) { error = exception; state = FailureState(exception); }
    }
    private PreparationState FailureState(Exception exception) => exception is SourceChangedDuringPreparationException
        ? PreparationState.UnstableSource : hasDisplayed ? PreparationState.FailedWithPrevious : PreparationState.Failed;
    /// <summary>Caller serializes GPU operations; final stamp check and output switch form one source-owned interval.</summary>
    public bool TryPublish(RequestStamp stamp, Action publish)
    {
        lock (gate)
        {
            if (state == PreparationState.Disposed || current != stamp) return false;
            publish();
            if (current != stamp) return false;
            hasDisplayed = true; state = PreparationState.DisplayedCurrent; return true;
        }
    }
    public void ChangeDeviceEpoch()
    {
        Reset(false, true);
    }
    public void Clear() => Reset(false, false);
    private void Reset(bool dispose, bool changeEpoch)
    {
        CancellationOwner? pending; PreparedAppearanceLease? old;
        lock (gate)
        {
            if (state == PreparationState.Disposed) return;
            if (changeEpoch) deviceEpoch++;
            current = new(sessionId, ++requestId, 0, 0, 0, deviceEpoch);
            pending = cancellation; cancellation = null; old = ready; ready = null; error = null;
            hasDisplayed = false;
            state = dispose ? PreparationState.Disposed : PreparationState.Empty;
            if (dispose) Ready = null;
        }
        pending?.Cancel(); old?.Dispose();
    }
    public void Dispose() => Reset(true, false);
}
