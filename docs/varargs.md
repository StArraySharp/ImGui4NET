# Managed text and formatting APIs

C# `__arglist` bindings have been replaced. Each former variadic method now has
one public managed method backed by private fixed-signature imports, including
`TextColored`, existing tree-node variants, internal format-buffer functions,
and `ImGuiTextBuffer.appendf`. No new same-name overloads are added:

```csharp
ImGui.Text("Progress: 50% {literal braces}");
ImGui.Text("Progress: {0:F1}%", 50.25);
ImGui.TextColored(in color, "Value: {0:N2}", value);
ImGui.LabelText("status", $"Progress: {progress:F1}%");
ImGuiTextBuffer.appendf(buffer, "Count: {0}", count);
```

Each method takes `string text, params object?[] args` after its original fixed
parameters. With no arguments (including an explicitly empty array), text is
literal: neither `%` nor braces are interpreted. With nonempty arguments, it uses
`string.Format(CultureInfo.InvariantCulture, text, args)` before crossing the
native boundary. All supplied arguments reach `string.Format`; invalid composite
formats throw `FormatException` before calling native code. A null argument array
throws `ArgumentNullException` through `string.Format`.
`params` retains the standard C# object-array expansion rules: cast an array to
`object` if it represents one formatted argument rather than the argument list.
For interpolated strings, interpolation uses the caller's C# culture rules before
this API receives the resulting literal string. Native C strings still terminate
at an embedded NUL.

This is an intentional source-breaking format-language change. `printf` formats
such as `%d`, `%s`, `%.2f`, `%p`, positional/width arguments and `%%` are **not** C#
composite formats. Convert them to `{0}`, `{1}`, `{0:F2}`, explicit pointer
formatting, and literal `%` respectively. In a composite format, escape literal
braces as `{{` and `}}`; do not escape braces when passing text without arguments. C#
formatting does not parse `printf` syntax: `Text("%d", value)` formats to the
literal `%d`, so existing printf call sites must be migrated. There were no
`__arglist` calls or calls to these variadic bindings in the checked-in sample;
it already uses `TextUnformatted` and C# interpolation.

## Native boundary and V variants

Private `*NativeText` imports retain the original native symbol, calling
convention, fixed parameter types and return type. Their fixed managed signature
adds a UTF-8 string argument in place of the C ellipsis. Wrappers pass the constant
native format `"%s"` plus the formatted/literal text. The exception is
`ImFormatStringToTempBuffer`: native code special-cases `"%s"` by returning the
input pointer, which would point to freed marshalled memory after the call.
Its import therefore takes two strings and passes `"%s%s"`, text, and an empty
string, forcing formatting into the native context-owned temporary buffer. Both
returned pointers retain the native temporary-buffer lifetime (until a subsequent
formatting operation/context destruction), not a managed marshaller lifetime.
This uses only pointer arguments in the variadic tail and never sends a
user-controlled format to native `printf`. The native library and exported ABI are unchanged;
no new native shim or exported symbol is required. As with the existing mangled
bindings, the native binary must match the generated target ABI. No floating
point or arbitrary object is passed directly through a variadic ABI.

### Darwin arm64 variadic stack layout

Apple's [Writing ARM64 code for Apple platforms](https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms),
under “Update code that passes arguments to variadic functions,” specifies
8-byte stack slots for variadic arguments. The non-variadic fixed parameters
continue to use their normal registers. A plain fixed-signature `fmt, text`
import would therefore incorrectly put `text` in a register on Darwin arm64.
Fetching the official page was blocked by this environment's DNS/security
checks; the link is a reference, not a claim of successful retrieval. The ABI
evidence obtained for this change is the local clang assembly check below.

Each managed method formats once, then selects `*NativeTextDarwinArm64` only when
`OperatingSystem.IsMacOS()` and `RuntimeInformation.ProcessArchitecture` is
`Architecture.Arm64`. This private import retains all original import attributes
(including library, symbol, calling convention and exact spelling) and fixed
parameters, then adds `nint` padding **after fmt** to exhaust GPRs x0–x7 before
the UTF-8 string tail. The first string then occupies sp+0; the temporary-buffer
helper's second string occupies sp+8. Other platforms use the original fixed
import. V text methods forward through the same platform dispatch; the expert
`*NativeVaList` imports remain untouched because they already have a fixed ABI.

