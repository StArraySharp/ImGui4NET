using System.Runtime.InteropServices;

namespace ImGui4NET.Club;

/// <summary>
/// Managed port of <c>imgui_club</c>'s Multi-Context Compositor.
/// </summary>
/// <remarks>
/// When several Dear ImGui contexts are displayed at the same time, this class
/// arbitrates between them: it decides which context receives the mouse, which
/// one owns the keyboard, which one may change the cursor shape, and it
/// replicates drag-and-drop payloads across contexts.
/// <para>
/// The C++ original is a struct plus free functions that read
/// <see cref="ImGuiContext"/> internals. This port keeps the same structure and
/// the same call order:
/// </para>
/// <code>
/// mcc.PreNewFrameUpdateAll();
/// ImGui.SetCurrentContext(ctx1);
/// ImGui.NewFrame();
/// mcc.PostNewFrameUpdateOne(ctx1);
/// ...
/// mcc.PostEndFrameUpdateAll();
/// </code>
/// <para>
/// Contexts are stored as raw pointers because <see cref="ImGuiContext"/> is a
/// native struct owned by Dear ImGui. Ordering is pure bookkeeping and never
/// crosses the ABI boundary, so a managed list is used instead of an
/// <c>ImVector</c>.
/// </para>
/// </remarks>
public sealed unsafe class MultiContextCompositor
{
    private const int FlagNoMouse = (int)ImGuiConfigFlags.ImGuiConfigFlags_NoMouse;
    private const int FlagNoKeyboard = (int)ImGuiConfigFlags.ImGuiConfigFlags_NoKeyboard;
    private const int FlagNoCursorChange = (int)ImGuiConfigFlags.ImGuiConfigFlags_NoMouseCursorChange;

    private const int ViewportCanHostOtherWindows =
        (int)ImGuiViewportFlags.ImGuiViewportFlags_CanHostOtherWindows;
    private const int ViewportIsFocused = (int)ImGuiViewportFlags.ImGuiViewportFlags_IsFocused;

    private const int DragSourceExtern = (int)ImGuiDragDropFlags.ImGuiDragDropFlags_SourceExtern;
    private const int DragPayloadNoCrossContext =
        (int)ImGuiDragDropFlags.ImGuiDragDropFlags_PayloadNoCrossContext;

    private ImGuiPayload _dragDropPayload;

    /// <summary>Registered contexts, in registration order.</summary>
    public List<nint> Contexts { get; } = [];

    /// <summary>Registered contexts, sorted front (index 0) to back.</summary>
    public List<nint> ContextsFrontToBack { get; } = [];

    /// <summary>First context, front to back, whose <c>io.WantCaptureMouse</c> is set.</summary>
    public nint CtxMouseFirst { get; private set; }

    /// <summary>Context owning the hovered secondary viewport, if any.</summary>
    public nint CtxMouseExclusive { get; private set; }

    /// <summary>Context that requested a non-default cursor shape.</summary>
    public nint CtxMouseShape { get; private set; }

    /// <summary>Context that owns the keyboard this frame.</summary>
    public nint CtxKeyboardExclusive { get; private set; }

    /// <summary>Context that started the active drag and drop, if any.</summary>
    public nint CtxDragDropSrc { get; private set; }

    /// <summary>Context that receives the replicated drag and drop payload.</summary>
    public nint CtxDragDropDst { get; private set; }

    /// <summary>Deep copy of the active drag and drop payload.</summary>
    public ImGuiPayload DragDropPayload => _dragDropPayload;

    private bool _payloadAllocated;

    // -----------------------------------------------------------------------
    // Registration
    // -----------------------------------------------------------------------

    /// <summary>Registers a context with the compositor.</summary>
    public void AddContext(ImGuiContext* context)
    {
        if (context == null || Contexts.Contains((nint)context))
        {
            return;
        }

        Contexts.Add((nint)context);
        ContextsFrontToBack.Add((nint)context);
    }

    /// <summary>Unregisters a context.</summary>
    public void RemoveContext(ImGuiContext* context)
    {
        nint key = (nint)context;
        Contexts.RemoveAll(c => c == key);
        ContextsFrontToBack.RemoveAll(c => c == key);

        if (CtxDragDropSrc == key)
        {
            CtxDragDropSrc = 0;
        }

        if (CtxDragDropDst == key)
        {
            CtxDragDropDst = 0;
        }
    }

