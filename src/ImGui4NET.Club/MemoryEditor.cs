using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ImGui4NET.Club;

/// <summary>
/// Managed port of <c>imgui_club</c>'s <c>MemoryEditor</c>: a hex/ASCII memory
/// viewer and editor.
/// </summary>
/// <remarks>
/// The C++ original is a header-only class holding its state inline and
/// formatting through <c>printf</c>. This port keeps the same layout of the UI
/// and the same behaviour, but:
/// <list type="bullet">
/// <item>state lives in managed fields instead of raw native memory;</item>
/// <item>the two text buffers required by <c>InputText</c> are allocated with
/// <see cref="NativeMemory"/> because they are edited in place by Dear ImGui;</item>
/// <item>formatting uses <see cref="string.Format(IFormatProvider?, string, object?)"/>
/// equivalents instead of <c>snprintf</c>.</item>
/// </list>
/// </remarks>
public sealed unsafe class MemoryEditor : IDisposable
{
    // -----------------------------------------------------------------------
    // Settings
    // -----------------------------------------------------------------------

    /// <summary>Whether the standalone window is open.</summary>
    public bool Open = true;

    /// <summary>Prevents editing the underlying memory.</summary>
    public bool ReadOnly;

    /// <summary>Number of bytes displayed per row.</summary>
    public int Cols = 16;

    /// <summary>Show the "Options" button and range line.</summary>
    public bool OptShowOptions = true;

    /// <summary>Show the data preview panel.</summary>
    public bool OptShowDataPreview;

    /// <summary>Render printable bytes as characters instead of hex.</summary>
    public bool OptShowHexII;

    /// <summary>Show the ASCII column.</summary>
    public bool OptShowAscii = true;

    /// <summary>Dim zero bytes.</summary>
    public bool OptGreyOutZeroes = true;

    /// <summary>Format hex digits in upper case.</summary>
    public bool OptUpperCaseHex = true;

    /// <summary>Insert extra spacing every N columns; 0 disables it.</summary>
    public int OptMidColsCount = 8;

    /// <summary>Force the address column width; 0 derives it from the range.</summary>
    public int OptAddrDigitsCount;

    /// <summary>Extra height reserved below the contents.</summary>
    public float OptFooterExtraHeight;

    /// <summary>Background colour used for highlighted ranges.</summary>
    public uint HighlightColor = 0x32FFFFFF;

    // -----------------------------------------------------------------------
    // Callbacks
    // -----------------------------------------------------------------------

    /// <summary>Optional reader; defaults to a direct byte load.</summary>
    public delegate* unmanaged[Cdecl]<byte*, nuint, void*, byte> ReadFn;

    /// <summary>Optional writer; defaults to a direct byte store.</summary>
    public delegate* unmanaged[Cdecl]<byte*, nuint, byte, void*, void> WriteFn;

    /// <summary>Optional per-byte highlight predicate.</summary>
    public delegate* unmanaged[Cdecl]<byte*, nuint, void*, byte> HighlightFn;

    /// <summary>Optional per-byte background colour provider.</summary>
    public delegate* unmanaged[Cdecl]<byte*, nuint, void*, uint> BgColorFn;

    /// <summary>Opaque pointer forwarded to the callbacks above.</summary>
    public void* UserData;

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    /// <summary>True while the mouse hovers a byte.</summary>
    public bool MouseHovered;

    /// <summary>Address under the mouse while hovering.</summary>
    public nuint MouseHoveredAddr;

    /// <summary>Set when the window width must be recomputed.</summary>
    public bool ContentsWidthChanged;

    private nuint _dataPreviewAddr = NoAddress;
    private nuint _dataEditingAddr = NoAddress;
    private bool _dataEditingTakeFocus;
    private nuint _gotoAddr = NoAddress;
    private nuint _highlightMin = NoAddress;
    private nuint _highlightMax = NoAddress;
    private int _previewEndianness;
    private int _previewDataType = (int)ImGuiDataType.ImGuiDataType_S32;

    private static readonly nuint NoAddress = unchecked((nuint)(-1));

    // The two buffers Dear ImGui edits in place. They must stay alive for the
    // lifetime of the editor and must not move, hence unmanaged allocation.
    private const int InputBufSize = 32;
    private sbyte* _dataInputBuf;
    private sbyte* _addrInputBuf;

    /// <summary>
    /// Per-call payload handed to <see cref="DataInputCallback"/>.
    /// </summary>
    /// <remarks>
    /// The address of an instance is passed as <c>user_data</c>, so it lives on
    /// the stack for the duration of one <c>InputText</c> call and must stay
    /// blittable.
    /// <para>
    /// <see cref="DataInputUserData.CurrentBufOverwrite"/> is <c>char[3]</c> in
    /// C++ and holds the two hex digits plus a terminator.
    /// </para>
    /// </remarks>
    private struct DataInputUserData
    {
        public fixed sbyte CurrentBufOverwrite[3];
        public int CursorPos;
    }

    private bool _disposed;

    // Scratch buffer for the data preview; needs no persistence.
    private const int PreviewBufSize = 128;

    /// <summary>Creates an editor with the upstream defaults.</summary>
    public MemoryEditor()
    {
        _dataInputBuf = (sbyte*)NativeMemory.AllocZeroed(InputBufSize);
        _addrInputBuf = (sbyte*)NativeMemory.AllocZeroed(InputBufSize);
    }

