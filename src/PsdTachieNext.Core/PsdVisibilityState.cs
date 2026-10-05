using System.Collections.Immutable;

namespace PsdTachieNext.Core;

/// <summary>
/// Generation-local logical choices in the normal orientation. Flip evaluation is pure;
/// counterpart activation may override force-visible flags without erasing hidden-parent intent.
/// This is not a persistent sparse-expression schema or a palette/Undo implementation.
/// </summary>
public sealed class PsdVisibilityState
{
    private readonly PsdNotationIndex notation;
    private readonly ImmutableArray<bool> local;
    private readonly ImmutableDictionary<int, ImmutableArray<int>> radios;
    public PsdFlipBindingIndex Bindings => notation.FlipBindings;
    public PsdFlipState FlipState { get; }
    public ImmutableArray<int> EnabledNodeIds { get; }
    public ImmutableDictionary<int, PsdDirectionScope> RadioSelectionScopes { get; }
    public string GenerationId => notation.GenerationId;
    internal PsdNotationIndex Notation => notation;
    internal ImmutableArray<bool> LocalChoices => local;
    internal PsdVisibilityState RestoreChoices(ImmutableArray<bool> choices,
        ImmutableDictionary<int, PsdDirectionScope> scopes, PsdFlipState flip)
        => new(notation, choices, radios, flip, radioSelectionScopes: scopes);

    internal string? PhysicalRadioRepairReason()
    {
        var parents = notation.Nodes.Where(n => n.SelectionMarker == PsdSelectionMarker.Radio)
            .ToDictionary(n => n.NodeId, n => n.ParentId ?? -1);
        foreach (var orientation in Enum.GetValues<PsdFlipState>())
        {
            var selected = new HashSet<int>();
            foreach (var id in WithFlip(orientation).EnabledNodeIds)
                if (parents.TryGetValue(id, out var parent) && !selected.Add(parent))
                    return "Evaluated physical radio choices conflict in " + orientation + "; explicit repair required.";
        }
        return null;
    }

    private PsdVisibilityState(PsdNotationIndex notation, ImmutableArray<bool> local,
        ImmutableDictionary<int, ImmutableArray<int>> radios, PsdFlipState flipState, bool normalizeInitialNone = false,
        ImmutableDictionary<int, PsdDirectionScope>? radioSelectionScopes = null)
    {
        this.notation = notation; this.radios = radios; FlipState = flipState;
        // Saved variants normalize exactly once. Missing nested matches can make None non-idempotent.
        if (normalizeInitialNone)
        {
            var normalized = Evaluate(local, PsdFlipState.None, initializing: true);
            // A saved directional radio must not leave the normal-side radio domain without a choice.
            foreach (var siblings in radios.Values)
            {
                var common = siblings.Where(id => Bindings.Selection.OriginNodeIds[id] == id &&
                    !Bindings.Selection.DirectionOnlyScopes.ContainsKey(id)).ToArray();
                // A selected common alias already supplies this domain after the one-pass normalization.
                var hasCommonChoice = siblings.Any(id => normalized[id] &&
                    !Bindings.Selection.DirectionOnlyScopes.ContainsKey(Bindings.Selection.OriginNodeIds[id]));
                if (common.Length > 0 && !hasCommonChoice) normalized[common[0]] = true;
            }
            this.local = normalized.ToImmutableArray();
        }
        else this.local = local;
        RadioSelectionScopes = radioSelectionScopes ?? radios.Values.SelectMany(ids => ids)
            .Select(id => Bindings.Selection.OriginNodeIds[id]).Distinct()
            .Where(origin => Bindings.Selection.AliasNodeIds[origin].Any(alias => this.local[alias]))
            .ToImmutableDictionary(origin => origin, origin => Bindings.Selection.SelectionScopes[origin]);
        var physical = Evaluate(this.local, flipState).ToImmutableArray();
        EnabledNodeIds = Enumerable.Range(0, physical.Length).Where(id => physical[id]).ToImmutableArray();
    }

    /// <summary>Strict diagnostic-free seam for focused validation.</summary>
    public static PsdVisibilityState CreateVerified(PsdNotationIndex notation)
        => CreateCore(notation, requireComplete: true);

    /// <summary>Reference-defined missing counterparts/children retain choices and diagnostic metadata.</summary>
    public static PsdVisibilityState Create(PsdNotationIndex notation)
        => CreateCore(notation, requireComplete: false);

