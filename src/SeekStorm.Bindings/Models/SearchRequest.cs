using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SeekStorm.Bindings.Models;

// ── Search types ─────────────────────────────────────────────────────

/// <summary>
/// Full search request. Serialized to UTF-8 JSON at the FFI boundary.
/// Matches the SeekStorm SearchRequestObject schema.
/// </summary>
public sealed class SearchRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("search_mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SearchMode { get; set; }

    [JsonPropertyName("query_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryType { get; set; }

    [JsonPropertyName("result_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResultType { get; set; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Offset { get; set; }

    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Length { get; set; } = 10;

    /// <summary>Include uncommitted documents (real-time search).</summary>
    [JsonPropertyName("realtime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Realtime { get; set; }

    /// <summary>Enable empty query. Iterates all documents.</summary>
    [JsonPropertyName("enable_empty_query")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool EnableEmptyQuery { get; set; }

    /// <summary>Field names to restrict search to.</summary>
    [JsonPropertyName("field_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? FieldFilter { get; set; }

    /// <summary>Field names to include in results (null = all stored fields).</summary>
    [JsonPropertyName("fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Fields { get; set; }

    /// <summary>Query vector for vector/hybrid search.</summary>
    [JsonPropertyName("query_vector")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float[]? QueryVector { get; set; }

    /// <summary>Similarity threshold for vector/hybrid search.</summary>
    [JsonPropertyName("similarity_threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? SimilarityThreshold { get; set; }

    // ── v2: faceting ──────────────────────────────────────────────

    /// <summary>Facet definitions to compute and return with results.</summary>
    [JsonPropertyName("query_facets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonArray? QueryFacets { get; set; }

    /// <summary>Facet filters to narrow results by facet values.</summary>
    [JsonPropertyName("facet_filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonArray? FacetFilter { get; set; }

    /// <summary>Sort results by specified fields (tie-breaking).</summary>
    [JsonPropertyName("result_sort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResultSort[]? ResultSort { get; set; }

    // ── v2: geo ───────────────────────────────────────────────────

    /// <summary>Distance fields to compute at query time (geo proximity).</summary>
    [JsonPropertyName("distance_fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DistanceField[]? DistanceFields { get; set; }

    // ── v2: highlighting ──────────────────────────────────────────

    /// <summary>Highlight definitions for KWIC snippet generation.</summary>
    [JsonPropertyName("highlights")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Highlight[]? Highlights { get; set; }

    // ── v2: query rewriting / typo tolerance ──────────────────────

    /// <summary>Query rewriting mode for typo tolerance and instant search.</summary>
    [JsonPropertyName("query_rewriting")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonObject? QueryRewriting { get; set; }
}

/// <summary>
/// Sort specification: field name, sort order, optional base value.
/// Multiple sort fields are combined by tie-breaking.
/// </summary>
public sealed class ResultSort
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>"Ascending" or "Descending"</summary>
    [JsonPropertyName("order")]
    public string Order { get; set; } = "Descending";

    /// <summary>Base value for sorting: None, a point [lat,lon], or a numeric value.</summary>
    [JsonPropertyName("base")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Base { get; set; }
}

/// <summary>
/// Distance field computed from a reference point at query time.
/// </summary>
public sealed class DistanceField
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>Reference point [latitude, longitude].</summary>
    [JsonPropertyName("point")]
    public double[] Point { get; set; } = [];

    /// <summary>Distance unit: "Kilometers" or "Miles".</summary>
    [JsonPropertyName("distance_unit")]
    public string DistanceUnit { get; set; } = "Kilometers";
}

/// <summary>
/// Highlight / KWIC snippet specification.
/// </summary>
public sealed class Highlight
{
    /// <summary>Field to extract highlights from.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>Display name for the highlight in results.</summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>Number of fragments per document.</summary>
    [JsonPropertyName("fragment_number")]
    public int FragmentNumber { get; set; } = 2;

    /// <summary>Characters per fragment.</summary>
    [JsonPropertyName("fragment_size")]
    public int FragmentSize { get; set; } = 160;

    /// <summary>Wrap query terms in HTML markup tags.</summary>
    [JsonPropertyName("highlight_markup")]
    public bool HighlightMarkup { get; set; } = true;
}

// ── Search result types ──────────────────────────────────────────────

public sealed class SearchResult
{
    [JsonPropertyName("original_query")]
    public string OriginalQuery { get; set; } = string.Empty;

    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("query_terms")]
    public List<string> QueryTerms { get; set; } = new();

    [JsonPropertyName("result_count")]
    public int ResultCount { get; set; }

    [JsonPropertyName("result_count_total")]
    public int ResultCountTotal { get; set; }

    [JsonPropertyName("results")]
    public List<SearchHit> Results { get; set; } = new();

    /// <summary>Suggestions from query rewriting (typo correction, completion).</summary>
    [JsonPropertyName("suggestions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<Suggestion>? Suggestions { get; set; }

    /// <summary>Facet results when query_facets were requested.</summary>
    [JsonPropertyName("facets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Facets { get; set; }
}

public sealed class SearchHit
{
    [JsonPropertyName("doc_id")]
    public ulong DocId { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Keyword-in-context highlights for this hit.</summary>
    [JsonPropertyName("highlights")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<string>>? Highlights { get; set; }
}

public sealed class Suggestion
{
    [JsonPropertyName("term")]
    public string Term { get; set; } = string.Empty;

    [JsonPropertyName("distance")]
    public int Distance { get; set; }

    [JsonPropertyName("count")]
    public long Count { get; set; }
}

// ── Iterator types ───────────────────────────────────────────────────

/// <summary>
/// Request for document iteration through the index.
/// </summary>
public sealed class IteratorRequest
{
    /// <summary>Start from this document ID (null = start/end of index).</summary>
    [JsonPropertyName("docid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? DocId { get; set; }

    /// <summary>Number of document IDs to skip before returning results.</summary>
    [JsonPropertyName("skip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ulong Skip { get; set; }

    /// <summary>Number to return. Positive = forward, negative = backward.</summary>
    [JsonPropertyName("take")]
    public long Take { get; set; } = 100;

    /// <summary>Include deleted documents in results.</summary>
    [JsonPropertyName("include_deleted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeDeleted { get; set; }

    /// <summary>Include full document bodies (field values).</summary>
    [JsonPropertyName("include_document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeDocument { get; set; } = true;

    /// <summary>Field names to include (empty = all stored fields).</summary>
    [JsonPropertyName("fields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Fields { get; set; }
}

/// <summary>
/// Result from document iteration.
/// </summary>
public sealed class IteratorResult
{
    [JsonPropertyName("skip")]
    public ulong Skip { get; set; }

    [JsonPropertyName("results")]
    public List<IteratorResultItem> Results { get; set; } = new();
}

/// <summary>
/// Single item from document iteration.
/// </summary>
public sealed class IteratorResultItem
{
    [JsonPropertyName("doc_id")]
    public ulong DocId { get; set; }

    [JsonPropertyName("doc")]
    public Dictionary<string, object?>? Doc { get; set; }
}
