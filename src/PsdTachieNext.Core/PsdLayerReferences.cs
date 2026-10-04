using System.Buffers.Binary;
using System.Collections.Immutable;

namespace PsdTachieNext.Core;

public sealed record PsdLayerPathPart(string Name, NodeKind Kind, int Occurrence, int Count);
public sealed record PersistentPsdLayerRef(string SourceSha256, uint? LayerId,
    ImmutableArray<PsdLayerPathPart> Path);
public sealed record PsdLayerReferenceResolution(int? NodeId, ImmutableArray<int> Candidates, string? Reason);

/// <summary>Worker-side identity metadata. Reads only verified lyid metadata, never pixel blocks or original PSDs.</summary>
public sealed class PsdLayerReferenceIndex
{
    public PsdNotationIndex Notation { get; }
    private readonly ImmutableDictionary<int, uint> nodeIds;
    private readonly ImmutableDictionary<uint, ImmutableArray<int>> ids;
    private readonly ImmutableDictionary<(int Parent, string Name, NodeKind Kind), ImmutableArray<int>> names;

    private PsdLayerReferenceIndex(PsdNotationIndex notation, Dictionary<int, uint> values)
    {
        Notation = notation;
        ids = values.GroupBy(p => p.Value).ToImmutableDictionary(g => g.Key, g => g.Select(p => p.Key).ToImmutableArray());
        nodeIds = values.Where(p => ids[p.Value].Length == 1).ToImmutableDictionary();
        names = notation.Nodes.GroupBy(n => (n.ParentId ?? -1, n.OriginalName, n.Kind))
            .ToImmutableDictionary(g => g.Key, g => g.Select(n => n.NodeId).ToImmutableArray());
    }

    public static PsdLayerReferenceIndex Read(SharedDocumentLease document, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var manifest = document.Manifest;
        var values = new Dictionary<int, uint>();
        foreach (var node in manifest.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var tags = node.ExtraTags.Where(t => t.Key == "lyid").ToArray();
            if (tags.Length != 1 || manifest.Blocks[tags[0].BlockId].RawLength != 4) continue;
            using var block = document.AcquireBlock(tags[0].BlockId);
            values[node.Id] = BinaryPrimitives.ReadUInt32BigEndian(block.Memory.Span);
        }
        return new(document.Notation, values);
    }

    public PersistentPsdLayerRef Capture(int nodeId)
    {
        if ((uint)nodeId >= (uint)Notation.Nodes.Length) throw new ArgumentOutOfRangeException(nameof(nodeId));
        var path = new List<PsdLayerPathPart>();
        for (int? current = nodeId; current is int id; current = Notation.Nodes[id].ParentId)
        {
            var node = Notation.Nodes[id];
            var siblings = names[(node.ParentId ?? -1, node.OriginalName, node.Kind)];
            path.Add(new(node.OriginalName, node.Kind, siblings.IndexOf(id), siblings.Length));
        }
        path.Reverse();
        return new(Notation.SourceSha256, nodeIds.TryGetValue(nodeId, out var layerId) ? layerId : null, path.ToImmutableArray());
    }

    public PsdLayerReferenceResolution Resolve(PersistentPsdLayerRef? reference)
    {
        if (reference is null || !CompiledFormat.IsHash(reference.SourceSha256) || reference.Path.IsDefaultOrEmpty ||
            reference.Path.Any(p => p is null || p.Name is null || !Enum.IsDefined(p.Kind) || p.Count < 1 || p.Occurrence < 0 || p.Occurrence >= p.Count))
            return new(null, [], "Malformed logical layer reference.");
        if (reference.LayerId is uint stable)
        {
            var matching = ids.GetValueOrDefault(stable, []);
            if (matching.Length == 1 && Notation.Nodes[matching[0]].Kind == reference.Path[^1].Kind)
            {
                if (reference.SourceSha256 != Notation.SourceSha256 || ResolveSameBytes(reference) == matching[0])
                    return new(matching[0], [], null);
                return new(null, matching, "Layer ID and same-source hierarchy disagree.");
            }
            if (!matching.IsEmpty) return new(null, matching, "Layer ID is duplicated or its kind changed.");
            return new(null, Suggest(reference), "Saved layer ID is missing; confirm a repair candidate.");
        }
        if (reference.SourceSha256 == Notation.SourceSha256)
        {
            var same = ResolveSameBytes(reference);
            return same is int id ? new(id, [], null) : new(null, [], "Same-source hierarchy does not match.");
        }
        return new(null, Suggest(reference), "Names/hierarchy alone require confirmed repair after a source change.");
    }

    private int? ResolveSameBytes(PersistentPsdLayerRef reference)
    {
        int? parent = null;
        foreach (var part in reference.Path)
        {
            var matching = names.GetValueOrDefault((parent ?? -1, part.Name, part.Kind), []);
            if (matching.Length != part.Count) return null;
            parent = matching[part.Occurrence];
        }
        return parent;
    }

    private ImmutableArray<int> Suggest(PersistentPsdLayerRef reference)
    {
        ImmutableArray<int> parents = [-1];
        foreach (var part in reference.Path)
            parents = parents.SelectMany(parent => names.GetValueOrDefault((parent, part.Name, part.Kind), [])).Distinct().ToImmutableArray();
        return parents;
    }
}
