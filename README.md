# ImGui4NET

**English** | [简体中文](docs/README.zh-CN.md)

Bare-metal .NET bindings for [Dear ImGui](https://github.com/ocornut/imgui), tracking `v1.93.0-docking`.

The bindings expose the native C++ API directly: structs are `unsafe` value
types with the exact native layout, and every function is a `[DllImport]` with
the mangled entry point. There is no wrapper layer and no allocation on the
interop boundary.

## Getting started

```bash
dotnet add package ImGui4NET
dotnet add package ImGui4NET.Backend.Glfw       # platform backend
dotnet add package ImGui4NET.Backend.OpenGL3    # renderer backend
```

Native libraries for `win-x64`, `linux-x64` and `android-arm64` ship inside the
packages under `runtimes/`. Packages target `net8.0` and `net10.0`; the
Direct3D and Win32 backends target `net8.0-windows` and `net10.0-windows`.
All packages are trim- and NativeAOT-compatible.

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

## Usage

```csharp
using ImGui4NET;
using ImGui4NET.Backend.OpenGL3;

unsafe
{
    ImGuiContext* ctx = ImGui.CreateContext();
    ImGui.SetCurrentContext(ctx);
    ImGui_ImplOpenGL3.Init("#version 330");
}
```

## Building from source

Requires the `imgui` and `imgui_club` submodules, bash, python3, the .NET SDK, and a C++17 compiler.

```bash
git submodule update --init --recursive
dotnet tool restore
./tools/fetch-deps.sh          # download Allegro/freeglut native libraries
./tools/generate.sh --native   # regenerate bindings and build desktop natives
dotnet build ImGui4NET.slnx
```

See [docs/build.md](docs/build.md) for the full pipeline, platform-specific
prerequisites, Android cross-compilation and troubleshooting.

## Documentation

- [docs/build.md](docs/build.md) — build pipeline, native compilation, troubleshooting ([中文](docs/build.zh-CN.md))
- [docs/CHANGELOG.md](docs/CHANGELOG.md) — release history
- [samples/ImGui4NET.Sample.OpenGL3](samples/ImGui4NET.Sample.OpenGL3/README.md) — a full dashboard built on the raw bindings, with screenshots

## Sample

[samples/ImGui4NET.Sample.OpenGL3](samples/ImGui4NET.Sample.OpenGL3/README.md)
is a complete application: OpenTK window, custom-drawn dashboard, Microsoft
YaHei for CJK text, the `imgui_club` Memory Editor, and PNG screenshot capture.

```bash
dotnet run --project samples/ImGui4NET.Sample.OpenGL3
```

## License

MIT — see [LICENSE](LICENSE).

Dear ImGui and imgui_club are MIT licensed by Omar Cornut. Third-party
components (Silk.NET, freeglut, Allegro) are consumed as NuGet packages and keep
their own licenses.