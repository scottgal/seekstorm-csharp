using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SeekStorm.Bindings;
using SeekStorm.Bindings.Models;

// Smoke test for SeekStorm C# SDK
// Exercises the full pipeline: create, index, commit, search, get, update, delete, iterate.
// Exit code 0 = pass, 1 = fail.

var exitCode = 0;
var stopwatch = Stopwatch.StartNew();
var indexPath = Path.Combine(Path.GetTempPath(), $"seekstorm_smoke_{Guid.NewGuid():N}");

try
{
    Console.WriteLine($"SeekStorm C# SDK smoke test");
    Console.WriteLine($"Index path: {indexPath}");

    using var client = new SeekStormClient();

    // 1. Create index
    Console.Write("  create_index... ");
    var meta = new IndexMeta
    {
        Name = "smoke_test",
        LexicalSimilarity = "Bm25f",
        Tokenizer = "UnicodeAlphanumeric",
        Stemmer = "None",
        DocumentCompression = "Snappy",
        AccessType = "Mmap",
    };
    var schema = new[]
    {
        new SchemaField { Field = "title",  FieldType = "Text",      Store = true, IndexLexical = true,  Boost = 10 },
        new SchemaField { Field = "body",   FieldType = "Text",      Store = true, IndexLexical = true },
        new SchemaField { Field = "url",    FieldType = "Text",      Store = true, IndexLexical = false },
        new SchemaField { Field = "price",  FieldType = "F32",       Store = true, IndexLexical = false, Facet = true },
        new SchemaField { Field = "date",   FieldType = "Timestamp", Store = true, IndexLexical = false, Facet = true },
    };
    client.CreateIndex(indexPath, meta, schema);
    Console.WriteLine("ok");

    // 2. Index documents
    Console.Write("  index_documents... ");
    var docs = new List<string>();
    for (int i = 0; i < 200; i++)
        docs.Add($$"""{"title":"Document {{i}}","body":"This is smoke test document number {{i}} with searchable text content for benchmarking.","url":"https://example.com/doc/{{i}}","price":{{i * 1.5}},"date":{{1700000000 + i * 86400}}}""");
    client.IndexDocuments($"[{string.Join(",", docs)}]");
    Console.WriteLine("ok");

    // 3. Commit
    Console.Write("  commit... ");
    client.Commit();
    Console.WriteLine("ok");

    // 4. Lexical search
    Console.Write("  search (lexical)... ");
    var results = client.Search("document searchable");
    Debug.Assert(results.Results.Count > 0, "no results");
    Debug.Assert(results.ResultCountTotal > 0, "zero total count");
    Debug.Assert(results.QueryTerms.Count > 0, "no query terms");
    Console.WriteLine($"ok ({results.ResultCount} hits, {results.ResultCountTotal} total)");

    // 5. Search with offset
    Console.Write("  search (paged)... ");
    var page1 = client.Search("document", offset: 0, length: 5);
    var page2 = client.Search("document", offset: 5, length: 5);
    Debug.Assert(page1.Results.Count == 5, $"page1 count {page1.Results.Count}");
    Debug.Assert(page2.Results.Count == 5, $"page2 count {page2.Results.Count}");
    var p1Ids = new HashSet<ulong>(page1.Results.Select(r => r.DocId));
    var p2Ids = new HashSet<ulong>(page2.Results.Select(r => r.DocId));
    Debug.Assert(!p1Ids.Overlaps(p2Ids), "pages overlap");
    Console.WriteLine("ok");

    // 6. Get document
    Console.Write("  get_document... ");
    var docId = results.Results[0].DocId;
    var doc = client.GetDocument((nuint)docId);
    Debug.Assert(doc.Contains("searchable"), "document content missing");
    Console.WriteLine("ok");

    // 7. Update document
    Console.Write("  update_document... ");
    client.UpdateDocuments(new List<UpdateDocument>
    {
        new() { Id = docId, Document = new() { ["title"] = "UPDATED TITLE", ["body"] = "modified body" } }
    });
    client.Commit();
    var updated = client.Search("UPDATED");
    Debug.Assert(updated.Results.Count > 0, "updated doc not found");
    Console.WriteLine("ok");

    // 8. Delete by query
    Console.Write("  delete_by_query... ");
    client.DeleteDocumentsByQuery(new DeleteByQueryRequest
    {
        Query = "UPDATED",
        QueryType = "Intersection",
    });
    client.Commit();
    var afterDel = client.Search("UPDATED");
    Debug.Assert(afterDel.Results.Count == 0, "deleted doc still present");
    Console.WriteLine("ok");

    // 9. Iterator
    Console.Write("  iterate... ");
    var iter = client.IterateDocuments(new IteratorRequest
    {
        Take = 10,
        IncludeDocument = true,
    });
    Debug.Assert(iter.Results.Count > 0, "iterator returned zero docs");
    Debug.Assert(iter.Results.Count <= 10, "iterator returned too many docs");
    Console.WriteLine($"ok ({iter.Results.Count} docs)");

    // 10. Faceted search
    Console.Write("  search (faceted)... ");
    var facetReq = new SearchRequest
    {
        Query = "document",
        FacetFilter = JsonNode.Parse("""[{"F32":{"field":"price","filter":{"start":100,"end":200}}}]""")!.AsArray(),
    };
    var facetResults = client.Search(facetReq);
    Debug.Assert(facetResults.Results.Count > 0, "facet filter got zero results");
    Console.WriteLine($"ok ({facetResults.ResultCount} hits)");

    // 11. Sort
    Console.Write("  search (sorted)... ");
    var sortReq = new SearchRequest
    {
        Query = "document",
        Length = 3,
        ResultSort = [new ResultSort { Field = "price", Order = "Ascending" }],
    };
    var sorted = client.Search(sortReq);
    Debug.Assert(sorted.Results.Count == 3, $"sorted count {sorted.Results.Count}");
    Console.WriteLine("ok");

    // 12. Typo tolerance (needs dictionary at index creation; returns suggestions when available)
    Console.Write("  search (typo)... ");
    var typoReq = new SearchRequest
    {
        Query = "dokument",
        QueryRewriting = JsonNode.Parse("""{"mode":"SearchSuggest","distance":2,"correct_threshold":3}""")!.AsObject(),
    };
    var typoResults = client.Search(typoReq);
    Console.WriteLine($"ok ({typoResults.ResultCount} hits, {typoResults.Suggestions?.Count ?? 0} suggestions)");

    // 13. Vector search (won't find results without embedding index, but shouldn't crash)
    Console.Write("  search (vector)... ");
    var vecReq = new SearchRequest
    {
        Query = "document",
        SearchMode = "Vector",
        QueryVector = Enumerable.Repeat(0.1f, 128).ToArray(),
        SimilarityThreshold = 0.5f,
    };
    client.Search(vecReq);
    Console.WriteLine("ok");

    // 14. Hybrid search
    Console.Write("  search (hybrid)... ");
    var hybReq = new SearchRequest
    {
        Query = "document",
        SearchMode = "Hybrid",
        QueryVector = Enumerable.Repeat(0.1f, 128).ToArray(),
        SimilarityThreshold = 0.5f,
    };
    client.Search(hybReq);
    Console.WriteLine("ok");

    // Done
    stopwatch.Stop();
    Console.WriteLine($"\nAll {14} tests passed in {stopwatch.Elapsed.TotalSeconds:F1}s.");
}
catch (Exception ex)
{
    Console.WriteLine($"\nFAIL: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    exitCode = 1;
}
finally
{
    try { if (Directory.Exists(indexPath)) Directory.Delete(indexPath, true); }
    catch { }
}

return exitCode;
