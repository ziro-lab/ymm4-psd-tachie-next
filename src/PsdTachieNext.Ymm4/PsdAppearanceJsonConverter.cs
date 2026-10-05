using Newtonsoft.Json;
using PsdTachieNext.Core;

namespace PsdTachieNext.Ymm4;

/// <summary>Preserve the complete envelope, including unknown versions/fields; expose no mutable JSON between owners.</summary>
public sealed class PsdAppearanceJsonConverter : JsonConverter<PsdAppearanceSettings>
{
    public override PsdAppearanceSettings? ReadJson(JsonReader reader, Type objectType, PsdAppearanceSettings? existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return null;
        // Keep opaque dates and arbitrary JSON numbers as text: JToken would parse/rewrite them
        // using the host's DateParseHandling/FloatParseHandling and lose unknown-data precision.
        if (reader.TokenType != JsonToken.String || reader.Value is not string json)
            throw new JsonSerializationException("Appearance must contain the immutable JSON envelope text.");
        return PsdAppearanceSettings.FromJson(json);
    }
    public override void WriteJson(JsonWriter writer, PsdAppearanceSettings? value, JsonSerializer serializer)
    {
        if (value is null) writer.WriteNull(); else writer.WriteValue(value.Json);
    }
}
