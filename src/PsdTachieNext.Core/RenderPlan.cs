using System.Collections.Immutable;

namespace PsdTachieNext.Core;

/// <summary>Single immutable visibility/clip evaluation consumed by block admission and the tree renderer.</summary>
public sealed class RenderPlan
{
    public CompiledManifest Manifest { get; }
    public ImmutableArray<ImmutableArray<int>> Children { get; }
    public ImmutableArray<int> ActiveNodeIds { get; }
    public ImmutableArray<int> RequiredBlockIds { get; }
    public long UploadBytes { get; }
    public PsdPrefixVisibility? PrefixVisibility { get; }
    public PsdVisibilityState? Visibility { get; }
    public PsdFlipState FlipState => Visibility?.FlipState ?? PsdFlipState.None;
    private readonly bool[] active;
    public bool IsActive(int id) => active[id];
    private RenderPlan(CompiledManifest manifest, IEnumerable<int>? enabled, PsdPrefixVisibility? prefixVisibility = null,
        PsdVisibilityState? visibility = null)
    {
        Manifest = manifest;
        PrefixVisibility = prefixVisibility;
        Visibility = visibility;
        var lists = Enumerable.Range(0, manifest.Nodes.Length + 1).Select(_ => new List<int>()).ToArray();
        foreach (var n in manifest.Nodes) lists[(n.ParentId ?? -1) + 1].Add(n.Id);
        Children = lists.Select(l => l.OrderBy(id => manifest.Nodes[id].Order).ToImmutableArray()).ToImmutableArray();
        var ids = (enabled ?? manifest.Nodes.Where(n => n.DefaultVisible).Select(n => n.Id)).ToArray();
        if (ids.Distinct().Count() != ids.Length || ids.Any(id => (uint)id >= (uint)manifest.Nodes.Length))
            throw new ArgumentException("Selection must contain unique node IDs in this generation.", nameof(enabled));
        active = new bool[manifest.Nodes.Length];
        foreach (var id in ids) active[id] = true;
        foreach (var n in manifest.Nodes) active[n.Id] &= n.Opacity != 0 && (n.ParentId is null || active[n.ParentId.Value]);
        foreach (var siblings in Children)
        {
            var baseOn = false;
            for (var i = siblings.Length - 1; i >= 0; i--)
            {
                var n = manifest.Nodes[siblings[i]];
                active[n.Id] &= n.ParentId is null || active[n.ParentId.Value];
                if (n.Clipping) active[n.Id] &= baseOn; else baseOn = active[n.Id];
            }
        }
        ActiveNodeIds = Enumerable.Range(0, active.Length).Where(i => active[i]).ToImmutableArray();
        var blocks = new HashSet<int>();
        long uploadBytes = 0;
        bool Node(LayerNode n)
        {
            var exists = n.Kind == NodeKind.Group ? Siblings(n.Id + 1, false) : n.PixelBlockId is not null;
            if (!exists) return false;
            if (n.Kind == NodeKind.Layer) { blocks.Add(n.PixelBlockId!.Value); uploadBytes = checked(uploadBytes + manifest.Blocks[n.PixelBlockId.Value].RawLength); }
            if (n.Mask is { } m && (m.Flags & 2) == 0)
                foreach (var p in m.Planes) { blocks.Add(p.BlockId); uploadBytes = checked(uploadBytes + manifest.Blocks[p.BlockId].RawLength); }
            return true;
        }
        bool Siblings(int slot, bool backdrop)
        {
            var siblings = Children[slot];
            for (var i = siblings.Length - 1; i >= 0;)
            {
                var n = manifest.Nodes[siblings[i]];
                var first = i - 1;
                while (first >= 0 && manifest.Nodes[siblings[first]].Clipping) first--;
                if (active[n.Id])
                {
                    if (n.Kind == NodeKind.Group && n.BlendMode == "pass") backdrop = Siblings(n.Id + 1, backdrop);
                    else if (Node(n))
                    {
                        for (var c = i - 1; c > first; c--) if (active[siblings[c]]) Node(manifest.Nodes[siblings[c]]);
                        backdrop = true;
                    }
                }
                i = first;
            }
            return backdrop;
        }
        Siblings(0, false);
        RequiredBlockIds = blocks.Order().ToImmutableArray();
        UploadBytes = uploadBytes;
    }
    public static RenderPlan Create(CompiledManifest manifest, IEnumerable<int>? enabled = null) => new(manifest, enabled);
    public static RenderPlan CreatePrefixVisibility(CompiledManifest manifest, PsdPrefixVisibility visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility);
        visibility.ValidateGeneration(manifest);
        return new(manifest, visibility.EnabledNodeIds, visibility);
    }
    public static RenderPlan CreateNotationVisibility(CompiledManifest manifest, PsdVisibilityState visibility)
    {
        ArgumentNullException.ThrowIfNull(visibility); visibility.ValidateGeneration(manifest);
        return new(manifest, visibility.EnabledNodeIds, visibility: visibility);
    }
}
