using System.Collections.Immutable;

namespace PsdTachieNext.Core;

public enum PsdFlipState { None, X, Y, XY }
public enum PsdFlipBindingIssue
{
    MissingBase, AmbiguousBase, MultipleVariants, KindMismatch, MissingChild, DuplicateCountMismatch,
    AmbiguousSelectionOrigin, SelectionOriginCycle, IncompatibleDirectionScope, AmbiguousDirectionalRadioGroup
}
public sealed record PsdFlipBindingDiagnostic(PsdFlipBindingIssue Issue, int NodeId, ImmutableArray<int> RelatedNodeIds)
{
    public bool BlocksPreparation => Issue is not (PsdFlipBindingIssue.MissingBase or PsdFlipBindingIssue.MissingChild);
}
public sealed record PsdFlipNodeMap(int NormalId, int FlippedId);
public sealed record PsdFlipPair(int NormalId, int FlippedId, PsdFlipTargets Targets, ImmutableArray<PsdFlipNodeMap> Children);

/// <summary>Immutable original-name bindings. Diagnostics preserve every unbound/ambiguous identity.</summary>
public sealed class PsdFlipBindingIndex
{
    public ImmutableArray<PsdFlipPair> Pairs { get; }
    public ImmutableArray<PsdFlipBindingDiagnostic> Diagnostics { get; }
    public ImmutableHashSet<int> CounterpartNodeIds { get; }
    public PsdFlipSelectionIndex Selection { get; }

    internal PsdFlipBindingIndex(PsdNotationIndex notation)
    {
        var issues = new List<PsdFlipBindingDiagnostic>();
        var candidates = new List<PsdFlipPair>();
        // Reference registration is depth-first postorder, with original top-to-bottom siblings.
        var visits = new Stack<(int Id, bool Done)>();
        foreach (var id in notation.Children(null).Reverse()) visits.Push((id, false));
        while (visits.TryPop(out var visit))
        {
            if (!visit.Done)
            {
                visits.Push((visit.Id, true));
                foreach (var id in notation.Children(visit.Id).Reverse()) visits.Push((id, false));
                continue;
            }
            var variant = notation.Nodes[visit.Id];
            if (variant.FlipTargets == PsdFlipTargets.None) continue;
            var normals = notation.FindOriginalName(variant.ParentId, variant.FlipBaseOriginalName);
            if (normals.IsEmpty) { issues.Add(new(PsdFlipBindingIssue.MissingBase, variant.NodeId, [])); continue; }
            if (normals.Length != 1) { issues.Add(new(PsdFlipBindingIssue.AmbiguousBase, variant.NodeId, normals)); continue; }
            var normal = notation.Nodes[normals[0]];
            if (normal.Kind != variant.Kind)
            { issues.Add(new(PsdFlipBindingIssue.KindMismatch, variant.NodeId, [normal.NodeId])); continue; }
            candidates.Add(new(normal.NodeId, variant.NodeId, variant.FlipTargets, MapChildren(normal.NodeId, variant.NodeId)));
        }
        var conflicting = new HashSet<int>();
        foreach (var target in new[] { PsdFlipTargets.X, PsdFlipTargets.Y, PsdFlipTargets.XY })
        foreach (var group in candidates.Where(p => (p.Targets & target) != 0).GroupBy(p => p.NormalId))
        {
            var rows = group.ToArray(); if (rows.Length <= 1) continue;
            foreach (var row in rows)
            {
                conflicting.Add(row.FlippedId);
                issues.Add(new(PsdFlipBindingIssue.MultipleVariants, row.FlippedId,
                    rows.Where(p => p.FlippedId != row.FlippedId).Select(p => p.FlippedId).ToImmutableArray()));
            }
        }
        Pairs = candidates.Where(p => !conflicting.Contains(p.FlippedId)).ToImmutableArray();
        Diagnostics = issues.ToImmutableArray();
        CounterpartNodeIds = Pairs.SelectMany(p => p.Children.Select(m => m.FlippedId).Prepend(p.FlippedId)).ToImmutableHashSet();
        Selection = new(notation, this);
        Diagnostics = Diagnostics.AddRange(Selection.Diagnostics);

        ImmutableArray<PsdFlipNodeMap> MapChildren(int normalRoot, int variantRoot)
        {
            var result = ImmutableArray.CreateBuilder<PsdFlipNodeMap>();
            var pending = new Stack<(int Normal, int Flipped, bool Emit)>(); pending.Push((normalRoot, variantRoot, false));
            while (pending.TryPop(out var parents))
            {
                if (parents.Emit) result.Add(new(parents.Normal, parents.Flipped));
                var normalChildren = notation.Children(parents.Normal);
                var flippedChildren = notation.Children(parents.Flipped);
                // Reference internal names contain the raw name plus its bottom-based duplicate ordinal.
                // Only equal duplicate cardinalities/kinds are mapped; no nearest/order fallback is guessed.
                var left = normalChildren.GroupBy(id => notation.Nodes[id].OriginalName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                var right = flippedChildren.GroupBy(id => notation.Nodes[id].OriginalName, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                var mapped = new List<PsdFlipNodeMap>();
                foreach (var name in left.Keys.Union(right.Keys, StringComparer.Ordinal))
                {
                    var a = left.GetValueOrDefault(name, []); var b = right.GetValueOrDefault(name, []);
                    if (a.Length == 0 || b.Length == 0)
                    {
                        foreach (var id in a.Concat(b)) issues.Add(new(PsdFlipBindingIssue.MissingChild, id, [parents.Normal, parents.Flipped]));
                        continue;
                    }
                    if (a.Length != b.Length)
                    { issues.Add(new(PsdFlipBindingIssue.DuplicateCountMismatch, parents.Flipped, a.Concat(b).ToImmutableArray())); continue; }
                    for (var i = 0; i < a.Length; i++)
                    {
                        if (notation.Nodes[a[i]].Kind != notation.Nodes[b[i]].Kind)
                        { issues.Add(new(PsdFlipBindingIssue.KindMismatch, b[i], [a[i]])); continue; }
                        mapped.Add(new(a[i], b[i]));
                    }
                }
                var ordered = mapped.OrderBy(m => notation.Nodes[m.FlippedId].Order).ToArray();
                // Flatten in counterpart preorder, exactly the order of reference deserialization.
                // Push each matched node with its descendants before the following sibling.
                foreach (var row in ordered.Reverse())
                    pending.Push((row.NormalId, row.FlippedId, true));
            }
            return result.ToImmutable();
        }
    }
}

public sealed class PsdFlipBindingException(ImmutableArray<PsdFlipBindingDiagnostic> diagnostics)
    : NotSupportedException("PSDTool の反転レイヤー対応を一意に確認できません。対応候補と子階層を確認してください。")
{
    public ImmutableArray<PsdFlipBindingDiagnostic> Diagnostics { get; } = diagnostics;
}
