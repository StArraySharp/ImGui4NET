#!/usr/bin/env python3
"""Generate linker aliases for LP64 size_t mangling.

The bindings are generated with a Windows target triple, where ``size_t`` is
``unsigned long long`` and mangles to ``y``. Android, Linux and macOS use the
LP64 model, where ``size_t`` is ``unsigned long`` and mangles to ``m``. The C
runtime's ``FILE`` type also differs: ``_iobuf`` under MSVC, ``__sFILE``
under bionic/glibc/Darwin.

Rather than maintain a second set of bindings, the native build publishes both
spellings. ELF (see ``--format linker``) uses a ``--defsym`` argument:

    -Wl,--defsym=_ZN5ImGui8MemAllocEy=_ZN5ImGui8MemAllocEm

Mach-O has no equivalent linker switch that resolves against an object-file
symbol, so ``--format asm`` emits an assembly file the toolchain assembles:

    .globl __ZN5ImGui8MemAllocEy
    .set __ZN5ImGui8MemAllocEy, __ZN5ImGui8MemAllocEm

This script diffs the entry points the bindings expect against the symbols a
built library actually exports, and prints the aliases needed.

Usage:
    python3 tools/lp64-aliases.py --entrypoints <dir> --library <lib.so>
    python3 tools/lp64-aliases.py --entrypoints <dir> --library <lib.dylib> \\
        --format asm --output aliases.s
"""

from __future__ import annotations

import argparse
import re
import struct
import subprocess
import sys
from pathlib import Path

ENTRY_POINT = re.compile(r'EntryPoint = "(_Z[^"]+)"')

MH_MAGIC_64 = 0xFEEDFACF
LC_SYMTAB = 0x2
N_EXT = 0x01
N_TYPE = 0x0E
N_SECT = 0x0E


def binding_entrypoints(source_dir: Path) -> set[str]:
    symbols: set[str] = set()
    for path in source_dir.rglob("*.cs"):
        symbols.update(ENTRY_POINT.findall(path.read_text(encoding="utf-8")))
    return symbols


def library_symbols(library: Path, nm: str) -> set[str]:
    """Return the global defined symbols a built library exports.

    Mach-O libraries are parsed directly: llvm-nm is not part of the MSYS2
    clang package, and the GNU nm bundled there only understands PE/ELF.
    """
    if library.suffix == ".dylib":
        return _macho_symbols(library)

    out = subprocess.run(
        [nm, "--dynamic", "--defined-only", str(library)],
        capture_output=True,
        text=True,
        check=True,
    ).stdout
    symbols: set[str] = set()
    for line in out.splitlines():
        parts = line.split()
        if parts and parts[-1].startswith("_Z"):
            symbols.add(parts[-1])
    return symbols


def _macho_symbols(library: Path) -> set[str]:
    data = library.read_bytes()
    magic = struct.unpack_from("<I", data, 0)[0]
    if magic != MH_MAGIC_64:
        raise SystemExit(f"error: {library} is not a 64-bit Mach-O file")

    ncmds = struct.unpack_from("<I", data, 16)[0]
    symoff = nsyms = stroff = 0
    offset = 32
    for _ in range(ncmds):
        cmd, cmdsize = struct.unpack_from("<II", data, offset)
        if cmd == LC_SYMTAB:
            symoff, nsyms, stroff, _ = struct.unpack_from("<IIII", data, offset + 8)
        offset += cmdsize

    symbols: set[str] = set()
    for index in range(nsyms):
        entry = symoff + index * 16
        strx, n_type, _, _, _ = struct.unpack_from("<IBBHQ", data, entry)
        if strx == 0:
            continue
        # Only external symbols defined in a section are exported. Undefined
        # imports and local (static) symbols would produce bogus aliases.
        if not (n_type & N_EXT) or (n_type & N_TYPE) != N_SECT:
            continue
        end = data.index(b"\0", stroff + strx)
        name = data[stroff + strx:end].decode("utf-8", "replace")
        # Mach-O prefixes every C symbol with an underscore.
        if name.startswith("_"):
            name = name[1:]
        if name.startswith("_Z"):
            symbols.add(name)
    return symbols


def _parse_nested(symbol: str, index: int) -> int:
    """Walk an ``N...E`` nested-name production and return the index past it.

    Both components bring their own structure:

    * ``<source-name>`` -- a length-prefixed identifier (``12MemoryEditor``)
    * ``C1`` / ``C2`` / ``D1`` / ``D2`` / ``C3`` -- constructors and
      destructors, which carry no length prefix but *are* followed by their own
      parameter list inside the nested name (``C1Ev``)

    The constructor case is why a plain bracket counter is not enough: the ``E``
    that terminates ``C1Ev`` looks exactly like the ``E`` closing the class
    name, and treating it as the latter shifted the whole parameter list into
    the name.
    """
    # Skip the leading 'N'.
    index += 1
    while index < len(symbol) and symbol[index] != "E":
        char = symbol[index]
        if char.isdigit():
            start = index
            while index < len(symbol) and symbol[index].isdigit():
                index += 1
            index += int(symbol[start:index])
        elif char == "C" and symbol[index : index + 2] in ("C1", "C2", "C3"):
            # Constructor: C1/C2/C3 followed by its own parameter list, then
            # the 'E' that ends this constructor's nested scope.
            index += 2
            while index < len(symbol) and symbol[index] != "E":
                index += 1
            if index < len(symbol):
                index += 1
        elif char == "D" and symbol[index : index + 2] in ("D0", "D1", "D2"):
            # Destructor: D0/D1/D2, no parameters of its own.
            index += 2
        elif char == "N":
            index = _parse_nested(symbol, index)
        else:
            index += 1
    # Step past the closing 'E' only when we actually found one: a nested name
    # that ended because the string ran out must not advance further.
    return index + 1 if index < len(symbol) else index


