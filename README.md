# SeekStorm C# SDK

[![NuGet](https://img.shields.io/badge/nuget-v0.2.4-blue)](https://www.nuget.org/packages/SeekStorm.Bindings)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

C# FFI bindings for the [SeekStorm](https://github.com/SeekStorm/SeekStorm) search library. Embed vector and lexical search directly in your .NET application with no server and no HTTP.

```
Your .NET 10 AOT App
│
├── SeekStormClient (C# public API)
│   ├── .Search(query)
│   ├── .IndexDocument(json)
│   └── .CreateIndex(path, meta, schema)
│
├── libseekstorm_ffi (Rust cdylib, C-ABI)
│   JSON at boundary, tokio runtime
│
└── seekstorm crate v3.4.1 (Rust lib)
    BM25F, vectors, faceting, geo
```

## Features

| Category | Capabilities |
|---|---|
| Lexical search | BM25F and BM25F_Proximity, intersection/union/phrase/not queries |
| Vector search | External F32 vectors, cosine/dot/euclidean similarity; typed Model2Vec configuration |
| Hybrid search | Lexical and vector combined with Reciprocal Rank Fusion |
| Faceting | Numeric range facets (U8 through F64), string facets, histogram aggregation |
| Geo search | Point facet filtering; search highlight/distance enrichment is explicitly unsupported |
| Query rewriting | Typed rewriting configuration; spelling dictionaries disabled because upstream 3.4.1 can panic during commit |
| Documents | Index, batch, update, delete by ID or query, document iterator |
| Visibility | Indexing queues work; Commit verifies submitted document counts and stored fields before durable reads |

## Quick start

Install the package:

```bash
dotnet add package SeekStorm.Bindings
```

Usage:

```csharp
using SeekStorm.Bindings;
using SeekStorm.Bindings.Models;

var client = new SeekStormClient();

// Create an index
var meta = new IndexMeta { Name = "docs" };
var schema = new[] {
    new SchemaField { Field = "title", FieldType = "Text", Store = true, IndexLexical = true },
    new SchemaField { Field = "body", FieldType = "Text", Store = true, IndexLexical = true },
};
client.CreateIndex("/data/my_index", meta, schema);

// Index documents
client.IndexDocument("""{"title":"Hello","body":"world"}""");
client.IndexDocuments("""[{"title":"Doc 2","body":"search text"},{"title":"Doc 3","body":"more text"}]""");
client.Commit();

// Search
var results = client.Search("search text");
foreach (var hit in results.Results)
    Console.WriteLine($"doc_id={hit.DocId} score={hit.Score}");

client.Dispose();
```

## External embeddings and evidence filters

Use your embedding model to populate a `Json` field with `IndexVector = true`. Dimensions must match the external configuration. A vector index is not created by merely passing `QueryVector` to search.

```csharp
using System.Text.Json.Nodes;

using var evidence = new SeekStormClient();
evidence.CreateIndex("/data/evidence", new IndexMeta {
    Name = "evidence", Inference = SearchConfiguration.ExternalVectors(384)
}, [
    new SchemaField { Field = "key", FieldType = "String16", Store = true, Facet = true },
    new SchemaField { Field = "role", FieldType = "String16", Store = true, Facet = true },
    new SchemaField { Field = "body", FieldType = "Text", Store = true, IndexLexical = true },
    new SchemaField { Field = "embedding", FieldType = "Json", IndexVector = true }
]);
// Supply actual 384-dimensional vectors from the same model for documents and queries.
// IndexDocument JSON: {"key":"claim-1","role":"role-1","body":"...","embedding":[...]}
evidence.Commit();
var hits = evidence.Search(new SearchRequest {
    Query = "production agent evaluation", SearchMode = "Hybrid",
    QueryVector = queryEmbedding,
    FacetFilter = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode)SearchConfiguration.StringFacet("role", "role-1")),
    Fields = ["key", "body"], Length = 8
});
```

`FieldFilter` selects searchable text fields; `FacetFilter` restricts documents. `Fields` hydrates the selected stored fields in `SearchHit.Document` (null means no hydration). `UpsertDocument(keyField, key, document)` and `DeleteDocumentByKey(keyField, key)` require a **stored String16 facet**. They commit their changes and preserve the logical key, not the engine's numeric document ID. Upsert is delete then insert; keep authoritative source data separately so an interrupted update can be rebuilt.

SeekStorm 3.4.1's vector branch does not apply facet filters. This binding intersects the complete ranked candidate set with native lexical facet eligibility **before pagination**. This compatibility path supports up to 100,000 indexed documents and rejects larger filtered-vector queries explicitly. Facet aggregations are supported only in lexical mode; vector/hybrid aggregations fail explicitly because the engine computes them only for lexical candidates. It is intended for bounded evidence collections; it does not claim the performance of engine-level vector prefiltering. Lexical searches on external-vector indexes also supply an unused unit vector to work around the engine's unconditional vector requirement. Both behaviors have package-consumer regression checks.

Union queries with facet filters also expose a 3.4.1 aggregation-slot bug. The binding initializes zero-length aggregation slots and removes its internal placeholder from results. Unwinding engine panics are caught at the C ABI boundary and returned as `SeekStormException`, so they do not unwind across P/Invoke. The smoke pipeline includes a multi-term union query with exact role filtering, and a Rust test verifies panic-to-error conversion.

## Verification and packaging

`./build.sh` builds the pinned native engine, stages the host runtime, and runs every integration test and the Release smoke pipeline. CI builds all six runtimes and preserves `runtimes/<rid>/native/` in artifacts. It verifies the actual `.nupkg` contents, then runs both managed and NativeAOT package consumers on every host, without manual library copies. Publishing is gated on those consumers; manual workflow runs default to verification only.

The smoke pipeline checks actual vector ranking, hybrid role isolation, stable-key replacement, filtered deletion, reopening an index, invalid filter rejection and a 2 MB input. Checks throw in Release builds; they do not depend on `Debug.Assert`.

## AOT publishing

The SDK supports NativeAOT. Publish with:

```bash
dotnet publish -c Release -r osx-arm64 /p:PublishAot=true
```

All serialization uses System.Text.Json source generators. No reflection, no runtime codegen. Native handles use SafeHandle. UTF-8 input buffers use ArrayPool and are cleared when returned; large documents do not consume the thread stack. Operations on each client are serialized, including handle replacement and disposal. Async wrappers schedule the same synchronous native calls on the thread pool.

## Platforms

| Platform | RID |
|---|---|
| macOS ARM64 | `osx-arm64` |
| macOS x64 | `osx-x64` |
| Linux x64 | `linux-x64` |
| Linux ARM64 | `linux-arm64` |
| Windows x64 | `win-x64` |
| Windows ARM64 | `win-arm64` |

## Building from source

Prerequisites: Rust 1.97+ and .NET SDK 10.0+

```bash
# Build the Rust FFI crate
cd src/seekstorm-ffi
cargo build --release

# Copy to runtimes directory
mkdir -p ../../runtimes/osx-arm64/native
cp target/release/libseekstorm_ffi.dylib ../../runtimes/osx-arm64/native/

# Build the C# SDK
cd ../SeekStorm.Bindings
dotnet build -c Release

# Run tests
dotnet test

# Run benchmarks
cd ../../bench/SeekStorm.Benchmarks
dotnet run -c Release
```

## Architecture

Two crates:

1. `src/seekstorm-ffi` (Rust cdylib). C-ABI wrapper around the `seekstorm` crate. JSON at the boundary, tokio runtime internally.
2. `src/SeekStorm.Bindings` (C# classlib, net10.0). Public API with P/Invoke interop, SafeHandle wrappers, and source-generated JSON serialization.

Documents and results cross the FFI boundary as UTF-8 JSON, matching the format SeekStorm's own REST API uses. This avoids struct marshaling and stays compatible with all SeekStorm field types.

## License

Apache 2.0, matching [SeekStorm's license](https://github.com/SeekStorm/SeekStorm).


## Binding contract and upstream limits (0.2.4)

This release pins **unmodified SeekStorm 3.4.1**. It does not fork or patch the engine.
Embedded indexes currently require **one shard** and `segmentBits = 11`. This bounds the default 400 MB-per-shard posting buffer and makes submitted
stored-field verification reliable. Multi-shard indexes must be rebuilt for this
embedded contract. Upstream reopening hardcodes 11 segment bits; other values are
rejected rather than creating an index that cannot be reopened. Typed fields must be omitted when absent; explicit nulls and
unknown document keys are rejected, rather than silently coerced. Null JSON values
remain valid except where an external-vector array is required.

Spelling dictionary configuration is **rejected before index allocation**. Upstream
3.4.1's dictionary commit can panic on an empty term; a current-engine update check
found no released fix. Existing indexes enabling that option cannot be opened by
this release. Resume retrieval does not use it. Highlights and distance-field
search enrichment also fail explicitly until the binding implements them.

Indexing submits asynchronous engine work. Commit checks expected document counts
and fingerprints of stored fields. It cannot observe every upstream background
failure: upstream auxiliary tasks may fail after documents were stored, and schemas
without stored fields have count-only verification. Do not treat a successful call
as a transactional guarantee across auxiliary engine structures. The CI smoke
runner additionally fails on native panic/task-failure output.

One client serializes its operations; raw C calls are also serialized per handle.
Closing/freeing a raw handle still requires exclusive ownership. Use only one
writer per index directory. Opening the same path on an active client is idempotent;
recreating that active path is rejected. A failed switch to another path retains
the original usable index. Symlink aliases and multiple-client writers are outside
this ownership contract. Model2Vec configuration serialization is verified; model
download/inference availability depends on the configured upstream model.
