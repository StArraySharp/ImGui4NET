using System;

namespace ImGui4NET;

/// <summary>
/// Mirror of Dear ImGui's <c>ImVector&lt;T&gt;</c>.
/// </summary>
/// <remarks>
/// ClangSharp cannot emit C++ class templates, so this type is written by hand
/// and must stay layout-compatible with the native definition:
/// <code>
/// template&lt;typename T&gt; struct ImVector { int Size; int Capacity; T* Data; };
/// </code>
/// The field order here follows the native declaration exactly.
/// </remarks>
public unsafe struct ImVector<T>
    where T : unmanaged
{
    public int Size;
    public int Capacity;
    public T* Data;

    public readonly int Length => Size;

    public readonly bool IsEmpty => Size == 0;

    public readonly ref T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Size)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return ref Data[index];
        }
    }

    /// <summary>Returns the elements as a span over the native buffer.</summary>
    public readonly Span<T> AsSpan() => Data is null ? Span<T>.Empty : new Span<T>(Data, Size);
}

