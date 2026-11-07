using ImGui4NET.Backend.OpenGL3;
using ImGui4NET;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

// Minimal Dear ImGui + OpenTK host. Shows the expected frame structure:
// feed input into ImGuiIO, run NewFrame, build the UI, render.
unsafe
{
    using var window = new GameWindow(
        GameWindowSettings.Default,
        new NativeWindowSettings
        {
            ClientSize = new Vector2i(1280, 720),
            Title = "ImGui4NET + OpenTK",
            API = ContextAPI.OpenGL,
            APIVersion = new Version(4, 1),
            Profile = ContextProfile.Core,
        });
    window.VSync = VSyncMode.On;

    window.Load += () =>
    {
        Console.WriteLine($"OpenGL: {GL.GetString(StringName.Version)}");
        Console.WriteLine($"Dear ImGui: {System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)ImGui.GetVersion())}");

        ImGui.SetCurrentContext(ImGui.CreateContext());
        if (ImGui_ImplOpenGL3.Init("#version 330") == 0)
        {
            throw new InvalidOperationException("ImGui_ImplOpenGL3.Init failed");
        }
    };

    window.Unload += () =>
    {
        ImGui_ImplOpenGL3.Shutdown();
        ImGui.DestroyContext(ImGui.GetCurrentContext());
    };

    window.KeyDown += e =>
    {
        if (e.Key == Keys.Escape)
        {
            window.Close();
        }
    };

    var lastTime = 0.0;
    window.RenderFrame += e =>
    {
        var deltaSeconds = Math.Clamp((float)(e.Time - lastTime), 0f, 0.1f);
        lastTime = e.Time;

        GL.ClearColor(0.08f, 0.08f, 0.10f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        ImGuiIO* io = ImGui.GetIO();
        io->DeltaTime = deltaSeconds;
        io->DisplaySize = new ImVec2 { x = window.ClientSize.X, y = window.ClientSize.Y };
        io->DisplayFramebufferScale = new ImVec2 { x = 1, y = 1 };

        var mouse = window.MouseState;
        ImGuiIO.AddMousePosEvent(io, mouse.X, mouse.Y);
        ImGuiIO.AddMouseButtonEvent(io, 0, mouse.IsButtonDown(MouseButton.Left) ? (byte)1 : (byte)0);
        ImGuiIO.AddMouseButtonEvent(io, 1, mouse.IsButtonDown(MouseButton.Right) ? (byte)1 : (byte)0);
        ImGuiIO.AddMouseButtonEvent(io, 2, mouse.IsButtonDown(MouseButton.Middle) ? (byte)1 : (byte)0);
        ImGuiIO.AddMouseWheelEvent(io, mouse.ScrollDelta.X, mouse.ScrollDelta.Y);

        ImGui_ImplOpenGL3.NewFrame();
        ImGui.NewFrame();

        ImGui.ShowDemoWindow();
        ImGui.Begin("Info");
        ImGui.TextUnformatted($"FPS: {io->Framerate:F0}");
        ImGui.End();

        ImGui.Render();
        ImGui_ImplOpenGL3.RenderDrawData(ImGui.GetDrawData());

        window.SwapBuffers();
    };

    window.Run();
}

