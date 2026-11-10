using System;
using System.Collections.Generic;
using ImGui4NET;

namespace ImGui4NET.Club;

/// <summary>
/// Managed port of <c>imgui_club</c>'s <c>ImTextureQueue</c>: a staged texture
/// proxy that lets an update thread and a render thread run at different rates.
/// </summary>
/// <remarks>
/// This is a straight port of <c>imgui_threaded_rendering.h</c> v0.21. The queue
/// acts as a proxy backend on the update side: it acknowledges
/// create/update/destroy requests immediately so Dear ImGui never blocks, and
/// keeps a per-texture staging mirror that the render thread replays against the
/// real backend.
/// <para>
/// Call ordering, copied from the header:
/// </para>
/// <code>
/// // update thread
/// queue.PreNewFrame();
/// ImGui.NewFrame();
/// ...
/// ImGui.Render();
/// queue.QueueRequests(drawData);
/// snapshot.SnapUsingSwap(drawData, ImGui.GetTime());
///
/// // render thread
/// queue.ProcessRequests(&amp;snapshot.DrawData);
/// backend.RenderDrawData(&amp;snapshot.DrawData);
///
/// // shutdown
/// queue.Shutdown();
/// </code>
/// <para>
/// Every call must be protected by the same lock; the queue does not own one,
/// matching the C++ original.
/// </para>
/// <para>
/// <see cref="UpdateTexFunc"/> is a native function pointer, normally a renderer
/// backend's texture update entry point (for example
/// <c>ImGui_ImplDX11_UpdateTexture</c>).
/// </para>
/// </remarks>
public sealed unsafe class ImTextureQueue
{
    /// <summary>
    /// Persistent per-texture state plus staging mirror. Kept in native memory
    /// because <see cref="ImTextureData.QueueUserData"/> stores its address and
    /// the render thread dereferences it.
    /// </summary>
    private struct Entry
    {
        /// <summary>Live texture owned by Dear ImGui.</summary>
        public ImTextureData* TexLive;

        /// <summary>Staging mirror replayed against the backend.</summary>
        public ImTextureData TexCopy;

        /// <summary>Draw-data frame count of the frame that queued the destroy; -1 = none.</summary>
        public int DestroyFrameCount;

        /// <summary><see cref="RenderFrame"/> value when the retirement countdown started.</summary>
        public int DestroyArmedAtRenderFrame;
    }

    /// <summary>Number of extra in-flight frames.</summary>
    public int InFlightFrames = 2;

    /// <summary>
    /// Backend texture updater, for example a renderer backend's
    /// <c>UpdateTexture</c> function. Required by <see cref="ProcessRequests"/>.
    /// </summary>
    public delegate* unmanaged[Cdecl]<ImTextureData*, void> UpdateTexFunc;

    /// <summary>Number of <see cref="ProcessRequests"/> calls, roughly the presents.</summary>
    public int RenderFrame;

    private readonly List<nint> _entries = [];

    /// <summary><c>ImTextureID_Invalid</c>, which is 0 by default.</summary>
    private const ulong TextureIdInvalid = 0;

    /// <summary>
    /// Update-side: acknowledges destroys the render thread has retired so the
    /// core library can free those textures during the following
    /// <c>NewFrame</c>. Call before <c>ImGui.NewFrame()</c>.
    /// </summary>
    public void PreNewFrame()
    {
        for (int index = 0; index < _entries.Count; index++)
        {
            Entry* entry = (Entry*)_entries[index];
            if (entry->TexCopy.Status != ImTextureStatus.ImTextureStatus_Destroyed)
            {
                continue;
            }

            ImTextureData* texLive = entry->TexLive;

            // UserTextures[] may set WantDestroy directly, so force it through.
            texLive->WantDestroyNextFrame = 1;
            SetStatus(texLive, ImTextureStatus.ImTextureStatus_Destroyed);
            texLive->TexID = TextureIdInvalid;
            texLive->QueueUserData = null;

            FreeEntry(entry);
            _entries.RemoveAt(index);
            index--;
        }
    }

