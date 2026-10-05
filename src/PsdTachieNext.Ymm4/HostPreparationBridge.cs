using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using PsdTachieNext.Core;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;

namespace PsdTachieNext.Ymm4;

/// <summary>Normal-host startup; no tool window or saved project setting is required.</summary>
public sealed class PreparationRefreshStartup : ILocalizePlugin
{
    public string Name => "PSD Tachie Next preparation refresh";
    public void SetCulture(CultureInfo culture) => HostPreparationBridge.Start();
}

/// <summary>
/// UI-owned live item registrations, CPU completion -> history-free parameter notification only.
/// Public WPF DataContext and the version-sensitive public Main/Timeline view-model getters are the
/// bounded host seam. No private fields, player hooks, seeks, worker GPU work or synchronous UI Invoke.
/// </summary>
internal static class HostPreparationBridge
{
    private static readonly object gate = new();
    private static readonly Dictionary<TachieItem, Owner> owners = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<TachieFaceItem, FaceOwner> faceOwners = new(ReferenceEqualityComparer.Instance);
    private static readonly ConditionalWeakTable<CompiledFaceParameter, object> knownFaceParameters = new();
    private static long faceOwnershipEpoch;
    internal static long FaceOwnershipEpoch => Interlocked.Read(ref faceOwnershipEpoch);
    internal static bool IsFaceInputCurrent(CompiledFaceParameter parameter, int layer)
    {
        lock (gate)
        {
            // Unregistered default/legacy parameters have no public item-owner contract. A once-owned
            // native face parameter must still be owned at the supplied layer; old clones cannot fall
            // back into that unregistered category after a swap or project close.
            return !knownFaceParameters.TryGetValue(parameter, out _)
                || faceOwners.Values.Any(owner => owner.Matches(parameter, layer));
        }
    }
    private static readonly List<WeakReference<CompiledTachieSource>> sources = [];
    private static object? main, scope, itemsSnapshot;
    private static long scopeEpoch;
    private static DispatcherTimer? bootstrap;
    private static bool started;
    private static readonly SelectedCharacterPrefetchBinding selectedCharacter=new();
    internal static Exception? LastError { get; private set; }
    internal static long ReplacementCount { get; private set; }
    internal static long RejectedCount { get; private set; }
    internal static object Diagnostics { get { lock(gate) return new { started, mainAttached=main is not null,
        scopeType=scope?.GetType().FullName,scopeEpoch,ownerCount=owners.Count,ReplacementCount,RejectedCount,
        selectedCharacterError=selectedCharacter.Error?.ToString(),prefetch=CompiledTachieSource.PrefetchDiagnostics }; } }
    internal sealed record Ticket(WeakReference<Owner> Owner, long Epoch);
    internal sealed record Observation(CompiledItemParameter Parameter, SourceAssetRef? Source,
        long ParameterRevision, long ScopeEpoch, Ticket[] Tickets);

