using System.Windows;
using PsdTachieNext.Core;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.UndoRedo;

namespace PsdTachieNext.Ymm4;

/// <summary>UI edit/record boundary, not an atomic host transaction. The caller supplies the
/// owning timeline's real manager through TimelineToolInfo. A failure after replacement starts
/// throws AppearanceEditCommitException; callers must not treat it as an unapplied edit or retry
/// automatically. Public Record can commit history before a notification handler throws.</summary>
public static class AppearanceEditBridge
{
    /// <returns>False for rejection before mutation; true when replacement and Record returned.
    /// Notifications may replace the parameter without throwing. True does not certify final
    /// ownership or retained appearance; callers must re-read and validate the current target.</returns>
    public static bool Commit(TachieItem item, CompiledItemParameter expected, long expectedRevision,
        PsdAppearanceResolution edited, UndoRedoManager manager)
    {
        ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(edited); ArgumentNullException.ThrowIfNull(manager);
        Application.Current?.Dispatcher.VerifyAccess();
        return CommitCore(() => item.TachieItemParameter as CompiledItemParameter,
            replacement => item.TachieItemParameter = replacement, expected, expectedRevision, edited, manager.Record);
    }

    public static bool Commit(TachieFaceItem item, CompiledFaceParameter expected, long expectedRevision,
        PsdAppearanceResolution edited, UndoRedoManager manager)
    {
        ArgumentNullException.ThrowIfNull(item); ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(edited); ArgumentNullException.ThrowIfNull(manager);
        Application.Current?.Dispatcher.VerifyAccess();
        return CommitFaceCore(() => item.TachieFaceParameter as CompiledFaceParameter,
            replacement => item.TachieFaceParameter = replacement, expected, expectedRevision, edited, manager.Record);
    }

    internal static bool CommitFaceCore(Func<CompiledFaceParameter?> current, Action<CompiledFaceParameter> replace,
        CompiledFaceParameter expected, long expectedRevision, PsdAppearanceResolution edited, Action record)
    {
        if (!ReferenceEquals(current(), expected) || expected.RefreshRevision != expectedRevision
            || !edited.Succeeded || edited.Settings is null || edited.Settings == expected.Appearance) return false;
        var replacement = expected.CreateEquivalentRefreshClone();
        replacement.Appearance = edited.Settings;
        try { replace(replacement); }
        catch (Exception error) { throw Failure(AppearanceEditFailureStage.ParameterReplacement, error); }
        try { record(); }
        catch (Exception error) { throw Failure(AppearanceEditFailureStage.HistoryRecord, error); }
        return true;

        AppearanceEditCommitException Failure(AppearanceEditFailureStage stage, Exception error)
        {
            AppearanceEditParameterObservation observation;
            PsdAppearanceSettings? appearance = null;
            try
            {
                var observed = current();
                observation = ReferenceEquals(observed, expected) ? AppearanceEditParameterObservation.Original
                    : ReferenceEquals(observed, replacement) ? AppearanceEditParameterObservation.Replacement
                    : AppearanceEditParameterObservation.Other;
                appearance = observed?.Appearance;
            }
            catch { observation = AppearanceEditParameterObservation.Unavailable; }
            return new(stage, observation, appearance, error);
        }
    }

    // The same operation used by the native adapter. Delegates let managed tests inject failures
    // and exercise the actual public host manager without manufacturing a TimelineToolInfo.
    internal static bool CommitCore(Func<CompiledItemParameter?> current, Action<CompiledItemParameter> replace,
        CompiledItemParameter expected, long expectedRevision, PsdAppearanceResolution edited, Action record)
    {
        if (!ReferenceEquals(current(), expected) || expected.RefreshRevision != expectedRevision
            || !edited.Succeeded || edited.Settings is null || edited.Settings == expected.Appearance) return false;
        var replacement = expected.CreateEquivalentRefreshClone();
        replacement.Appearance = edited.Settings;
        try { replace(replacement); }
        catch (Exception error) { throw Failure(AppearanceEditFailureStage.ParameterReplacement, error); }
        try { record(); }
        catch (Exception error) { throw Failure(AppearanceEditFailureStage.HistoryRecord, error); }
        return true;

        AppearanceEditCommitException Failure(AppearanceEditFailureStage stage, Exception error)
        {
            AppearanceEditParameterObservation observation;
            PsdAppearanceSettings? appearance = null;
            try
            {
                var observed = current();
                observation = ReferenceEquals(observed, expected) ? AppearanceEditParameterObservation.Original
                    : ReferenceEquals(observed, replacement) ? AppearanceEditParameterObservation.Replacement
                    : AppearanceEditParameterObservation.Other;
                appearance = observed?.Appearance;
            }
            catch { observation = AppearanceEditParameterObservation.Unavailable; }
            return new(stage, observation, appearance, error);
        }
    }
}

public enum AppearanceEditFailureStage { ParameterReplacement, HistoryRecord }
public enum AppearanceEditParameterObservation { Original, Replacement, Other, Unavailable }

/// <summary>Failure after native mutation was attempted. State observations are diagnostic,
/// not a history receipt. Even Original does not prove that the native pending history is clean.
/// Preserve the observed edit; no rollback, Record retry or history clearing is authorized here.</summary>
public sealed class AppearanceEditCommitException : Exception
{
    public AppearanceEditFailureStage Stage { get; }
    public AppearanceEditParameterObservation ParameterObservation { get; }
    public PsdAppearanceSettings? ObservedAppearance { get; }
    public bool HistoryMayHaveChanged => true;

    internal AppearanceEditCommitException(AppearanceEditFailureStage stage,
        AppearanceEditParameterObservation observation, PsdAppearanceSettings? appearance, Exception error)
        : base($"Appearance edit failed during {stage}; current parameter: {observation}. " +
            "Native history may already contain this edit. No automatic rollback or retry was performed.", error)
    { Stage = stage; ParameterObservation = observation; ObservedAppearance = appearance; }
}
