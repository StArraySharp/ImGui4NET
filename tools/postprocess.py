#!/usr/bin/env python3
"""Post-process ClangSharp output into compilable, idiomatic C# bindings.

Handles everything the generator cannot express for a C++ API:

  * strips trailing underscores from type names (``ImDrawFlags_`` -> ``ImDrawFlags``)
  * rewrites ``sbyte*`` string parameters to ``string`` with UTF-8 marshalling
  * drops C++ string-literal defaults (``= "%.3f"u8``) in favour of ``null``
  * turns ``static extern readonly`` into ``static extern``
  * renames constructors emitted as ``Type(Type* pThis)`` to ``Type_Ctor``
  * removes the duplicated ``NativeTypeName``/``NativeAnnotation`` helper types
  * optionally splits the output into one file per type

Run with ``--help`` for the full option list.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

# ---------------------------------------------------------------------------
# C++ template expansion
# ---------------------------------------------------------------------------

# The generator cannot express C++ class templates, so it emits them verbatim
# as invalid C#. Each template below is expanded into its equivalent field
# layout. Sizes match the native definitions exactly:
#
#   ImBitArray<N, K>      -> ImU32 Data[(N + 31) >> 5]      (see imgui_internal.h)
#   ImSpan<T>             -> T* Data; T* DataEnd;
#   ImPool<T>             -> ImVector<T> Buf; ImGuiStorage Map; int FreeIdx; int AliveCount;
#   ImChunkStream<T>      -> ImVector<char> Buf;
#   ImStableVector<T, B>  -> int Size; int Capacity; ImVector<T*> Blocks;
#
# ``ImGuiKey_NamedKey_COUNT`` is 155 and ``ImGuiKey_NamedKey_BEGIN`` is 512, so
# ``ImBitArrayForNamedKeys`` occupies (155 + 31) >> 5 == 5 uint slots.

# ``ImSmallStack<T, N>`` (added in 1.93) is:
#     T    LocalData[N];
#     ImU16 Size;
SMALLSTACK = re.compile(
    r"public ImSmallStack<(?P<t>[A-Za-z_][A-Za-z0-9_]*), (?P<cap>\d+)> (?P<n>\w+);"
)

BITARRAY_NAMED_KEYS = re.compile(
    r"\[NativeTypeName\(\"ImBitArrayForNamedKeys\"\)\]\s*"
    r"public ImBitArray<\(int\)\(ImGuiKey_NamedKey_COUNT\), -\(int\)\(ImGuiKey_NamedKey_BEGIN\)> (?P<n>\w+);"
)

TEMPLATE_EXPANSIONS: list[tuple[re.Pattern[str], str]] = [
    # ImBitArrayForNamedKeys: 5 x ImU32
    (
        BITARRAY_NAMED_KEYS,
        "public fixed uint {n}[5];",
    ),
    # ImSpan<T>
    (
        re.compile(r"public ImSpan<(?P<t>[A-Za-z_][A-Za-z0-9_]*)> (?P<n>\w+);"),
        "public {t}* {n}_Data;\n        public {t}* {n}_DataEnd;",
    ),
    # ImPool<T>
    (
        re.compile(r"public ImPool<(?P<t>[A-Za-z_][A-Za-z0-9_]*)> (?P<n>\w+);"),
        "public ImVector<{t}> {n}_Buf;\n        public ImGuiStorage {n}_Map;\n"
        "        public int {n}_FreeIdx;\n        public int {n}_AliveCount;",
    ),
    # ImChunkStream<T>
    (
        re.compile(r"public ImChunkStream<(?P<t>[A-Za-z_][A-Za-z0-9_]*)> (?P<n>\w+);"),
        "public ImVector<sbyte> {n}_Buf;",
    ),
    # ImStableVector<T, BLOCKSIZE>
    (
        re.compile(r"public ImStableVector<(?P<t>[A-Za-z_][A-Za-z0-9_]*), (?P<b>\d+)> (?P<n>\w+);"),
        "public int {n}_Size;\n        public int {n}_Capacity;\n"
        "        public ImVector<{t}*> {n}_Blocks;",
    ),
        # ImSmallStack<T, N>
        (
            SMALLSTACK,
            "public fixed {t} {n}_LocalData[{cap}];\n        public ushort {n}_Size;",
        ),
    ]


def expand_templates(text: str) -> str:
    """Replace C++ class template fields with their flattened layout."""

    def make_repl(template: str):
        def repl(match: re.Match[str]) -> str:
            groups = {k: v for k, v in match.groupdict().items() if v is not None}
            groups.setdefault("name", groups.get("n", ""))
            return template.format(**groups)

        return repl

    for pattern, template in TEMPLATE_EXPANSIONS:
        text = pattern.sub(make_repl(template), text)
    return text


# ---------------------------------------------------------------------------
# Text rewrites
# ---------------------------------------------------------------------------

# ``ImDrawFlags_`` -> ``ImDrawFlags``. Only identifiers are touched: mangled
# entry points such as ``_Z10ImFileOpenPKcS0_`` legitimately end with an
# underscore, so ``DllImport`` strings are protected before rewriting.
TRAILING_UNDERSCORE = re.compile(r"\b([A-Za-z][A-Za-z0-9]*?)_\b")

ENTRY_POINT = re.compile(r'EntryPoint = "[^"]*"')

# Parameters declared as ``sbyte*`` with a C string annotation become managed
# strings. ``const char **`` and ``va_list`` keep their pointer form.
STRING_PARAM = re.compile(
    r'\[NativeTypeName\("(?:const )?char \*"\)\]\s*sbyte\*\s+'
    r'(?P<name>@?[A-Za-z_][A-Za-z0-9_]*)'
)

# ``sbyte* x = "%.3f"u8`` -> ``string? x = null``. Dear ImGui treats a NULL
# format as "use the default", so ``null`` preserves behaviour.
STRING_LITERAL_DEFAULT = re.compile(r'=\s*"(?:[^"\\]|\\.)*"u8')

# ``ImVec2 graph_size = new ImVec2(0, 0)`` -> ``= default``. A default
# constructed ImVec2 is the same value.
VEC2_CONSTRUCTOR_DEFAULT = re.compile(r"=\s*new ImVec2\([^)]*\)")

# ``[NativeTypeName("va_list")] sbyte* x`` must stay a pointer.
VA_LIST = re.compile(r'\[NativeTypeName\("va_list"\)\]\s*sbyte\*\s+([A-Za-z_][A-Za-z0-9_]*)')

HELPER_TYPE_BLOCK = re.compile(
    r"\n    /// <summary>Defines the type of a member as it was used in the native signature\.</summary>.*?\n    \}\n"
    r"|\n    /// <summary>Defines the annotation found in a native declaration\.</summary>.*?\n    \}\n",
    re.S,
)


def strip_trailing_underscore(text: str) -> str:
    """Drop the trailing underscore ClangSharp appends to C++ enum names.

    ``DllImport`` entry points are mangled C++ symbols and may end in ``_``
    themselves, so they are masked out first.
    """
    protected: dict[str, str] = {}

    def protect(match: re.Match[str]) -> str:
        token = f"__ENTRYPOINT_{len(protected)}__"
        protected[token] = match.group(0)
        return token

    text = ENTRY_POINT.sub(protect, text)
    text = TRAILING_UNDERSCORE.sub(lambda m: m.group(1), text)

    for token, original in protected.items():
        text = text.replace(token, original)
    return text


def rewrite_string_params(text: str) -> str:
    """Convert ``sbyte*`` C-string parameters into ``string``."""
    # Protect va_list parameters first: they are declared with the same shape.
    protected: dict[str, str] = {}

    def protect(match: re.Match[str]) -> str:
        token = f"__VA_LIST_{len(protected)}__"
        protected[token] = match.group(0)
        return token

    text = VA_LIST.sub(protect, text)

    def repl(match: re.Match[str]) -> str:
        return f'[MarshalAs(UnmanagedType.LPUTF8Str)] string {match.group("name")}'

    text = STRING_PARAM.sub(repl, text)

    for token, original in protected.items():
        text = text.replace(token, original)
    return text


def drop_string_literal_defaults(text: str) -> str:
    text = STRING_LITERAL_DEFAULT.sub("= null", text)
    return VEC2_CONSTRUCTOR_DEFAULT.sub("= default", text)


# Parameters the C++ side takes by reference (``const ImVec2&``) but which
# ClangSharp emits as pointers. A reference's default argument constructs a
# temporary that is never null, and the native code dereferences it
# unconditionally, so ``= null`` makes any call that omits the argument crash
# with an access violation. C# default arguments must be compile-time
# constants, so a pointer cannot carry the zero value here: the default is
# dropped instead, forcing the caller to pass an explicit pointer.
BY_REF_DEFAULT_NAMES = {
    # ImVec2&
    "size", "size_arg", "pivot", "outer_size", "uv0", "uv1", "align",
    # ImVec4&
    "bg_col", "tint_col",
}

# ``ImVec2* size = null`` -> ``ImVec2* size`` for the names above. Pointer
# parameters the native side null-checks (IsMousePosValid's mouse_pos,
# DockSpace's window_class, ItemAdd's nav_bb, RenderTextClipped's clip_rect) are
# intentionally absent and keep their null default.
BY_REF_DEFAULT = re.compile(
    r"(?P<type>ImVec2|ImVec4|ImRect)\*\s+(?P<name>@?[A-Za-z_][A-Za-z0-9_]*)"
    r"\s*=\s*(?:null|default)(?P<tail>\s*[,)])"
)


def fix_by_reference_defaults(text: str) -> str:
    """Drop null defaults on by-reference parameters.

    See :data:`BY_REF_DEFAULT_NAMES` for why null is not safe there. C# requires
    every optional parameter to come after all required ones, so removing a
    default forces the whole signature to become required: the offending
    parameter's own default is removed together with any default before or after
    it.
    """
    lines = text.split("\n")
    out: list[str] = []

    # ``= <literal>`` up to the next comma or the closing paren.
    PARAM_DEFAULT = re.compile(r"\s*=\s*[^,()]+(?=\s*[,)])")

    for line in lines:
        match = BY_REF_DEFAULT.search(line)
        if match is None or match.group("name").lstrip("@") not in BY_REF_DEFAULT_NAMES:
            out.append(line)
            continue

        out.append(PARAM_DEFAULT.sub("", line))

    return "\n".join(out)


POINTER_TYPE_ARG = re.compile(r"ImVector<(?P<t>[A-Za-z_]\w*)\*>")


def fix_pointer_type_arguments(text: str) -> str:
    """``ImVector<T*>`` cannot use a pointer as a generic argument.

    ``T*`` is a pointer-sized value, so ``IntPtr`` is layout compatible.
    """
    return POINTER_TYPE_ARG.sub(lambda m: f"ImVector<IntPtr>", text)


def fix_extern_modifiers(text: str) -> str:
    return text.replace("static extern readonly ", "static extern ")


BOOL_DEFAULT = re.compile(
    r"(\[NativeTypeName\(\"bool\"\)\] byte \w+ = )(?:true|false)(?=\s*[,)])"
)


def fix_bool_defaults(text: str) -> str:
    """``byte x = false`` is not valid C#; ``bool`` maps to ``byte`` so use 0/1."""
    return BOOL_DEFAULT.sub(
        lambda m: m.group(1) + ("1" if "true" in m.group(0) else "0"), text
    )


