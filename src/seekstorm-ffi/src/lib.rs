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
//! - Closing a handle requires exclusive ownership: no concurrent calls or later reuse

use libc::c_char;
use seekstorm::commit::Commit;
use seekstorm::index::{
    create_index, open_index, Close, DeleteDocuments, Document, IndexArc, IndexDocument,
    IndexDocuments, IndexMetaObject, UpdateDocuments,
};
use seekstorm::iterator::GetIterator;
use seekstorm::search::{
    FacetFilter, QueryFacet, QueryRewriting, QueryType, ResultSort, ResultType, Search, SearchMode,
};
use seekstorm::vector::{Embedding, Inference};
use serde_json::Value;
use std::ffi::{CStr, CString};
use std::hash::{Hash, Hasher};
use std::path::Path;
use std::sync::{Mutex, OnceLock};
use tokio::runtime::Runtime;

// ── Global tokio runtime ──────────────────────────────────────────────

fn runtime() -> &'static Runtime {
    static RT: OnceLock<Runtime> = OnceLock::new();
    RT.get_or_init(|| Runtime::new().expect("failed to create tokio runtime"))
}

// ── Helpers ───────────────────────────────────────────────────────────

unsafe fn cstr_to_str<'a>(ptr: *const c_char) -> &'a str {
    assert!(!ptr.is_null(), "null pointer passed to FFI");
    CStr::from_ptr(ptr)
        .to_str()
        .expect("invalid UTF-8 in FFI string")
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
        unsafe {
            *out_json = to_owned_cstring(&output);
        }
    }
}

fn write_error(err: String, out_json: *mut *mut c_char) -> *mut c_char {
    write_output(err.clone(), out_json);
    to_owned_cstring(&err)
}

struct Handle {
    index: IndexArc,
    operation: Mutex<()>,
    next_id: Mutex<usize>,
    pending: Mutex<Vec<(usize, Option<u64>)>>,
}

fn make_handle(index: IndexArc) -> *mut libc::c_void {
    let next = runtime().block_on(async { index.read().await.indexed_doc_count().await });
    Box::into_raw(Box::new(Handle {
        index,
        operation: Mutex::new(()),
        next_id: Mutex::new(next),
        pending: Mutex::new(Vec::new()),
    })) as *mut libc::c_void
}

unsafe fn state_from_handle(handle: *mut libc::c_void) -> &'static Handle {
    assert!(!handle.is_null(), "null index handle");
    &*(handle as *const Handle)
}
unsafe fn index_from_handle(handle: *mut libc::c_void) -> &'static IndexArc {
    &state_from_handle(handle).index
}

fn document_hash(document: &Document) -> u64 {
    fn canonical(value: Value) -> Value {
        match value {
            Value::Object(object) => {
                let mut entries: Vec<_> = object.into_iter().collect();
                entries.sort_by(|a, b| a.0.cmp(&b.0));
                Value::Object(
                    entries
                        .into_iter()
                        .map(|(key, value)| (key, canonical(value)))
                        .collect(),
                )
            }
            Value::Array(values) => Value::Array(values.into_iter().map(canonical).collect()),
            other => other,
        }
    }
    let mut hasher = std::collections::hash_map::DefaultHasher::new();
    canonical(serde_json::to_value(document).unwrap())
        .to_string()
        .hash(&mut hasher);
    hasher.finish()
}

unsafe fn queue_checks(handle: *mut libc::c_void, documents: &[Document]) -> Result<(), String> {
    let state = state_from_handle(handle);
    let index = runtime().block_on(state.index.read());
    let stored: Vec<_> = index
        .schema_map
        .values()
        .filter(|field| field.store)
        .map(|field| field.field.clone())
        .collect();
    let mut next = state.next_id.lock().map_err(|_| "index state poisoned")?;
    let mut pending = state.pending.lock().map_err(|_| "index state poisoned")?;
    for document in documents {
        let fingerprint = if stored.is_empty() {
            None
        } else {
            Some(document_hash(
                &document
                    .iter()
                    .filter(|(key, _)| stored.contains(key))
                    .map(|(key, value)| (key.clone(), value.clone()))
                    .collect(),
            ))
        };
        pending.push((*next, fingerprint));
        *next += 1;
    }
    Ok(())
}

fn checked_commit(state: &Handle) -> Result<(), String> {
    runtime().block_on(state.index.commit());
    let index = runtime().block_on(state.index.read());
    let expected = *state.next_id.lock().map_err(|_| "index state poisoned")?;
    let committed = runtime().block_on(index.committed_doc_count());
    if committed != expected {
        return Err(format!(
            "incomplete native commit: expected {expected} documents, committed {committed}"
        ));
    }
    let mut pending = state.pending.lock().map_err(|_| "index state poisoned")?;
    for (id, expected_hash) in pending.iter() {
        if let Some(expected_hash) = expected_hash {
            let document = runtime()
                .block_on(index.get_document(
                    *id,
                    false,
                    &None,
                    &std::collections::HashSet::new(),
                    &Vec::new(),
                ))
                .map_err(|error| format!("commit could not verify document {id}: {error}"))?;
            if document_hash(&document) != *expected_hash {
                return Err(format!(
                    "commit stored-field verification failed for document {id}"
                ));
            }
        }
    }
    pending.clear();
    Ok(())
}

