using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PsdTachieNext.Core;
using PsdTachieNext.Ymm4;
using PsdTachieNext.Compiler;
using PsdTachieNext.Tests;
using YukkuriMovieMaker.Plugin.Tachie;

// Managed parameter contract only. Does not launch YMM4 or prove native Undo/save.
var host = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    var candidate = Path.Combine(host, name.Name + ".dll");
    return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
};
try
{
    ParameterCases.Run(args[1]);
    AppearanceEditCases.Run(Path.Combine(Path.GetDirectoryName(args[1])!, "appearance-edit-failure-results.json"));
    FaceParameterCases.Run(Path.Combine(Path.GetDirectoryName(args[1])!, "face-parameter-results.json"));
    PaletteProjectionCases.Run(Path.Combine(Path.GetDirectoryName(args[1])!, "palette-projection-results.json"));
    PaletteHierarchyCases.Run(Path.Combine(Path.GetDirectoryName(args[1])!, "palette-hierarchy-results.json"));
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static class ParameterCases
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Run(string output)
    {
        int assertions = 0;
        void Check(bool condition, string message)
        { assertions++; if (!condition) throw new InvalidOperationException(message); }
        var source = new SourceAssetRef(1, "synthetic-asset", Path.Combine(Path.GetDirectoryName(output)!, "synthetic.psd"));
        var appearance = PsdAppearanceSettings.FromJson("{\"version\":999,\"defaults\":1,\"asset\":\"synthetic-asset\",\"flip\":2,\"overrides\":[],\"future\":{\"opaque\":[null,false,123]}}");
        var parameter = new CompiledItemParameter { Source = source };
        var changes = new List<string?>();
        parameter.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var revision = parameter.RefreshRevision;
        parameter.Appearance = appearance;
        Check(parameter.RefreshRevision == revision + 1, "one appearance revision");
        Check(changes.SequenceEqual(new[] { "Appearance" }), "one base Set notification");
        parameter.Appearance = PsdAppearanceSettings.FromJson(appearance.Json);
        Check(parameter.RefreshRevision == revision + 1 && changes.Count == 1, "equal settings no setter event");
        var json = JsonConvert.SerializeObject(parameter);
        var root = JObject.Parse(json);
        Check(root["Appearance"]!.Value<string>() == appearance.Json, "appearance stored as exact opaque JSON text");
        Check(root["RefreshRevision"] is null && root["File"] is null, "runtime values omitted");
        var restored = JsonConvert.DeserializeObject<CompiledItemParameter>(json)!;
        Check(restored.Source == source, "logical source restored");
        Check(restored.Appearance == appearance, "unknown version and data restored");
        var clone = parameter.CreateEquivalentRefreshClone();
        Check(!ReferenceEquals(clone, parameter) && clone.Source == parameter.Source, "new owner same source");
        Check(ReferenceEquals(clone.Appearance, appearance), "immutable setting shared safely");
        Check(clone.RefreshRevision == parameter.RefreshRevision, "clone creates no setter revision");
        clone.Appearance = PsdAppearanceSettings.Create(source.AssetIdentity);
        Check(ReferenceEquals(parameter.Appearance, appearance), "clone edit leaves original intact");
        clone.Appearance = null;
        Check(JObject.Parse(JsonConvert.SerializeObject(clone))["Appearance"] is null, "null old default omitted");
        var legacy = JsonConvert.DeserializeObject<CompiledItemParameter>("{\"Source\":" + JsonConvert.SerializeObject(source) + "}")!;
        Check(legacy.Appearance is null && legacy.Source == source, "legacy source-only default");
        var explicitOff = PsdAppearanceSettings.FromJson("{\"version\":1,\"defaults\":1,\"asset\":\"synthetic-asset\",\"flip\":0,\"overrides\":[{\"visible\":false,\"future\":\"kept\"}]}");
        parameter.Appearance = explicitOff;
        restored = JsonConvert.DeserializeObject<CompiledItemParameter>(JsonConvert.SerializeObject(parameter))!;
        Check(restored.Appearance == explicitOff, "explicit off and None kept without attempting resolve");
        bool duplicateRejected = false;
        try { JsonConvert.DeserializeObject<CompiledItemParameter>("{\"Appearance\":{\"version\":1,\"version\":2}}"); }
        catch (JsonException) { duplicateRejected = true; }
        Check(duplicateRejected, "unexpected storage shape rejected");
        var opaque = PsdAppearanceSettings.FromJson("{\"version\":999,\"date\":\"2026-10-04T12:00:00+09:00\",\"number\":0.123456789012345678901234567890123456789,\"large\":1e9999}");
        clone.Appearance = opaque;
        Check(JsonConvert.DeserializeObject<CompiledItemParameter>(JsonConvert.SerializeObject(clone))!.Appearance!.Json == opaque.Json,
            "unknown dates and unbounded numeric precision retained exactly");
        revision = parameter.RefreshRevision;
        parameter.Appearance = appearance; parameter.Appearance = explicitOff;
        Check(parameter.RefreshRevision == revision + 2, "appearance ABA counted");
        ITachieItemParameter current = parameter;
        int replacements = 0;
        Check(CompiledParameterRefreshBridge.TryReplaceEquivalent(parameter, () => current,
            next => { replacements++; current = next; }, out var replacement), "owner equivalent refresh");
        Check(replacements == 1 && replacement?.Appearance == explicitOff, "refresh carries saved intent");
        Check(!CompiledParameterRefreshBridge.TryReplaceEquivalent(parameter, () => current,
            _ => replacements++, out _), "stale owner rejected");
        Check(!CompiledParameterRefreshBridge.TryReplaceEquivalent(replacement!, () => current,
            _ => replacements++, out _, () => false), "retired owner rejected");
        int guards = 0;
        Check(!CompiledParameterRefreshBridge.TryReplaceEquivalent(replacement!, () => current,
            _ => replacements++, out _, () => { if (++guards == 1) replacement!.Appearance = appearance; return true; }),
            "revision changed during bridge rejected");
        Check(replacements == 1, "no stale replacements");
        PsdFixture.Write(source.Path, visibleName: "*base", hiddenName: "*other", layerIds: [11, 12]);
        var compiled = new PsdCompiler().Compile(source.Path, Path.Combine(Path.GetDirectoryName(output)!, "synthetic-cache"));
        using (var pool = new SharedDocumentPool(4096, 1))
        using (var doc = pool.Acquire(compiled))
        {
            var index = PsdLayerReferenceIndex.Read(doc);
            var authored = PsdAppearanceSettings.Create(source.AssetIdentity).SetVisible(source.AssetIdentity, index, 1, true)
                .Settings!.WithFlip(source.AssetIdentity, index, PsdFlipState.XY).Settings!;
            parameter.Appearance = authored;
            restored = JsonConvert.DeserializeObject<CompiledItemParameter>(JsonConvert.SerializeObject(parameter))!;
            var resolved = restored.Appearance!.Resolve(restored.Source!.AssetIdentity, index);
            Check(resolved.Succeeded && resolved.State!.EnabledNodeIds.SequenceEqual([1]) && resolved.State.FlipState == PsdFlipState.XY,
                "real synthetic authored refs mask and flip survive parameter JSON");
            clone = parameter.CreateEquivalentRefreshClone();
            clone.Appearance = clone.Appearance!.SetVisible(source.AssetIdentity, index, 0, true).Settings;
            Check(parameter.Appearance!.Resolve(source.AssetIdentity, index).State!.EnabledNodeIds.SequenceEqual([1]) &&
                clone.Appearance!.Resolve(source.AssetIdentity, index).State!.EnabledNodeIds.SequenceEqual([0]), "real radio clone edits independent");
        }
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new
        { total = 1, assertions, failed = 0, nativeUndo = false, nativeSave = false, guiLaunched = false }));
        Console.WriteLine($"PARAMETER SUMMARY: 1/1; {assertions} assertions; 0 failures.");
    }
}