FILE_POINTER = re.compile(r"\b_iobuf\*")


def fix_negative_enum_values(text: str) -> str:
    """Cast complemented integer literals so they fit the enum's backing type.

    ClangSharp emits C++ ``~0`` / ``~0x7FF...`` verbatim. C# evaluates those as
    ``int``, which does not implicitly convert when the enum's members exceed
    ``int.MaxValue`` (the compiler then infers ``uint``). Casting through the
    underlying type keeps both the value and the inferred type intact.
    """
    def patch(match: re.Match[str]) -> str:
        body = match.group("body")

        def to_signed_hex(literal: str) -> str:
            # Reproduce C's 32-bit complement: the mask literals are 32-bit, so
            # wrap the result back into the signed range.
            value = ~int(literal, 0) & 0xFFFFFFFF
            signed = value - (1 << 32) if value >= (1 << 31) else value
            return str(signed)

        body = re.sub(
            r"= ~(0x[0-9A-Fa-f]+)\b",
            lambda m: f"= {to_signed_hex(m.group(1))}",
            body,
        )
        body = re.sub(r"= ~0\b", "= -1", body)
        return f"public enum {match.group('name')}\n    {{{body}"

    return re.sub(
        r"public enum (?P<name>\w+)(?: : \w+)?\n    \{(?P<body>.*?\n    \})",
        patch,
        text,
        flags=re.S,
    )