Generation explicitly whitelists each accepted fixed-parameter type as exactly
one GPR: `string`, `int`, `nuint`, `void*`, `sbyte*`, `sbyte**`,
`ImGuiTextBuffer*`, and `in ImVec4`. The audited exception is `TextAligned`'s
exact `float align_x, float width` prefix: these fixed arguments use SIMD s0/s1,
consume no GPRs, and leave fmt in x0, requiring seven padding slots. Unknown
shapes, by-value aggregates, unsupported returns and fixed prefixes already
spilling onto the stack fail closed. Current integer/reference prefixes need
five to seven padding slots. No object layout or fabricated `va_list` is used.

The regression suite invokes clang with `-target arm64-apple-macos -S` on
header-free C supplied via standard input. For all 11 current prefix shapes it
compares padded fixed-signature callers with real variadic callers: the first
string is stored at sp+0, and the two-string helper stores its second pointer at
sp+8. It also checks copied import attributes, unsupported-ABI rejection,
legacy-output migration and idempotence. The compiler test skips explicitly if
clang is unavailable. This is a **static ABI check**, not execution of .NET
marshalling on Apple hardware. **The Darwin arm64 path has not been tested on a
real Mac.** Windows native smoke tests exercise only the non-Darwin branch.

For every original V method (including `appendfv`), the old name now exposes the
same single managed text/format method and routes through the matching safe
non-V method. For example, `TextColoredV(in color, "{0:F2}", value)` is safe and
has the same managed formatting semantics as `TextColored`.

The exact original V import remains available as `*NativeVaList`, for example
`TextVNativeVaList`, `TextColoredVNativeVaList`, and `appendfvNativeVaList`.
These are expert-only unsafe APIs: their `fmt` is a **native printf format** and
the final pointer must be a valid native `va_list` from the matching platform,
compiler ABI, thread and live call frame. The generator preserves its declared
pointer and all original parameter annotations. A pointer to managed objects,
a pinned argument array, or `RuntimeArgumentHandle` is not a portable `va_list`.
This library neither constructs nor stores native `va_list` values. Ordinary
managed callers should use the safe text methods. C# `params object?[]` must never
be passed to `*NativeVaList`.

## Generator integration

`tools/postprocess_varargs.py` exports the pure, idempotent function:

```python
from postprocess_varargs import rewrite_varargs

text = rewrite_varargs(text)
```

Call it in `postprocess.py` after string and reference parameter rewriting and
before splitting types/writing output. It also works on already split files.
The single-value pointer pass handles a separate set of declarations and leaves
`__arglist` declarations for this pass. The function processes all matching
text declarations rather than maintaining a
fragile list of method names, and fails on unrecognized `__arglist` signatures.
It does not alter unrelated pointer signatures, exported symbols, or calling
conventions. The main `tools/postprocess.py` pipeline runs this pass immediately
following the single-value pointer rewrite.

Standalone conversion and validation from the repository root:

```powershell
python tools\postprocess_varargs.py src\ImGui4NET\Generated
python tools\postprocess_varargs.py --check src\ImGui4NET\Generated
python -m unittest discover -s tools -p test_postprocess_varargs.py
dotnet build src\ImGui4NET\ImGui4NET.csproj --no-restore
```

The optional real-native regression harness is `tools/varargs-smoke`. It covers
UTF-8, literal percent/braces, invariant formatting, V forwarding, return lengths,
truncation, invalid formats/null arguments, struct append, and temporary-buffer
pointer lifetime. It also exercises single-value pointer APIs: real mouse-click
`ref bool` write-back, `in` positions, `out` allocator/font-atlas results, and
default window, style, and clipping overloads. These checks have passed with
the Windows native binary. Pass a matching native DLL path:

```powershell
dotnet run --project tools\varargs-smoke\VarargsSmoke.csproj --no-restore -- samples\ImGui4NET.Sample.OpenGL3\bin\Debug\net10.0\imgui.dll
```

On a fresh harness checkout, the first command reports missing `project.assets.json`;
restore that project with `dotnet restore tools\varargs-smoke\VarargsSmoke.csproj`
and rerun. It declares no external package dependencies.
`--check` exits nonzero if conversion would change a file; a second conversion
leaves the generated bytes unchanged.
