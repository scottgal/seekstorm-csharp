//! C-ABI FFI bindings for the SeekStorm search library.
//!
//! Every function follows the same pattern:
//! - Takes JSON-serialized inputs, returns JSON-serialized outputs
//! - Returns null on success (with output written to `*out_json`),
//!   or a heap-allocated error message on failure
//! - The caller must free returned strings via `seekstorm_free_string`
//!
//! # Safety
//! All public functions are `unsafe` — the caller must guarantee:
//! - Pointer arguments are non-null and valid for the stated lifetime
//! - Strings are valid UTF-8 and null-terminated
//! - Handles returned by create/open are only used with the correct function family

use libc::c_char;
use seekstorm::index::{
    create_index, open_index,
    DeleteDocuments, DeleteDocumentsByQuery,
    IndexArc, IndexDocument, IndexDocuments, IndexMetaObject,
    UpdateDocuments, Document,
};
use seekstorm::search::{
    QueryType, QueryRewriting, ResultType, Search, SearchMode, FacetFilter, QueryFacet,
    ResultSort,
};
use seekstorm::iterator::GetIterator;
use seekstorm::commit::Commit;
use seekstorm::vector::Embedding;
use serde_json::Value;
use std::ffi::{CStr, CString};
use std::path::Path;
use std::sync::OnceLock;
use tokio::runtime::Runtime;

// ── Global tokio runtime ──────────────────────────────────────────────

fn runtime() -> &'static Runtime {
    static RT: OnceLock<Runtime> = OnceLock::new();
    RT.get_or_init(|| {
        Runtime::new().expect("failed to create tokio runtime")
    })
}

// ── Helpers ───────────────────────────────────────────────────────────

unsafe fn cstr_to_str<'a>(ptr: *const c_char) -> &'a str {
    assert!(!ptr.is_null(), "null pointer passed to FFI");
    CStr::from_ptr(ptr).to_str().expect("invalid UTF-8 in FFI string")
}

unsafe fn json_from_cstr(ptr: *const c_char) -> serde_json::Result<Value> {
    serde_json::from_str(cstr_to_str(ptr))
}

fn to_owned_cstring(s: &str) -> *mut c_char {
    CString::new(s)
        .unwrap_or_else(|_| CString::new("string contained null byte").unwrap())
        .into_raw()
}

fn write_output(output: String, out_json: *mut *mut c_char) {
    if !out_json.is_null() {
        unsafe { *out_json = to_owned_cstring(&output); }
    }
}

fn write_error(err: String, out_json: *mut *mut c_char) -> *mut c_char {
    write_output(err.clone(), out_json);
    to_owned_cstring(&err)
}

unsafe fn index_from_handle(handle: *mut libc::c_void) -> &'static IndexArc {
    &*(handle as *const IndexArc)
}

// ── Public C-ABI exports ──────────────────────────────────────────────