def fix_file_pointer(text: str) -> str:
    """``FILE*`` is opaque here; ``_iobuf`` is a CRT-internal name we never use."""
    return FILE_POINTER.sub("void*", text)


# Structs that contain pointer or fixed-buffer fields must be declared unsafe.
PLAIN_STRUCT = re.compile(r"^( {4})public partial struct (\w+)$", re.M)


def mark_unsafe_structs(text: str) -> str:
    """Add ``unsafe`` to structs whose body contains pointers or fixed buffers."""
    lines = text.splitlines(keepends=True)
    output: list[str] = []
    index = 0
    while index < len(lines):
        match = PLAIN_STRUCT.match(lines[index].rstrip("\n"))
        if match is None:
            output.append(lines[index])
            index += 1
            continue

        # Find the struct body extent by brace depth.
        depth = 0
        body_end = index
        seen_open = False
        for probe in range(index, len(lines)):
            depth += lines[probe].count("{") - lines[probe].count("}")
            if "{" in lines[probe]:
                seen_open = True
            if seen_open and depth == 0:
                body_end = probe
                break

        body = "".join(lines[index : body_end + 1])
        needs_unsafe = "*" in body or "fixed " in body
        if needs_unsafe:
            output.append(
                lines[index].replace("public partial struct", "public unsafe partial struct")
            )
        else:
            output.append(lines[index])

        for offset in range(index + 1, body_end + 1):
            output.append(lines[offset])
        index = body_end + 1

    return "".join(output)


