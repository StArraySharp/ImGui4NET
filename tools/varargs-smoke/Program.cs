using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ImGui4NET;

unsafe class Program
{
    static void Equal(string expected, string? actual)
    {
        if (expected != actual) throw new Exception($"Expected [{expected}], actual [{actual}]");
    }

    static void TestSingleValueParameters()
    {
        ImGui.GetAllocatorFunctions(out delegate* unmanaged[Cdecl]<nuint, void*, void*> allocate,
            out delegate* unmanaged[Cdecl]<void*, void*, void> free, out void* userData);
        void* allocation = allocate(32, userData);
        if (allocation == null) throw new Exception("Allocator output callback failed");
        free(allocation, userData);
        ImGuiContext* context = ImGui.CreateContext();
        try
        {
            ImVec2 position = new() { x = 5, y = 10 };
            if (ImGui.IsMousePosValid(in position) == 0) throw new Exception("Read-only position failed");
            ImGui.StyleColorsDark();
            ImGui.StyleColorsDark(ref *ImGui.GetStyle());
            ImGuiIO* io = ImGui.GetIO();
            io->IniFilename = null;
            ImVec2 displaySize = new() { x = 640, y = 480 };
            io->DisplaySize = displaySize;
            io->DeltaTime = 1f / 60f;
            ImFontAtlas.GetTexDataAsRGBA32(io->Fonts, out byte* pixels, out int width, out int height, out int bpp);
            if (pixels == null || width <= 0 || height <= 0 || bpp != 4) throw new Exception("RGBA outputs failed");
            ImFontAtlas.GetTexDataAsAlpha8(io->Fonts, out pixels, out width, out height);
            if (pixels == null || width <= 0 || height <= 0) throw new Exception("Alpha outputs failed");
            bool enabled = true;
            ImVec2 mouse = new() { x = -100, y = -100 };
            ImVec2 origin = default;
            for (int frame = 0; frame < 5; frame++)
            {
                ImGuiIO.AddMousePosEvent(io, mouse.x, mouse.y);
                ImGuiIO.AddMouseButtonEvent(io, 0, frame == 2 ? (byte)1 : (byte)0);
                ImGui.NewFrame();
                ImGui.SetNextWindowPos(in origin, 0, in origin);
                ImGui.SetNextWindowSize(in displaySize, 0);
                bool open = true;
                ImGui.Begin("Interop smoke", ref open);
                if (context->CurrentWindow->HasCloseButton == 0) throw new Exception("ref open must enable the close button");
                ImGui.Checkbox("Enabled", ref enabled);
                ImVec2 min = ImGui.GetItemRectMin();
                ImVec2 max = ImGui.GetItemRectMax();
                mouse = new ImVec2 { x = (min.x + max.x) / 2, y = (min.y + max.y) / 2 };
                int flags = 0;
                ImGui.CheckboxFlags("Flag", ref flags, 1);
                int value = 1;
                float scalar = 0.5f;
                double number = 1;
                ImGui.DragFloat("Float", ref scalar);
                ImGui.SliderInt("Integer", ref value, 0, 10);
                ImGui.InputDouble("Double", ref number);
                bool selected = true;
                ImGui.Selectable("Selected", ref selected);
                ImGui.Text("Literal: 50% {braces}");
                ImVec4 color = new() { x = 1, y = 1, z = 1, w = 1 };
                ImGui.TextColored(in color, "Formatted: {0:F2}", 1.25);
                ImDrawList* drawList = ImGui.GetWindowDrawList();
                ImVec2 textPosition = ImGui.GetCursorScreenPos();
                ImVec4 clip = drawList->_CmdHeader.ClipRect;
                ImDrawList.AddText(drawList, null, 0, in textPosition, 0xffffffff, "Clipped", null, 0, in clip);
                ImFont.RenderChar(ImGui.GetFont(), drawList, ImGui.GetFontSize(), in textPosition, 0xffffffff, 'A', in clip);
                ImDrawList.AddText(drawList, null, 0, in textPosition, 0xffffffff, "Default clip");
                ImFont.RenderChar(ImGui.GetFont(), drawList, ImGui.GetFontSize(), in textPosition, 0xffffffff, 'B');
                ImGui.End();
                ImGui.Begin("Default window", flags: (int)ImGuiWindowFlags.ImGuiWindowFlags_NoInputs);
                if (context->CurrentWindow->HasCloseButton != 0) throw new Exception("Default Begin must omit the close button");
                ImGui.End();
                ImGui.Render();
                if (!open || !selected) throw new Exception("Unrelated Boolean state changed");
            }
            if (enabled) throw new Exception("Native Checkbox did not write back to ref bool");
        }
        finally { ImGui.DestroyContext(context); }
        Console.WriteLine("PASS: ref Boolean writeback, in position, out callbacks/textures, and default window/style/clipping overloads.");
    }

    static void Main(string[] arguments)
    {
        var library = NativeLibrary.Load(Path.GetFullPath(arguments[0]));
        NativeLibrary.SetDllImportResolver(typeof(ImGui).Assembly, (name, assembly, path) => name == "imgui" ? library : 0);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        sbyte* output = stackalloc sbyte[256];
        var literal = "中文 100% %s %n {literal}";
        var length = ImGui.ImFormatString(output, 256, literal);
        Equal(literal, Marshal.PtrToStringUTF8((nint)output));
        if (length != Encoding.UTF8.GetByteCount(literal)) throw new Exception("UTF-8 length lost");
        ImGui.ImFormatStringV(output, 256, "{0:F2}% {{ok}} {1}", 12.5, "中文");
        Equal("12.50% {ok} 中文", Marshal.PtrToStringUTF8((nint)output));
        ImGui.ImFormatString(output, 256, literal, Array.Empty<object?>());
        Equal(literal, Marshal.PtrToStringUTF8((nint)output));
        ImGui.ImFormatString(output, 5, "abcdef");
        Equal("abcd", Marshal.PtrToStringUTF8((nint)output));
        try { ImGui.ImFormatString(output, 256, "{2}", 1); throw new Exception("FormatException missing"); }
        catch (FormatException) { }
        try { ImGui.ImFormatString(output, 256, "literal", (object?[])null!); throw new Exception("ArgumentNullException missing"); }
        catch (ArgumentNullException) { }
        ImGuiTextBuffer buffer = default;
        try
        {
            ImGuiTextBuffer.appendf(&buffer, literal);
            ImGuiTextBuffer.appendfv(&buffer, "|{0:F1}%", 7.5);
            Equal(literal + "|7.5%", Marshal.PtrToStringUTF8((nint)buffer.Buf.Data));
        }
        finally { ImGui.MemFree(buffer.Buf.Data); }
        var context = ImGui.CreateContext();
        try
        {
            sbyte* begin;
            sbyte* end;
            ImGui.ImFormatStringToTempBuffer(&begin, &end, literal);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Equal(literal, Marshal.PtrToStringUTF8((nint)begin));
            if (end - begin != Encoding.UTF8.GetByteCount(literal)) throw new Exception("Temporary buffer end lost");
            ImGui.ImFormatStringToTempBufferV(&begin, &end, "{0:F2}%", 12.5);
            Equal("12.50%", Marshal.PtrToStringUTF8((nint)begin));
            if (end - begin != 6) throw new Exception("Formatted end lost");
        }
        finally { ImGui.DestroyContext(context); }
        Console.WriteLine("PASS: literal percent/braces/UTF-8; invariant args; V forwarding; truncation/length; exceptions; struct append; native temporary-buffer lifetime.");
        TestSingleValueParameters();
    }
}