    // -----------------------------------------------------------------------
    // Frame hooks
    // -----------------------------------------------------------------------

    /// <summary>
    /// Call at a shared sync point before <c>NewFrame</c> runs on any context.
    /// </summary>
    public void PreNewFrameUpdateAll()
    {
        FreePayload();

        CtxMouseFirst = 0;
        CtxMouseExclusive = 0;
        CtxMouseShape = 0;
        CtxKeyboardExclusive = 0;
        CtxDragDropSrc = 0;
        CtxDragDropDst = 0;

        // PASS 1: decide ownership for this frame.
        foreach (nint raw in ContextsFrontToBack)
        {
            ImGuiContext* ctx = (ImGuiContext*)raw;
            if (ctx == null)
            {
                continue;
            }

            TrackSecondaryViewportOwners(raw, ctx);

            if (CtxMouseFirst == 0 && ctx->IO.WantCaptureMouse != 0)
            {
                CtxMouseFirst = raw;
            }

            // Remember the second context as a potential drop target.
            if (CtxDragDropDst == 0 && ctx->HoveredWindowBeforeClear != null)
            {
                CtxDragDropDst = raw;
            }

            if (CtxMouseShape == 0 && ctx->MouseCursor != (int)ImGuiMouseCursor.ImGuiMouseCursor_Arrow)
            {
                CtxMouseShape = raw;
            }

            bool dragActive = ctx->DragDropActive != 0;
            bool externalSource = (ctx->DragDropSourceFlags & DragSourceExtern) != 0;

            if (CtxDragDropSrc == 0 && dragActive && !externalSource)
            {
                CtxDragDropSrc = raw;
            }
        }

        // Without a focused secondary viewport the top-most context keeps the keyboard.
        if (CtxKeyboardExclusive == 0 && ContextsFrontToBack.Count > 0)
        {
            CtxKeyboardExclusive = ContextsFrontToBack[0];
        }

        if (CtxDragDropSrc != 0)
        {
            CopyPayloadFromSource((ImGuiContext*)CtxDragDropSrc);
        }

        // No payload means there is nothing to drop anywhere.
        if (CtxDragDropDst != 0 && !_payloadAllocated)
        {
            CtxDragDropDst = 0;
        }

        // PASS 2: apply the decisions.
        bool aboveClaimingContext = true;

        foreach (nint raw in ContextsFrontToBack)
        {
            ImGuiContext* ctx = (ImGuiContext*)raw;
            if (ctx == null)
            {
                continue;
            }

            ApplyKeyboardFlags(raw, ctx);
            ApplyCursorFlags(raw, ctx);
            aboveClaimingContext = ApplyMouseFlags(raw, ctx, aboveClaimingContext);
        }

        if (CtxDragDropSrc != 0 && CtxDragDropDst != 0)
        {
            PushPayloadToDestination();
        }
    }

    /// <summary>
    /// Call right after <c>NewFrame</c> on <paramref name="context"/>.
    /// </summary>
    public void PostNewFrameUpdateOne(ImGuiContext* context)
    {
        if (context == null)
        {
            return;
        }

        // Clicking inside a context brings it to the front.
        if (context->NavWindow != null && AnyMouseClicked())
        {
            BringContextToFront((nint)context, CtxKeyboardExclusive);
        }
    }

    /// <summary>
    /// Call at a shared sync point after <c>EndFrame</c> on every context.
    /// </summary>
    public void PostEndFrameUpdateAll() => FreePayload();

    // -----------------------------------------------------------------------
    // Routing helpers
    // -----------------------------------------------------------------------