def rename_constructors(text: str) -> str:
    """``public static extern Foo(Foo* pThis, ...)`` -> ``public static extern void Foo_Ctor(Foo* pThis, ...)``."""
    pattern = re.compile(
        r"(public static extern )([A-Za-z_][A-Za-z0-9_]*)\(\2\* pThis"
    )
    return pattern.sub(lambda m: f"{m.group(1)}void {m.group(2)}_Ctor({m.group(2)}* pThis", text)


def nullable_string_params(text: str) -> str:
    """Mark string parameters with a ``null`` default as nullable."""
    pattern = re.compile(
        r"(\[MarshalAs\(UnmanagedType\.LPUTF8Str\)\] string )"
        r"([A-Za-z_][A-Za-z0-9_]*)( = null)"
    )
    return pattern.sub(lambda m: f"{m.group(1)}?{m.group(2)}{m.group(3)}", text)


def rewrite_library(text: str, library: str) -> str:
    """Point every ``DllImport`` at ``library``.

    Each backend ships its own native library, so the import name has to match
    the file that actually exports the symbol.
    """
    return re.sub(r'\[DllImport\("[^"]*"', f'[DllImport("{library}"', text)


def add_missing_usings(text: str, usings: list[str]) -> str:
    """Ensure ``usings`` are present in the file header."""
    if not usings:
        return text
    missing = [u for u in usings if f"using {u};" not in text]
    if not missing:
        return text
    anchor = "using System.Runtime.InteropServices;"
    if anchor in text:
        insertion = "".join(f"using {u};\n" for u in missing)
        return text.replace(anchor, anchor + "\n" + insertion, 1)
    return text


def strip_method_prefix(text: str, prefix: str) -> str:
    """Remove the redundant C prefix from methods inside a backend class.

    ``OpenGL3.Init`` reads better than ``ImGui_ImplOpenGL3.ImGui_ImplOpenGL3_Init``.
    Only the method container is touched; type declarations are handled by
    :func:`strip_type_prefixes`.
    """
    if not prefix:
        return text
    return re.sub(rf"\b{re.escape(prefix)}_([A-Za-z]\w*)\b", r"\1", text)


def strip_type_prefixes(text: str, prefixes: list[str]) -> str:
    """Remove C name prefixes from generated types.

    ``ImGui_ImplDX11_RenderState`` -> ``RenderState``,
    ``ALLEGRO_DISPLAY`` -> ``Display``. Longest prefixes win so that
    ``ImGui_ImplVulkanH_`` is tried before ``ImGui_ImplVulkan_``.
    """
    if not prefixes:
        return text
    for prefix in sorted(prefixes, key=len, reverse=True):
        pattern = re.compile(rf"\b{re.escape(prefix)}([A-Za-z]\w*)\b")
        text = pattern.sub(r"\1", text)
    return text


