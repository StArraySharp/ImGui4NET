"""Dependency-free regression tests: python -m unittest discover -s tools -p test_postprocess_varargs.py."""
import re
import shutil
import subprocess
import unittest

from postprocess_varargs import rewrite_varargs


STRING = '[MarshalAs(UnmanagedType.LPUTF8Str)] string '
VA_LIST = '[NativeTypeName("va_list")] sbyte* args'


def declaration(name, parameters, result='void', convention='Cdecl'):
    return (
        f'        [DllImport("imgui", CallingConvention = CallingConvention.{convention}, '
        f'EntryPoint = "_native_{name}", ExactSpelling = true)]\n'
        f'        public static extern {result} {name}({parameters});'
    )


class VarargsTests(unittest.TestCase):
    def test_all_shapes_and_v_variants(self):
        shapes = [
            ('Text', '', 'void'),
            ('TextColored', 'in ImVec4 col, ', 'void'),
            ('LabelText', STRING + 'label, ', 'void'),
            ('TreeNode', 'void* ptr_id, ', 'byte'),
            ('TreeNodeEx', STRING + 'str_id, int flags, ', 'byte'),
            ('TextAligned', 'float align_x, float width, ', 'void'),
            ('ImFormatString', 'sbyte* buf, nuint buf_size, ', 'int'),
            ('ImFormatStringToTempBuffer', 'sbyte** out_buf, sbyte** out_buf_end, ', 'void'),
            ('appendf', 'ImGuiTextBuffer* pThis, ', 'void'),
        ]
        for name, prefix, result in shapes:
            with self.subTest(name=name):
                vname = name + ('v' if name == 'appendf' else 'V')
                original = '\n'.join([
                    declaration(name, prefix + STRING + 'fmt, __arglist', result),
                    declaration(vname, prefix + STRING + 'fmt, ' + VA_LIST, result, 'ThisCall'),
                ])
                rewritten = rewrite_varargs(original)
                self.assertNotIn('__arglist', rewritten)
                self.assertEqual(rewrite_varargs(rewritten), rewritten)
                original_imports = re.findall(r'\[DllImport[^\n]+', original)
                self.assertEqual([original_imports[0], original_imports[0], original_imports[1]],
                                 re.findall(r'\[DllImport[^\n]+', rewritten))
                suffix = f', {STRING}suffix' if name == 'ImFormatStringToTempBuffer' else ''
                self.assertIn(f'{name}NativeText({prefix}{STRING}fmt, {STRING}text{suffix})', rewritten)
                self.assertIn(f'{vname}NativeVaList({prefix}{STRING}fmt, {VA_LIST})', rewritten)
                self.assertIn('"%s%s", text, string.Empty)' if name == 'ImFormatStringToTempBuffer'
                              else '"%s", text)', rewritten)
                padding = 7 if name == 'TextAligned' else 7 - (len(prefix.split(', ')) - 1 if prefix else 0)
                self.assertEqual(rewritten.count('nint __varargPad'), padding)
                self.assertIn('System.OperatingSystem.IsMacOS() && ', rewritten)
                self.assertIn('RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64', rewritten)
                self.assertEqual(rewritten.count('params object?[] args'), 2)
                self.assertEqual(rewritten.count('InvariantCulture, text, args)'), 1)
                self.assertEqual(rewritten.count(f'public static {result} {name}('), 1)
                self.assertEqual(rewritten.count(f'public static {result} {vname}('), 1)
                self.assertIn(f'{vname}(', rewritten)
                if name == 'TextColored':
                    self.assertIn('TextColoredNativeText(in col, "%s", text)', rewritten)
                if name == 'ImFormatString':
                    self.assertIn('public static int ImFormatString(sbyte* buf, nuint buf_size, string text, params object?[] args)', rewritten)
                if name == 'ImFormatStringToTempBuffer':
                    self.assertIn('"%s%s", 0, 0, 0, 0, 0, text, string.Empty)', rewritten)

    def test_import_attributes_are_copied_verbatim(self):
        original = declaration('TreeNode', STRING + 'label, ' + STRING + 'fmt, __arglist', 'byte')
        original = original.replace('ExactSpelling = true', 'ExactSpelling = true, SetLastError = true, CharSet = CharSet.Ansi')
        original = original.replace('        public static', '        [return: NativeTypeName("bool")]\n        public static')
        rewritten = rewrite_varargs(original)
        attributes = original.split('        public static')[0]
        self.assertEqual(rewritten.count(attributes), 2)
        self.assertEqual(rewrite_varargs(rewritten), rewritten)

    def test_legacy_upgrade_is_local_and_idempotent(self):
        original = declaration('Text', STRING + 'fmt, ' + STRING + 'text').replace(
            'public static extern void Text(', 'private static extern void TextNativeText('
        ) + '''

        /// <summary>Passes literal UTF-8 text, or invariant-culture C# composite formatting when arguments are supplied.</summary>
        public static void Text(string text, params object?[] args) =>
            TextNativeText("%s", args is { Length: 0 } ? text : string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args));'''
        unrelated = declaration('Unrelated', 'float* values')
        updated = rewrite_varargs(original + '\n' + unrelated)
        self.assertIn('TextNativeTextDarwinArm64(', updated)
        self.assertTrue(updated.endswith(unrelated))
        self.assertEqual(rewrite_varargs(updated), updated)
        with self.assertRaises(ValueError):
            rewrite_varargs(original.replace('TextNativeText("%s",', 'TextNativeText(text,'))

    def test_unaudited_abis_fail_closed(self):
        for prefix in ('ImVec4 col, ', 'long count, ', 'double value, ', 'float value, ',
                       'ref int count, ', 'in Unknown value, ',
                       ', '.join(f'int a{i}' for i in range(8)) + ', '):
            with self.subTest(prefix=prefix), self.assertRaises(ValueError):
                rewrite_varargs(declaration('Text', prefix + STRING + 'fmt, __arglist'))
        with self.assertRaises(ValueError):
            rewrite_varargs(declaration('TextAligned', 'float other, float width, ' + STRING + 'fmt, __arglist'))
        with self.assertRaises(ValueError):
            rewrite_varargs(declaration('Text', STRING + 'fmt, __arglist', 'ImVec4'))

    @unittest.skipUnless(shutil.which('clang'), 'clang is required for the static Darwin arm64 ABI check')
    def test_clang_darwin_arm64_variadic_stack_slots(self):
        # Independent expected padding counts; fixed floats occupy s0/s1, not x0/x1.
        shapes = [
            ('Text', '', 7),
            ('TextColored', 'in ImVec4 col, ', 6),
            ('LabelText', STRING + 'label, ', 6),
            ('TreeNode', STRING + 'str_id, ', 6),
            ('TreeNode', 'void* ptr_id, ', 6),
            ('TreeNodeEx', STRING + 'str_id, int flags, ', 5),
            ('TreeNodeEx', 'void* ptr_id, int flags, ', 5),
            ('TextAligned', 'float align_x, float width, ', 7),
            ('ImFormatString', 'sbyte* buf, nuint buf_size, ', 5),
            ('ImFormatStringToTempBuffer', 'sbyte** out_buf, sbyte** out_buf_end, ', 5),
            ('appendf', 'ImGuiTextBuffer* pThis, ', 6),
        ]
        c_types = {'string': 'const char*', 'in ImVec4': 'const void*', 'void*': 'void*',
                   'sbyte*': 'char*', 'sbyte**': 'const char**', 'ImGuiTextBuffer*': 'void*',
                   'int': 'int', 'nuint': 'unsigned long', 'nint': 'long', 'float': 'float'}
        c_source = []
        for index, (name, prefix, padding) in enumerate(shapes):
            rewritten = rewrite_varargs(declaration(name, prefix + STRING + 'fmt, __arglist'))
            parameters = re.search(r'NativeTextDarwinArm64\(([^\n]+)\);', rewritten)[1]
            parameters = re.sub(r'\[[^\]]+\]\s*', '', parameters).split(', ')
            self.assertEqual(sum(p.startswith('nint ') for p in parameters), padding)
            fixed_types, fixed_values, real_types, real_values = [], [], [], []
            for parameter in parameters:
                kind, parameter_name = parameter.rsplit(' ', 1)
                c_type = c_types[kind]
                if parameter_name.startswith('__varargPad'):
                    value = '0'
                elif parameter_name in ('text', 'suffix'):
                    value = parameter_name
                elif parameter_name == 'fmt':
                    value = '"%s%s"' if name == 'ImFormatStringToTempBuffer' else '"%s"'
                elif kind == 'float':
                    value = '1.25f'
                elif '*' in c_type:
                    value = f'({c_type})17'
                else:
                    value = '17'
                fixed_types.append(c_type)
                fixed_values.append(value)
                if not parameter_name.startswith('__varargPad'):
                    real_values.append(value)
                    if parameter_name not in ('text', 'suffix'):
                        real_types.append(c_type)
            c_source.extend([
                f'extern void fixed{index}({", ".join(fixed_types)});',
                f'extern void variadic{index}({", ".join(real_types)}, ...);',
                f'void probe_fixed{index}(const char* text, const char* suffix) {{ fixed{index}({", ".join(fixed_values)}); }}',
                f'void probe_variadic{index}(const char* text, const char* suffix) {{ variadic{index}({", ".join(real_values)}); }}',
            ])
        process = subprocess.run([
            shutil.which('clang'), '-target', 'arm64-apple-macos', '-x', 'c', '-',
            '-S', '-O1', '-fno-optimize-sibling-calls', '-o', '-',
        ], input='\n'.join(c_source), text=True, capture_output=True)
        self.assertEqual(process.returncode, 0, process.stderr)
        for index, (name, _, _) in enumerate(shapes):
            with self.subTest(name=name, index=index):
                def tail_store(kind):
                    body = re.search(rf'^_probe_{kind}{index}:.*?\.cfi_endproc', process.stdout, re.M | re.S)[0]
                    return re.findall(r'\b(?:str|stp)\s+x0(?:,\s*x1)?,\s*\[sp\]', body)
                padded_store = tail_store('fixed')
                self.assertEqual(len(padded_store), 1, process.stdout)
                self.assertEqual(padded_store, tail_store('variadic'))
                self.assertTrue(padded_store[0].startswith('stp' if name == 'ImFormatStringToTempBuffer' else 'str'))

    def test_unrelated_pointer_signature_untouched(self):
        original = declaration('Input', 'float* values, int count')
        self.assertEqual(rewrite_varargs(original), original)

    def test_unknown_format_shape_fails_closed(self):
        with self.assertRaises(ValueError):
            rewrite_varargs(declaration('Bad', 'int flags, __arglist'))

    def test_unknown_varargs_declaration_fails_closed(self):
        with self.assertRaises(ValueError):
            rewrite_varargs('public extern void Bad(__arglist);')


if __name__ == '__main__':
    unittest.main()
