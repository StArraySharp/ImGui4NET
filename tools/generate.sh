#!/usr/bin/env bash
# Regenerate every binding and (optionally) rebuild the native libraries.
#
# Layout produced:
#   src/ImGui4NET/Generated/*.cs                     one file per type
#   src/ImGui4NET.Backend.<Name>/Generated/*.cs      one file per backend
#   src/ImGui4NET/runtimes/<rid>/native/imgui.*      core native library
#   src/ImGui4NET.Backend.<Name>/runtimes/<rid>/native/*
#
# Requirements: bash, python3, dotnet, a C++17 compiler (g++/clang++).
# The ClangSharp generator needs a clang resource directory; on Windows/MSYS2
# that is <mingw>/lib/clang/<version>.
#
# Usage:
#   ./tools/generate.sh              # regenerate bindings only
#   ./tools/generate.sh --native     # also rebuild desktop native libraries
#   ./tools/generate.sh --android    # also rebuild the Android arm64 libraries
#   ./tools/generate.sh --macos      # also cross-build the macOS libraries
#   ./tools/generate.sh --all        # desktop + Android + macOS
#   ./tools/generate.sh --native --no-generate
#                                    # build libraries only, keep existing bindings
#   ./tools/generate.sh --help
#
# macOS cross-builds run on any host that has clang and ld64.lld (the MSYS2
# clang package provides both). The SDK is fetched on demand; see
# tools/fetch-macos-sdk.sh.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

IMGUI_DIR="$REPO_ROOT/imgui"
CLUB_DIR="$REPO_ROOT/imgui_club"
SRC_DIR="$REPO_ROOT/src"
STUB_DIR="$REPO_ROOT/third_party/stubs"
TOOL="$SCRIPT_DIR/postprocess.py"

# Third-party native libraries that are distributed as NuGet packages rather
# than coming from the toolchain. Expected layout:
#   third_party/libs/freeglut/{sources,runtimes/...}
#   third_party/libs/allegro/{build/native,...}
EXTERNAL_LIBS_DIR="$REPO_ROOT/third_party/libs"

# macOS cross-compilation inputs. The SDK is unpacked by tools/fetch-macos-sdk.sh
# into third_party/macos-sdk/<version>/MacOSX<version>.sdk; point
# MACOS_SDK_DIR at an existing SDK to reuse one that is already on disk.
MACOS_SDK_VERSION="${MACOS_SDK_VERSION:-26.0}"
MACOS_SDK_ROOT="$REPO_ROOT/third_party/macos-sdk"
MACOS_MIN_VERSION="${MACOS_MIN_VERSION:-11.0}"
MACOS_ARCHES="${MACOS_ARCHES:-x64 arm64}"

NAMESPACE="ImGui4NET"
BACKEND_NAMESPACE_PREFIX="ImGui4NET.Backend"

BUILD_NATIVE=0
BUILD_ANDROID=0
BUILD_MACOS=0
SKIP_GENERATE=0
for arg in "$@"; do
    case "$arg" in
        --native) BUILD_NATIVE=1 ;;
        --android) BUILD_ANDROID=1 ;;
        --macos) BUILD_MACOS=1 ;;
        --all) BUILD_NATIVE=1; BUILD_ANDROID=1; BUILD_MACOS=1 ;;
        # Native-only: build the libraries without regenerating bindings. Used
        # when cross-building on a machine whose target triple differs from the
        # one the checked-in bindings were generated with.
        --no-generate) SKIP_GENERATE=1 ;;
        --help|-h)
            sed -n '2,32p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "error: unknown argument '$arg'" >&2
            exit 1
            ;;
    esac
done

# ---------------------------------------------------------------------------
# Toolchain discovery
# ---------------------------------------------------------------------------

if [ -z "${PYTHON:-}" ]; then
    for candidate in python3 python py; do
        if command -v "$candidate" >/dev/null 2>&1; then
            # Reject the MSYS2/cygwin python: it cannot open Windows paths.
            if "$candidate" -c 'import sys; print(sys.platform)' 2>/dev/null | grep -q win; then
                continue
            fi
            PYTHON="$candidate"
            break
        fi
    done
fi
if [ -z "${PYTHON:-}" ]; then
    echo "error: python3 not found (needed by $TOOL)" >&2
    exit 1
fi

if [ "$SKIP_GENERATE" != "1" ] && ! command -v dotnet >/dev/null 2>&1; then
    echo "error: dotnet not found" >&2
    exit 1
fi

