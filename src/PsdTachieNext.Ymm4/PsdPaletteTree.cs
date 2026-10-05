using PsdTachieNext.Core;

namespace PsdTachieNext.Ymm4;

/// <summary>Logical origin hierarchy only. Folding never edits appearance or native history.</summary>
internal static class PsdPaletteTree
{
    internal sealed record Entry(PsdNotationNode Node, int Depth, int? ParentOrigin);
    internal static Entry[] Project(PsdNotationIndex notation, IReadOnlySet<int> collapsed)
    {
        var selection = notation.FlipBindings.Selection;
        var logical = notation.Nodes.Where(n => selection.OriginNodeIds[n.NodeId] == n.NodeId).ToArray();
        var children = logical.GroupBy(n => n.ParentId is int parent ? selection.OriginNodeIds[parent] : -1)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n.Order).ToArray());
        var result = new List<Entry>();
        var pending = new Stack<(PsdNotationNode Node, int Depth, int? Parent)>();
        Push(-1, 0, null);
        while (pending.TryPop(out var entry))
        {
            result.Add(new(entry.Node, entry.Depth, entry.Parent));
            if (!collapsed.Contains(entry.Node.NodeId)) Push(entry.Node.NodeId, entry.Depth + 1, entry.Node.NodeId);
        }
        return result.ToArray();
        void Push(int parent, int depth, int? origin)
        {
            if (!children.TryGetValue(parent, out var nodes)) return;
            for (var i = nodes.Length - 1; i >= 0; i--) pending.Push((nodes[i], depth, origin));
        }
    }
}