/// Create a new index. Returns null on success (out_json receives `{"id": N}`).
///
/// # Arguments
/// - `index_path` — filesystem path for the index directory
/// - `meta_json` — JSON-serialized IndexMetaObject
/// - `schema_json` — JSON array of schema field definitions
/// - `segment_number_bits` — 2^N segments (e.g. 11 → 2048)
/// - `out_handle` — receives the opaque index handle on success
/// - `out_json` — receives JSON result or is written with error context
///
/// Returns null on success, or a heap-allocated error string on failure.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_create_index(
    index_path: *const c_char,
    meta_json: *const c_char,
    schema_json: *const c_char,
    segment_number_bits: usize,
    out_handle: *mut *mut libc::c_void,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let path = Path::new(cstr_to_str(index_path));
        let meta: IndexMetaObject = serde_json::from_str(cstr_to_str(meta_json))
            .map_err(|e| format!("invalid meta_json: {e}"))?;
        let schema: Vec<seekstorm::index::SchemaField> =
            serde_json::from_str(cstr_to_str(schema_json))
                .map_err(|e| format!("invalid schema_json: {e}"))?;

        let index_arc = runtime().block_on(create_index(
            path,
            meta,
            &schema,
            &Vec::new(),
            segment_number_bits,
            false,
            None,
        ))
        .map_err(|e| format!("create_index failed: {e}"))?;

        // Leak the Arc so the handle outlives this function
        let handle = Box::into_raw(Box::new(index_arc)) as *mut libc::c_void;
        if !out_handle.is_null() {
            unsafe { *out_handle = handle; }
        }
        Ok(r#"{"status":"created"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Open an existing index. Returns null on success.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_open_index(
    index_path: *const c_char,
    out_handle: *mut *mut libc::c_void,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let path = Path::new(cstr_to_str(index_path));
        let index_arc = runtime().block_on(open_index(path))
            .map_err(|e| format!("open_index failed: {e}"))?;

        let handle = Box::into_raw(Box::new(index_arc)) as *mut libc::c_void;
        if !out_handle.is_null() {
            unsafe { *out_handle = handle; }
        }
        Ok(r#"{"status":"opened"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Close an index and free its resources.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_close_index(
    handle: *mut libc::c_void,
) {
    if handle.is_null() {
        return;
    }
    let _dropped = Box::from_raw(handle as *mut IndexArc);
    // IndexArc (Arc<RwLock<Index>>) drops, potentially freeing the last reference
}

/// Index a single document. `document_json` is a JSON object with field→value mappings.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_index_document(
    handle: *mut libc::c_void,
    document_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let doc: Document = serde_json::from_str(cstr_to_str(document_json))
            .map_err(|e| format!("invalid document_json: {e}"))?;

        runtime().block_on(index_arc.index_document(doc, seekstorm::index::FileType::None));
        Ok(r#"{"status":"indexed"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Index multiple documents. `documents_json` is a JSON array of document objects.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_index_documents(
    handle: *mut libc::c_void,
    documents_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let docs: Vec<Document> = serde_json::from_str(cstr_to_str(documents_json))
            .map_err(|e| format!("invalid documents_json: {e}"))?;

        runtime().block_on(index_arc.index_documents(docs));
        Ok(r#"{"status":"indexed"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Delete documents by their IDs. `doc_ids_json` is a JSON array of u64 values.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_delete_documents(
    handle: *mut libc::c_void,
    doc_ids_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let ids: Vec<u64> = serde_json::from_str(cstr_to_str(doc_ids_json))
            .map_err(|e| format!("invalid doc_ids_json: {e}"))?;

        runtime().block_on(index_arc.delete_documents(ids));
        Ok(r#"{"status":"deleted"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Delete documents matching a query.
/// `request_json` has fields: query, query_type (default "Intersection"),
/// offset, length, include_uncommitted, field_filter, facet_filter, result_sort
#[no_mangle]
pub unsafe extern "C" fn seekstorm_delete_documents_by_query(
    handle: *mut libc::c_void,
    request_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let req: Value = json_from_cstr(request_json)
            .map_err(|e| format!("invalid request_json: {e}"))?;

        let query = req["query"].as_str().unwrap_or("").to_string();
        let query_type = parse_query_type(req["query_type"].as_str());
        let offset = req["offset"].as_u64().unwrap_or(0) as usize;
        let length = req["length"].as_u64().unwrap_or(10) as usize;
        let include_uncommitted = req["include_uncommitted"].as_bool().unwrap_or(false);

        runtime().block_on(index_arc.delete_documents_by_query(
            query, query_type, offset, length, include_uncommitted,
            Vec::new(), Vec::new(), Vec::new(),
        ));
        Ok(r#"{"status":"deleted"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Commit indexed documents to disk.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_commit(
    handle: *mut libc::c_void,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        runtime().block_on(index_arc.commit());
        Ok(r#"{"status":"committed"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Search the index. `request_json` contains the search parameters (see spec).
/// Returns JSON matching SearchRequestObject schema.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_search(
    handle: *mut libc::c_void,
    request_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let req: Value = json_from_cstr(request_json)
            .map_err(|e| format!("invalid request_json: {e}"))?;

        let query = req["query"].as_str().unwrap_or("").to_string();
        let query_vector = req.get("query_vector")
            .and_then(|v| v.as_array())
            .map(|arr| {
                let f32s: Vec<f32> = arr.iter()
                    .filter_map(|v| v.as_f64().map(|f| f as f32))
                    .collect();
                Embedding::F32(f32s)
            });

        let search_mode = parse_search_mode(&req);
        let enable_empty_query = req["enable_empty_query"].as_bool().unwrap_or(false);
        let offset = req["offset"].as_u64().unwrap_or(0) as usize;
        let length = req["length"].as_u64().unwrap_or(10) as usize;
        let query_type = parse_query_type(req["query_type"].as_str());
        let result_type = parse_result_type(req["result_type"].as_str());
        let include_uncommitted = req["realtime"].as_bool().unwrap_or(false);

        // v2: full parameter support
        let field_filter: Vec<String> = req["field_filter"].as_array()
            .map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect())
            .unwrap_or_default();
        let query_facets: Vec<QueryFacet> = req["query_facets"].as_array()
            .map(|a| serde_json::from_value(Value::Array(a.clone())).unwrap_or_default())
            .unwrap_or_default();
        let facet_filter: Vec<FacetFilter> = req["facet_filter"].as_array()
            .map(|a| serde_json::from_value(Value::Array(a.clone())).unwrap_or_default())
            .unwrap_or_default();
        let result_sort: Vec<ResultSort> = req["result_sort"].as_array()
            .map(|a| serde_json::from_value(Value::Array(a.clone())).unwrap_or_default())
            .unwrap_or_default();
        let query_rewriting = parse_query_rewriting(&req);

        let result = runtime().block_on(index_arc.search(
            query,
            query_vector,
            query_type,
            search_mode,
            enable_empty_query,
            offset,
            length,
            result_type,
            include_uncommitted,
            field_filter,
            query_facets,
            facet_filter,
            result_sort,
            query_rewriting,
        ));

        let result_json = serde_json::to_string(&result)
            .map_err(|e| format!("serialization failed: {e}"))?;
        Ok(result_json)
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Get a single document by its ID. Returns JSON object with field→value pairs.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_get_document(
    handle: *mut libc::c_void,
    doc_id: usize,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let index = runtime().block_on(index_arc.read());

        let doc = runtime().block_on(
            index.get_document(doc_id, false, &None, &std::collections::HashSet::new(), &Vec::new())
        ).map_err(|e| format!("get_document failed: {e}"))?;

        serde_json::to_string(&doc)
            .map_err(|e| format!("serialization failed: {e}"))
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Update existing documents.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_update_documents(
    handle: *mut libc::c_void,
    updates_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let updates: Vec<(u64, Document)> = serde_json::from_str(cstr_to_str(updates_json))
            .map_err(|e| format!("invalid updates_json (expected [[id, doc], ...]): {e}"))?;

        runtime().block_on(index_arc.update_documents(updates));
        Ok(r#"{"status":"updated"}"#.to_string())
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Iterate through documents. `request_json` fields: docid (optional u64), skip, take (isize),
/// include_deleted, include_document (bool), fields ([string]).
/// Returns IteratorResult as JSON.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_iterate_documents(
    handle: *mut libc::c_void,
    request_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match (|| -> Result<String, String> {
        let index_arc = index_from_handle(handle);
        let req: Value = json_from_cstr(request_json)
            .map_err(|e| format!("invalid request_json: {e}"))?;

        let docid = req["docid"].as_u64();
        let skip = req["skip"].as_u64().unwrap_or(0) as usize;
        let take = req["take"].as_i64().unwrap_or(100) as isize;
        let include_deleted = req["include_deleted"].as_bool().unwrap_or(false);
        let include_document = req["include_document"].as_bool().unwrap_or(true);
        let fields: Vec<String> = req["fields"].as_array()
            .map(|a| a.iter().filter_map(|v| v.as_str().map(String::from)).collect())
            .unwrap_or_default();

        let result = runtime().block_on(index_arc.get_iterator(
            docid, skip, take, include_deleted, include_document, fields,
        ));

        serde_json::to_string(&result)
            .map_err(|e| format!("serialization failed: {e}"))
    })() {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Free a string previously returned by any seekstorm_* function.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        let _ = CString::from_raw(ptr);
    }
}

// ── Internal parsers ──────────────────────────────────────────────────

fn parse_query_type(s: Option<&str>) -> QueryType {
    match s {
        Some("Union") => QueryType::Union,
        Some("Intersection") => QueryType::Intersection,
        Some("Phrase") => QueryType::Phrase,
        Some("Not") => QueryType::Not,
        _ => QueryType::Intersection,
    }
}

fn parse_result_type(s: Option<&str>) -> ResultType {
    match s {
        Some("Count") => ResultType::Count,
        Some("Topk") => ResultType::Topk,
        Some("TopkCount") | None => ResultType::TopkCount,
        _ => ResultType::TopkCount,
    }
}

fn parse_search_mode(req: &Value) -> SearchMode {
    match req["search_mode"].as_str() {
        Some("Vector") => SearchMode::Vector {
            similarity_threshold: req["similarity_threshold"].as_f64().map(|v| v as f32),
            ann_mode: seekstorm::vector_similarity::AnnMode::All,
        },
        Some("Hybrid") => SearchMode::Hybrid {
            similarity_threshold: req["similarity_threshold"].as_f64().map(|v| v as f32),
            ann_mode: seekstorm::vector_similarity::AnnMode::All,
        },
        _ => SearchMode::Lexical,
    }
}

fn parse_query_rewriting(req: &Value) -> QueryRewriting {
    let qr = &req["query_rewriting"];
    let mode = qr["mode"].as_str().unwrap_or("SearchOnly");
    match mode {
        "SearchSuggest" => QueryRewriting::SearchSuggest {
            correct: qr["correct_threshold"].as_u64().map(|v| v as usize),
            distance: qr["distance"].as_u64().unwrap_or(2) as usize,
            term_length_threshold: None,
            complete: qr["complete_threshold"].as_u64().map(|v| v as usize),
            length: qr["suggestion_length"].as_u64().map(|v| v as usize),
        },
        "SearchRewrite" => QueryRewriting::SearchRewrite {
            correct: qr["correct_threshold"].as_u64().map(|v| v as usize),
            distance: qr["distance"].as_u64().unwrap_or(2) as usize,
            term_length_threshold: None,
            complete: qr["complete_threshold"].as_u64().map(|v| v as usize),
            length: qr["suggestion_length"].as_u64().map(|v| v as usize),
        },
        "SuggestOnly" => QueryRewriting::SuggestOnly {
            correct: qr["correct_threshold"].as_u64().map(|v| v as usize),
            distance: qr["distance"].as_u64().unwrap_or(2) as usize,
            term_length_threshold: None,
            complete: qr["complete_threshold"].as_u64().map(|v| v as usize),
            length: qr["suggestion_length"].as_u64().map(|v| v as usize),
        },
        _ => QueryRewriting::SearchOnly,
    }
}

// ── Tests ─────────────────────────────────────────────────────────────

#[cfg(test)]
mod tests {
    // Integration tests belong in the C# layer; Rust-side tests
    // here would need a live seekstorm index on disk.
}
