# Changelog

All packages share one version number: `<Dear ImGui version>.<library revision>`.
For example `1.93.0.1` is the first revision of the bindings for Dear ImGui
1.93.0. The revision is bumped by hand whenever a package is republished without
moving to a new Dear ImGui tag.

## 1.93.0.3

Replaces the 1.93.0.2 workaround with real C++ defaults, and fixes a crash
caused by by-reference parameters whose default was `null`.

- **Binding fix**: ClangSharp emits C++ reference parameters such as
  `SetNextWindowPos`'s `pivot` as `ImVec2* pivot = null`. A C# default argument
  must be a compile-time constant, so a pointer cannot carry the C++ value, and
  `= null` made the native code dereference a null pointer (`0xC0000005`).
  `tools/postprocess.py` now parses the defaults out of `imgui.h` and emits each
  one as a `static readonly` field plus a forwarding overload, so the native
  value is preserved exactly:

  - `ProgressBar`'s `size_arg` keeps `ImVec2(-FLT_MIN, 0)` — "fill the available
    width". Treating `FLT_MIN` as `FLT_MAX` would have changed the layout.
  - `Image`'s `uv0`/`uv1` keep `ImVec2(0, 0)` and `ImVec2(1, 1)`.
  - `ImageWithBg`/`ImageButton`'s `bg_col`/`tint_col` keep `ImVec4(0,0,0,0)` and
    `ImVec4(1,1,1,1)`.

  Unlike 1.93.0.2, existing call sites keep compiling: the parameter is still a
  pointer in the extern, and omitting it now forwards to the C++ default instead
  of passing null. Parameters the native side genuinely null-checks
  (`IsMousePosValid`'s `mouse_pos`, `DockSpace`'s `window_class`,
  `RenderTextClipped`'s `clip_rect`) keep `= null`.
- The sample dashboard gained a README with screenshots, Microsoft YaHei font
  loading for CJK text, and PNG screenshot capture (`--screenshot`, `F12`).
- The sample's frame loop measures time with a monotonic clock; OpenTK's
  `FrameEventArgs.Time` could stall and freeze every animation.
- CI skips build-only runs for documentation changes, except
  `docs/PACKAGE-README.md`, which is packed as the NuGet README.

## 1.93.0.2

Superseded by 1.93.0.3. Removed the default from by-reference parameters, which
fixed the crash but broke call sites and discarded the C++ default values.

## 1.93.0.1

First public release.

- Bindings for Dear ImGui `v1.93.0-docking`, including `imgui_internal.h`.
- Native libraries for `win-x64`, `linux-x64` and `android-arm64`.
- Core package plus 15 backend packages (platform and renderer) and
  `ImGui4NET.Club` for the `ocornut/imgui_club` extensions.
- Targets `net8.0` and `net10.0`; Direct3D and Win32 backends target
  `net8.0-windows` and `net10.0-windows`.
- Trim- and NativeAOT-compatible: the bindings contain no reflection.
