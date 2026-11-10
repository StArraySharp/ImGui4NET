using System.Collections.Generic;
using ImGui4NET;

namespace ImGui4NET.Club;

/// <summary>
/// Managed port of <c>imgui_club</c>'s <c>ImDrawDataSnapshot</c>: a cheap
/// snapshot of the draw data produced by one context, for staged or
/// multi-threaded rendering.
/// </summary>
/// <remarks>
/// The C++ original swaps the vertex/index/command buffers of each
/// <see cref="ImDrawList"/> with a per-list copy it owns, so publishing a
/// snapshot costs no copies and the main context can keep rendering into its
/// own buffers. This port keeps exactly that algorithm:
/// <list type="bullet">
/// <item>one <see cref="Entry"/> per source draw list, keyed by the pointer;</item>
/// <item>buffers are swapped, never copied;</item>
/// <item>entries unused for <see cref="MemoryCompactTimer"/> seconds are
/// released during the next snapshot.</item>
/// </list>
/// <para>
/// The copies are allocated with <see cref="ImGui.MemAlloc"/> because their
/// buffers are handed back to Dear ImGui as <c>ImDrawList</c> pointers and are
/// freed by its allocator on swap-out.
/// </para>
/// </remarks>
public sealed unsafe class ImDrawDataSnapshot
{
    private sealed class Entry
    {
        public ImDrawList* SrcCopy;
        public ImDrawList* OurCopy;
        public double LastUsedTime;
    }

    /// <summary>
    /// Snapshot of the last submitted frame. Its <c>CmdLists</c> point at draw
    /// lists owned by this snapshot.
    /// </summary>
    public ImDrawData DrawData;

    /// <summary>Discard unused data after this many seconds.</summary>
    public float MemoryCompactTimer = 20.0f;

    private readonly Dictionary<uint, Entry> _cache = [];

    // Scratch buffer reused by CompactUnused so a steady-state snapshot does not
    // allocate a fresh key array every frame.
    private uint[] _compactKeys = [];

    /// <summary>
    /// Releases every copied draw list and clears the snapshot.
    /// </summary>
    /// <remarks>
    /// The C++ destructor calls this, and the documented usage calls it again
    /// before <c>ImGui.DestroyContext</c> so the context does not assert on
    /// textures that are still referenced.
    /// </remarks>
    public void Clear()
    {
        foreach (Entry entry in _cache.Values)
        {
            FreeDrawList(entry.OurCopy);
        }

        _cache.Clear();

        fixed (ImDrawData* drawData = &DrawData)
        {
            ImDrawData.Clear(drawData);
        }
    }

    /// <summary>
    /// Publishes <paramref name="src"/> into this snapshot.
    /// </summary>
    /// <param name="src">Draw data to snapshot. It is left unusable: its command
    /// lists are swapped out and immediately swapped back, so the caller must
    /// re-render into it.</param>
    /// <param name="currentTime">Current time in seconds, used for retention.</param>
    public void SnapUsingSwap(ImDrawData* src, double currentTime)
    {
        fixed (ImDrawData* dst = &DrawData)
        {
                    // CmdLists is ImVector<ImDrawList*>, which ClangSharp spells as
                    // ImVector<IntPtr> because a pointer is not a legal type argument.
                    ImVector<IntPtr> backup = default;

            ImVectorUtil.Swap(ref backup, ref src->CmdLists);
                    *dst = *src;
                    ImVectorUtil.Swap(ref backup, ref src->CmdLists);

                    int listCount = src->CmdLists.Size;
                    for (int index = 0; index < listCount; index++)
                    {
                        ImDrawList* srcList = (ImDrawList*)src->CmdLists.Data[index];
                        Entry entry = GetOrAddEntry(srcList);

                if (entry.OurCopy == null)
                {
                    entry.SrcCopy = srcList;
                    entry.OurCopy = NewDrawList(srcList->_Data);
                }

                ImVectorUtil.Swap(ref entry.SrcCopy->CmdBuffer, ref entry.OurCopy->CmdBuffer);
                ImVectorUtil.Swap(ref entry.SrcCopy->IdxBuffer, ref entry.OurCopy->IdxBuffer);
                ImVectorUtil.Swap(ref entry.SrcCopy->VtxBuffer, ref entry.OurCopy->VtxBuffer);
                ImVectorUtil.Swap(ref entry.SrcCopy->_CallbacksDataBuf, ref entry.OurCopy->_CallbacksDataBuf);

                // Preserve the larger capacity so two consecutive frames do not
                // reallocate.
                ImVectorUtil.Reserve(ref entry.SrcCopy->CmdBuffer, entry.OurCopy->CmdBuffer.Capacity);
                ImVectorUtil.Reserve(ref entry.SrcCopy->IdxBuffer, entry.OurCopy->IdxBuffer.Capacity);
                ImVectorUtil.Reserve(ref entry.SrcCopy->VtxBuffer, entry.OurCopy->VtxBuffer.Capacity);
                ImVectorUtil.Reserve(ref entry.SrcCopy->_CallbacksDataBuf, entry.OurCopy->_CallbacksDataBuf.Capacity);

                entry.LastUsedTime = currentTime;
                                ImVectorUtil.PushBack(ref dst->CmdLists, (nint)entry.OurCopy);
            }
        }

        CompactUnused(currentTime);
    }

    private void CompactUnused(double currentTime)
    {
        double threshold = currentTime - MemoryCompactTimer;

            int count = _cache.Count;
            if (count == 0)
            {
                return;
            }

            uint[] keys = _compactKeys;
            if (keys.Length < count)
            {
                keys = new uint[count * 2];
                _compactKeys = keys;
            }

            _cache.Keys.CopyTo(keys, 0);

            for (int index = 0; index < count; index++)
            {
                uint key = keys[index];
                Entry entry = _cache[key];
                if (entry.LastUsedTime > threshold)
                {
                    continue;
                }

                FreeDrawList(entry.OurCopy);
                _cache.Remove(key);
            }
        }

    /// <summary>Mirror of <c>ImDrawDataSnapshot::GetDrawListID</c>: hash of the pointer.</summary>
    private static uint GetDrawListID(ImDrawList* srcList)
    {
        ImDrawList** handle = &srcList;
        return ImGui.ImHashData(handle, (nuint)sizeof(ImDrawList*));
    }

    private Entry GetOrAddEntry(ImDrawList* srcList)
    {
        uint key = GetDrawListID(srcList);
        if (_cache.TryGetValue(key, out Entry? existing))
        {
            return existing;
        }

        Entry entry = new();
        _cache[key] = entry;
        return entry;
    }

    private static ImDrawList* NewDrawList(ImDrawListSharedData* sharedData)
    {
        ImDrawList* list = (ImDrawList*)ImGui.MemAlloc((nuint)sizeof(ImDrawList));
        ImDrawList.ImDrawList_Ctor(list, sharedData);
        return list;
    }

    private static void FreeDrawList(ImDrawList* list)
    {
        if (list == null)
        {
            return;
        }

        ImDrawList.Dispose(list);
        ImGui.MemFree(list);
    }
}