# Locate the clang resource directory bundled with the generator toolchain.
find_clang_resource() {
    if [ -n "${CLANG_RESOURCE:-}" ]; then
        echo "$CLANG_RESOURCE"
        return
    fi
    local roots=()
    [ -n "${MINGW_ROOT:-}" ] && roots+=("$MINGW_ROOT/lib/clang")
    roots+=("/usr/lib/clang" "/usr/lib/llvm-*/lib/clang" "/usr/local/lib/clang")

    local found
    for root in "${roots[@]}"; do
        for dir in $root/*/; do
            [ -d "$dir/include" ] && found="$dir"
        done
    done
    if [ -z "${found:-}" ]; then
        echo "error: no clang resource directory found; set CLANG_RESOURCE" >&2
        exit 1
    fi
    echo "${found%/}"
}

CLANG_RESOURCE_DIR=""
if [ "$SKIP_GENERATE" != "1" ]; then
    CLANG_RESOURCE_DIR="$(find_clang_resource)"
fi

# ---------------------------------------------------------------------------
# Host detection
# ---------------------------------------------------------------------------

case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) HOST=windows ;;
    Linux)                HOST=linux ;;
    Darwin)               HOST=osx ;;
    *)                    HOST=unknown ;;
esac

# The generator's --target must match the ABI of the native library so the
# emitted EntryPoints carry the right name mangling.
case "$HOST" in
    windows)
        TARGET_TRIPLE="x86_64-w64-windows-gnu"
        LIB_NAME="imgui.dll"
        DEFAULT_CXX_SUFFIX=".exe"
        ;;
    linux)
        TARGET_TRIPLE="x86_64-unknown-linux-gnu"
        LIB_NAME="libimgui.so"
        DEFAULT_CXX_SUFFIX=""
        ;;
    osx)
        TARGET_TRIPLE="x86_64-apple-darwin"
        LIB_NAME="libimgui.dylib"
        DEFAULT_CXX_SUFFIX=""
        ;;
    *)
        echo "error: unsupported host $(uname -s)" >&2
        exit 1
        ;;
esac

RID="win-x64"
[ "$HOST" = "linux" ] && RID="linux-x64"
[ "$HOST" = "osx" ] && RID="osx-x64"

# Prefix of the toolchain that owns the C++ headers; used to locate nested SDK
# headers (SDL2 keeps its headers in an SDL2/ subdirectory).
resolve_prefix() {
    local cxx="$1"
    # <prefix>/bin/<compiler> -> <prefix>
    local bindir
    bindir="$(cd "$(dirname "$cxx")" && pwd)"
    echo "$(dirname "$bindir")"
}

echo "==> host=$HOST target=$TARGET_TRIPLE clang=$CLANG_RESOURCE_DIR"

# ---------------------------------------------------------------------------
# Binding generation
# ---------------------------------------------------------------------------

GEN_ARGS=(
    "-a" "--target=$TARGET_TRIPLE"
    "-a" "-isystem" "-a" "$IMGUI_DIR"
    "-rd" "$CLANG_RESOURCE_DIR"
    "-std" "c++17"
    "--generate" "helper-types"
    "--generate" "funcs-with-body=false"
)

# ImGui's internal headers reference <immintrin.h> when SSE is enabled; the
# bundled libclang does not know every AVX512 builtin, so keep it off.
DEFINE_ARGS=(
    "-a" "-DIMGUI_DISABLE_SSE"
    "-a" "-DIMGUI_DISABLE_WIN32_FUNCTIONS"
)

generate() {
    local out_dir="$1" namespace="$2" input="$3" library="$4" methods_class="$5"
    shift 5
    local scratch
    scratch="$(mktemp -d)"
    local raw="$scratch/raw.cs"

    echo "    -> $namespace"
    dotnet tool run ClangSharpPInvokeGenerator -- \
        -f "$input" \
        -n "Scratch.Generated" \
        -o "$raw" \
        -l "$library" \
        "${GEN_ARGS[@]}" "${DEFINE_ARGS[@]}" "$@" >"$scratch/log" 2>&1 || {
            echo "error: generation failed for $input" >&2
            cat "$scratch/log" >&2
            rm -rf "$scratch"
            exit 1
        }

    local -a extra_post=()
    # Backends live in their own namespace and consume the core types.
    if [ "$namespace" != "$NAMESPACE" ]; then
        extra_post+=(--using "$NAMESPACE")
    fi
    # Strip the C type prefixes so generated types read naturally.
    for prefix in ${TYPE_PREFIXES:-}; do
        extra_post+=(--type-prefix "$prefix")
    done

    rm -rf "$out_dir"
    "$PYTHON" "$TOOL" "$raw" -d "$out_dir" \
        -n "$namespace" -l "$library" --methods-class "$methods_class" \
        --method-prefix "$methods_class" \
        "${extra_post[@]}"
    rm -rf "$scratch"
}

# A synthetic translation unit that pulls in both public and internal headers.
# Generating from imgui.cpp instead would drop every function whose definition
# has a body, and imgui.h alone does not declare the internal structs.
TU_DIR="$(mktemp -d)"
cat >"$TU_DIR/imgui_tu.h" <<'EOF'
#include "imgui.h"
#include "imgui_internal.h"
EOF

echo "==> Generating core bindings"
if [ "$SKIP_GENERATE" = "1" ]; then
    echo "    (skipped: --no-generate)"
else
    generate "$SRC_DIR/ImGui4NET/Generated" "$NAMESPACE" "$TU_DIR/imgui_tu.h" \
        "imgui" "ImGui" \
        -t "$IMGUI_DIR/imgui.h" \
        -t "$IMGUI_DIR/imgui_internal.h"
fi

# ---------------------------------------------------------------------------
# imgui_club
# ---------------------------------------------------------------------------
#
# The club extensions are bound as a pure C# port and need no generator step:
# imgui_memory_editor.h, imgui_threaded_rendering.h and
# imgui_multicontext_compositor.h all define their methods inline, and
# ClangSharp emits nothing for a function whose body lives in a header it
# traverses. Rather than forcing those definitions out of a native library, the
# logic lives in src/ImGui4NET.Club/*.cs and calls only the public Dear ImGui
# entry points via ImGui4NET.
echo "==> imgui_club: pure C# port, nothing to generate"

rm -rf "$TU_DIR"

# ---------------------------------------------------------------------------
# Backend bindings
# ---------------------------------------------------------------------------

# name|header|extra args|remap table|type prefixes|hosts
# ``hosts`` restricts a backend's native library to platforms where its
# dependencies exist. Comma-separated; empty means every supported host.
#
# Android notes: the NDK provides GLES (not desktop GL) and Vulkan from API 24,
# so OpenGL3 builds against GLESv2/EGL and Vulkan needs the api-24 toolchain.
# OpenGL2 (fixed-function GL), GLFW, SDL2/3, Glut and Allegro5 have no NDK
# headers and are therefore skipped there.
#
# macOS notes: only the backends whose dependencies ship inside the macOS SDK
# are built. OpenGL2/OpenGL3 need OpenGL.framework, Glut needs GLUT.framework.
# GLFW, SDL2/3 and Vulkan would need their own headers, and Metal is
# Objective-C++ (.mm) which this build does not drive.
BACKENDS=(
    "OpenGL2|imgui_impl_opengl2.h|||ImGui_ImplOpenGL2_|windows,linux,osx"
    "OpenGL3|imgui_impl_opengl3.h|||ImGui_ImplOpenGL3_|windows,linux,android,osx"
    "DirectX9|imgui_impl_dx9.h||D3D9|ImGui_ImplDX9_|windows"
    "DirectX11|imgui_impl_dx11.h||D3D11|ImGui_ImplDX11_|windows"
    "DirectX12|imgui_impl_dx12.h|-a -isystem -a $STUB_DIR/d3d12|D3D12|ImGui_ImplDX12_|windows"
    "Vulkan|imgui_impl_vulkan.h||Vulkan|ImGui_ImplVulkanH_ ImGui_ImplVulkan_|windows,linux,android"
    "Win32|imgui_impl_win32.h|||ImGui_ImplWin32_|windows"
    "Glfw|imgui_impl_glfw.h||GLFW|ImGui_ImplGlfw_|windows,linux"
    "Sdl2|imgui_impl_sdl2.h||SDL2|ImGui_ImplSDL2_|windows,linux"
    "Sdl3|imgui_impl_sdl3.h|||ImGui_ImplSDL3_|windows,linux"
    "SdlGpu3|imgui_impl_sdlgpu3.h|-a -isystem -a $STUB_DIR -t $IMGUI_DIR/backends/imgui_impl_sdlgpu3.h -t $STUB_DIR/SDL3/SDL_gpu.h||ImGui_ImplSDLGPU3_|windows,linux"
    "Glut|imgui_impl_glut.h|||ImGui_ImplGLUT_|windows,linux,osx"
    "Allegro5|imgui_impl_allegro5.h|||ImGui_ImplAllegro5_ ALLEGRO_|windows,linux"
    "Android|imgui_impl_android.h|-a -include -a stdint.h||ImGui_ImplAndroid_|android"
    # Objective-C++ sources: these are built by the macOS section only, and the
    # bindings come from tools/gen-objc-backend.sh rather than the generic
    # generator path (see the OBJC_BACKENDS table).
    "Metal|imgui_impl_metal.h|||ImGui_ImplMetal_|osx"
    "Metal4|imgui_impl_metal4.h|||ImGui_ImplMetal4_|osx"
    "OSX|imgui_impl_osx.h|||ImGui_ImplOSX_|osx"
)

# Objective-C++ backends have their own generation path and link requirements.
# name|header|frameworks|opaque ObjC types|deployment target|architectures
#
# ``deployment target`` overrides MACOS_MIN_VERSION for that backend only.
# Metal4 is the reason this exists: it binds MTL4 symbols that are declared
# API_AVAILABLE(macos(26.0)), so an 11.0 floor would produce a dylib that loads
# on older systems and then fails to resolve those symbols at import time.
#
# ``architectures`` limits the backend to the architectures it can actually run
# on. Metal4 needs Apple Silicon, so x64 is skipped even though it would link.
OBJC_BACKENDS=(
    "Metal|imgui_impl_metal.h|-framework Metal -framework MetalKit -framework Cocoa -framework QuartzCore|MTLRenderPassDescriptor MTLDevice MTLCommandBuffer MTLRenderCommandEncoder|$MACOS_MIN_VERSION|x64,arm64"
    "Metal4|imgui_impl_metal4.h|-framework Metal -framework QuartzCore -framework Cocoa|MTL4RenderPassDescriptor MTLDevice MTL4CommandQueue MTL4CommandBuffer MTL4RenderCommandEncoder|26.0|arm64"
    "OSX|imgui_impl_osx.h|-framework Cocoa -framework GameController -framework Metal -framework QuartzCore|NSView|$MACOS_MIN_VERSION|x64,arm64"
)

# Remaps the generator cannot infer: C++ names that already exist in Silk.NET.
# Passing ``-e NAME -rt NAME=Silk.NET.Type`` makes the emitted code reference
# the Silk.NET type instead of defining a duplicate struct.
REMAP_VULKAN=(
    VkInstance_T=Instance VkPhysicalDevice_T=PhysicalDevice VkDevice_T=Device
    VkQueue_T=Queue VkCommandBuffer_T=CommandBuffer VkCommandPool_T=CommandPool
    VkDescriptorPool_T=DescriptorPool VkDescriptorSet_T=DescriptorSet
    VkFence_T=Fence VkSemaphore_T=Semaphore VkFramebuffer_T=Framebuffer
    VkImage_T=Image VkImageView_T=ImageView VkPipeline_T=Pipeline
    VkPipelineCache_T=PipelineCache VkPipelineLayout_T=PipelineLayout
    VkRenderPass_T=RenderPass VkSampler_T=Sampler
    VkSurfaceKHR_T=SurfaceKHR VkSwapchainKHR_T=SwapchainKHR
    VkAllocationCallbacks=AllocationCallbacks VkClearValue=ClearValue
    VkAttachmentDescription=AttachmentDescription VkDynamicState=DynamicState
    VkFormat=Format VkImageLayout=ImageLayout VkImageUsageFlags=ImageUsageFlags
    VkResult=Result VkSampleCountFlagBits=SampleCountFlags
    VkShaderModuleCreateInfo=ShaderModuleCreateInfo
    VkSurfaceFormatKHR=SurfaceFormatKHR VkPresentModeKHR=PresentModeKHR
    VkColorSpaceKHR=ColorSpaceKHR VkDeviceSize=DeviceSize
    # The non-KHR spelling is the promoted alias of the KHR struct.
    VkPipelineRenderingCreateInfo=PipelineRenderingCreateInfoKHR
    VkPipelineRenderingCreateInfoKHR=PipelineRenderingCreateInfoKHR
)

REMAP_D3D12=(
    DXGI_FORMAT=Silk.NET.DXGI.Format
    D3D12_CPU_DESCRIPTOR_HANDLE=Silk.NET.Direct3D12.CpuDescriptorHandle
    D3D12_GPU_DESCRIPTOR_HANDLE=Silk.NET.Direct3D12.GpuDescriptorHandle
    ID3D12Device=Silk.NET.Direct3D12.ID3D12Device
    ID3D12CommandQueue=Silk.NET.Direct3D12.ID3D12CommandQueue
    ID3D12DescriptorHeap=Silk.NET.Direct3D12.ID3D12DescriptorHeap
    ID3D12GraphicsCommandList=Silk.NET.Direct3D12.ID3D12GraphicsCommandList
)

# DX11/DX9 types are already blittable COM vtbl structs in Silk.NET, so the
# generated pointer declarations can reference them directly.
REMAP_D3D11=(
    ID3D11Device=Silk.NET.Direct3D11.ID3D11Device
    ID3D11DeviceContext=Silk.NET.Direct3D11.ID3D11DeviceContext
    ID3D11Buffer=Silk.NET.Direct3D11.ID3D11Buffer
    ID3D11SamplerState=Silk.NET.Direct3D11.ID3D11SamplerState
)

REMAP_D3D9=(
    IDirect3DDevice9=Silk.NET.Direct3D9.IDirect3DDevice9
)

# GLFW's opaque handles have Silk.NET equivalents.
REMAP_GLFW=(
    GLFWwindow=Silk.NET.GLFW.WindowHandle
    GLFWmonitor=Silk.NET.GLFW.Monitor
)

# SDL2 platform handles.
REMAP_SDL2=(
    SDL_Window=Silk.NET.SDL.Window
    SDL_Renderer=Silk.NET.SDL.Renderer
    SDL_GameController=Silk.NET.SDL.GameController
    SDL_Event=Silk.NET.SDL.Event
)

build_remap_args() {
    local kind="$1"
    REMAP_ARGS=()
    local -a pairs=()
    case "$kind" in
        Vulkan) pairs=("${REMAP_VULKAN[@]}") ;;
        D3D12)  pairs=("${REMAP_D3D12[@]}") ;;
        D3D11)  pairs=("${REMAP_D3D11[@]}") ;;
        D3D9)   pairs=("${REMAP_D3D9[@]}") ;;
        GLFW)   pairs=("${REMAP_GLFW[@]}") ;;
        SDL2)   pairs=("${REMAP_SDL2[@]}") ;;
        *)      return ;;
    esac
    for pair in "${pairs[@]}"; do
        local name="${pair%%=*}"
        local target="${pair#*=}"
        # Only the Vulkan table stores short names; every other table already
        # carries the fully qualified type.
        if [ "$kind" = "Vulkan" ]; then
            target="Silk.NET.Vulkan.$target"
        fi
        REMAP_ARGS+=("-e" "$name" "-rt" "$name=$target")
    done
}

echo "==> Generating backend bindings"
if [ "$SKIP_GENERATE" = "1" ]; then
    echo "    (skipped: --no-generate)"
else
    for entry in "${BACKENDS[@]}"; do
        IFS='|' read -r name header extra remap type_prefixes hosts <<<"$entry"

        # Objective-C++ backends are handled by gen-objc-backend.sh below.
        case "$name" in
            Metal|Metal4|OSX) continue ;;
        esac

        header_path="$IMGUI_DIR/backends/$header"
        if [ ! -f "$header_path" ]; then
            echo "    skipping $name (no $header)"
            continue
        fi

        # Backend native libraries are named after the backend so several can be
        # loaded side by side without clashing.
        library="imgui_backend_$(echo "$name" | tr '[:upper:]' '[:lower:]')"

        build_remap_args "$remap"
        TYPE_PREFIXES="$type_prefixes"

        # shellcheck disable=SC2086
        generate "$SRC_DIR/ImGui4NET.Backend.$name/Generated" \
            "$BACKEND_NAMESPACE_PREFIX.$name" \
            "$header_path" \
            "$library" \
            "ImGui_Impl$name" \
            "${REMAP_ARGS[@]}" \
            -t "$header_path" \
            $extra
    done

    # Objective-C++ backends. Their headers declare the API only under
    # #ifdef __OBJC__, the native code is .mm, and Mach-O symbol tables add a
    # leading underscore that dlsym() does not want - all handled by the
    # dedicated script.
    for entry in "${OBJC_BACKENDS[@]}"; do
        IFS='|' read -r name header _frameworks opaque min_version arches <<<"$entry"
        header_path="$IMGUI_DIR/backends/$header"
        if [ ! -f "$header_path" ]; then
            echo "    skipping $name (no $header)"
            continue
        fi

        library="imgui_backend_$(echo "$name" | tr '[:upper:]' '[:lower:]')"
        # A backend with a newer deployment target than the toolchain default
        # must generate against that target too: the generator's
        # -mmacosx-version-min decides which API_AVAILABLE declarations are
        # visible, and Metal4's symbols only exist at 26.0.
        # shellcheck disable=SC2086
        MACOS_MIN_VERSION="${min_version:-$MACOS_MIN_VERSION}" \
            "${BASH:-/bin/bash}" "$SCRIPT_DIR/gen-objc-backend.sh" \
            "$header_path" \
            "$BACKEND_NAMESPACE_PREFIX.$name" \
            "$library" \
            "$SRC_DIR/ImGui4NET.Backend.$name/Generated" \
            "ImGui_Impl$name" \
            "ImGui_Impl${name}_" \
            $opaque
    done
fi

# ---------------------------------------------------------------------------
# Native libraries
# ---------------------------------------------------------------------------

# Every backend build log goes here. Kept out of src/ so nothing stray ends up
# inside a packed runtimes folder. Created unconditionally: the Android and
# macOS sections write here too and can be requested on their own.
BUILD_LOG_DIR="$REPO_ROOT/build/logs"
mkdir -p "$BUILD_LOG_DIR"

if [ "$BUILD_NATIVE" = "1" ]; then
    echo "==> Building native libraries"

    CXX="${CXX:-}"
    if [ -z "$CXX" ]; then
        for candidate in g++ clang++ c++; do
            if command -v "$candidate" >/dev/null 2>&1; then CXX="$(command -v "$candidate")"; break; fi
        done
    fi
    if [ -z "$CXX" ]; then
        echo "error: no C++ compiler found (set CXX)" >&2
        exit 1
    fi
    TOOLCHAIN_PREFIX="${TOOLCHAIN_PREFIX:-$(resolve_prefix "$CXX")}"

    CORE_OUT="$SRC_DIR/ImGui4NET/runtimes/$RID/native"
    mkdir -p "$CORE_OUT"

    # -static-libgcc/-static-libstdc++ keep the shipped DLL free of MinGW
    # runtime dependencies. Backends omit them because linking the core import
    # library would otherwise duplicate libgcc_eh symbols.
    SHARED_FLAGS=(-shared -O2 -DNDEBUG -fPIC)
    BACKEND_FLAGS=(-shared -O2 -DNDEBUG -fPIC)
    case "$HOST" in
        windows)
            SHARED_FLAGS+=("-Wl,--export-all-symbols" -static-libgcc -static-libstdc++)
            # Linking the core import library alongside libgcc_eh.a duplicates
            # _Unwind_Resume; the backend only needs its own symbols exported.
            BACKEND_FLAGS+=("-Wl,--export-all-symbols" -static-libgcc -static-libstdc++
                            "-Wl,--allow-multiple-definition")
            ;;
        *)
            SHARED_FLAGS+=("-Wl,--export-dynamic")
            BACKEND_FLAGS+=("-Wl,--export-dynamic")
            ;;
    esac

    # Import library the backends link against. Kept outside the ``runtimes`` tree:
    # it is a link-time artifact, not something an application should load, and
    # packing it would ship a duplicate copy of every exported symbol. Only
    # MinGW produces one; on ELF the shared object is its own link target.
    case "$HOST" in
        windows)
            IMPLIB_DIR="$SCRIPT_DIR/../build/implib/$RID"
            mkdir -p "$IMPLIB_DIR"
            CORE_IMPLIB="$IMPLIB_DIR/libimgui.dll.a"
            (cd "$IMGUI_DIR" && "$CXX" -std=c++17 "${SHARED_FLAGS[@]}" -I. \
                "-Wl,--out-implib,$CORE_IMPLIB" \
                -o "$CORE_OUT/$LIB_NAME" \
                imgui.cpp imgui_draw.cpp imgui_tables.cpp imgui_widgets.cpp imgui_demo.cpp)
            ;;
        *)
            CORE_IMPLIB="$CORE_OUT/$LIB_NAME"
            # A soname lets the backend's DT_NEEDED record just "libimgui.so"
            # rather than an absolute path, which is what the .NET loader
            # expects to resolve from the package's runtimes folder.
            (cd "$IMGUI_DIR" && "$CXX" -std=c++17 "${SHARED_FLAGS[@]}" -I. \
                "-Wl,-soname,$LIB_NAME" \
                -o "$CORE_OUT/$LIB_NAME" \
                imgui.cpp imgui_draw.cpp imgui_tables.cpp imgui_widgets.cpp imgui_demo.cpp)

            # The bindings are generated with the LLP64 (Windows) mangling, but
            # LP64 platforms spell size_t and FILE differently. Publish the
            # Windows spellings as aliases so one binding set serves both.
            lp64_args=()
            while IFS= read -r alias; do
                [ -n "$alias" ] && lp64_args+=("$alias")
            done < <("$PYTHON" "$SCRIPT_DIR/lp64-aliases.py" \
                       --entrypoints "$SRC_DIR/ImGui4NET/Generated" \
                       --library "$CORE_OUT/$LIB_NAME" \
                       --nm "$(command -v llvm-nm || command -v nm)" 2>/dev/null)

            echo "    core -> $CORE_OUT/$LIB_NAME (${#lp64_args[@]} LP64 aliases)"
            if [ "${#lp64_args[@]}" -gt 0 ]; then
                (cd "$IMGUI_DIR" && "$CXX" -std=c++17 "${SHARED_FLAGS[@]}" -I. \
                    "-Wl,-soname,$LIB_NAME" \
                    "${lp64_args[@]}" \
                    -o "$CORE_OUT/$LIB_NAME" \
                    imgui.cpp imgui_draw.cpp imgui_tables.cpp imgui_widgets.cpp imgui_demo.cpp)
            fi
            ;;
    esac
    echo "    core -> $CORE_OUT/$LIB_NAME"

    # Platform libraries each backend needs at link time. These mirror the
    # upstream build rules in imgui's examples/ CMake files. Host-specific
    # arms must come first: ``*:X`` would otherwise shadow ``windows:X``.
    platform_libs() {
        case "$HOST:$1" in
            windows:OpenGL2|windows:OpenGL3) echo "-lopengl32" ;;
            windows:Glut)   echo "-lopengl32 -lfreeglut" ;;
            windows:Glfw)   echo "-lglfw3 -lopengl32 -lgdi32" ;;
            windows:Vulkan) echo "-lvulkan-1" ;;
            windows:DirectX9) echo "-ld3d9 -ldxguid" ;;
            windows:DirectX11) echo "-ld3d11 -ldxgi -ld3dcompiler" ;;
            windows:DirectX12) echo "-ld3d12 -ldxgi -ld3dcompiler" ;;
            windows:Win32)  echo "-ldwmapi -lgdi32 -limm32 -lole32" ;;
            *:OpenGL2|*:OpenGL3) echo "-lGL" ;;
            *:Glut)   echo "-lGL -lglut" ;;
            *:Glfw)   echo "-lglfw -lGL" ;;
            *:Sdl2)   echo "-lSDL2" ;;
            *:Sdl3|*:SdlGpu3) echo "-lSDL3" ;;
            *:Vulkan) echo "-lvulkan" ;;
            *:Allegro5) echo "-lallegro -lallegro_primitives" ;;
            *) echo "" ;;
        esac
    }

    # Extra include directories per backend (some SDKs nest their headers).
    platform_includes() {
        case "$HOST:$1" in
            windows:Sdl2) echo "-I$TOOLCHAIN_PREFIX/include/SDL2" ;;
            linux:Sdl2)   echo "-I/usr/include/SDL2" ;;
            *)            echo "" ;;
        esac
    }

    # External import libraries pulled from NuGet packages instead of the
    # toolchain. On Windows these are MSVC-built but MinGW links them fine;
    # on Linux the same packages exist in the system prefix.
    external_libs() {
        case "$HOST:$1" in
            windows:Glut)     echo "-L$EXTERNAL_LIBS_DIR/freeglut/runtimes/win-x64/native/Release -l:freeglut.lib" ;;
            windows:Allegro5) echo "-L$EXTERNAL_LIBS_DIR/allegro/build/native/v143/x64/lib -l:allegro.lib -l:allegro_primitives.lib" ;;
            linux:Glut)       echo "-lGL -lglut" ;;
            linux:Allegro5)   echo "-lallegro -lallegro_primitives" ;;
            *)                echo "" ;;
        esac
    }

    external_includes() {
        case "$HOST:$1" in
            windows:Glut)     echo "-I$EXTERNAL_LIBS_DIR/freeglut/sources" ;;
            windows:Allegro5) echo "-I$EXTERNAL_LIBS_DIR/allegro/build/native/include" ;;
            *)                echo "" ;;
        esac
    }

    # Runtime DLLs the backend links against must ship next to our library.
    # Linux resolves shared libraries through the system loader, so only
    # Windows needs the DLLs copied.
    external_runtime_copies() {
        case "$HOST:$1" in
            windows:Glut)
                echo "$EXTERNAL_LIBS_DIR/freeglut/runtimes/win-x64/native/Release/freeglut.dll"
                ;;
            windows:Allegro5)
                local dir="$EXTERNAL_LIBS_DIR/allegro/build/native/v143/x64/bin"
                if [ -d "$dir" ]; then
                    # Dear ImGui's Allegro5 backend needs the core and the
                    # primitives addon.
                    echo "$dir/allegro-5.2.dll"
                    echo "$dir/allegro_primitives-5.2.dll"
                fi
                ;;
            *) echo "" ;;
        esac
    }

    # Returns non-zero when the backend cannot be linked on this host because
    # its SDK is missing. Checked before building so an absent optional SDK
    # is reported as "skipped" rather than a hard failure.
    backend_available() {
        case "$HOST:$1" in
            linux:Sdl3|linux:SdlGpu3)
                pkg-config --exists sdl3 2>/dev/null || return 1
                ;;
        esac
        return 0
    }

    for entry in "${BACKENDS[@]}"; do
        IFS='|' read -r name header _ _ _ hosts <<<"$entry"
        source="$IMGUI_DIR/backends/${header%.h}.cpp"
        [ -f "$source" ] || continue

        # Skip backends whose platform dependencies do not exist on this host.
        if [ -n "$hosts" ]; then
            case ",$hosts," in
                *",$HOST,"*) ;;
                *) echo "    $name skipped ($hosts only)"; continue ;;
            esac
        fi

        if ! backend_available "$name"; then
            echo "    $name skipped (SDK not found)"
            continue
        fi

        backend_out="$SRC_DIR/ImGui4NET.Backend.$name/runtimes/$RID/native"
        mkdir -p "$backend_out"
        backend_lib="imgui_backend_$(echo "$name" | tr '[:upper:]' '[:lower:]')"
        if [ "$HOST" = "windows" ]; then backend_lib="$backend_lib.dll"; else backend_lib="lib$backend_lib.so"; fi

        # Build logs go to a scratch dir: writing them next to the output would
        # leave stray files inside the packed runtimes tree.
        build_log="$BUILD_LOG_DIR/$name.log"

        # shellcheck disable=SC2046
        (cd "$IMGUI_DIR" && "$CXX" -std=c++17 "${BACKEND_FLAGS[@]}" -I. \
                    $(platform_includes "$name") $(external_includes "$name") \
            -o "$backend_out/$backend_lib" \
            "backends/${header%.h}.cpp" \
                    "-L$CORE_OUT" -limgui $(platform_libs "$name") $(external_libs "$name")) 2>"$build_log" || {
                echo "    $name backend FAILED (see $build_log)"
                continue
            }
        rm -f "$build_log"
        echo "    $name backend"

        # Copy the third-party runtime DLLs the backend depends on.
        for dll in $(external_runtime_copies "$name"); do
            [ -f "$dll" ] && cp -f "$dll" "$backend_out/"
        done
    done
fi

# ---------------------------------------------------------------------------
# macOS (osx-x64, osx-arm64)
# ---------------------------------------------------------------------------
#
# Cross-built with the same clang that drives the bindings plus ld64.lld. The
# interesting part is the SDK: a Mach-O link needs Apple's headers, the libc++
# headers and the .tbd stubs, none of which are part of the host toolchain.
# tools/fetch-macos-sdk.sh downloads the republished SDK tarball.
#
# Three platform specifics are handled below:
#
#   * LP64 ABI. The bindings carry the Windows (LLP64) mangling, where size_t
#     mangles to 'y'. On Darwin it mangles to 'm' and FILE becomes __sFILE, so
#     the alias table differs per architecture. ld64's -alias cannot bind to a
#     symbol defined by an object file, hence the generated assembly file.
#   * va_list. x86_64 spells it P13__va_list_tag, arm64 spells it Pc. Both are
#     already covered by tools/lp64-aliases.py.
#   * install_name. Backends sit next to the core library in the same runtimes
#     folder, so @loader_path resolves it regardless of where the package is
#     unpacked.

if [ "$BUILD_MACOS" = "1" ]; then
    echo "==> Building macOS native libraries"

    MACOS_SDK_DIR="${MACOS_SDK_DIR:-$MACOS_SDK_ROOT/$MACOS_SDK_VERSION/MacOSX$MACOS_SDK_VERSION.sdk}"
    if [ ! -d "$MACOS_SDK_DIR/usr/include" ]; then
        echo "    SDK not found at $MACOS_SDK_DIR"
        echo "    fetching it now (set MACOS_SDK_DIR to reuse an existing SDK)"
        # Re-invoke with the shell that is already running this script. A bare
        # "bash" would resolve through PATH, where Windows' WSL bash.exe comes
        # first and cannot open an MSYS path such as /d/repo/tools/x.sh.
        "${BASH:-/bin/bash}" "$SCRIPT_DIR/fetch-macos-sdk.sh" \
            "$MACOS_SDK_ROOT/$MACOS_SDK_VERSION"
    fi
    if [ ! -d "$MACOS_SDK_DIR/System/Library/Frameworks" ]; then
        echo "error: $MACOS_SDK_DIR is not a usable macOS SDK" >&2
        exit 1
    fi

    MACOS_CXX="${MACOS_CXX:-}"
    if [ -z "$MACOS_CXX" ]; then
        for candidate in clang clang++ c++; do
            if command -v "$candidate" >/dev/null 2>&1; then
                MACOS_CXX="$(command -v "$candidate")"; break
            fi
        done
    fi
    if [ -z "$MACOS_CXX" ]; then
        echo "error: no clang found for the macOS build (set MACOS_CXX)" >&2
        exit 1
    fi

    # ld64.lld ships with clang on MSYS2 but is not on PATH under its own name
    # everywhere, so accept an explicit override.
    MACOS_LD="${MACOS_LD:-}"
    if [ -z "$MACOS_LD" ]; then
        for candidate in ld64.lld ld64.lld.exe; do
            if command -v "$candidate" >/dev/null 2>&1; then
                MACOS_LD="$(command -v "$candidate")"; break
            fi
            local_dir="$(dirname "$MACOS_CXX")"
            if [ -x "$local_dir/$candidate" ]; then MACOS_LD="$local_dir/$candidate"; break; fi
        done
    fi
    if [ -z "$MACOS_LD" ]; then
        cat >&2 <<'EOF'
error: ld64.lld not found.

Install it next to clang (MSYS2: pacman -S mingw-w64-x86_64-lld) or set
MACOS_LD to the executable.
EOF
        exit 1
    fi

    MACOS_FRAMEWORKS="$MACOS_SDK_DIR/System/Library/Frameworks"
    MACOS_SDK_LIB="$MACOS_SDK_DIR/usr/lib"

    echo "    sdk=$MACOS_SDK_DIR"
    echo "    cxx=$MACOS_CXX ld=$MACOS_LD min=$MACOS_MIN_VERSION"

    # Mach-O counterparts of the ELF ``-Wl,--export-dynamic`` used above: every
    # symbol the bindings reference must stay in the dynamic table.
    macos_arch_triple() {
        case "$1" in
            x64)   echo "x86_64-apple-darwin" ;;
            arm64) echo "arm64-apple-darwin" ;;
            *)     echo "error: unknown macOS arch $1" >&2; exit 1 ;;
        esac
    }

    macos_arch_ld() {
        case "$1" in
            x64)   echo "x86_64" ;;
            arm64) echo "arm64" ;;
        esac
    }

    # Frameworks each backend needs to link against, mirroring the upstream
    # examples/ CMake rules.
    macos_backend_frameworks() {
        case "$1" in
            OpenGL2|OpenGL3) echo "-framework OpenGL" ;;
            Glut)            echo "-framework OpenGL -framework GLUT -framework Cocoa" ;;
            *)               echo "" ;;
        esac
    }

    # Objective-C++ compiler flags. -fobjc-arc is what the upstream examples
    # use, and the .mm sources rely on ARC for their object lifetime handling.
    macos_objc_flags=(-fobjc-arc)

    for arch in $MACOS_ARCHES; do
        case "$arch" in
            x64)   MACOS_RID="osx-x64" ;;
            arm64) MACOS_RID="osx-arm64" ;;
            *) echo "error: unknown MACOS_ARCHES entry '$arch'" >&2; exit 1 ;;
        esac

        triple="$(macos_arch_triple "$arch")"
        ld_arch="$(macos_arch_ld "$arch")"
        core_out="$SRC_DIR/ImGui4NET/runtimes/$MACOS_RID/native"
        mkdir -p "$core_out"
        core_lib="$core_out/libimgui.dylib"
        obj_dir="$REPO_ROOT/build/macos-obj/$arch"
        mkdir -p "$obj_dir"
        log="$BUILD_LOG_DIR/macos-$arch-core.log"

        echo "    [$arch] core"

        # -mmacosx-version-min stamps the objects with the deployment target so
        # the linker does not warn about them being newer than the platform
        # version. MACOSX_DEPLOYMENT_TARGET would not reach the ld64.lld call.
        # The min-version flag is not part of macos_cflags because Objective-C++
        # backends may need a newer one (Metal4: 26.0).
        macos_cflags=(-target "$triple" -isysroot "$MACOS_SDK_DIR"
                      -std=c++17 -O2 -DNDEBUG -fPIC)

        core_objs=()
        for unit in imgui.cpp imgui_draw.cpp imgui_tables.cpp imgui_widgets.cpp imgui_demo.cpp; do
            obj="$obj_dir/${unit%.cpp}.o"
            core_objs+=("$obj")
            (cd "$IMGUI_DIR" && "$MACOS_CXX" "${macos_cflags[@]}" \
                -mmacosx-version-min="$MACOS_MIN_VERSION" -I. \
                -c "$unit" -o "$obj") 2>>"$log" || {
                    echo "error: [$arch] $unit failed (see $log)" >&2
                    exit 1
                }
        done

        ( "$MACOS_LD" -dylib -arch "$ld_arch" \
            -platform_version macos "$MACOS_MIN_VERSION" "$MACOS_MIN_VERSION" \
            -install_name '@loader_path/libimgui.dylib' \
            -L "$MACOS_SDK_LIB" -lSystem -lc++ -lc++abi \
            -o "$core_lib" "${core_objs[@]}" ) 2>>"$log" || {
                echo "error: [$arch] core link failed (see $log)" >&2
                exit 1
            }

        # Publish the LLP64 spellings the bindings use as aliases of the real
        # LP64 symbols. This is a single pass: the alias set is fixed by the
        # object files, and adding aliases cannot change which symbols are
        # missing.
        aliases_src="$obj_dir/lp64-aliases.s"
        aliases_obj="$obj_dir/lp64-aliases.o"
        alias_count=0

        "$PYTHON" "$SCRIPT_DIR/lp64-aliases.py" \
            --entrypoints "$SRC_DIR/ImGui4NET/Generated" \
            --library "$core_lib" \
            --format asm --output "$aliases_src" 2>>"$log" || true

        if [ -s "$aliases_src" ] && grep -q '\.globl' "$aliases_src"; then
            "$MACOS_CXX" -target "$triple" -isysroot "$MACOS_SDK_DIR" \
                -c "$aliases_src" -o "$aliases_obj" 2>>"$log" || {
                    echo "error: [$arch] assembling LP64 aliases failed (see $log)" >&2
                    exit 1
                }

            ( "$MACOS_LD" -dylib -arch "$ld_arch" \
                -platform_version macos "$MACOS_MIN_VERSION" "$MACOS_MIN_VERSION" \
                -install_name '@loader_path/libimgui.dylib' \
                -L "$MACOS_SDK_LIB" -lSystem -lc++ -lc++abi \
                -o "$core_lib" "${core_objs[@]}" "$aliases_obj" ) 2>>"$log" || {
                    echo "error: [$arch] core relink failed (see $log)" >&2
                    exit 1
                }

            alias_count="$(grep -c '\.globl' "$aliases_src")"
        fi

        rm -f "$log"
        echo "    [$arch] core -> $core_lib ($alias_count LP64 aliases)"

        # Backends. Each links against the core dylib rather than its objects so
        # the dependency is recorded as @loader_path/libimgui.dylib.
        for entry in "${BACKENDS[@]}"; do
            IFS='|' read -r name header _ _ _ hosts <<<"$entry"
            case ",$hosts," in
                *",osx,"*) ;;
                *) continue ;;
            esac

            source="$IMGUI_DIR/backends/${header%.h}.cpp"
            [ -f "$source" ] || continue

            backend_out="$SRC_DIR/ImGui4NET.Backend.$name/runtimes/$MACOS_RID/native"
            mkdir -p "$backend_out"
            backend_lib="libimgui_backend_$(echo "$name" | tr '[:upper:]' '[:lower:]').dylib"
            backend_obj="$obj_dir/${name}.o"
            backend_log="$BUILD_LOG_DIR/macos-$arch-$name.log"

            # shellcheck disable=SC2046
            (cd "$IMGUI_DIR" && "$MACOS_CXX" "${macos_cflags[@]}" \
                -mmacosx-version-min="$MACOS_MIN_VERSION" -I. \
                -c "backends/${header%.h}.cpp" -o "$backend_obj") 2>"$backend_log" || {
                    echo "    [$arch] $name FAILED (see $backend_log)"
                    continue
                }

            # shellcheck disable=SC2046
            ( "$MACOS_LD" -dylib -arch "$ld_arch" \
                -platform_version macos "$MACOS_MIN_VERSION" "$MACOS_MIN_VERSION" \
                -install_name "@loader_path/$backend_lib" \
                -L "$MACOS_SDK_LIB" -lSystem -lc++ -lc++abi \
                -F "$MACOS_FRAMEWORKS" $(macos_backend_frameworks "$name") \
                -o "$backend_out/$backend_lib" "$backend_obj" "$core_lib" ) 2>>"$backend_log" || {
                    echo "    [$arch] $name FAILED (see $backend_log)"
                    continue
                }

            rm -f "$backend_log"
            echo "    [$arch] $name backend"
        done

        # Objective-C++ backends. They compile from .mm sources with ARC and
        # link against their own frameworks; everything else matches the C++
        # backends above.
        for entry in "${OBJC_BACKENDS[@]}"; do
            IFS='|' read -r name header frameworks _opaque entry_min entry_arches <<<"$entry"

            # Backends can be limited to the architectures they run on: Metal4
            # requires Apple Silicon and is therefore arm64-only.
            case ",${entry_arches:-x64,arm64}," in
                *",$arch,"*) ;;
                *) continue ;;
            esac

            source="$IMGUI_DIR/backends/${header%.h}.mm"
            [ -f "$source" ] || continue

            backend_min="${entry_min:-$MACOS_MIN_VERSION}"

            backend_out="$SRC_DIR/ImGui4NET.Backend.$name/runtimes/$MACOS_RID/native"
            mkdir -p "$backend_out"
            backend_lib="libimgui_backend_$(echo "$name" | tr '[:upper:]' '[:lower:]').dylib"
            backend_obj="$obj_dir/${name}.o"
            backend_log="$BUILD_LOG_DIR/macos-$arch-$name.log"

            # The deployment target is per backend: a backend that binds
            # API_AVAILABLE(macos(26.0)) symbols has to record 26.0 in
            # LC_BUILD_VERSION, or the dynamic loader would accept it on an
            # older system and then fail on the first symbol lookup.
            # shellcheck disable=SC2086
            (cd "$IMGUI_DIR" && "$MACOS_CXX" "${macos_cflags[@]}" \
                -mmacosx-version-min="$backend_min" \
                "${macos_objc_flags[@]}" -I. \
                -c "backends/${header%.h}.mm" -o "$backend_obj") 2>"$backend_log" || {
                    echo "    [$arch] $name FAILED (see $backend_log)"
                    continue
                }

            # shellcheck disable=SC2046,SC2086
            ( "$MACOS_LD" -dylib -arch "$ld_arch" \
                -platform_version macos "$backend_min" "$backend_min" \
                -install_name "@loader_path/$backend_lib" \
                -L "$MACOS_SDK_LIB" -lSystem -lc++ -lc++abi \
                -F "$MACOS_FRAMEWORKS" $frameworks \
                -o "$backend_out/$backend_lib" "$backend_obj" "$core_lib" ) 2>>"$backend_log" || {
                    echo "    [$arch] $name FAILED (see $backend_log)"
                    continue
                }

            rm -f "$backend_log"
            echo "    [$arch] $name backend (objective-c++, min=$backend_min)"
        done
    done

    echo "    macos done"
fi

# ---------------------------------------------------------------------------
# Android (arm64)
# ---------------------------------------------------------------------------

if [ "$BUILD_ANDROID" = "1" ]; then
    echo "==> Building Android arm64 native libraries"

    # NDK discovery, in order of precedence:
    #   1. ANDROID_NDK               explicit path to an NDK root
    #   2. ANDROID_NDK_HOME          same, conventional CI variable
    #   3. <sdk>/ndk/<version>       highest version under the SDK's ndk/ folder
    #   4. NDK_ROOT                  legacy name
    # The SDK itself is located via ANDROID_SDK_ROOT, then ANDROID_HOME.
    find_ndk() {
        local candidate

        for candidate in "${ANDROID_NDK:-}" "${ANDROID_NDK_HOME:-}" "${NDK_ROOT:-}"; do
            if [ -n "$candidate" ] && [ -d "$candidate" ]; then
                echo "$candidate"
                return
            fi
        done

        local sdk
        for sdk in "${ANDROID_SDK_ROOT:-}" "${ANDROID_HOME:-}"; do
            [ -n "$sdk" ] && [ -d "$sdk/ndk" ] || continue
            # Entries may be symlinks (the SDK usually links a versioned name to
            # the unpacked NDK), so `find -type d` would skip them. Test each
            # entry with -d instead, and version-sort to pick the newest.
            candidate="$(
                for entry in "$sdk"/ndk/*; do
                    [ -d "$entry" ] && basename "$entry"
                done | sort -V | tail -1
            )"
            if [ -n "$candidate" ]; then
                echo "$sdk/ndk/$candidate"
                return
            fi
        done
    }

    NDK="$(find_ndk)"
    if [ -z "$NDK" ]; then
        cat >&2 <<'EOF'
error: Android NDK not found.

Set one of these and retry:
  ANDROID_NDK       path to the NDK root, e.g. /opt/android-ndk-r27c
  ANDROID_NDK_HOME  same
  ANDROID_SDK_ROOT  SDK root containing an ndk/<version> folder
  ANDROID_HOME      same
EOF
        exit 1
    fi

    # API 24 is the minimum that ships libvulkan.so. Building everything against
    # one level keeps the core library compatible with the Vulkan backend.
    ANDROID_API="${ANDROID_API:-24}"
    ANDROID_CXX=""
    for host_tag in windows-x86_64 linux-x86_64 darwin-x86_64; do
        for suffix in "" ".cmd"; do
            candidate="$NDK/toolchains/llvm/prebuilt/$host_tag/bin/aarch64-linux-android${ANDROID_API}-clang++${suffix}"
            if [ -f "$candidate" ]; then ANDROID_CXX="$candidate"; break 2; fi
        done
    done
    if [ -z "$ANDROID_CXX" ]; then
        echo "error: aarch64 Android clang++ (API $ANDROID_API) not found under $NDK" >&2
        exit 1
    fi
    ANDROID_NM="$(dirname "$ANDROID_CXX")/llvm-nm"

    ANDROID_RID="android-arm64"
    ANDROID_CORE="$SRC_DIR/ImGui4NET/runtimes/$ANDROID_RID/native"
    mkdir -p "$ANDROID_CORE"

    echo "    ndk=$NDK api=$ANDROID_API"
    (cd "$IMGUI_DIR" && "$ANDROID_CXX" -std=c++17 -shared -O2 -DNDEBUG -fPIC -I. \
        -o "$ANDROID_CORE/libimgui.so" \
        imgui.cpp imgui_draw.cpp imgui_tables.cpp imgui_widgets.cpp imgui_demo.cpp) \
        2>"$BUILD_LOG_DIR/android-core.log"
    if [ ! -f "$ANDROID_CORE/libimgui.so" ]; then
        echo "error: Android core build failed (see $BUILD_LOG_DIR/android-core.log)" >&2
        exit 1
    fi
    echo "    core -> $ANDROID_CORE/libimgui.so"

    # The bindings are generated for the LLP64 (Windows) mangling. On LP64
    # platforms size_t and FILE differ, so publish aliases for the few symbols
    # whose spelling changed. See tools/lp64-aliases.py.
    DEFSYM_ARGS=()
    while IFS= read -r alias; do
        [ -n "$alias" ] && DEFSYM_ARGS+=("$alias")
    done < <("$PYTHON" "$SCRIPT_DIR/lp64-aliases.py" \
               --entrypoints "$SRC_DIR/ImGui4NET/Generated" \
               --library "$ANDROID_CORE/libimgui.so" \
               --nm "$ANDROID_NM" 2>/dev/null)

        echo "    computed ${#DEFSYM_ARGS[@]} LP64 aliases"
        if [ "${#DEFSYM_ARGS[@]}" -gt 0 ]; then
        (cd "$IMGUI_DIR" && "$ANDROID_CXX" -std=c++17 -shared -O2 -DNDEBUG -fPIC -I. \
            -o "$ANDROID_CORE/libimgui.so" \
            "${DEFSYM_ARGS[@]}" \
            imgui.cpp imgui_draw.cpp imgui_tables.cpp imgui_widgets.cpp imgui_demo.cpp)
            echo "    applied LP64 aliases"
        fi

    # Android link flags per backend. The NDK ships GLES, not desktop GL, hence
    # GLESv2/EGL for OpenGL3. Vulkan exists from API 24, which ANDROID_API
    # already guarantees.
    android_libs() {
        case "$1" in
            OpenGL3) echo "-lGLESv2 -lEGL" ;;
            Vulkan)  echo "-lvulkan" ;;
            Android) echo "-landroid -llog" ;;
            *)       echo "" ;;
        esac
    }

    for entry in "${BACKENDS[@]}"; do
        IFS='|' read -r name header _ _ _ hosts <<<"$entry"
        case ",$hosts," in
            *",android,"*) ;;
            *) continue ;;
        esac

        source="$IMGUI_DIR/backends/${header%.h}.cpp"
        [ -f "$source" ] || continue

        out="$SRC_DIR/ImGui4NET.Backend.$name/runtimes/$ANDROID_RID/native"
        mkdir -p "$out"
        lib="libimgui_backend_$(echo "$name" | tr '[:upper:]' '[:lower:]').so"
        log="$BUILD_LOG_DIR/android-$name.log"

        # shellcheck disable=SC2046
        (cd "$IMGUI_DIR" && "$ANDROID_CXX" -std=c++17 -shared -O2 -DNDEBUG -fPIC -I. \
            -o "$out/$lib" "backends/${header%.h}.cpp" \
            "-L$ANDROID_CORE" -limgui $(android_libs "$name")) 2>"$log" || {
                echo "    $name FAILED (see $log)"
                continue
            }
        rm -f "$log"
        echo "    $name backend"
    done

    echo "    android done"
fi

echo "==> Done"
