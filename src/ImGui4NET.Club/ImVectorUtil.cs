using System;
using ImGui4NET;

namespace ImGui4NET.Club;

/// <summary>
/// Managed equivalents of the <c>ImVector</c> operations the imgui_club
/// extensions rely on.
/// </summary>
/// <remarks>
/// <see cref="ImVector{T}"/> is only a layout mirror: it exposes the three
/// native fields and nothing else, because every mutating operation in the C++
/// original allocates through <c>IM_ALLOC</c>/<c>IM_FREE</c>. The club ports are
/// plain C# now, so those operations are reproduced here against
/// <see cref="ImGui.MemAlloc"/> and <see cref="ImGui.MemFree"/>: the buffers must
/// stay allocated by Dear ImGui's allocator, otherwise a pointer handed back to
/// native code (for example <c>ImDrawList::CmdBuffer</c>) would be freed by the
/// wrong allocator.
/// </remarks>
internal static unsafe class ImVectorUtil
{
    /// <summary>Mirror of <c>ImVector&lt;T&gt;::_grow_capacity</c>.</summary>
    public static int GrowCapacity<T>(in ImVector<T> vector, int size)
        where T : unmanaged
    {
        int newCapacity = vector.Capacity != 0
            ? vector.Capacity + vector.Capacity / 2
            : 8;
        return newCapacity > size ? newCapacity : size;
    }

    /// <summary>Mirror of <c>ImVector&lt;T&gt;::reserve</c>.</summary>
    public static void Reserve<T>(ref ImVector<T> vector, int newCapacity)
        where T : unmanaged
    {
        if (newCapacity <= vector.Capacity)
        {
            return;
        }

        T* newData = (T*)ImGui.MemAlloc((nuint)newCapacity * (nuint)sizeof(T));
        if (vector.Data != null)
        {
            if (vector.Size > 0)
            {
                Buffer.MemoryCopy(vector.Data, newData,
                    (long)newCapacity * sizeof(T), (long)vector.Size * sizeof(T));
            }

            ImGui.MemFree(vector.Data);
        }

        vector.Data = newData;
        vector.Capacity = newCapacity;
    }

    /// <summary>Mirror of <c>ImVector&lt;T&gt;::resize</c> without value filling.</summary>
    public static void Resize<T>(ref ImVector<T> vector, int newSize)
        where T : unmanaged
    {
        if (newSize > vector.Capacity)
        {
            Reserve(ref vector, GrowCapacity(vector, newSize));
        }

        vector.Size = newSize;
    }

    /// <summary>Mirror of <c>ImVector&lt;T&gt;::push_back</c>.</summary>
    public static void PushBack<T>(ref ImVector<T> vector, T value)
        where T : unmanaged
    {
        if (vector.Size == vector.Capacity)
        {
            Reserve(ref vector, GrowCapacity(vector, vector.Size + 1));
        }

        vector.Data[vector.Size++] = value;
    }

    /// <summary>Mirror of <c>ImVector&lt;T&gt;::swap</c>: swaps the three fields.</summary>
    public static void Swap<T>(ref ImVector<T> left, ref ImVector<T> right)
        where T : unmanaged
    {
        (left, right) = (right, left);
    }

    /// <summary>Mirror of <c>ImVector&lt;T&gt;::clear</c>.</summary>
    public static void Clear<T>(ref ImVector<T> vector)
        where T : unmanaged
    {
        if (vector.Data == null)
        {
            return;
        }

        vector.Size = 0;
        vector.Capacity = 0;
        ImGui.MemFree(vector.Data);
        vector.Data = null;
    }

    /// <summary>Mirror of <c>ImVector&lt;T&gt;::operator=</c>: deep copy.</summary>
    public static void CopyFrom<T>(ref ImVector<T> destination, in ImVector<T> source)
        where T : unmanaged
    {
        Clear(ref destination);
        Resize(ref destination, source.Size);
        if (destination.Data != null && source.Data != null && source.Size > 0)
        {
            Buffer.MemoryCopy(source.Data, destination.Data,
                (long)destination.Capacity * sizeof(T), (long)source.Size * sizeof(T));
        }
    }
}