    private static PsdVisibilityState CreateCore(PsdNotationIndex notation, bool requireComplete)
    {
        var initial = PsdPrefixVisibility.CreateForFlipBindings(notation);
        var blocking = notation.FlipBindings.Diagnostics.Where(d => requireComplete || d.BlocksPreparation).ToImmutableArray();
        if (!blocking.IsEmpty) throw new PsdFlipBindingException(blocking);
        var radios = notation.Nodes.Where(n => n.SelectionMarker == PsdSelectionMarker.Radio)
            .GroupBy(n => n.ParentId ?? -1).ToImmutableDictionary(g => g.Key,
                g => g.OrderBy(n => n.Order).Select(n => n.NodeId).ToImmutableArray());
        var values = new bool[notation.Nodes.Length];
        foreach (var id in initial.EnabledNodeIds) values[id] = true;
        // Reference initialization normalizes prefixes, then brings saved-visible variants back to None.
        return new(notation, values.ToImmutableArray(), radios, PsdFlipState.None, normalizeInitialNone: true);
    }

    public bool IsLocallyVisible(int nodeId) { ValidateNode(nodeId); return local[nodeId]; }
    /// <summary>Derive an orientation from current choices, including edits made while flipped.</summary>
    public PsdVisibilityState WithFlip(PsdFlipState flipState)
    {
        if (!Enum.IsDefined(flipState)) throw new ArgumentOutOfRangeException(nameof(flipState));
        return flipState == FlipState ? this : new(notation, local, radios, flipState,
            radioSelectionScopes: RadioSelectionScopes);
    }

    /// <summary>
    /// Edit explicit selection-origin IDs. Common edits survive None; direction-only edits retain
    /// their own memory and are displayed only in their structural scopes. Counterpart aliases
    /// are rejected, never matched by display name. Explicit radio selection wins in its
    /// applicable directions; competing choices retain scopes outside the actual overlap.
    /// </summary>
    public PsdVisibilityState SetVisible(int nodeId, bool visible)
    {
        ValidateNode(nodeId);
        var index = Bindings.Selection;
        if (index.OriginNodeIds[nodeId] != nodeId)
            throw new ArgumentException("Edit the explicit normal counterpart ID.", nameof(nodeId));
        var node = notation.Nodes[nodeId];
        if (!visible && node.SelectionMarker != PsdSelectionMarker.Ordinary) return this;
        if (local[nodeId] == visible && node.SelectionMarker != PsdSelectionMarker.Radio) return this;
        var next = local.ToArray();
        ClearAliases(next, nodeId); next[nodeId] = visible;
        var selection = RadioSelectionScopes;
        if (node.SelectionMarker == PsdSelectionMarker.Radio)
        {
            var allowed = index.SelectionScopes[nodeId];
            var nextScopes = RadioSelectionScopes.ToBuilder();
            foreach (var (peer, overlap) in RadioPeers(nodeId, allowed))
            {
                if (peer == nodeId) continue;
                var remaining = RadioSelectionScopes.GetValueOrDefault(peer) & ~overlap;
                if (remaining == 0)
                {
                    nextScopes.Remove(peer); next[peer] = false; ClearAliases(next, peer);
                }
                else nextScopes[peer] = remaining; // Preserve raw alias intent outside the overlap.
            }
            nextScopes[nodeId] = allowed;
            selection = nextScopes.ToImmutable();
        }
        if (next.SequenceEqual(local) && selection.Count == RadioSelectionScopes.Count &&
            selection.All(p => RadioSelectionScopes.GetValueOrDefault(p.Key) == p.Value)) return this;
        return new(notation, next.ToImmutableArray(), radios, FlipState, radioSelectionScopes: selection);
    }

    internal void ValidateGeneration(CompiledManifest manifest)
    {
        if (manifest.GenerationId != GenerationId || manifest.Source.Sha256 != notation.SourceSha256 || manifest.Nodes.Length != local.Length)
            throw new ArgumentException("Visibility belongs to a different compiled generation.", nameof(manifest));
    }

