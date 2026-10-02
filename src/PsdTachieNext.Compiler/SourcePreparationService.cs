using PsdTachieNext.Core;

namespace PsdTachieNext.Compiler;

public enum SourceChangeReason { ExternalHint, MonitoringError, Resume, ExplicitReload }

/// <summary>Path/revision single flight; content publication is serialized and reused by the repository.</summary>
public sealed class SourcePreparationService : IDisposable
{
    private sealed class Flight
    {
        public readonly CancellationTokenSource Worker = new();
        public Task<PreparedAssetLease> Task = null!;
        public int Waiters;
        public bool Retired;
        private readonly object workerGate = new();
        private bool workerDisposed;
        public void Cancel() { lock (workerGate) if (!workerDisposed) Worker.Cancel(); }
        public void Finish() { lock (workerGate) { if (workerDisposed) return; workerDisposed = true; Worker.Dispose(); } }
    }
    // One snapshot/parser job process-wide, including across service instances.
    private static readonly SemaphoreSlim CompilerSlot = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<(string Path, long Revision), Flight> flights = [];
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly Dictionary<string, long> revisions = new(PathComparer);
    private sealed class Registration(string path) { public string Path { get; } = path; public FileSystemWatcher? Watcher; public Exception? Error; }
    private readonly Dictionary<string, Registration> watchers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timer> debounce = new(PathComparer);
    private readonly Dictionary<string, SourceChangeReason> hintReasons = new(PathComparer);
    private readonly PsdCompiler compiler;
    private readonly CompiledAssetRepository repository;
    private readonly int maxPending;
    private bool disposed;
    public long CompilationCount => repository.CompilationCount;
    public event Action<string, long>? SourceChanged;
    public event Action<string, long, SourceChangeReason>? SourceInvalidated;
    internal Func<CancellationToken, Task>? BeforeSnapshot { get; set; }
    internal Func<SourceSnapshot, CancellationToken, Task>? AfterSnapshot { get; set; }
    internal bool ManualHints { get; set; }
    public SourcePreparationService(CompiledAssetRepository repository, PsdCompiler? compiler = null, int maxPending = 32)
    {
        if (maxPending <= 0) throw new ArgumentOutOfRangeException(nameof(maxPending));
        this.repository = repository; this.compiler = compiler ?? new(); this.maxPending = maxPending;
    }
    private static string Normalize(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
    public long Revision(string path)
    {
        path = Normalize(path);
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); return revisions.GetValueOrDefault(path); }
    }
    public long Invalidate(string path, SourceChangeReason reason = SourceChangeReason.ExplicitReload)
    {
        path = Normalize(path); long revision; Action<string, long>? notify; Action<string, long, SourceChangeReason>? detailed;
        lock (gate)
        {
            if (disposed) return -1;
            revisions[path] = revision = checked(revisions.GetValueOrDefault(path) + 1); notify = SourceChanged; detailed = SourceInvalidated;
        }
        notify?.Invoke(path, revision); detailed?.Invoke(path, revision, reason); return revision;
    }
    public IDisposable Watch(string path)
    {
        path = Normalize(path); var registration = new Registration(path); var key = Guid.NewGuid().ToString("N");
        lock (gate) { ObjectDisposedException.ThrowIf(disposed, this); revisions.TryAdd(path, 0); watchers.Add(key, registration); }
        Arm(registration);
        return new Subscription(() =>
        {
            FileSystemWatcher? old;
            lock (gate) { watchers.Remove(key); old = registration.Watcher; registration.Watcher = null; }
            old?.Dispose();
        });
    }
    private void Arm(Registration registration)
    {
        // OS watcher creation happens outside the shared service lock.
        FileSystemWatcher? candidate = null;
        try
        {
        candidate = new FileSystemWatcher(Path.GetDirectoryName(registration.Path)!) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
        candidate.Changed += (_, e) => Schedule(e.FullPath);
        candidate.Created += (_, e) => Schedule(e.FullPath);
        candidate.Deleted += (_, e) => Schedule(e.FullPath);
        candidate.Renamed += (_, e) => { Schedule(e.OldFullPath); Schedule(e.FullPath); };
        candidate.Error += (_, e) =>
        { lock (gate) registration.Error = e.GetException(); NotifyOverflow(); };
        candidate.EnableRaisingEvents = true;
        lock (gate)
        {
            if (disposed || !watchers.Values.Contains(registration)) return;
            var old = registration.Watcher; registration.Watcher = candidate; candidate = old; registration.Error = null;
        }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { lock (gate) registration.Error = ex; }
        finally { candidate?.Dispose(); }
    }
    /// <summary>Called on public host activation/resume, never every frame. Rearms failed watches and checks content next use.</summary>
    public void RecheckWatchedSources()
    {
        Registration[] registered;
        lock (gate) { if (disposed) return; registered = watchers.Values.ToArray(); }
        foreach (var registration in registered)
        {
            bool broken; lock (gate) broken = registration.Error is not null || registration.Watcher is null;
            if (broken) Arm(registration);
        }
        foreach (var path in registered.Select(r => r.Path).Distinct(PathComparer)) Invalidate(path, SourceChangeReason.Resume);
    }
    public Exception? MonitoringError(string path)
    {
        path = Normalize(path);
        lock (gate) return watchers.Values.FirstOrDefault(r => r.Path == path && r.Error is not null)?.Error;
    }
    private void Schedule(string path, SourceChangeReason reason = SourceChangeReason.ExternalHint)
    {
        path = Normalize(path);
        lock (gate)
        {
            if (disposed || !revisions.ContainsKey(path)) return;
            if (!hintReasons.ContainsKey(path) || reason == SourceChangeReason.MonitoringError) hintReasons[path] = reason;
            if (debounce.TryGetValue(path, out var pending)) { if (!ManualHints) pending.Change(250, Timeout.Infinite); return; }
            Timer? timer = null;
            timer = new Timer(_ => ExpireHint(path, timer!), null, Timeout.Infinite, Timeout.Infinite);
            debounce[path] = timer; if (!ManualHints) timer.Change(250, Timeout.Infinite);
        }
    }
    private void ExpireHint(string path, Timer timer)
    {
        SourceChangeReason reason;
        lock (gate)
        {
            if (debounce.GetValueOrDefault(path) != timer) return;
            debounce.Remove(path);
            reason = hintReasons[path]; hintReasons.Remove(path);
        }
        timer.Dispose(); Invalidate(path, reason);
    }
    internal void NotifyHint(string path) => Schedule(path);
    internal void NotifyOverflow()
    {
        string[] all; lock (gate) all = revisions.Keys.ToArray(); foreach (var path in all) Schedule(path, SourceChangeReason.MonitoringError);
    }
    internal void FlushHint(string path)
    {
        path = Normalize(path); Timer? pending;
        lock (gate) pending = debounce.GetValueOrDefault(path);
        if (pending is not null) ExpireHint(path, pending);
    }
    public async Task<PreparedAssetLease> PrepareAsync(SourceAssetRef source, long revision, CancellationToken waiterCancellation = default)
    {
        source.Validate(); waiterCancellation.ThrowIfCancellationRequested();
        var path = Normalize(source.Path); var key = (path, revision); Flight flight;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            revisions.TryAdd(path, revision);
            if (!flights.TryGetValue(key, out flight!))
            {
                if (flights.Count >= maxPending) throw new CacheCapacityException("素材準備キューが満杯です。不要な要求を取消して再読込してください。");
                flight = new Flight { Waiters = 1 }; flights.Add(key, flight);
                flight.Task = Task.Run(async () =>
                {
                    await CompilerSlot.WaitAsync(flight.Worker.Token).ConfigureAwait(false);
                    try
                    {
                        repository.Initialize();
                        using var admission = repository.BeginPreparation(source.Path, flight.Worker.Token);
                        if (BeforeSnapshot is { } before) await before(flight.Worker.Token).ConfigureAwait(false);
                        using var snapshot = compiler.CreateSnapshot(source.Path, repository.SnapshotRoot, flight.Worker.Token, admission.SnapshotBudget);
                        if (AfterSnapshot is { } after) await after(snapshot, flight.Worker.Token).ConfigureAwait(false);
                        var result = repository.Prepare(snapshot, compiler, flight.Worker.Token, lockHeld: true);
                        try
                        {
                            flight.Worker.Token.ThrowIfCancellationRequested();
                            if (Revision(source.Path) != revision) throw new SourceChangedDuringPreparationException("素材revisionが変更されました。古い準備結果は使用しません。");
                            return result;
                        }
                        catch { result.Dispose(); throw; }
                    }
                    finally { CompilerSlot.Release(); }
                });
                _ = flight.Task.ContinueWith(t => { _ = t.Exception; Retire(key, flight); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else flight.Waiters++;
        }
        try
        {
            var artifact = await flight.Task.WaitAsync(waiterCancellation).ConfigureAwait(false);
            waiterCancellation.ThrowIfCancellationRequested();
            return await Task.Run(() => repository.Borrow(artifact), waiterCancellation).ConfigureAwait(false);
        }
        finally
        {
            var cancel = false;
            lock (gate)
            {
                flight.Waiters--; cancel = flight.Waiters == 0 && !flight.Task.IsCompleted;
                // Close enrollment before canceling; a new waiter gets a new flight, never a canceled worker.
                if (cancel && flights.GetValueOrDefault(key) == flight) flights.Remove(key);
            }
            if (cancel) flight.Cancel();
            Retire(key, flight);
        }
    }
    private void Retire((string Path, long Revision) key, Flight flight)
    {
        PreparedAssetLease? artifact = null;
        lock (gate)
        {
            if (flight.Waiters != 0 || !flight.Task.IsCompleted || flight.Retired) return;
            flight.Retired = true;
            if (flights.GetValueOrDefault(key) == flight) flights.Remove(key);
            if (flight.Task.IsCompletedSuccessfully) artifact = flight.Task.Result;
        }
        artifact?.Dispose(); flight.Finish();
    }
    public void Dispose()
    {
        Flight[] pending; FileSystemWatcher[] observing; Timer[] timers;
        lock (gate)
        {
            if (disposed) return; disposed = true;
            pending = flights.Values.ToArray(); observing = watchers.Values.Select(w => w.Watcher).OfType<FileSystemWatcher>().ToArray(); timers = debounce.Values.ToArray();
            watchers.Clear(); debounce.Clear(); hintReasons.Clear(); SourceChanged = null; SourceInvalidated = null;
        }
        foreach (var w in observing) w.Dispose(); foreach (var t in timers) t.Dispose();
        foreach (var f in pending) f.Cancel(); // Never wait for worker completion on the UI thread.
    }
    private sealed class Subscription(Action release) : IDisposable
    { private Action? release = release; public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke(); }
}
