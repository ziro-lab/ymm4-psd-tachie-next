namespace PsdTachieNext.Core;

public sealed class PsdAppearanceRepairException(PsdAppearanceResolution result)
    : InvalidOperationException(result.Reason ?? "Saved appearance requires repair.")
{
    public PsdAppearanceResolution Resolution { get; } = result;
}
