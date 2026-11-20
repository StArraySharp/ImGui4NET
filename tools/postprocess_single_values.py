"""Wrap audited single-value native pointer parameters in managed APIs."""
from __future__ import annotations

from dataclasses import dataclass
from itertools import combinations
import re

REF_PARAMS = {
    "Checkbox": {"v"}, "CheckboxFlags": {"flags"}, "RadioButton": {"v"},
    "Combo": {"current_item"}, "ListBox": {"current_item"},
    "DragFloat": {"v"}, "DragInt": {"v"},
    "DragFloatRange2": {"v_current_min", "v_current_max"},
    "DragIntRange2": {"v_current_min", "v_current_max"},
    "SliderFloat": {"v"}, "SliderInt": {"v"}, "SliderAngle": {"v_rad"},
    "VSliderFloat": {"v"}, "VSliderInt": {"v"},
    "InputFloat": {"v"}, "InputInt": {"v"}, "InputDouble": {"v"},
    "Selectable": {"p_selected"}, "CollapsingHeader": {"p_visible"},
    "MenuItem": {"p_selected"}, "Begin": {"p_open"},
    "BeginPopupModal": {"p_open"}, "BeginTabItem": {"p_open"},
    "ShowDemoWindow": {"p_open"}, "ShowMetricsWindow": {"p_open"},
    "ShowDebugLogWindow": {"p_open"}, "ShowIDStackToolWindow": {"p_open"},
    "ShowAboutWindow": {"p_open"}, "ShowStyleEditor": {"@ref"},
    "StyleColorsDark": {"dst"}, "StyleColorsLight": {"dst"},
    "StyleColorsClassic": {"dst"},
}
IN_PARAMS = {
    "IsMousePosValid": {"mouse_pos"}, "ItemAdd": {"nav_bb"},
    "BeginDragDropTargetViewport": {"p_bb"},
    "RenderTextClipped": {"text_size_if_known", "clip_rect"},
    "RenderTextClippedEx": {"text_size_if_known", "clip_rect"},
    "RenderTextEllipsis": {"text_size_if_known"},
    "RenderChar": {"cpu_fine_clip"}, "AddText": {"cpu_fine_clip_rect"},
}
OUT_PARAMS = {
    "GetAllocatorFunctions": {"p_alloc_func", "p_free_func", "p_user_data"},
    "_SelectLineTexture": {"out_uv0", "out_uv1", "out_fringe"},
    "GetTexDataAsAlpha8": {"out_pixels", "out_width", "out_height", "out_bytes_per_pixel"},
    "GetTexDataAsRGBA32": {"out_pixels", "out_width", "out_height", "out_bytes_per_pixel"},
}
ATTR = re.compile(r'\[(?:NativeTypeName|MarshalAs)\((?:[^)"\n]|"[^"\n]*")*\)\]\s*')


# Native NULL semantics are opt-in: no close button, current style or mouse
# position, inferred bounds/text size, or the draw list's default clipping.
NULL_SAFE_REFS = {
    ("Begin", "p_open"), ("BeginPopupModal", "p_open"), ("BeginTabItem", "p_open"),
    ("ShowDemoWindow", "p_open"), ("ShowMetricsWindow", "p_open"),
    ("ShowDebugLogWindow", "p_open"), ("ShowIDStackToolWindow", "p_open"), ("ShowAboutWindow", "p_open"),
    ("ShowStyleEditor", "@ref"),
    ("StyleColorsDark", "dst"), ("StyleColorsLight", "dst"), ("StyleColorsClassic", "dst"),
}
NULL_SAFE_INPUTS = {
    ("IsMousePosValid", "mouse_pos"),
    ("ItemAdd", "nav_bb"),
    ("BeginDragDropTargetViewport", "p_bb"),
    ("RenderTextClipped", "text_size_if_known"), ("RenderTextClipped", "clip_rect"),
    ("RenderTextClippedEx", "text_size_if_known"), ("RenderTextClippedEx", "clip_rect"),
    ("RenderTextEllipsis", "text_size_if_known"),
    ("RenderChar", "cpu_fine_clip"),
    ("AddText", "cpu_fine_clip_rect"),
}

# The existing C++ reference pass drops these defaults before this pass runs.
MANAGED_DEFAULTS = {
    ("Selectable", "flags"): "0",
    ("Selectable", "size"): "default",
    ("RenderTextClipped", "align"): "default",
    ("RenderTextClippedEx", "align"): "default",
}
NULLABLE_STRINGS = {
    ("MenuItem", "shortcut"), ("RenderTextClipped", "text_end"),
    ("RenderTextClippedEx", "text_end"), ("RenderTextEllipsis", "text_end"),
}
DECLARATION = re.compile(
    r"(?P<attrs>(?:^[ \t]*\[[^\n]+\]\r?\n)*)"
    r"^(?P<indent>[ \t]*)public static extern (?P<result>[\w*]+) "
    r"(?P<method>\w+)\((?P<params>[^\n]*)\);(?=\r?$)", re.M
)


@dataclass
class Parameter:
    raw: str
    typ: str
    name: str
    default: str
    mode: str


