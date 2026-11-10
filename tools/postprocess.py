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
# A C++ ``const char*`` parameter is a read-only string, so marshalling it as a
# managed ``string`` is correct and allocation-free.
#
# A non-const ``char*`` is an OUTPUT buffer: Dear ImGui writes into it
# (InputText, InputTextMultiline, InputTextWithHint, ...). Marshalling those as
# ``string`` makes the write land on a temporary copy that is immediately
# discarded, so the caller never observes an edit. They must stay ``sbyte*``.
STRING_PARAM = re.compile(
    r'\[NativeTypeName\("const char \*"\)\]\s*sbyte\*\s+'
    r"(?P<name>@?[A-Za-z_][A-Za-z0-9_]*)"
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


def drop_vec_constructor_defaults(text: str) -> str:
    """Replace a default-constructed vector with ``default``.

    ClangSharp renders the C++ default argument ``ImVec2(0, 0)`` as
    ``new ImVec2(0, 0)``, but the generated ``ImVec2`` is a plain two-field
    struct with no constructor. ``default`` is the same value.
    """
    return VEC2_CONSTRUCTOR_DEFAULT.sub("= default", text)


def drop_string_literal_defaults(text: str) -> str:
    text = STRING_LITERAL_DEFAULT.sub("= null", text)
    return text


# ClangSharp turns a C++ reference parameter (``const ImVec2& v``) into a
# pointer (``ImVec2* v``), which is the correct ABI. The default argument is the
# problem: C++ constructs a temporary for the default so the reference is never
# null, while the generated pointer default is ``null`` and the native code
# dereferences it unconditionally. Every call that omits the argument then dies
# with an access violation (0xC0000005).
#
# A pointer cannot be a C# default argument (defaults must be compile-time
# constants), so the value is carried by a static readonly field instead and the
# parameter is moved into an overload that omits it:
#
#   ImVec2* pivot = null
#     -> overload: Foo(..., ImGui.C_ImVec2_0_0)
#        core:     Foo(..., ImVec2* pivot)
#
# The constant keeps the exact C++ value, so ``ProgressBar``'s
# ``ImVec2(-FLT_MIN, 0)`` and ``Image``'s ``ImVec2(1, 1)`` behave like upstream
# instead of silently becoming zero.
REF_PARAM = re.compile(
    r'\[NativeTypeName\("(?P<native>const )?(?P<type>ImVec2|ImVec4|ImRect) &"\)\]\s*'
    r"(?P<ctype>ImVec2|ImVec4|ImRect)\*\s+"
    r"(?P<name>@?[A-Za-z_][A-Za-z0-9_]*)"
    r"(?P<default>\s*=\s*(?:null|default|new\s+\w+\([^)]*\)))?"
)

# A struct constructor default such as ``new ImVec2(0, 0)``.
CTOR_DEFAULT = re.compile(r"new\s+(?P<type>ImVec2|ImVec4|ImRect)\((?P<args>[^)]*)\)")


def _literal_to_csharp(arg: str) -> str:
    """Translate a C++ float literal to C#.

    ``FLT_MIN``/``FLT_MAX`` are C macros, so they are expanded here instead of
    leaking into the generated source. ``FLT_MIN`` is the smallest *positive*
    normal, not the most negative value: mixing it up with ``FLT_MAX`` silently
    changes what ImGui means by "fill the remaining width".
    """
    arg = arg.strip()
    if arg == "FLT_MIN":
        return "1.17549435e-38F"
    if arg == "-FLT_MIN":
        return "-1.17549435e-38F"
    if arg == "FLT_MAX":
        return "3.40282347e+38F"
    if arg == "-FLT_MAX":
        return "-3.40282347e+38F"
    if "." in arg or "e" in arg.lower():
        return arg if arg.endswith("f") or arg.endswith("F") else f"{arg}f"
    return f"{arg}f"


class RefDefaultTable:
    """Static fields holding the C++ default values of reference parameters.

    The values come from the Dear ImGui headers rather than being assumed, so
    ``ProgressBar``'s ``ImVec2(-FLT_MIN, 0)`` and ``Image``'s ``ImVec2(1, 1)``
    keep their upstream meaning.
    """

    def __init__(self, headers: list[Path] | None = None) -> None:
        self.fields: dict[str, tuple[str, str]] = {}
        self._native = _load_native_defaults(headers or [])

    def key(self, type_name: str, args: list[str]) -> str:
        slug = "_".join(
            _literal_to_csharp(a).replace("-", "neg").replace(".", "p").replace("e+", "e")
            for a in args
        )
        return f"C_{type_name}_{slug}"

    def add(self, type_name: str, args: list[str]) -> str:
        name = self.key(type_name, args)
        self.fields[name] = (type_name, [_literal_to_csharp(a) for a in args])
        return name

    def emit(self, text: str) -> str:
        if not self.fields:
            return text

        lines = [
            "",
            "    /// <summary>",
            "    /// C++ default arguments for by-reference parameters. A pointer cannot be a",
            "    /// C# default argument, so calls that omit one are forwarded to these values.",
            "    /// </summary>",
        ]
        for name, (type_name, values) in sorted(self.fields.items()):
            axes = ["x", "y", "z", "w"]
            members = ", ".join(
                f"{axes[i]} = {value}" for i, value in enumerate(values) if i < len(axes)
            )
            lines.append(
                f"    internal static readonly {type_name} {name} = new() {{ {members} }};"
            )

        block = "\n".join(lines) + "\n"
        match = re.search(r"(public static unsafe partial class \w+\s*\n\s*\{\n)", text)
        if match is None:
            return text
        return text[: match.end()] + block + "\n" + text[match.end() :]


# ---------------------------------------------------------------------------
# C++ default extraction
# ---------------------------------------------------------------------------

NATIVE_DECL = re.compile(r"IMGUI_API\s+[^;{(]*?\b(?P<name>\w+)\s*\((?P<params>[^;]*?)\)\s*;", re.S)
NATIVE_PARAM = re.compile(
    r"^\s*(?P<type>const\s+[\w:]+\s*&|[\w:]+\s*[&*]?)\s*(?P<name>\w+)"
    r"\s*(?:=\s*(?P<default>.+))?$",
    re.S,
)
NATIVE_CTOR = re.compile(r"^(?P<type>ImVec2|ImVec4|ImRect)\s*\((?P<args>[^)]*)\)$")


def _split_top_level(params: str) -> list[str]:
    """Split a C++ parameter list on commas that are not nested."""
    parts: list[str] = []
    depth = 0
    cur = ""
    for ch in params:
        if ch in "(<[":
            depth += 1
        elif ch in ")>]":
            depth -= 1
        if ch == "," and depth == 0:
            parts.append(cur)
            cur = ""
        else:
            cur += ch
    if cur.strip():
        parts.append(cur)
    return parts


def _load_native_defaults(headers: list[Path]) -> dict[tuple[str, int], tuple[str, list[str]]]:
    """Map ``(function, parameter index)`` to the C++ struct default.

    Only ``ImVec2``/``ImVec4``/``ImRect`` constructor defaults are collected:
    those are the reference parameters whose null default is unsafe.
    """
    table: dict[tuple[str, int], tuple[str, list[str]]] = {}

    for header in headers:
        if not header.exists():
            continue

        text = header.read_text(encoding="utf-8", errors="replace")
        for decl in NATIVE_DECL.finditer(text):
            name = decl.group("name")
            for index, raw in enumerate(_split_top_level(decl.group("params"))):
                param = NATIVE_PARAM.match(raw)
                if param is None:
                    continue
                default = (param.group("default") or "").strip()
                ctor = NATIVE_CTOR.match(default)
                if ctor is None:
                    continue
                args = [a.strip() for a in ctor.group("args").split(",")]
                table[(name, index)] = (ctor.group("type"), args)

    return table


def fix_reference_params(text: str, defaults: RefDefaultTable) -> str:
    """Route reference parameters with defaults through a static constant.

    See :data:`REF_PARAM` for the reasoning.
    """
    lines = text.split("\n")
    out: list[str] = []
    overloads: list[str] = []

    for line in lines:
        if not REF_PARAM.search(line):
            out.append(line)
            continue

        # Collect the parameters of this signature. The leading indentation is part
        # of the match: `prefix` is written back verbatim, so dropping it would
        # unindent the declaration.
        match = re.search(r"(^[ \t]*public static extern [\w\*]+ \w+\()(.*)(\);)", line)
        if match is None:
            out.append(line)
            continue

        prefix, params, suffix = match.group(1), match.group(2), match.group(3)
        parts: list[str] = []
        depth = 0
        cur = ""
        for ch in params:
            if ch in "<(":
                depth += 1
            elif ch in ">)":
                depth -= 1
            if ch == "," and depth == 0:
                parts.append(cur)
                cur = ""
            else:
                cur += ch
        if cur.strip():
            parts.append(cur)

        # Every reference parameter becomes a required pointer in the core
        # signature; the defaults are supplied by the overload chain below.
        stripped_parts = list(parts)
        for index, part in enumerate(stripped_parts):
            stripped_parts[index] = REF_PARAM.sub(
                lambda m: f'[NativeTypeName("const {m.group("type")} &")] {m.group("ctype")}* {m.group("name")}',
                part,
            )

        # C# forbids an optional parameter before a required one, so any default
        # that now precedes a newly-required reference parameter is dropped too.
        seen_required = False
        for index in range(len(stripped_parts) - 1, -1, -1):
            part = stripped_parts[index]
            if REF_PARAM.search(parts[index]):
                seen_required = True
            elif seen_required:
                stripped_parts[index] = re.sub(r"\s*=\s*[^,()]+(?=\s*[,)]|$)", "", part)

        out.append(prefix + ",".join(stripped_parts) + suffix)

        # Build one overload per reference parameter that has a native default,
        # from the rightmost to the leftmost, so each level only supplies the
        # defaults for the parameters it drops.
        ret_match = re.search(r"public static extern (\w+)(\*?)\s", line)
        ret_type = (ret_match.group(1) + (ret_match.group(2) or "")) if ret_match else "void"
        method_name = re.search(r"extern [\w\*]+ (\w+)\(", line).group(1)

        missing = [
            index
            for index, part in enumerate(parts)
            if REF_PARAM.search(part) and (method_name, index) in defaults._native
        ]

        while missing:
            index = missing.pop()
            native_type, native_args = defaults._native[(method_name, index)]
            constant = defaults.add(native_type, native_args)

            call_args = []
            for position, part in enumerate(stripped_parts):
                if position == index:
                    # The extern takes a pointer and the default lives in a
                    # static field, so hand out its address. A `fixed` statement
                    # would need a block, and the field is not on the GC heap, so
                    # Unsafe.AsPointer is both legal and allocation free.
                    call_args.append(
                        f"({native_type}*)Unsafe.AsPointer(ref Unsafe.AsRef(in {constant}))"
                    )
                else:
                    name_match = re.search(r"(\w+)\s*$", part.strip())
                    call_args.append(name_match.group(1) if name_match else "")

            overload_params = [p for i, p in enumerate(stripped_parts) if i != index]
            overloads.append(
                f"    public static {ret_type} {method_name}({','.join(overload_params)}) "
                f"=> {method_name}({', '.join(call_args)});"
            )

            stripped_parts = overload_params
            missing = [m - 1 if m > index else m for m in missing]

    if overloads:
        # The overloads must sit inside the static class, next to the externs.
        # The class closes with the last "    }" before the namespace brace.
        out_text = "\n".join(out)
        class_end = out_text.rfind("\n    }")
        if class_end == -1:
            out_text += "\n" + "\n".join(overloads)
        else:
            out_text = (
                out_text[:class_end]
                + "\n\n"
                + "\n".join(overloads)
                + out_text[class_end:]
            )
        return out_text

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

    A trailing underscore in ``prefix`` is dropped first: the Objective-C++
    backends pass ``ImGui_ImplMetal4_`` (the full C prefix including the
    separator) while the generic path passes ``ImGui_ImplOpenGL3``.
    """
    if not prefix:
        return text
    prefix = prefix.rstrip("_")
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
    # Wrappers are inserted as "native decl\n\nwrapper"; inside a class body a
    # single blank line separates members, and whitespace-only lines left behind
    # by the removed reference-default comment block are dropped too.
    text = re.sub(r"(?m)^[ \t]+$", "", text)
    text = re.sub(r"\n{3,}", "\n\n", text)
    return text


def normalise_comma_spacing(text: str) -> str:
    """Ensure a single space after a comma, but only within a parameter list.

    The native declaration of a wrapped Objective-C function is rebuilt by
    joining its parameters, which can leave ``a,[Attr] b``. Enum members are
    separated by commas too, so the rewrite is limited to something that looks
    like a call or declaration.
    """
    return re.sub(r"(?<=,)(?=\[NativeTypeName)", " ", text)


# Mach-O symbol tables store every C symbol with a leading underscore, and
# ClangSharp reads the linker's spelling rather than the C name. dlsym() wants
# the C spelling: its manual page states the name passed to dlsym "must NOT be
# prepended with an underscore". Only the EntryPoint string is affected - the
# mangled body (`_Z...`) is left exactly as generated.
MACHO_ENTRY_POINT = re.compile(r'EntryPoint = "(__Z[^"]*)"')


def strip_macho_underscore(text: str) -> str:
    # A replacement function rather than a "\1" template: the backreference is
    # followed by no delimiter here, and a template would have to be re-escaped
    # at two levels (regex string and replacement string).
    return MACHO_ENTRY_POINT.sub(
        lambda m: 'EntryPoint = "' + m.group(1)[1:] + '"', text
    )


# Objective-C types referenced by an ObjC++ header. ClangSharp does not emit a
# declaration for a class or protocol it only sees forward-declared, so the
# binding would not compile. A single-pointer struct gives the pointer a
# distinct type without claiming anything about the object, which is exactly
# what an opaque handle needs.
#
# The struct is passed to the extern by value: it holds one machine word and is
# therefore blittable, so the ABI hands the native function the pointer it
# expects without any marshalling or wrapper method.
OBJC_OPAQUE_TEMPLATE = '''    /// <summary>
    /// Handle to the Objective-C <c>{name}</c> object.
    /// </summary>
    /// <remarks>
    /// ClangSharp emits no declaration for an Objective-C type it only sees
    /// forward-declared, so the native signature degrades to <c>void*</c>. This
    /// struct restores a distinct type: it carries only the pointer, is
    /// blittable, and is passed to the native function exactly like the pointer
    /// the header declares.
    /// </remarks>
    public readonly partial struct {name}
    {{
        private readonly nint _h;

        /// <summary>Wraps a native object pointer.</summary>
        public {name}(nint handle) => _h = handle;

        /// <summary>The native object pointer.</summary>
        public nint Handle => _h;

        /// <summary>Whether this handle is null.</summary>
        public bool IsNull => _h == 0;

        /// <summary>
        /// A null handle. Pass this for parameters the header marks
        /// <c>_Nullable</c>.
        /// </summary>
        public static {name} Null => default;
    }}
'''


def add_objc_opaque_types(text: str, names: list[str]) -> str:
    if not names:
        return text

    # Only add a type the file actually references and does not already define.
    additions = []
    for name in names:
        if not re.search(rf"\b{re.escape(name)}\b", text):
            continue
        if re.search(rf"\b(?:struct|class) {re.escape(name)}\b", text):
            continue
        additions.append(OBJC_OPAQUE_TEMPLATE.format(name=name))

    if not additions:
        return text

    # Append inside the namespace, before its closing brace.
    closing = text.rfind("}")
    if closing < 0:
        return text
    block = "\n".join(additions)
    return text[:closing] + block + text[closing:]


# ``[DllImport(...)] public static extern <ret> <Name>(<params>);`` as emitted
# for one function. The parameter list cannot be matched with ``[^)]*`` because
# Objective-C types such as ``id<MTLCommandBuffer>`` appear inside
# ``NativeTypeName("...")`` attributes; it is scanned by paren depth instead.
EXTERN_HEAD = re.compile(
    r"(?P<attrs>(?:^[ \t]*\[[^\n]*\n)+)"
    r"^[ \t]*public static extern (?P<ret>[A-Za-z_][A-Za-z0-9_* ]*?) "
    r"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\(",
    re.M,
)


def _match_paren(text: str, open_index: int) -> int:
    """Return the index of the ``)`` that closes the one at ``open_index``.

    String literals are skipped: a ``NativeTypeName("id<MTLDevice>")`` attribute
    contains no parentheses, but a future one might, and quotes inside the
    attribute must not be treated as structure.
    """
    depth = 0
    index = open_index
    in_string = False
    while index < len(text):
        char = text[index]
        if in_string:
            if char == "\\":
                index += 2
                continue
            if char == '"':
                in_string = False
        elif char == '"':
            in_string = True
        elif char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
            if depth == 0:
                return index
        index += 1
    return -1


def _objc_type_of(param: str, known: list[str]) -> str | None:
    """Return the opaque type a parameter really represents.

    Two spellings reach this point: ``void*`` with a ``NativeTypeName`` of
    ``id<Protocol>`` (what ClangSharp emits for a forward-declared Objective-C
    protocol), and a pointer to a forward-declared class, which ClangSharp keeps
    as ``SomeClass*`` even though it emitted no body for the struct.
    """
    native = re.search(r'NativeTypeName\("([^"]+)"\)', param)
    raw = native.group(1) if native else param

    protocol = re.search(r"id<([A-Za-z0-9_]+)>", raw)
    if protocol and protocol.group(1) in known:
        return protocol.group(1)

    for name in known:
        if re.search(rf"\b{re.escape(name)}\b", raw):
            return name
    return None


def retype_objc_handles(text: str, known: list[str]) -> str:
    """Give the Objective-C handle parameters their real types.

    ClangSharp degrades ``id<MTLDevice>`` and ``NSView*`` to ``void*`` because the
    Objective-C class is only forward-declared, which would make the binding
    indistinguishable from any other pointer. The declared type is swapped for
    the opaque handle struct instead, in place: the struct carries nothing but
    the pointer, so it is blittable and the extern keeps its public name and its
    DllImport, with no wrapper method of any kind.
    """
    if not known:
        return text

    replacements: list[tuple[str, str]] = []

    position = 0
    while True:
        match = EXTERN_HEAD.search(text, position)
        if match is None:
            break
        open_paren = match.end() - 1
        close_paren = _match_paren(text, open_paren)
        if close_paren < 0:
            position = match.end()
            continue

        params = text[open_paren + 1:close_paren]
        position = close_paren

        if "void*" not in params and not any(
            re.search(rf"\b{re.escape(name)}\s*\*", params) for name in known
        ):
            continue

        rewritten: list[str] = []
        changed = False
        for param in _split_top_level(params):
            objc = _objc_type_of(param, known)
            if objc is None:
                rewritten.append(param)
                continue
            changed = True
            rewritten.append(_retype_objc_param(param, objc))

        if not changed:
            continue

        new_params = ",".join(rewritten)
        replacements.append((
            text[open_paren + 1:close_paren],
            new_params,
        ))
        # Skip past what was just rewritten so the same span is not revisited.
        position = close_paren

    for original, replacement in replacements:
        text = text.replace(original, replacement, 1)

    return text


def _retype_objc_param(param: str, objc: str) -> str:
    """Swap a parameter's ``void*`` for the opaque handle type.

    Parameters the header marks ``_Nullable`` / ``_Nonnull`` keep that fact in
    their ``NativeTypeName`` attribute only: the handle is a value type, so a
    null one is expressed as ``default`` rather than a nullable reference.
    """
    attribute = re.search(r'\[NativeTypeName\("([^"]+)"\)\]\s*', param)
    prefix = attribute.group(0) if attribute else ""
    rest = param[attribute.end():] if attribute else param
    name = rest.strip().split()[-1].lstrip("*").strip()
    return f"{prefix}{objc} {name}"

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
    macho_symbols: bool = False,
    objc_opaque_types: list[str] | None = None,
) -> str:
    if strip_underscore:
        text = strip_trailing_underscore(text)
    if macho_symbols:
        text = strip_macho_underscore(text)
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
    # Capture the C++ defaults before they are normalised away, then replace the
    # nullable pointer defaults with forwarding overloads.
    reference_defaults = RefDefaultTable()
    text = fix_reference_params(text, reference_defaults)
    text = drop_vec_constructor_defaults(text)
    text = drop_string_literal_defaults(text)
    text = reference_defaults.emit(text)
    text = fix_pointer_type_arguments(text)
    text = rename_constructors(text)
    text = nullable_string_params(text)
    text = rename_methods_class(text, methods_class)
    text = remove_helper_types(text)
    text = remove_assembly_attributes(text)
    if objc_opaque_types:
        # Types first: the parameter retyping needs to know which names are
        # Objective-C handles.
        text = add_objc_opaque_types(text, objc_opaque_types)
        text = retype_objc_handles(text, objc_opaque_types)
        text = normalise_comma_spacing(text)
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
    r"^ {4}(?:public (?:static )?(?:unsafe )?(?:readonly )?partial (?:struct|class) (?P<name>[A-Za-z_][A-Za-z0-9_]*)"
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
    parser.add_argument(
        "--macho-symbols",
        action="store_true",
        help="drop the Mach-O leading underscore from EntryPoint strings",
    )
    parser.add_argument(
        "--objc-opaque-type",
        dest="objc_opaque_types",
        action="append",
        default=[],
        help="Objective-C class/protocol to declare as an opaque struct (repeatable)",
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
        macho_symbols=args.macho_symbols,
        objc_opaque_types=args.objc_opaque_types,
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