    private void TrackSecondaryViewportOwners(nint raw, ImGuiContext* ctx)
    {
        // A hovered secondary viewport takes exclusive mouse ownership. The
        // backend-submitted MouseHoveredViewport is used, not the last valid one.
        if (CtxMouseExclusive == 0 && ctx->IO.MouseHoveredViewport != 0)
        {
            ImGuiViewport* hovered = FindViewport(ctx, ctx->IO.MouseHoveredViewport);
            if (hovered != null && (hovered->Flags & ViewportCanHostOtherWindows) == 0)
            {
                CtxMouseExclusive = raw;
            }
        }

        // A focused secondary viewport takes exclusive keyboard ownership.
        if (CtxKeyboardExclusive == 0 && ctx->NavWindow != null)
        {
            ImGuiViewportP* viewportP = ctx->NavWindow->Viewport;
            ImGuiViewport* viewport = (ImGuiViewport*)viewportP;
            if (viewport != null
                && (viewport->Flags & ViewportIsFocused) != 0
                && (viewport->Flags & ViewportCanHostOtherWindows) == 0)
            {
                CtxKeyboardExclusive = raw;
            }
        }
    }

    private static ImGuiViewport* FindViewport(ImGuiContext* ctx, uint id)
    {
        ImVector<IntPtr> viewports = ctx->PlatformIO.Viewports;

        for (int i = 0; i < viewports.Size; i++)
        {
            ImGuiViewport* viewport = (ImGuiViewport*)viewports.Data[i];
            if (viewport != null && viewport->ID == id)
            {
                return viewport;
            }
        }

        return null;
    }

    private void ApplyKeyboardFlags(nint raw, ImGuiContext* ctx)
    {
        ImGuiIO* io = &ctx->IO;
        if (CtxKeyboardExclusive == raw)
        {
            io->ConfigFlags &= ~FlagNoKeyboard;
        }
        else
        {
            io->ConfigFlags |= FlagNoKeyboard;
        }
    }

    private void ApplyCursorFlags(nint raw, ImGuiContext* ctx)
    {
        ImGuiIO* io = &ctx->IO;
        if (CtxMouseShape == 0 || CtxMouseShape == raw)
        {
            io->ConfigFlags &= ~FlagNoCursorChange;
        }
        else
        {
            io->ConfigFlags |= FlagNoCursorChange;
        }
    }

    /// <summary>
    /// Applies the mouse flags and returns whether the next context is still
    /// above the one that claimed the mouse.
    /// </summary>
    private bool ApplyMouseFlags(nint raw, ImGuiContext* ctx, bool aboveClaimingContext)
    {
        ImGuiIO* io = &ctx->IO;
        bool allow;

        if (CtxMouseExclusive != 0)
        {
            // A secondary viewport is hovered: only its owner gets the mouse.
            allow = CtxMouseExclusive == raw;
        }
        else
        {
            // On a shared viewport everything above the claiming context also
            // gets the mouse, so overlapping windows behave as one context.
            allow = aboveClaimingContext || CtxDragDropDst == raw;
        }

        if (allow)
        {
            io->ConfigFlags &= ~FlagNoMouse;
        }
        else
        {
            io->ConfigFlags |= FlagNoMouse;
        }

        return aboveClaimingContext && raw != CtxMouseFirst;
    }

    private void BringContextToFront(nint contextKey, nint keepInputsFor)
    {
        ContextsFrontToBack.RemoveAll(c => c == contextKey);
        ContextsFrontToBack.Insert(0, contextKey);

        foreach (nint otherKey in ContextsFrontToBack)
        {
            if (otherKey == 0 || otherKey == contextKey || otherKey == keepInputsFor)
            {
                continue;
            }

            ImGuiContext* otherCtx = (ImGuiContext*)otherKey;
            ImGuiIO* otherIo = &otherCtx->IO;
            ImGuiIO.ClearInputKeys(otherIo);
        }
    }

    // -----------------------------------------------------------------------
    // Drag and drop replication
    // -----------------------------------------------------------------------

    private void CopyPayloadFromSource(ImGuiContext* source)
    {
        if (source->DragDropActive == 0)
        {
            return;
        }

        // A payload flagged PayloadNoCrossContext must not leave its context.
        if ((source->DragDropSourceFlags & DragPayloadNoCrossContext) != 0)
        {
            return;
        }

        ImGuiPayload* src = &source->DragDropPayload;
        if (src->Data == null || src->DataSize <= 0)
        {
            return;
        }

        void* copy = ImGui.MemAlloc((nuint)src->DataSize);
        if (copy == null)
        {
            return;
        }

        Buffer.MemoryCopy(src->Data, copy, src->DataSize, src->DataSize);

        _dragDropPayload = *src;
        _dragDropPayload.Data = copy;
        _payloadAllocated = true;
    }

