using System.IO;
using System.Numerics;
using PsdTachieNext.Core;
using PsdTachieNext.Compiler;
using YukkuriMovieMaker.Player.Video;
using PsdTachieNext.Direct2D;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Plugin.Tachie;

namespace PsdTachieNext.Ymm4;

/// <summary>
/// Small public-API adapter. Uses the supported ITachieSource contract for this single-source checkpoint.
/// Full multi-face input moves to ITachieSource2 with its own later evidence gate.
/// Owns a separate D2D context on the supplied device: never begins/ends the caller's draw batch.
/// Original CPU preparation is nonblocking for Paused requests; only host Update applies GPU candidates.
/// </summary>
public sealed class CompiledTachieSource : ITachieSource2
{
    // Friend-assembly regression seams. Null in normal host use; never persisted in parameters.
    internal static SourcePreparationService? ProofPreparation { get; set; }
    internal static Action<CompiledTachieSource, TachieSourceDescription>? ProofHostUpdate { get; set; }
    internal static Action<TachieSourceDescription>? ProofHostRequest { get; set; }
    internal static Action<RequestStamp,int>? ProofReady { get; set; }
    internal static SelectedSourcePrefetch? ProofPrefetch { get; set; }
    internal static Action<CompiledTachieSource,string>? ProofLifecycle { get; set; }
    internal static Action<CompiledTachieSource,TachieSourceDescription,Exception>? ProofHostError { get; set; }
    private static readonly SharedDocumentPool DefaultPool = new(64L * 1024 * 1024, 4);
    private readonly object owner = new();
    private static readonly Lazy<SourcePreparationService> DefaultPreparation = new(() => new(
        new CompiledAssetRepository(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PsdTachieNext", "compiled-v1"))));
    // Provisional one-source policy: at most 16 MiB of required decoded blocks for 30s after selection.
    // Normal rendering keeps its existing pool limits and its independently owned leases.
    internal static SelectedSourcePrefetch CreateSelectedPrefetch(SourcePreparationService preparation)
        => new(preparation,DefaultPool,TimeSpan.FromSeconds(30),16L*1024*1024);
    internal static PoolSnapshot ProofDocumentSnapshot => DefaultPool.Snapshot();
    private static readonly Lazy<SelectedSourcePrefetch> DefaultPrefetch=new(()=>CreateSelectedPrefetch(DefaultPreparation.Value));
    internal static void PrefetchSelected(SourceAssetRef? source)
    {
        if(ProofPreparation is not null){if(ProofPrefetch is { } proof)_=proof.Select(source);return;}
        if(source is not null||DefaultPrefetch.IsValueCreated)_=DefaultPrefetch.Value.Select(source);
    }
    internal static void StopPrefetch(){if(DefaultPrefetch.IsValueCreated)DefaultPrefetch.Value.Dispose();}
    internal static object PrefetchDiagnostics => new {created=DefaultPrefetch.IsValueCreated,
        slot=ProofPrefetch?.Snapshot()??(DefaultPrefetch.IsValueCreated?DefaultPrefetch.Value.Snapshot():null)};
    private SelectedSourcePrefetch? Prefetch => ProofPrefetch is { } proof&&proof.Matches(Preparation,pool)?proof:
        injectedPreparation is null&&DefaultPrefetch.IsValueCreated?DefaultPrefetch.Value:null;
    private readonly SourcePreparationService? injectedPreparation;
    private SourcePreparationService Preparation => injectedPreparation ?? DefaultPreparation.Value;
    internal static long DefaultCompilationCount => ProofPreparation?.CompilationCount ?? DefaultPreparation.Value.CompilationCount;
    internal bool UsesPreparation(SourcePreparationService service)=>ReferenceEquals(Preparation,service);
    private readonly SourceSession session = new();
    private PreparedAppearanceLease? displayed;
    private SourceAssetRef? requestedSource;
    private long requestedRevision = -1;
    private IDisposable? watch;
    private bool subscribed, exporting;
    private ExportRequestScope? exportScope;
    private readonly object changeGate=new();
    private sealed record PendingChange(string Path,long Revision,SourceChangeReason Reason);
    private PendingChange? pendingChange;
    private bool changeWorker;
    private readonly SharedDocumentPool pool;
    private readonly ID2D1DeviceContext renderContext;
    private readonly AffineTransform2D center;
    private readonly ID2D1Image stableOutput;
    private readonly ID2D1Bitmap empty;
    private SharedDocumentLease? lease;
    private TreeCompiledRenderer? renderer;
    private string? currentManifest;
    private int[]? selection;
    private bool hasSelection, disposed;
    private int disposalRequested;
    private IDisposable? hostRecheck;
    private HostPreparationBridge.Observation? lastHostObservation, pendingRefresh;
    private long settingsRevision;
    internal Action? BeforeOriginalPublication { get; set; }
    public ID2D1Image Output => stableOutput;
    public long CompositionCount => renderer?.CompositionCount ?? 0;
    public string? CurrentGeneration => displayed?.Plan.Manifest.GenerationId ?? lease?.Manifest.GenerationId;
    public PreparationState PreparationState => session.State;
    public Exception? PreparationError => session.Error;
    public PreparationDiagnostic? PreparationDiagnostic => session.Diagnostic;
    public Exception? HostRecheckError => HostSourceRecheck.LastError;
    public Task PreparationCompletion => session.Completion;
    internal RequestStamp RefreshRequest => session.Current;

    public CompiledTachieSource(ID2D1DeviceContext hostContext, SharedDocumentPool? sharedPool = null, SourcePreparationService? preparation = null)
    {
        ArgumentNullException.ThrowIfNull(hostContext);
        pool = sharedPool ?? DefaultPool; injectedPreparation = preparation;
        using var device = hostContext.Device;
        renderContext = device.CreateDeviceContext(DeviceContextOptions.None);
        AffineTransform2D? effect = null; ID2D1Image? image = null; ID2D1Bitmap? bitmap = null;
        try
        {
            effect = new AffineTransform2D(hostContext); image = effect.Output;
            bitmap = hostContext.CreateEmptyBitmap();
            effect.SetInput(0, bitmap, true);
            center = effect; stableOutput = image; empty = bitmap;
            if (preparation is null) hostRecheck = HostSourceRecheck.Subscribe(Preparation);
            session.Ready += OnReady;
        }
        catch
        {
            effect?.SetInput(0, null, true); image?.Dispose(); effect?.Dispose(); bitmap?.Dispose();
            renderContext.Dispose(); throw;
        }
    }

    public void Update(TimeSpan tachieTime, TimeSpan tachieLength, TimeSpan faceTime, TimeSpan faceLength,
        ITachieCharacterParameter characterParameter, ITachieItemParameter itemParameter,
        ITachieFaceParameter faceParameter, double kuchipaku)
    {
        // Mouth/face parameters are deliberately not interpreted by this checkpoint.
        // This legacy contract has no Usage. Treat it strictly, never infer preview from timing.
        UpdateOriginal((itemParameter as CompiledItemParameter)?.Source, strict: true, export: true);
    }

    public void Update(TachieSourceDescription description)
    {
        ProofHostRequest?.Invoke(description);
        var strict = description.Usage != TimelineSourceUsage.Paused;
        var parameter = description.Tachie.ItemParameter as CompiledItemParameter;
        var observation = parameter is null ? null : HostPreparationBridge.Observe(this, parameter);
        try{UpdateOriginal(parameter?.Source, strict, description.Usage == TimelineSourceUsage.Exporting, observation);}
        catch(Exception ex){ProofHostError?.Invoke(this,description,ex);throw;}
        ProofHostUpdate?.Invoke(this, description);
    }
    private void OnReady(RequestStamp stamp)
    {
        // No owner lock here: strict host Update may be awaiting this worker while holding it.
        var observation = Volatile.Read(ref pendingRefresh);
        ProofReady?.Invoke(stamp, observation?.Tickets.Length ?? -1);
        if (observation is not null) HostPreparationBridge.Ready(this, observation, stamp);
    }
    internal bool CanRefresh(HostPreparationBridge.Observation observation, RequestStamp stamp)
    {
        // Notification must not wait on a playing/exporting host owner interval.
        if (Volatile.Read(ref disposalRequested) != 0 || !Monitor.TryEnter(owner)) return false;
        try
        {
            return !disposed && !exporting && ReferenceEquals(Volatile.Read(ref pendingRefresh), observation)
                && observation.Parameter.RefreshRevision == observation.ParameterRevision
                && observation.Parameter.Source == observation.Source && requestedSource == observation.Source
                && session.Current == stamp && session.State == PreparationState.ReadyToRender
                && requestedSource is not null && Preparation.Revision(requestedSource.Path) == stamp.SourceRevision;
        }
        finally { Monitor.Exit(owner); }
    }
    internal void RefreshOwnerRegistration()
    {
        if (Volatile.Read(ref disposalRequested) != 0 || !Monitor.TryEnter(owner)) return;
        try
        {
            var old = Volatile.Read(ref pendingRefresh);
            // Never recapture existing tickets after Undo/edits. An unregistered first request must
            // finish before its CPU cache is reused by a distinct request generation.
            if (disposed || exporting || old is null || old.Tickets.Length != 0
                || session.State != PreparationState.ReadyToRender
                || old.Parameter.RefreshRevision != old.ParameterRevision || old.Parameter.Source != old.Source
                || requestedSource != old.Source || old.Source is null) return;
            var next = HostPreparationBridge.Observe(this, old.Parameter);
            if (next.Tickets.Length == 0) return;
            lastHostObservation = next; settingsRevision++;
            RequestOriginalCore(old.Source, Preparation.Revision(old.Source.Path));
        }
        finally { Monitor.Exit(owner); }
    }

    /// <summary>Nonblocking CPU request seam. Completion requires an owner-side draw before publication.</summary>
    public void RequestOriginal(SourceAssetRef source)
    {
        lock (owner)
        {
            if(exporting)throw new InvalidOperationException("動画出力を終了してから素材の準備要求を変更してください。");
            RequestOriginalCore(source, Preparation.Revision(source.Path));
        }
    }
    private void RequestOriginalCore(SourceAssetRef source, long revision)
    {
        ObjectDisposedException.ThrowIf(disposed, this); source.Validate();
        if (!subscribed) { Preparation.SourceInvalidated += OnSourceChanged; subscribed = true; }
        if (requestedSource?.Path != source.Path)
        {
            watch?.Dispose(); watch = Preparation.Watch(source.Path);
        }
        requestedSource = source; requestedRevision = revision;
        Volatile.Write(ref pendingRefresh, lastHostObservation);
        session.Request(revision, settingsRevision, 0, (stamp, token) => PrepareOriginal(source, stamp, token),
            retryUnstable: !exporting, readSourceRevision: () => Preparation.Revision(source.Path));
    }
    private async Task<PreparedAppearanceLease> PrepareOriginal(SourceAssetRef source, RequestStamp stamp, CancellationToken token)
    {
        var prefetch=Prefetch;
        PreparedAppearanceLease ready;
        var promoted=prefetch is null?null:await prefetch.TakeVerifiedReadyAsync(source,stamp.SourceRevision,token).ConfigureAwait(false);
        if(promoted is not null)ready=promoted;
        else
        {
            // Enroll the actual waiter before dropping speculative interest in a shared job.
            var preparation=Preparation.PrepareAsync(source,stamp.SourceRevision,token);
            using var actualUse=prefetch?.BeginActualUse();
            using var asset = await preparation.ConfigureAwait(false);
            if(actualUse is not null)await actualUse.Retirement.WaitAsync(token).ConfigureAwait(false);
            ready = await Task.Run(() => asset.PrepareAppearance(pool, token: token), token).ConfigureAwait(false);
        }
        if (Preparation.Revision(source.Path) != stamp.SourceRevision)
        { ready.Dispose(); throw new SourceChangedDuringPreparationException("block準備中に素材revisionが変更されました。"); }
        return ready;
    }
    private void OnSourceChanged(string path, long revision, SourceChangeReason reason)
    {
        // Native export may await preparation while holding owner. Never make the UI/resume
        // notifier join that interval. A callback captured for an old episode only marks that episode.
        if(Volatile.Read(ref exportScope) is { } frozen){frozen.Invalidate(path,revision);return;}
        if(!Monitor.TryEnter(owner))
        {
            var source=Volatile.Read(ref requestedSource);
            if(source is null||!string.Equals(Path.GetFullPath(source.Path),path,StringComparison.OrdinalIgnoreCase))return;
            lock(changeGate)
            {
                if(pendingChange is null||pendingChange.Revision<=revision)pendingChange=new(path,revision,reason);
                if(!changeWorker){changeWorker=true;_=Task.Run(DrainChanges);}
            }
            return;
        }
        try{SourceChangedCore(path,revision,reason);}finally{Monitor.Exit(owner);}
    }
    private void DrainChanges()
    {
        while(true)
        {
            PendingChange change;
            lock(changeGate){if(pendingChange is null){changeWorker=false;return;}change=pendingChange;pendingChange=null;}
            lock(owner)
            {
                try{SourceChangedCore(change.Path,change.Revision,change.Reason);}
                catch(Exception ex){session.Fail(session.Current,ex);}
            }
        }
    }
    // Called only inside owner; at most one background worker joins a busy interval.
    private void SourceChangedCore(string path,long revision,SourceChangeReason reason)
    {
            if (Volatile.Read(ref disposalRequested) != 0 || disposed || requestedSource is null || !string.Equals(Path.GetFullPath(requestedSource.Path), path, StringComparison.OrdinalIgnoreCase)) return;
            if (revision <= session.Current.SourceRevision) return; // A delayed hint already incorporated by retry must not create a third job.
            if (exporting) { Volatile.Read(ref exportScope)?.Invalidate(path,revision); return; }
            if (session.State is PreparationState.Preparing or PreparationState.UpdatingWithPrevious) return;
            if (session.State == PreparationState.UnstableSource && reason == SourceChangeReason.ExternalHint) return;
            RequestOriginalCore(requestedSource, revision);
    }
    /// <summary>Explicit recovery; does not mutate saved parameters or add Undo commands.</summary>
    public void RetryOriginal()
    {
        lock (owner)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (requestedSource is null) return;
            if (exporting) throw new InvalidOperationException("動画出力を終了してから素材を再読込してください。");
            var revision = Preparation.Invalidate(requestedSource.Path);
            if (requestedRevision != revision) RequestOriginalCore(requestedSource, revision);
        }
    }
    private void UpdateOriginal(SourceAssetRef? source, bool strict, bool export,
        HostPreparationBridge.Observation? observation = null)
    {
        lock (owner)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var changedOwner = false;
            if (observation is not null)
            {
                var previous = lastHostObservation;
                var ownRefreshClone = previous is not null && observation.Parameter.IsRefreshCloneOf(previous.Parameter)
                    && previous.Source == observation.Source && previous.ParameterRevision == observation.ParameterRevision;
                changedOwner = previous is not null && !ownRefreshClone &&
                    (!ReferenceEquals(previous.Parameter, observation.Parameter)
                        || previous.ParameterRevision != observation.ParameterRevision
                        || !HostPreparationBridge.SameOwners(previous, observation));
                if(exporting&&export&&changedOwner)
                {
                    exportScope!.RejectChange();
                    throw new IOException("動画出力中に立ち絵の設定・所有元が変更されました。出力を中断して再実行してください。");
                }
                if (changedOwner) settingsRevision++;
                lastHostObservation = observation;
            }
            if(exporting&&export)
                exportScope!.Validate(source,session.Current,source is null?-1:Preparation.Revision(source.Path),settingsRevision);
            if (source is null) { Clear(); return; }
            var beginExport = export && !exporting;
            exporting = export;
            if (!export) Volatile.Write(ref exportScope,null);
            var revision = Preparation.Revision(source.Path);
            if(beginExport)
            {
                Volatile.Write(ref exportScope,new ExportRequestScope(source,revision,settingsRevision));
                ProofLifecycle?.Invoke(this,"export-start");
            }
            var busy = session.State is PreparationState.Preparing or PreparationState.UpdatingWithPrevious;
            if (beginExport || changedOwner || requestedSource != source ||
                (session.Current.SourceRevision != revision && !busy && session.State != PreparationState.UnstableSource) || session.State == PreparationState.Empty)
                RequestOriginalCore(source, revision);
            if(beginExport)exportScope!.Bind(session.Current);
            if (strict) session.Completion.GetAwaiter().GetResult(); // Only explicit Playing/Exporting or unknown-legacy requests.
            if (strict && Preparation.Revision(source.Path) != session.Current.SourceRevision)
            {
                if(export)exportScope!.RejectChange();
                throw new IOException("準備後に素材revisionが変更されました。再読込または出力の再実行が必要です。");
            }
            if (session.Error is { } error)
            {
                var diagnostic = session.Diagnostic!;
                throw new InvalidOperationException(diagnostic.Message + diagnostic.Action, error);
            }
            if(export)exportScope!.Validate(source,session.Current,Preparation.Revision(source.Path),settingsRevision);
            ApplyReadyCore();
            if (strict && (session.State is PreparationState.Preparing or PreparationState.UpdatingWithPrevious))
            {
                session.Completion.GetAwaiter().GetResult();
                if (session.Error is { } retryError) throw new InvalidOperationException(session.Diagnostic!.Message + session.Diagnostic.Action, retryError);
                ApplyReadyCore();
            }
            if (strict && session.State != PreparationState.DisplayedCurrent)
                throw new InvalidOperationException("要求した世代の立ち絵が未準備です。古い画像で出力を続行できません。");
            if(export)exportScope!.Validate(source,session.Current,Preparation.Revision(source.Path),settingsRevision);
        }
    }
    public bool ApplyReady()
    {
        lock (owner) { ObjectDisposedException.ThrowIf(disposed, this); return ApplyReadyCore(); }
    }
    private bool ApplyReadyCore()
    {
        var ready = session.TakeReady(out var stamp); if (ready is null) return false;
        TreeCompiledRenderer? candidate = null;
        try
        {
            candidate = new TreeCompiledRenderer(renderContext, ready, deviceEpoch: stamp.DeviceEpoch);
            candidate.UpdatePrepared(ready);
            BeforeOriginalPublication?.Invoke();
            if (requestedSource is not null && Preparation.Revision(requestedSource.Path) != stamp.SourceRevision)
                throw new SourceChangedDuringPreparationException("GPU候補の準備中に素材revisionが変更されました。古い候補を公開せず再読込が必要です。");
            var published = session.TryPublish(stamp, () =>
            {
                var oldRenderer = renderer; var oldLease = lease; var oldDisplayed = displayed;
                // Both origin and stable effect input change within the same owner interval after EndDraw.
                center.TransformMatrix = Matrix3x2.CreateTranslation(-ready.Plan.Manifest.Width / 2f, -ready.Plan.Manifest.Height / 2f);
                center.SetInput(0, candidate.Output, true);
                renderer = candidate; candidate = null; displayed = ready; lease = null; currentManifest = null;
                requestedRevision = stamp.SourceRevision;
                oldRenderer?.Dispose(); oldLease?.Dispose(); oldDisplayed?.Dispose();
            });
            if (!published) ready.Dispose();
            return published;
        }
        catch (SourceChangedDuringPreparationException) when (!exporting && requestedSource is not null)
        {
            ready.Dispose(); var source = requestedSource;
            if (session.RetryChangedCandidate(stamp, Preparation.Revision(source.Path), (next, token) => PrepareOriginal(source, next, token))) return false;
            session.Fail(stamp, new SourceChangedDuringPreparationException("素材の更新が収束していません。自動再試行は1回で終了しました。"));
            throw;
        }
        catch (Exception ex) { ready.Dispose(); session.Fail(stamp, ex); throw; }
        finally { candidate?.Dispose(); }
    }

    /// <summary>Explicit node selection is a diagnostic/core seam, not a persisted end-user face format.</summary>
    public bool UpdateCompiled(string? manifestPath, IEnumerable<int>? enabledNodeIds = null)
    {
        lock (owner) return UpdateCompiledCore(manifestPath, enabledNodeIds);
    }
    private bool UpdateCompiledCore(string? manifestPath, IEnumerable<int>? enabledNodeIds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (string.IsNullOrWhiteSpace(manifestPath)) { var existed = renderer is not null; Clear(); return existed; }
        var path = Path.GetFullPath(manifestPath);
        if (!string.Equals(Path.GetFileName(path), "manifest.json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("This integration checkpoint expects a generated manifest.json, not a PSD.", nameof(manifestPath));
        var nextSelection = enabledNodeIds?.ToArray();
        var samePath = string.Equals(path, currentManifest, StringComparison.OrdinalIgnoreCase);
        if (samePath && hasSelection && EqualSelection(selection, nextSelection)) return false;
        if (samePath)
        {
            var changed = renderer!.Update(nextSelection);
            if (changed) center.SetInput(0, renderer.Output, true);
            selection = nextSelection; hasSelection = true; return changed;
        }

        SharedDocumentLease? nextLease = null; TreeCompiledRenderer? nextRenderer = null;
        try
        {
            nextLease = pool.Acquire(Path.GetDirectoryName(path)!);
            nextRenderer = new TreeCompiledRenderer(renderContext, nextLease);
            nextRenderer.Update(nextSelection);
            // Prepare entirely before switching: neither a decode nor a validation failure destroys the old frame.
            center.TransformMatrix = Matrix3x2.CreateTranslation(-nextLease.Manifest.Width / 2f, -nextLease.Manifest.Height / 2f);
            center.SetInput(0, nextRenderer.Output, true);
            var oldRenderer = renderer; var oldLease = lease; var oldDisplayed = displayed;
            displayed = null; session.Clear();
            renderer = nextRenderer; nextRenderer = null; lease = nextLease; nextLease = null;
            currentManifest = path; selection = nextSelection; hasSelection = true;
            try { oldRenderer?.Dispose(); } finally { oldLease?.Dispose(); oldDisplayed?.Dispose(); }
            return true;
        }
        finally { try { nextRenderer?.Dispose(); } finally { nextLease?.Dispose(); } }
    }

    private static bool EqualSelection(int[]? a, int[]? b)
        => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    public void Clear()
    {
        session.Clear(); // Cancel waiters before acquiring an owner interval that may be awaiting CPU preparation.
        lock (owner) ClearCore();
    }
    private void ClearCore()
    {
        if (disposed) return;
        Volatile.Write(ref pendingRefresh, null); lastHostObservation = null;
        center.SetInput(0, empty, true); center.TransformMatrix = Matrix3x2.Identity;
        watch?.Dispose(); watch = null;
        if (subscribed) { Preparation.SourceInvalidated -= OnSourceChanged; subscribed = false; }
        requestedSource = null; requestedRevision = -1; exporting = false; Volatile.Write(ref exportScope,null);
        try { renderer?.Dispose(); } finally { renderer = null; lease?.Dispose(); lease = null; displayed?.Dispose(); displayed = null; }
        currentManifest = null; selection = null; hasSelection = false;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalRequested, 1) != 0) return;
        ProofLifecycle?.Invoke(this,"dispose-requested");
        session.Dispose(); // Release a strict wait before joining its GPU-owner interval.
        lock (owner)
        {
            if (disposed) return;
            ClearCore(); disposed = true; session.Dispose();
            hostRecheck?.Dispose(); hostRecheck = null;
            center.SetInput(0, null, true); stableOutput.Dispose(); center.Dispose(); empty.Dispose(); renderContext.Dispose();
        }
    }
}
