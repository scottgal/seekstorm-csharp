using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SeekStorm.Bindings.Models;

/// <summary>
/// Source-generated JSON serialization context for AOT compatibility.
/// Every type that crosses the FFI boundary as JSON must be listed here.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Serialization,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResult))]
[JsonSerializable(typeof(SearchHit))]
[JsonSerializable(typeof(Suggestion))]
[JsonSerializable(typeof(List<Suggestion>))]
[JsonSerializable(typeof(IndexMeta))]
[JsonSerializable(typeof(SchemaField))]
[JsonSerializable(typeof(SchemaField[]))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(DeleteByQueryRequest))]
[JsonSerializable(typeof(UpdateDocument))]
[JsonSerializable(typeof(List<UpdateDocument>))]
[JsonSerializable(typeof(ResultSort))]
[JsonSerializable(typeof(ResultSort[]))]
[JsonSerializable(typeof(DistanceField))]
[JsonSerializable(typeof(DistanceField[]))]
[JsonSerializable(typeof(Highlight))]
[JsonSerializable(typeof(Highlight[]))]
[JsonSerializable(typeof(InferenceConfig))]
[JsonSerializable(typeof(SpellingCorrectionConfig))]
[JsonSerializable(typeof(QueryRewritingConfig))]
[JsonSerializable(typeof(IteratorRequest))]
[JsonSerializable(typeof(IteratorResult))]
[JsonSerializable(typeof(List<ulong>))]
[JsonSerializable(typeof(ulong[]))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonNode))]
public sealed partial class SeekStormJsonContext : JsonSerializerContext
{
}