    private bool[] Evaluate(ImmutableArray<bool> choices, PsdFlipState flip, bool initializing = false)
    {
        var result = choices.ToArray();
        var index = Bindings.Selection;
        var scope = PsdFlipSelectionIndex.Scope(flip);
        if (!initializing)
        {
            foreach (var (origin, allowed) in index.DirectionOnlyScopes)
                if (notation.Nodes[origin].SelectionMarker != PsdSelectionMarker.Radio && (allowed & scope) == 0)
                    foreach (var alias in index.AliasNodeIds[origin]) result[alias] = false;
            foreach (var origin in radios.Values.SelectMany(ids => ids).Select(id => index.OriginNodeIds[id]).Distinct())
                if ((RadioSelectionScopes.GetValueOrDefault(origin) & scope) == 0)
                    foreach (var alias in index.AliasNodeIds[origin]) result[alias] = false;
            // None consumes canonical choices directly: never run flip-off normalization twice.
            if (flip == PsdFlipState.None) return result;
            foreach (var siblings in radios.Values)
            {
                var selected = siblings.FirstOrDefault(id => result[id] && IsDirectional(id), -1);
                if (selected < 0) selected = siblings.FirstOrDefault(id => result[id], -1);
                foreach (var id in siblings) result[id] = id == selected;
            }
        }
        bool IsDirectional(int id)
        {
            var origin = index.OriginNodeIds[id];
            return index.DirectionOnlyScopes.TryGetValue(origin, out var allowed) && (allowed & scope) != 0 &&
                (notation.Nodes[origin].SelectionMarker == PsdSelectionMarker.Radio
                    ? (RadioSelectionScopes.GetValueOrDefault(origin) & scope) != 0 : choices[origin]);
        }
        void SetPhysical(int id, bool visible)
        {
            if (visible && notation.Nodes[id].SelectionMarker == PsdSelectionMarker.Radio)
            {
                var siblings = radios[notation.Nodes[id].ParentId ?? -1];
                if (!initializing && !IsDirectional(id) && siblings.Any(peer => result[peer] && IsDirectional(peer)))
                { result[id] = false; return; }
                foreach (var sibling in siblings) result[sibling] = false;
            }
            result[id] = visible;
        }
        void Apply(PsdFlipTargets target, bool enabled)
        {
            foreach (var pair in Bindings.Pairs)
            {
                if ((pair.Targets & target) == 0) continue;
                if (enabled ? !result[pair.NormalId] : !result[pair.FlippedId]) continue;
                foreach (var child in pair.Children)
                    SetPhysical(enabled ? child.FlippedId : child.NormalId, result[enabled ? child.NormalId : child.FlippedId]);
                SetPhysical(enabled ? pair.FlippedId : pair.NormalId, true);
                SetPhysical(enabled ? pair.NormalId : pair.FlippedId, false);
            }
        }
        switch (flip)
        {
            case PsdFlipState.None: Apply(PsdFlipTargets.X, false); Apply(PsdFlipTargets.Y, false); Apply(PsdFlipTargets.XY, false); break;
            case PsdFlipState.X: Apply(PsdFlipTargets.Y, false); Apply(PsdFlipTargets.XY, false); Apply(PsdFlipTargets.X, true); break;
            case PsdFlipState.Y: Apply(PsdFlipTargets.XY, false); Apply(PsdFlipTargets.X, false); Apply(PsdFlipTargets.Y, true); break;
            case PsdFlipState.XY: Apply(PsdFlipTargets.X, false); Apply(PsdFlipTargets.Y, false); Apply(PsdFlipTargets.XY, true); break;
        }
        return result;
    }

    private void ClearAliases(bool[] values, int origin)
    {
        foreach (var alias in Bindings.Selection.AliasNodeIds[origin]) if (alias != origin) values[alias] = false;
    }
    private IEnumerable<KeyValuePair<int, PsdDirectionScope>> RadioPeers(int origin, PsdDirectionScope scope)
    {
        var index = Bindings.Selection;
        var peers = new Dictionary<int, PsdDirectionScope>();
        foreach (var alias in index.AliasNodeIds[origin])
        {
            if ((index.PhysicalScopes[alias] & scope) == 0 || notation.Nodes[alias].SelectionMarker != PsdSelectionMarker.Radio) continue;
            foreach (var sibling in radios[notation.Nodes[alias].ParentId ?? -1])
            {
                var peer = index.OriginNodeIds[sibling];
                var overlap = scope & index.PhysicalScopes[alias] & index.PhysicalScopes[sibling];
                if (overlap != 0) peers[peer] = peers.GetValueOrDefault(peer) | overlap;
            }
        }
        return peers;
    }
    private void ValidateNode(int id)
    { if ((uint)id >= (uint)local.Length) throw new ArgumentOutOfRangeException(nameof(id)); }
}
