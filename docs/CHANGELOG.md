# Changelog

All packages share one version number: `<Dear ImGui version>.<library revision>`.
For example `1.93.0.1` is the first revision of the bindings for Dear ImGui
1.93.0. The revision is bumped by hand whenever a package is republished without
moving to a new Dear ImGui tag.

## 1.93.0.2

Fixes a crash when calling any API whose C++ parameter is a reference with a
default value, and adds a full sample application.

- **Binding fix (breaking for call sites)**: ClangSharp emitted by-reference
  parameters such as `SetNextWindowPos`'s `pivot` as `ImVec2* pivot = null`.
  The native signature takes a `const ImVec2&` whose default constructs a
  temporary, so it is never null, and the native code dereferences it
  unconditionally — omitting the argument crashed with an access violation
  (`0xC0000005`). `tools/postprocess.py` now drops the default on those
  parameters, so they must be passed explicitly. Affected: `BeginChild`,
  `SetNextWindowPos`, `Button`, `ButtonEx`, `ProgressBar`, `Image`,
  `ImageWithBg`, `ImageButton`, `InputTextMultiline`, `ColorButton`,
  `Selectable`, `BeginListBox`, `BeginTable`, `BeginTableEx`,
  `RenderTextClipped`, `RenderTextClippedEx`.
  Parameters the native side null-checks keep their `= null` default.
- New `samples/ImGui4NET.Sample.OpenGL3` dashboard: custom-drawn chrome,
  animated navigation, stat cards with sparklines and progress rings, a live
  two-series chart, tables, and the `imgui_club` Memory Editor. It loads
  Microsoft YaHei for CJK text and can capture PNG screenshots
  (`--screenshot`, or `F12` at runtime).
- The sample's frame loop now measures time with a monotonic clock. OpenTK's
  `FrameEventArgs.Time` could stall, which collapsed `DeltaTime` to zero and
  froze every animation.

## 1.93.0.1

First public release.

- Bindings for Dear ImGui `v1.93.0-docking`, including `imgui_internal.h`.
- Native libraries for `win-x64`, `linux-x64` and `android-arm64`.
- Core package plus 15 backend packages (platform and renderer) and
  `ImGui4NET.Club` for the `ocornut/imgui_club` extensions.
- Targets `net8.0` and `net10.0`; Direct3D and Win32 backends target
  `net8.0-windows` and `net10.0-windows`.
- Trim- and NativeAOT-compatible: the bindings contain no reflection.
