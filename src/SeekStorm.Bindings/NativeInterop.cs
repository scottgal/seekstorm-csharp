using System.Runtime.InteropServices;

namespace SeekStorm.Bindings;

/// <summary>
/// SafeHandle for a native SeekStorm index. Ensures the native handle
/// is freed even if the managed object is finalized.
/// </summary>
public sealed class IndexHandle : SafeHandle
{
    public IndexHandle(IntPtr handle) : base(IntPtr.Zero, ownsHandle: true)
    {
        SetHandle(handle);
    }

    public override bool IsInvalid => handle == IntPtr.Zero;

    protected override bool ReleaseHandle()
    {
        NativeMethods.seekstorm_close_index(handle);
        return true;
    }
}

/// <summary>
/// P/Invoke declarations for the seekstorm-ffi cdylib. All interop is
/// isolated in this class — nowhere else in the SDK touches DllImport.
/// </summary>
internal static unsafe class NativeMethods
{
    // Native library name — runtime resolves per-platform:
    //   Windows: seekstorm_ffi.dll
    //   Linux:   libseekstorm_ffi.so
    //   macOS:   libseekstorm_ffi.dylib
    private const string LibName = "seekstorm_ffi";

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_create_index(
        byte* indexPath,
        byte* metaJson,
        byte* schemaJson,
        nuint segmentNumberBits,
        IntPtr* outHandle,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_open_index(
        byte* indexPath,
        IntPtr* outHandle,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void seekstorm_close_index(IntPtr handle);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_index_document(
        IntPtr handle,
        byte* documentJson,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_index_documents(
        IntPtr handle,
        byte* documentsJson,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_delete_documents(
        IntPtr handle,
        byte* docIdsJson,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_delete_documents_by_query(
        IntPtr handle,
        byte* requestJson,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_commit(
        IntPtr handle,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_search(
        IntPtr handle,
        byte* requestJson,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_get_document(
        IntPtr handle,
        nuint docId,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_update_documents(
        IntPtr handle,
        byte* updatesJson,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte* seekstorm_iterate_documents(
        IntPtr handle,
        byte* requestJson,
        byte** outJson);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void seekstorm_free_string(byte* ptr);
}

/// <summary>
/// Exception thrown when a native seekstorm-ffi call returns an error.
/// </summary>
public sealed class SeekStormException : Exception
{
    public SeekStormException(string message) : base(message) { }
}
