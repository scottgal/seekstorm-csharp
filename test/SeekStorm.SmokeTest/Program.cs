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
        AccessType = "Mmap"
    };
    var schema = new[]
    {
        new SchemaField { Field = "title",  FieldType = "Text",      Store = true, IndexLexical = true,  Boost = 10 },
        new SchemaField { Field = "body",   FieldType = "Text",      Store = true, IndexLexical = true, DictionarySource = true },
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
    Require(results.Results.Count > 0, "no results");
    Require(results.ResultCountTotal > 0, "zero total count");
    Require(results.QueryTerms.Count > 0, "no query terms");
    Console.WriteLine($"ok ({results.ResultCount} hits, {results.ResultCountTotal} total)");

    var countOnly = client.Search(new SearchRequest { Query = "document", Length = 0 });
    Require(countOnly.Results.Count == 0 && countOnly.ResultCountTotal == 200, "length-zero search was not count-only");
    client.DeleteDocumentsByQuery(new DeleteByQueryRequest { Query = "document", Length = 0 });
    client.Commit();
    Require(client.Search("document").ResultCountTotal == 200, "length-zero delete removed documents");
    foreach (var invalid in new SearchRequest[] {
        new() { Query = "document", Offset = -1 },
        new() { Query = "document", Length = -1 },
        new() { Query = "document", SearchMode = "Vectro" },
        new() { Query = "document", QueryType = "Typo" },
        new() { Query = "document", ResultType = "Typo" },
        new() { Query = "document", Highlights = [new Highlight { Field = "body" }] },
        new() { Query = "document", DistanceFields = [new DistanceField { Field = "point" }] },
        new() { Query = "document", QueryRewriting = new JsonObject { ["mode"] = "Typo" } }
    })
    {
        try { client.Search(invalid); throw new InvalidOperationException("invalid/unsupported query accepted"); }
        catch (SeekStormException) { }
    }
    try { client.IndexDocument("""{"title":"bad price","price":"cheap"}"""); throw new InvalidOperationException("invalid numeric field accepted"); }
    catch (SeekStormException) { }

    // 5. Search with offset
    Console.Write("  search (paged)... ");
    var page1 = client.Search("document", offset: 0, length: 5);
    var page2 = client.Search("document", offset: 5, length: 5);
    Require(page1.Results.Count == 5, $"page1 count {page1.Results.Count}");
    Require(page2.Results.Count == 5, $"page2 count {page2.Results.Count}");
    var p1Ids = new HashSet<ulong>(page1.Results.Select(r => r.DocId));
    var p2Ids = new HashSet<ulong>(page2.Results.Select(r => r.DocId));
    Require(!p1Ids.Overlaps(p2Ids), "pages overlap");
    Console.WriteLine("ok");

    // 6. Get document
    Console.Write("  get_document... ");
    var docId = results.Results[0].DocId;
    var doc = client.GetDocument((nuint)docId);
    Require(doc.Contains("searchable"), "document content missing");
    Console.WriteLine("ok");

    // 7. Update document
    Console.Write("  update_document... ");
    client.UpdateDocuments(new List<UpdateDocument>
    {
        new() { Id = docId, Document = new() { ["title"] = "UPDATED TITLE", ["body"] = "modified body" } }
    });
    client.Commit();
    var updated = client.Search("UPDATED");
    Require(updated.Results.Count > 0, "updated doc not found");
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
    Require(afterDel.Results.Count == 0, "deleted doc still present");
    Console.WriteLine("ok");

    // 9. Iterator
    Console.Write("  iterate... ");
    var iter = client.IterateDocuments(new IteratorRequest
    {
        Take = 10,
        IncludeDocument = true,
    });
    Require(iter.Results.Count > 0, "iterator returned zero docs");
    Require(iter.Results.Count <= 10, "iterator returned too many docs");
    Console.WriteLine($"ok ({iter.Results.Count} docs)");

    // 10. Faceted search
    Console.Write("  search (faceted)... ");
    var facetReq = new SearchRequest
    {
        Query = "document",
        FacetFilter = JsonNode.Parse("""[{"F32":{"field":"price","filter":{"start":100,"end":200}}}]""")!.AsArray(),
    };
    var facetResults = client.Search(facetReq);
    Require(facetResults.Results.Count > 0, "facet filter got zero results");
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
    Require(sorted.Results.Count == 3, $"sorted count {sorted.Results.Count}");
    Console.WriteLine("ok");

    // 12. Broken optional upstream spelling must fail explicitly before allocation.
    Console.Write("  spelling dictionary rejection... ");
    using (var unsupported = new SeekStormClient())
    {
        ExpectRejected(() => unsupported.CreateIndex(indexPath + "_spelling",
            new IndexMeta { SpellingCorrection = new SpellingCorrectionConfig() }, schema), "unsafe spelling dictionary was accepted");
    }
    Console.WriteLine("ok");

    // Real external-vector index, including exact role filtering and stable keys.
    var vectorPath = indexPath + "_vectors";
    try
    {
        using (var vectors = new SeekStormClient())
        {
            vectors.CreateIndex(vectorPath, new IndexMeta
            {
                Name = "evidence",
                Inference = SearchConfiguration.ExternalVectors(64)
            }, [
                new SchemaField { Field = "key", FieldType = "String16", Store = true, Facet = true },
                new SchemaField { Field = "role", FieldType = "String16", Store = true, Facet = true },
                new SchemaField { Field = "body", FieldType = "Text", Store = true, IndexLexical = true, DictionarySource = true },
                new SchemaField { Field = "embedding", FieldType = "Json", IndexVector = true }
            ]);
            float[] Vector(int position) { var v = new float[64]; v[position] = 1; return v; }
            JsonObject Evidence(string key, string role, string body, int position) => new()
            {
                ["key"] = key,
                ["role"] = role,
                ["body"] = body,
                ["embedding"] = new JsonArray(Vector(position).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray())
            };
            vectors.UpsertDocument("key", "a", Evidence("a", "role-a", "production agent orchestration", 0));
            vectors.UpsertDocument("key", "b", Evidence("b", "role-b", "database recovery", 1));
            var semantic = vectors.Search(new SearchRequest
            {
                SearchMode = "Vector",
                QueryVector = Vector(1),
                SimilarityThreshold = 0.1f,
                Fields = ["key"]
            });
            Require(semantic.Results.Count > 0 && semantic.Results[0].Document?["key"]?.GetValue<string>() == "b", "vector ranking failed");
            try
            {
                vectors.Search(new SearchRequest { SearchMode = "Vector", QueryVector = new float[63] });
                throw new InvalidOperationException("wrong vector dimensions accepted");
            }
            catch (SeekStormException) { }
            foreach (var mode in new[] { "Vector", "Hybrid" })
            {
                try
                {
                    vectors.Search(new SearchRequest
                    {
                        Query = "production",
                        SearchMode = mode,
                        QueryVector = Vector(0),
                        QueryFacets = JsonNode.Parse("""[{"String16":{"field":"role","prefix":"","length":10}}]""")!.AsArray()
                    });
                    throw new InvalidOperationException("unsupported vector facet aggregation accepted");
                }
                catch (SeekStormException ex)
                {
                    Require(ex.Message.Contains("facet aggregation"), "aggregation failed for unrelated reason");
                }
            }
            var unionFiltered = vectors.Search(new SearchRequest
            {
                Query = "production agent orchestration missingword",
                QueryType = "Union",
                FacetFilter = new JsonArray((JsonNode)SearchConfiguration.StringFacet("role", "role-a")),
                Fields = ["key"]
            });
            Require(unionFiltered.Results.Count == 1 && unionFiltered.Results[0].Document?["key"]?.GetValue<string>() == "a", "union facet search failed");
            Require(unionFiltered.Facets?.AsObject().ContainsKey("__seekstorm_binding_alignment__") != true, "internal alignment facet leaked");
            var hybrid = vectors.Search(new SearchRequest
            {
                Query = "production agent",
                SearchMode = "Hybrid",
                QueryVector = Vector(0),
                SimilarityThreshold = 0.1f,
                FacetFilter = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode)SearchConfiguration.StringFacet("role", "role-a")),
                Fields = ["key", "role"]
            });
            Require(hybrid.Results.Count == 1 && hybrid.Results[0].Document?["key"]?.GetValue<string>() == "a", "hybrid role filter failed");
            var filteredVector = vectors.Search(new SearchRequest
            {
                SearchMode = "Vector",
                QueryVector = Vector(1),
                Length = 1,
                FacetFilter = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode)SearchConfiguration.StringFacet("role", "role-a")),
                Fields = ["key"]
            });
            Require(filteredVector.Results.Count == 1 && filteredVector.Results[0].Document?["key"]?.GetValue<string>() == "a", "filter applied after top-k pagination");
            try
            {
                var invalid = Evidence("a", "role-a", "invalid replacement", 0);
                invalid["embedding"] = new JsonArray(JsonValue.Create(1.0));
                vectors.UpsertDocument("key", "a", invalid);
                throw new InvalidOperationException("invalid replacement accepted");
            }
            catch (SeekStormException) { }
            Require(vectors.Search("production").Results.Count == 1, "invalid upsert deleted prior evidence");
            vectors.UpsertDocument("key", "a", Evidence("a", "role-a", "updated orchestration", 0));
            Require(vectors.Search("production").Results.Count == 0, "upsert retained old text");
            Require(vectors.Search("updated").Results.Count == 1, "upsert duplicated or lost key");
            // Invalid filters must fail rather than silently widen the search.
            try
            {
                vectors.Search(new SearchRequest { Query = "updated", FacetFilter = new JsonArray((JsonNode)new JsonObject { ["INVALID"] = true }) });
                throw new InvalidOperationException("invalid facet accepted");
            }
            catch (SeekStormException) { }
            try
            {
                vectors.Search(new SearchRequest
                {
                    Query = "updated",
                    FacetFilter = new JsonArray((JsonNode)SearchConfiguration.StringFacet("missing_role", "role-a"))
                });
                throw new InvalidOperationException("unknown facet field accepted");
            }
            catch (SeekStormException) { }
            vectors.DeleteDocumentsByQuery(new DeleteByQueryRequest
            {
                Query = "updated",
                FacetFilter = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode)SearchConfiguration.StringFacet("role", "role-b"))
            });
            vectors.Commit();
            Require(vectors.Search("updated").Results.Count == 1, "filtered deletion removed another role");
            vectors.DeleteDocumentsByQuery(new DeleteByQueryRequest
            {
                Query = "database",
                FacetFilter = new JsonArray((JsonNode)SearchConfiguration.StringFacet("role", "role-b"))
            });
            vectors.Commit();
            Require(vectors.Search("database").Results.Count == 0, "positive filtered deletion failed on vector index");
            vectors.UpsertDocument("key", "b", Evidence("b", "role-b", "database recovery", 1));
            vectors.DeleteDocumentByKey("key", "b");
        }
        using (var reopened = new SeekStormClient())
        {
            reopened.OpenIndex(vectorPath);
            Require(reopened.Search("updated").Results.Count == 1, "reopen lost upsert");
            Require(reopened.Search("database").Results.Count == 0, "reopen resurrected deletion");
        }
        Console.WriteLine("  vector ranking, hybrid role filtering, stable upsert, filtered deletion and reopen... ok");
    }
    finally { if (Directory.Exists(vectorPath)) Directory.Delete(vectorPath, true); }

    // Large JSON inputs previously used unbounded stack allocation.
    client.IndexDocument(new JsonObject { ["title"] = "largepayload", ["body"] = new string('x', 2_000_000) }.ToJsonString());
    client.Commit();
    Require(client.Search("largepayload").Results.Count == 1, "large document not searchable");
    Console.WriteLine("  2 MB document input... ok");

    // Done
    stopwatch.Stop();
    Console.WriteLine($"\nAll pipeline checks passed in {stopwatch.Elapsed.TotalSeconds:F1}s.");
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

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void ExpectRejected(Action action, string message)
{
    try { action(); }
    catch (SeekStormException error)
    {
        Require(error.Message.Contains("spelling"), "operation failed for unrelated reason");
        return;
    }
    throw new InvalidOperationException(message);
}
