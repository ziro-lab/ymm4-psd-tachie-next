using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PsdTachieNext.Core;
using PsdTachieNext.Ymm4;

internal static class FaceParameterCases
{
    internal static void Run(string output)
    {
        var assertions = 0;
        void Check(bool condition) { assertions++; if (!condition) throw new InvalidOperationException("Face parameter contract failed."); }
        var appearance = PsdAppearanceSettings.FromJson("{\"version\":999,\"asset\":\"synthetic\",\"opaque\":0.123456789012345678901234567890}");
        var parameter = new CompiledFaceParameter();
        var events = 0; parameter.PropertyChanged += (_, _) => events++;
        Check(JObject.Parse(JsonConvert.SerializeObject(parameter))["Appearance"] is null);
        parameter.Appearance = appearance;
        Check(parameter.RefreshRevision == 1 && events == 1);
        parameter.Appearance = PsdAppearanceSettings.FromJson(appearance.Json);
        Check(parameter.RefreshRevision == 1 && events == 1);
        var json = JsonConvert.SerializeObject(parameter);
        Check(JObject.Parse(json)["RefreshRevision"] is null);
        Check(JsonConvert.DeserializeObject<CompiledFaceParameter>(json)!.Appearance!.Json == appearance.Json);
        Check(JsonConvert.DeserializeObject<CompiledFaceParameter>("{}")!.Appearance is null);
        var clone = parameter.CreateEquivalentRefreshClone();
        Check(!ReferenceEquals(parameter, clone) && clone.RefreshRevision == parameter.RefreshRevision && ReferenceEquals(clone.Appearance, appearance));
        clone.Appearance = null; Check(parameter.Appearance == appearance && clone.RefreshRevision == 2);
        parameter.Appearance = null; parameter.Appearance = appearance;
        Check(parameter.RefreshRevision == 3 && events == 3);

        var edited = new PsdAppearanceResolution(PsdAppearanceSettings.Create("synthetic"), null, [], null);
        // A rejected resolution must never reach replacement or Record, regardless of stale input.
        int replacements = 0, records = 0;
        Check(!AppearanceEditBridge.CommitFaceCore(() => parameter, _ => replacements++, parameter, parameter.RefreshRevision,
            edited, () => records++) && replacements == 0 && records == 0);
        // The commit kernel checks state presence, not pixels: use an empty compiled notation fixture in the existing test helper.
        var directory = Path.Combine(Path.GetDirectoryName(output)!, "face-edit-store");
        Directory.CreateDirectory(directory);
        PsdTachieNext.Tests.PsdFixture.Write(Path.Combine(directory, "synthetic.psd"), layerIds: [1, 2]);
        var compiled = new PsdTachieNext.Compiler.PsdCompiler().Compile(Path.Combine(directory, "synthetic.psd"), Path.Combine(directory, "cache"));
        using var pool = new SharedDocumentPool(4096, 1); using var doc = pool.Acquire(compiled);
        var index = PsdLayerReferenceIndex.Read(doc);
        var successful = PsdAppearanceSettings.Create("synthetic").SetVisible("synthetic", index, 0, false);
        Check(successful.Succeeded);
        foreach (var stage in Enum.GetValues<AppearanceEditFailureStage>())
        {
            var current = new CompiledFaceParameter(); var expected = current;
            var exception = new InvalidOperationException("injected");
            AppearanceEditCommitException? failure = null;
            try
            {
                AppearanceEditBridge.CommitFaceCore(() => current,
                    next => { current = next; if (stage == AppearanceEditFailureStage.ParameterReplacement) throw exception; },
                    expected, expected.RefreshRevision, successful, () => throw exception);
            }
            catch (AppearanceEditCommitException ex) { failure = ex; }
            Check(failure?.Stage == stage && failure.HistoryMayHaveChanged && ReferenceEquals(failure.InnerException, exception));
            Check(failure!.ParameterObservation == AppearanceEditParameterObservation.Replacement && current.Appearance == successful.Settings);
            Check(expected.Appearance is null && !ReferenceEquals(current, expected));
        }
        var live = new CompiledFaceParameter(); var original = live;
        Check(AppearanceEditBridge.CommitFaceCore(() => live, next => live = next, original, 0, successful, () => records++));
        Check(records == 1 && live.Appearance == successful.Settings && original.Appearance is null);
        Check(!AppearanceEditBridge.CommitFaceCore(() => live, _ => replacements++, original, 0, successful, () => records++) && replacements == 0 && records == 1);
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { total = 1, assertions, failed = 0, nativeUndo = false, nativeSave = false }));
        Console.WriteLine($"FACE CONTRACT SUMMARY: {assertions} assertions; 0 failures.");
    }
}