def rewrite_single_value_params(text: str, split_params) -> str:
    def rewrite(match):
        attrs, indent, result, method, raw = match.group("attrs", "indent", "result", "method", "params")
        if not any(method in rules for rules in (REF_PARAMS, IN_PARAMS, OUT_PARAMS)):
            return match.group(0)
        params = []
        for part in split_params(raw):
            decl, _, default = part.strip().partition(" = ")
            decl = re.sub(r"\bstring \?(?=\w)", "string? ", decl)
            plain = ATTR.sub("", decl)
            parsed = re.fullmatch(r"(.+?)\s+(@?\w+)", plain)
            if not parsed:  # In particular, leave __arglist declarations to the varargs pass.
                return match.group(0)
            typ, name = parsed.groups()
            if typ == "string" and (default == "null" or (method, name) in NULLABLE_STRINGS):
                typ = "string?"
                decl = re.sub(r"\bstring (?=@?\w+$)", "string? ", decl)
            mode = ""
            if (typ.endswith("*") and not typ.startswith(("ref ", "in ", "out "))
                    and not re.search(r'NativeTypeName\("[^"]*(?:&|\[)', decl)):
                mode = next((mode for mode, rules in (("ref", REF_PARAMS), ("in", IN_PARAMS),
                             ("out", OUT_PARAMS)) if name in rules.get(method, ())), "")
            params.append(Parameter(decl, typ, name, default, mode))
        if not any(p.mode for p in params):
            return match.group(0)

        raw_name = f"{method}_Raw"
        if "[DllImport(" in attrs and not re.search(r"\bEntryPoint\s*=", attrs):
            attrs = re.sub(r'(\[DllImport\([^\r\n]*)(\)\])',
                           rf'\1, EntryPoint = "{method}"\2', attrs, count=1)
        raw_params = ", ".join(p.raw for p in params)
        blocks = [f"{attrs}{indent}private static unsafe extern {result} {raw_name}({raw_params});"]

        def wrapper(adapted):
            public, args, locals_, pins, copy_back = [], [], [], [], []
            for index, p in enumerate(params):
                name = p.name
                key = (method, name)
                if not p.mode:
                    public.append((f"{p.typ} {name}", p.default or MANAGED_DEFAULTS.get(key, "")))
                    modifier = p.typ.split()[0]
                    args.append(f"{modifier} {name}" if modifier in ("ref", "in", "out") else name)
                    continue

                typ = p.typ[:-1].rstrip()
                local, pointer = f"__value{index}", f"__pointer{index}"
                if p.mode == "out":
                    locals_.append(f"{typ} {local} = default;")
                    args.append(f"&{local}")
                    if index not in adapted:
                        public.append((f"out {typ} {name}", ""))
                        copy_back.append(f"{name} = {local};")
                elif index in adapted:
                    if p.mode == "in" and key in NULL_SAFE_INPUTS:
                        public.append((f"{typ}? {name}", p.default))
                        locals_.append(f"{typ} {local} = {name}.GetValueOrDefault();")
                        args.append(f"{name}.HasValue ? &{local} : null")
                    elif p.mode == "ref" and key in NULL_SAFE_REFS and p.default in ("null", "default"):
                        args.append("null")
                    else:
                        fallback = "default" if p.default == "null" else p.default
                        locals_.append(f"{typ} {local} = {fallback};")
                        args.append(f"&{local}")
                else:
                    public.append((f"{p.mode} {typ} {name}", ""))
                    # bool* points to a one-byte C# bool directly (no bool marshalling).
                    pins.append(f"fixed ({typ}* {pointer} = &{name})")
                    args.append(pointer)

            last_required = max((i for i, (_, default) in enumerate(public) if not default), default=-1)
            signature = ", ".join(f"{decl} = {default}" if default and i > last_required else decl
                                  for i, (decl, default) in enumerate(public))
            invocation = f"{raw_name}({', '.join(args)});"
            if result != "void":
                invocation = f"{result} __result = {invocation}" if copy_back else f"return {invocation}"
            # Keep the result in scope for copy-back even when pins are needed.
            if result != "void" and copy_back and pins:
                locals_.append(f"{result} __result;")
                invocation = f"__result = {raw_name}({', '.join(args)});"
            lines = [f"public static unsafe {result} {method}({signature})", "{"]
            lines.extend(f"    {line}" for line in locals_ + pins)
            if pins:
                lines.extend(("    {", f"        {invocation}", "    }"))
            else:
                lines.append(f"    {invocation}")
            lines.extend(f"    {line}" for line in copy_back)
            if result != "void" and copy_back:
                lines.append("    return __result;")
            lines.append("}")
            return "\n".join(indent + line for line in lines)

        adaptable = [i for i, p in enumerate(params) if p.mode and (
            p.default or (p.mode == "in" and (method, p.name) in NULL_SAFE_INPUTS)
        )]
        for count in range(len(adaptable) + 1):
            blocks.extend(wrapper(adapted) for adapted in combinations(adaptable, count))
        return "\n\n".join(blocks)

    return DECLARATION.sub(rewrite, text)
