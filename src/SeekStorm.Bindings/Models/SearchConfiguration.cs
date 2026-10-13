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

    /// <summary>Exact document filtering, unlike FieldFilter which selects searchable fields.</summary>
    public static JsonObject StringFacet(string field, params string[] values) => new()
    {
        ["String16"] = new JsonObject { ["field"] = field, ["filter"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()) }
    };
}
