using System.Text.Json.Serialization;

namespace SeekStorm.Bindings.Models;

/// <summary>
/// Index metadata used when creating a new SeekStorm index.
/// Serialized to JSON at the FFI boundary.
/// </summary>
public sealed class IndexMeta
{
    /// <summary>Internal ID (0 for auto-assign).</summary>
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    /// <summary>Human-readable index name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Lexical similarity function: "Bm25f" (default), "Bm25fProximity".
    /// </summary>
    [JsonPropertyName("lexical_similarity")]
    public string LexicalSimilarity { get; set; } = "Bm25f";

    /// <summary>
    /// Tokenizer: "AsciiAlphabetic", "UnicodeAlphanumeric", "UnicodeAlphanumericChinese", etc.
    /// </summary>
    [JsonPropertyName("tokenizer")]
    public string Tokenizer { get; set; } = "UnicodeAlphanumeric";

    /// <summary>
    /// Stemmer algorithm or "None".
    /// </summary>
    [JsonPropertyName("stemmer")]
    public string Stemmer { get; set; } = "None";

    /// <summary>
    /// Stop word list: "None", "English", etc.
    /// </summary>
    [JsonPropertyName("stop_words")]
    public string StopWords { get; set; } = "None";

    /// <summary>
    /// Frequent word list: "None", "English", etc. Used for n-gram phrase search optimization.
    /// </summary>
    [JsonPropertyName("frequent_words")]
    public string FrequentWords { get; set; } = "English";

    /// <summary>
    /// N-gram indexing for phrase search: 0 = disabled, 2 = bigrams + frequent words.
    /// </summary>
    [JsonPropertyName("ngram_indexing")]
    public byte NgramIndexing { get; set; }

    /// <summary>
    /// Document compression: "None", "Snappy", "Lz4", "Zstd".
    /// </summary>
    [JsonPropertyName("document_compression")]
    public string DocumentCompression { get; set; } = "Snappy";

    /// <summary>
    /// Access type: "Mmap" (default), "Ram".
    /// </summary>
    [JsonPropertyName("access_type")]
    public string AccessType { get; set; } = "Mmap";

    /// <summary>
    /// Spelling correction configuration. Null = disabled.
    /// </summary>
    [JsonPropertyName("spelling_correction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? SpellingCorrection { get; set; }

    /// <summary>
    /// Query completion / instant search configuration. Null = disabled.
    /// </summary>
    [JsonPropertyName("query_completion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? QueryCompletion { get; set; }

    /// <summary>
    /// Clustering mode: "None" (default).
    /// </summary>
    [JsonPropertyName("clustering")]
    public string Clustering { get; set; } = "None";

    /// <summary>
    /// Inference configuration for auto-generating embeddings. Null = disabled.
    /// </summary>
    [JsonPropertyName("inference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Inference { get; set; }
}

/// <summary>
/// Schema field definition for index creation.
/// </summary>
public sealed class SchemaField
{
    /// <summary>Field name.</summary>
    [JsonPropertyName("field")]
    public string Field { get; set; } = string.Empty;

    /// <summary>
    /// Field type: "Text", "String16", "String32", "U8".."U64", "I8".."I64",
    /// "F32", "F64", "Bool", "Timestamp", "Point", "Json", "Binary", "Vector".
    /// </summary>
    [JsonPropertyName("field_type")]
    public string FieldType { get; set; } = "Text";

    /// <summary>Store the field value in the document store (retrievable).</summary>
    [JsonPropertyName("store")]
    public bool Store { get; set; }

    /// <summary>Index the field for lexical search.</summary>
    [JsonPropertyName("index_lexical")]
    public bool IndexLexical { get; set; }

    /// <summary>Enable faceting on this field.</summary>
    [JsonPropertyName("facet")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Facet { get; set; }

    /// <summary>Index-level boost factor for this field (lexical).</summary>
    [JsonPropertyName("boost")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Boost { get; set; }

    /// <summary>Designate this field as the longest text field per document.</summary>
    [JsonPropertyName("longest")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Longest { get; set; }
}

/// <summary>
/// Simple status response from create/open/commit/delete operations.
/// </summary>
public sealed class StatusResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ulong? Id { get; set; }
}

/// <summary>
/// Body for delete-documents-by-query requests.
/// </summary>
public sealed class DeleteByQueryRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; } = string.Empty;

    [JsonPropertyName("query_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryType { get; set; }

    [JsonPropertyName("offset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Offset { get; set; }

    [JsonPropertyName("length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Length { get; set; } = 100;

    [JsonPropertyName("include_uncommitted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IncludeUncommitted { get; set; }
}

/// <summary>
/// Update document: a (document_id, new_fields) pair.
/// The document_id identifies the document; new_fields supplies replacement field values.
/// Serialized as [id, {fields}] per SeekStorm's format.
/// </summary>
public sealed class UpdateDocument
{
    [JsonPropertyName("id")]
    public ulong Id { get; set; }

    [JsonPropertyName("document")]
    public Dictionary<string, object?> Document { get; set; } = new();
}

// ── Vector / inference types ────────────────────────────────────────

/// <summary>
/// Inference configuration for embedding generation at index time.
/// Pass as object? in IndexMeta; use this class to construct.
/// </summary>
public sealed class InferenceConfig
{
    /// <summary>Model name for embedding generation.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "PotionBase2M";

    /// <summary>Vector precision: "F32" or "I8".</summary>
    [JsonPropertyName("precision")]
    public string Precision { get; set; } = "F32";

    /// <summary>Chunk size in bytes for text splitting.</summary>
    [JsonPropertyName("chunk_size")]
    public int ChunkSize { get; set; } = 1000;

    /// <summary>Vector similarity measure: "CosineSimilarity", "DotProduct", "EuclideanDistance".</summary>
    [JsonPropertyName("vector_similarity")]
    public string VectorSimilarity { get; set; } = "CosineSimilarity";

    /// <summary>Quantization: "None", "Scalar", "TurboQuant".</summary>
    [JsonPropertyName("quantization")]
    public string Quantization { get; set; } = "None";
}

/// <summary>
/// Spelling correction configuration for index creation.
/// </summary>
public sealed class SpellingCorrectionConfig
{
    /// <summary>Max edit distance for corrections.</summary>
    [JsonPropertyName("max_edit_distance")]
    public int MaxEditDistance { get; set; } = 2;

    /// <summary>Minimum word length for correction candidates.</summary>
    [JsonPropertyName("min_word_length")]
    public int MinWordLength { get; set; } = 2;
}

/// <summary>
/// Query rewriting config for typo tolerance on a per-search basis.
/// </summary>
public sealed class QueryRewritingConfig
{
    /// <summary>
    /// Mode: "SearchOnly", "SearchSuggest", "SearchRewrite", "SuggestOnly".
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "SearchOnly";

    /// <summary>Enable spelling correction for queries >= this length.</summary>
    [JsonPropertyName("correct_threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CorrectThreshold { get; set; }

    /// <summary>Max edit distance for suggestions.</summary>
    [JsonPropertyName("distance")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Distance { get; set; } = 2;

    /// <summary>Enable query completion for queries >= this length.</summary>
    [JsonPropertyName("complete_threshold")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CompleteThreshold { get; set; }

    /// <summary>Max number of returned suggestions.</summary>
    [JsonPropertyName("suggestion_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SuggestionLength { get; set; }
}
