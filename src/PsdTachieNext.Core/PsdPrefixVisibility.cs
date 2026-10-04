using System.Collections.Immutable;

namespace PsdTachieNext.Core;

[Flags]
public enum PsdNotationUnsupportedReason { None = 0, FlipNotation = 1, TokenOnlyName = 2 }

/// <summary>Name-notation failure, distinct from the source pixel/drawing profile.</summary>
public sealed class UnsupportedPsdNotationException(PsdNotationUnsupportedReason reasons)
    : NotSupportedException(MessageFor(reasons))
{
    public PsdNotationUnsupportedReason Reasons { get; } = reasons;
    private static string MessageFor(PsdNotationUnsupportedReason reasons) => reasons switch
    {
        PsdNotationUnsupportedReason.FlipNotation => "PSDTool の反転記法（:flipx/:flipy/:flipxy）は、この版では未対応です。",
        PsdNotationUnsupportedReason.TokenOnlyName => "PSDTool のトークンだけの名前（flipx/flipy/flipxy）は、この版では解釈が未対応です。",
        PsdNotationUnsupportedReason.FlipNotation | PsdNotationUnsupportedReason.TokenOnlyName =>
            "PSDTool の反転記法とトークンだけの名前は、この版では未対応です。",
        _ => throw new ArgumentOutOfRangeException(nameof(reasons))
    };
}

/// <summary>
/// Immutable, generation-local checkbox state for the prefix-only PSDTool profile.
/// Ancestor visibility, opacity and clipping are evaluated later by RenderPlan; hiding
/// a parent never destroys a child's local choice. This is not a saved sparse patch.
/// </summary>
public sealed class PsdPrefixVisibility
{
    private readonly PsdNotationIndex notation;
    private readonly ImmutableDictionary<int, ImmutableArray<int>> radioGroups;
    private readonly ImmutableArray<bool> local;
    public string GenerationId => notation.GenerationId;
    public string SourceSha256 => notation.SourceSha256;
    public ImmutableArray<int> EnabledNodeIds { get; }

    private PsdPrefixVisibility(PsdNotationIndex notation,
        ImmutableDictionary<int, ImmutableArray<int>> radioGroups, ImmutableArray<bool> local)
    {
        this.notation = notation; this.radioGroups = radioGroups; this.local = local;
        EnabledNodeIds = Enumerable.Range(0, local.Length).Where(id => local[id]).ToImmutableArray();
    }

    public static PsdPrefixVisibility Create(PsdNotationIndex notation)
        => CreateCore(notation, allowFlips: false);

    internal static PsdPrefixVisibility CreateForFlipBindings(PsdNotationIndex notation)
        => CreateCore(notation, allowFlips: true);

    private static PsdPrefixVisibility CreateCore(PsdNotationIndex notation, bool allowFlips)
    {
        ArgumentNullException.ThrowIfNull(notation);
        // Flip binding/default normalization cannot be approximated with prefix rules.
        // Known literal bare markers are admitted; unresolved reference parser edges are not.
        // Original-source compilation precedes this check; only compiled-block decoding is avoided.
        var unsupported = PsdNotationUnsupportedReason.None;
        foreach (var node in notation.Nodes)
        {
            if ((!allowFlips && node.FlipTargets != PsdFlipTargets.None) ||
                (node.Diagnostics & PsdNotationDiagnostics.EmptyFlipBase) != 0)
                unsupported |= PsdNotationUnsupportedReason.FlipNotation;
            if ((node.Diagnostics & PsdNotationDiagnostics.ReferenceTokenOnlyName) != 0)
                unsupported |= PsdNotationUnsupportedReason.TokenOnlyName;
        }
        if (unsupported != PsdNotationUnsupportedReason.None)
            throw new UnsupportedPsdNotationException(unsupported);

        var groups = notation.Nodes.Where(n => n.SelectionMarker == PsdSelectionMarker.Radio)
            .GroupBy(n => n.ParentId ?? -1)
            .ToImmutableDictionary(g => g.Key, g => g.OrderBy(n => n.Order).Select(n => n.NodeId).ToImmutableArray());
        var values = notation.Nodes.Select(n => n.DefaultVisible ||
            n.SelectionMarker == PsdSelectionMarker.ForceVisible).ToArray();
        foreach (var siblings in groups.Values)
        {
            var selected = siblings.FirstOrDefault(id => values[id], siblings[0]);
            foreach (var id in siblings) values[id] = id == selected;
        }
        return new(notation, groups, values.ToImmutableArray());
    }

    public bool IsLocallyVisible(int nodeId)
    {
        ValidateNodeId(nodeId);
        return local[nodeId];
    }

    /// <summary>One radio switch yields one new state. Radio/force-visible deselection is a no-op.</summary>
    public PsdPrefixVisibility SetVisible(int nodeId, bool visible)
    {
        ValidateNodeId(nodeId);
        var node = notation.Nodes[nodeId];
        if (local[nodeId] == visible ||
            (!visible && node.SelectionMarker != PsdSelectionMarker.Ordinary)) return this;
        var next = local.ToBuilder();
        if (node.SelectionMarker == PsdSelectionMarker.Radio)
            foreach (var sibling in radioGroups[node.ParentId ?? -1]) next[sibling] = sibling == nodeId;
        else next[nodeId] = visible;
        return new(notation, radioGroups, next.ToImmutable());
    }

    internal void ValidateGeneration(CompiledManifest manifest)
    {
        if (manifest.GenerationId != GenerationId || manifest.Source.Sha256 != SourceSha256 ||
            manifest.Nodes.Length != local.Length)
            throw new ArgumentException("Visibility belongs to a different compiled generation.", nameof(manifest));
    }

    private void ValidateNodeId(int nodeId)
    {
        if ((uint)nodeId >= (uint)local.Length) throw new ArgumentOutOfRangeException(nameof(nodeId));
    }
}
