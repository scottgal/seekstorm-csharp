using System.Buffers;
using System.Text;

namespace SeekStorm.Bindings;

/// <summary>Null-terminated native input without document-sized stack allocations.</summary>
internal sealed class Utf8Buffer : IDisposable
{
    public byte[] Bytes { get; }

    public Utf8Buffer(string text)
    {
        var length = Encoding.UTF8.GetByteCount(text);
        Bytes = ArrayPool<byte>.Shared.Rent(checked(length + 1));
        Encoding.UTF8.GetBytes(text, Bytes);
        Bytes[length] = 0;
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(Bytes, clearArray: true);
}
