using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using SeekStorm.Bindings;
using SeekStorm.Bindings.Models;

BenchmarkRunner.Run<SearchBenchmarks>(
    args: args,
    config: BenchmarkDotNet.Configs.ManualConfig
        .Create(BenchmarkDotNet.Configs.DefaultConfig.Instance)
        .WithOption(BenchmarkDotNet.Configs.ConfigOptions.DisableOptimizationsValidator, true));

// ── Benchmarks ────────────────────────────────────────────────────────

[MemoryDiagnoser]
[ShortRunJob]
public class SearchBenchmarks
{
    private SeekStormClient? _client;
    private SearchRequest? _searchRequest;

    [GlobalSetup]
    public void Setup()
    {
        _client = new SeekStormClient();

        var meta = new IndexMeta
        {
            Name = "bench_index",
            LexicalSimilarity = "Bm25f",
            Tokenizer = "UnicodeAlphanumeric",
            Stemmer = "None",
            StopWords = "None",
            FrequentWords = "English",
            NgramIndexing = 0,
            DocumentCompression = "Snappy",
            AccessType = "Mmap",
        };

        var schema = new[]
        {
            new SchemaField { Field = "title", FieldType = "Text", Store = true, IndexLexical = true },
            new SchemaField { Field = "body", FieldType = "Text", Store = true, IndexLexical = true },
            new SchemaField { Field = "url", FieldType = "Text", Store = true, IndexLexical = false },
        };

        _client.CreateIndex("/tmp/seekstorm_bench", meta, schema, segmentBits: 11);

        // Index 1000 benchmark documents
        for (int i = 0; i < 1000; i++)
        {
            _client.IndexDocument(
                $"{{\"title\":\"Document {i}\",\"body\":\"This is benchmark document number {i} with some searchable text content for performance testing.\",\"url\":\"https://example.com/doc/{i}\"}}");
        }

        _client.Commit();

        _searchRequest = new SearchRequest
        {
            Query = "benchmark searchable",
            Offset = 0,
            Length = 10,
        };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client?.Dispose();
    }

    /// <summary>
    /// Measure search latency and allocations on the hot path.
    /// Target: &lt; 100 byte allocation per call (excluding result data).
    /// </summary>
    [Benchmark]
    public SearchResult Search()
    {
        return _client!.Search(_searchRequest!);
    }

    /// <summary>
    /// Measure single-document indexing latency.
    /// </summary>
    [Benchmark]
    public void IndexDocument()
    {
        _client!.IndexDocument(
            """{"title":"Extra doc","body":"additional benchmark content","url":"https://example.com/extra"}""");
    }

    /// <summary>
    /// Measure get-document-by-ID latency.
    /// </summary>
    [Benchmark]
    public string GetDocument()
    {
        return _client!.GetDocument(1);
    }

    /// <summary>
    /// Measure batch indexing (10 documents at once).
    /// </summary>
    [Benchmark]
    public void IndexDocumentsBatch()
    {
        var docs = Enumerable.Range(0, 10)
            .Select(i => $"{{\"title\":\"Batch {i}\",\"body\":\"batch document {i}\",\"url\":\"url/{i}\"}}");
        _client!.IndexDocuments($"[{string.Join(",", docs)}]");
    }
}
