using System.Runtime.InteropServices;

namespace DesktopPet.Models.Live2D;

internal unsafe sealed class AlignedBuffer : IDisposable
{
    public void* Pointer { get; }

    private AlignedBuffer(void* pointer)
    {
        Pointer = pointer;
    }

    public static AlignedBuffer Allocate(uint size, nuint alignment)
    {
        var pointer = NativeMemory.AlignedAlloc(size, alignment);
        if (pointer is null)
        {
            throw new OutOfMemoryException();
        }

        NativeMemory.Clear(pointer, size);
        return new AlignedBuffer(pointer);
    }

    public static AlignedBuffer CopyFrom(ReadOnlySpan<byte> source, nuint alignment)
    {
        var buffer = Allocate((uint)source.Length, alignment);
        source.CopyTo(new Span<byte>(buffer.Pointer, source.Length));
        return buffer;
    }

    public void Dispose()
    {
        NativeMemory.AlignedFree(Pointer);
    }
}
