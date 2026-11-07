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
#   ./tools/generate.sh --all        # desktop + Android
#   ./tools/generate.sh --native --no-generate
#                                    # build libraries only, keep existing bindings
#   ./tools/generate.sh --help

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

NAMESPACE="ImGui4NET"
BACKEND_NAMESPACE_PREFIX="ImGui4NET.Backend"

BUILD_NATIVE=0
BUILD_ANDROID=0
SKIP_GENERATE=0
for arg in "$@"; do
    case "$arg" in
        --native) BUILD_NATIVE=1 ;;
        --android) BUILD_ANDROID=1 ;;
        --all) BUILD_NATIVE=1; BUILD_ANDROID=1 ;;
        # Native-only: build the libraries without regenerating bindings. Used
        # when cross-building on a machine whose target triple differs from the
        # one the checked-in bindings were generated with.
        --no-generate) SKIP_GENERATE=1 ;;
        --help|-h)
            sed -n '2,24p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
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
# imgui_club bindings
# ---------------------------------------------------------------------------
#
# The club extensions are separate C++ structs with their own translation unit
# requirements: imgui_memory_editor.h uses ImU8/ImU32/IM_ASSERT without
# including anything, so imgui.h must come first.
#
# imgui.h is deliberately NOT passed as -t: it is a dependency to parse, not
# output to generate. Listing it would duplicate every core type in this
# package and collide with ImGui4NET.
CLUB_TU="$TU_DIR/club_tu.h"
cat >"$CLUB_TU" <<'EOF'
#include "imgui.h"
#include "imgui_memory_editor.h"
#include "imgui_threaded_rendering.h"
#include "imgui_multicontext_compositor.h"
EOF

echo "==> Generating imgui_club bindings"
if [ "$SKIP_GENERATE" = "1" ]; then
    echo "    (skipped: --no-generate)"
else
    generate "$SRC_DIR/ImGui4NET.Club/Generated" "ImGui4NET.Club" "$CLUB_TU" \
        "imgui_club" "ImGuiClub" \
        -t "$CLUB_TU" \
        -t "$CLUB_DIR/imgui_memory_editor/imgui_memory_editor.h" \
        -t "$CLUB_DIR/imgui_threaded_rendering/imgui_threaded_rendering.h" \
        -t "$CLUB_DIR/imgui_multicontext_compositor/imgui_multicontext_compositor.h" \
        -a "-isystem" -a "$CLUB_DIR/imgui_memory_editor" \
        -a "-isystem" -a "$CLUB_DIR/imgui_threaded_rendering" \
        -a "-isystem" -a "$CLUB_DIR/imgui_multicontext_compositor"
fi

rm -rf "$TU_DIR"