// The engine ignores unknown fields and mismatched facet types. Reject them at
// the boundary so a typo cannot silently remove an intended document restriction.
fn validate_filters(index_arc: &IndexArc, req: &Value) -> Result<(), String> {
    let index = runtime().block_on(index_arc.read());
    if let Some(fields) = req.get("field_filter").and_then(Value::as_array) {
        for field in fields {
            let name = field
                .as_str()
                .ok_or("field_filter must contain field names")?;
            if !index
                .schema_map
                .get(name)
                .is_some_and(|field| field.index_lexical)
            {
                return Err(format!("unknown field_filter field: {name}"));
            }
        }
    }
    for parameter in ["facet_filter", "query_facets"] {
        if let Some(filters) = req.get(parameter).and_then(Value::as_array) {
            for filter in filters {
                let (kind, body) = filter
                    .as_object()
                    .and_then(|o| o.iter().next())
                    .ok_or("invalid facet filter")?;
                let name = body
                    .get("field")
                    .and_then(Value::as_str)
                    .ok_or("facet filter requires field")?;
                let schema = index
                    .schema_map
                    .get(name)
                    .ok_or_else(|| format!("unknown facet field: {name}"))?;
                let actual = serde_json::to_value(&schema.field_type).map_err(|e| e.to_string())?;
                if !schema.facet || actual.as_str() != Some(kind.as_str()) {
                    return Err(format!("facet type mismatch or disabled facet: {name}"));
                }
            }
        }
    }
    Ok(())
}

// 3.4.1's Union facet-filter path assumes an aggregation slot for every facet.
// Request a zero-length internal aggregation to initialize those slots without
// collecting counts. Remove its synthetic output before crossing the boundary.
const ALIGNMENT_FACET: &str = "__seekstorm_binding_alignment__";
fn align_facets(mut facets: Vec<QueryFacet>, filters: &[FacetFilter]) -> Vec<QueryFacet> {
    if facets.is_empty() && !filters.is_empty() {
        facets.push(QueryFacet::String16 {
            field: ALIGNMENT_FACET.into(),
            prefix: String::new(),
            length: 0,
        });
    }
    facets
}

fn guarded<T>(action: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(action)).unwrap_or_else(|panic| {
        let message = panic
            .downcast_ref::<String>()
            .map(String::as_str)
            .or_else(|| panic.downcast_ref::<&str>().copied())
            .unwrap_or("unknown engine panic");
        Err(format!("native engine failure: {message}"))
    })
}

fn validate_request(request: &Value) -> Result<(), String> {
    if !request.is_object() {
        return Err("request must be an object".into());
    }
    for (name, allowed) in [
        ("search_mode", &["Lexical", "Vector", "Hybrid"][..]),
        (
            "query_type",
            &["Union", "Intersection", "Phrase", "Not"][..],
        ),
        ("result_type", &["Topk", "Count", "TopkCount"][..]),
    ] {
        if let Some(value) = request.get(name).filter(|v| !v.is_null()) {
            if !value.as_str().is_some_and(|v| allowed.contains(&v)) {
                return Err(format!("invalid {name}"));
            }
        }
    }
    for name in ["offset", "length", "skip", "docid"] {
        if let Some(value) = request.get(name).filter(|v| !v.is_null()) {
            if value.as_u64().is_none() {
                return Err(format!("{name} must be a nonnegative integer"));
            }
        }
    }
    if let Some(value) = request.get("take").filter(|v| !v.is_null()) {
        if value.as_i64().is_none() {
            return Err("take must be a signed integer".into());
        }
    }
    for name in [
        "realtime",
        "enable_empty_query",
        "include_uncommitted",
        "include_deleted",
        "include_document",
    ] {
        if let Some(value) = request.get(name).filter(|v| !v.is_null()) {
            if !value.is_boolean() {
                return Err(format!("{name} must be boolean"));
            }
        }
    }
    if let Some(value) = request.get("similarity_threshold").filter(|v| !v.is_null()) {
        if !value.as_f64().is_some_and(|n| (n as f32).is_finite()) {
            return Err("similarity_threshold must be finite".into());
        }
    }
    for name in ["highlights", "distance_fields"] {
        if let Some(value) = request.get(name).filter(|v| !v.is_null()) {
            let options = value
                .as_array()
                .ok_or_else(|| format!("{name} must be an array"))?;
            if !options.is_empty() {
                return Err(format!("{name} is not implemented by this binding"));
            }
        }
    }
    if let Some(rewriting) = request.get("query_rewriting").filter(|v| !v.is_null()) {
        if !rewriting.is_object() {
            return Err("query_rewriting must be an object".into());
        }
        if let Some(mode) = rewriting.get("mode") {
            if !mode.as_str().is_some_and(|mode| {
                [
                    "SearchOnly",
                    "SearchSuggest",
                    "SearchRewrite",
                    "SuggestOnly",
                ]
                .contains(&mode)
            }) {
                return Err("invalid query_rewriting mode".into());
            }
        }
        if let Some(value) = rewriting
            .get("term_length_threshold")
            .filter(|v| !v.is_null())
        {
            if !value.as_array().is_some_and(|items| {
                !items.is_empty() && items.iter().all(|v| v.as_u64().is_some())
            }) {
                return Err(
                    "term_length_threshold must be a nonempty array of nonnegative integers".into(),
                );
            }
        }
        for name in [
            "correct_threshold",
            "complete_threshold",
            "suggestion_length",
            "distance",
        ] {
            if let Some(value) = rewriting.get(name).filter(|v| !v.is_null()) {
                if value.as_u64().is_none() {
                    return Err(format!("query_rewriting {name} must be nonnegative"));
                }
            }
        }
        if let Some(value) = rewriting.get("distance") {
            if !value.as_u64().is_some_and(|n| n <= 2) {
                return Err("query rewriting distance must be between 0 and 2".into());
            }
        }
    }
    Ok(())
}