def split_mangled(symbol: str) -> tuple[str, str | None]:
    """Split an Itanium-encoded symbol into (name, parameter-type-list).

    Returns ``(symbol, None)`` when the encoding cannot be split, in which case
    the caller leaves the symbol unchanged.
    """
    if not symbol.startswith("_Z"):
        return symbol, None

    if symbol.startswith("_ZN"):
        index = _parse_nested(symbol, 2)
        if index <= 0 or index > len(symbol):
            return symbol, None
        return symbol[:index], symbol[index:]

    index = 2
    digits_start = index
    while index < len(symbol) and symbol[index].isdigit():
        index += 1
    if index == digits_start:
        return symbol, None

    name_length = int(symbol[digits_start:index])
    name_end = index + name_length
    if name_end > len(symbol):
        return symbol, None
    return symbol[:name_end], symbol[name_end:]


def mangle_candidates(symbol: str) -> list[str]:
    """Yield plausible LP64 spellings of ``symbol``, most likely first.

    A global ``y`` -> ``m`` swap is wrong because ``y`` also occurs inside
    ordinary name characters (``ImStrdupcpy``), so only the parameter-type
    suffix is rewritten.

    ``va_list`` differs by platform even within LP64:

    * aarch64 Android / aarch64 Linux: ``St9__va_list`` (a struct)
    * x86_64 Linux: ``P13__va_list_tag`` (a pointer to a tag struct)
    * Windows: ``Pc`` or the ``S_`` substitution
    """
    head, params = split_mangled(symbol)
    if params is None:
        return []

    candidates: list[str] = []

    def emit(*rewrites: tuple[str, str]) -> None:
        text = params
        for old, new in rewrites:
            text = text.replace(old, new)
        candidates.append(head + text)

    file_types = [("6_iobuf", "7__sFILE"), ("6_iobuf", "8_IO_FILE")]
    sizes = [("y", "m")]
    va_variants = [
        [("Pc", "St9__va_list")],
        [("S_", "St9__va_list")],
        [("Pc", "P13__va_list_tag")],
        [("S_", "P13__va_list_tag")],
    ]

    # Most specific candidates first so the best match is picked when several
    # spellings happen to exist.
    for va in va_variants:
        for ftype in file_types:
            emit(ftype, *sizes, *va)
        emit(*sizes, *va)
        for ftype in file_types:
            emit(ftype, *sizes)
        emit(*sizes)
    for ftype in file_types:
        for va in va_variants:
            emit(ftype, *va)
    emit()

    return list(dict.fromkeys(candidates))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--entrypoints", type=Path, required=True,
                        help="directory with generated .cs files")
    parser.add_argument("--library", type=Path, required=True,
                        help="built .so/.dylib to inspect")
    parser.add_argument("--nm", default="llvm-nm",
                        help="nm-compatible tool (default: llvm-nm)")
    parser.add_argument("--format", choices=["linker", "list", "asm"], default="linker",
                        help="'linker' prints -Wl,--defsym=..., 'list' prints pairs, "
                             "'asm' prints a Mach-O assembly file")
    parser.add_argument("--output", type=Path,
                        help="write the result to this file instead of stdout")
    args = parser.parse_args(argv)

    expected = binding_entrypoints(args.entrypoints)
    available = library_symbols(args.library, args.nm)

    aliases: list[tuple[str, str]] = []
    unresolved: list[str] = []

    for symbol in sorted(expected - available):
        for candidate in mangle_candidates(symbol):
            if candidate in available:
                aliases.append((symbol, candidate))
                break
        else:
            unresolved.append(symbol)

    lines: list[str] = []
    if args.format == "linker":
        lines.extend(f"-Wl,--defsym={alias}={target}" for alias, target in aliases)
    elif args.format == "asm":
        # ld64's -alias cannot bind to a symbol defined by an object file, so the
        # alias is established by the assembler instead. Mach-O prefixes C
        # symbols with an underscore, hence the leading _ on both names.
        lines.append("\t.section __TEXT,__text,regular,pure_instructions")
        for alias, target in aliases:
            lines.append(f"\t.globl _{alias}")
            lines.append(f"\t.set _{alias}, _{target}")
    else:
        lines.extend(f"{alias} {target}" for alias, target in aliases)

    text = "\n".join(lines) + ("\n" if lines else "")
    if args.output:
        args.output.write_text(text, encoding="ascii")
    else:
        sys.stdout.write(text)

    for symbol in unresolved:
        print(f"warning: no LP64 equivalent for {symbol}", file=sys.stderr)

    return 0 if not unresolved else 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
