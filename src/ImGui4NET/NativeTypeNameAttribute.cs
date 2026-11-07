using System;
using System.Diagnostics;

namespace ImGui4NET;

/// <summary>
/// Records the C++ type a member was declared with. Only present in debug
/// builds; it exists so the generated bindings stay readable when inspecting
/// the native signature.
/// </summary>
[AttributeUsage(
    AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property
    | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue,
    AllowMultiple = false,
    Inherited = true)]
[Conditional("DEBUG")]
public sealed class NativeTypeNameAttribute : Attribute
{
    private readonly string _name;

    public NativeTypeNameAttribute(string name) => _name = name;

    public string Name => _name;
}

/// <summary>
/// Records an annotation found in a native declaration.
/// </summary>
[AttributeUsage(
    AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property
    | AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.ReturnValue,
    AllowMultiple = true,
    Inherited = false)]
[Conditional("DEBUG")]
public sealed class NativeAnnotationAttribute : Attribute
{
    private readonly string _annotation;

    public NativeAnnotationAttribute(string annotation) => _annotation = annotation;

    public string Annotation => _annotation;
}