    /// <summary>
    /// Update-side: merges this frame's texture requests into the staging
    /// mirrors and acknowledges them immediately. Call after
    /// <c>ImGui.Render()</c> and before publishing the snapshot.
    /// </summary>
    public void QueueRequests(ImDrawData* drawData)
    {
        if (drawData->Textures == null)
        {
            return;
        }

        int frameCount = drawData->FrameCount;
        ImVector<IntPtr> textures = *drawData->Textures;

        for (int index = 0; index < textures.Size; index++)
        {
            ImTextureData* texLive = (ImTextureData*)textures.Data[index];
            if (texLive->Status == ImTextureStatus.ImTextureStatus_OK)
            {
                continue;
            }

            Entry* entry = (Entry*)texLive->QueueUserData;

            if (texLive->Status == ImTextureStatus.ImTextureStatus_WantCreate)
            {
                Entry* created = NewEntry(texLive);
                // Mark the texture as referenced by this queue, which stops the
                // core from deleting it while BackendUserData is unset.
                texLive->QueueUserData = created;
                SetStatus(texLive, ImTextureStatus.ImTextureStatus_OK);
            }
            else if (texLive->Status == ImTextureStatus.ImTextureStatus_WantUpdates)
            {
                if (entry == null)
                {
                    continue;
                }

                ImTextureData* texCopy = &entry->TexCopy;
                if (texCopy->Updates.Size == 0)
                {
                    texCopy->UpdateRect.x = texCopy->UpdateRect.y = 0xFFFF;
                    texCopy->UpdateRect.w = texCopy->UpdateRect.h = 0;
                }

                Span<ImTextureRect> updates = texLive->Updates.AsSpan();
                for (int updateIndex = 0; updateIndex < updates.Length; updateIndex++)
                {
                    ImTextureRect rect = updates[updateIndex];
                    ImGui.ImTextureDataQueueUpload(texCopy, rect.x, rect.y, rect.w, rect.h);
                }

                texCopy->UseColors = texLive->UseColors;
                SetStatus(texLive, ImTextureStatus.ImTextureStatus_OK);
            }
            else if (texLive->Status == ImTextureStatus.ImTextureStatus_WantDestroy)
            {
                if (entry == null)
                {
                    // Never seen in WantCreate state, so no snapshot can
                    // reference it and the core may delete it right away.
                    SetStatus(texLive, ImTextureStatus.ImTextureStatus_Destroyed);
                    continue;
                }

                if (entry->DestroyFrameCount >= 0)
                {
                    // Already tracked: either waiting for the render thread, or
                    // retired and waiting for PreNewFrame to acknowledge.
                    continue;
                }

                // Stamp the record but do not acknowledge: a pending
                // create/update is processed first, and the destroy executes
                // once the mirror is idle.
                entry->DestroyFrameCount = frameCount;
            }
        }
    }

