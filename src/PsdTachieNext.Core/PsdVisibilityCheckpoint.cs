using System.Collections.Immutable;

namespace PsdTachieNext.Core;

/// <summary>
/// Experimental pure-state checkpoint, not the phase-D sparse patch or a native saved parameter.
/// Keys link an immutable reference forest; they are never reused as target-generation IDs.
/// Raw alias choices are retained because one-pass None normalization is not idempotent.
/// </summary>
internal sealed record PsdCheckpointNode(int Key, int? ParentKey, string OriginalName, NodeKind Kind,
    int DuplicateIndex, int DuplicateCount, int OriginKey, PsdDirectionScope AvailableScopes,
    bool LocallyVisible, PsdDirectionScope? RadioScopes);

internal sealed record PsdVisibilityCheckpoint(int Version, string AssetIdentity, string GenerationId,
    string SourceSha256, PsdFlipState Flip, ImmutableArray<PsdCheckpointNode> Nodes)
{
    public const int ExperimentalVersion = 1;

    public static PsdVisibilityCheckpoint Capture(string assetIdentity, PsdVisibilityState state)
    {
        if (string.IsNullOrWhiteSpace(assetIdentity)) throw new ArgumentException("Missing logical asset identity.", nameof(assetIdentity));
        ArgumentNullException.ThrowIfNull(state);
        var index = state.Notation;
        var selection = state.Bindings.Selection;
        var positions = new (int Index, int Count)[index.Nodes.Length];
        foreach (var group in index.Nodes.GroupBy(n => (n.ParentId, n.OriginalName, n.Kind)))
        {
            var siblings = group.ToArray();
            for (var i = 0; i < siblings.Length; i++) positions[siblings[i].NodeId] = (i, siblings.Length);
        }
        var nodes = index.Nodes.Select(n =>
        {
            var origin = selection.OriginNodeIds[n.NodeId];
            return new PsdCheckpointNode(n.NodeId, n.ParentId, n.OriginalName, n.Kind,
                positions[n.NodeId].Index, positions[n.NodeId].Count, origin, selection.SelectionScopes[origin],
                state.LocalChoices[n.NodeId], n.SelectionMarker == PsdSelectionMarker.Radio && origin == n.NodeId
                    ? state.RadioSelectionScopes.GetValueOrDefault(origin) : null);
        }).ToImmutableArray();
        return new(ExperimentalVersion, assetIdentity, index.GenerationId, index.SourceSha256, state.FlipState, nodes);
    }

    /// <summary>
    /// Atomic recovery: a failure returns no replacement state and keeps this checkpoint intact.
    /// Across generations only exact unique original hierarchy/kind is admitted. Renames, moves,
    /// duplicate correspondence and changed radio domains require explicit repair.
    /// </summary>
    public PsdCheckpointRecovery Restore(string assetIdentity, PsdNotationIndex target, bool allowUniqueHierarchyRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Version != ExperimentalVersion) throw new NotSupportedException("Unsupported experimental visibility checkpoint version.");
        if (string.IsNullOrWhiteSpace(AssetIdentity) || AssetIdentity != assetIdentity)
            return Failure("Logical asset identity differs.");
        if (!CompiledFormat.IsHash(SourceSha256) || !CompiledFormat.IsHash(GenerationId) || !Enum.IsDefined(Flip) ||
            Nodes.IsDefault || Nodes.Length > new FormatLimits().MaxNodes)
            return Failure("Malformed checkpoint identity or orientation.");
        PsdVisibilityState initial;
        try { initial = PsdVisibilityState.Create(target); }
        catch (UnsupportedPsdNotationException ex)
        { return Failure("Target notation is unsupported; explicit repair required. " + ex.Message); }
        catch (PsdFlipBindingException ex)
        { return Failure("Target flip bindings are unsupported; explicit repair required. " + ex.Message); }
        var selection = initial.Bindings.Selection;
        var exact = GenerationId == target.GenerationId && SourceSha256 == target.SourceSha256;
        if (GenerationId == target.GenerationId && !exact) return Failure("Generation and source identities disagree.");
        if (!exact && !allowUniqueHierarchyRecovery)
            return Failure("Generation changed; unique-hierarchy recovery requires explicit opt-in.");
        var mapping = new Dictionary<int, int>();
        var used = new HashSet<int>();
        var targetNames = target.Nodes.GroupBy(n => (n.ParentId, n.OriginalName, n.Kind))
            .ToDictionary(g => g.Key, g => g.Select(n => n.NodeId).ToArray());
        foreach (var saved in Nodes)
        {
            if (saved is null || saved.Key != mapping.Count || !Enum.IsDefined(saved.Kind) || saved.OriginalName is null ||
                saved.DuplicateCount < 1 || saved.DuplicateIndex < 0 || saved.DuplicateIndex >= saved.DuplicateCount ||
                (saved.ParentKey is int parentKey && !mapping.ContainsKey(parentKey)))
                return Failure("Malformed reference forest.");
            int? parent = saved.ParentKey is int key ? mapping[key] : null;
            var candidates = targetNames.GetValueOrDefault((parent, saved.OriginalName, saved.Kind), []);
            int id;
            if (exact)
            {
                if (saved.Key >= target.Nodes.Length || candidates.Length != saved.DuplicateCount ||
                    candidates[saved.DuplicateIndex] != saved.Key)
                    return Failure("Generation-local reference does not match metadata.");
                id = saved.Key;
            }
            else
            {
                if (saved.DuplicateCount != 1 || candidates.Length != 1)
                    return Failure("Missing or ambiguous original hierarchy; explicit repair required.");
                id = candidates[0];
            }
            if (!used.Add(id)) return Failure("Multiple references resolve to one node.");
            mapping.Add(saved.Key, id);
        }
        if (exact && mapping.Count != target.Nodes.Length) return Failure("Incomplete generation-local checkpoint.");
        foreach (var aliases in Nodes.GroupBy(n => n.OriginKey))
        {
            if (!mapping.TryGetValue(aliases.Key, out var origin) ||
                !selection.AliasNodeIds.TryGetValue(origin, out var targetAliases) ||
                !aliases.Select(n => mapping[n.Key]).Order().SequenceEqual(targetAliases.Order()))
                return Failure("Counterpart membership changed; explicit repair required.");
        }
        var choices = initial.LocalChoices.ToArray();
        var scopes = initial.RadioSelectionScopes.ToBuilder();
        foreach (var saved in Nodes)
        {
            var id = mapping[saved.Key];
            if (!mapping.TryGetValue(saved.OriginKey, out var origin) || selection.OriginNodeIds[id] != origin ||
                saved.AvailableScopes != selection.SelectionScopes[origin])
                return Failure("Logical origin or applicable directions changed; explicit repair required.");
            var isRadioOrigin = target.Nodes[id].SelectionMarker == PsdSelectionMarker.Radio && origin == id;
            if (isRadioOrigin != saved.RadioScopes.HasValue ||
                (saved.RadioScopes is { } mask && (mask & ~saved.AvailableScopes) != 0))
                return Failure("Invalid radio selection mask.");
            choices[id] = saved.LocallyVisible;
            if (saved.RadioScopes is { } radio)
            {
                if (radio == 0) scopes.Remove(origin); else scopes[origin] = radio;
            }
        }
        // New target defaults must not silently compete with restored choices.
        foreach (var siblings in target.Nodes.Where(n => n.SelectionMarker == PsdSelectionMarker.Radio).GroupBy(n => n.ParentId))
        {
            foreach (var direction in new[] { PsdDirectionScope.Unflipped, PsdDirectionScope.X, PsdDirectionScope.Y, PsdDirectionScope.XY })
            {
                int? chosen = null;
                foreach (var node in siblings)
                {
                    var origin = selection.OriginNodeIds[node.NodeId];
                    if ((scopes.GetValueOrDefault(origin) & selection.PhysicalScopes[node.NodeId] & direction) == 0) continue;
                    if (chosen is int previous && previous != origin)
                        return Failure("Radio choices conflict after reference recovery.");
                    chosen = origin;
                }
            }
        }
        foreach (var origin in selection.AliasNodeIds.Keys)
        {
            var node = target.Nodes[origin];
            var selected = selection.AliasNodeIds[origin].Any(id => choices[id]);
            if (node.SelectionMarker == PsdSelectionMarker.Radio && selected != scopes.ContainsKey(origin))
                return Failure("Radio mask and logical choices disagree.");
            if (node.SelectionMarker == PsdSelectionMarker.ForceVisible && !selected)
                return Failure("A forced logical part cannot be cleared.");
        }
        var candidate = initial.RestoreChoices(choices.ToImmutableArray(), scopes.ToImmutable(), Flip);
        if (candidate.PhysicalRadioRepairReason() is { } reason) return Failure(reason);
        return new(this, candidate, null);
    }

    private PsdCheckpointRecovery Failure(string reason) => new(this, null, reason);
}

internal sealed record PsdCheckpointRecovery(PsdVisibilityCheckpoint Checkpoint, PsdVisibilityState? State, string? RepairReason)
{
    public bool Succeeded => State is not null;
}