echo "==> imgui_club note:"
echo "    MemoryEditor/ImDrawDataSnapshot/ImTextureQueue methods are defined"
echo "    inline in their headers. ClangSharp only emits externs for functions"
echo "    whose definition lives in a header it traverses, so the struct layout"
echo "    is generated here and the externs are provided by ClubNative.cs."

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
BACKENDS=(
    "OpenGL2|imgui_impl_opengl2.h|||ImGui_ImplOpenGL2_|windows,linux"
    "OpenGL3|imgui_impl_opengl3.h|||ImGui_ImplOpenGL3_|windows,linux,android"
    "DirectX9|imgui_impl_dx9.h||D3D9|ImGui_ImplDX9_|windows"
    "DirectX11|imgui_impl_dx11.h||D3D11|ImGui_ImplDX11_|windows"
    "DirectX12|imgui_impl_dx12.h|-a -isystem -a $STUB_DIR/d3d12|D3D12|ImGui_ImplDX12_|windows"
    "Vulkan|imgui_impl_vulkan.h||Vulkan|ImGui_ImplVulkanH_ ImGui_ImplVulkan_|windows,linux,android"
    "Win32|imgui_impl_win32.h|||ImGui_ImplWin32_|windows"
    "Glfw|imgui_impl_glfw.h||GLFW|ImGui_ImplGlfw_|windows,linux"
    "Sdl2|imgui_impl_sdl2.h||SDL2|ImGui_ImplSDL2_|windows,linux"
    "Sdl3|imgui_impl_sdl3.h|||ImGui_ImplSDL3_|windows,linux"
    "SdlGpu3|imgui_impl_sdlgpu3.h|-a -isystem -a $STUB_DIR -t $IMGUI_DIR/backends/imgui_impl_sdlgpu3.h -t $STUB_DIR/SDL3/SDL_gpu.h||ImGui_ImplSDLGPU3_|windows,linux"
    "Glut|imgui_impl_glut.h|||ImGui_ImplGLUT_|windows,linux"
    "Allegro5|imgui_impl_allegro5.h|||ImGui_ImplAllegro5_ ALLEGRO_|windows,linux"
    "Null|imgui_impl_null.h|||ImGui_ImplNull_|windows,linux,android"
    "Android|imgui_impl_android.h|-a -include -a stdint.h||ImGui_ImplAndroid_|android"
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
        IFS='|' read -r name header extra remap type_prefixes _rest <<<"$entry"
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
fi

# ---------------------------------------------------------------------------
# Native libraries
# ---------------------------------------------------------------------------

# Every backend build log goes here. Kept out of src/ so nothing stray ends up
# inside a packed runtimes folder.
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

    # -----------------------------------------------------------------------
    # imgui_club
    # -----------------------------------------------------------------------
    #
    # Every club method is defined inline, so nothing is emitted unless the code
    # is forced out. -fkeep-inline-functions plus tools/club-instantiate.cpp
    # guarantees the symbols ClubNative.cs binds to exist.
    #
    # -O0 matters: at -O2 the compiler inlines or drops the instantiation calls
    # in club-instantiate.cpp, leaving the constructors unemitted.
    if [ -d "$CLUB_DIR" ]; then
        club_out="$SRC_DIR/ImGui4NET.Club/runtimes/$RID/native"
        mkdir -p "$club_out"
        club_lib="imgui_club"
        if [ "$HOST" = "windows" ]; then club_lib="$club_lib.dll"; else club_lib="lib$club_lib.so"; fi

        club_src=(
            "$REPO_ROOT/tools/club-instantiate.cpp"
            "$CLUB_DIR/imgui_multicontext_compositor/imgui_multicontext_compositor.cpp"
        )
        club_inc=(
            -I"$CLUB_DIR/imgui_memory_editor"
            -I"$CLUB_DIR/imgui_threaded_rendering"
            -I"$CLUB_DIR/imgui_multicontext_compositor"
        )
        club_log="$BUILD_LOG_DIR/imgui_club.log"

        # Flat flags: no -O2 so the instantiation bodies survive.
        club_flags=(-shared -O0 -DNDEBUG -fPIC -fkeep-inline-functions)
        if [ "$HOST" = "windows" ]; then
            club_flags+=("-Wl,--export-all-symbols" -static-libgcc -static-libstdc++
                         "-Wl,--allow-multiple-definition")
        else
            club_flags+=("-Wl,--export-dynamic")
        fi

        club_aliases=()
        for attempt in 1 2 3 4; do
            (cd "$IMGUI_DIR" && "$CXX" -std=c++17 "${club_flags[@]}" \
                -I. "${club_inc[@]}" \
                -o "$club_out/$club_lib" \
                "${club_aliases[@]}" "${club_src[@]}" \
                "-L$CORE_OUT" -limgui) 2>"$club_log" || {
                    echo "    imgui_club FAILED (see $club_log)"
                    break
                }

            [ "$HOST" = "windows" ] && break

            new_aliases=()
            while IFS= read -r alias; do
                [ -n "$alias" ] && new_aliases+=("$alias")
            done < <("$PYTHON" "$SCRIPT_DIR/lp64-aliases.py" \
                       --entrypoints "$SRC_DIR/ImGui4NET.Club" \
                       --library "$club_out/$club_lib" \
                       --nm "$(command -v llvm-nm || command -v nm)" 2>/dev/null)

            if [ "${#new_aliases[@]}" -eq 0 ] || [ "${#new_aliases[@]}" -eq "${#club_aliases[@]}" ]; then
                club_aliases=("${new_aliases[@]}")
                break
            fi
            club_aliases=("${new_aliases[@]}")
        done

        [ -f "$club_out/$club_lib" ] && rm -f "$club_log" && echo "    imgui_club"
    fi
fi

echo "==> Done"

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

    # imgui_club on Android.
    # Clang has no -fkeep-inline-functions, so the inline definitions are forced
    # out by tools/club-instantiate.cpp instead. -fvisibility=default plus
    # --export-dynamic put the resulting symbols in the dynamic table where the
    # CLR can resolve them.
    if [ -d "$CLUB_DIR" ]; then
        club_out="$SRC_DIR/ImGui4NET.Club/runtimes/$ANDROID_RID/native"
        mkdir -p "$club_out"
        log="$BUILD_LOG_DIR/android-imgui_club.log"
        club_src=(
            "$REPO_ROOT/tools/club-instantiate.cpp"
            "$CLUB_DIR/imgui_multicontext_compositor/imgui_multicontext_compositor.cpp"
        )
        club_inc=(
            -I"$CLUB_DIR/imgui_memory_editor"
            -I"$CLUB_DIR/imgui_threaded_rendering"
            -I"$CLUB_DIR/imgui_multicontext_compositor"
        )

        # Iterate to a fixed point: each relink publishes the aliases for the
        # symbols present at that moment, and the next sweep may find more
        # (the alias table grows as the compiler emits more inline bodies).
        club_aliases=()
        for attempt in 1 2 3 4; do
            (cd "$IMGUI_DIR" && "$ANDROID_CXX" -std=c++17 -shared -O0 -DNDEBUG -fPIC \
                -fvisibility=default -femit-all-decls -Wl,--export-dynamic -I. "${club_inc[@]}" \
                -o "$club_out/libimgui_club.so" \
                "${club_aliases[@]}" "${club_src[@]}" \
                "-L$ANDROID_CORE" -limgui) 2>"$log" || {
                    echo "    imgui_club FAILED (see $log)"
                    break
                }

            # Recompute from the freshly linked file. Each relink can expose more
            # symbols (aliases are additive), so loop until the list is stable.
            new_aliases=()
            while IFS= read -r alias; do
                [ -n "$alias" ] && new_aliases+=("$alias")
            done < <("$PYTHON" "$SCRIPT_DIR/lp64-aliases.py" \
                       --entrypoints "$SRC_DIR/ImGui4NET.Club" \
                       --library "$club_out/libimgui_club.so" \
                       --nm "$ANDROID_NM" 2>/dev/null)

            if [ "${#new_aliases[@]}" -eq 0 ]; then
                club_aliases=()
                break
            fi
            if [ "${#new_aliases[@]}" -eq "${#club_aliases[@]}" ]; then
                club_aliases=("${new_aliases[@]}")
                break
            fi
            club_aliases=("${new_aliases[@]}")
        done

        if [ -f "$club_out/libimgui_club.so" ]; then
            rm -f "$log"
            echo "    imgui_club (${#club_aliases[@]} LP64 aliases)"
        fi
    fi

    echo "    android done"
fi

echo "==> Done"



