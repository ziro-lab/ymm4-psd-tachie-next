using System.Collections.Immutable;

namespace PsdTachieNext.Core;

[Flags]
public enum PsdDirectionScope { Unflipped = 1, X = 2, Y = 4, XY = 8, All = 15 }

/// <summary>Raw-ID selection ownership and structural direction scopes; no display-name matching.</summary>
public sealed class PsdFlipSelectionIndex
{
    public ImmutableArray<int> OriginNodeIds { get; }
    public ImmutableDictionary<int, ImmutableArray<int>> AliasNodeIds { get; }
    public ImmutableArray<PsdDirectionScope> PhysicalScopes { get; }
    public ImmutableDictionary<int, PsdDirectionScope> SelectionScopes { get; }
    public ImmutableDictionary<int, PsdDirectionScope> DirectionOnlyScopes { get; }
    public ImmutableArray<PsdFlipBindingDiagnostic> Diagnostics { get; }

    internal PsdFlipSelectionIndex(PsdNotationIndex notation, PsdFlipBindingIndex bindings)
    {
        var count = notation.Nodes.Length;
        var issues = ImmutableArray.CreateBuilder<PsdFlipBindingDiagnostic>();
        var dependencies = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        foreach (var pair in bindings.Pairs)
        {
            dependencies[pair.FlippedId].Add(pair.NormalId);
            foreach (var child in pair.Children) dependencies[child.FlippedId].Add(child.NormalId);
        }
        var dependants = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        var remaining = dependencies.Select(d => d.Count).ToArray();
        var roots = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        var queue = new Queue<int>();
        for (var id = 0; id < count; id++)
        {
            foreach (var source in dependencies[id]) dependants[source].Add(id);
            if (remaining[id] == 0) { roots[id].Add(id); queue.Enqueue(id); }
        }
        while (queue.TryDequeue(out var id))
        {
            foreach (var next in dependants[id])
            {
                roots[next].UnionWith(roots[id]);
                if (--remaining[next] == 0) queue.Enqueue(next);
            }
        }
        var origins = new int[count];
        for (var id = 0; id < count; id++)
        {
            origins[id] = roots[id].Count == 1 && remaining[id] == 0 ? roots[id].Single() : -1;
            if (origins[id] < 0)
                issues.Add(new(remaining[id] == 0 ? PsdFlipBindingIssue.AmbiguousSelectionOrigin : PsdFlipBindingIssue.SelectionOriginCycle,
                    id, roots[id].Order().ToImmutableArray()));
        }
        OriginNodeIds = origins.ToImmutableArray();
        AliasNodeIds = Enumerable.Range(0, count).Where(id => origins[id] >= 0)
            .GroupBy(id => origins[id]).ToImmutableDictionary(g => g.Key, g => g.ToImmutableArray());

        var variants = bindings.Pairs.ToDictionary(p => p.FlippedId, p => Scope(p.Targets));
        var normals = bindings.Pairs.GroupBy(p => p.NormalId).ToDictionary(g => g.Key,
            g => g.Aggregate((PsdDirectionScope)0, (scope, pair) => scope | Scope(pair.Targets)));
        var missing = bindings.Diagnostics.Where(d => d.Issue == PsdFlipBindingIssue.MissingBase).Select(d => d.NodeId).ToHashSet();
        var scopes = new PsdDirectionScope[count]; var directionalOwner = new bool[count];
        var pending = new Stack<int>(notation.Children(null).Reverse());
        while (pending.TryPop(out var id))
        {
            var node = notation.Nodes[id];
            var scope = node.ParentId is { } parent ? scopes[parent] : PsdDirectionScope.All;
            var owned = node.ParentId is { } ownerParent && directionalOwner[ownerParent];
            if (normals.TryGetValue(id, out var normalTargets)) scope &= ~normalTargets;
            if (variants.TryGetValue(id, out var variantTargets)) { scope &= variantTargets; owned = true; }
            else if (missing.Contains(id)) { scope &= Scope(node.FlipTargets); owned = true; }
            scopes[id] = scope; directionalOwner[id] = owned;
            foreach (var child in notation.Children(id).Reverse()) pending.Push(child);
        }
        PhysicalScopes = scopes.ToImmutableArray();
        SelectionScopes = AliasNodeIds.ToImmutableDictionary(p => p.Key,
            p => p.Value.Aggregate((PsdDirectionScope)0, (value, id) => value | scopes[id]));
        var exclusive = ImmutableDictionary.CreateBuilder<int, PsdDirectionScope>();
        foreach (var (origin, aliases) in AliasNodeIds)
        {
            if (!directionalOwner[origin]) continue;
            var scope = SelectionScopes[origin];
            if (scope == 0)
                issues.Add(new(PsdFlipBindingIssue.IncompatibleDirectionScope, origin, aliases));
            else if ((scope & PsdDirectionScope.Unflipped) == 0) exclusive.Add(origin, scope);
        }
        DirectionOnlyScopes = exclusive.ToImmutable();

        // Independent directional radio owners cannot be merged by transfer order.
        foreach (var group in notation.Nodes.Where(n => n.SelectionMarker == PsdSelectionMarker.Radio).GroupBy(n => n.ParentId))
        {
            var members = group.Where(n => origins[n.NodeId] >= 0 && exclusive.ContainsKey(origins[n.NodeId])).ToArray();
            foreach (var scope in new[] { PsdDirectionScope.X, PsdDirectionScope.Y, PsdDirectionScope.XY })
            {
                var active = members.Where(n => (scopes[n.NodeId] & scope) != 0).ToArray();
                if (active.Select(n => notation.Nodes[origins[n.NodeId]].ParentId).Distinct().Take(2).Count() <= 1) continue;
                issues.Add(new(PsdFlipBindingIssue.AmbiguousDirectionalRadioGroup, active[0].NodeId,
                    active.SelectMany(n => new[] { n.NodeId, origins[n.NodeId] }).Distinct().ToImmutableArray()));
            }
        }
        Diagnostics = issues.ToImmutable();
    }

    internal static PsdDirectionScope Scope(PsdFlipTargets targets) => (PsdDirectionScope)((int)targets << 1);
    internal static PsdDirectionScope Scope(PsdFlipState state) => (PsdDirectionScope)(1 << (int)state);
}
