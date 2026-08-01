# SeekStorm C# SDK

[![NuGet](https://img.shields.io/badge/nuget-v0.1.0-blue)](https://www.nuget.org/packages/SeekStorm.Bindings)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

C# FFI bindings for the [SeekStorm](https://github.com/SeekStorm/SeekStorm) search library. Embed sub-millisecond vector and lexical search directly in your .NET application with no server and no HTTP.

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
└── seekstorm crate v3.3 (Rust lib)
    BM25F, vectors, faceting, geo
```

## Features

| Category | Capabilities |
|---|---|
| Lexical search | BM25F and BM25F_Proximity, intersection/union/phrase/not queries |
| Vector search | ANN with clustering, F32/I8 precision, cosine/dot/euclidean similarity |
| Hybrid search | Lexical and vector combined with Reciprocal Rank Fusion |
| Faceting | Numeric range facets (U8 through F64), string facets, histogram aggregation |
| Geo search | Proximity filtering and distance sorting (km/miles) |
| Typo tolerance | Query rewriting, spelling correction, query completion |
| Documents | Index, batch, update, delete by ID or query, document iterator |
| Real-time | Documents searchable immediately after indexing |

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

// Vector search
var request = new SearchRequest {
    Query = "semantic concept",
    SearchMode = "Vector",
    QueryVector = new float[] { 0.1f, 0.2f, /* ... 128 dims */ },
};
var vecResults = client.Search(request);

// Faceted search
request.FacetFilter = JsonNode.Parse("""[{"F32":{"field":"price","filter":{"start":10,"end":50}}}]""")!.AsArray();
request.ResultSort = [new ResultSort { Field = "price", Order = "Ascending" }];

// Typo tolerance
request.Query = "dokument";
request.QueryRewriting = JsonNode.Parse("""{"mode":"SearchRewrite","distance":2,"correct_threshold":3}""")!.AsObject();

client.Dispose();
```

## AOT publishing

The SDK supports NativeAOT. Publish with:

```bash
dotnet publish -c Release -r osx-arm64 /p:PublishAot=true
```

All serialization uses System.Text.Json source generators. No reflection, no runtime codegen. Native handles use SafeHandle. Buffers under 4KB are stack-allocated; larger payloads use ArrayPool.

## Platforms

| Platform | RID |
|---|---|
| macOS ARM64 | `osx-arm64` |
| macOS x64 | `osx-x64` |
| Linux x64 | `linux-x64` |
| Linux ARM64 | `linux-arm64` |
| Windows x64 | `win-x64` |

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
