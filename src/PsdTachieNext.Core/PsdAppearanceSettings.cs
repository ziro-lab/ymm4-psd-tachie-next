using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsdTachieNext.Core;

public sealed record PsdAppearanceRepair(PersistentPsdLayerRef? Reference, ImmutableArray<int> Candidates, string Reason);
public sealed record PsdAppearanceResolution(PsdAppearanceSettings? Settings, PsdVisibilityState? State,
    ImmutableArray<PsdAppearanceRepair> Repairs, string? Reason)
{
    public bool Succeeded => State is not null;
}

/// <summary>Immutable, lossless JSON envelope. Only authored targets/scopes are owned; absent values inherit source defaults.</summary>
public sealed partial record PsdAppearanceSettings
{
    public string Json { get; }
    public const int CurrentVersion = 1;
    private PsdAppearanceSettings(string json) { Json = json; }

    public static PsdAppearanceSettings FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > new FormatLimits().MaxManifestBytes) throw new JsonException("Appearance exceeds the size limit.");
        using var document = JsonDocument.Parse(json);
        var pending = new Stack<JsonElement>(); pending.Push(document.RootElement);
        while (pending.TryPop(out var element))
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate appearance property.");
                    pending.Push(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) pending.Push(child);
        }
        return new(json);
    }

    public static PsdAppearanceSettings Create(string assetIdentity)
    {
        if (string.IsNullOrWhiteSpace(assetIdentity)) throw new ArgumentException("Missing asset identity.", nameof(assetIdentity));
        return FromJson(new JsonObject { ["version"] = CurrentVersion, ["defaults"] = 1, ["asset"] = assetIdentity,
            ["flip"] = null, ["overrides"] = new JsonArray() }.ToJsonString());
    }

    public static PsdAppearanceResolution ResolveOrDefault(PsdAppearanceSettings? settings, string assetIdentity, PsdLayerReferenceIndex index)
    {
        if (settings is not null) return settings.Resolve(assetIdentity, index);
        try { return new(null, PsdVisibilityState.Create(index.Notation), [], null); }
        catch (NotSupportedException ex) when (ex is UnsupportedPsdNotationException or PsdFlipBindingException)
        { return new(null, null, [], ex.Message); }
    }

    private sealed record AliasProof(PersistentPsdLayerRef Layer, PsdDirectionScope Scope, PersistentPsdLayerRef? RadioParent);
    private sealed record Operation(JsonObject Raw, int Origin, bool? Visible, PsdDirectionScope? Radio, bool Canonical);

    public PsdAppearanceResolution Resolve(string assetIdentity, PsdLayerReferenceIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var repairs = ImmutableArray.CreateBuilder<PsdAppearanceRepair>();
        try
        {
            var root = Root(assetIdentity);
            var initial = PsdVisibilityState.Create(index.Notation);
            var operations = ReadOperations(root, index, repairs);
            if (repairs.Count != 0) return new(this, null, repairs.ToImmutable(), "Unresolved appearance references; confirm repair candidates.");
            var choices = initial.LocalChoices.ToArray();
            var scopes = initial.RadioSelectionScopes.ToBuilder();
            var selection = initial.Bindings.Selection;
            var radios = operations.Where(op => op.Radio.HasValue).ToArray();
            foreach (var op in radios)
            {
                var coverage = op.Radio!.Value;
                foreach (var other in radios)
                {
                    if (other.Origin == op.Origin) continue;
                    var overlap = Peers(index.Notation, other.Origin, other.Radio!.Value).GetValueOrDefault(op.Origin);
                    if ((op.Radio.Value & overlap) != 0) throw new InvalidDataException("Authored radio choices overlap.");
                    coverage |= overlap;
                }
                if (coverage != selection.SelectionScopes[op.Origin] &&
                    selection.AliasNodeIds[op.Origin].Any(alias => alias != op.Origin && initial.LocalChoices[alias]))
                    throw new InvalidDataException("Partial authored radio coverage does not retain complete explicit-selection provenance.");
            }
            foreach (var op in radios)
                foreach (var (peer, overlap) in Peers(index.Notation, op.Origin, op.Radio!.Value))
                {
                    var remaining = scopes.GetValueOrDefault(peer) & ~overlap;
                    if (remaining == 0) { scopes.Remove(peer); Clear(peer); }
                    else scopes[peer] = remaining;
                }
            foreach (var op in operations)
            {
                if (op.Radio is { } radio)
                {
                    Clear(op.Origin); choices[op.Origin] = true;
                    scopes[op.Origin] = scopes.GetValueOrDefault(op.Origin) | radio;
                }
                else if (op.Visible is bool visible && (op.Canonical || choices[op.Origin] != visible))
                { Clear(op.Origin); choices[op.Origin] = visible; }
            }
            var flip = root["flip"] is null ? PsdFlipState.None : (PsdFlipState)root["flip"]!.GetValue<int>();
            if (!Enum.IsDefined(flip)) throw new InvalidDataException("Unknown orientation.");
            var candidate = initial.RestoreChoices(choices.ToImmutableArray(), scopes.ToImmutable(), flip);
            if (candidate.PhysicalRadioRepairReason() is { } conflict) throw new InvalidDataException(conflict);
            return new(this, candidate, [], null);
            void Clear(int origin) { foreach (var alias in selection.AliasNodeIds[origin]) choices[alias] = false; }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException
            or UnsupportedPsdNotationException or PsdFlipBindingException)
        { return new(this, null, repairs.ToImmutable(), ex.Message); }
    }

    /// <summary>One immutable edit result. Reselecting a radio owns its full supported domain, even at the current default value.</summary>
    public PsdAppearanceResolution SetVisible(string assetIdentity, PsdLayerReferenceIndex index, int origin, bool visible)
    {
        var before = Resolve(assetIdentity, index);
        if (!before.Succeeded) return before;
        try
        {
            var state = before.State!;
            var selection = state.Bindings.Selection;
            if ((uint)origin >= (uint)index.Notation.Nodes.Length || selection.OriginNodeIds[origin] != origin)
                throw new ArgumentException("Edit a resolved logical origin.");
            var marker = index.Notation.Nodes[origin].SelectionMarker;
            if (!visible && marker != PsdSelectionMarker.Ordinary) throw new InvalidOperationException("Forced/radio selections cannot be deselected.");
            var root = Root(assetIdentity);
            var operations = ReadOperations(root, index, ImmutableArray.CreateBuilder<PsdAppearanceRepair>());
            var existing = operations.SingleOrDefault(op => op.Origin == origin);
            JsonObject row;
            if (existing is not null) row = existing.Raw;
            else
            {
                row = new JsonObject { ["target"] = JsonSerializer.SerializeToNode(index.Capture(origin)),
                    ["proof"] = JsonSerializer.SerializeToNode(CaptureProof(index, origin)) };
                root["overrides"]!.AsArray().Add(row);
            }
            if (marker == PsdSelectionMarker.Radio)
            {
                var allowed = selection.SelectionScopes[origin];
                var peers = Peers(index.Notation, origin, allowed);
                foreach (var op in operations.Where(op => op.Radio.HasValue && op.Origin != origin))
                {
                    var remaining = op.Radio!.Value & ~peers.GetValueOrDefault(op.Origin);
                    op.Raw["radio"] = remaining == 0 ? null : JsonValue.Create((int)remaining);
                }
                row["visible"] = null; row["radio"] = (int)allowed; row["canonical"] = true;
            }
            else
            {
                row["radio"] = null; row["visible"] = visible;
                row["canonical"] = existing?.Canonical == true || state.IsLocallyVisible(origin) != visible;
            }
            return VerifyEdit(FromJson(root.ToJsonString()), assetIdentity, index, state.SetVisible(origin, visible));
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return new(this, null, [], ex.Message); }
    }

    public PsdAppearanceResolution WithFlip(string assetIdentity, PsdLayerReferenceIndex index, PsdFlipState? flip)
    {
        var before = Resolve(assetIdentity, index);
        if (!before.Succeeded) return before;
        if (flip is { } value && !Enum.IsDefined(value)) return new(this, null, [], "Unknown orientation.");
        var root = Root(assetIdentity); root["flip"] = flip is null ? null : JsonValue.Create((int)flip.Value);
        return VerifyEdit(FromJson(root.ToJsonString()), assetIdentity, index, before.State!.WithFlip(flip ?? PsdFlipState.None));
    }

    public PsdAppearanceResolution Inherit(string assetIdentity, PsdLayerReferenceIndex index, int origin)
    {
        var before = Resolve(assetIdentity, index);
        if (!before.Succeeded) return before;
        var root = Root(assetIdentity);
        foreach (var row in ReadOperations(root, index, ImmutableArray.CreateBuilder<PsdAppearanceRepair>()).Where(op => op.Origin == origin))
        { row.Raw["visible"] = null; row.Raw["radio"] = null; row.Raw["canonical"] = null; }
        var result = FromJson(root.ToJsonString()).Resolve(assetIdentity, index);
        return result.Succeeded ? result : new(this, null, result.Repairs, result.Reason);
    }

    private PsdAppearanceResolution VerifyEdit(PsdAppearanceSettings edited, string assetIdentity, PsdLayerReferenceIndex index, PsdVisibilityState expected)
    {
        var restored = edited.Resolve(assetIdentity, index);
        if (!restored.Succeeded) return new(this, null, restored.Repairs, restored.Reason);
        var actual = restored.State!;
        if (actual.FlipState != expected.FlipState || !actual.LocalChoices.SequenceEqual(expected.LocalChoices) ||
            !actual.RadioSelectionScopes.OrderBy(p => p.Key).SequenceEqual(expected.RadioSelectionScopes.OrderBy(p => p.Key)))
            return new(this, null, [], "Sparse edit reconstruction would change unrelated logical intent.");
        return restored;
    }

    private JsonObject Root(string assetIdentity)
    {
        var root = JsonNode.Parse(Json) as JsonObject ?? throw new InvalidDataException("Appearance envelope must be an object.");
        if (root["version"]?.GetValue<int>() != CurrentVersion || root["defaults"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Unsupported appearance schema/default rules; preserved without evaluation.");
        if (string.IsNullOrWhiteSpace(assetIdentity) || root["asset"]?.GetValue<string>() != assetIdentity)
            throw new InvalidDataException("Appearance asset identity differs.");
        if (root["overrides"] is not JsonArray rows || rows.Count > new FormatLimits().MaxNodes)
            throw new InvalidDataException("Invalid sparse override collection.");
        return root;
    }

    private static List<Operation> ReadOperations(JsonObject root, PsdLayerReferenceIndex index,
        ImmutableArray<PsdAppearanceRepair>.Builder repairs)
    {
        var selection = index.Notation.FlipBindings.Selection;
        var result = new List<Operation>(); var owned = new HashSet<int>();
        foreach (var raw in root["overrides"]!.AsArray())
        {
            var row = raw as JsonObject ?? throw new InvalidDataException("Invalid sparse operation.");
            var visible = row["visible"]?.GetValue<bool>();
            var radio = row["radio"] is null ? (PsdDirectionScope?)null : (PsdDirectionScope)row["radio"]!.GetValue<int>();
            var canonical = row["canonical"]?.GetValue<bool>() ?? false;
            if (visible is null && radio is null) continue; // Unknown extension data is retained, not promoted into owned settings.
            if (visible is not null && radio is not null) throw new InvalidDataException("Mixed visible/radio operation.");
            var reference = row["target"]?.Deserialize<PersistentPsdLayerRef>() ?? throw new InvalidDataException("Missing logical reference.");
            var resolved = index.Resolve(reference);
            if (resolved.NodeId is not int origin) { repairs.Add(new(reference, resolved.Candidates, resolved.Reason!)); continue; }
            if (selection.OriginNodeIds[origin] != origin || !owned.Add(origin)) throw new InvalidDataException("Duplicate or non-origin operation.");
            var node = index.Notation.Nodes[origin];
            if (radio is { } mask && (node.SelectionMarker != PsdSelectionMarker.Radio || mask == 0 || (mask & ~selection.SelectionScopes[origin]) != 0))
                throw new InvalidDataException("Invalid authored radio mask.");
            if (visible is bool flag && (node.SelectionMarker == PsdSelectionMarker.Radio || (!flag && node.SelectionMarker == PsdSelectionMarker.ForceVisible)))
                throw new InvalidDataException("Invalid ordinary/force visibility override.");
            var proofs = row["proof"]?.Deserialize<ImmutableArray<AliasProof>>() ?? throw new InvalidDataException("Missing binding proof.");
            if (proofs.IsDefault || proofs.Length != selection.AliasNodeIds[origin].Length) throw new InvalidDataException("Counterpart membership changed.");
            var aliases = new HashSet<int>();
            foreach (var proof in proofs)
            {
                if (proof is null) throw new InvalidDataException("Invalid binding proof.");
                var alias = index.Resolve(proof.Layer);
                if (alias.NodeId is not int id) { repairs.Add(new(proof.Layer, alias.Candidates, alias.Reason!)); continue; }
                if (!aliases.Add(id) || selection.OriginNodeIds[id] != origin || selection.PhysicalScopes[id] != proof.Scope)
                    throw new InvalidDataException("Logical binding or direction scope changed.");
                var physical = index.Notation.Nodes[id];
                if (physical.SelectionMarker == PsdSelectionMarker.Radio && physical.ParentId is int parent)
                {
                    if (proof.RadioParent is null) throw new InvalidDataException("Radio parent changed.");
                    var resolvedParent = index.Resolve(proof.RadioParent);
                    if (resolvedParent.NodeId is not int group) { repairs.Add(new(proof.RadioParent, resolvedParent.Candidates, resolvedParent.Reason!)); continue; }
                    if (group != parent) throw new InvalidDataException("Radio domain changed.");
                }
                else if (proof.RadioParent is not null) throw new InvalidDataException("Radio parent changed.");
            }
            result.Add(new(row, origin, visible, radio, canonical));
        }
        return result;
    }

    private static ImmutableArray<AliasProof> CaptureProof(PsdLayerReferenceIndex index, int origin)
        => index.Notation.FlipBindings.Selection.AliasNodeIds[origin].Select(id =>
        {
            var node = index.Notation.Nodes[id];
            return new AliasProof(index.Capture(id), index.Notation.FlipBindings.Selection.PhysicalScopes[id],
                node.SelectionMarker == PsdSelectionMarker.Radio && node.ParentId is int parent ? index.Capture(parent) : null);
        }).ToImmutableArray();

    private static Dictionary<int, PsdDirectionScope> Peers(PsdNotationIndex notation, int origin, PsdDirectionScope scope)
    {
        var selection = notation.FlipBindings.Selection;
        var result = new Dictionary<int, PsdDirectionScope>();
        foreach (var alias in selection.AliasNodeIds[origin])
        {
            if ((selection.PhysicalScopes[alias] & scope) == 0) continue;
            foreach (var sibling in notation.Children(notation.Nodes[alias].ParentId))
            {
                if (notation.Nodes[sibling].SelectionMarker != PsdSelectionMarker.Radio) continue;
                var overlap = scope & selection.PhysicalScopes[alias] & selection.PhysicalScopes[sibling];
                var peer = selection.OriginNodeIds[sibling];
                if (overlap != 0) result[peer] = result.GetValueOrDefault(peer) | overlap;
            }
        }
        return result;
    }
}
