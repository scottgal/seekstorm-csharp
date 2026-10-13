using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using SeekStorm.Bindings.Models;

namespace SeekStorm.Bindings;

/// <summary>
/// High-performance, AOT-compatible C# client for the SeekStorm search library.
/// Communicates with the native library via P/Invoke through the seekstorm-ffi cdylib.
///
/// Usage:
/// <code>
/// using var client = new SeekStormClient();
/// await client.CreateIndexAsync("/data/index", meta, schema);
/// await client.IndexDocumentAsync(docJson);
/// await client.CommitAsync();
/// var results = await client.SearchAsync("hello world");
/// </code>
///
/// Both async and sync overloads are provided. The underlying Rust library
/// is async (tokio); sync methods use GetAwaiter().GetResult() internally
/// via a dedicated thread to avoid blocking the tokio runtime.
/// </summary>
public sealed class SeekStormClient : IDisposable
{
    private IndexHandle? _handle;
    private readonly object _sync = new();
    private bool _disposed;

    // ── Index lifecyle ─────────────────────────────────────────────

    /// <summary>
    /// Create a new index at the specified path.
    /// </summary>
    /// <param name="indexPath">Filesystem path for the index directory.</param>
    /// <param name="meta">Index metadata (similarity, tokenizer, etc.).</param>
    /// <param name="schema">Schema field definitions.</param>
    /// <param name="segmentBits">2^N index segments (default 11 = 2048).</param>
    public unsafe void CreateIndex(
        string indexPath, IndexMeta meta, SchemaField[] schema, int segmentBits = 11)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string metaJson = Serialize(meta, SeekStormJsonContext.Default.IndexMeta);
            string schemaJson = Serialize(schema, SeekStormJsonContext.Default.SchemaFieldArray);

            IntPtr rawHandle = IntPtr.Zero;
            byte* errorPtr;
            byte* resultPtr = null;

            try
            {
                using var pathBuf = new Utf8Buffer(indexPath);
                using var metaBuf = new Utf8Buffer(metaJson);
                using var schemaBuf = new Utf8Buffer(schemaJson);

                fixed (byte* pp = pathBuf.Bytes, mp = metaBuf.Bytes, sp = schemaBuf.Bytes)
                {
                    errorPtr = NativeMethods.seekstorm_create_index(
                        pp, mp, sp, (nuint)segmentBits, &rawHandle, &resultPtr);
                }

                if (errorPtr != null)
                    ThrowNativeError(errorPtr);
            }
            finally
            {
                if (resultPtr != null)
                    NativeMethods.seekstorm_free_string(resultPtr);
            }

