using System.Globalization;
using YukkuriMovieMaker.Plugin;
namespace PsdTachieNext.HostProof;
/// <summary>Runner-only synthetic validation bootstrap; never part of product distribution.</summary>
public sealed class PaletteUiProofPlugin : ILocalizePlugin
{
    public string Name => "PSD palette synthetic Actions proof";
    public void SetCulture(CultureInfo cultureInfo) => PaletteNativeProof.Schedule();
}
