# ImGui4NET

Bare-metal .NET bindings for [Dear ImGui](https://github.com/ocornut/imgui), tracking `v1.93.0-docking`.

The bindings expose the native C++ API directly: structs are `unsafe` value
types with the exact native layout, and every function is a `[DllImport]` with
the mangled entry point. There is no wrapper layer and no allocation on the
interop boundary.

## Getting started

Install the core package plus one platform backend and one renderer backend:

```bash
dotnet add package ImGui4NET
dotnet add package ImGui4NET.Backend.Glfw       # platform
dotnet add package ImGui4NET.Backend.OpenGL3    # renderer
```

Native libraries for `win-x64`, `linux-x64` and `android-arm64` ship inside the
packages under `runtimes/`, so no extra build step is needed.

### Prerequisites

- .NET 8.0 or later.
- The native platform library the chosen backend links against (GLFW, SDL2/3,
  Vulkan loader, Direct3D, ...) must be resolvable at run time. Packages that
  need one already depend on the matching Silk.NET package, which supplies it.

## Usage

```csharp
using ImGui4NET;
using ImGui4NET.Backend.Glfw;
using ImGui4NET.Backend.OpenGL3;

unsafe
{
    ImGuiContext* ctx = ImGui.CreateContext();
    ImGui.SetCurrentContext(ctx);

    ImGui_ImplGlfw.InitForOpenGL(window, install_callbacks: 1);
    ImGui_ImplOpenGL3.Init("#version 330");

    // ... your frame loop ...

    ImGui_ImplOpenGL3.Shutdown();
    ImGui_ImplGlfw.Shutdown();
    ImGui.DestroyContext(ctx);
}
```

## Packages

| Package | Contents |
|---|---|
| `ImGui4NET` | Core Dear ImGui bindings plus the native `imgui` library |
| `ImGui4NET.Club` | Official `ocornut/imgui_club` extensions: Memory Editor, Multi-Context Compositor, Threaded Rendering |
| `ImGui4NET.Backend.OpenGL2` | OpenGL2 renderer backend |
| `ImGui4NET.Backend.OpenGL3` | OpenGL3 renderer backend |
| `ImGui4NET.Backend.DirectX9` | Direct3D 9 renderer backend |
| `ImGui4NET.Backend.DirectX11` | Direct3D 11 renderer backend |
| `ImGui4NET.Backend.DirectX12` | Direct3D 12 renderer backend |
| `ImGui4NET.Backend.Vulkan` | Vulkan renderer backend |
| `ImGui4NET.Backend.Win32` | Win32 platform backend |
| `ImGui4NET.Backend.Glfw` | GLFW platform backend |
| `ImGui4NET.Backend.Sdl2` | SDL2 platform backend |
| `ImGui4NET.Backend.Sdl3` | SDL3 platform backend |
| `ImGui4NET.Backend.SdlGpu3` | SDL3 GPU renderer backend |
| `ImGui4NET.Backend.Glut` | GLUT platform backend |
| `ImGui4NET.Backend.Allegro5` | Allegro5 platform backend |
| `ImGui4NET.Backend.Null` | Headless platform backend |
| `ImGui4NET.Backend.Android` | Android platform backend (arm64) |

Each backend package ships its own native library and depends only on the core
package, so an application pulls in exactly the backends it uses.

## Additional documentation

- [Repository](https://github.com/StArraySharp/ImGui4NET)
- [Build pipeline and native compilation](https://github.com/StArraySharp/ImGui4NET/blob/main/docs/build.md)
- [Changelog](https://github.com/StArraySharp/ImGui4NET/blob/main/docs/CHANGELOG.md)

## Feedback

Issues and pull requests: <https://github.com/StArraySharp/ImGui4NET/issues>

## License

MIT — see [LICENSE](https://github.com/StArraySharp/ImGui4NET/blob/main/LICENSE).

Dear ImGui and imgui_club are MIT licensed by Omar Cornut. Third-party
components (Silk.NET, freeglut, Allegro) are consumed as NuGet packages and keep
their own licenses.
