#!/usr/bin/env bash
# Generate bindings for an Objective-C++ backend.
#
# Every other backend in this repository is plain C++ and goes through the
# generic path in generate.sh. These differ in three ways:
#
#   * Their headers declare the API only under `#ifdef __OBJC__`, so the
#     generator must parse with the objective-c++ language.
#   * Objective-C types (id<MTLDevice>, NSView*) have no C# equivalent and
#     ClangSharp emits no declaration for a class it only sees forward-declared,
#     so the opaque handles are supplied by postprocess.py.
#   * Every C symbol in a Mach-O binary carries a leading underscore in the
#     symbol table. ClangSharp copies that spelling into the EntryPoint
#     attribute, but dlsym() expects the C name, so the extra underscore is
#     stripped again.
#
# Usage:
#   ./tools/gen-objc-backend.sh <header> <namespace> <library> <output-dir> \
#       <methods-class> <method-prefix> <opaque-type>...
#
# Paths handed to the generator must be Windows paths: ClangSharp is a native
# Windows process under MSYS2 and cannot open /d/... style paths.

set -euo pipefail

if [ "$#" -lt 6 ]; then
    sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
    exit 2
fi

HEADER="$1"
NAMESPACE="$2"
LIBRARY="$3"
OUTPUT_DIR="$4"
METHODS_CLASS="$5"
METHOD_PREFIX="$6"
shift 6
OPAQUE_TYPES=("$@")

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TOOL="$SCRIPT_DIR/postprocess.py"
SDK="${MACOS_SDK_DIR:-$REPO_ROOT/third_party/macos-sdk/${MACOS_SDK_VERSION:-26.0}/MacOSX${MACOS_SDK_VERSION:-26.0}.sdk}"
CLANG_RESOURCE="${CLANG_RESOURCE:-D:/Softwares/msys2/mingw64/lib/clang/22}"

if [ ! -d "$SDK/usr/include" ]; then
    echo "error: macOS SDK not found at $SDK" >&2
    echo "       run tools/fetch-macos-sdk.sh, or set MACOS_SDK_DIR" >&2
    exit 1
fi

if [ -z "${PYTHON:-}" ]; then
    for candidate in python3 python; do
        if command -v "$candidate" >/dev/null 2>&1; then PYTHON="$candidate"; break; fi
    done
fi
if [ -z "${PYTHON:-}" ]; then
    echo "error: python3 not found" >&2
    exit 1
fi

windows_path() {
    if command -v cygpath >/dev/null 2>&1; then
        cygpath -w "$1"
    else
        printf '%s' "$1" | sed -e 's|^/\([a-zA-Z]\)/|\1:/|'
    fi
}

SDK_W="$(windows_path "$SDK")"
REPO_W="$(windows_path "$REPO_ROOT")"
HEADER_W="$(windows_path "$HEADER")"
OUTPUT_W="$(windows_path "$OUTPUT_DIR")"

SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT
RAW="$SCRATCH/raw.cs"
RAW_W="$(windows_path "$RAW")"

echo "    -> $NAMESPACE (objective-c++)"

DOTNET_ARGS=(
    -f "$HEADER_W"
    -n "Scratch.Generated"
    -o "$RAW_W"
    -l "$LIBRARY"
    --include-directory "$REPO_W/imgui"
    --include-directory "$REPO_W/imgui/backends"
    --resource-directory "$CLANG_RESOURCE"
    --with-using "*=ImGui4NET"
    --generate "helper-types"
    --generate "funcs-with-body=false"
    -std "c++17"
    -x "objective-c++"
    -a "--target=@MACOS_TRIPLE@"
    -a "-isysroot$SDK_W"
    -a "-mmacosx-version-min=@MACOS_MIN@"
    -a "-DIMGUI_DISABLE_SSE"
)

# The target triple only changes the C++ mangling, which is identical for all
# three macOS architectures here (x86_64 and arm64 agree on everything these
# two headers use); generate for x86_64 and let both RIDs share the bindings.
TRIPLE="${MACOS_GEN_TRIPLE:-x86_64-apple-darwin}"
MIN_VERSION="${MACOS_MIN_VERSION:-11.0}"
for index in "${!DOTNET_ARGS[@]}"; do
    DOTNET_ARGS[$index]="${DOTNET_ARGS[$index]//@MACOS_TRIPLE@/$TRIPLE}"
    DOTNET_ARGS[$index]="${DOTNET_ARGS[$index]//@MACOS_MIN@/$MIN_VERSION}"
done

dotnet tool run ClangSharpPInvokeGenerator -- "${DOTNET_ARGS[@]}" >"$SCRATCH/log" 2>&1 || true

# ClangSharp reports forward-declared Objective-C types as "Unsupported
# declaration" and exits non-zero even though the C functions it can bind are
# complete. Those handles are declared by postprocess.py (--objc-opaque-type)
# for exactly this reason, so the exit code is not decisive: an empty output
# file is.
if [ ! -s "$RAW" ]; then
    echo "error: generation produced no output for $HEADER" >&2
    cat "$SCRATCH/log" >&2
    exit 1
fi

POST_ARGS=(
    "$RAW"
    -d "$OUTPUT_DIR"
    -n "$NAMESPACE"
    -l "$LIBRARY"
    --methods-class "$METHODS_CLASS"
    --method-prefix "$METHOD_PREFIX"
    --macho-symbols
    --using "ImGui4NET"
)

for type in "${OPAQUE_TYPES[@]}"; do
    POST_ARGS+=(--objc-opaque-type "$type")
done

"$PYTHON" "$TOOL" "${POST_ARGS[@]}"