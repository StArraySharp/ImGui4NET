# Changelog

All packages share one version number: `<Dear ImGui version>.<library revision>`.
For example `1.93.0.1` is the first revision of the bindings for Dear ImGui
1.93.0. The revision is bumped by hand whenever a package is republished without
moving to a new Dear ImGui tag.

## 1.93.0.1

First public release.

- Bindings for Dear ImGui `v1.93.0-docking`, including `imgui_internal.h`.
- Native libraries for `win-x64`, `linux-x64` and `android-arm64`.
- Core package plus 15 backend packages (platform and renderer) and
  `ImGui4NET.Club` for the `ocornut/imgui_club` extensions.
- Targets `net8.0` and `net10.0`; Direct3D and Win32 backends target
  `net8.0-windows` and `net10.0-windows`.
- Trim- and NativeAOT-compatible: the bindings contain no reflection.