def rename_methods_class(text: str, name: str = "ImGui") -> str:
    """The generator names the extern container ``Methods``; use ``ImGui``."""
    text = re.sub(
        r"^    public static unsafe partial class Methods\b",
        f"    public static unsafe partial class {name}",
        text,
        flags=re.M,
    )
    return text


def remove_helper_types(text: str) -> str:
    return HELPER_TYPE_BLOCK.sub("\n", text)


ASSEMBLY_ATTR = re.compile(r"^\[assembly: [A-Za-z]+\([^\n]*\)\]\n", re.M)


def remove_assembly_attributes(text: str) -> str:
    """Drop ``[assembly: ...]`` lines; per-type files would duplicate them."""
    return ASSEMBLY_ATTR.sub("", text)


def rewrite_namespace(text: str, namespace: str) -> str:
    """Force the emitted namespace to ``namespace``.

    The generator is invoked with a scratch namespace during probing; rewriting
    here keeps the binding project name and the namespace independent. Any
    ``using static`` directives that referenced the scratch namespace are
    rewritten too.
    """
    match = re.search(r"^namespace ([A-Za-z_][A-Za-z0-9_.]*)", text, re.M)
    if match is None:
        return text
    original = match.group(1)
    text = text.replace(f"using static {original}.", f"using static {namespace}.")
    return re.sub(
        r"^namespace [A-Za-z_][A-Za-z0-9_.]*",
        f"namespace {namespace}",
        text,
        count=1,
        flags=re.M,
    )


def collapse_blank_lines(text: str) -> str:
    return re.sub(r"\n{3,}", "\n\n", text)


def apply_rewrites(
    text: str,
    *,
    strip_underscore: bool,
    namespace: str | None = None,
    library: str | None = None,
    methods_class: str = "ImGui",
    usings: list[str] | None = None,
    method_prefix: str | None = None,
    type_prefixes: list[str] | None = None,
) -> str:
    if strip_underscore:
        text = strip_trailing_underscore(text)
    if namespace:
        text = rewrite_namespace(text, namespace)
    if library:
        text = rewrite_library(text, library)
    if method_prefix:
        text = strip_method_prefix(text, method_prefix)
    if type_prefixes:
        text = strip_type_prefixes(text, type_prefixes)
    text = add_missing_usings(text, usings or [])
    text = expand_templates(text)
    text = fix_extern_modifiers(text)
    text = fix_bool_defaults(text)
    text = fix_negative_enum_values(text)
    text = fix_file_pointer(text)
    text = mark_unsafe_structs(text)
    text = rewrite_string_params(text)
    text = drop_string_literal_defaults(text)
    text = fix_by_reference_defaults(text)
    text = fix_pointer_type_arguments(text)
    text = rename_constructors(text)
    text = nullable_string_params(text)
    text = rename_methods_class(text, methods_class)
    text = remove_helper_types(text)
    text = remove_assembly_attributes(text)
    text = collapse_blank_lines(text)
    return text


# ---------------------------------------------------------------------------
# Type splitting
# ---------------------------------------------------------------------------

# Matches a top-level type declaration inside the namespace block. Top-level
# types sit at four-space indentation; nested types (``_X_e__FixedBuffer`` and
# friends) are indented deeper and must stay with their parent.
# Matches a namespace-level type declaration. Used only to find the start of a
# type; the extent is then found by tracking brace depth.
TYPE_DECL = re.compile(
    r"^ {4}(?:public (?:static )?(?:unsafe )?partial (?:struct|class) (?P<name>[A-Za-z_][A-Za-z0-9_]*)"
    r"|public enum (?P<ename>[A-Za-z_][A-Za-z0-9_]*))",
    re.M,
)


def _find_type_extent(lines: list[str], start: int) -> int:
    """Return the index just past the closing brace of the type starting at ``start``.

    ``start`` must point at the ``public ...`` line (attribute lines are walked
    backwards by the caller). Brace depth is tracked so nested types and
    ``fixed`` buffers stay attached to their parent.
    """
    depth = 0
    seen_open = False
    for index in range(start, len(lines)):
        line = lines[index]
        depth += line.count("{") - line.count("}")
        if "{" in line:
            seen_open = True
        if seen_open and depth == 0:
            return index + 1
    return len(lines)


