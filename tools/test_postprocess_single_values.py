import unittest

from postprocess import _split_top_level
from postprocess_single_values import rewrite_single_value_params


def transform(source):
    return rewrite_single_value_params(source, _split_top_level)


class SingleValueParameterTests(unittest.TestCase):
    def test_ref_wrapper_uses_fixed_address_and_preserves_default_arguments(self):
        source = '[DllImport("imgui", EntryPoint = "native_begin")]\n        public static extern byte Begin(string name, bool* p_open = null, int flags = 0);'
        result = transform(source)
        self.assertIn('private static unsafe extern byte Begin_Raw(string name, bool* p_open, int flags);', result)
        self.assertIn('public static unsafe byte Begin(string name, ref bool p_open, int flags = 0)', result)
        self.assertIn('public static unsafe byte Begin(string name, int flags = 0)', result)
        self.assertIn('Begin_Raw(name, null, flags)', result)
        self.assertIn('EntryPoint = "native_begin"', result)
        self.assertNotIn('bool* p_open', result.split('public static unsafe byte Begin', 1)[1])

    def test_required_ref_and_readonly_input_use_fixed_managed_byrefs(self):
        source = '\n'.join([
            '        public static extern byte Checkbox(string label, bool* v);',
            '        public static extern bool IsMousePosValid(ImVec2* mouse_pos);',
        ])
        result = transform(source)
        self.assertIn('private static unsafe extern byte Checkbox_Raw(string label, bool* v);', result)
        self.assertIn('public static unsafe byte Checkbox(string label, ref bool v)', result)
        self.assertIn('fixed (bool* __pointer1 = &v)', result)
        self.assertIn('public static unsafe bool IsMousePosValid(in ImVec2 mouse_pos)', result)
        self.assertIn('fixed (ImVec2* __pointer0 = &mouse_pos)', result)
        self.assertIn('IsMousePosValid_Raw(__pointer0)', result)

    def test_audited_nullable_input_preserves_native_default(self):
        source = '        public static extern void RenderTextClipped(in ImVec2 pos_min, ImVec2* text_size_if_known, in ImVec2 align = default, ImRect* clip_rect = null);'
        result = transform(source)
        self.assertIn('RenderTextClipped(in ImVec2 pos_min, in ImVec2 text_size_if_known, in ImVec2 align = default, ImRect? clip_rect = null)', result)
        self.assertIn('RenderTextClipped(in ImVec2 pos_min, ImVec2? text_size_if_known, in ImVec2 align, in ImRect clip_rect)', result)
        self.assertIn('RenderTextClipped(in ImVec2 pos_min, ImVec2? text_size_if_known, in ImVec2 align = default, ImRect? clip_rect = null)', result)
        self.assertIn('ImRect __value3 = clip_rect.GetValueOrDefault();', result)
        self.assertIn('RenderTextClipped_Raw(in pos_min, __pointer1, in align, clip_rect.HasValue ? &__value3 : null)', result)
        self.assertIn('RenderTextClipped_Raw(in pos_min, text_size_if_known.HasValue ? &__value1 : null, in align, __pointer3)', result)

    def test_style_defaults_use_current_style_or_local_value(self):
        source = '\n'.join([
            '        public static extern void StyleColorsDark(ImGuiStyle* dst = null);',
            '        public static extern void ShowStyleEditor(ImGuiStyle* @ref = null);',
        ])
        result = transform(source)
        self.assertIn('StyleColorsDark(ref ImGuiStyle dst)', result)
        self.assertIn('StyleColorsDark()', result)
        self.assertIn('StyleColorsDark_Raw(null)', result)
        self.assertIn('ShowStyleEditor(ref ImGuiStyle @ref)', result)
        self.assertIn('ShowStyleEditor()', result)
        self.assertIn('ShowStyleEditor_Raw(null)', result)

    def test_unreviewed_null_uses_a_local_and_preserves_other_defaults(self):
        source = '        public static extern byte Selectable(string label, bool* p_selected = null, int flags = 0, in ImVec2 size);'
        result = transform(source)
        self.assertIn('Selectable(string label, ref bool p_selected, int flags = 0, in ImVec2 size = default)', result)
        self.assertIn('bool __value1 = default;', result)
        self.assertIn('Selectable_Raw(label, &__value1, flags, in size)', result)
        self.assertNotIn('Selectable_Raw(label, null', result)

    def test_nullable_strings_with_generated_spacing_do_not_skip_rewrite(self):
        source = '        public static extern byte SliderFloat(string label, float* v, [MarshalAs(UnmanagedType.LPUTF8Str)] string ?format = null);'
        result = transform(source)
        self.assertIn('SliderFloat(string label, ref float v, string? format = null)', result)
        self.assertIn('SliderFloat_Raw(string label, float* v, [MarshalAs(UnmanagedType.LPUTF8Str)] string? format);', result)

    def test_out_values_use_locals_and_copy_back_or_discard(self):
        source = '        public static extern void GetTexDataAsAlpha8(ImFontAtlas* pThis, byte** out_pixels, int* out_width, int* out_height, int* out_bytes_per_pixel = null);'
        result = transform(source)
        self.assertIn('GetTexDataAsAlpha8_Raw(ImFontAtlas* pThis, byte** out_pixels, int* out_width, int* out_height, int* out_bytes_per_pixel);', result)
        self.assertIn('GetTexDataAsAlpha8(ImFontAtlas* pThis, out byte* out_pixels, out int out_width, out int out_height, out int out_bytes_per_pixel)', result)
        self.assertIn('byte* __value1 = default;', result)
        self.assertEqual(result.count('GetTexDataAsAlpha8_Raw(pThis, &__value1, &__value2, &__value3, &__value4)'), 2)
        self.assertIn('out_pixels = __value1;', result)
        self.assertIn('out_width = __value2;', result)
        self.assertIn('GetTexDataAsAlpha8(ImFontAtlas* pThis, out byte* out_pixels, out int out_width, out int out_height)', result)
        self.assertEqual(result.count('out_bytes_per_pixel = __value4;'), 1)

    def test_allocator_function_pointer_outputs_stay_typed(self):
        source = '        public static extern void GetAllocatorFunctions(delegate* unmanaged[Cdecl]<nuint, void*, void*>* p_alloc_func, delegate* unmanaged[Cdecl]<void*, void*, void>* p_free_func, void** p_user_data);'
        result = transform(source)
        self.assertIn('GetAllocatorFunctions(out delegate* unmanaged[Cdecl]<nuint, void*, void*> p_alloc_func, out delegate* unmanaged[Cdecl]<void*, void*, void> p_free_func, out void* p_user_data)', result)
        self.assertIn('delegate* unmanaged[Cdecl]<nuint, void*, void*> __value0 = default;', result)
        self.assertIn('p_alloc_func = __value0;', result)

    def test_array_pointers_and_resource_handles_are_unchanged(self):
        source = '\n'.join([
            '        public static extern byte DragFloat3(string label, float* v);',
            '        public static extern byte DragFloat(string label, [NativeTypeName("float[1]")] float* v);',
            '        public static extern void SetCurrentContext(ImGuiContext* ctx);',
            '        public static extern byte Checkbox(string label, [NativeTypeName("bool &")] bool* v);',
            '        public static extern byte Checkbox(string label, ref bool v);',
            '        public static extern byte Checkbox(string label, bool* v, __arglist);',
        ])
        self.assertEqual(source, transform(source))

    def test_raw_import_attributes_are_preserved_and_rewrite_is_idempotent(self):
        source = '[DllImport("imgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "native_symbol", ExactSpelling = true)]\n        public static extern byte Checkbox(string label, bool* v);'
        result = transform(source)
        self.assertEqual(result, transform(result))
        self.assertIn('[DllImport("imgui", CallingConvention = CallingConvention.Cdecl, EntryPoint = "native_symbol", ExactSpelling = true)]', result)
        self.assertIn('private static unsafe extern byte Checkbox_Raw', result)
        self.assertNotIn('public static extern', result)


if __name__ == '__main__':
    unittest.main()
