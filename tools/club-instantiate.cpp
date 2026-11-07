// Explicit instantiation unit for the imgui_club extensions.
//
// The club headers define every method inline, so the compiler normally emits
// nothing: an inline function is only materialised in a translation unit that
// actually calls it. Building imgui_club as a shared library therefore needs a
// unit that forces the code to exist.
//
// -fkeep-inline-functions makes GCC emit the inline definitions, and this file
// provides the non-template entry points the C# bindings call. Together they
// give the package a stable ABI surface instead of relying on whichever symbols
// a caller happened to reference.

#include "imgui.h"
#include "imgui_memory_editor.h"
#include "imgui_threaded_rendering.h"
#include "imgui_multicontext_compositor.h"

#include <new>

// Sink that the instantiation helpers write into. A volatile global prevents the
// optimiser from proving the objects are unused and removing the constructor
// calls, which is exactly what happened on Android with plain locals.
static void* volatile s_force_sink = nullptr;

// ---------------------------------------------------------------------------
// MemoryEditor
// ---------------------------------------------------------------------------
//
// The plain constructor is inline; instantiating it here gives the binding a
// symbol to call. The remaining methods are already member functions whose
// addresses this unit takes, which is enough to force emission.

namespace
{
    // Storage large enough for every club type. Placement-constructing into a byte
        // buffer and then reading the pointer back stops the optimiser from treating
        // the object as unused and eliding both it and the constructor call.
        template <typename T>
        T* ForceCtor()
        {
            static unsigned char storage[sizeof(T)];
            T* obj = ::new (static_cast<void*>(storage)) T();
            s_force_sink = obj;
            return obj;
        }

    // Taking the address of each inline member forces the compiler to emit it.
        // A static object is used (rather than a local) because clang can drop a
        // local whose address never escapes, together with every call on it.
        void ForceMemoryEditor()
        {
            static MemoryEditor ed;
            ed.GotoAddrAndHighlight(0, 0);

            static MemoryEditor::Sizes sizes{};
            ed.CalcSizes(sizes, 0, 0);

            ed.DrawContents(nullptr, 0, 0);
            ed.DrawOptionsLine(sizes, nullptr, 0, 0);
            ed.DrawPreviewLine(sizes, nullptr, 0, 0);

            (void)ed.DataTypeGetDesc(ImGuiDataType_U8);
            (void)ed.DataTypeGetSize(ImGuiDataType_U8);
            (void)ed.DataFormatGetDesc(MemoryEditor::DataFormat_Bin);
            (void)ed.IsBigEndian();
            (void)ed.FormatBinary(nullptr, 0);

            static char buffer[16];
            ed.DrawPreviewData(0, nullptr, 0, ImGuiDataType_U8, MemoryEditor::DataFormat_Bin, buffer, 16);
        }

        void ForceThreadedRendering()
        {
            static ImTextureQueue queue;
            queue.PreNewFrame();
            queue.QueueRequests(nullptr);
            queue.ProcessRequests(nullptr);
            queue.Shutdown();

            static ImDrawDataSnapshot snapshot;
            snapshot.SnapUsingSwap(nullptr, 0.0);
            snapshot.Clear();
        }

        void ForceMultiContextCompositor()
        {
            static ImGuiMultiContextCompositor mcc{};
            ImGuiMultiContextCompositor_AddContext(&mcc, nullptr);
            ImGuiMultiContextCompositor_RemoveContext(&mcc, nullptr);
            ImGuiMultiContextCompositor_PreNewFrameUpdateAll(&mcc);
            ImGuiMultiContextCompositor_PostNewFrameUpdateOne(&mcc, nullptr);
            ImGuiMultiContextCompositor_PostEndFrameUpdateAll(&mcc);
            ImGuiMultiContextCompositor_ShowDebugWindow(&mcc);
        }
}

// A single exported anchor keeps the anonymous-namespace helpers alive: without
// an external reference the linker would drop the whole section.
extern "C" void ImGuiClub_ForceInstantiation()
{
    // Non-template, non-inlined construction is the only reliable way to make
    // clang emit the constructor bodies: it sees a real call at this exact site.
    static MemoryEditor memory_editor;
    static ImTextureQueue texture_queue;
    static ImDrawDataSnapshot snapshot;
    static ImGuiMultiContextCompositor compositor;

    s_force_sink = &memory_editor;
    s_force_sink = &texture_queue;
    s_force_sink = &snapshot;
    s_force_sink = &compositor;

    // ForceCtor<> keeps the template instantiations reachable as well.
    ForceCtor<MemoryEditor>();
    ForceCtor<ImTextureQueue>();
    ForceCtor<ImDrawDataSnapshot>();
    ForceCtor<ImGuiMultiContextCompositor>();

    ForceMemoryEditor();
    ForceThreadedRendering();
    ForceMultiContextCompositor();

    // Read the sink back so the value cannot be treated as write-only.
    if (s_force_sink == reinterpret_cast<void*>(1))
    {
        s_force_sink = nullptr;
    }
}