def split_types(text: str, namespace: str) -> dict[str, str]:
    """Split a generated file into ``{type_name: file_text}``.

    Anything that is not a type declaration (usings, the static class holding
    the extern methods) is collected under ``__preamble`` / ``__methods``.
    """
    match = re.search(r"^namespace [A-Za-z_][A-Za-z0-9_.]*", text, re.M)
    if match is None:
        raise SystemExit("no namespace declaration found in generated output")
    header_end = match.start()
    preamble = text[:header_end]
    body_start = text.index("{", match.end()) + 1
    body_end = text.rindex("}")

    body = text[body_start:body_end]
    lines = body.splitlines(keepends=True)

    starts: list[tuple[int, int, str]] = []  # (decl_line, block_start, name)
    for index, line in enumerate(lines):
        found = TYPE_DECL.match(line)
        if not found:
            continue
        name = found.group("name") or found.group("ename")

        # Walk backwards over contiguous attribute lines so they stay attached.
        block_start = index
        while block_start > 0 and lines[block_start - 1].startswith("    ["):
            block_start -= 1

        starts.append((index, block_start, name))

    files: dict[str, str] = {}
    consumed = 0
    for position, (decl_line, block_start, name) in enumerate(starts):
        end_line = _find_type_extent(lines, decl_line)
        if position + 1 < len(starts):
            end_line = min(end_line, starts[position + 1][1])

        chunk = "".join(lines[block_start:end_line]).rstrip()
        consumed = max(consumed, end_line)
        if not chunk:
            continue

        if "partial class" in lines[decl_line]:
            files["__methods"] = chunk
        else:
            files[name] = chunk

    leftover = "".join(lines[consumed:]).strip()
    if leftover:
        files["__preamble"] = leftover

    rendered: dict[str, str] = {}
    for name, chunk in files.items():
        rendered[name] = (
            "// <auto-generated>\n"
            "//     Generated by ImGui4NET from the Dear ImGui headers.\n"
            "// </auto-generated>\n"
            f"{preamble}namespace {namespace}\n{{\n{chunk}\n}}\n"
        )
    return rendered


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="generated .cs file")
    parser.add_argument("-o", "--output", type=Path, help="output file (single-file mode)")
    parser.add_argument(
        "-d",
        "--output-dir",
        type=Path,
        help="write one file per type into this directory instead",
    )
    parser.add_argument("-n", "--namespace", required=True)
    parser.add_argument(
        "-l",
        "--library",
        help="native library name for DllImport (default: keep generated name)",
    )
    parser.add_argument(
        "--methods-class",
        default="ImGui",
        help="name for the static class holding the extern methods (default: ImGui)",
    )
    parser.add_argument(
        "--using",
        dest="usings",
        action="append",
        default=[],
        help="namespace to import (repeatable), e.g. --using ImGui4NET.Native",
    )
    parser.add_argument(
        "--method-prefix",
        help="C prefix to strip from method names inside the backend class",
    )
    parser.add_argument(
        "--type-prefix",
        dest="type_prefixes",
        action="append",
        default=[],
        help="C prefix to strip from type names (repeatable, longest wins)",
    )
    parser.add_argument(
        "--keep-trailing-underscore",
        action="store_true",
        help="do not strip trailing underscores from type names",
    )
    args = parser.parse_args(argv)

    text = args.input.read_text(encoding="utf-8")
    text = apply_rewrites(
        text,
        strip_underscore=not args.keep_trailing_underscore,
        namespace=args.namespace,
        library=args.library,
        methods_class=args.methods_class,
        usings=args.usings,
        method_prefix=args.method_prefix,
        type_prefixes=args.type_prefixes,
    )

    if args.output_dir:
        args.output_dir.mkdir(parents=True, exist_ok=True)
        for name, content in split_types(text, args.namespace).items():
            target = args.output_dir / f"{name.lstrip('_')}.cs"
            target.write_text(content, encoding="utf-8")
            print(f"  wrote {target.name}", file=sys.stderr)
    else:
        if not args.output:
            parser.error("either --output or --output-dir is required")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
        print(f"  wrote {args.output.name}", file=sys.stderr)

    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
