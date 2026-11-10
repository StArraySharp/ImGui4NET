# Changelog

All packages share one version number: `<Dear ImGui version>.<library revision>`.
For example `1.93.0.1` is the first revision of the bindings for Dear ImGui
1.93.0. The revision is bumped by hand whenever a package is republished without
moving to a new Dear ImGui tag.

## 1.93.0.4

Drops `net8.0`, adds macOS support, and removes `ImGui4NET.Backend.Null`.

- **Target framework is now `net10.0` only.** `net8.0` is gone from every
  package, so a project on .NET 8 can no longer reference them. This is a
  breaking change for consumers who had not moved to .NET 10.
- The Windows-only backends (`DirectX9`, `DirectX11`, `DirectX12`, `Win32`)
  used to ship only `net8.0-windows`/`net10.0-windows`. They now ship
  `net10.0-windows` **and** a plain `net10.0` asset: a project that sets
  `<TargetFramework>net10.0-windows</TargetFramework>` still resolves the
  Windows build, while a project without a platform qualifier (a
  cross-compiled tool, a `net10.0` library, a test project) can reference the
  package instead of failing with NU1201.
  - Native libraries for `osx-x64` and `osx-arm64` are now built and shipped. They
  are cross-compiled from Windows or Linux with clang plus `ld64.lld` against a
  downloaded macOS SDK — no Mac required. `tools/fetch-macos-sdk.sh` fetches the
  SDK, and `tools/generate.sh --macos` (also part of `--all`) drives both
  architectures.
- Backends for macOS: `OpenGL2`, `OpenGL3`, `Glut`, `Metal`, `Metal4` and `OSX`.
  GLFW, SDL2/3 and Vulkan need headers that are not part of the SDK.
- **New package `ImGui4NET.Backend.Metal4`.** Metal 4 renderer backend for
  `osx-arm64` only. Unlike every other backend it records `minos=26.0`
  (`-mmacosx-version-min=26.0`), because `imgui_impl_metal4.mm` binds
  `MTL4RenderPassDescriptor` and `MTL4CommandQueue`, both
  `API_AVAILABLE(macos(26.0))`, without a runtime guard. Building it with the
  usual 11.0 floor would only silence the availability warnings and produce a
  dylib that dyld accepts on macOS 11 and then fails to resolve. The
  `OBJC_BACKENDS` table in `tools/generate.sh` therefore carries a per-backend
  deployment target and architecture list; x64 is skipped even though it links.
- Method names in the Objective-C++ backends drop their C prefix like every
  other backend (`ImGui_ImplMetal4.Init`, not `ImGui_ImplMetal4.ImGui_ImplMetal4_Init`),
  and a C++ `bool` return stays a `byte` instead of being wrapped into a managed
  `bool`, matching the rest of the repository. There are no `_native` wrappers
  anywhere either: the `DllImport` method itself is the public API, with the
  handle types taken by value.
- **`ImGui4NET.Club` is now a pure C# port.** The three extensions define every
  method inside their class body, so ClangSharp emitted the struct layout but no
  externs, and forcing the inline bodies out of a native library needed a
  dedicated instantiation unit, `-fkeep-inline-functions`, `-O0` and, on Clang,
  `-femit-all-decls`. Since all three only ever call the public Dear ImGui API,
  they are reimplemented in C# instead:
  `MemoryEditor`, `MultiContextCompositor`, `ImDrawDataSnapshot` and
  `ImTextureQueue`. `tools/club-instantiate.cpp`, `ClubNative.cs`, the generated
  layout files and the `runtimes/` folder are gone; `generate.sh` prints
  `imgui_club: pure C# port, nothing to generate`. This also removes one native
  library per RID from the package.
  - `ImVectorUtil` reproduces `ImVector<T>`'s `reserve`/`resize`/`push_back`/
    `swap`/`clear` against `ImGui.MemAlloc`/`MemFree`, because a draw list
    buffer allocated by the CLR cannot be handed back to Dear ImGui.
  - `ImTextureQueue`'s per-texture records stay in native memory: their address
    is stored in `ImTextureData.QueueUserData` and dereferenced by the render
    thread.
- `Metal` and `OSX` are Objective-C++. Their bindings come from
  `tools/gen-objc-backend.sh`, which parses with `-x objective-c++` and hands the
  header to `postprocess.py --macho-symbols`: a Mach-O symbol table stores every
  C symbol with a leading underscore, but `dlsym()` expects the C spelling, so
  the extra underscore is removed from `EntryPoint` while the `_Z…` mangling is
  left intact. Objective-C handles (`id<MTLDevice>`, `NSView`) become
    `readonly struct` value types carrying only the pointer, so the `DllImport`
    takes them by value: no wrapper method, no marshalling, and one null value
    (`NSView.Null`) for parameters the header marks `_Nullable`.
- The backend-to-core dependency is recorded as `@loader_path/libimgui.dylib`,
  so each backend resolves the core library from its own `runtimes/<rid>/native`
  folder.
- `tools/lp64-aliases.py` learned to read Mach-O symbol tables directly (GNU
  `nm` cannot) and to emit an assembly alias file, because `ld64.lld -alias`
  cannot bind to a symbol defined by an object file. x64 needs 46 aliases,
  arm64 needs 29 — `va_list` mangles differently between the two.
- The Null backend was a headless platform stub that no published package
  depends on and that the sample never used. Keeping it meant building,
  packing and shipping three native libraries for a backend nobody consumes, so
  the project, its generated bindings and its native binaries are gone and
  `tools/generate.sh` no longer emits them.
- Backend packages are now 17 instead of 15 (Metal 4 added, Null removed).

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
