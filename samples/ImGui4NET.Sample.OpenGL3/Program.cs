using ImGui4NET.Backend.OpenGL3;
using ImGui4NET.Club;
using ImGui4NET;
using ImGui4NET.Sample.OpenGL3;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using System.Runtime.InteropServices;

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

        ShowcaseUi.ApplyTheme();
        ShowcaseUi.LoadFonts();
    };

    window.Unload += () =>
    {
        ShowcaseUi.Shutdown();
        ImGui_ImplOpenGL3.Shutdown();
        ImGui.DestroyContext(ImGui.GetCurrentContext());
    };

    window.KeyDown += e =>
    {
        if (e.Key == Keys.Escape)
        {
            window.Close();
        }
        else if (e.Key == Keys.F12)
        {
            CaptureFrame(window, "screenshot.png");
        }
    };

    // `--screenshot <path>` renders a few frames, saves a PNG and exits. Used for
    // headless inspection of the UI from a terminal.
    string? autoScreenshotPath = null;
    int initialTab = 0;
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == "--screenshot")
        {
            autoScreenshotPath = args[i + 1];
        }
        else if (args[i] == "--tab" && int.TryParse(args[i + 1], out int tab))
        {
            initialTab = Math.Clamp(tab, 0, 4);
        }
    }

    ShowcaseUi.SetActiveTab(initialTab);

    int frameCount = 0;
    int autoScreenshotAt = autoScreenshotPath != null ? 30 : -1;

    // OpenTK's FrameEventArgs.Time is not guaranteed to advance when vsync is
    // disabled or when the driver throttles, which froze every animation because
    // DeltaTime collapsed to 0. Measure with a monotonic clock instead.
    var clock = System.Diagnostics.Stopwatch.StartNew();
    double lastTime = 0.0;
    const double TargetFrameSeconds = 1.0 / 120.0;

    window.RenderFrame += e =>
    {
        double now = clock.Elapsed.TotalSeconds;
        double elapsed = now - lastTime;
        lastTime = now;

        // Clamp so a debugger pause or a window drag does not teleport the
        // animations, but keep sub-millisecond deltas intact.
        var deltaSeconds = (float)Math.Clamp(elapsed, 0.0, 0.1);

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

        ShowcaseUi.Draw(deltaSeconds);

        ImGui.Render();
        ImGui_ImplOpenGL3.RenderDrawData(ImGui.GetDrawData());

        window.SwapBuffers();

        frameCount++;
        if (autoScreenshotAt >= 0 && frameCount == autoScreenshotAt)
        {
            CaptureFrame(window, autoScreenshotPath!);
            window.Close();
        }
    };

    window.Run();
}

static void CaptureFrame(GameWindow window, string path)
{
    window.MakeCurrent();
    Screenshot.Capture(window.ClientSize.X, window.ClientSize.Y, path);
}

