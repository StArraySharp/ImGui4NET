using System.Runtime.InteropServices;
using ImGui4NET;

namespace ImGui4NET.Club;

/// <summary>
/// Entry points for the imgui_club extensions whose methods are defined inline
/// in their headers.
/// </summary>
/// <remarks>
/// ClangSharp only emits a <c>DllImport</c> for a function whose definition it
/// can see in a traversed header. Every method of <c>MemoryEditor</c>,
/// <c>ImDrawDataSnapshot</c> and <c>ImTextureQueue</c> is declared and defined
/// inside the class body, so the generator produces the struct layout but no
/// methods. The declarations below are written by hand; the symbol names in
/// their <c>EntryPoint</c> attributes were taken from the built library.
/// <para>
/// The native side is compiled with <c>-fkeep-inline-functions</c> plus
/// <c>tools/club-instantiate.cpp</c>, which forces every inline definition to be
/// emitted so these entry points actually exist.
/// </para>
/// <para>
/// <c>ImGuiMultiContextCompositor</c> is plain C functions, so its bindings are
/// generated automatically into <c>Generated/methods.cs</c> and are not repeated
/// here.
/// </para>
/// </remarks>
public static unsafe partial class ClubNative
{
    private const string Library = "imgui_club";

    // -----------------------------------------------------------------------
    // MemoryEditor
    // -----------------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN12MemoryEditorC2Ev", ExactSpelling = true)]
    public static extern void MemoryEditor_Ctor(MemoryEditor* pThis);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN12MemoryEditor10DrawWindowEPKcPvyy", ExactSpelling = true)]
    public static extern void MemoryEditor_DrawWindow(
        MemoryEditor* pThis,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        void* mem_data,
        nuint mem_size,
        nuint base_display_addr);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN12MemoryEditor12DrawContentsEPvyy", ExactSpelling = true)]
    public static extern void MemoryEditor_DrawContents(
        MemoryEditor* pThis,
        void* mem_data_void,
        nuint mem_size,
        nuint base_display_addr);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN12MemoryEditor20GotoAddrAndHighlightEyy", ExactSpelling = true)]
    public static extern void MemoryEditor_GotoAddrAndHighlight(
        MemoryEditor* pThis,
        nuint addr_min,
        nuint addr_max);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN12MemoryEditor9CalcSizesERNS_5SizesEyy", ExactSpelling = true)]
    public static extern void MemoryEditor_CalcSizes(
        MemoryEditor* pThis,
            MemoryEditor.Sizes* s,
        nuint mem_size,
        nuint base_display_addr);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZNK12MemoryEditor15DataTypeGetDescEi", ExactSpelling = true)]
    [return: NativeTypeName("const char *")]
    public static extern sbyte* MemoryEditor_DataTypeGetDesc(MemoryEditor* pThis, int data_type);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZNK12MemoryEditor15DataTypeGetSizeEi", ExactSpelling = true)]
    public static extern nuint MemoryEditor_DataTypeGetSize(MemoryEditor* pThis, int data_type);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZNK12MemoryEditor17DataFormatGetDescENS_10DataFormatE", ExactSpelling = true)]
    [return: NativeTypeName("const char *")]
        public static extern sbyte* MemoryEditor_DataFormatGetDesc(MemoryEditor* pThis, MemoryEditor.DataFormat data_format);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZNK12MemoryEditor11IsBigEndianEv", ExactSpelling = true)]
    [return: NativeTypeName("bool")]
    public static extern byte MemoryEditor_IsBigEndian(MemoryEditor* pThis);

    // -----------------------------------------------------------------------
    // ImDrawDataSnapshot
    // -----------------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN18ImDrawDataSnapshotC2Ev", ExactSpelling = true)]
    public static extern void ImDrawDataSnapshot_Ctor(ImDrawDataSnapshot* pThis);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN18ImDrawDataSnapshotD2Ev", ExactSpelling = true)]
    public static extern void ImDrawDataSnapshot_Dtor(ImDrawDataSnapshot* pThis);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN18ImDrawDataSnapshot13SnapUsingSwapEP10ImDrawDatad", ExactSpelling = true)]
    public static extern void ImDrawDataSnapshot_SnapUsingSwap(
        ImDrawDataSnapshot* pThis,
        ImDrawData* draw_data,
        double current_time);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN18ImDrawDataSnapshot5ClearEv", ExactSpelling = true)]
    public static extern void ImDrawDataSnapshot_Clear(ImDrawDataSnapshot* pThis);

    // -----------------------------------------------------------------------
    // ImTextureQueue
    // -----------------------------------------------------------------------

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN14ImTextureQueueC2Ev", ExactSpelling = true)]
    public static extern void ImTextureQueue_Ctor(ImTextureQueue* pThis);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN14ImTextureQueue11PreNewFrameEv", ExactSpelling = true)]
    public static extern void ImTextureQueue_PreNewFrame(ImTextureQueue* pThis);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN14ImTextureQueue13QueueRequestsEP10ImDrawData", ExactSpelling = true)]
    public static extern void ImTextureQueue_QueueRequests(ImTextureQueue* pThis, ImDrawData* draw_data);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN14ImTextureQueue15ProcessRequestsEP10ImDrawData", ExactSpelling = true)]
    public static extern void ImTextureQueue_ProcessRequests(ImTextureQueue* pThis, ImDrawData* draw_data);

    [DllImport(Library, CallingConvention = CallingConvention.ThisCall, EntryPoint = "_ZN14ImTextureQueue8ShutdownEv", ExactSpelling = true)]
    public static extern void ImTextureQueue_Shutdown(ImTextureQueue* pThis);
}
