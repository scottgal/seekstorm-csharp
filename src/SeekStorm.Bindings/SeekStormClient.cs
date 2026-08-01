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

    // ── Index lifecyle ─────────────────────────────────────────────

    /// <summary>
    /// Create a new index at the specified path.
    /// </summary>
    /// <param name="indexPath">Filesystem path for the index directory.</param>
    /// <param name="meta">Index metadata (similarity, tokenizer, etc.).</param>
    /// <param name="schema">Schema field definitions.</param>
    /// <param name="segmentBits">2^N index segments (default 11 → 2048).</param>
    public unsafe void CreateIndex(
        string indexPath, IndexMeta meta, SchemaField[] schema, int segmentBits = 11)
    {
        string metaJson = Serialize(meta, SeekStormJsonContext.Default.IndexMeta);
        string schemaJson = Serialize(schema, SeekStormJsonContext.Default.SchemaFieldArray);

        IntPtr rawHandle = IntPtr.Zero;
        byte* errorPtr;
        byte* resultPtr = null;

        try
        {
            int pathLen = System.Text.Encoding.UTF8.GetByteCount(indexPath);
            int metaLen = System.Text.Encoding.UTF8.GetByteCount(metaJson);
            int schemaLen = System.Text.Encoding.UTF8.GetByteCount(schemaJson);

            Span<byte> pathBuf = stackalloc byte[pathLen + 1];
            Span<byte> metaBuf = stackalloc byte[metaLen + 1];
            Span<byte> schemaBuf = stackalloc byte[schemaLen + 1];

            System.Text.Encoding.UTF8.GetBytes(indexPath, pathBuf);
            System.Text.Encoding.UTF8.GetBytes(metaJson, metaBuf);
            System.Text.Encoding.UTF8.GetBytes(schemaJson, schemaBuf);
            pathBuf[pathLen] = 0;
            metaBuf[metaLen] = 0;
            schemaBuf[schemaLen] = 0;

            fixed (byte* pp = pathBuf, mp = metaBuf, sp = schemaBuf)
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

        _handle = new IndexHandle(rawHandle);
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
        IntPtr rawHandle = IntPtr.Zero;
        byte* resultPtr = null;

        try
        {
            int pathLen = System.Text.Encoding.UTF8.GetByteCount(indexPath);
            Span<byte> pathBuf = stackalloc byte[pathLen + 1];
            System.Text.Encoding.UTF8.GetBytes(indexPath, pathBuf);
            pathBuf[pathLen] = 0;

            byte* errorPtr;
            fixed (byte* pp = pathBuf)
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

        _handle = new IndexHandle(rawHandle);
    }

    /// <summary>Async wrapper for OpenIndex.</summary>
    public Task OpenIndexAsync(string indexPath)
        => Task.Run(() => OpenIndex(indexPath));

    // ── Document indexing ──────────────────────────────────────────

    /// <summary>Index a single document as a JSON object string.</summary>
    public unsafe void IndexDocument(string documentJson)
    {
        EnsureHandle();
        CallHandleJson(documentJson, &NativeMethods.seekstorm_index_document);
    }

    /// <summary>Async wrapper for IndexDocument.</summary>
    public Task IndexDocumentAsync(string documentJson)
        => Task.Run(() => IndexDocument(documentJson));

    /// <summary>Index multiple documents as a JSON array string.</summary>
    public unsafe void IndexDocuments(string documentsJson)
    {
        EnsureHandle();
        CallHandleJson(documentsJson, &NativeMethods.seekstorm_index_documents);
    }

    /// <summary>Async wrapper for IndexDocuments.</summary>
    public Task IndexDocumentsAsync(string documentsJson)
        => Task.Run(() => IndexDocuments(documentsJson));

    // ── Document deletion ──────────────────────────────────────────

    /// <summary>Delete documents by their IDs.</summary>
    public unsafe void DeleteDocuments(ulong[] docIds)
    {
        EnsureHandle();
        string json = Serialize(docIds, SeekStormJsonContext.Default.UInt64Array);
        CallHandleJson(json, &NativeMethods.seekstorm_delete_documents);
    }

    /// <summary>Async wrapper for DeleteDocuments.</summary>
    public Task DeleteDocumentsAsync(ulong[] docIds)
        => Task.Run(() => DeleteDocuments(docIds));

    /// <summary>Delete documents matching a query.</summary>
    public unsafe void DeleteDocumentsByQuery(DeleteByQueryRequest request)
    {
        EnsureHandle();
        string json = Serialize(request, SeekStormJsonContext.Default.DeleteByQueryRequest);
        CallHandleJson(json, &NativeMethods.seekstorm_delete_documents_by_query);
    }

    /// <summary>Async wrapper for DeleteDocumentsByQuery.</summary>
    public Task DeleteDocumentsByQueryAsync(DeleteByQueryRequest request)
        => Task.Run(() => DeleteDocumentsByQuery(request));

    // ── Commit ─────────────────────────────────────────────────────

    /// <summary>Commit indexed documents to disk.</summary>
    public unsafe void Commit()
    {
        EnsureHandle();
        CallHandleOnly(&NativeMethods.seekstorm_commit);
    }

    /// <summary>Async wrapper for Commit.</summary>
    public Task CommitAsync()
        => Task.Run(() => Commit());

    // ── Search ─────────────────────────────────────────────────────

    /// <summary>
    /// Search the index. Returns deserialized results.
    /// This is the hot path — keep allocations minimal.
    /// </summary>
    public unsafe SearchResult Search(SearchRequest request)
    {
        EnsureHandle();
        string requestJson = Serialize(request, SeekStormJsonContext.Default.SearchRequest);

        byte* resultPtr = null;
        try
        {
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(requestJson);
            Span<byte> buffer = stackalloc byte[byteCount + 1];
            System.Text.Encoding.UTF8.GetBytes(requestJson, buffer);
            buffer[byteCount] = 0;

            byte* errorPtr;
            fixed (byte* buf = buffer)
            {
                errorPtr = NativeMethods.seekstorm_search(
                    _handle!.DangerousGetHandle(), buf, &resultPtr);
            }

            if (errorPtr != null)
                ThrowNativeError(errorPtr);

            return resultPtr != null
                ? DeserializeSearchResult(resultPtr)
                : new SearchResult();
        }
        finally
        {
            if (resultPtr != null)
                NativeMethods.seekstorm_free_string(resultPtr);
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

    /// <summary>Async wrapper for GetDocument.</summary>
    public Task<string> GetDocumentAsync(nuint docId)
        => Task.Run(() => GetDocument(docId));

    // ── v2: Update documents ────────────────────────────────────────

    /// <summary>Update existing documents with new field values.</summary>
    /// <param name="updatesJson">JSON array of [doc_id, {fields}] tuples.</param>
    public unsafe void UpdateDocuments(string updatesJson)
    {
        EnsureHandle();
        CallHandleJson(updatesJson, &NativeMethods.seekstorm_update_documents);
    }

    /// <summary>Async wrapper for UpdateDocuments.</summary>
    public Task UpdateDocumentsAsync(string updatesJson)
        => Task.Run(() => UpdateDocuments(updatesJson));

    /// <summary>
    /// Update documents by strongly-typed pairs. Each tuple is (docId, fieldDictionary).
    /// </summary>
    public void UpdateDocuments(List<UpdateDocument> updates)
    {
        // Serialize as [[id, doc], [id, doc], ...] — the FFI expects Vec<(u64, Value)>
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
        EnsureHandle();
        string json = Serialize(request, SeekStormJsonContext.Default.IteratorRequest);

        byte* resultPtr = null;
        try
        {
            int byteCount = Encoding.UTF8.GetByteCount(json);
            Span<byte> buffer = stackalloc byte[byteCount + 1];
            Encoding.UTF8.GetBytes(json, buffer);
            buffer[byteCount] = 0;

            byte* errorPtr;
            fixed (byte* buf = buffer)
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

    /// <summary>Async wrapper for IterateDocuments.</summary>
    public Task<IteratorResult> IterateDocumentsAsync(IteratorRequest request)
        => Task.Run(() => IterateDocuments(request));

    // ── IDisposable ────────────────────────────────────────────────

    public void Dispose()
    {
        _handle?.Dispose();
        _handle = null;
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
            int byteCount = System.Text.Encoding.UTF8.GetByteCount(json);
            Span<byte> buffer = stackalloc byte[byteCount + 1];
            System.Text.Encoding.UTF8.GetBytes(json, buffer);
            buffer[byteCount] = 0;

            byte* errorPtr;
            fixed (byte* buf = buffer)
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
