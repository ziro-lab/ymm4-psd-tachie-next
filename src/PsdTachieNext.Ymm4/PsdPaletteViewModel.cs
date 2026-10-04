using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PsdTachieNext.Compiler;
using PsdTachieNext.Core;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project.Items;

namespace PsdTachieNext.Ymm4;

public sealed class PsdPalettePlugin : IToolPlugin
{
    public string Name => "PSD立ち絵パレット";
    public Type ViewModelType => typeof(PsdPaletteViewModel);
    public Type ViewType => typeof(PsdPaletteView);
    public bool AllowMultipleInstances => false;
    public string DefaultGroupName => YukkuriMovieMaker.Resources.Localization.Texts.ToolGroupUtilityName;
}

/// <summary>UI-owned target and document lease. Commands carry an epoch; no worker writes a host parameter.
/// Uses actual public TimelineToolInfo, SelectedItems and the owning native history.</summary>
public sealed class PsdPaletteViewModel : ITimelineToolViewModel, INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private TimelineToolInfo? info;
    private readonly List<INotifyPropertyChanged> observed = [];
    private CancellationTokenSource? loading;
    private PreparedAssetLease? asset;
    private SharedDocumentLease? document;
    private IDisposable? watch;
    private PsdLayerReferenceIndex? index;
    private Context? ready;
    private SourceAssetRef? loadedSource;
    private long loadedRevision = -1, epoch;
    private bool active = true, disposed, committing;
    private bool subscribed;
    private string? editFailure;
    private string header = "編集対象を選択してください", status = "未選択";
    private bool canEdit;
    private readonly SourcePreparationService preparation = CompiledTachieSource.PalettePreparation;
    private readonly SharedDocumentPool pool = CompiledTachieSource.PalettePool;
    private sealed record Owner(TachieItem Item, CompiledItemParameter Parameter, long Revision, SourceAssetRef Source,
        PsdAppearanceSettings? Appearance, int Frame, int Length, int Layer);
    private sealed record Context(TimelineToolInfo Scope, IItem Item, object Parameter, long Revision, SourceAssetRef Source,
        PsdAppearanceSettings? Appearance, PsdAppearanceSettings? Base, Owner[] Owners, int Frame, int Length, int Layer, object Character)
    {
        internal bool Same(Context other) => ReferenceEquals(Scope.Timeline, other.Scope.Timeline)
            && ReferenceEquals(Scope.UndoRedoManager, other.Scope.UndoRedoManager)
            && ReferenceEquals(Item, other.Item) && ReferenceEquals(Parameter, other.Parameter)
            && Revision == other.Revision && Source == other.Source && Appearance == other.Appearance && Base == other.Base
            && Owners.SequenceEqual(other.Owners) && Frame == other.Frame && Length == other.Length && Layer == other.Layer
            && ReferenceEquals(Character, other.Character);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<PsdPaletteRow> Rows { get; } = [];
    public string Header { get => header; private set { header = value; Notify(); } }
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool CanEdit { get => canEdit; private set { canEdit = value; Notify(); CommandManager.InvalidateRequerySuggested(); } }
    internal object ProofLifetime => new { active, subscribed, hasDocument = document is not null,
        hasAsset = asset is not null, hasWatch = watch is not null, epoch, hasEditFailure = editFailure is not null };
    public string Orientation { get; private set; } = "向き: 継承";
    public ICommand ReloadCommand { get; }
    public ICommand InheritFlipCommand { get; }
    public ICommand NoneCommand { get; }
    public ICommand XCommand { get; }
    public ICommand YCommand { get; }
    public ICommand XYCommand { get; }
    public PsdPaletteViewModel()
    {
        ReloadCommand = new PaletteCommand(ReconfirmTarget, () => active && !disposed);
        InheritFlipCommand = Flip(null); NoneCommand = Flip(PsdFlipState.None);
        XCommand = Flip(PsdFlipState.X); YCommand = Flip(PsdFlipState.Y); XYCommand = Flip(PsdFlipState.XY);
        preparation.SourceInvalidated += Invalidated;
        subscribed = true;
    }
    private ICommand Flip(PsdFlipState? value) => new PaletteCommand(() => Edit(epoch, (settings, context, refs) =>
        settings.WithFlip(context.Source.AssetIdentity, refs, value)), () => CanEdit);
    public void SetTimelineToolInfo(TimelineToolInfo value)
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        info = value; Refresh();
    }
    public void Suspend()
    {
        dispatcher.VerifyAccess(); active = false; ClearReady(); Detach();
        if (subscribed) { preparation.SourceInvalidated -= Invalidated; subscribed = false; }
        Header = "編集対象を選択してください"; Status = "パレット休止中";
    }
    public void Resume()
    {
        dispatcher.VerifyAccess(); if (disposed) return; active = true;
        if (!subscribed) { preparation.SourceInvalidated += Invalidated; subscribed = true; }
        Refresh();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void ReconfirmTarget()
    {
        dispatcher.VerifyAccess(); editFailure = null; Refresh();
    }
    private void Observe(INotifyPropertyChanged value)
    { if (observed.Any(old => ReferenceEquals(old, value))) return; observed.Add(value); value.PropertyChanged += Changed; }
    private void Detach()
    { foreach (var value in observed) value.PropertyChanged -= Changed; observed.Clear(); }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (committing || disposed || !active) return;
        if (sender == info?.Timeline && e.PropertyName is not (null or "" or "Items" or "SelectedItems" or "SelectedItem")) return;
        if (dispatcher.CheckAccess()) Refresh();
        else dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Normal);
    }
    private void Invalidated(string path, long revision, SourceChangeReason reason)
    {
        if (disposed) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (!active || loadedSource is null || !string.Equals(Path.GetFullPath(loadedSource.Path), path, StringComparison.OrdinalIgnoreCase)) return;
            Refresh();
        }), DispatcherPriority.Normal);
    }
    private void ClearReady(bool release = true)
    {
        epoch++; CanEdit = false; ready = null; Rows.Clear();
        loading?.Cancel(); loading?.Dispose(); loading = null;
        if (!release) return;
        document?.Dispose(); document = null; asset?.Dispose(); asset = null; index = null;
        watch?.Dispose(); watch = null; loadedSource = null; loadedRevision = -1;
    }
    private Context Capture()
    {
        if (info is null || !active || disposed) throw new InvalidOperationException("Timelineに接続していません");
        if (info.AsyncAwaitStatus.IsBusy) throw new InvalidOperationException("YMM4が処理中です。完了を待ってください");
        var selected = info.Timeline.SelectedItems;
        if (selected.Count != 1) throw new InvalidOperationException("立ち絵または表情アイテムを1つ選択してください");
        var item = selected[0];
        if (!info.Timeline.Items.Contains(item)) throw new InvalidOperationException("選択対象が現在のTimelineにありません");
        if (item.IsLocked || item.IsHidden) throw new InvalidOperationException("ロック・非表示の対象は編集できません");
        if (item is TachieItem tachie && tachie.TachieItemParameter is CompiledItemParameter parameter)
        {
            var source = parameter.Source ?? throw new InvalidOperationException("立ち絵の元PSD / PSBが未設定です");
            source.Validate();
            return new(info, item, parameter, parameter.RefreshRevision, source, parameter.Appearance, null, [],
                item.Frame, item.Length, item.Layer, tachie.Character);
        }
        if (item is not TachieFaceItem face || face.TachieFaceParameter is not CompiledFaceParameter expression)
            throw new InvalidOperationException("PSD Tachie Nextの立ち絵または表情を選択してください");
        var owners = info.Timeline.Items.OfType<TachieItem>()
            .Where(owner => ReferenceEquals(owner.Character, face.Character) && !owner.IsHidden &&
                (long)owner.Frame < (long)face.Frame + face.Length && (long)face.Frame < (long)owner.Frame + owner.Length)
            .Where(owner => owner.TachieItemParameter is CompiledItemParameter { Source: not null })
            .Select(owner => { var p = (CompiledItemParameter)owner.TachieItemParameter;
                return new Owner(owner, p, p.RefreshRevision, p.Source!, p.Appearance, owner.Frame, owner.Length, owner.Layer); }).ToArray();
        if (owners.Length == 0) throw new InvalidOperationException("同じキャラクター・時間範囲の元PSD設定済み立ち絵がありません");
        var sources = owners.Select(owner => owner.Source).Distinct().ToArray();
        if (sources.Length != 1 || owners.Select(owner => owner.Appearance).Distinct().Count() != 1)
            throw new InvalidOperationException("表情の継承元が複数あります。元PSDと立ち絵設定を一意にしてください");
        sources[0].Validate();
        return new(info, item, expression, expression.RefreshRevision, sources[0], expression.Appearance, owners[0].Appearance, owners,
            item.Frame, item.Length, item.Layer, face.Character);
    }
    public void Refresh()
    {
        dispatcher.VerifyAccess(); if (disposed || !active || committing) return;
        Detach();
        if (info is not null)
        {
            Observe(info.Timeline); Observe(info.AsyncAwaitStatus);
            foreach (var item in info.Timeline.Items)
            {
                Observe(item);
                if (item is TachieItem { TachieItemParameter: CompiledItemParameter p }) Observe(p);
                if (item is TachieFaceItem { TachieFaceParameter: CompiledFaceParameter f }) Observe(f);
            }
        }
        if (editFailure is not null) { ClearReady(); Status = editFailure; return; }
        Context context;
        try { context = Capture(); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException)
        { ClearReady(); Header = "編集対象を確認してください"; Status = ex.Message; return; }
        var character = context.Item is TachieItem tachie ? tachie.CharacterName : ((TachieFaceItem)context.Item).CharacterName;
        Header = $"{character} / {(context.Item is TachieFaceItem ? "表情" : "立ち絵")} / Layer {context.Item.Layer}\n" +
            $"Frame {context.Item.Frame}–{(long)context.Item.Frame + context.Item.Length - 1} / {Path.GetFileName(context.Source.Path)}";
        var revision = preparation.Revision(context.Source.Path);
        if (loadedSource == context.Source && loadedRevision == revision && index is not null)
        { ClearReady(release: false); Show(context); return; }
        ClearReady(); Status = "元PSDを準備しています。編集は準備完了後に有効になります";
        loadedSource = context.Source; loadedRevision = revision;
        var ticket = epoch;
        loading = new();
        watch = preparation.Watch(context.Source.Path);
        _ = Load(context, revision, ticket, loading.Token);
    }
    private async Task Load(Context context, long revision, long ticket, CancellationToken token)
    {
        PreparedAssetLease? candidate = null; SharedDocumentLease? lease = null;
        try
        {
            // An independently enrolled waiter in the existing preparation service, with no speculative cache of our own.
            candidate = await preparation.PrepareAsync(context.Source, revision, token);
            lease = pool.Acquire(candidate.Directory);
            var references = await Task.Run(() => PsdLayerReferenceIndex.Read(lease!, token), token);
            token.ThrowIfCancellationRequested();
            if (ticket != epoch || !context.Same(Capture()) || preparation.Revision(context.Source.Path) != revision) return;
            asset = candidate; candidate = null; document = lease; lease = null; index = references;
            Show(context);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ticket == epoch && active && !disposed)
            { ClearReady(); Status = "準備できません: " + ex.Message; }
        }
        finally { lease?.Dispose(); candidate?.Dispose(); }
    }
    private void Show(Context context)
    {
        var refs = index!;
        var own = context.Appearance ?? PsdAppearanceSettings.Create(context.Source.AssetIdentity);
        var ownResult = own.Resolve(context.Source.AssetIdentity, refs);
        var effective = new PsdAppearanceStack(context.Item is TachieFaceItem ? context.Base : context.Appearance,
            context.Item is TachieFaceItem && context.Appearance is not null ? [new(0, context.Appearance)] : [])
            .Resolve(context.Source.AssetIdentity, refs);
        if (!ownResult.Succeeded || !effective.Succeeded)
        { Status = "設定を保持して編集を停止しています: " + (ownResult.Reason ?? effective.Reason); return; }
        ready = context;
        var owned = own.OwnedOrigins(context.Source.AssetIdentity, refs);
        var selection = refs.Notation.FlipBindings.Selection;
        var visibleOrigins = SelectedOrigins(effective.State!);
        var ticket = epoch;
        foreach (var node in refs.Notation.Nodes.Where(node => selection.OriginNodeIds[node.NodeId] == node.NodeId))
        {
            var depth = 0; var parent = node.ParentId;
            while (parent is int id) { depth++; parent = refs.Notation.Nodes[id].ParentId; }
            var visible = visibleOrigins.Contains(node.NodeId);
            Rows.Add(new PsdPaletteRow(node.NodeId, new string(' ', depth * 2) + node.DisplayName, visible,
                owned.Contains(node.NodeId), node.SelectionMarker,
                new PaletteCommand(() => Edit(ticket, (settings, target, idx) => settings.SetVisible(target.Source.AssetIdentity,
                    idx, node.NodeId, node.SelectionMarker == PsdSelectionMarker.Radio || !visible)),
                    () => CanEdit && ticket == epoch && node.SelectionMarker != PsdSelectionMarker.ForceVisible),
                new PaletteCommand(() => Edit(ticket, (settings, target, idx) => settings.Inherit(target.Source.AssetIdentity, idx, node.NodeId)),
                    () => CanEdit && ticket == epoch && owned.Contains(node.NodeId))));
        }
        Orientation = "向き: " + (own.AuthoredFlip(context.Source.AssetIdentity)?.ToString() ?? "継承"); Notify(nameof(Orientation));
        Status = context.Item is TachieFaceItem
            ? "準備完了。選択表情と立ち絵の継承を表示。ほかの表情との最終合成はYMM4プレビューで確認してください"
            : "準備完了。選択立ち絵の設定を表示。表情との最終合成はYMM4プレビューで確認してください";
        CanEdit = true;
    }
    public bool SetVisible(int origin, bool visible)
        => Edit(epoch, (settings, context, refs) => settings.SetVisible(context.Source.AssetIdentity, refs, origin, visible));
    public bool Inherit(int origin)
        => Edit(epoch, (settings, context, refs) => settings.Inherit(context.Source.AssetIdentity, refs, origin));
    internal static HashSet<int> SelectedOrigins(PsdVisibilityState state)
        => state.EnabledNodeIds.Select(id => state.Bindings.Selection.OriginNodeIds[id]).ToHashSet();
    private bool Edit(long ticket, Func<PsdAppearanceSettings, Context, PsdLayerReferenceIndex, PsdAppearanceResolution> change)
    {
        dispatcher.VerifyAccess();
        if (!CanEdit || ticket != epoch || ready is not { } target || index is null) return false;
        var mutationStarted = false;
        try
        {
            if (!target.Same(Capture()) || loadedRevision != preparation.Revision(target.Source.Path))
            { Refresh(); Status = "対象または元PSDが変わりました。内容を確認して再操作してください"; return false; }
            var edited = change(target.Appearance ?? PsdAppearanceSettings.Create(target.Source.AssetIdentity), target, index);
            if (!edited.Succeeded) { Status = "編集できません: " + edited.Reason; return false; }
            // Verify composition without persisting its combined settings into the target patch.
            var combined = new PsdAppearanceStack(target.Item is TachieFaceItem ? target.Base : edited.Settings,
                target.Item is TachieFaceItem ? [new(0, edited.Settings!)] : []).Resolve(target.Source.AssetIdentity, index);
            if (!combined.Succeeded) { Status = "継承との合成を確認できません: " + combined.Reason; return false; }
            committing = true;
            mutationStarted = true;
            bool committed;
            try
            {
                committed = target.Item switch
                {
                    TachieItem item => AppearanceEditBridge.Commit(item, (CompiledItemParameter)target.Parameter, target.Revision, edited, target.Scope.UndoRedoManager),
                    TachieFaceItem face => AppearanceEditBridge.Commit(face, (CompiledFaceParameter)target.Parameter, target.Revision, edited, target.Scope.UndoRedoManager),
                    _ => false
                };
            }
            finally { committing = false; }
            var now = Capture();
            var retained = ReferenceEquals(now.Scope.Timeline, target.Scope.Timeline)
                && ReferenceEquals(now.Scope.UndoRedoManager, target.Scope.UndoRedoManager)
                && ReferenceEquals(now.Item, target.Item) && now.Source == target.Source && now.Appearance == edited.Settings
                && now.Frame == target.Frame && now.Length == target.Length && now.Layer == target.Layer
                && ReferenceEquals(now.Character, target.Character);
            Refresh();
            if (committed && !retained)
            {
                editFailure = "通知中に対象の設定が変わりました。対象を再確認するまで編集を停止しています";
                Refresh();
            }
            return committed && retained;
        }
        catch (AppearanceEditCommitException ex)
        {
            committing = false;
            editFailure = $"編集通知に失敗しました ({ex.Stage})。履歴に残った可能性があります。対象を再確認するまで編集を停止しています。自動取消・再記録は行っていません";
            Refresh();
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            if (mutationStarted) editFailure = "変更開始後の対象確認に失敗しました。対象を再確認するまで編集を停止しています: " + ex.Message;
            Refresh();
            if (!mutationStarted) Status = "対象を再確認してください: " + ex.Message;
            return false;
        }
    }
    public void Dispose()
    {
        dispatcher.VerifyAccess(); if (disposed) return;
        Suspend(); disposed = true; preparation.SourceInvalidated -= Invalidated;
    }
}

public sealed record PsdPaletteRow(int Origin, string Label, bool Visible, bool Owned, PsdSelectionMarker Marker,
    ICommand ToggleCommand, ICommand InheritCommand)
{
    public bool ToggleEnabled => Marker != PsdSelectionMarker.ForceVisible;
    public string Ownership => Marker == PsdSelectionMarker.ForceVisible ? "固定" : Owned ? "この対象で指定" : "継承";
}
internal sealed class PaletteCommand(Action execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
}
