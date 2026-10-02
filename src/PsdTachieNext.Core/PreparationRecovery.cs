namespace PsdTachieNext.Core;

/// <summary>Only source stability failures qualify for the single automatic retry.</summary>
public sealed class SourceChangedDuringPreparationException(string message, Exception? inner = null) : IOException(message, inner);
public enum PreparationRecovery { None, ReloadSource, LocateSource, ReleaseCapacity, CheckAccess, UnsupportedSource, WaitForStableSource }
public sealed record PreparationDiagnostic(PreparationRecovery Recovery, string Message, string Action)
{
    public bool HasPreviousOutput { get; init; }
    public static PreparationDiagnostic? From(Exception? error)
    {
        if (error is null) return null;
        return error switch
        {
            SourceChangedDuringPreparationException => new(PreparationRecovery.WaitForStableSource, "素材が準備中に更新されました。", "素材の保存完了を待って再読込してください。"),
            CacheCapacityException => new(PreparationRecovery.ReleaseCapacity, "準備に必要な容量が不足しています。", "不要なSourceを閉じるか容量予算を見直して再読込してください。"),
            FileNotFoundException or DirectoryNotFoundException => new(PreparationRecovery.LocateSource, "元PSDが見つかりません。", "元PSDの場所を指定し直してください。"),
            UnauthorizedAccessException => new(PreparationRecovery.CheckAccess, "元PSDまたは専用cacheにアクセスできません。", "ファイルのアクセス権を確認して再読込してください。"),
            NotSupportedException => new(PreparationRecovery.UnsupportedSource, "この素材の形式・描画機能は未対応です。", "対応するRGB8素材を指定してください。"),
            _ => new(PreparationRecovery.ReloadSource, "素材の準備または描画に失敗しました。", "素材と詳細エラーを確認して再読込してください。")
        };
    }
}
