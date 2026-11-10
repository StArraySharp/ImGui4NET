#!/usr/bin/env python3
"""Unpack a macOS SDK tarball on a platform that cannot create symlinks.

An SDK tarball is full of symbolic links: every framework mirrors its
``Versions/<name>`` tree at the top level, and many man pages are links. On
Windows without Developer Mode (or an elevated shell) neither ``tar`` nor ``7z``
can create them, and what you get instead is either an aborted extraction or a
pile of zero-length files where the headers should be.

This script reads the archive itself and turns every symbolic link into a copy
of its resolved target, which is exactly what a compiler and linker need. It
also skips ``usr/share``: around 175 MB of man pages and locale data that never
take part in a compile.

Usage:
    python tools/extract-macos-sdk.py <sdk.tar.xz> <destination-dir>

The destination is expected to be the directory that will contain
``MacOSX<version>.sdk``.
"""

from __future__ import annotations

import shutil
import sys
import tarfile
from pathlib import Path

# Subtrees no compile or link needs. The man pages are the bulk of the archive
# and their symlinks are what break naive extraction the most.
SKIP_PREFIXES = (
    "usr/share/",
    "usr/libexec/",
)

# Resolution is repeated until a pass makes no progress. Framework trees nest
# (CoreServices/Versions/A/Frameworks/AE.framework/Headers resolves only after
# the enclosing Copytree exists), so the number of passes needed is far larger
# than the depth a single link chain suggests. The cap is only a loop guard.
MAX_LINK_PASSES = 500


def _strip_root(name: str) -> str:
    return name[2:] if name.startswith("./") else name


def _relative_to_sdk(name: str) -> str:
    """Drop the leading ``MacOSX<version>.sdk/`` component, if present.

    Archive members carry the SDK root, so the skip prefixes have to be matched
    against the path *inside* the SDK.
    """
    head, sep, rest = name.partition("/")
    if sep and head.endswith(".sdk"):
        return rest
    return name


def _link_candidates(dest: Path, name: str, link_target: str) -> list[Path]:
    """Paths a link could resolve to, most likely first.

    A symlink target is relative to the link's own directory; a hard link
    target is relative to the archive root.
    """
    return [
        (dest / name).parent / link_target,
        dest / link_target,
    ]


def _resolve_current(path: Path) -> Path | None:
    """Resolve a path that runs through a framework's ``Versions/Current``.

    ``Versions/Current`` is itself a symlink to the version directory (``A``,
    ``B``, ``32023``, ...). Rewriting it explicitly avoids depending on that
    alias having been materialised already, and trying every version directory
    copes with frameworks that carry more than one.
    """
    parts = path.parts

    # A nested framework path can contain several "Versions" components, as in
    # CoreServices.framework/Versions/A/Frameworks/AE.framework/Versions/Current.
    # Only the innermost one qualifies, so scan for the last match.
    index = -1
    for position in range(len(parts) - 1):
        if parts[position] == "Versions" and parts[position + 1] == "Current":
            index = position
    if index < 0:
        return None

    versions_dir = Path(*parts[: index + 1])
    rest = parts[index + 2:]
    if not versions_dir.is_dir():
        return None

    # ``rest`` is empty when the link points at ``Versions/Current`` itself,
    # which happens for the framework-level ``Current`` symlink.
    for entry in sorted(versions_dir.iterdir()):
        if entry.is_dir() and entry.name != "Current":
            candidate = entry.joinpath(*rest) if rest else entry
            if candidate.exists():
                return candidate
    return None


def _find_source(dest: Path, name: str, link_target: str) -> Path | None:
    """Resolve one link, trying the plain target first and then ``Versions/Current``.

    ``Current`` is retried against *every* candidate spelling rather than only
    when the plain lookup missed: a ``Versions/Current/...`` path can appear to
    exist through an already-materialised ``Current`` directory while the real
    version directory holds the file.
    """
    candidates = _link_candidates(dest, name, link_target)
    for candidate in candidates:
        if candidate.exists():
            return candidate
    for candidate in candidates:
        rewritten = _resolve_current(candidate)
        if rewritten is not None and rewritten.exists():
            return rewritten
    return None


def _file_count(path: Path) -> int:
    """Number of files under *path*, used to detect a stale directory copy."""
    return sum(1 for entry in path.rglob("*") if entry.is_file())


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print("usage: extract-macos-sdk.py <sdk.tar.xz> <destination-dir>",
              file=sys.stderr)
        return 2

    archive = Path(argv[0])
    dest = Path(argv[1])
    if not archive.is_file():
        print(f"error: {archive} not found", file=sys.stderr)
        return 1
    dest.mkdir(parents=True, exist_ok=True)

    regular = 0
    directories = 0
    links: list[tuple[str, str]] = []
    skipped = 0

    # Streaming mode keeps memory flat: the uncompressed stream is ~1.4 GB.
    with tarfile.open(archive, "r|xz") as tar:
        for member in tar:
            name = _strip_root(member.name)
            if not name or name == ".":
                continue
            if _relative_to_sdk(name).startswith(SKIP_PREFIXES):
                skipped += 1
                continue

            target = dest / name

            if member.isdir():
                target.mkdir(parents=True, exist_ok=True)
                directories += 1
                continue

            if member.issym() or member.islnk():
                # Recorded now and resolved after the pass: a link may point at
                # a file that appears later in the archive.
                links.append((name, _strip_root(member.linkname)))
                continue

            if not member.isfile():
                continue

            target.parent.mkdir(parents=True, exist_ok=True)
            source = tar.extractfile(member)
            if source is None:
                continue
            with source, target.open("wb") as out:
                shutil.copyfileobj(source, out, length=1 << 20)
            try:
                target.chmod(member.mode & 0o777)
            except OSError:
                pass
            regular += 1

    print(f"    {regular} files, {directories} directories, {len(links)} links"
          f" ({skipped} skipped)", flush=True)

    # ``Versions/Current`` is not materialised. It is the alias every framework
    # uses internally (``Headers -> Versions/Current/Headers``), and copying it
    # would create a snapshot that goes stale as soon as a nested framework is
    # filled in. _find_source rewrites that component on demand instead.
    pending = {
        name: target for name, target in links
        if name.rsplit("/", 1)[-1] != "Current" and target != "Current"
    }

    # Materialise deepest paths first. A framework's top-level ``Headers`` and
    # ``Frameworks`` aliases are copies of a real tree, and ``Frameworks``
    # contains nested frameworks whose own aliases must already exist when the
    # copy is taken:
    #   .../CoreServices.framework/Versions/A/Frameworks/AE.framework/Headers
    # has to be done before .../CoreServices.framework/Frameworks is copied.
    materialised = 0
    for _ in range(MAX_LINK_PASSES):
        if not pending:
            break
        progressed = False
        for name in sorted(pending, key=lambda item: -item.count("/")):
            source = _find_source(dest, name, pending[name])
            if source is None:
                continue

            destination = dest / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            if destination.is_dir():
                shutil.rmtree(destination)
            elif destination.exists():
                destination.unlink()
            if source.is_dir():
                shutil.copytree(source, destination, symlinks=False,
                                dirs_exist_ok=True)
            else:
                shutil.copy2(source, destination)
            del pending[name]
            materialised += 1
            progressed = True
        if not progressed:
            break

    print(f"    {materialised} links materialised", flush=True)

    # Links into a skipped subtree can never resolve, and nothing in a build
    # reads them.
    for name, target in pending.items():
        if _relative_to_sdk(name).startswith(SKIP_PREFIXES):
            continue
        print(f"warning: unresolved link {name} -> {target}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))