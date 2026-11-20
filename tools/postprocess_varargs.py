#!/usr/bin/env python3
"""Replace C# varargs with literal-text imports and single managed formatting APIs.

Run after string/reference rewrites, before or after splitting generated types.
Import rewrite_varargs(text) from the main postprocessor; this module has no
third-party dependencies. Native symbols and calling conventions stay intact.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re

IMPORT_ATTRIBUTES = (
    r'(?P<attributes>(?P<indent> +)\[[^\n]+\]\n'
    r'(?:(?P=indent)\[[^\n]+\]\n)*)'
)
DECLARATION = re.compile(
    '^' + IMPORT_ATTRIBUTES +
    r'(?P=indent)public static extern (?P<result>\w+) '
    r'(?P<name>\w+)\((?P<params>[^\n]*)\);$', re.MULTILINE
)
# Upgrade this module's previous unpadded output without regenerating or touching
# unrelated bindings. Already upgraded blocks cannot match this shape.
LEGACY_TEXT = re.compile(
    '^' + IMPORT_ATTRIBUTES +
    r'(?P=indent)private static extern (?P<result>\w+) '
    r'(?P<name>\w+)NativeText\((?P<params>[^\n]*)\);\n\n'
    r'(?P=indent)/// <summary>Passes literal UTF-8 text, or invariant-culture C# composite formatting when arguments are supplied.</summary>\n'
    r'(?P=indent)public static (?P=result) (?P=name)\((?P<managed>[^\n]*)\) =>\n'
    r'(?P=indent)    (?P<body>[^\n]+);$', re.MULTILINE
)
ATTRIBUTE = re.compile(r"\[[^\]\n]+\]\s*")
UTF8_STRING = '[MarshalAs(UnmanagedType.LPUTF8Str)] string '
FORMAT = re.compile(r'(?:\[MarshalAs\(UnmanagedType\.LPUTF8Str\)\]\s*)?string fmt$')
# Each accepted scalar/reference consumes exactly one x register, never two or
# a hidden aggregate slot. TextAligned's audited float pair uses s0/s1 instead.
SINGLE_GPR_TYPES = frozenset({
    'string', 'int', 'nuint', 'void*', 'sbyte*', 'sbyte**', 'ImGuiTextBuffer*',
    'in ImVec4',
})


def _darwin_padding(name: str, native_prefix: str, result: str) -> int:
    if result not in ('void', 'byte', 'int'):
        raise ValueError(f'Unaudited variadic return ABI: {result} {name}')
    prefix = ATTRIBUTE.sub('', native_prefix).removesuffix(', ')
    if name == 'TextAligned':
        if prefix != 'float align_x, float width':
            raise ValueError(f'Unaudited TextAligned prefix: {prefix}')
        return 7  # Only fmt uses a GPR; the fixed floats use SIMD registers.
    types = [p.rsplit(' ', 1)[0] for p in prefix.split(', ')] if prefix else []
    if any(parameter_type not in SINGLE_GPR_TYPES for parameter_type in types):
        raise ValueError(f'Unaudited variadic fixed-parameter ABI: {name}({prefix})')
    fixed_gprs = len(types) + 1  # Include fmt, not the variadic strings.
    if fixed_gprs > 8:
        raise ValueError(f'Fixed variadic parameters already spill to the stack: {name}')
    return 8 - fixed_gprs


def _prefix(params: str) -> tuple[str, str, list[str]]:
    """Separate the format from preceding fixed parameters without changing ABI."""
    fixed, separator, fmt = params.rpartition(', ')
    if not separator:
        fixed, fmt = '', params
    if not FORMAT.fullmatch(fmt):
        raise ValueError(f'Expected a postprocessed string fmt parameter: {params}')
    managed = ATTRIBUTE.sub('', fixed)
    calls = []
    for parameter in managed.split(', ') if managed else []:
        words = parameter.split()
        modifier = words[0] + ' ' if words[0] in ('in', 'ref', 'out') else ''
        calls.append(modifier + words[-1])
    return fixed + ', ' if fixed else '', managed + ', ' if managed else '', calls


def rewrite_varargs(text: str) -> str:
    """Rewrite raw or legacy varargs, preserving native symbols and import attributes.

    Requires string/reference rewrites first. Darwin arm64 uses a separate fixed
    import exhausting x0-x7 before its pointer-only variadic tail. Unknown fixed
    parameter/return ABIs fail closed; no native va_list is constructed.
    """
    def replace(match: re.Match[str]) -> str:
        indent, attributes, result, name, params = match.group(
            'indent', 'attributes', 'result', 'name', 'params'
        )
        variadic = params.endswith(', __arglist')
        va_list = bool(re.search(r'\[NativeTypeName\("va_list"\)\] \w+\* \w+$', params))
        if not variadic and not va_list:
            return match.group(0)
        if name.endswith('NativeVaList'):
            return match.group(0)
        if '[DllImport(' not in attributes:
            raise ValueError(f'Missing native import attributes: {name}')
        if not variadic and not name.endswith(('V', 'v')):
            raise ValueError(f'Unknown va_list method: {name}')
        native_prefix, managed_prefix, calls = _prefix(params.rsplit(', ', 1)[0])
        call_prefix = ', '.join(calls) + ', ' if calls else ''
        summary = f'{indent}/// <summary>Passes literal UTF-8 text, or invariant-culture C# composite formatting when arguments are supplied.</summary>'
        public = f'{indent}public static {result} {name}({managed_prefix}string text, params object?[] args)'
        if not variadic:
            return '\n'.join([
                attributes + f'{indent}public static extern {result} {name}NativeVaList({params});',
                '', summary, public + ' =>',
                f'{indent}    {name[:-1]}({call_prefix}text, args);',
            ])

        padding = _darwin_padding(name, native_prefix, result)
        # "%s" would return the marshalled input pointer from the native temp
        # buffer helper. Two strings force its context-owned buffer path.
        temp_buffer = name == 'ImFormatStringToTempBuffer'
        suffix = f', {UTF8_STRING}suffix' if temp_buffer else ''
        tail = f'{UTF8_STRING}text{suffix}'
        fixed = f'{native_prefix}{UTF8_STRING}fmt, '
        pad_parameters = ''.join(f'nint __varargPad{i}, ' for i in range(8 - padding, 8))
        normal_import = attributes + f'{indent}private static extern {result} {name}NativeText({fixed}{tail});'
        darwin_import = attributes + f'{indent}private static extern {result} {name}NativeTextDarwinArm64({fixed}{pad_parameters}{tail});'
        fmt = '"%s%s"' if temp_buffer else '"%s"'
        tail_arguments = 'text, string.Empty' if temp_buffer else 'text'
        normal_arguments = f'{call_prefix}{fmt}, {tail_arguments}'
        darwin_arguments = f'{call_prefix}{fmt}, ' + '0, ' * padding + tail_arguments
        returns = '' if result == 'void' else 'return '
        return '\n'.join([
            normal_import, '', darwin_import, '', summary, public,
            f'{indent}' + '{',
            f'{indent}    text = args is {{ Length: 0 }} ? text : string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args);',
            f'{indent}    if (System.OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64)',
            f'{indent}    ' + '{',
            f'{indent}        {returns}{name}NativeTextDarwinArm64({darwin_arguments});',
            f'{indent}    ' + '}',
            f'{indent}    else',
            f'{indent}    ' + '{',
            f'{indent}        {returns}{name}NativeText({normal_arguments});',
            f'{indent}    ' + '}',
            f'{indent}' + '}',
        ])

    def upgrade_legacy(match: re.Match[str]) -> str:
        name = match.group('name')
        suffix = f', {UTF8_STRING}text'
        if name == 'ImFormatStringToTempBuffer':
            suffix += f', {UTF8_STRING}suffix'
        params = match.group('params')
        if not params.endswith(suffix):
            raise ValueError(f'Unrecognized legacy variadic import: {name}')
        preceding = params[:-len(suffix)]
        _, managed_prefix, calls = _prefix(preceding)
        call_prefix = ', '.join(calls) + ', ' if calls else ''
        expression = 'args is { Length: 0 } ? text : string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args)'
        fmt, tail = ('"%s%s"', ', string.Empty') if name == 'ImFormatStringToTempBuffer' else ('"%s"', '')
        expected = f'{name}NativeText({call_prefix}{fmt}, {expression}{tail})'
        if match.group('body') != expected or match.group('managed') != managed_prefix + 'string text, params object?[] args':
            raise ValueError(f'Unrecognized legacy variadic wrapper: {name}')
        return (match.group('attributes') + match.group('indent') +
                f'public static extern {match.group("result")} {name}({preceding}, __arglist);')

    rewritten = LEGACY_TEXT.sub(upgrade_legacy, text)
    rewritten = DECLARATION.sub(replace, rewritten)
    if '__arglist' in rewritten:
        raise ValueError('Unrecognized __arglist declaration remains')
    # Do not silently accept an old wrapper that no longer matches the precise
    # migration shape, nor an attribute-less/unrecognized private import.
    imports = re.findall(r'private static extern \w+ (\w+)NativeText\(', rewritten)
    padded = re.findall(r'private static extern \w+ (\w+)NativeTextDarwinArm64\(', rewritten)
    if sorted(imports) != sorted(padded):
        raise ValueError('Unrecognized unpadded NativeText import remains')
    return rewritten


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('paths', nargs='+', type=Path, help='C# files or generated directories (recursive)')
    parser.add_argument('--check', action='store_true', help='Fail if any file still requires conversion')
    args = parser.parse_args()
    changed = []
    for path in args.paths:
        files = sorted(path.rglob('*.cs')) if path.is_dir() else [path]
        for file in files:
            original = file.read_text(encoding='utf-8')
            rewritten = rewrite_varargs(original)
            if rewritten != original:
                changed.append(str(file))
                if not args.check:
                    with file.open('w', encoding='utf-8', newline='\n') as output:
                        output.write(rewritten)
    print(f'{len(changed)} file(s) {"require conversion" if args.check else "converted"}')
    if args.check and changed:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
