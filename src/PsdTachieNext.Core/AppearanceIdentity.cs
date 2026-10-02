using System.Collections.Immutable;
using System.Text;

namespace PsdTachieNext.Core;

/// <summary>Generation-local identities must never silently bind by a non-unique display name.</summary>
public sealed record LayerRef(string SourceSha256, int NodeId, string OriginalName);
public enum LayerResolution { Found, SourceChanged, Missing, NameMismatch }
public static class LayerReferences
{
    public static LayerResolution Resolve(CompiledManifest manifest, LayerRef reference, out LayerNode? node)
    {
        node = null;
        if (reference.SourceSha256 != manifest.Source.Sha256) return LayerResolution.SourceChanged;
        if ((uint)reference.NodeId >= (uint)manifest.Nodes.Length) return LayerResolution.Missing;
        var match = manifest.Nodes[reference.NodeId];
        if (match.Name != reference.OriginalName) return LayerResolution.NameMismatch;
        node = match; return LayerResolution.Found;
    }
}

public sealed record AppearanceKey(string Value)
{
    // Ordered IDs are intentional: sorting them could hide a draw-order change.
    public static AppearanceKey Create(CompiledManifest manifest, IEnumerable<int> orderedLayerIds,
        string renderProfile, int flipState = 0, long deviceEpoch = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(renderProfile);
        var ids = orderedLayerIds.ToImmutableArray();
        if (ids.Distinct().Count() != ids.Length || ids.Any(id => (uint)id >= (uint)manifest.Nodes.Length))
            throw new ArgumentException("Layer IDs must be unique and belong to this document.", nameof(orderedLayerIds));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(manifest.GenerationId); writer.Write(renderProfile);
            writer.Write(flipState); writer.Write(deviceEpoch); writer.Write(ids.Length);
            foreach (var id in ids) writer.Write(id);
        }
        return new(CompiledFormat.Hash(stream.ToArray()));
    }
}