    internal static void Start()
    {
        var app = Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (started) return;
            started = true;
            app.Exit += (_, _) => Detach();
            // Startup can precede MainWindow construction. Stop polling once its DataContext is attached.
            bootstrap = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(100) };
            bootstrap.Tick += (_, _) => AttachMain();
            bootstrap.Start(); AttachMain();
        }), DispatcherPriority.Normal);
    }
    private static object? Public(object? value, string name)
        => value?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
    private static void AttachMain()
    {
        try
        {
            var app = Application.Current;
            if (app is null) return;
            var window = app.Windows.Cast<Window>().FirstOrDefault(w => w.DataContext?.GetType().FullName == "YukkuriMovieMaker.ViewModels.MainViewModel");
            if (window?.DataContext is not INotifyPropertyChanged vm) return;
            main = vm; vm.PropertyChanged += MainChanged;
            window.Closed += (_, _) => Detach();
            bootstrap?.Stop(); bootstrap = null;
            RefreshScope(); LastError = null;
        }
        catch (Exception e) { LastError = e; }
    }
    private static void MainChanged(object? sender, PropertyChangedEventArgs e)
    {
        // ActiveTimelineViewModel is a computed public getter; activation can notify ActiveContent.
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is "ActiveTimelineViewModel" or "ActiveContent" or "IsEmptyProject") RefreshScope();
    }
    private static void RefreshScope()
    {
        Application.Current.Dispatcher.VerifyAccess();
        var next = Public(main, "ActiveTimelineViewModel");
        if (!ReferenceEquals(scope, next))
        {
            if (scope is INotifyPropertyChanged old) old.PropertyChanged -= ScopeChanged;
            RetireOwners(); scope = next; Interlocked.Increment(ref scopeEpoch);
            if (scope is INotifyPropertyChanged current) current.PropertyChanged += ScopeChanged;
            selectedCharacter.Bind(Public(scope,"CurrentCharacter"));
        }
        RefreshOwners();
    }
    private static void ScopeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == "Items") RefreshOwners();
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is "CurrentCharacter" or "PrimarySerifInputRow")
            selectedCharacter.Bind(Public(scope,"CurrentCharacter"));
    }
    private static TachieItem[] LiveItems(object? snapshot) => (snapshot as IEnumerable)?.Cast<object>()
        .Select(vm => Public(vm, "Item")).OfType<TachieItem>()
        .Where(item => item.TachieItemParameter is CompiledItemParameter).ToArray() ?? [];
    private static TachieFaceItem[] LiveFaceItems(object? snapshot) => (snapshot as IEnumerable)?.Cast<object>()
        .Select(vm => Public(vm, "Item")).OfType<TachieFaceItem>()
        .Where(item => item.TachieFaceParameter is CompiledFaceParameter).ToArray() ?? [];
    private static void RefreshOwners()
    {
        Application.Current.Dispatcher.VerifyAccess();
        try
        {
            var snapshot = Public(scope, "Items");
            if (ReferenceEquals(itemsSnapshot, snapshot)) return; // Immutable host list: no repeated full scan per ready Source.
            var items = LiveItems(snapshot);
            var faces = LiveFaceItems(snapshot);
            lock (gate)
            {
                foreach (var item in owners.Keys.Where(item => !items.Contains(item, ReferenceEqualityComparer.Instance)).ToArray())
                { owners[item].Dispose(); owners.Remove(item); }
                foreach (var item in items)
                    if (!owners.ContainsKey(item)) owners.Add(item, new Owner(item));
                foreach (var face in faceOwners.Keys.Where(face => !faces.Contains(face, ReferenceEqualityComparer.Instance)).ToArray())
                { faceOwners[face].Dispose(); faceOwners.Remove(face); Interlocked.Increment(ref faceOwnershipEpoch); }
                foreach (var face in faces)
                    if (!faceOwners.ContainsKey(face)) { faceOwners.Add(face, new FaceOwner(face)); Interlocked.Increment(ref faceOwnershipEpoch); }
                itemsSnapshot = snapshot;
            }
            // Late initial registration starts a NEW request after the unbound request is ready.
            // It never renews an existing owner ticket or publishes an unbound candidate.
            CompiledTachieSource[] pending;
            lock (gate)
            {
                sources.RemoveAll(w => !w.TryGetTarget(out _));
                pending = sources.Select(w => w.TryGetTarget(out var s) ? s : null).OfType<CompiledTachieSource>().ToArray();
            }
            foreach (var source in pending) source.RefreshOwnerRegistration();
        }
        catch (Exception e) { LastError = e; }
    }
    internal static Observation Observe(CompiledTachieSource source, CompiledItemParameter parameter)
    {
        Ticket[] tickets;
        lock (gate)
        {
            sources.RemoveAll(w => !w.TryGetTarget(out _));
            if (!sources.Any(w => w.TryGetTarget(out var s) && ReferenceEquals(s, source))) sources.Add(new(source));
            tickets = owners.Values.Where(o => ReferenceEquals(o.Parameter, parameter))
                .Select(o => new Ticket(new(o), o.Lifetime.Capture())).ToArray();
        }
        return new(parameter, parameter.Source, parameter.RefreshRevision, Interlocked.Read(ref scopeEpoch), tickets);
    }
    internal static void Ready(CompiledTachieSource source, Observation observation, RequestStamp stamp)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        var weak = new WeakReference<CompiledTachieSource>(source);
        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!weak.TryGetTarget(out var current)) return;
                // Reconcile public UI ownership at completion, including initial Items binding that
                // occurs after scope activation. Existing ticket epochs are never renewed here.
                RefreshScope();
                if (observation.Tickets.Length == 0) { current.RefreshOwnerRegistration(); return; }
                if (!current.CanRefresh(observation, stamp)) return;
                var snapshot = Public(scope, "Items");
                var live = LiveItems(snapshot).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var ticket in observation.Tickets)
                {
                    if (!ticket.Owner.TryGetTarget(out var owner)) continue;
                    bool Valid() => ticket.Epoch == owner.Lifetime.Capture() && owner.Lifetime.IsCurrent(ticket.Epoch)
                        && observation.ScopeEpoch == Interlocked.Read(ref scopeEpoch)
                        && ReferenceEquals(Public(main, "ActiveTimelineViewModel"), scope)
                        && ReferenceEquals(snapshot, Public(scope, "Items")) && live.Contains(owner.Item)
                        && ReferenceEquals(owner.Item.TachieItemParameter, observation.Parameter)
                        && current.CanRefresh(observation, stamp);
                    if (!Valid()) { RejectedCount++; continue; }
                    // A same-value item parameter swap still creates a native pending Undo command.
                    // Notify through the SDK's protected Bindable API without replacing the owner.
                    observation.Parameter.NotifyPreparationReady();
                    ReplacementCount++; // Historical diagnostics name: counts successful refresh hints.
                }
            }
            catch (Exception e) { LastError = e; } // Notification failures do not fail/consume CPU preparation.
        }), DispatcherPriority.Background);
    }
    internal static bool SameOwners(Observation? a, Observation b)
        => a is not null && a.ScopeEpoch == b.ScopeEpoch && a.Tickets.Length == b.Tickets.Length
            && a.Tickets.Zip(b.Tickets).All(pair => pair.First.Epoch == pair.Second.Epoch
                && pair.First.Owner.TryGetTarget(out var first) && pair.Second.Owner.TryGetTarget(out var second)
                && ReferenceEquals(first, second));
    private static void RetireOwners()
    {
        itemsSnapshot = null;
        lock (gate)
        {
            foreach (var owner in owners.Values) owner.Dispose(); owners.Clear();
            foreach (var face in faceOwners.Values) face.Dispose(); faceOwners.Clear();
            Interlocked.Increment(ref faceOwnershipEpoch);
        }
    }
    private static void Detach()
    {
        bootstrap?.Stop(); bootstrap = null;
        if (main is INotifyPropertyChanged vm) vm.PropertyChanged -= MainChanged;
        if (scope is INotifyPropertyChanged active) active.PropertyChanged -= ScopeChanged;
        RetireOwners(); main = scope = null; Interlocked.Increment(ref scopeEpoch);
        selectedCharacter.Dispose();CompiledTachieSource.StopPrefetch();
        lock (gate) sources.Clear();
    }
    // Public live face items, registered on the UI thread. Identity/ABA changes invalidate captured
    // requests before their CPU-ready candidate can be published, even before the next host Update.
    private sealed class FaceOwner : IDisposable
    {
        private readonly TachieFaceItem item;
        private volatile CompiledFaceParameter? parameter;
        internal bool Matches(CompiledFaceParameter expected, int layer)
            => ReferenceEquals(parameter, expected) && item.Layer == layer;
        internal FaceOwner(TachieFaceItem item)
        {
            this.item = item;
            ((INotifyPropertyChanged)item).PropertyChanged += Changed;
            if (item is INotifyPropertyChanging changing) changing.PropertyChanging += Changing;
            Bind();
        }
        private void Changing(object? sender, PropertyChangingEventArgs e) => Interlocked.Increment(ref faceOwnershipEpoch);
        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            Interlocked.Increment(ref faceOwnershipEpoch);
            if (ReferenceEquals(sender, item) && (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(TachieFaceItem.TachieFaceParameter))) Bind();
        }
        private void Bind()
        {
            if (parameter is not null) parameter.PropertyChanged -= Changed;
            if (parameter is INotifyPropertyChanging old) old.PropertyChanging -= Changing;
            parameter = item.TachieFaceParameter as CompiledFaceParameter;
            if (parameter is not null) knownFaceParameters.GetValue(parameter, _ => new object());
            if (parameter is not null) parameter.PropertyChanged += Changed;
            if (parameter is INotifyPropertyChanging current) current.PropertyChanging += Changing;
        }
        public void Dispose()
        {
            ((INotifyPropertyChanged)item).PropertyChanged -= Changed;
            if (item is INotifyPropertyChanging changing) changing.PropertyChanging -= Changing;
            if (parameter is not null) parameter.PropertyChanged -= Changed;
            if (parameter is INotifyPropertyChanging old) old.PropertyChanging -= Changing;
            parameter = null;
        }
    }
    internal sealed class Owner : IDisposable
    {
        internal TachieItem Item { get; }
        internal CompiledItemParameter? Parameter { get; private set; }
        internal RefreshOwnerLifetime Lifetime { get; } = new();
        internal Owner(TachieItem item)
        {
            Item = item; ((INotifyPropertyChanged)item).PropertyChanged += Changed; Bind();
        }
        private void Bind()
        {
            if (Parameter is INotifyPropertyChanged old) old.PropertyChanged -= ParameterChanged;
            Parameter = Item.TachieItemParameter as CompiledItemParameter;
            if (Parameter is INotifyPropertyChanged current) current.PropertyChanged += ParameterChanged;
        }
        private void Changed(object? sender, PropertyChangedEventArgs e)
        {
            // Parameter swap and other item changes invalidate queued completion, including an Undo ABA.
            if (e.PropertyName == nameof(TachieItem.TachieItemParameter)
                && ReferenceEquals(Item.TachieItemParameter, Parameter) && Parameter?.IsPreparationNotification == true) return;
            Lifetime.Invalidate();
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(TachieItem.TachieItemParameter)) Bind();
        }
        private void ParameterChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CompiledItemParameter.Appearance) && Parameter?.IsPreparationNotification == true) return;
            Lifetime.Invalidate();
        }
        public void Dispose()
        {
            Lifetime.Dispose(); ((INotifyPropertyChanged)Item).PropertyChanged -= Changed;
            if (Parameter is INotifyPropertyChanged parameter) parameter.PropertyChanged -= ParameterChanged;
            Parameter = null;
        }
    }
}
