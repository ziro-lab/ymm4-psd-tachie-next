using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PsdTachieNext.Core;

public sealed record PsdAppearanceContribution(int Layer, PsdAppearanceSettings Settings);

/// <summary>Immutable request input. Layer order is priority; enumeration order never breaks ties.
/// The effective envelope is temporary and must never be saved into any contributor.</summary>
public sealed class PsdAppearanceStack : IEquatable<PsdAppearanceStack>
{
    public PsdAppearanceSettings? Base { get; }
    public ImmutableArray<PsdAppearanceContribution> Faces { get; }
    public bool HasSettings => Base is not null || Faces.Length != 0;
    public PsdAppearanceStack(PsdAppearanceSettings? @base, IEnumerable<PsdAppearanceContribution>? faces = null)
    {
        Base = @base;
        Faces = (faces ?? []).OrderBy(f => f.Layer).ThenBy(f => f.Settings.Json, StringComparer.Ordinal).ToImmutableArray();
    }
    public PsdAppearanceResolution Resolve(string assetIdentity, PsdLayerReferenceIndex index)
        => PsdAppearanceSettings.ResolveStack(this, assetIdentity, index);
    public bool Equals(PsdAppearanceStack? other) => other is not null && Base == other.Base && Faces.SequenceEqual(other.Faces);
    public override bool Equals(object? obj) => obj is PsdAppearanceStack other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Base);
        foreach (var face in Faces) hash.Add(face);
        return hash.ToHashCode();
    }
}

public sealed partial record PsdAppearanceSettings
{
    /// <summary>Metadata only, on an already validated immutable envelope.</summary>
    public ImmutableHashSet<int> OwnedOrigins(string assetIdentity, PsdLayerReferenceIndex index)
    {
        var result = Resolve(assetIdentity, index);
        if (!result.Succeeded) throw new InvalidDataException(result.Reason);
        return ReadOperations(Root(assetIdentity), index, ImmutableArray.CreateBuilder<PsdAppearanceRepair>())
            .Select(op => op.Origin).ToImmutableHashSet();
    }
    public PsdFlipState? AuthoredFlip(string assetIdentity)
        => Root(assetIdentity)["flip"] is { } value ? (PsdFlipState)value.GetValue<int>() : null;

    internal static PsdAppearanceResolution ResolveStack(PsdAppearanceStack stack, string assetIdentity, PsdLayerReferenceIndex index)
    {
        var baseline = ResolveOrDefault(stack.Base, assetIdentity, index);
        if (!baseline.Succeeded || stack.Faces.IsEmpty) return baseline;
        var merged = Create(assetIdentity).Root(assetIdentity);
        var selected = new Dictionary<int, Operation>();
        PsdFlipState? flip = null;
        try
        {
            if (stack.Base is { } baseSettings) Apply([baseSettings], sameLayer: false);
            foreach (var layer in stack.Faces.GroupBy(f => f.Layer)) Apply(layer.Select(f => f.Settings), sameLayer: true);
            merged["flip"] = flip is null ? null : JsonValue.Create((int)flip.Value);
            foreach (var operation in selected.Values.OrderBy(op => op.Origin))
                merged["overrides"]!.AsArray().Add(operation.Raw.DeepClone());
            return FromJson(merged.ToJsonString()).Resolve(assetIdentity, index);
        }
        catch (StackRepairException ex) { return ex.Resolution; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        { return new(null, null, [], ex.Message); }

        void Apply(IEnumerable<PsdAppearanceSettings> settings, bool sameLayer)
        {
            var peersAtLayer = new List<Operation>();
            PsdFlipState? flipAtLayer = null;
            foreach (var patch in settings)
            {
                var validated = patch.Resolve(assetIdentity, index);
                if (!validated.Succeeded) throw new StackRepairException(validated);
                var root = patch.Root(assetIdentity);
                var patchFlip = patch.AuthoredFlip(assetIdentity);
                if (sameLayer && flipAtLayer.HasValue && patchFlip.HasValue && flipAtLayer != patchFlip)
                    throw new InvalidDataException("Conflicting orientation at the same Timeline layer; no tie order is inferred.");
                flipAtLayer ??= patchFlip;
                foreach (var operation in ReadOperations(root, index, ImmutableArray.CreateBuilder<PsdAppearanceRepair>()))
                {
                    if (sameLayer && peersAtLayer.Any(peer => Conflict(peer, operation)))
                        throw new InvalidDataException("Conflicting authored parts at the same Timeline layer; no tie order is inferred.");
                    peersAtLayer.Add(operation);
                }
            }
            if (flipAtLayer.HasValue) flip = flipAtLayer;
            foreach (var operation in peersAtLayer)
            {
                if (operation.Radio is { } mask)
                {
                    var radioPeers = Peers(index.Notation, operation.Origin, mask);
                    foreach (var lower in selected.Values.Where(op => op.Radio.HasValue && op.Origin != operation.Origin).ToArray())
                    {
                        var remaining = lower.Radio!.Value & ~radioPeers.GetValueOrDefault(lower.Origin);
                        if (remaining == 0) selected.Remove(lower.Origin);
                        else
                        {
                            var row = (JsonObject)lower.Raw.DeepClone(); row["radio"] = (int)remaining;
                            selected[lower.Origin] = lower with { Raw = row, Radio = remaining };
                        }
                    }
                    var combined = mask | (selected.GetValueOrDefault(operation.Origin)?.Radio ?? 0);
                    var raw = (JsonObject)operation.Raw.DeepClone(); raw["radio"] = (int)combined;
                    selected[operation.Origin] = operation with { Raw = raw, Radio = combined };
                }
                else selected[operation.Origin] = operation;
            }
        }
        bool Conflict(Operation left, Operation right)
        {
            if (left.Origin == right.Origin)
                return left.Visible != right.Visible || left.Radio.HasValue != right.Radio.HasValue || left.Canonical != right.Canonical;
            return left.Radio is { } lm && right.Radio is { } rm &&
                (Peers(index.Notation, left.Origin, lm).GetValueOrDefault(right.Origin) & rm) != 0;
        }
    }
    private sealed class StackRepairException(PsdAppearanceResolution resolution) : Exception
    { internal PsdAppearanceResolution Resolution { get; } = resolution; }
}