            var previous = _handle;
            _handle = new IndexHandle(rawHandle);
            previous?.Dispose();

        }
    }

    /// <summary>Async wrapper for CreateIndex.</summary>
    public Task CreateIndexAsync(
        string indexPath, IndexMeta meta, SchemaField[] schema, int segmentBits = 11)
        => Task.Run(() => CreateIndex(indexPath, meta, schema, segmentBits));

    /// <summary>
    /// Open an existing index from disk.
    /// </summary>
    public unsafe void OpenIndex(string indexPath)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IntPtr rawHandle = IntPtr.Zero;
            byte* resultPtr = null;

            try
            {
                using var pathBuf = new Utf8Buffer(indexPath);

                byte* errorPtr;
                fixed (byte* pp = pathBuf.Bytes)
                {
                    errorPtr = NativeMethods.seekstorm_open_index(pp, &rawHandle, &resultPtr);
                }

                if (errorPtr != null)
                    ThrowNativeError(errorPtr);
            }
            finally
            {
                if (resultPtr != null)
                    NativeMethods.seekstorm_free_string(resultPtr);
            }

            var previous = _handle;
            _handle = new IndexHandle(rawHandle);
            previous?.Dispose();

        }
    }

    /// <summary>Async wrapper for OpenIndex.</summary>
    public Task OpenIndexAsync(string indexPath)
        => Task.Run(() => OpenIndex(indexPath));

    // ── Document indexing ──────────────────────────────────────────

    /// <summary>Index a single document as a JSON object string.</summary>
    public unsafe void IndexDocument(string documentJson)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            CallHandleJson(documentJson, &NativeMethods.seekstorm_index_document);

        }
    }

    /// <summary>Async wrapper for IndexDocument.</summary>
    public Task IndexDocumentAsync(string documentJson)
        => Task.Run(() => IndexDocument(documentJson));

    /// <summary>Index multiple documents as a JSON array string.</summary>
    public unsafe void IndexDocuments(string documentsJson)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            CallHandleJson(documentsJson, &NativeMethods.seekstorm_index_documents);

        }
    }

    /// <summary>Async wrapper for IndexDocuments.</summary>
    public Task IndexDocumentsAsync(string documentsJson)
        => Task.Run(() => IndexDocuments(documentsJson));

    // ── Document deletion ──────────────────────────────────────────

    /// <summary>Delete documents by their IDs.</summary>
    public unsafe void DeleteDocuments(ulong[] docIds)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            string json = Serialize(docIds, SeekStormJsonContext.Default.UInt64Array);
            CallHandleJson(json, &NativeMethods.seekstorm_delete_documents);

        }
    }

    /// <summary>Async wrapper for DeleteDocuments.</summary>
    public Task DeleteDocumentsAsync(ulong[] docIds)
        => Task.Run(() => DeleteDocuments(docIds));

    /// <summary>Delete documents matching a query.</summary>
    public unsafe void DeleteDocumentsByQuery(DeleteByQueryRequest request)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            string json = Serialize(request, SeekStormJsonContext.Default.DeleteByQueryRequest);
            CallHandleJson(json, &NativeMethods.seekstorm_delete_documents_by_query);

        }
    }

    /// <summary>Async wrapper for DeleteDocumentsByQuery.</summary>
    public Task DeleteDocumentsByQueryAsync(DeleteByQueryRequest request)
        => Task.Run(() => DeleteDocumentsByQuery(request));

    // ── Commit ─────────────────────────────────────────────────────

    /// <summary>Commit indexed documents to disk.</summary>
    public unsafe void Commit()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            CallHandleOnly(&NativeMethods.seekstorm_commit);

        }
    }

    /// <summary>Async wrapper for Commit.</summary>
    public Task CommitAsync()
        => Task.Run(() => Commit());

    // ── Search ─────────────────────────────────────────────────────

    /// <summary>
    /// Search the index. Returns deserialized results.
    /// This is the hot path. Keep allocations minimal.
    /// </summary>
    public unsafe SearchResult Search(SearchRequest request)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            string requestJson = Serialize(request, SeekStormJsonContext.Default.SearchRequest);

            byte* resultPtr = null;
            try
            {
                using var buffer = new Utf8Buffer(requestJson);

                byte* errorPtr;
                fixed (byte* buf = buffer.Bytes)
                {
                    errorPtr = NativeMethods.seekstorm_search(
                        _handle!.DangerousGetHandle(), buf, &resultPtr);
                }

                if (errorPtr != null)
                    ThrowNativeError(errorPtr);

                var result = resultPtr != null
                    ? DeserializeSearchResult(resultPtr)
                    : new SearchResult();
                if (request.Fields is not null)
                    foreach (var hit in result.Results)
                    {
                        var document = System.Text.Json.Nodes.JsonNode.Parse(GetDocument((nuint)hit.DocId))!.AsObject();
                        hit.Document = new();
                        foreach (var field in request.Fields)
                            if (document.TryGetPropertyValue(field, out var value)) hit.Document[field] = value?.DeepClone();
                    }
                return result;
            }
            finally
            {
                if (resultPtr != null)
                    NativeMethods.seekstorm_free_string(resultPtr);
            }

        }
    }

    /// <summary>Async wrapper for Search.</summary>
    public Task<SearchResult> SearchAsync(SearchRequest request)
        => Task.Run(() => Search(request));

    /// <summary>
    /// Convenience overload: search with a plain query string (lexical, top-10).
    /// </summary>
    public SearchResult Search(string query, int offset = 0, int length = 10)
        => Search(new SearchRequest
        {
            Query = query,
            Offset = offset,
            Length = length,
        });

    /// <summary>Async convenience overload.</summary>
    public Task<SearchResult> SearchAsync(string query, int offset = 0, int length = 10)
        => Task.Run(() => Search(query, offset, length));

    // ── Document retrieval ─────────────────────────────────────────

    /// <summary>Get a document by its ID. Returns raw JSON.</summary>
    public unsafe string GetDocument(nuint docId)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            byte* resultPtr = null;
            try
            {
                byte* errorPtr = NativeMethods.seekstorm_get_document(
                    _handle!.DangerousGetHandle(), docId, &resultPtr);

                if (errorPtr != null)
                    ThrowNativeError(errorPtr);

                return resultPtr != null ? ReadUtf8String(resultPtr) : "{}";
            }
            finally
            {
                if (resultPtr != null)
                    NativeMethods.seekstorm_free_string(resultPtr);
            }

        }
    }

    /// <summary>Async wrapper for GetDocument.</summary>
    public Task<string> GetDocumentAsync(nuint docId)
        => Task.Run(() => GetDocument(docId));

    // ── v2: Update documents ────────────────────────────────────────

    /// <summary>Update existing documents with new field values.</summary>
    /// <param name="updatesJson">JSON array of [doc_id, {fields}] tuples.</param>
    public unsafe void UpdateDocuments(string updatesJson)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            CallHandleJson(updatesJson, &NativeMethods.seekstorm_update_documents);

        }
    }

    /// <summary>Async wrapper for UpdateDocuments.</summary>
    public Task UpdateDocumentsAsync(string updatesJson)
        => Task.Run(() => UpdateDocuments(updatesJson));

    /// <summary>
    /// Update documents by strongly-typed pairs. Each tuple is (docId, fieldDictionary).
    /// </summary>
    public void UpdateDocuments(List<UpdateDocument> updates)
    {
        // Serialize as [[id, doc], [id, doc], ...]. The FFI expects Vec<(u64, Document)>
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < updates.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('[');
            sb.Append(updates[i].Id);
            sb.Append(',');
            sb.Append(JsonSerializer.Serialize(updates[i].Document,
                SeekStormJsonContext.Default.DictionaryStringObject));
            sb.Append(']');
        }
        sb.Append(']');
        UpdateDocuments(sb.ToString());
    }

    /// <summary>Async typed wrapper for UpdateDocuments.</summary>
    public Task UpdateDocumentsAsync(List<UpdateDocument> updates)
        => Task.Run(() => UpdateDocuments(updates));

    // ── v2: Document iteration ──────────────────────────────────────

    /// <summary>Iterate through all documents in the index.</summary>
    public unsafe IteratorResult IterateDocuments(IteratorRequest request)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            string json = Serialize(request, SeekStormJsonContext.Default.IteratorRequest);

            byte* resultPtr = null;
            try
            {
                using var buffer = new Utf8Buffer(json);

                byte* errorPtr;
                fixed (byte* buf = buffer.Bytes)
                {
                    errorPtr = NativeMethods.seekstorm_iterate_documents(
                        _handle!.DangerousGetHandle(), buf, &resultPtr);
                }

                if (errorPtr != null)
                    ThrowNativeError(errorPtr);

                if (resultPtr != null)
                {
                    int resultLen = 0;
                    while (resultPtr[resultLen] != 0) resultLen++;
                    var resultSpan = new ReadOnlySpan<byte>(resultPtr, resultLen);
                    return JsonSerializer.Deserialize(resultSpan,
                        SeekStormJsonContext.Default.IteratorResult) ?? new IteratorResult();
                }
                return new IteratorResult();
            }
            finally
            {
                if (resultPtr != null)
                    NativeMethods.seekstorm_free_string(resultPtr);
            }

        }
    }

    /// <summary>Async wrapper for IterateDocuments.</summary>
    public Task<IteratorResult> IterateDocumentsAsync(IteratorRequest request)
        => Task.Run(() => IterateDocuments(request));

    // ── IDisposable ────────────────────────────────────────────────

    /// <summary>Replace a logical document using an stored exact String16 facet key. Native IDs may change.</summary>
    public void UpsertDocument(string keyField, string key, System.Text.Json.Nodes.JsonObject document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyField);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(document);
        if (document[keyField]?.GetValue<string>() != key)
            throw new ArgumentException("The document must contain the supplied stable key.", nameof(document));
        lock (_sync)
        {
            DeleteDocumentByKey(keyField, key);
            IndexDocument(document.ToJsonString());
            Commit();
        }
    }

    /// <summary>Delete all versions of one logical document. The key field must be a stored exact String16 facet.</summary>
    public void DeleteDocumentByKey(string keyField, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyField);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_sync)
        {
            while (true)
            {
                var found = Search(new SearchRequest
                {
                    EnableEmptyQuery = true,
                    Realtime = true,
                    Length = 256,
                    FacetFilter = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode)SearchConfiguration.StringFacet(keyField, key)),
                    Fields = [keyField]
                });
                if (found.Results.Count == 0) return;
                if (found.Results.Any(hit => hit.Document?[keyField]?.GetValue<string>() != key))
                    throw new InvalidOperationException("The stable key field must be configured as a stored String16 facet.");
                DeleteDocuments(found.Results.Select(hit => hit.DocId).ToArray());
                Commit();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _handle?.Dispose();
            _handle = null;

        }
    }

    // ── Internal helpers ───────────────────────────────────────────

    private void EnsureHandle()
    {
        if (_handle is null || _handle.IsInvalid)
            throw new InvalidOperationException(
                "No open index. Call CreateIndex or OpenIndex first.");
    }

    private unsafe void CallHandleJson(
        string json,
        delegate*<IntPtr, byte*, byte**, byte*> nativeFunc)
    {
        byte* resultPtr = null;
        try
        {
            using var buffer = new Utf8Buffer(json);

            byte* errorPtr;
            fixed (byte* buf = buffer.Bytes)
            {
                errorPtr = nativeFunc(
                    _handle!.DangerousGetHandle(), buf, &resultPtr);
            }

            if (errorPtr != null)
                ThrowNativeError(errorPtr);
        }
        finally
        {
            if (resultPtr != null)
                NativeMethods.seekstorm_free_string(resultPtr);
        }
    }

    private unsafe void CallHandleOnly(
        delegate*<IntPtr, byte**, byte*> nativeFunc)
    {
        byte* resultPtr = null;
        try
        {
            byte* errorPtr = nativeFunc(
                _handle!.DangerousGetHandle(), &resultPtr);

            if (errorPtr != null)
                ThrowNativeError(errorPtr);
        }
        finally
        {
            if (resultPtr != null)
                NativeMethods.seekstorm_free_string(resultPtr);
        }
    }

    private unsafe void ThrowNativeError(byte* errorPtr)
    {
        string error = ReadUtf8String(errorPtr);
        NativeMethods.seekstorm_free_string(errorPtr);
        throw new SeekStormException(error);
    }

    private static unsafe string ReadUtf8String(byte* ptr)
    {
        int len = 0;
        while (ptr[len] != 0) len++;
        return System.Text.Encoding.UTF8.GetString(ptr, len);
    }

    private static string Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);

    private unsafe SearchResult DeserializeSearchResult(byte* ptr)
    {
        int len = 0;
        while (ptr[len] != 0) len++;
        var span = new ReadOnlySpan<byte>(ptr, len);
        return JsonSerializer.Deserialize(span, SeekStormJsonContext.Default.SearchResult)
               ?? new SearchResult();
    }
}
