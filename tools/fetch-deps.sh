#!/usr/bin/env bash
# Fetch the third-party native libraries that the Allegro5 and GLUT backends
# need. Both are only published as NuGet packages, so they are downloaded and
# unpacked into third_party/libs/<name>/ instead of being vendored by hand.
#
# Usage:
#   ./tools/fetch-deps.sh
#
# Produces:
#   third_party/libs/freeglut/{sources/GL/..., runtimes/win-x64/native/Release/...}
#   third_party/libs/allegro/{build/native/{include,lib,bin}, ...}

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LIBS_DIR="$REPO_ROOT/third_party/libs"

FREEGLUT_ID="freeglut.3.2.2.v140"
FREEGLUT_VERSION="1.0.0"
ALLEGRO_ID="Allegro"
ALLEGRO_VERSION="5.2.11.3"

fetch() {
    local id="$1" version="$2" target="$3"
    local lower
    lower="$(echo "$id" | tr '[:upper:]' '[:lower:]')"
    local url="https://api.nuget.org/v3-flatcontainer/$lower/$version/$lower.$version.nupkg"

    echo "==> $id $version"
    rm -rf "$target"
    mkdir -p "$target"

    local tmp
    tmp="$(mktemp -d)"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$url" -o "$tmp/pkg.zip"
    elif command -v wget >/dev/null 2>&1; then
        wget -q "$url" -O "$tmp/pkg.zip"
    else
        echo "error: neither curl nor wget is available" >&2
        exit 1
    fi

    # NuGet packages are zip archives. Prefer unzip, fall back to python.
    if command -v unzip >/dev/null 2>&1; then
        unzip -q "$tmp/pkg.zip" -d "$target"
    else
        "${PYTHON:-python3}" -c \
            "import sys,zipfile; zipfile.ZipFile(sys.argv[1]).extractall(sys.argv[2])" \
            "$tmp/pkg.zip" "$target"
    fi
    rm -rf "$tmp"
    echo "    -> $target"
}

mkdir -p "$LIBS_DIR"
fetch "$FREEGLUT_ID" "$FREEGLUT_VERSION" "$LIBS_DIR/freeglut"
fetch "$ALLEGRO_ID" "$ALLEGRO_VERSION" "$LIBS_DIR/allegro"

echo "==> Done"