fn validate_documents(index_arc: &IndexArc, documents: &[Document]) -> Result<(), String> {
    let index = runtime().block_on(index_arc.read());
    for document in documents {
        if let Some(name) = document
            .keys()
            .find(|name| !index.schema_map.contains_key(*name))
        {
            return Err(format!("unknown document field: {name}"));
        }
        for schema in index.schema_map.values() {
            let Some(value) = document.get(&schema.field) else {
                continue;
            };
            if value.is_null() && !matches!(schema.field_type, seekstorm::index::FieldType::Json) {
                return Err(format!(
                    "omit null typed field {} instead of coercing it",
                    schema.field
                ));
            }
            let kind = serde_json::to_value(&schema.field_type).map_err(|e| e.to_string())?;
            let valid = match kind.as_str().unwrap_or("") {
                "Text" | "String16" | "String32" | "Binary" => value.is_string(),
                "StringSet16" | "StringSet32" => value
                    .as_array()
                    .is_some_and(|values| values.iter().all(Value::is_string)),
                "Bool" => value.is_boolean(),
                "U8" => value.as_u64().is_some_and(|n| n <= u8::MAX as u64),
                "U16" => value.as_u64().is_some_and(|n| n <= u16::MAX as u64),
                "U32" => value.as_u64().is_some_and(|n| n <= u32::MAX as u64),
                "U64" => value.as_u64().is_some(),
                "I8" => value.as_i64().is_some_and(|n| i8::try_from(n).is_ok()),
                "I16" => value.as_i64().is_some_and(|n| i16::try_from(n).is_ok()),
                "I32" => value.as_i64().is_some_and(|n| i32::try_from(n).is_ok()),
                "I64" | "Timestamp" => value.as_i64().is_some(),
                "F32" => value.as_f64().is_some_and(|n| (n as f32).is_finite()),
                "F64" => value.as_f64().is_some_and(f64::is_finite),
                "Point" => value.as_array().is_some_and(|point| {
                    point.len() == 2
                        && point[0]
                            .as_f64()
                            .is_some_and(|n| (-90.0..=90.0).contains(&n))
                        && point[1]
                            .as_f64()
                            .is_some_and(|n| (-180.0..=180.0).contains(&n))
                }),
                "Json" => true,
                _ => false,
            };
            if !valid {
                return Err(format!("invalid value for field {} ({kind})", schema.field));
            }
            if let Inference::External {
                dimensions,
                similarity,
                ..
            } = &index.meta.inference
            {
                if !schema.index_vector {
                    continue;
                }
                let vector = value.as_array().ok_or_else(|| {
                    format!("external vector field {} must be an array", schema.field)
                })?;
                let numbers: Vec<f32> = vector
                    .iter()
                    .map(|v| {
                        v.as_f64()
                            .map(|n| n as f32)
                            .filter(|n| n.is_finite())
                            .ok_or("vector elements must be finite numbers")
                    })
                    .collect::<Result<_, _>>()?;
                if numbers.len() != *dimensions {
                    return Err(format!(
                        "vector dimensions do not match field {}",
                        schema.field
                    ));
                }
                if matches!(
                    similarity,
                    seekstorm::vector_similarity::VectorSimilarity::Cosine
                ) {
                    let norm: f32 = numbers.iter().map(|n| n * n).sum();
                    if !norm.is_finite() || norm <= 0.0 {
                        return Err("cosine vectors require a finite nonzero norm".into());
                    }
                }
            }
        }
    }
    Ok(())
}

