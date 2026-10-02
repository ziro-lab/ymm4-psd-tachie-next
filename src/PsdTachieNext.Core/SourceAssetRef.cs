namespace PsdTachieNext.Core;

/// <summary>Project-owned logical reference. Cache locations and generation IDs are never saved here.</summary>
public sealed record SourceAssetRef(int SchemaVersion, string AssetIdentity, string Path)
{
    public const int CurrentSchema = 1;
    public static SourceAssetRef Create(string path) => new(CurrentSchema, Guid.NewGuid().ToString("N"), path);
    public SourceAssetRef Relink(string path) => this with { Path = path };
    public void Validate()
    {
        if (SchemaVersion != CurrentSchema) throw new NotSupportedException("未対応の元PSD参照schemaです。");
        if (string.IsNullOrWhiteSpace(AssetIdentity) || string.IsNullOrWhiteSpace(Path))
            throw new ArgumentException("元PSD参照が空です。");
    }
}