    /// <summary>
    /// Render-side: replays staged create/update/destroy requests through
    /// <see cref="UpdateTexFunc"/>. Call once per render frame, after adopting
    /// the latest snapshot and before rendering any viewport.
    /// </summary>
    /// <param name="drawData">Adopted snapshot, or <c>null</c> to drain pending
    /// work during shutdown.</param>
    public void ProcessRequests(ImDrawData* drawData)
    {
        if (UpdateTexFunc == null)
        {
            throw new InvalidOperationException(
                $"{nameof(ImTextureQueue)}.{nameof(UpdateTexFunc)} must be set before processing requests.");
        }

        // null means a shutdown drain: nothing will be rendered any more.
        int adoptedFrameCount = drawData != null ? drawData->FrameCount : int.MaxValue;
        if (drawData != null)
        {
            // The queue handles textures, so the backend must not. The pointer
            // also aliases the update thread's live list and must not be
            // dereferenced on this thread.
            drawData->Textures = null;
        }

        RenderFrame++;

        foreach (nint handle in _entries)
        {
            Entry* entry = (Entry*)handle;
            ImTextureData* texCopy = &entry->TexCopy;

            ImTextureStatus statusRequest = texCopy->Status;
            if (statusRequest is ImTextureStatus.ImTextureStatus_WantCreate
                or ImTextureStatus.ImTextureStatus_WantUpdates)
            {
                UpdateTexFunc(texCopy);
                texCopy->Updates.Size = 0;

                if (statusRequest == ImTextureStatus.ImTextureStatus_WantCreate)
                {
                    // The only render-thread write to a live texture. Every
                    // ImDrawCmd::GetTexID read happens on this thread after it.
                    entry->TexLive->TexID = texCopy->TexID;
                }
            }

            // Execute a pending destroy once the mirror is idle, on the
            // consumer-side retirement clock.
            if (texCopy->Status != ImTextureStatus.ImTextureStatus_OK || entry->DestroyFrameCount < 0)
            {
                continue;
            }

            if (adoptedFrameCount < entry->DestroyFrameCount)
            {
                // A snapshot that may still reference this texture is, or may
                // become, active.
                continue;
            }

            if (entry->DestroyArmedAtRenderFrame < 0)
            {
                entry->DestroyArmedAtRenderFrame = RenderFrame;
            }

            if (RenderFrame < entry->DestroyArmedAtRenderFrame + InFlightFrames)
            {
                // Wait for the in-flight GPU frames to retire.
                continue;
            }

            texCopy->Status = ImTextureStatus.ImTextureStatus_WantDestroy;
            // Make the SetStatus(Destroyed) call in the backend stick.
            texCopy->WantDestroyNextFrame = 1;
            // Deferring is the queue's job: pass any backend-side UnusedFrames check.
            texCopy->UnusedFrames = 99;
            UpdateTexFunc(texCopy);
        }
    }

    /// <summary>
    /// Drains pending work and releases every per-texture record. Call after the
    /// render thread has been joined.
    /// </summary>
    public void Shutdown()
    {
        InFlightFrames = 0;

        if (UpdateTexFunc != null)
        {
            ProcessRequests(null);
        }

        PreNewFrame();

        foreach (nint handle in _entries)
        {
            Entry* entry = (Entry*)handle;
            ImTextureData* texLive = entry->TexLive;

            // Hand the backend data back so the renderer backend's regular
            // single-threaded shutdown path can destroy the textures.
            texLive->BackendUserData = entry->TexCopy.BackendUserData;
            texLive->QueueUserData = null;

                FreeEntry(entry);
            }

            _entries.Clear();
        }

    private Entry* NewEntry(ImTextureData* texLive)
    {
        Entry* entry = (Entry*)ImGui.MemAlloc((nuint)sizeof(Entry));
        *entry = default;
        entry->TexLive = texLive;
        entry->DestroyFrameCount = -1;
        entry->DestroyArmedAtRenderFrame = -1;

        // Copy the main struct. Pixels keep pointing at the live allocation,
        // which never moves during a texture's lifetime.
        entry->TexCopy = *texLive;

        _entries.Add((nint)entry);
        return entry;
    }

    private static void FreeEntry(Entry* entry)
    {
        // Pixels belong to the live texture; the mirror must not free them.
        entry->TexCopy.Pixels = null;
        ImVectorUtil.Clear(ref entry->TexCopy.Updates);
        ImGui.MemFree(entry);
    }

    /// <summary>
    /// Mirror of <c>ImTextureData::SetStatus</c>: a destroyed texture that still
    /// has pixels and was not queued for destruction returns to WantCreate.
    /// </summary>
    private static void SetStatus(ImTextureData* tex, ImTextureStatus status)
    {
        tex->Status = status;
        if (status == ImTextureStatus.ImTextureStatus_Destroyed
            && tex->WantDestroyNextFrame == 0
            && tex->Pixels != null)
        {
            tex->Status = ImTextureStatus.ImTextureStatus_WantCreate;
        }
    }
}