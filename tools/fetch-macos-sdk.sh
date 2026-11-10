#!/usr/bin/env bash
# Fetch the macOS SDK needed to cross-build the native libraries.
#
# Building a Mach-O dylib needs three things the Windows/Linux toolchain does
# not already provide: the System framework headers, the libc++ headers, and
# the .tbd stubs that stand in for the real shared libraries at link time.
# joseluisq/macosx-sdks republishes Apple's SDK tarballs, which is where they
# are downloaded from.
#
# The SDK version and the deployment target are independent: an SDK 26 build
# with MACOS_MIN_VERSION=11.0 records minos=11.0 in LC_BUILD_VERSION, so the
# resulting dylib still loads on macOS 11. Only the `sdk` field records 26.0,
# and nothing consults it at run time. See docs/build.md.
#
# Usage:
#   ./tools/fetch-macos-sdk.sh [dir]
#
# Produces:
#   <dir>/MacOSX<version>.sdk/...
#
# Defaults to third_party/macos-sdk/<version>/ next to this script. The
# directory is git-ignored: the SDK is ~1.4 GB once unpacked.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

VERSION="${MACOS_SDK_VERSION:-26.0}"
DEST_ROOT="${1:-$REPO_ROOT/third_party/macos-sdk/$VERSION}"
SDK_NAME="MacOSX$VERSION.sdk"
SDK_DIR="$DEST_ROOT/$SDK_NAME"

URL="${MACOS_SDK_URL:-https://github.com/joseluisq/macosx-sdks/releases/download/$VERSION/MacOSX$VERSION.sdk.tar.xz}"

if [ -d "$SDK_DIR/usr/include" ] && [ -d "$SDK_DIR/System/Library/Frameworks" ]; then
    echo "==> macOS SDK $VERSION already present"
    echo "    $SDK_DIR"
    exit 0
fi

if [ -z "${PYTHON:-}" ]; then
    for candidate in python3 python; do
        if command -v "$candidate" >/dev/null 2>&1; then
            PYTHON="$candidate"
            break
        fi
    done
fi
if [ -z "${PYTHON:-}" ]; then
    echo "error: python3 not found (needed to unpack the SDK)" >&2
    exit 1
fi

echo "==> Fetching macOS SDK $VERSION"
echo "    $URL"

mkdir -p "$DEST_ROOT"

# Download to a scratch file so an interrupted transfer cannot leave a
# half-written archive that the "already present" check would accept.
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
ARCHIVE="$TMP_DIR/sdk.tar.xz"

if command -v curl >/dev/null 2>&1; then
    curl -fL --retry 3 --progress-bar "$URL" -o "$ARCHIVE"
elif command -v wget >/dev/null 2>&1; then
    wget -q --show-progress "$URL" -O "$ARCHIVE"
else
    echo "error: neither curl nor wget is available" >&2
    exit 1
fi

echo "==> Unpacking into $DEST_ROOT"

# tar and 7z are both unusable here on Windows: an SDK tarball is mostly
# symbolic links, and creating one needs Developer Mode or an elevated shell.
# tools/extract-macos-sdk.py materialises each link as a copy instead, which is
# what the compiler and linker need anyway.
"$PYTHON" "$SCRIPT_DIR/extract-macos-sdk.py" "$ARCHIVE" "$DEST_ROOT"

if [ ! -d "$SDK_DIR/usr/include" ]; then
    echo "error: $SDK_DIR does not look like an SDK (no usr/include)" >&2
    exit 1
fi
if [ ! -d "$SDK_DIR/System/Library/Frameworks/OpenGL.framework/Headers" ]; then
    echo "error: framework headers missing from $SDK_DIR" >&2
    exit 1
fi

echo "    -> $SDK_DIR"
echo "==> Done"