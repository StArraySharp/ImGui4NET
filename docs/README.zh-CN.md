# ImGui4NET

[English](../README.md) | **简体中文**

面向 [Dear ImGui](https://github.com/ocornut/imgui) 的**裸机 .NET 绑定**，跟踪 `v1.93.0-docking`。

绑定直接暴露原生 C++ API：结构体是布局与原生完全一致的 `unsafe` 值类型，每个函数都是带 mangled 入口点的 `[DllImport]`。**没有包装层，互操作边界上零分配。**

## 包清单

| 包 | 内容 |
|---|---|
| `ImGui4NET` | Dear ImGui 核心绑定 + 原生 `imgui` 库 |
| `ImGui4NET.Club` | `ocornut/imgui_club` 官方扩展：内存编辑器、多上下文合成器、线程化渲染 |
| `ImGui4NET.Backend.OpenGL2` | OpenGL2 渲染后端 |
| `ImGui4NET.Backend.OpenGL3` | OpenGL3 渲染后端 |
| `ImGui4NET.Backend.DirectX9` | Direct3D 9 渲染后端 |
| `ImGui4NET.Backend.DirectX11` | Direct3D 11 渲染后端 |
| `ImGui4NET.Backend.DirectX12` | Direct3D 12 渲染后端 |
| `ImGui4NET.Backend.Vulkan` | Vulkan 渲染后端 |
| `ImGui4NET.Backend.Win32` | Win32 平台后端 |
| `ImGui4NET.Backend.Glfw` | GLFW 平台后端 |
| `ImGui4NET.Backend.Sdl2` | SDL2 平台后端 |
| `ImGui4NET.Backend.Sdl3` | SDL3 平台后端 |
| `ImGui4NET.Backend.SdlGpu3` | SDL3 GPU 渲染后端 |
| `ImGui4NET.Backend.Glut` | GLUT 平台后端 |
| `ImGui4NET.Backend.Allegro5` | Allegro5 平台后端 |
| `ImGui4NET.Backend.Null` | 无头平台后端 |
| `ImGui4NET.Backend.Android` | Android 平台后端（arm64） |

每个后端包自带对应的原生库，只依赖核心包，因此应用只会拉取真正用到的后端。

## 用法

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

## 从源码构建

需要 `imgui` 与 `imgui_club` 子模块、bash、python3、.NET SDK 和一个 C++17 编译器。

```bash
git submodule update --init --recursive
dotnet tool restore
./tools/fetch-deps.sh          # 下载 Allegro / freeglut 原生库
./tools/generate.sh --native   # 重新生成绑定并编译桌面原生库
dotnet build ImGui4NET.slnx
```

完整流程、各平台前置依赖、Android 交叉编译与排错见 [docs/build.zh-CN.md](build.zh-CN.md)。

## 文档

- [docs/build.zh-CN.md](build.zh-CN.md) —— 构建管线、原生编译、排错（[English](build.md)）
- [docs/dependency-tree.md](dependency-tree.md) —— 包依赖与原生依赖图

## 许可证

MIT —— 见 [LICENSE](../LICENSE)。

Dear ImGui 与 imgui_club 由 Omar Cornut 以 MIT 许可发布。Silk.NET、freeglut、
Allegro 等第三方组件以 NuGet 包形式引用，各自保留原有许可。