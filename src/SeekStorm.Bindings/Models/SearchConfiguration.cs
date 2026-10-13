using System.Text.Json.Nodes;

namespace SeekStorm.Bindings.Models;

public static class SearchConfiguration
{
    /// <summary>Supply your own vectors in a Json schema field with IndexVector=true.</summary>
    public static JsonObject ExternalVectors(int dimensions, string similarity = "Cosine")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        return new JsonObject
        {
            ["External"] = new JsonObject
            {
                ["dimensions"] = dimensions,
                ["precision"] = "F32",
                ["quantization"] = "None",
                ["similarity"] = similarity
            }
        };
    }

    /// <summary>Tagged built-in model configuration; model weights may be downloaded by the engine.</summary>
    public static JsonObject Model2Vec(string model = "PotionBase2M", int chunkSize = 1000, string quantization = "None")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunkSize);
        return new JsonObject { ["Model2Vec"] = new JsonObject { ["model"] = model, ["chunk_size"] = chunkSize, ["quantization"] = quantization } };
    }

    /// <summary>Exact document filtering, unlike FieldFilter which selects searchable fields.</summary>
    public static JsonObject StringFacet(string field, params string[] values) => new()
    {
        ["String16"] = new JsonObject { ["field"] = field, ["filter"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) }
    };
}