    private void PushPayloadToDestination()
    {
        if (_dragDropPayload.Data == null)
        {
            return;
        }

        int flags = DragSourceExtern
            | (int)ImGuiDragDropFlags.ImGuiDragDropFlags_SourceNoPreviewTooltip;

        if (ImGui.BeginDragDropSource(flags) != 0)
        {
                    string dataType;
                    fixed (sbyte* dataTypePtr = &_dragDropPayload.DataType.e0)
                    {
                        dataType = Marshal.PtrToStringUTF8((nint)dataTypePtr) ?? string.Empty;
                    }

                    ImGui.SetDragDropPayload(dataType, _dragDropPayload.Data, (nuint)_dragDropPayload.DataSize);
                    ImGui.EndDragDropSource();
                }
    }

    private void FreePayload()
    {
        if (!_payloadAllocated)
        {
            _dragDropPayload = default;
            return;
        }

        if (_dragDropPayload.Data != null)
        {
            ImGui.MemFree(_dragDropPayload.Data);
        }

        _dragDropPayload = default;
        _payloadAllocated = false;
    }

    // -----------------------------------------------------------------------
    // Debug overlay
    // -----------------------------------------------------------------------

    /// <summary>Draws the upstream debug overlay in the current context.</summary>
    public void ShowDebugWindow()
    {
        ImGuiViewport* mainViewport = ImGui.GetMainViewport();
        if (mainViewport != null)
        {
            ImVec2 pos = mainViewport->Pos;
            ImVec2 pivot = new() { x = 0f, y = 0f };
            ImGui.SetNextWindowPos(&pos, 0, &pivot);
        }

        ImVec4 windowBg = new() { x = 1f, y = 1f, z = 1f, w = 0.5f };
        ImVec4 textCol = new() { x = 0f, y = 0f, z = 0f, w = 1f };

        ImGui.PushStyleColor((int)ImGuiCol.ImGuiCol_WindowBg, &windowBg);
        ImGui.PushStyleColor((int)ImGuiCol.ImGuiCol_Text, &textCol);

        int flags = (int)ImGuiWindowFlags.ImGuiWindowFlags_NoDecoration
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_AlwaysAutoResize
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoFocusOnAppearing
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoInputs;

        ImGui.Begin("Multi-Context Compositor Overlay", null, flags);
        ImGui.SeparatorText("Multi-Context Compositor");
        ImGui.TextUnformatted($"Front: {NameOf(FrontContext())}");
        ImGui.TextUnformatted($"MousePos first: {NameOf(CtxMouseFirst)}");
        ImGui.TextUnformatted($"MousePos excl.: {NameOf(CtxMouseExclusive)}");
        ImGui.TextUnformatted($"Keyboard excl.: {NameOf(CtxKeyboardExclusive)}");
        ImGui.TextUnformatted($"DragDrop src: {NameOf(CtxDragDropSrc)}");
        ImGui.TextUnformatted($"DragDrop dst: {NameOf(CtxDragDropDst)}");
        ImGui.End();

        ImGui.PopStyleColor(2);
    }

    private nint FrontContext()
        => ContextsFrontToBack.Count > 0 ? ContextsFrontToBack[0] : 0;

    private static string NameOf(nint ctxRaw)
        {
            if (ctxRaw == 0)
            {
                return string.Empty;
            }

            ImGuiContext* ctx = (ImGuiContext*)ctxRaw;

            // ContextName lives inside the native context, so taking its address
            // needs no `fixed`: the whole struct is unmanaged.
            sbyte* name = &ctx->ContextName.e0;
            return Marshal.PtrToStringUTF8((nint)name) ?? string.Empty;
        }

        /// <summary>
        /// True when any mouse button was pressed this frame. The bindings do not
        /// expose <c>IsAnyMouseClicked</c>, so the three buttons are queried.
        /// </summary>
        private static bool AnyMouseClicked()
            => ImGui.IsMouseClicked((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0
            || ImGui.IsMouseClicked((int)ImGuiMouseButton.ImGuiMouseButton_Right) != 0
            || ImGui.IsMouseClicked((int)ImGuiMouseButton.ImGuiMouseButton_Middle) != 0;
    }