    /// <summary>Releases the unmanaged input buffers.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NativeMemory.Free(_dataInputBuf);
        NativeMemory.Free(_addrInputBuf);
        _dataInputBuf = null;
        _addrInputBuf = null;
        _disposed = true;
    }

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------

    /// <summary>Scrolls to <paramref name="addrMin"/> and highlights a range.</summary>
    public void GotoAddrAndHighlight(nuint addrMin, nuint addrMax)
    {
        _gotoAddr = addrMin;
        _highlightMin = addrMin;
        _highlightMax = addrMax;
    }

    // -----------------------------------------------------------------------
    // Layout metrics
    // -----------------------------------------------------------------------

    /// <summary>Measurements derived from the font and the current options.</summary>
    public struct Sizes
    {
        /// <summary>Digits needed to represent the highest address.</summary>
        public int AddrDigitsCount;

        /// <summary>Height of one line, without spacing.</summary>
        public float LineHeight;

        /// <summary>Width of one glyph; a monospace font is assumed.</summary>
        public float GlyphWidth;

        /// <summary>Width of one hex cell, roughly 2.5 glyphs.</summary>
        public float HexCellWidth;

        /// <summary>Extra space inserted between column groups.</summary>
        public float SpacingBetweenMidCols;

        /// <summary>X offset of the first hex cell.</summary>
        public float OffsetHexMinX;

        /// <summary>X offset just past the last hex cell.</summary>
        public float OffsetHexMaxX;

        /// <summary>X offset of the ASCII column.</summary>
        public float OffsetAsciiMinX;

        /// <summary>X offset just past the ASCII column.</summary>
        public float OffsetAsciiMaxX;

        /// <summary>Ideal window width for the current options.</summary>
        public float WindowWidth;
    }

    /// <summary>Computes the layout metrics for the given memory range.</summary>
    public void CalcSizes(out Sizes s, nuint memSize, nuint baseDisplayAddr)
    {
        ImGuiStyle* style = ImGui.GetStyle();

        s = default;
        s.AddrDigitsCount = OptAddrDigitsCount;

        if (s.AddrDigitsCount == 0)
        {
            for (nuint n = baseDisplayAddr + memSize - 1; n > 0; n >>= 4)
            {
                s.AddrDigitsCount++;
            }
        }

        s.LineHeight = ImGui.GetTextLineHeight();
        s.GlyphWidth = ImGui.CalcTextSize("F", null, 0, -1f).x + 1f;
        s.HexCellWidth = (int)(s.GlyphWidth * 2.5f);
        s.SpacingBetweenMidCols = (int)(s.HexCellWidth * 0.25f);
        s.OffsetHexMinX = (s.AddrDigitsCount + 2) * s.GlyphWidth;
        s.OffsetHexMaxX = s.OffsetHexMinX + s.HexCellWidth * Cols;
        s.OffsetAsciiMinX = s.OffsetAsciiMaxX = s.OffsetHexMaxX;

        if (OptShowAscii)
        {
            s.OffsetAsciiMinX = s.OffsetHexMaxX + s.GlyphWidth;

            if (OptMidColsCount > 0)
            {
                s.OffsetAsciiMinX += (Cols + OptMidColsCount - 1) / OptMidColsCount * s.SpacingBetweenMidCols;
            }

            s.OffsetAsciiMaxX = s.OffsetAsciiMinX + Cols * s.GlyphWidth;
        }

        s.WindowWidth = s.OffsetAsciiMaxX
            + style->ScrollbarSize
            + style->WindowPadding.x * 2f
            + s.GlyphWidth;
    }

    // -----------------------------------------------------------------------
    // Standalone window
    // -----------------------------------------------------------------------

    /// <summary>
    /// Draws the editor as its own window. The window owns the
    /// <c>Begin</c>/<c>End</c> pair.
    /// </summary>
    public void DrawWindow(string title, void* memData, nuint memSize, nuint baseDisplayAddr = 0)
    {
        CalcSizes(out Sizes s, memSize, baseDisplayAddr);

        ImVec2 defaultSize = new() { x = s.WindowWidth, y = s.WindowWidth * 0.60f };
        int firstUse = (int)ImGuiCond.ImGuiCond_FirstUseEver;
        ImGui.SetNextWindowSize(&defaultSize, firstUse);

        ImVec2 minSize = new() { x = 0f, y = 0f };
        ImVec2 maxSize = new() { x = s.WindowWidth, y = float.MaxValue };
        ImGui.SetNextWindowSizeConstraints(&minSize, &maxSize, null, null);

        Open = true;
        bool openFlag = Open;
        int flags = (int)ImGuiWindowFlags.ImGuiWindowFlags_NoScrollbar;

        if (ImGui.Begin(title, &openFlag, flags) != 0)
        {
            DrawContents(memData, memSize, baseDisplayAddr);

            if (ContentsWidthChanged)
            {
                CalcSizes(out s, memSize, baseDisplayAddr);
                ImVec2 newSize = new() { x = s.WindowWidth, y = ImGui.GetWindowSize().y };
                ImGui.SetWindowSize(&newSize, 0);
            }
        }

        Open = openFlag;
        ImGui.End();
    }

    // -----------------------------------------------------------------------
    // Contents
    // -----------------------------------------------------------------------

    /// <summary>
    /// Draws only the editor body, for embedding inside an existing window.
    /// </summary>
    public void DrawContents(void* memDataVoid, nuint memSize, nuint baseDisplayAddr = 0)
    {
        if (Cols < 1)
        {
            Cols = 1;
        }

        byte* memData = (byte*)memDataVoid;
        CalcSizes(out Sizes s, memSize, baseDisplayAddr);
        ImGuiStyle* style = ImGui.GetStyle();

        ImVec2 contentsPosStart = ImGui.GetCursorScreenPos();

        // The clipping region is a child window started with NoMove so that a
        // click inside it is not interpreted as a window drag: the click
        // detection below deliberately does not set ActiveId.
        float heightSeparator = style->ItemSpacing.y;
        float footerHeight = OptFooterExtraHeight;

        if (OptShowOptions)
        {
            footerHeight += heightSeparator + ImGui.GetFrameHeightWithSpacing();
        }

        if (OptShowDataPreview)
        {
            footerHeight += heightSeparator
                + ImGui.GetFrameHeightWithSpacing()
                + ImGui.GetTextLineHeightWithSpacing() * 3f;
        }

        // C++ writes this as ImVec2(-FLT_MIN, -footer_height): -FLT_MIN is the
        // smallest positive normal negated, a sentinel meaning "fill the
        // remaining width". C#'s float.MinValue is -FLT_MAX instead, which
        // would make BeginChild clamp the width to 4 pixels and hide the data.
        ImVec2 scrollingSize = new() { x = -1.17549435e-38f, y = -footerHeight };
        int childFlags = (int)ImGuiChildFlags.ImGuiChildFlags_None;
        int childWindowFlags = (int)ImGuiWindowFlags.ImGuiWindowFlags_NoMove
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoNav;

        ImGui.BeginChild("##scrolling", &scrollingSize, childFlags, childWindowFlags);
        ImDrawList* drawList = ImGui.GetWindowDrawList();

        ImVec2 zeroPad = new() { x = 0f, y = 0f };
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_FramePadding, &zeroPad);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_ItemSpacing, &zeroPad);

        ImVec2 availSize = ImGui.GetContentRegionAvail();
        int lineTotalCount = (int)((memSize + (nuint)Cols - 1) / (nuint)Cols);

        ImGuiListClipper clipper;
        ImGuiListClipper.ImGuiListClipper_Ctor(&clipper);
        ImGuiListClipper.Begin(&clipper, lineTotalCount, s.LineHeight);

        bool dataNext = false;

        if (_dataEditingAddr >= memSize)
        {
            _dataEditingAddr = NoAddress;
        }

        if (_dataPreviewAddr >= memSize)
        {
            _dataPreviewAddr = NoAddress;
        }

        nuint previewDataTypeSize = OptShowDataPreview ? DataTypeGetSize(_previewDataType) : 0;
        nuint dataEditingAddrNext = NoAddress;

        if (_dataEditingAddr != NoAddress)
        {
            // The cursor move is applied on the next frame so that scrolling
            // stays in sync; the scroll position cannot change mid-render.
            if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_UpArrow, 1) != 0
                && (nint)_dataEditingAddr >= (nint)Cols)
            {
                dataEditingAddrNext = _dataEditingAddr - (nuint)Cols;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_DownArrow, 1) != 0
                && (nint)_dataEditingAddr < (nint)memSize - Cols)
            {
                dataEditingAddrNext = _dataEditingAddr + (nuint)Cols;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_LeftArrow, 1) != 0
                && (nint)_dataEditingAddr > 0)
            {
                dataEditingAddrNext = _dataEditingAddr - 1;
            }
            else if (ImGui.IsKeyPressed(ImGuiKey.ImGuiKey_RightArrow, 1) != 0
                && (nint)_dataEditingAddr < (nint)memSize - 1)
            {
                dataEditingAddrNext = _dataEditingAddr + 1;
            }
        }

        ImVec2 windowPos = ImGui.GetWindowPos();
        if (OptShowAscii)
        {
            ImVec2 from = new()
            {
                x = windowPos.x + s.OffsetAsciiMinX - s.GlyphWidth,
                y = windowPos.y,
            };

            ImVec2 to = new()
            {
                x = from.x,
                y = windowPos.y + ImGui.GetWindowSize().y,
            };

            ImDrawList.AddLine(drawList, &from, &to, ImGui.GetColorU32((int)ImGuiCol.ImGuiCol_Border), 1f);
        }

        uint colorText = ImGui.GetColorU32((int)ImGuiCol.ImGuiCol_Text);
        uint colorDisabled = OptGreyOutZeroes
            ? ImGui.GetColorU32((int)ImGuiCol.ImGuiCol_TextDisabled)
            : colorText;

        // .NET has no "%02X " equivalent: a trailing space turns "X2 " into a
        // custom format string that is emitted literally. The separator is
        // appended after formatting instead.
        string byteFormat = OptUpperCaseHex ? "X2" : "x2";

        MouseHovered = false;
        MouseHoveredAddr = 0;

        while (ImGuiListClipper.Step(&clipper) != 0)
        {
            for (int lineI = clipper.DisplayStart; lineI < clipper.DisplayEnd; lineI++)
            {
                nuint addr = (nuint)lineI * (nuint)Cols;

                DrawAddressLine(drawList, s, lineI, baseDisplayAddr, addr, colorText, OptUpperCaseHex);

                addr = (nuint)lineI * (nuint)Cols;
                DrawHexCells(drawList, memData, memSize, s, ref addr, colorText, colorDisabled,
                    byteFormat, previewDataTypeSize, ref dataNext, ref dataEditingAddrNext);

                if (OptShowAscii)
                {
                    DrawAsciiCells(drawList, memData, memSize, s, lineI, colorText, colorDisabled);
                }
            }
        }

        ImGuiListClipper.Dispose(&clipper);

        ImGui.PopStyleVar(2);
        float childWidth = ImGui.GetWindowSize().x;
        ImGui.EndChild();

        // Report the ideal child width to the parent window; Dear ImGui has no
        // API to query a child's content size.
        ImVec2 backupPos = ImGui.GetCursorScreenPos();
        ImGui.SetCursorPosX(s.WindowWidth);
        ImVec2 zero = new() { x = 0f, y = 0f };
        ImGui.Dummy(&zero);
        ImGui.SetCursorScreenPos(&backupPos);

        ApplyPendingNavigation(memSize, ref dataNext, dataEditingAddrNext);

        bool lockShowDataPreview = OptShowDataPreview;

        if (OptShowOptions)
        {
            ImGui.Separator();
            DrawOptionsLine(s, memData, memSize, baseDisplayAddr);
        }

        if (lockShowDataPreview)
        {
            ImGui.Separator();
            DrawPreviewLine(s, memData, memSize);
        }

        HandleGotoRequest(memSize, availSize);

        ImVec2 contentsPosEnd = new()
        {
            x = contentsPosStart.x + childWidth,
            y = ImGui.GetCursorScreenPos().y,
        };

        HandleOptionsPopup(s, contentsPosStart, contentsPosEnd);
    }

    private void HandleGotoRequest(nuint memSize, ImVec2 availSize)
    {
        if (_gotoAddr == NoAddress)
        {
            return;
        }

        if (_gotoAddr < memSize)
        {
            ImVec2 scrollingSize = new() { x = 0f, y = 0f };
            ImGui.BeginChild("##scrolling", &scrollingSize, 0, 0);
            ImGui.SetScrollY((_gotoAddr / (nuint)Cols) * ImGui.GetTextLineHeight() - availSize.y * 0.5f);
            ImGui.EndChild();

            _dataEditingAddr = _dataPreviewAddr = _gotoAddr;
            _dataEditingTakeFocus = true;
        }

        _gotoAddr = NoAddress;
    }

    private void ApplyPendingNavigation(nuint memSize, ref bool dataNext, nuint dataEditingAddrNext)
    {
        if (dataNext && _dataEditingAddr + 1 < memSize)
        {
            _dataEditingAddr = _dataPreviewAddr = _dataEditingAddr + 1;
            _dataEditingTakeFocus = true;
        }
        else if (dataEditingAddrNext != NoAddress)
        {
            _dataEditingAddr = _dataPreviewAddr = dataEditingAddrNext;
            _dataEditingTakeFocus = true;
        }
    }

    private void HandleOptionsPopup(Sizes s, ImVec2 contentsPosStart, ImVec2 contentsPosEnd)
    {
        if (OptShowOptions)
        {
            ImVec2 from = contentsPosStart;
            ImVec2 to = contentsPosEnd;

            if (ImGui.IsMouseHoveringRect(&from, &to, 1) != 0)
            {
                int childWindows = (int)ImGuiHoveredFlags.ImGuiHoveredFlags_ChildWindows;
                if (ImGui.IsWindowHovered(childWindows) != 0
                    && ImGui.IsMouseReleased((int)ImGuiMouseButton.ImGuiMouseButton_Right) != 0)
                {
                    ImGui.OpenPopup("OptionsPopup");
                }
            }
        }

        if (ImGui.BeginPopup("OptionsPopup") != 0)
        {
            ImGui.SetNextItemWidth(s.GlyphWidth * 7f + ImGui.GetStyle()->FramePadding.x * 2f);

            int cols = Cols;
            if (ImGui.DragInt("##cols", &cols, 0.2f, 4, 32, "%d cols") != 0)
            {
                Cols = Math.Max(cols, 1);
                ContentsWidthChanged = true;
            }

            CheckboxBool("Show Data Preview", ref OptShowDataPreview);
            CheckboxBool("Show HexII", ref OptShowHexII);

            if (CheckboxBool("Show Ascii", ref OptShowAscii))
            {
                ContentsWidthChanged = true;
            }

            CheckboxBool("Grey out zeroes", ref OptGreyOutZeroes);
            CheckboxBool("Uppercase Hex", ref OptUpperCaseHex);

            ImGui.EndPopup();
        }
    }

    // -----------------------------------------------------------------------
    // Row rendering
    // -----------------------------------------------------------------------

    private void DrawAddressLine(ImDrawList* drawList, Sizes s, int lineI, nuint baseDisplayAddr,
            nuint addr, uint colorText, bool upperCase)
    {
        _ = drawList;
        _ = colorText;

        nuint displayAddr = baseDisplayAddr + addr;
        string text = FormatHex(displayAddr, s.AddrDigitsCount, OptUpperCaseHex) + ": ";
        ImGui.TextUnformatted(text);
    }

    private void DrawHexCells(ImDrawList* drawList, byte* memData, nuint memSize, Sizes s,
            ref nuint addr, uint colorText, uint colorDisabled, string byteFormat,
        nuint previewDataTypeSize, ref bool dataNext, ref nuint dataEditingAddrNext)
    {
        for (int n = 0; n < Cols && addr < memSize; n++, addr++)
        {
            float bytePosX = s.OffsetHexMinX + s.HexCellWidth * n;
            if (OptMidColsCount > 0)
            {
                bytePosX += n / OptMidColsCount * s.SpacingBetweenMidCols;
            }

            ImGui.SameLine(bytePosX, -1f);

            bool highlightRange = addr >= _highlightMin && addr < _highlightMax;
            bool highlightFunc = HighlightFn != null && HighlightFn(memData, addr, UserData) != 0;
            bool highlightPreview = previewDataTypeSize > 0
                && addr >= _dataPreviewAddr
                && addr < _dataPreviewAddr + previewDataTypeSize;

            uint bgColor = 0;
            bool nextHighlighted = false;

            if (highlightRange || highlightFunc || highlightPreview)
            {
                nextHighlighted = addr + 1 < memSize
                    && ((_highlightMax != NoAddress && addr + 1 < _highlightMax)
                        || (HighlightFn != null && HighlightFn(memData, addr + 1, UserData) != 0)
                        || (previewDataTypeSize > 0
                            && addr + 1 < _dataPreviewAddr + previewDataTypeSize));
                bgColor = HighlightColor;
            }
            else if (BgColorFn != null)
            {
                nextHighlighted = addr + 1 < memSize
                    && (BgColorFn(memData, addr + 1, UserData) & 0xFF000000) != 0;
                bgColor = BgColorFn(memData, addr, UserData);
            }

            if (bgColor != 0)
            {
                float bgWidth = s.GlyphWidth * 2f;

                if (nextHighlighted || n + 1 == Cols)
                {
                    bgWidth = s.HexCellWidth;

                    if (OptMidColsCount > 0 && n > 0 && n + 1 < Cols && (n + 1) % OptMidColsCount == 0)
                    {
                        bgWidth += s.SpacingBetweenMidCols;
                    }
                }

                ImVec2 pos = ImGui.GetCursorScreenPos();
                ImVec2 posMax = new() { x = pos.x + bgWidth, y = pos.y + s.LineHeight };
                ImDrawList.AddRectFilled(drawList, &pos, &posMax, bgColor, 0f);
            }

            if (_dataEditingAddr == addr)
            {
                DrawEditingByte(drawList, memData, s, addr, byteFormat, ref dataNext, ref dataEditingAddrNext);
            }
            else
            {
                DrawStaticByte(memData, addr, colorDisabled, byteFormat, ref dataEditingAddrNext);
            }
        }
    }

    private void DrawStaticByte(byte* memData, nuint addr, uint colorDisabled,
            string byteFormat, ref nuint dataEditingAddrNext)
    {
        byte b = ReadFn != null ? ReadFn(memData, addr, UserData) : memData[addr];

        if (OptShowHexII)
        {
            if (b >= 32 && b < 128)
            {
                ImGui.TextUnformatted($".{(char)b} ");
            }
            else if (b == 0xFF && OptGreyOutZeroes)
            {
                PushDisabledText();
                ImGui.TextUnformatted("## ");
                ImGui.PopStyleColor();
            }
            else if (b == 0x00)
            {
                ImGui.TextUnformatted("   ");
            }
            else
            {
                ImGui.TextUnformatted(b.ToString(byteFormat) + " ");
            }
        }
        else if (b == 0 && OptGreyOutZeroes)
        {
            PushDisabledText();
            ImGui.TextUnformatted("00 ");
            ImGui.PopStyleColor();
        }
        else
        {
            ImGui.TextUnformatted(b.ToString(byteFormat) + " ");
        }

        if (ImGui.IsItemHovered() != 0)
        {
            MouseHovered = true;
            MouseHoveredAddr = addr;

            if (ImGui.IsMouseClicked((int)ImGuiMouseButton.ImGuiMouseButton_Left) != 0)
            {
                _dataEditingTakeFocus = true;
                dataEditingAddrNext = addr;
            }
        }
    }

    // Mirrors ImGuiInputTextCallbackData's HasSelection(). Kept as a helper
    // because the generated struct exposes fields only.
    private static bool HasSelection(ImGuiInputTextCallbackData* data)
        => data->SelectionStart != data->SelectionEnd;

    /// <summary>
    /// Port of the <c>InputTextUserData::Callback</c> in the C++ original.
    /// </summary>
    /// <remarks>
    /// <c>InputText</c> owns the master copy of the text it edits, so when the
    /// user is not selecting anything the content is forced back to the byte
    /// currently in memory. Without this the widget would keep whatever was
    /// typed before the editor scrolled that address out of view.
    /// <para>
    /// The callback only runs because <c>CallbackAlways</c> is requested; Dear
    /// ImGui asserts the pointer is non-null in that case, so passing a flag
    /// without a callback aborts the process.
    /// </para>
    /// </remarks>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int DataInputCallback(ImGuiInputTextCallbackData* data)
    {
        DataInputUserData* userData = (DataInputUserData*)data->UserData;
        if (!HasSelection(data))
        {
            userData->CursorPos = data->CursorPos;
        }

        if (data->SelectionStart == 0 && data->SelectionEnd == data->BufTextLen)
        {
            ImGuiInputTextCallbackData.DeleteChars(data, 0, data->BufTextLen);
            ImGuiInputTextCallbackData.InsertChars(data, 0, new string(userData->CurrentBufOverwrite));
            data->SelectionStart = 0;
            data->SelectionEnd = 2;
            data->CursorPos = 0;
        }

        return 0;
    }

    private void DrawEditingByte(ImDrawList* drawList, byte* memData, Sizes s, nuint addr,
        string byteFormat, ref bool dataNext, ref nuint dataEditingAddrNext)
    {
        _ = drawList;

        bool dataWrite = false;
        ImGui.PushID((void*)addr);

        if (_dataEditingTakeFocus)
        {
            ImGui.SetKeyboardFocusHere(0);
            WriteCString(_addrInputBuf, InputBufSize, FormatHex(addr, s.AddrDigitsCount, OptUpperCaseHex));

            byte current = ReadFn != null ? ReadFn(memData, addr, UserData) : memData[addr];
            WriteCString(_dataInputBuf, InputBufSize, current.ToString(byteFormat));
        }

        int flags = (int)ImGuiInputTextFlags.ImGuiInputTextFlags_CharsHexadecimal
            | (int)ImGuiInputTextFlags.ImGuiInputTextFlags_EnterReturnsTrue
            | (int)ImGuiInputTextFlags.ImGuiInputTextFlags_AutoSelectAll
            | (int)ImGuiInputTextFlags.ImGuiInputTextFlags_NoHorizontalScroll
            | (int)ImGuiInputTextFlags.ImGuiInputTextFlags_CallbackAlways
            | (int)ImGuiInputTextFlags.ImGuiInputTextFlags_AlwaysOverwrite;

        if (ReadOnly)
        {
            flags |= (int)ImGuiInputTextFlags.ImGuiInputTextFlags_ReadOnly;
        }

        ImGui.SetNextItemWidth(s.GlyphWidth * 2f);

        DataInputUserData userData = default;
        userData.CursorPos = -1;
        byte live = ReadFn != null ? ReadFn(memData, addr, UserData) : memData[addr];
        WriteCString(userData.CurrentBufOverwrite, 3, live.ToString(byteFormat));

        if (ImGui.InputText("##data", _dataInputBuf, InputBufSize, flags, &DataInputCallback, &userData) != 0)
        {
            dataWrite = dataNext = true;
        }
        else if (!_dataEditingTakeFocus && ImGui.IsItemActive() == 0)
        {
            _dataEditingAddr = dataEditingAddrNext = NoAddress;
        }

        _dataEditingTakeFocus = false;

        if (dataEditingAddrNext != NoAddress)
        {
            dataWrite = dataNext = false;
        }

        // A cursor past the first digit means the user finished the byte.
        if (userData.CursorPos >= 2)
        {
            dataWrite = dataNext = true;
        }

        if (!ReadOnly && dataWrite && TryParseHexByte(_dataInputBuf, out byte value))
        {
            if (WriteFn != null)
            {
                WriteFn(memData, addr, value, UserData);
            }
            else
            {
                memData[addr] = value;
            }
        }

        if (ImGui.IsItemHovered() != 0)
        {
            MouseHovered = true;
            MouseHoveredAddr = addr;
        }

        ImGui.PopID();
    }

    private void DrawAsciiCells(ImDrawList* drawList, byte* memData, nuint memSize, Sizes s,
        int lineI, uint colorText, uint colorDisabled)
    {
        ImGui.SameLine(s.OffsetAsciiMinX, -1f);
        ImVec2 pos = ImGui.GetCursorScreenPos();
        nuint addr = (nuint)lineI * (nuint)Cols;

        float mouseOffsetX = ImGui.GetIO()->MousePos.x - pos.x;
        nuint mouseAddr = mouseOffsetX >= 0f && mouseOffsetX < s.OffsetAsciiMaxX - s.OffsetAsciiMinX
            ? addr + (nuint)(mouseOffsetX / s.GlyphWidth)
            : NoAddress;

        ImGui.PushID(lineI);

        ImVec2 buttonSize = new()
        {
            x = s.OffsetAsciiMaxX - s.OffsetAsciiMinX,
            y = s.LineHeight,
        };

        if (ImGui.InvisibleButton("ascii", &buttonSize) != 0)
        {
            _dataEditingAddr = _dataPreviewAddr = mouseAddr;
            _dataEditingTakeFocus = true;
        }

        if (ImGui.IsItemHovered() != 0)
        {
            MouseHovered = true;
            MouseHoveredAddr = mouseAddr;
        }

        ImGui.PopID();

        for (int n = 0; n < Cols && addr < memSize; n++, addr++)
        {
            if (addr == _dataEditingAddr)
            {
                ImVec2 cellMax = new() { x = pos.x + s.GlyphWidth, y = pos.y + s.LineHeight };
                ImDrawList.AddRectFilled(drawList, &pos, &cellMax,
                    ImGui.GetColorU32((int)ImGuiCol.ImGuiCol_FrameBg, 1f), 0f);
                ImDrawList.AddRectFilled(drawList, &pos, &cellMax,
                    ImGui.GetColorU32((int)ImGuiCol.ImGuiCol_TextSelectedBg, 1f), 0f);
            }
            else if (BgColorFn != null)
            {
                ImVec2 cellMax = new() { x = pos.x + s.GlyphWidth, y = pos.y + s.LineHeight };
                ImDrawList.AddRectFilled(drawList, &pos, &cellMax, BgColorFn(memData, addr, UserData), 0f);
            }

            byte c = ReadFn != null ? ReadFn(memData, addr, UserData) : memData[addr];
            char display = c < 32 || c >= 128 ? '.' : (char)c;
            uint color = display == (char)c ? colorText : colorDisabled;

            ImDrawList.AddText(drawList, &pos, color, display.ToString(), null);

            pos.x += s.GlyphWidth;
        }
    }

    // -----------------------------------------------------------------------
    // Options and preview
    // -----------------------------------------------------------------------

    private void DrawOptionsLine(Sizes s, void* memData, nuint memSize, nuint baseDisplayAddr)
    {
        _ = memData;

        ImGuiStyle* style = ImGui.GetStyle();
        string rangeFormat = OptUpperCaseHex
            ? $"Range {FormatHex(baseDisplayAddr, s.AddrDigitsCount, OptUpperCaseHex)}.."
                        : $"Range {FormatHex(baseDisplayAddr, s.AddrDigitsCount, OptUpperCaseHex)}..";

        ImVec2 optionsButtonSize = new() { x = 0f, y = 0f };
        if (ImGui.Button("Options", &optionsButtonSize) != 0)
        {
            ImGui.OpenPopup("OptionsPopup");
        }

        ImGui.SameLine(0f, -1f);
        _ = rangeFormat;
        ImGui.TextUnformatted(
            $"Range {FormatHex(baseDisplayAddr, s.AddrDigitsCount, OptUpperCaseHex)}..{FormatHex(baseDisplayAddr + memSize - 1, s.AddrDigitsCount, OptUpperCaseHex)}");

        ImGui.SameLine(0f, -1f);
        ImGui.SetNextItemWidth((s.AddrDigitsCount + 1) * s.GlyphWidth + style->FramePadding.x * 2f);

        int addrFlags = (int)ImGuiInputTextFlags.ImGuiInputTextFlags_CharsHexadecimal
            | (int)ImGuiInputTextFlags.ImGuiInputTextFlags_EnterReturnsTrue;

        if (ImGui.InputText("##addr", _addrInputBuf, InputBufSize, addrFlags) != 0)
        {
            if (TryParseHexAddress(_addrInputBuf, out nuint gotoAddr))
            {
                _gotoAddr = gotoAddr - baseDisplayAddr;
                _highlightMin = _highlightMax = NoAddress;
            }
        }
    }

    private void DrawPreviewLine(Sizes s, byte* memData, nuint memSize)
    {
        ImGuiStyle* style = ImGui.GetStyle();

        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Preview as:");
        ImGui.SameLine(0f, -1f);
        ImGui.SetNextItemWidth(s.GlyphWidth * 10f + style->FramePadding.x * 2f + style->ItemInnerSpacing.x);

        int largest = (int)ImGuiComboFlags.ImGuiComboFlags_HeightLargest;
        if (ImGui.BeginCombo("##combo_type", DataTypeGetDesc(_previewDataType), largest) != 0)
        {
            for (int n = 0; n < SupportedDataTypes.Length; n++)
            {
                int dataType = SupportedDataTypes[n];
                ImVec2 selSize = new() { x = 0f, y = 0f };
                if (ImGui.Selectable(DataTypeGetDesc(dataType), dataType == _previewDataType ? (byte)1 : (byte)0, 0, &selSize) != 0)
                {
                    _previewDataType = dataType;
                }
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine(0f, -1f);
        ImGui.SetNextItemWidth(s.GlyphWidth * 6f + style->FramePadding.x * 2f + style->ItemInnerSpacing.x);

        int endianness = _previewEndianness;
        ImGui.Combo("##combo_endianness", &endianness, "LE\0BE\0\0", 2);
        _previewEndianness = endianness;

        float x = s.GlyphWidth * 6f;
        bool hasValue = _dataPreviewAddr != NoAddress;

        string dec = hasValue ? DrawPreviewData(memData, memSize, DataFormat.Dec) : "N/A";
        ImGui.TextUnformatted("Dec");
        ImGui.SameLine(x, -1f);
        ImGui.TextUnformatted(dec);

        string hex = hasValue ? DrawPreviewData(memData, memSize, DataFormat.Hex) : "N/A";
        ImGui.TextUnformatted("Hex");
        ImGui.SameLine(x, -1f);
        ImGui.TextUnformatted(hex);

        string bin = hasValue ? DrawPreviewData(memData, memSize, DataFormat.Bin) : "N/A";
        ImGui.TextUnformatted("Bin");
        ImGui.SameLine(x, -1f);
        ImGui.TextUnformatted(bin);
    }

    /// <summary>Data formats the preview panel can render.</summary>
    private enum DataFormat
    {
        Bin = 0,
        Dec = 1,
        Hex = 2,
    }

    private static readonly int[] SupportedDataTypes =
    [
        (int)ImGuiDataType.ImGuiDataType_S8,
        (int)ImGuiDataType.ImGuiDataType_U8,
        (int)ImGuiDataType.ImGuiDataType_S16,
        (int)ImGuiDataType.ImGuiDataType_U16,
        (int)ImGuiDataType.ImGuiDataType_S32,
        (int)ImGuiDataType.ImGuiDataType_U32,
        (int)ImGuiDataType.ImGuiDataType_S64,
        (int)ImGuiDataType.ImGuiDataType_U64,
        (int)ImGuiDataType.ImGuiDataType_Float,
        (int)ImGuiDataType.ImGuiDataType_Double,
    ];

    private static string DataTypeGetDesc(int dataType)
    {
        string[] descs = ["Int8", "Uint8", "Int16", "Uint16", "Int32", "Uint32", "Int64", "Uint64", "Float", "Double"];
        return (uint)dataType < (uint)descs.Length ? descs[dataType] : "?";
    }

    private static nuint DataTypeGetSize(int dataType)
    {
        nuint[] sizes = [1, 1, 2, 2, 4, 4, 8, 8, 4, 8];
        return (uint)dataType < (uint)sizes.Length ? sizes[dataType] : 0;
    }

    private string DrawPreviewData(byte* memData, nuint memSize, DataFormat format)
    {
        int dataType = _previewDataType;
        nuint elementSize = DataTypeGetSize(dataType);
        nuint size = _dataPreviewAddr + elementSize > memSize
            ? memSize - _dataPreviewAddr
            : elementSize;

        byte* buf = stackalloc byte[8];
        for (nuint i = 0; i < 8; i++)
        {
            buf[i] = 0;
        }

        if (ReadFn != null)
        {
            for (nuint i = 0; i < size; i++)
            {
                buf[i] = ReadFn(memData, _dataPreviewAddr + i, UserData);
            }
        }
        else
        {
            Buffer.MemoryCopy(memData + _dataPreviewAddr, buf, 8, (long)size);
        }

        byte* ordered = stackalloc byte[8];
        for (nuint i = 0; i < 8; i++)
        {
            ordered[i] = 0;
        }

        EndiannessCopy(ordered, buf, size);

        if (format == DataFormat.Bin)
        {
            return FormatBinary(ordered, (int)size * 8);
        }

        return dataType switch
        {
            (int)ImGuiDataType.ImGuiDataType_S8 => FormatS8(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_U8 => FormatU8(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_S16 => FormatS16(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_U16 => FormatU16(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_S32 => FormatS32(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_U32 => FormatU32(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_S64 => FormatS64(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_U64 => FormatU64(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_Float => FormatFloat(ordered, format),
            (int)ImGuiDataType.ImGuiDataType_Double => FormatDouble(ordered, format),
            _ => string.Empty,
        };
    }

    private static string FormatS8(byte* p, DataFormat f)
        => f == DataFormat.Dec ? ((sbyte)p[0]).ToString() : $"0x{p[0]:x2}";

    private static string FormatU8(byte* p, DataFormat f)
        => f == DataFormat.Dec ? p[0].ToString() : $"0x{p[0]:x2}";

    private static string FormatS16(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(short*)p).ToString() : $"0x{*(ushort*)p:x4}";

    private static string FormatU16(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(ushort*)p).ToString() : $"0x{*(ushort*)p:x4}";

    private static string FormatS32(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(int*)p).ToString() : $"0x{*(uint*)p:x8}";

    private static string FormatU32(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(uint*)p).ToString() : $"0x{*(uint*)p:x8}";

    private static string FormatS64(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(long*)p).ToString() : $"0x{*(ulong*)p:x16}";

    private static string FormatU64(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(ulong*)p).ToString() : $"0x{*(ulong*)p:x16}";

    private static string FormatFloat(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(float*)p).ToString("F6") : (*(float*)p).ToString("R");

    private static string FormatDouble(byte* p, DataFormat f)
        => f == DataFormat.Dec ? (*(double*)p).ToString("F6") : (*(double*)p).ToString("R");

    private void EndiannessCopy(byte* dst, byte* src, nuint size)
    {
        bool isBigEndian = !BitConverter.IsLittleEndian;

        bool swap = _previewEndianness == 0 ? isBigEndian : !isBigEndian;

        if (!swap)
        {
            Buffer.MemoryCopy(src, dst, 8, (long)size);
            return;
        }

        for (nuint i = 0; i < size; i++)
        {
            dst[i] = src[size - 1 - i];
        }
    }

    private static string FormatBinary(byte* buf, int width)
    {
        if (width > 64)
        {
            width = 64;
        }

        var sb = new System.Text.StringBuilder(80);
        int n = width / 8;

        for (int j = n - 1; j >= 0; --j)
        {
            for (int i = 0; i < 8; ++i)
            {
                sb.Append((buf[j] & (1 << (7 - i))) != 0 ? '1' : '0');
            }

            sb.Append(' ');
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Small helpers
    // -----------------------------------------------------------------------

    private static string FormatHex(nuint value, int digits, bool upper)
            => value.ToString((upper ? "X" : "x") + digits.ToString());

    private static void WriteCString(sbyte* buffer, int size, string text)
    {
        int length = Math.Min(text.Length, size - 1);
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (sbyte)text[i];
        }

        buffer[length] = 0;
    }

    private static bool TryParseHexByte(sbyte* buffer, out byte value)
    {
        value = 0;
        string text = Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty;
        return byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static bool TryParseHexAddress(sbyte* buffer, out nuint value)
    {
        value = 0;
        string text = Marshal.PtrToStringUTF8((nint)buffer) ?? string.Empty;
        return nuint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static bool CheckboxBool(string label, ref bool value)
    {
        bool temp = value;
        bool changed = ImGui.Checkbox(label, &temp) != 0;
        if (changed)
        {
            value = temp;
        }

        return changed;
    }

    private static void PushDisabledText()
    {
        ImVec4 c = ImGui.GetStyle()->Colors[(int)ImGuiCol.ImGuiCol_TextDisabled];
        ImGui.PushStyleColor((int)ImGuiCol.ImGuiCol_Text, &c);
    }
}