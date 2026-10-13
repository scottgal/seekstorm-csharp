using System.Text.Json;
using SeekStorm.Bindings;
using SeekStorm.Bindings.Models;
using Xunit;
using Xunit.Abstractions;

namespace SeekStorm.Bindings.Tests;

/// <summary>
/// Integration tests for the SeekStorm C# SDK.
/// Requires the native libseekstorm_ffi.{so,dylib,dll} in the runtimes directory.
/// Set SKIP_INTEGRATION_TESTS=1 to skip tests that need the native binary.
/// </summary>
public class SeekStormClientTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private SeekStormClient? _client;
    private readonly string _indexPath;
    private static readonly bool SkipNative = Environment.GetEnvironmentVariable("SKIP_INTEGRATION_TESTS") == "1";

    public SeekStormClientTests(ITestOutputHelper output)
    {
        _output = output;
        _indexPath = Path.Combine(Path.GetTempPath(), $"seekstorm_test_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        _client?.Dispose();
        try { if (Directory.Exists(_indexPath)) Directory.Delete(_indexPath, true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void Invalid_index_switch_retains_the_active_index()
    {
        if (SkipNative) return;
        SetupIndex(); IndexTestDocs(2);
        Assert.Throws<SeekStormException>(() => _client!.OpenIndex(_indexPath + "_missing"));
        Assert.Equal(2, _client!.Search("document").ResultCount);
        Assert.Throws<SeekStormException>(() => _client.CreateIndex(_indexPath + "_invalid", DefaultMeta(), []));
        Assert.Equal(2, _client.Search("document").ResultCount);
        _client.OpenIndex(_indexPath); // Idempotent; does not reopen an active writer.
        Assert.Throws<InvalidOperationException>(() => _client.CreateIndex(_indexPath, DefaultMeta(), DefaultSchema()));
        Assert.Equal(2, _client.Search("document").ResultCount);
    }

    [Fact]
    public void Unsafe_configuration_is_rejected_before_allocation()
    {
        if (SkipNative) return;
        _client = new SeekStormClient();
        var spelling = DefaultMeta(); spelling.SpellingCorrection = new SpellingCorrectionConfig();
        Assert.Throws<SeekStormException>(() => _client.CreateIndex(_indexPath, spelling, DefaultSchema()));
        Assert.False(Directory.Exists(_indexPath));
        var model = DefaultMeta(); model.Inference = SearchConfiguration.Model2Vec();
        var invalidVectorSchema = new[] { new SchemaField { Field = "embedding", FieldType = "Json", IndexVector = true } };
        var vectorError = Assert.Throws<SeekStormException>(() => _client.CreateIndex(_indexPath, model, invalidVectorSchema));
        Assert.Contains("Json", vectorError.Message);
        var shards = DefaultMeta(); shards.ShardCount = 2;
        Assert.Throws<ArgumentOutOfRangeException>(() => _client.CreateIndex(_indexPath, shards, DefaultSchema()));
        foreach (var bits in new[] { -1, 0, 1, 10, 12, 13, 17, int.MaxValue })
            Assert.Throws<ArgumentOutOfRangeException>(() => _client.CreateIndex(_indexPath, DefaultMeta(), DefaultSchema(), bits));
        Assert.False(Directory.Exists(_indexPath));
    }

    [Fact]
    public void Unknown_document_fields_and_null_typed_facets_are_not_silently_coerced()
    {
        if (SkipNative) return;
        SetupIndex();
        Assert.Throws<SeekStormException>(() => _client!.IndexDocument("{\"titel\":\"typo\",\"body\":\"document\"}"));
        Assert.Throws<SeekStormException>(() => _client!.IndexDocument("{\"title\":\"document\",\"price\":null}"));
        _client!.IndexDocument("{\"title\":\"document\"}");
        _client.Commit();
        Assert.Single(_client.Search("document").Results);
    }

    [Fact]
    public void Updates_of_deleted_or_absent_ids_do_not_insert_replacements_or_partially_update()
    {
        if (SkipNative) return;
        SetupIndex(); IndexTestDocs(2);
        var ids = _client!.IterateDocuments(new IteratorRequest { Take = 2 }).Results.Select(hit => hit.DocId).ToArray();
        _client.DeleteDocuments([ids[0]]);
        var replacement = new Dictionary<string, object?> { ["title"] = "replacement", ["body"] = "replacement" };
        Assert.Throws<SeekStormException>(() => _client.UpdateDocuments([
            new() { Id = ids[1], Document = replacement }, new() { Id = ids[0], Document = replacement }]));
        Assert.Throws<SeekStormException>(() => _client.UpdateDocuments([new() { Id = ulong.MaxValue, Document = replacement }]));
        _client.Commit();
        Assert.Empty(_client.Search("replacement").Results);
        Assert.Single(_client.Search("document").Results);
    }

    [Fact]
    public void Required_segment_configuration_can_commit_and_reopen()
    {
        if (SkipNative) return;
        _client = new SeekStormClient();
        _client.CreateIndex(_indexPath, DefaultMeta(), DefaultSchema(), 11);
        _client.IndexDocument("{\"title\":\"boundarydocument\"}");
        _client.Commit(); _client.Dispose();
        _client = new SeekStormClient();
        _client.OpenIndex(_indexPath);
        Assert.Single(_client.Search("boundarydocument").Results);
    }

    [Fact]
    public void Suggestions_deserialize_the_upstream_string_array_shape()
    {
        var result = JsonSerializer.Deserialize("{\"results\":[],\"suggestions\":[\"document\"]}", SeekStormJsonContext.Default.SearchResult);
        Assert.Equal("document", Assert.Single(result!.Suggestions!));
    }

    // ── Helpers ────────────────────────────────────────────────────

    private IndexMeta DefaultMeta() => new()
    {
        Name = "test_index",
        LexicalSimilarity = "Bm25f",
        Tokenizer = "UnicodeAlphanumeric",
        Stemmer = "None",
        StopWords = "None",
        FrequentWords = "English",
        NgramIndexing = 0,
        DocumentCompression = "Snappy",
        AccessType = "Mmap",
    };

    private SchemaField[] DefaultSchema() => new[]
    {
        new SchemaField { Field = "title", FieldType = "Text", Store = true, IndexLexical = true, Boost = 10 },
        new SchemaField { Field = "body", FieldType = "Text", Store = true, IndexLexical = true },
        new SchemaField { Field = "url", FieldType = "Text", Store = true, IndexLexical = false },
        new SchemaField { Field = "price", FieldType = "F32", Store = true, IndexLexical = false, Facet = true },
        new SchemaField { Field = "date", FieldType = "Timestamp", Store = true, IndexLexical = false, Facet = true },
    };

    private void SetupIndex()
    {
        if (SkipNative) return;
        _client = new SeekStormClient();
        _client.CreateIndex(_indexPath, DefaultMeta(), DefaultSchema());
    }

    private void IndexTestDocs(int count = 100)
    {
        if (SkipNative) return;
        for (int i = 0; i < count; i++)
        {
            _client!.IndexDocument(
                $$"""{"title":"Document {{i}}","body":"This is test document number {{i}} with searchable text content.","url":"https://example.com/doc/{{i}}","price":{{i * 1.5}},"date":{{1700000000 + i * 86400}}}""");
        }
        _client!.Commit();
    }

    // ── Index lifecycle ────────────────────────────────────────────

    [Fact]
    public void CreateIndex_Succeeds()
    {
        if (SkipNative) return;
        SetupIndex();
        Assert.NotNull(_client);
        _output.WriteLine($"Index created at: {_indexPath}");
    }

    [Fact]
    public void OpenIndex_Succeeds()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.Dispose();

        _client = new SeekStormClient();
        _client.OpenIndex(_indexPath);
        Assert.NotNull(_client);
    }

    // ── Document indexing ──────────────────────────────────────────

    [Fact]
    public void IndexDocument_Succeeds()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.IndexDocument(
            """{"title":"Hello World","body":"This is a test document","url":"https://example.com"}""");
    }

    [Fact]
    public void IndexDocuments_Batch_Succeeds()
    {
        if (SkipNative) return;
        SetupIndex();
        var docs = Enumerable.Range(0, 10)
            .Select(i => $"{{\"title\":\"Batch {i}\",\"body\":\"batch doc {i}\",\"url\":\"url/{i}\"}}");
        _client!.IndexDocuments($"[{string.Join(",", docs)}]");
    }

    // ── Search ─────────────────────────────────────────────────────

    [Fact]
    public void Search_Lexical_ReturnsResults()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(50);

        var result = _client!.Search("test document");
        Assert.NotNull(result);
        Assert.NotEmpty(result.Results);
        Assert.NotEmpty(result.QueryTerms);
        _output.WriteLine($"Found {result.ResultCount} results (total: {result.ResultCountTotal})");
        _output.WriteLine($"First hit: doc_id={result.Results[0].DocId}, score={result.Results[0].Score}");
    }

    [Fact]
    public void Search_WithOffset_ReturnsCorrectPage()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(50);

        var page1 = _client!.Search("document", offset: 0, length: 5);
        var page2 = _client.Search("document", offset: 5, length: 5);

        Assert.Equal(5, page1.Results.Count);
        Assert.Equal(5, page2.Results.Count);
        // Page 2 should have different doc IDs
        var page1Ids = page1.Results.Select(r => r.DocId).ToHashSet();
        var page2Ids = page2.Results.Select(r => r.DocId).ToHashSet();
        Assert.Empty(page1Ids.Intersect(page2Ids));
    }

    [Fact]
    public void Search_VectorMode_DoesNotCrash()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(20);

        var request = new SearchRequest
        {
            Query = "document",
            SearchMode = "Vector",
            QueryVector = Enumerable.Repeat(0.1f, 128).ToArray(),
            SimilarityThreshold = 0.5f,
        };

        // Vector search won't work without embedding index, but shouldn't crash
        Assert.Throws<SeekStormException>(() => _client!.Search(request));
    }

    [Fact]
    public void Search_HybridMode_DoesNotCrash()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(20);

        var request = new SearchRequest
        {
            Query = "document",
            SearchMode = "Hybrid",
            QueryVector = Enumerable.Repeat(0.1f, 128).ToArray(),
            SimilarityThreshold = 0.5f,
        };

        Assert.Throws<SeekStormException>(() => _client!.Search(request));
    }

    // ── Faceted search ─────────────────────────────────────────────

    [Fact]
    public void Search_WithFacetFilter_DoesNotCrash()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(20);

        var request = new SearchRequest
        {
            Query = "document",
            // Facet filter: price range 10-50
            FacetFilter = System.Text.Json.Nodes.JsonNode.Parse("""
            [{"F32":{"field":"price","filter":{"start":10,"end":50}}}]
            """)!.AsArray(),
        };

        var result = _client!.Search(request);
        Assert.NotNull(result);
        _output.WriteLine($"Facet-filtered results: {result.ResultCount}");
    }

    // ── Document retrieval ─────────────────────────────────────────

    [Fact]
    public void GetDocument_RetrievesIndexedDoc()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.IndexDocument(
            """{"title":"Find Me","body":"unique content for retrieval test","url":"https://example.com"}""");
        _client.Commit();

        // Search to find the doc ID
        var result = _client.Search("Find Me");
        Assert.NotEmpty(result.Results);
        var docId = result.Results[0].DocId;

        // Retrieve by ID
        var doc = _client.GetDocument((nuint)docId);
        Assert.Contains("Find Me", doc);
        Assert.Contains("unique content", doc);
    }

    // ── Delete documents ───────────────────────────────────────────

    [Fact]
    public void DeleteDocuments_RemovesFromIndex()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.IndexDocument(
            """{"title":"To Delete","body":"this document will be deleted","url":"https://example.com"}""");
        _client.Commit();

        var before = _client.Search("Delete");
        Assert.NotEmpty(before.Results);
        var docId = before.Results[0].DocId;

        _client.DeleteDocuments([docId]);
        _client.Commit();

        var after = _client.Search("Delete");
        Assert.Empty(after.Results);
    }

    // ── Delete by query ────────────────────────────────────────────

    [Fact]
    public void DeleteDocumentsByQuery_Works()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(30);

        var before = _client!.Search("document 5");
        var countBefore = before.ResultCountTotal;

        _client.DeleteDocumentsByQuery(new DeleteByQueryRequest
        {
            Query = "document 5",
            QueryType = "Intersection",
            Length = 100,
        });
        _client.Commit();

        var after = _client.Search("document 5");
        Assert.True(after.ResultCountTotal < countBefore,
            $"Expected fewer results after deletion. Before: {countBefore}, After: {after.ResultCountTotal}");
    }

    // ── Update documents ───────────────────────────────────────────

    [Fact]
    public void UpdateDocuments_ChangesFields()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.IndexDocument(
            """{"title":"Original","body":"original body","url":"https://example.com"}""");
        _client.Commit();

        var result = _client.Search("Original");
        var docId = result.Results[0].DocId;

        _client.UpdateDocuments(new List<UpdateDocument>
        {
            new() { Id = docId, Document = new() { ["title"] = "Updated!", ["body"] = "modified body" } }
        });
        _client.Commit();

        var after = _client.Search("Updated");
        Assert.NotEmpty(after.Results);
    }

    // ── Iterator ───────────────────────────────────────────────────

    [Fact]
    public void IterateDocuments_ReturnsDocs()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(25);

        var result = _client!.IterateDocuments(new IteratorRequest
        {
            Take = 10,
            IncludeDocument = true,
        });

        Assert.NotNull(result);
        Assert.NotEmpty(result.Results);
        Assert.True(result.Results.Count <= 10);
        _output.WriteLine($"Iterator returned {result.Results.Count} docs");
    }

    // ── Typo tolerance ─────────────────────────────────────────────

    [Fact]
    public void Search_TypoTolerance_ReturnsSuggestions()
    {
        if (SkipNative) return;
        SetupIndex();
        IndexTestDocs(20);

        var request = new SearchRequest
        {
            Query = "dokument",
            QueryRewriting = System.Text.Json.Nodes.JsonNode.Parse("""
            {"mode":"SearchSuggest","correct_threshold":3,"distance":2}
            """)!.AsObject(),
        };

        var result = _client!.Search(request);
        Assert.NotNull(result);
        // Suggestions may or may not be present depending on dictionary
        _output.WriteLine($"Suggestions: {result.Suggestions?.Count ?? 0}");
    }

    // ── Dispose safety ─────────────────────────────────────────────

    [Fact]
    public void DoubleDispose_DoesNotCrash()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.Dispose();
        _client.Dispose(); // second dispose should be safe
    }

    [Fact]
    public void OperationsAfterDispose_Throws()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _client.Search("test"));
    }

    [Fact]
    public void Dispose_commits_pending_documents_and_releases_index_for_reopen()
    {
        if (SkipNative) return;
        SetupIndex();
        _client!.IndexDocument("""{"title":"pendingpersist","body":"queued before dispose"}""");
        _client.Dispose();
        _client = new SeekStormClient();
        _client.OpenIndex(_indexPath);
        Assert.Single(_client.Search("pendingpersist").Results);
    }

    // ── Model round-trip ───────────────────────────────────────────

    [Fact]
    public void SearchRequest_SerializationRoundTrip()
    {
        var original = new SearchRequest
        {
            Query = "hello +world",
            SearchMode = "Hybrid",
            QueryType = "Intersection",
            ResultType = "TopkCount",
            Offset = 5,
            Length = 20,
            Realtime = true,
            EnableEmptyQuery = false,
            FieldFilter = ["title", "body"],
            QueryVector = [0.1f, 0.2f, 0.3f],
            SimilarityThreshold = 0.7f,
            ResultSort = [new ResultSort { Field = "price", Order = "Ascending" }],
        };

        var json = JsonSerializer.Serialize(original, SeekStormJsonContext.Default.SearchRequest);
        var deserialized = JsonSerializer.Deserialize(json, SeekStormJsonContext.Default.SearchRequest);

        Assert.NotNull(deserialized);
        Assert.Equal("hello +world", deserialized.Query);
        Assert.Equal("Hybrid", deserialized.SearchMode);
        Assert.Equal(5, deserialized.Offset);
        Assert.Equal(20, deserialized.Length);
        Assert.NotNull(deserialized.QueryVector);
        Assert.Equal(3, deserialized.QueryVector.Length);
    }

    [Fact]
    public void IndexMeta_SerializationRoundTrip()
    {
        var meta = new IndexMeta
        {
            Name = "test",
            LexicalSimilarity = "Bm25fProximity",
            Tokenizer = "UnicodeAlphanumericChinese",
        };

        var json = JsonSerializer.Serialize(meta, SeekStormJsonContext.Default.IndexMeta);
        var deserialized = JsonSerializer.Deserialize(json, SeekStormJsonContext.Default.IndexMeta);

        Assert.NotNull(deserialized);
        Assert.Equal("Bm25fProximity", deserialized.LexicalSimilarity);
        Assert.Contains("Chinese", deserialized.Tokenizer);
    }
}
