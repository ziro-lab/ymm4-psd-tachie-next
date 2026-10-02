using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using Newtonsoft.Json;
using PsdTachieNext.Core;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Settings;
using YukkuriMovieMaker.Commons;
using YukkuriMovieMaker.Controls;
using YukkuriMovieMaker.Plugin.Tachie;

namespace PsdTachieNext.Ymm4;

/// <summary>Original-source preparation checkpoint; broader PSD compatibility and palettes remain separate gates.</summary>
public sealed class CompiledTachiePlugin : ITachiePlugin
{
    public string Name => "PSD Tachie Next（元PSD・試作）";
    public ITachieCharacterParameter CreateCharacterParameter() => new CompiledCharacterParameter();
    public ITachieItemParameter CreateItemParameter() => new CompiledItemParameter();
    public ITachieFaceParameter CreateFaceParameter() => new CompiledFaceParameter();
    public ITachieSource CreateTachieSource(IGraphicsDevicesAndContext devices)
        => new CompiledTachieSource(devices.DeviceContext, preparation: CompiledTachieSource.ProofPreparation);
    public bool HasScriptFile => false;
    public void CreateScriptFile(string scriptDirectoryPath) { }
    public IEnumerable<ExoItem> CreateExoItems(int fps, IEnumerable<TachieItemExoDescription> items,
        IEnumerable<TachieFaceItemExoDescription> faces, IEnumerable<TachieVoiceItemExoDescription> voices) => [];
}

public sealed class CompiledCharacterParameter : TachieCharacterParameterBase { }
public sealed class CompiledItemParameter : TachieItemParameterBase, IFileItem
{
    private SourceAssetRef? source;
    private long refreshRevision;
    private readonly WeakReference<CompiledItemParameter>? refreshParent;
    public CompiledItemParameter() { }
    private CompiledItemParameter(CompiledItemParameter original)
    {
        source = original.source; refreshRevision = original.RefreshRevision;
        refreshParent = new(original);
    }
    [JsonIgnore, Browsable(false)]
    public long RefreshRevision => Interlocked.Read(ref refreshRevision);
    /// <summary>New parameter identity, identical persisted reference; no setters, Undo or prepared resources.</summary>
    public CompiledItemParameter CreateEquivalentRefreshClone() => new(this);
    internal bool IsRefreshCloneOf(CompiledItemParameter original)
        => refreshParent?.TryGetTarget(out var parent) == true && ReferenceEquals(parent, original);
    [Browsable(false)]
    public SourceAssetRef? Source
    {
        get => source;
        set
        {
            if (source == value) return;
            Interlocked.Increment(ref refreshRevision); // Includes A -> B -> A, even before a player Update.
            Set(ref source, value, nameof(Source), [nameof(File)]);
        }
    }
    [JsonIgnore]
    [Display(Name = "元PSD / PSB", Description = "元ファイルは変更せず、必要な準備を自動で行います。対応するRGB8素材のみ。")]
    [FileSelector(FileGroupType.TachieParts, CustomFilterName = "PSD / PSB", CustomFilterValue = "*.psd;*.psb", ShowThumbnail = false)]
    public string? File
    {
        get => Source?.Path;
        set => Source = string.IsNullOrWhiteSpace(value) ? null : source?.Relink(value) ?? SourceAssetRef.Create(value);
    }
    public IEnumerable<string> GetFiles() => Source is null ? [] : [Source.Path];
    public void ReplaceFile(string oldPath, string newPath)
    {
        if (Source is not null && string.Equals(Source.Path, oldPath, StringComparison.OrdinalIgnoreCase)) File = newPath;
    }
    public IEnumerable<TimelineResource> GetResources()
    {
        if (Source is not null && TimelineResource.TryParseFromPath(Source.Path, TimelineResourceType.Tachie, out var resource)) yield return resource;
    }
    protected override IEnumerable<IAnimatable> GetAnimatables() => [];
}
/// <summary>Caller supplies a live owner on the UI thread and an additional lifetime/generation guard.</summary>
public static class CompiledParameterRefreshBridge
{
    public static bool TryReplaceEquivalent(CompiledItemParameter expectedCurrent,
        Func<ITachieItemParameter?> getCurrent, Action<ITachieItemParameter> replaceCurrent,
        out CompiledItemParameter? replacement, Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(expectedCurrent);
        ArgumentNullException.ThrowIfNull(getCurrent);
        ArgumentNullException.ThrowIfNull(replaceCurrent);
        replacement = null;
        var revision = expectedCurrent.RefreshRevision;
        if (!ReferenceEquals(getCurrent(), expectedCurrent) || isCurrent?.Invoke() == false) return false;
        var clone = expectedCurrent.CreateEquivalentRefreshClone();
        if (revision != expectedCurrent.RefreshRevision || !ReferenceEquals(getCurrent(), expectedCurrent)
            || isCurrent?.Invoke() == false) return false;
        replaceCurrent(clone);
        replacement = clone;
        return true;
    }
}
public sealed class CompiledFaceParameter : TachieFaceParameterBase
{
    // No misleading expression controls before sparse-stack/animation behavior is implemented.
    protected override IEnumerable<IAnimatable> GetAnimatables() => [];
}
