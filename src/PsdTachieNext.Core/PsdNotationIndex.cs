using System.Collections.Immutable;

namespace PsdTachieNext.Core;

public enum PsdSelectionMarker { Ordinary, Radio, ForceVisible }

/// <summary>Separate flip states: X | Y does not mean the XY state.</summary>
[Flags]
public enum PsdFlipTargets { None = 0, X = 1, Y = 2, XY = 4 }

[Flags]
public enum PsdNotationDiagnostics
{
    None = 0,
    MarkerWithoutName = 1,
    EmptyFlipBase = 2,
    ReferenceTokenOnlyName = 4
}

/// <summary>Lexical metadata only. NodeId and ParentId belong to this compiled generation.</summary>
public sealed record PsdNotationNode(
    int NodeId, int? ParentId, int Order, NodeKind Kind, string OriginalName,
    string DisplayName, bool DefaultVisible, PsdSelectionMarker SelectionMarker,
    string FlipBaseOriginalName, PsdFlipTargets FlipTargets, PsdNotationDiagnostics Diagnostics);

/// <summary>
/// Immutable index over validated manifest names. Does not change visibility, bind flip pairs,
/// resolve duplicate names, decode paths, or provide references across source edits.
/// Built once per shared document entry; no source or pixel I/O is needed.
/// </summary>
public sealed class PsdNotationIndex
{
    private readonly ImmutableDictionary<int, ImmutableArray<int>> children;
    private readonly ImmutableDictionary<(int Parent, string Name), ImmutableArray<int>> names;
    private readonly Lazy<PsdFlipBindingIndex> flipBindings;
    public string GenerationId { get; }
    public string SourceSha256 { get; }
    public ImmutableArray<PsdNotationNode> Nodes { get; }
    public PsdFlipBindingIndex FlipBindings => flipBindings.Value;

    // Only the shared pool supplies a validated manifest.
    internal PsdNotationIndex(CompiledManifest manifest)
    {
        GenerationId = manifest.GenerationId;
        SourceSha256 = manifest.Source.Sha256;
        var nodes = ImmutableArray.CreateBuilder<PsdNotationNode>(manifest.Nodes.Length);
        var byParent = new Dictionary<int, List<int>>();
        var byName = new Dictionary<(int Parent, string Name), List<int>>();
        foreach (var node in manifest.Nodes)
        {
            nodes.Add(Parse(node));
            var parent = node.ParentId ?? -1;
            if (!byParent.TryGetValue(parent, out var siblings)) byParent.Add(parent, siblings = []);
            siblings.Add(node.Id);
            var key = (parent, node.Name);
            if (!byName.TryGetValue(key, out var duplicates)) byName.Add(key, duplicates = []);
            duplicates.Add(node.Id);
        }
        Nodes = nodes.MoveToImmutable();
        children = byParent.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableArray());
        names = byName.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableArray());
        flipBindings = new(() => new PsdFlipBindingIndex(this));
    }

    /// <summary>Original sibling order; null denotes the root. Negative parent IDs are invalid.</summary>
    public ImmutableArray<int> Children(int? parentId) => children.GetValueOrDefault(ParentKey(parentId), []);

    /// <summary>All exact, ordinal matches. No display-label matching or unique-match guessing.</summary>
    public ImmutableArray<int> FindOriginalName(int? parentId, string originalName)
    {
        ArgumentNullException.ThrowIfNull(originalName);
        return names.GetValueOrDefault((ParentKey(parentId), originalName), []);
    }

    private static int ParentKey(int? parentId)
    {
        if (parentId < 0) throw new ArgumentOutOfRangeException(nameof(parentId));
        return parentId ?? -1;
    }

    private static PsdNotationNode Parse(LayerNode node)
    {
        var name = node.Name;
        var marker = PsdSelectionMarker.Ordinary;
        var prefixLength = 0;
        var diagnostics = PsdNotationDiagnostics.None;
        // PSDTool's reference excludes bare markers and the exact reserved name "!?".
        if (name.Length > 1 && name != "!?")
        {
            marker = name[0] switch
            {
                '*' => PsdSelectionMarker.Radio,
                '!' => PsdSelectionMarker.ForceVisible,
                _ => PsdSelectionMarker.Ordinary
            };
            prefixLength = marker == PsdSelectionMarker.Ordinary ? 0 : 1;
        }
        else if (name is "*" or "!") diagnostics |= PsdNotationDiagnostics.MarkerWithoutName;

        var end = name.Length;
        var flips = PsdFlipTargets.None;
        while (end > 0)
        {
            var colon = name.LastIndexOf(':', end - 1, end);
            if (colon < 0) break;
            var token = name.AsSpan(colon + 1, end - colon - 1);
            var target = token switch
            {
                "flipx" => PsdFlipTargets.X,
                "flipy" => PsdFlipTargets.Y,
                "flipxy" => PsdFlipTargets.XY,
                _ => PsdFlipTargets.None
            };
            if (target == PsdFlipTargets.None) break;
            flips |= target;
            end = colon;
        }
        if (flips != PsdFlipTargets.None && end <= prefixLength)
            diagnostics |= PsdNotationDiagnostics.EmptyFlipBase;
        // The reference parser can reject these token-only names, although the manual
        // describes colon suffixes. Preserve them and make this unresolved edge explicit.
        if (name[prefixLength..end] is "flipx" or "flipy" or "flipxy")
            diagnostics |= PsdNotationDiagnostics.ReferenceTokenOnlyName;
        var display = diagnostics == PsdNotationDiagnostics.None ? name[prefixLength..end] : name;
        return new(node.Id, node.ParentId, node.Order, node.Kind, name, display, node.DefaultVisible,
            marker, name[..end], flips, diagnostics);
    }
}