#[no_mangle]
pub unsafe extern "C" fn seekstorm_validate_document(
    handle: *mut libc::c_void,
    document_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match guarded(|| {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let document: Document =
            serde_json::from_str(cstr_to_str(document_json)).map_err(|e| e.to_string())?;
        validate_documents(index_from_handle(handle), &[document])?;
        Ok(String::from(r#"{"status":"valid"}"#))
    }) {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(error) => write_error(error, out_json),
    }
}

fn validate_configuration(
    meta: &IndexMetaObject,
    schema: &[seekstorm::index::SchemaField],
) -> Result<(), String> {
    if meta.spelling_correction.is_some() {
        return Err("spelling dictionaries are unavailable with SeekStorm 3.4.1: upstream dictionary commit can panic".into());
    }
    if schema.is_empty()
        || schema.iter().any(|field| field.field.trim().is_empty())
        || schema
            .iter()
            .map(|field| &field.field)
            .collect::<std::collections::HashSet<_>>()
            .len()
            != schema.len()
    {
        return Err("schema must have unique nonempty field names".into());
    }
    if let Inference::External {
        dimensions,
        precision,
        ..
    } = &meta.inference
    {
        if *dimensions == 0 || *dimensions > 65_536 {
            return Err("external dimensions must be between 1 and 65536".into());
        }
        if !matches!(precision, seekstorm::vector::Precision::F32) {
            return Err("external vectors currently require F32 precision".into());
        }
        if schema.iter().any(|field| {
            field.index_vector && !matches!(field.field_type, seekstorm::index::FieldType::Json)
        }) {
            return Err("external vector fields currently require Json arrays".into());
        }
    }
    if !matches!(meta.inference, Inference::External { .. })
        && schema.iter().any(|field| {
            field.index_vector && matches!(field.field_type, seekstorm::index::FieldType::Json)
        })
    {
        return Err("vector-indexed Json fields require External inference; Model2Vec vector fields must be Text".into());
    }
    if matches!(meta.inference, Inference::None) && schema.iter().any(|field| field.index_vector) {
        return Err("indexed vector fields require inference configuration".into());
    }
    Ok(())
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
    match guarded(|| -> Result<String, String> {
        if out_handle.is_null() {
            return Err("out_handle must not be null".into());
        }
        *out_handle = std::ptr::null_mut();
        if segment_number_bits != 11 {
            return Err(
                "segment_number_bits must be 11: upstream reopening assumes this default".into(),
            );
        }
        let path = Path::new(cstr_to_str(index_path));
        let metadata: Value =
            json_from_cstr(meta_json).map_err(|e| format!("invalid meta_json: {e}"))?;
        let shard_count = metadata
            .get("shard_count")
            .map(|v| {
                v.as_u64()
                    .filter(|n| *n == 1)
                    .ok_or("embedded bindings currently require shard_count = 1 for verified document IDs and bounded memory")
            })
            .transpose()?
            .unwrap_or(1) as usize;
        let meta: IndexMetaObject =
            serde_json::from_value(metadata).map_err(|e| format!("invalid meta_json: {e}"))?;
        if meta.spelling_correction.is_some() {
            return Err("spelling dictionaries are unavailable with SeekStorm 3.4.1: upstream dictionary commit can panic; leave spelling_correction unset".into());
        }
        let schema: Vec<seekstorm::index::SchemaField> =
            serde_json::from_str(cstr_to_str(schema_json))
                .map_err(|e| format!("invalid schema_json: {e}"))?;

        validate_configuration(&meta, &schema)?;
        let index_arc = runtime()
            .block_on(create_index(
                path,
                meta,
                &schema,
                &Vec::new(),
                segment_number_bits,
                false,
                Some(shard_count),
            ))
            .map_err(|e| format!("create_index failed: {e}"))?;

        // Leak the Arc so the handle outlives this function
        let handle = make_handle(index_arc);
        if !out_handle.is_null() {
            unsafe {
                *out_handle = handle;
            }
        }
        Ok(r#"{"status":"created"}"#.to_string())
    }) {
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
    match guarded(|| -> Result<String, String> {
        if out_handle.is_null() {
            return Err("out_handle must not be null".into());
        }
        *out_handle = std::ptr::null_mut();
        let path = Path::new(cstr_to_str(index_path));
        // Validate the pinned engine's persisted layout before it allocates shard buffers.
        let metadata: IndexMetaObject = serde_json::from_slice(
            &std::fs::read(path.join("index.json"))
                .map_err(|error| format!("cannot read index metadata: {error}"))?,
        )
        .map_err(|error| format!("invalid index metadata: {error}"))?;
        if metadata.spelling_correction.is_some() {
            return Err("spelling dictionaries are unavailable with SeekStorm 3.4.1".into());
        }
        let schema: Vec<seekstorm::index::SchemaField> = serde_json::from_slice(
            &std::fs::read(path.join("schema.json"))
                .map_err(|error| format!("cannot read index schema: {error}"))?,
        )
        .map_err(|error| format!("invalid index schema: {error}"))?;
        validate_configuration(&metadata, &schema)?;
        let shards = std::fs::read_dir(path.join("shards"))
            .map_err(|error| error.to_string())?
            .filter_map(Result::ok)
            .filter(|entry| entry.path().is_dir())
            .filter(|entry| entry.file_name().to_string_lossy().parse::<usize>().is_ok())
            .count();
        if shards != 1 {
            return Err("embedded bindings require a persisted one-shard index".into());
        }
        let index_arc = runtime()
            .block_on(open_index(path))
            .map_err(|e| format!("open_index failed: {e}"))?;

        let unsupported = runtime().block_on(async {
            let index = index_arc.read().await;
            index.meta.spelling_correction.is_some() || index.shard_count().await != 1
        });
        if unsupported {
            runtime().block_on(index_arc.close());
            return Err("index uses unsupported spelling dictionaries or multiple shards; rebuild with spelling unset and one shard".into());
        }
        let handle = make_handle(index_arc);
        if !out_handle.is_null() {
            unsafe {
                *out_handle = handle;
            }
        }
        Ok(r#"{"status":"opened"}"#.to_string())
    }) {
        Ok(result) => {
            write_output(result, out_json);
            std::ptr::null_mut()
        }
        Err(err) => write_error(err, out_json),
    }
}

/// Close must break the engine's root/shard Arc cycle, not merely drop the handle.
unsafe fn close_handle(handle: *mut libc::c_void) -> Result<(), String> {
    if handle.is_null() {
        return Ok(());
    }
    let state = Box::from_raw(handle as *mut Handle);
    let verified = guarded(|| checked_commit(&state));
    let closed = guarded(|| {
        runtime().block_on(state.index.close());
        Ok(())
    });
    verified.and(closed)
}

#[no_mangle]
pub unsafe extern "C" fn seekstorm_close_index(handle: *mut libc::c_void) {
    if let Err(error) = close_handle(handle) {
        eprintln!("{error}");
    }
}

/// Explicit close reports failure; the handle is consumed even on failure.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_close_index_checked(
    handle: *mut libc::c_void,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match close_handle(handle) {
        Ok(()) => {
            write_output(r#"{"status":"closed"}"#.into(), out_json);
            std::ptr::null_mut()
        }
        Err(error) => write_error(error, out_json),
    }
}

/// Index a single document. `document_json` is a JSON object with field→value mappings.
#[no_mangle]
pub unsafe extern "C" fn seekstorm_index_document(
    handle: *mut libc::c_void,
    document_json: *const c_char,
    out_json: *mut *mut c_char,
) -> *mut c_char {
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        let doc: Document = serde_json::from_str(cstr_to_str(document_json))
            .map_err(|e| format!("invalid document_json: {e}"))?;

        validate_documents(index_arc, std::slice::from_ref(&doc))?;
        queue_checks(handle, std::slice::from_ref(&doc))?;
        runtime().block_on(index_arc.index_document(doc, seekstorm::index::FileType::None));
        Ok(r#"{"status":"queued"}"#.to_string())
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        let docs: Vec<Document> = serde_json::from_str(cstr_to_str(documents_json))
            .map_err(|e| format!("invalid documents_json: {e}"))?;

        validate_documents(index_arc, &docs)?;
        queue_checks(handle, &docs)?;
        runtime().block_on(index_arc.index_documents(docs));
        Ok(r#"{"status":"queued"}"#.to_string())
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        checked_commit(state_from_handle(handle))?;
        let ids: Vec<u64> = serde_json::from_str(cstr_to_str(doc_ids_json))
            .map_err(|e| format!("invalid doc_ids_json: {e}"))?;

        state_from_handle(handle)
            .pending
            .lock()
            .map_err(|_| "index state poisoned")?
            .retain(|(id, _)| !ids.contains(&(*id as u64)));
        runtime().block_on(index_arc.delete_documents(ids));
        Ok(r#"{"status":"deleted"}"#.to_string())
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        let req: Value =
            json_from_cstr(request_json).map_err(|e| format!("invalid request_json: {e}"))?;
        validate_request(&req)?;

        let query = req["query"].as_str().unwrap_or("").to_string();
        let query_type = parse_query_type(req["query_type"].as_str());
        let offset = req["offset"].as_u64().unwrap_or(0) as usize;
        let length = req["length"].as_u64().unwrap_or(10) as usize;
        let include_uncommitted = req["include_uncommitted"].as_bool().unwrap_or(false);

        let field_filter = serde_json::from_value(optional_array(&req, "field_filter"))
            .map_err(|e| format!("invalid field_filter: {e}"))?;
        let facet_filter: Vec<FacetFilter> =
            serde_json::from_value(optional_array(&req, "facet_filter"))
                .map_err(|e| format!("invalid facet_filter: {e}"))?;
        let result_sort = serde_json::from_value(optional_array(&req, "result_sort"))
            .map_err(|e| format!("invalid result_sort: {e}"))?;
        validate_filters(index_arc, &req)?;
        if include_uncommitted {
            checked_commit(state_from_handle(handle))?;
        }
        if length == 0 {
            return Ok(r#"{"status":"deleted"}"#.into());
        }
        let query_vector = match &runtime().block_on(index_arc.read()).meta.inference {
            Inference::External { dimensions, .. } if *dimensions > 0 => {
                let mut unit = vec![0.0; *dimensions];
                unit[0] = 1.0;
                Some(Embedding::F32(unit))
            }
            _ => None,
        };
        let matches = runtime().block_on(index_arc.search(
            query,
            query_vector,
            query_type,
            SearchMode::Lexical,
            false,
            offset,
            length,
            ResultType::Topk,
            include_uncommitted,
            field_filter,
            align_facets(Vec::new(), &facet_filter),
            facet_filter,
            result_sort,
            QueryRewriting::SearchOnly,
        ));
        runtime().block_on(
            index_arc.delete_documents(
                matches
                    .results
                    .into_iter()
                    .map(|hit| hit.doc_id as u64)
                    .collect(),
            ),
        );
        Ok(r#"{"status":"deleted"}"#.to_string())
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        checked_commit(state_from_handle(handle))?;
        Ok(r#"{"status":"committed"}"#.to_string())
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        let req: Value =
            json_from_cstr(request_json).map_err(|e| format!("invalid request_json: {e}"))?;
        validate_request(&req)?;

        let query = req["query"].as_str().unwrap_or("").to_string();
        let search_mode = parse_search_mode(&req);
        let vector_enabled = runtime()
            .block_on(index_arc.read())
            .schema_map
            .values()
            .any(|field| field.index_vector);
        if !matches!(search_mode, SearchMode::Lexical) && !vector_enabled {
            return Err("vector search requires an indexed vector field".into());
        }
        let dimensions = {
            let index = runtime().block_on(index_arc.read());
            (index.vector_dimensions_original > 0).then_some(index.vector_dimensions_original)
        };
        let query_vector = match req.get("query_vector") {
            None | Some(Value::Null) => None,
            Some(value) => {
                let array = value.as_array().ok_or("query_vector must be an array")?;
                let numbers: Vec<f32> = array
                    .iter()
                    .map(|value| {
                        let number = value
                            .as_f64()
                            .ok_or("query_vector elements must be numbers")?
                            as f32;
                        if !number.is_finite() {
                            return Err("query_vector elements must be finite");
                        }
                        Ok(number)
                    })
                    .collect::<Result<_, _>>()?;
                if numbers.is_empty() || dimensions.is_some_and(|n| numbers.len() != n) {
                    return Err("query_vector dimensions do not match the index".into());
                }
                if matches!(
                    runtime().block_on(index_arc.read()).vector_similarity,
                    seekstorm::vector_similarity::VectorSimilarity::Cosine
                ) {
                    let norm: f32 = numbers.iter().map(|n| n * n).sum();
                    if !norm.is_finite() || norm <= 0.0 {
                        return Err("cosine vectors require a finite nonzero norm".into());
                    }
                }
                Some(Embedding::F32(numbers))
            }
        };
        // 3.4.1 unnecessarily requires a vector even for lexical-only searches
        // on external-vector indexes. A unit vector is unused by lexical ranking.
        let query_vector = if query_vector.is_none() && matches!(search_mode, SearchMode::Lexical) {
            dimensions.map(|n| {
                let mut v = vec![0.0; n];
                v[0] = 1.0;
                Embedding::F32(v)
            })
        } else {
            query_vector
        };
        if matches!(
            runtime().block_on(index_arc.read()).meta.inference,
            Inference::External { .. }
        ) && !matches!(search_mode, SearchMode::Lexical)
            && query_vector.is_none()
        {
            return Err("external vector search requires query_vector".into());
        }
        let enable_empty_query = req["enable_empty_query"].as_bool().unwrap_or(false);
        let offset = req["offset"].as_u64().unwrap_or(0) as usize;
        let length = req["length"].as_u64().unwrap_or(10) as usize;
        let query_type = parse_query_type(req["query_type"].as_str());
        let corpus_size =
            runtime().block_on(async { index_arc.read().await.indexed_doc_count().await });
        let offset = offset.min(corpus_size);
        let length = length.min(corpus_size.saturating_sub(offset));
        let result_type = if length == 0 {
            ResultType::Count
        } else {
            parse_result_type(req["result_type"].as_str())
        };
        let include_uncommitted = req["realtime"].as_bool().unwrap_or(false);

        // Invalid filters must fail, never silently broaden the query.
        let field_filter: Vec<String> =
            serde_json::from_value(optional_array(&req, "field_filter"))
                .map_err(|e| format!("invalid field_filter: {e}"))?;
        let query_facets: Vec<QueryFacet> =
            serde_json::from_value(optional_array(&req, "query_facets"))
                .map_err(|e| format!("invalid query_facets: {e}"))?;
        let facet_filter: Vec<FacetFilter> =
            serde_json::from_value(optional_array(&req, "facet_filter"))
                .map_err(|e| format!("invalid facet_filter: {e}"))?;
        let result_sort: Vec<ResultSort> =
            serde_json::from_value(optional_array(&req, "result_sort"))
                .map_err(|e| format!("invalid result_sort: {e}"))?;
        let query_rewriting = parse_query_rewriting(&req);
        validate_filters(index_arc, &req)?;

        // SeekStorm 3.4.1 filters lexical hits, but its vector branch ignores facets.
        // Intersect the complete ranked candidate set before pagination, never a top-k slice.
        let filtered_vectors =
            !facet_filter.is_empty() && !matches!(search_mode, SearchMode::Lexical);
        let candidate_limit = if filtered_vectors {
            let count =
                runtime().block_on(async { index_arc.read().await.indexed_doc_count().await });
            if count > 100_000 {
                return Err(
                    "filtered vector search currently supports at most 100000 indexed documents"
                        .into(),
                );
            }
            count.max(1)
        } else {
            length
        };
        if !matches!(search_mode, SearchMode::Lexical) && !query_facets.is_empty() {
            return Err("facet aggregation with vector or hybrid search is not supported".into());
        }
        let allowed = if filtered_vectors {
            let eligible = runtime().block_on(index_arc.search(
                String::new(),
                query_vector.clone(),
                QueryType::Union,
                SearchMode::Lexical,
                true,
                0,
                candidate_limit,
                ResultType::TopkCount,
                include_uncommitted,
                Vec::new(),
                align_facets(Vec::new(), &facet_filter),
                facet_filter.clone(),
                Vec::new(),
                QueryRewriting::SearchOnly,
            ));
            Some(
                eligible
                    .results
                    .into_iter()
                    .map(|hit| hit.doc_id)
                    .collect::<std::collections::HashSet<_>>(),
            )
        } else {
            None
        };
        let mut result = runtime().block_on(index_arc.search(
            query,
            query_vector,
            query_type,
            search_mode,
            enable_empty_query,
            if filtered_vectors { 0 } else { offset },
            candidate_limit,
            if filtered_vectors {
                ResultType::TopkCount
            } else {
                result_type.clone()
            },
            include_uncommitted,
            field_filter,
            align_facets(query_facets, &facet_filter),
            facet_filter,
            result_sort,
            query_rewriting,
        ));

        result.facets.remove(ALIGNMENT_FACET);
        if let Some(allowed) = allowed {
            result.results.retain(|hit| allowed.contains(&hit.doc_id));
            result.result_count_total = result.results.len();
            result.results = result
                .results
                .into_iter()
                .skip(offset)
                .take(length)
                .collect();
            if matches!(result_type, ResultType::Count) {
                result.results.clear();
            }
            result.result_count = result.results.len();
        }
        let result_json =
            serde_json::to_string(&result).map_err(|e| format!("serialization failed: {e}"))?;
        Ok(result_json)
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        let index = runtime().block_on(index_arc.read());

        let doc = runtime()
            .block_on(index.get_document(
                doc_id,
                true,
                &None,
                &std::collections::HashSet::new(),
                &Vec::new(),
            ))
            .map_err(|e| format!("get_document failed: {e}"))?;

        serde_json::to_string(&doc).map_err(|e| format!("serialization failed: {e}"))
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        let updates: Vec<(u64, Document)> = serde_json::from_str(cstr_to_str(updates_json))
            .map_err(|e| format!("invalid updates_json (expected [[id, doc], ...]): {e}"))?;

        let documents: Vec<_> = updates.iter().map(|(_, doc)| doc.clone()).collect();
        validate_documents(index_arc, &documents)?;
        checked_commit(state_from_handle(handle))?;
        let mut unique = std::collections::HashSet::new();
        for (id, _) in &updates {
            if !unique.insert(*id) {
                return Err("update IDs must be unique".into());
            }
            let existing = runtime().block_on(index_arc.get_iterator(
                Some(*id),
                0,
                1,
                false,
                false,
                Vec::new(),
            ));
            if existing.results.first().map(|hit| hit.doc_id) != Some(*id) {
                return Err(format!("cannot update absent or deleted document {id}"));
            }
        }
        state_from_handle(handle)
            .pending
            .lock()
            .map_err(|_| "index state poisoned")?
            .retain(|(id, _)| !updates.iter().any(|(removed, _)| *removed == *id as u64));
        queue_checks(handle, &documents)?;
        runtime().block_on(index_arc.update_documents(updates));
        Ok(r#"{"status":"updated"}"#.to_string())
    }) {
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
    match guarded(|| -> Result<String, String> {
        let _operation = state_from_handle(handle)
            .operation
            .lock()
            .map_err(|_| "index operation poisoned")?;
        let index_arc = index_from_handle(handle);
        let req: Value =
            json_from_cstr(request_json).map_err(|e| format!("invalid request_json: {e}"))?;
        validate_request(&req)?;

        let docid = req["docid"].as_u64();
        let skip = req["skip"].as_u64().unwrap_or(0) as usize;
        let take = req["take"].as_i64().unwrap_or(100) as isize;
        let include_deleted = req["include_deleted"].as_bool().unwrap_or(false);
        let include_document = req["include_document"].as_bool().unwrap_or(true);
        let fields: Vec<String> = serde_json::from_value(optional_array(&req, "fields"))
            .map_err(|e| format!("invalid iterator fields: {e}"))?;
        if take == isize::MIN {
            return Err("iterator take cannot be the minimum signed integer".into());
        }

        let result = runtime().block_on(index_arc.get_iterator(
            docid,
            skip,
            take,
            include_deleted,
            include_document,
            fields,
        ));

        serde_json::to_string(&result).map_err(|e| format!("serialization failed: {e}"))
    }) {
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
    let thresholds = qr
        .get("term_length_threshold")
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .map(|v| v.as_u64().unwrap() as usize)
                .collect::<Vec<_>>()
        });
    match mode {
        "SearchSuggest" => QueryRewriting::SearchSuggest {
            correct: qr["correct_threshold"].as_u64().map(|v| v as usize),
            distance: qr["distance"].as_u64().unwrap_or(2) as usize,
            term_length_threshold: thresholds,
            complete: qr["complete_threshold"].as_u64().map(|v| v as usize),
            length: qr["suggestion_length"].as_u64().map(|v| v as usize),
        },
        "SearchRewrite" => QueryRewriting::SearchRewrite {
            correct: qr["correct_threshold"].as_u64().map(|v| v as usize),
            distance: qr["distance"].as_u64().unwrap_or(2) as usize,
            term_length_threshold: thresholds,
            complete: qr["complete_threshold"].as_u64().map(|v| v as usize),
            length: qr["suggestion_length"].as_u64().map(|v| v as usize),
        },
        "SuggestOnly" => QueryRewriting::SuggestOnly {
            correct: qr["correct_threshold"].as_u64().map(|v| v as usize),
            distance: qr["distance"].as_u64().unwrap_or(2) as usize,
            term_length_threshold: thresholds,
            complete: qr["complete_threshold"].as_u64().map(|v| v as usize),
            length: qr["suggestion_length"].as_u64().map(|v| v as usize),
        },
        _ => QueryRewriting::SearchOnly,
    }
}

// ── Tests ─────────────────────────────────────────────────────────────

fn optional_array(request: &Value, field: &str) -> Value {
    request
        .get(field)
        .filter(|value| !value.is_null())
        .cloned()
        .unwrap_or_else(|| Value::Array(Vec::new()))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn create_rejects_missing_output_before_native_allocation() {
        unsafe {
            let error = seekstorm_create_index(
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                11,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            );
            assert!(!error.is_null());
            assert!(CStr::from_ptr(error)
                .to_str()
                .unwrap()
                .contains("out_handle"));
            seekstorm_free_string(error);
        }
    }

    #[test]
    fn create_rejects_oversized_segment_shift_before_reading_inputs() {
        unsafe {
            let mut handle = std::ptr::null_mut();
            let error = seekstorm_create_index(
                std::ptr::null(),
                std::ptr::null(),
                std::ptr::null(),
                usize::MAX,
                &mut handle,
                std::ptr::null_mut(),
            );
            assert!(!error.is_null());
            assert!(handle.is_null());
            assert!(CStr::from_ptr(error)
                .to_str()
                .unwrap()
                .contains("segment_number_bits"));
            seekstorm_free_string(error);
        }
    }

    #[test]
    fn engine_panics_become_errors() {
        let result: Result<(), String> = guarded(|| panic!("synthetic engine failure"));
        assert!(result.unwrap_err().contains("synthetic engine failure"));
    }
}
