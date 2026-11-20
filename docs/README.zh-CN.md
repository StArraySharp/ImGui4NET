# ImGui4NET

[English](../README.md) | **简体中文**

面向 [Dear ImGui](https://github.com/ocornut/imgui) 的**裸机 .NET 绑定**，跟踪 `v1.93.0-docking`。

绑定直接暴露原生 C++ API：结构体是布局与原生完全一致的 `unsafe` 值类型，原生导入使用 mangled 入口点。原可变参数文本 API 改为托管格式化入口，再调用固定签名的原生导入；传入格式参数时可能产生分配。

## 快速开始

```bash
dotnet add package ImGui4NET
dotnet add package ImGui4NET.Backend.Glfw       # 平台后端
dotnet add package ImGui4NET.Backend.OpenGL3    # 渲染后端
```

`win-x64`、`linux-x64`、`osx-x64`、`osx-arm64`、`android-arm64` 的原生库随包发布在 `runtimes/` 下。所有包统一目标 `net10.0`；Direct3D 与 Win32 后端额外提供 `net10.0-windows` 版本（面向 Windows 的项目会自动选它），同时保留 `net10.0` 资产以便非 Windows 限定的项目引用。全部包均兼容 trim 与 NativeAOT。

## 包清单

| 包 | 内容 |
|---|---|
| `ImGui4NET` | Dear ImGui 核心绑定 + 原生 `imgui` 库 |
| `ImGui4NET.Club` | `ocornut/imgui_club` 官方扩展的纯 C# 移植：内存编辑器、多上下文合成器、线程化渲染（不带原生库） |
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
| `ImGui4NET.Backend.Metal` | Metal 渲染后端（macOS） |
| `ImGui4NET.Backend.Metal4` | Metal 4 渲染后端（macOS 26+、Apple Silicon） |
| `ImGui4NET.Backend.OSX` | macOS/Cocoa 平台后端 |
| `ImGui4NET.Backend.Allegro5` | Allegro5 平台后端 |
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

## 单值指针参数

经核对的单值指针由私有 P/Invoke 保留原生签名，对外提供 `ref`、`in`、`out` 托管重载。
例如 `ImGui.Checkbox("启用", ref enabled)`；`ImGui.Begin("窗口", ref open)` 显示关闭按钮并回写状态，
而 `ImGui.Begin("窗口")` 仍不显示关闭按钮。`ImGui.StyleColorsDark()` 仍更新当前样式。

可选裁剪参数和字体图集的每像素字节数输出仍可省略。只有确认原生支持的参数才会收到空指针。
原先为已转换参数传入裸指针的调用需要迁移；数组、句柄和缓冲区仍保留指针签名。

## 文本格式化

原 `__arglist` 方法现在接收普通文本，传入格式参数时使用 C# 组合格式：
`ImGui.Text("数值：{0:F2}", value)`。不传参数时，百分号和花括号均按普通文本处理。
原来的 `%d` 等 `printf` 格式需要改为 `{0}`；V 变体及原生 `va_list` 入口见
[文本格式化说明](varargs.md)。

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
- [docs/CHANGELOG.md](CHANGELOG.md) —— 发布历史

## 许可证

MIT —— 见 [LICENSE](../LICENSE)。

Dear ImGui 与 imgui_club 由 Omar Cornut 以 MIT 许可发布。Silk.NET、freeglut、
Allegro 等第三方组件以 NuGet 包形式引用，各自保留原有许可。