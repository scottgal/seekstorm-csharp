# SeekStorm C# SDK

[![NuGet](https://img.shields.io/badge/nuget-v0.1.0-blue)](https://www.nuget.org/packages/SeekStorm.Bindings)
[![.NET](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![NativeAOT](https://img.shields.io/badge/NativeAOT-ready-green)](#)
[![License](https://img.shields.io/badge/license-Apache%202.0-blue)](LICENSE)

High-performance, **NativeAOT-compatible C# FFI bindings** for the [SeekStorm](https://github.com/SeekStorm/SeekStorm) search library. Embed sub-millisecond vector & lexical search directly in your .NET application — no server, no HTTP.

```
┌────────────────────────────────────────────┐
│  Your .NET 10 AOT App                      │
│  ┌──────────────────────────────────────┐  │
│  │  SeekStormClient (C# public API)    │  │
│  │  .Search("hello world")              │  │
│  │  .IndexDocument(json)                │  │
│  │  .CreateIndex(path, meta, schema)    │  │
│  └──────────────┬───────────────────────┘  │
│                 │ P/Invoke (C-ABI)          │
│  ┌──────────────▼───────────────────────┐  │
│  │  libseekstorm_ffi (Rust cdylib)     │  │
│  │  JSON-at-boundary, tokio-driven      │  │
│  └──────────────┬───────────────────────┘  │
│                 │                          │
│  ┌──────────────▼───────────────────────┐  │
│  │  seekstorm crate v3.3 (Rust lib)    │  │
│  │  BM25F · vectors · faceting · geo    │  │
│  └──────────────────────────────────────┘  │
└────────────────────────────────────────────┘
```

## Features

| Category | Capabilities |
|---|---|
| **Lexical search** | BM25F / BM25F_Proximity, intersection/union/phrase/not queries |
| **Vector search** | ANN with clustering, F32/I8 precision, cosine/dot/euclidean |
| **Hybrid search** | Combined lexical + vector with Reciprocal Rank Fusion |
| **Faceting** | Numeric ranges (U8-F64), string facets, histogram aggregation |
| **Geo search** | Proximity filtering + distance sorting (km/miles) |
| **Typo tolerance** | Query rewriting, spelling correction, instant search |
| **Documents** | Index, batch, update, delete (by ID or query), iterator |
| **Real-time** | Documents searchable the millisecond they're indexed |

## Quick start

### 1. Install the NuGet package

```bash
dotnet add package SeekStorm.Bindings
```

### 2. Use it

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
// → automatically corrects to "document" and returns results + suggestions

client.Dispose();
```

## AOT publishing

The SDK is fully NativeAOT-compatible:

```bash
dotnet publish -c Release -r osx-arm64 /p:PublishAot=true
```

- Zero reflection — all JSON via System.Text.Json source generators
- No runtime codegen — `IsAotCompatible=true`
- SafeHandle for all native resources — no IntPtr leaks
- Stack-allocated buffers for small JSON, ArrayPool for large payloads

## Platforms

| Platform | RID | Status |
|---|---|---|
| macOS ARM64 | `osx-arm64` | ✅ Built & tested |
| macOS x64 | `osx-x64` | ✅ Supported |
| Linux x64 | `linux-x64` | ✅ Supported |
| Linux ARM64 | `linux-arm64` | ✅ Supported |
| Windows x64 | `win-x64` | ✅ Supported |

## Building from source

```bash
# Prerequisites
# - Rust 1.97+ (install: https://rustup.rs)
# - .NET SDK 10.0+

# Build the Rust FFI crate
cd src/seekstorm-ffi
cargo build --release

# Copy to runtimes
mkdir -p ../../runtimes/osx-arm64/native
cp target/release/libseekstorm_ffi.dylib ../../runtimes/osx-arm64/native/

# Build the C# SDK
cd ../SeekStorm.Bindings
dotnet build -c Release

# Run tests (set SKIP_INTEGRATION_TESTS=0 with native binary)
dotnet test

# Run benchmarks
cd ../../bench/SeekStorm.Benchmarks
dotnet run -c Release
```

## Architecture

This is a **two-layer FFI SDK**:

1. **`src/seekstorm-ffi`** (Rust cdylib) — C-ABI wrapper around the `seekstorm` crate. JSON at the boundary, tokio-driven internally.
2. **`src/SeekStorm.Bindings`** (C# classlib, net10.0) — Public API with P/Invoke interop, SafeHandle wrappers, and source-generated JSON.

Documents and results cross the FFI boundary as **UTF-8 JSON** — the same format SeekStorm's own REST API uses. This avoids complex struct marshaling and stays compatible with SeekStorm's field-type richness.

## License

Apache 2.0 — matches [SeekStorm's license](https://github.com/SeekStorm/SeekStorm).
