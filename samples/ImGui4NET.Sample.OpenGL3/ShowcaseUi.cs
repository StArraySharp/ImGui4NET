using System.Runtime.InteropServices;
using ImGui4NET;
using ImGui4NET.Club;

namespace ImGui4NET.Sample.OpenGL3;

/// <summary>
/// A custom-drawn dashboard built directly on the ImGui4NET bindings: custom
/// chrome, animated navigation, stat cards with sparklines and progress rings,
/// a smooth two-series chart, tables, tabs, and the imgui_club Memory Editor.
/// </summary>
/// <remarks>
/// The layout is drawn through <see cref="ImDrawList"/> rather than the stock
/// widgets, so nothing depends on the default style metrics. Stock widgets are
/// only used where real interaction is needed (sliders, checkbox, combo, table).
/// </remarks>
internal static unsafe class ShowcaseUi
{
    // -----------------------------------------------------------------------
    // Palette
    // -----------------------------------------------------------------------

    static readonly ImVec4 ColBgPanel = V4(0.071f, 0.078f, 0.106f, 1.00f);
    static readonly ImVec4 ColText = V4(0.902f, 0.914f, 0.961f, 1.00f);
    static readonly ImVec4 ColTextDim = V4(0.541f, 0.565f, 0.643f, 1.00f);
    static readonly ImVec4 ColTextFaint = V4(0.361f, 0.384f, 0.455f, 1.00f);
    static readonly ImVec4 ColBorder = V4(1.000f, 1.000f, 1.000f, 0.055f);

    static readonly uint Accent = Rgba(0x7C, 0x6C, 0xFF);
    static readonly uint Cyan = Rgba(0x35, 0xD6, 0xD0);
    static readonly uint Amber = Rgba(0xFF, 0xB4, 0x4D);
    static readonly uint Rose = Rgba(0xFF, 0x6B, 0x8A);
    static readonly uint Mint = Rgba(0x4A, 0xDE, 0x9B);
    static readonly uint Border = Rgba(255, 255, 255, 0x0E);
    static readonly uint TextDim = Rgba(0x8A, 0x90, 0xA4);
    static readonly uint TextFaint = Rgba(0x5C, 0x62, 0x74);
    static readonly uint TextBright = Rgba(0xF0, 0xF2, 0xF8);

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------

    static bool _fontsLoaded;
    static bool _fontIsYahei;

    const int NavCount = 5;
    static readonly string[] NavLabels = ["总览 Overview", "组件 Components", "内存 Memory", "设置 Settings", "关于 About"];
    static readonly string[] TabLabels = ["Dashboard", "Components", "Memory", "Settings", "About"];

    static float _navPill;
    static float _tabPill;
    static int _navIndex;
    static int _activeTab;

    static bool _t1 = true, _t2 = true, _t3, _t4 = true;
    static float _a1 = 1f, _a2 = 1f, _a3, _a4 = 1f;

    static float _sliderQuality = 0.72f;
    static float _sliderScale = 1.0f;
    static int _sliderThreads = 8;
    static int _comboProfile;
    static float _colorR = 0.486f, _colorG = 0.424f, _colorB = 1.0f;
    static bool _showDemo;

    static float _frameMs = 16.6f;
    static float _fps = 60f;
    static float _fpsElapsed;
    static int _fpsFrames;

    static readonly float[] Spark1 = [12, 18, 15, 24, 21, 30, 27, 36, 33, 42, 39, 48];
    static readonly float[] Spark2 = [40, 33, 36, 29, 31, 25, 28, 22, 26, 20, 23, 18];
    static readonly float[] Spark3 = [8, 14, 11, 19, 26, 22, 31, 28, 35, 41, 37, 45];
    static readonly float[] Spark4 = [30, 28, 32, 27, 25, 29, 24, 26, 22, 24, 20, 21];

    static byte* _memData;
        static MemoryEditor? _memEditor;
    const int MemorySize = 0x800;

    const float TopBarHeight = 64f;
    const float RailWidth = 236f;
    const float RightWidth = 312f;

    // -----------------------------------------------------------------------
    // Setup
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads Microsoft YaHei from the Windows font directory, falling back to
    /// other CJK fonts and finally to the embedded default so this never fails.
    /// </summary>
    public static void LoadFonts()
    {
        if (_fontsLoaded)
        {
            return;
        }

        ImGuiIO* io = ImGui.GetIO();
        ImFontAtlas* atlas = io->Fonts;
        ushort* ranges = ImFontAtlas.GetGlyphRangesChineseSimplifiedCommon(atlas);

        string[] candidates =
        [
            @"C:\Windows\Fonts\msyh.ttc",   // Microsoft YaHei
            @"C:\Windows\Fonts\msyhbd.ttc", // Microsoft YaHei Bold
            @"C:\Windows\Fonts\Deng.ttf",   // DengXian
            @"C:\Windows\Fonts\simhei.ttf", // SimHei
        ];

        ImFont* font = null;
        foreach (string path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            font = ImFontAtlas.AddFontFromFileTTF(atlas, path, 16.0f, null, ranges);
            if (font != null)
            {
                _fontIsYahei = path.Contains("msyh", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }

        if (font == null)
        {
            font = ImFontAtlas.AddFontDefault(atlas);
        }

        io->FontDefault = font;
        _fontsLoaded = true;

        _memData = (byte*)NativeMemory.AllocZeroed(MemorySize);
                for (int i = 0; i < MemorySize; i++)
                {
                    _memData[i] = (byte)((i * 37) ^ (i >> 3));
                }

                _memEditor = new MemoryEditor
                {
                    Cols = 16,
                    OptShowOptions = true,
                    OptShowDataPreview = true,
                    OptShowAscii = true,
                    OptGreyOutZeroes = true,
                    OptUpperCaseHex = true,
                    OptMidColsCount = 8,
                };
            }

            public static void Shutdown()
            {
                _memEditor?.Dispose();
                _memEditor = null;

                if (_memData != null)
                {
                    NativeMemory.Free(_memData);
                    _memData = null;
                }
            }

    public static void SetActiveTab(int index)
    {
        _activeTab = Math.Clamp(index, 0, TabLabels.Length - 1);
        _tabPill = _activeTab;
    }

    /// <summary>Applies the dark dashboard theme.</summary>
    public static void ApplyTheme()
    {
        ImGui.StyleColorsDark();

        ImGuiStyle* style = ImGui.GetStyle();
        style->WindowRounding = 0f;
        style->WindowBorderSize = 0f;
        style->WindowPadding = V2(0f, 0f);
        style->ChildRounding = 12f;
        style->ChildBorderSize = 0f;
        style->FrameRounding = 8f;
        style->FrameBorderSize = 0f;
        style->FramePadding = V2(10f, 6f);
        style->ItemSpacing = V2(10f, 8f);
        style->ItemInnerSpacing = V2(6f, 6f);
        style->PopupRounding = 10f;
        style->PopupBorderSize = 1f;
        style->ScrollbarSize = 10f;
        style->ScrollbarRounding = 8f;
        style->GrabRounding = 8f;
        style->TabRounding = 8f;
        style->TabBarBorderSize = 0f;
        style->IndentSpacing = 18f;

        SetCol(style, ImGuiCol.ImGuiCol_Text, ColText);
        SetCol(style, ImGuiCol.ImGuiCol_TextDisabled, ColTextFaint);
        SetCol(style, ImGuiCol.ImGuiCol_WindowBg, V4(0.043f, 0.047f, 0.067f, 1f));
        SetCol(style, ImGuiCol.ImGuiCol_ChildBg, V4(0f, 0f, 0f, 0f));
        SetCol(style, ImGuiCol.ImGuiCol_PopupBg, V4(0.086f, 0.094f, 0.125f, 0.98f));
        SetCol(style, ImGuiCol.ImGuiCol_Border, ColBorder);
        SetCol(style, ImGuiCol.ImGuiCol_BorderShadow, V4(0f, 0f, 0f, 0f));
        SetCol(style, ImGuiCol.ImGuiCol_FrameBg, V4(1f, 1f, 1f, 0.055f));
        SetCol(style, ImGuiCol.ImGuiCol_FrameBgHovered, V4(1f, 1f, 1f, 0.09f));
        SetCol(style, ImGuiCol.ImGuiCol_FrameBgActive, V4(1f, 1f, 1f, 0.12f));
        SetCol(style, ImGuiCol.ImGuiCol_TitleBg, ColBgPanel);
        SetCol(style, ImGuiCol.ImGuiCol_TitleBgActive, ColBgPanel);
        SetCol(style, ImGuiCol.ImGuiCol_MenuBarBg, ColBgPanel);
        SetCol(style, ImGuiCol.ImGuiCol_ScrollbarBg, V4(0f, 0f, 0f, 0f));
        SetCol(style, ImGuiCol.ImGuiCol_ScrollbarGrab, V4(1f, 1f, 1f, 0.14f));
        SetCol(style, ImGuiCol.ImGuiCol_ScrollbarGrabHovered, V4(1f, 1f, 1f, 0.22f));
        SetCol(style, ImGuiCol.ImGuiCol_ScrollbarGrabActive, V4(1f, 1f, 1f, 0.30f));
        SetCol(style, ImGuiCol.ImGuiCol_CheckMark, V4(1f, 1f, 1f, 1f));
        SetCol(style, ImGuiCol.ImGuiCol_SliderGrab, V4(0.486f, 0.424f, 1f, 1f));
        SetCol(style, ImGuiCol.ImGuiCol_SliderGrabActive, V4(0.604f, 0.549f, 1f, 1f));
        SetCol(style, ImGuiCol.ImGuiCol_Button, V4(1f, 1f, 1f, 0.07f));
        SetCol(style, ImGuiCol.ImGuiCol_ButtonHovered, V4(1f, 1f, 1f, 0.12f));
        SetCol(style, ImGuiCol.ImGuiCol_ButtonActive, V4(1f, 1f, 1f, 0.17f));
        SetCol(style, ImGuiCol.ImGuiCol_Header, V4(0.486f, 0.424f, 1f, 0.18f));
        SetCol(style, ImGuiCol.ImGuiCol_HeaderHovered, V4(0.486f, 0.424f, 1f, 0.24f));
        SetCol(style, ImGuiCol.ImGuiCol_HeaderActive, V4(0.486f, 0.424f, 1f, 0.34f));
        SetCol(style, ImGuiCol.ImGuiCol_Separator, ColBorder);
        SetCol(style, ImGuiCol.ImGuiCol_Tab, V4(0f, 0f, 0f, 0f));
        SetCol(style, ImGuiCol.ImGuiCol_TabHovered, V4(1f, 1f, 1f, 0.06f));
        SetCol(style, ImGuiCol.ImGuiCol_TabSelected, V4(1f, 1f, 1f, 0.09f));
        SetCol(style, ImGuiCol.ImGuiCol_TableHeaderBg, V4(1f, 1f, 1f, 0.03f));
        SetCol(style, ImGuiCol.ImGuiCol_TableBorderStrong, V4(1f, 1f, 1f, 0.10f));
        SetCol(style, ImGuiCol.ImGuiCol_TableBorderLight, V4(1f, 1f, 1f, 0.05f));
        SetCol(style, ImGuiCol.ImGuiCol_TableRowBg, V4(0f, 0f, 0f, 0f));
        SetCol(style, ImGuiCol.ImGuiCol_TableRowBgAlt, V4(1f, 1f, 1f, 0.018f));
        SetCol(style, ImGuiCol.ImGuiCol_TextSelectedBg, V4(0.486f, 0.424f, 1f, 0.35f));
    }

    // -----------------------------------------------------------------------
    // Frame
    // -----------------------------------------------------------------------

    public static void Draw(float deltaTime)
    {
        ImGuiIO* io = ImGui.GetIO();
        float width = io->DisplaySize.x;
        float height = io->DisplaySize.y;

        // Sample the frame rate once per second. Using the instantaneous
        // deltaTime made the chip flicker to 0 on any single long frame.
        _fpsElapsed += deltaTime;
        _fpsFrames++;
        if (_fpsElapsed >= 1f)
        {
            _fps = _fpsFrames / _fpsElapsed;
            _frameMs = _fpsElapsed * 1000f / _fpsFrames;
            _fpsElapsed = 0f;
            _fpsFrames = 0;
        }

        float dt = io->DeltaTime;
        _navPill = Approach(_navPill, _navIndex, dt * 34f);
        _tabPill = Approach(_tabPill, _activeTab, dt * 34f);
        _a1 = Approach(_a1, _t1 ? 1f : 0f, dt * 40f);
        _a2 = Approach(_a2, _t2 ? 1f : 0f, dt * 40f);
        _a3 = Approach(_a3, _t3 ? 1f : 0f, dt * 40f);
        _a4 = Approach(_a4, _t4 ? 1f : 0f, dt * 40f);

        ImVec2 zero = V2(0f, 0f);
        ImVec2 pivot = V2(0f, 0f);
        ImVec2 displaySize = io->DisplaySize;
        // pivot must not be null: the native signature takes an ImVec2& whose
        // default constructs a temporary, but the generated binding exposes it
        // as a nullable pointer that the native side dereferences directly.
        ImGui.SetNextWindowPos(&zero, CondAlways, &pivot);
        ImGui.SetNextWindowSize(&displaySize, CondAlways);

        int rootFlags = (int)ImGuiWindowFlags.ImGuiWindowFlags_NoDecoration
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoMove
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoResize
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoSavedSettings
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoBringToFrontOnFocus
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoNavFocus
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoScrollbar
            | (int)ImGuiWindowFlags.ImGuiWindowFlags_NoScrollWithMouse;

        ImVec2 rootPadding = V2(0f, 0f);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowPadding, &rootPadding);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowRounding, 0f);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowBorderSize, 0f);

        bool open = ImGui.Begin("##showcase-root", null, rootFlags) != 0;
        if (open)
        {
            DrawBackdrop(width, height);
            DrawTopBar(width);

            SetCursor(0f, TopBarHeight);

            float bodyHeight = height - TopBarHeight;
            float centerWidth = width - RailWidth - RightWidth;

            DrawRail(bodyHeight);
            ImGui.SameLine(0f, 0f);
            DrawCenter(centerWidth, bodyHeight);
            ImGui.SameLine(0f, 0f);
            DrawRightPanel(bodyHeight);
        }

        ImGui.End();
        ImGui.PopStyleVar(3);

        if (_showDemo)
        {
            ImGui.ShowDemoWindow();
        }
    }

    static readonly int CondAlways = (int)ImGuiCond.ImGuiCond_Always;

    // -----------------------------------------------------------------------
    // Backdrop and top bar
    // -----------------------------------------------------------------------

    static void DrawBackdrop(float width, float height)
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 origin = ImGui.GetWindowPos();

        RectGrad(dl, origin, V2(origin.x + width, origin.y + height),
            Rgba(0x0C, 0x0D, 0x16), Rgba(0x0C, 0x0D, 0x16), Rgba(0x07, 0x08, 0x0E), Rgba(0x07, 0x08, 0x0E));

        ImVec2 glow1 = V2(origin.x + width * 0.24f, origin.y - height * 0.16f);
        for (int i = 6; i >= 1; i--)
        {
            CircleFill(dl, glow1, 140f + i * 76f, Rgba(0x7C, 0x6C, 0xFF, 3 + i), 64);
        }

        ImVec2 glow2 = V2(origin.x + width * 0.88f, origin.y + height * 0.94f);
        for (int i = 5; i >= 1; i--)
        {
            CircleFill(dl, glow2, 120f + i * 68f, Rgba(0x35, 0xD6, 0xD0, 2 + i), 64);
        }
    }

    static void DrawTopBar(float width)
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 origin = ImGui.GetWindowPos();

        Rect(dl, origin, V2(origin.x + width, origin.y + TopBarHeight), Rgba(0x0D, 0x0F, 0x17, 0xD8));
        Line(dl, V2(origin.x, origin.y + TopBarHeight), V2(origin.x + width, origin.y + TopBarHeight), Border, 1f);

        ImVec2 logo = V2(origin.x + 34f, origin.y + TopBarHeight * 0.5f);
        CircleFill(dl, logo, 15f, Accent, 48);
        CircleFill(dl, V2(logo.x + 4f, logo.y - 4f), 9f, Rgba(0x35, 0xD6, 0xD0, 0x88), 32);

        const float q = 5.5f;
        Quad(dl,
            V2(logo.x, logo.y - q), V2(logo.x + q, logo.y),
            V2(logo.x, logo.y + q), V2(logo.x - q, logo.y),
            Rgba(255, 255, 255, 0xF0));

        Text(dl, V2(origin.x + 60f, origin.y + 14f), TextBright, "ImGui4NET Studio", 19f);
        Text(dl, V2(origin.x + 60f, origin.y + 38f), TextDim, "Dear ImGui 1.93.0   OpenGL 4.1   net10.0", 12f);

        float navX = origin.x + width * 0.5f - 215f;
        float navY = origin.y + TopBarHeight * 0.5f - 13f;

        for (int i = 0; i < NavCount; i++)
        {
            float itemWidth = Measure(NavLabels[i], 13f).x + 26f;
            ImVec2 pos = V2(navX, navY);

            ImGui.SetCursorScreenPos(&pos);
            bool clicked = InvisibleButton($"##nav{i}", itemWidth, 26f);
            bool hovered = ImGui.IsItemHovered() != 0;

            if (clicked)
            {
                _navIndex = i;
            }

            float t = Math.Clamp(1f - MathF.Abs(_navPill - i), 0f, 1f);
            if (t > 0.02f)
            {
                Rect(dl, pos, V2(pos.x + itemWidth, pos.y + 26f), Rgba(0x7C, 0x6C, 0xFF, (int)(0x26 * t)), 8f);
            }
            else if (hovered)
            {
                Rect(dl, pos, V2(pos.x + itemWidth, pos.y + 26f), Rgba(255, 255, 255, 0x0C), 8f);
            }

            uint color = t > 0.5f ? TextBright : (hovered ? Rgba(0xC8, 0xCC, 0xDC) : TextDim);
            Text(dl, V2(pos.x + 13f, pos.y + 5f), color, NavLabels[i], 13f);

            if (t > 0.02f)
            {
                Rect(dl, V2(pos.x + itemWidth * 0.5f - 14f, pos.y + 24f),
                    V2(pos.x + itemWidth * 0.5f + 14f, pos.y + 26f),
                    Rgba(0x7C, 0x6C, 0xFF, (int)(0xFF * t)), 1f);
            }

            navX += itemWidth + 4f;
        }

        float rightX = origin.x + width - 24f;
        float cy = origin.y + TopBarHeight * 0.5f;

        ImVec2 closeDot = V2(rightX - 11f, cy);
        CircleFill(dl, closeDot, 5f, Rgba(0xFF, 0x6B, 0x8A, 0xD0), 20);
        CircleFill(dl, V2(closeDot.x - 22f, cy), 5f, Rgba(0xFF, 0xB4, 0x4D, 0xB0), 20);
        CircleFill(dl, V2(closeDot.x - 44f, cy), 5f, Rgba(0x4A, 0xDE, 0x9B, 0xB0), 20);

        string fps = $"{_fps,5:F0} FPS";
        ImVec2 fpsSize = Measure(fps, 12f);
        ImVec2 chipMin = V2(closeDot.x - 68f - fpsSize.x - 20f, cy - 12f);
        ImVec2 chipMax = V2(chipMin.x + fpsSize.x + 20f, chipMin.y + 24f);
        Rect(dl, chipMin, chipMax, Rgba(255, 255, 255, 0x0A), 12f);
        RectOutline(dl, chipMin, chipMax, Border, 12f, 1f);

        uint fpsColor = _fps >= 55f ? Mint : (_fps >= 30f ? Amber : Rose);
        CircleFill(dl, V2(chipMin.x + 11f, chipMin.y + 12f), 3f, fpsColor, 12);
        Text(dl, V2(chipMin.x + 19f, chipMin.y + 5f), TextDim, fps, 12f);
    }

    // -----------------------------------------------------------------------
    // Left rail
    // -----------------------------------------------------------------------

    static void DrawRail(float height)
    {
        PushChildBg(ColBgPanel);
        PushWindowPadding(14f, 16f);
        BeginChild("##rail", RailWidth, height);

        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 origin = ImGui.GetWindowPos();

        Text(dl, V2(origin.x + 16f, origin.y + 10f), TextDim, "工作区  WORKSPACE", 11f);
        ImGui.SetCursorPosY(38f);

        for (int i = 0; i < NavCount; i++)
        {
            ImVec2 pos = ImGui.GetCursorScreenPos();
            float w = RailWidth - 28f;

            ImGui.SetCursorScreenPos(&pos);
            bool clicked = InvisibleButton($"##rail{i}", w, 38f);
            bool hovered = ImGui.IsItemHovered() != 0;

            if (clicked)
            {
                _navIndex = i;
            }

            float t = Math.Clamp(1f - MathF.Abs(_navPill - i), 0f, 1f);
            if (t > 0.02f)
            {
                Rect(dl, pos, V2(pos.x + w, pos.y + 38f), Rgba(0x7C, 0x6C, 0xFF, (int)(0x30 * t)), 10f);
                Rect(dl, V2(pos.x, pos.y + 8f), V2(pos.x + 3f, pos.y + 30f), Rgba(0x7C, 0x6C, 0xFF, (int)(0xFF * t)), 2f);
            }
            else if (hovered)
            {
                Rect(dl, pos, V2(pos.x + w, pos.y + 38f), Rgba(255, 255, 255, 0x08), 10f);
            }

            uint labelColor = t > 0.5f ? TextBright : (hovered ? Rgba(0xC8, 0xCC, 0xDC) : TextDim);
            Text(dl, V2(pos.x + 20f, pos.y + 11f), labelColor, NavLabels[i], 13f);

            SetCursorScreen(pos.x, pos.y + 42f);
            Dummy(1f, 0f);
        }

        ImVec2 cardMin = V2(origin.x + 14f, origin.y + height - 74f);
        ImVec2 cardMax = V2(origin.x + RailWidth - 14f, origin.y + height - 16f);
        Rect(dl, cardMin, cardMax, Rgba(255, 255, 255, 0x07), 12f);
        RectOutline(dl, cardMin, cardMax, Border, 12f, 1f);

        ImVec2 avatar = V2(cardMin.x + 26f, cardMin.y + 29f);
        CircleFill(dl, avatar, 15f, Accent, 32);
        CircleFill(dl, V2(avatar.x, avatar.y - 4f), 5f, Rgba(255, 255, 255, 0xD0), 20);
        CircleFill(dl, V2(avatar.x, avatar.y + 9f), 8f, Rgba(255, 255, 255, 0xD0), 20);

        Text(dl, V2(cardMin.x + 50f, cardMin.y + 15f), Rgba(0xE8, 0xEA, 0xF2), "StArray", 13f);
        Text(dl, V2(cardMin.x + 50f, cardMin.y + 34f), TextDim, "Maintainer", 11f);

        ImGui.EndChild();
        PopWindowPadding();
        PopChildBg();
    }

    // -----------------------------------------------------------------------
    // Center column
    // -----------------------------------------------------------------------

    static void DrawCenter(float width, float height)
    {
        PushWindowPadding(20f, 16f);
        BeginChild("##center", width, height);

        DrawTabBar(width);

        float tabContentTop = ImGui.GetCursorScreenPos().y;
        float pageHeight = ImGui.GetContentRegionAvail().y;

        switch (_activeTab)
        {
            case 1: DrawComponentsTab(); break;
            case 2: DrawMemoryTab(); break;
            case 3: DrawSettingsTab(); break;
            case 4: DrawAboutTab(); break;
            default: DrawDashboardTab(); break;
        }

        // Every tab draws with absolute screen coordinates, so the child window's
        // tracked content size stays near zero and any SetCursorScreenPos below
        // the last submitted item is reported as a boundary error. Submitting one
        // dummy covering the whole page makes the content size cover the drawing.
        SetCursorScreen(ImGui.GetCursorScreenPos().x, tabContentTop);
        Dummy(1f, pageHeight);

        ImGui.EndChild();
        PopWindowPadding();
    }

    static void DrawTabBar(float width)
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 origin = ImGui.GetWindowPos();
        ImVec2 start = ImGui.GetCursorScreenPos();

        float x = start.x;
        for (int i = 0; i < TabLabels.Length; i++)
        {
            float w = Measure(TabLabels[i], 13f).x + 30f;
            ImVec2 pos = V2(x, start.y);

            ImGui.SetCursorScreenPos(&pos);
            bool clicked = InvisibleButton($"##tab{i}", w, 30f);
            bool hovered = ImGui.IsItemHovered() != 0;

            if (clicked)
            {
                _activeTab = i;
            }

            float t = Math.Clamp(1f - MathF.Abs(_tabPill - i), 0f, 1f);
            if (t > 0.02f)
            {
                Rect(dl, pos, V2(pos.x + w, pos.y + 30f), Rgba(0x7C, 0x6C, 0xFF, (int)(0x28 * t)), 9f);
                Rect(dl, V2(pos.x + 12f, pos.y + 28f), V2(pos.x + w - 12f, pos.y + 30f),
                    Rgba(0x7C, 0x6C, 0xFF, (int)(0xFF * t)), 1f);
            }
            else if (hovered)
            {
                Rect(dl, pos, V2(pos.x + w, pos.y + 30f), Rgba(255, 255, 255, 0x07), 9f);
            }

            uint color = t > 0.5f ? TextBright : TextDim;
            Text(dl, V2(pos.x + 15f, pos.y + 7f), color, TabLabels[i], 13f);

            x += w + 6f;
        }

        const string hint = "点击切换标签页";
        ImVec2 hintSize = Measure(hint, 11f);
        Text(dl, V2(origin.x + width - hintSize.x - 8f, start.y + 9f), TextDim, hint, 11f);

        ImGui.SetCursorPosY(46f);
    }

    static void DrawDashboardTab()
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        float avail = ImGui.GetContentRegionAvail().x;
        const float gap = 12f;
        float cardWidth = MathF.Max((avail - gap * 3f) / 4f, 90f);

        ImVec2 rowStart = ImGui.GetCursorScreenPos();

        StatCard(dl, rowStart, cardWidth, "总请求 Requests", "1.28M", "+12.4%", Spark1, Accent, 0.0f);
        StatCard(dl, V2(rowStart.x + (cardWidth + gap), rowStart.y), cardWidth, "延迟 Latency", "8.2ms", "-6.1%", Spark2, Cyan, 0.4f);
        StatCard(dl, V2(rowStart.x + (cardWidth + gap) * 2f, rowStart.y), cardWidth, "吞吐 Throughput", "42.7K", "+9.8%", Spark3, Mint, 0.8f);
        StatCard(dl, V2(rowStart.x + (cardWidth + gap) * 3f, rowStart.y), cardWidth, "错误 Errors", "0.04%", "-2.3%", Spark4, Rose, 1.2f);

        SetCursorScreen(rowStart.x, rowStart.y + 120f);
        DrawChartCard(dl, avail);
        DrawTableCard(dl, avail);
    }

    static void StatCard(ImDrawList* dl, ImVec2 pos, float width,
        string title, string value, string delta, float[] series, uint accent, float phase)
    {
        const float height = 108f;
        ImVec2 min = pos;
        ImVec2 max = V2(pos.x + width, pos.y + height);

        Rect(dl, min, max, Rgba(0x12, 0x15, 0x1F, 0xE0), 14f);
        RectOutline(dl, min, max, Border, 14f, 1f);

        RectGrad(dl, V2(min.x + 14f, min.y + 1f), V2(max.x - 14f, min.y + 3f),
            Rgba(0, 0, 0, 0), accent, accent, Rgba(0, 0, 0, 0));

        Text(dl, V2(min.x + 16f, min.y + 16f), TextDim, title, 11f);
        Text(dl, V2(min.x + 16f, min.y + 34f), Rgba(0xF2, 0xF4, 0xFA), value, 26f);

        uint deltaColor = delta.StartsWith('+') ? Mint : Rose;
        Text(dl, V2(min.x + 16f, min.y + 68f), deltaColor, delta, 11f);

        float plotWidth = MathF.Max(width * 0.40f, 40f);
        DrawSparkline(dl, V2(max.x - plotWidth - 14f, min.y + 42f), plotWidth, 34f, series, accent, phase);

        const float ringRadius = 11f;
        ImVec2 ringCenter = V2(max.x - ringRadius - 16f, max.y - ringRadius - 16f);
        float target = 0.35f + (MathF.Sin(Time * 0.8f + phase) + 1f) * 0.3f;
        target = Math.Clamp(target, 0f, 1f);
        CircleOutline(dl, ringCenter, ringRadius, Rgba(255, 255, 255, 0x14), 3f, 32);
        DrawArc(dl, ringCenter, ringRadius, -MathF.PI * 0.5f, -MathF.PI * 0.5f + target * MathF.PI * 2f, accent, 3f, 32);
    }

    static void DrawSparkline(ImDrawList* dl, ImVec2 pos, float width, float height, float[] series, uint color, float phase)
    {
        if (series.Length < 2)
        {
            return;
        }

        float min = series[0];
        float max = series[0];
        foreach (float v in series)
        {
            min = MathF.Min(min, v);
            max = MathF.Max(max, v);
        }

        float range = MathF.Max(max - min, 0.0001f);
        float stepX = width / (series.Length - 1);
        uint fill = (color & 0x00FFFFFF) | 0x33000000;

        for (int i = 0; i < series.Length - 1; i++)
        {
            float x0 = pos.x + i * stepX;
            float x1 = x0 + stepX;
            float y0 = pos.y + height - (series[i] - min) / range * height;
            float y1 = pos.y + height - (series[i + 1] - min) / range * height;

            Quad(dl, V2(x0, y0), V2(x1, y1), V2(x1, pos.y + height), V2(x0, pos.y + height), fill);
            Line(dl, V2(x0, y0), V2(x1, y1), color, 1.8f);
        }

        float t = (Time * 0.35f + phase) % 1f;
        int idx = Math.Clamp((int)(t * (series.Length - 1)), 0, series.Length - 1);
        ImVec2 head = V2(pos.x + t * width, pos.y + height - (series[idx] - min) / range * height);
        CircleFill(dl, head, 4.5f, Rgba(255, 255, 255, 0x30), 16);
        CircleFill(dl, head, 2.4f, color, 16);
    }

    static void DrawChartCard(ImDrawList* dl, float width)
    {
        ImVec2 pos = ImGui.GetCursorScreenPos();
        const float height = 200f;

        ImVec2 min = pos;
        ImVec2 max = V2(pos.x + width, pos.y + height);
        Rect(dl, min, max, Rgba(0x12, 0x15, 0x1F, 0xE0), 14f);
        RectOutline(dl, min, max, Border, 14f, 1f);

        Text(dl, V2(min.x + 18f, min.y + 14f), Rgba(0xE8, 0xEA, 0xF2), "吞吐趋势 Throughput", 14f);
        Text(dl, V2(min.x + 18f, min.y + 35f), TextDim, "实时采样  两条序列", 11f);

        float legendX = max.x - 186f;
        CircleFill(dl, V2(legendX, min.y + 22f), 4f, Accent, 12);
        Text(dl, V2(legendX + 10f, min.y + 15f), TextDim, "读取 Read", 11f);
        CircleFill(dl, V2(legendX + 92f, min.y + 22f), 4f, Cyan, 12);
        Text(dl, V2(legendX + 102f, min.y + 15f), TextDim, "写入 Write", 11f);

        ImVec2 plotMin = V2(min.x + 18f, min.y + 62f);
        ImVec2 plotMax = V2(max.x - 18f, max.y - 22f);

        for (int i = 0; i <= 4; i++)
        {
            float y = plotMin.y + (plotMax.y - plotMin.y) * i / 4f;
            Line(dl, V2(plotMin.x, y), V2(plotMax.x, y), Rgba(255, 255, 255, 0x07), 1f);
        }

        DrawWave(dl, plotMin, plotMax, 0.58f, 0.16f, Accent, 0.0f, 2.4f);
        DrawWave(dl, plotMin, plotMax, 0.36f, 0.22f, Cyan, 1.7f, 2.0f);

        SetCursorScreen(pos.x, pos.y + height + 12f);
    }

    static void DrawWave(ImDrawList* dl, ImVec2 plotMin, ImVec2 plotMax,
        float baseLevel, float amplitude, uint color, float phase, float thickness)
    {
        const int Samples = 48;
        float prevX = plotMin.x;
        float prevY = plotMax.y;

        for (int i = 0; i <= Samples; i++)
        {
            float t = i / (float)Samples;
            float wave = MathF.Sin(t * 7.2f + phase + Time * 0.6f) * 0.5f
                       + MathF.Sin(t * 13.4f + phase * 2.1f) * 0.26f
                       + MathF.Sin(t * 2.4f + phase * 0.5f) * 0.34f;

            float norm = Math.Clamp(baseLevel + wave * amplitude, 0.02f, 0.98f);
            float x = plotMin.x + (plotMax.x - plotMin.x) * t;
            float y = plotMax.y - (plotMax.y - plotMin.y) * norm;

            if (i > 0)
            {
                Line(dl, V2(prevX, prevY), V2(x, y), color, thickness);
            }

            prevX = x;
            prevY = y;
        }
    }

    static void DrawTableCard(ImDrawList* dl, float width)
    {
        ImVec2 pos = ImGui.GetCursorScreenPos();
        const float height = 252f;

        ImVec2 min = pos;
        ImVec2 max = V2(pos.x + width, pos.y + height);
        Rect(dl, min, max, Rgba(0x12, 0x15, 0x1F, 0xE0), 14f);
        RectOutline(dl, min, max, Border, 14f, 1f);

        Text(dl, V2(min.x + 18f, min.y + 14f), Rgba(0xE8, 0xEA, 0xF2), "最近构建 Recent builds", 14f);
        Text(dl, V2(min.x + 18f, min.y + 35f), TextDim, "CI 产物一览", 11f);

        SetCursorScreen(min.x + 14f, min.y + 56f);
        PushCellPadding(8f, 6f);

        int tableFlags = (int)ImGuiTableFlags.ImGuiTableFlags_RowBg
            | (int)ImGuiTableFlags.ImGuiTableFlags_BordersInnerH
            | (int)ImGuiTableFlags.ImGuiTableFlags_SizingStretchProp
            | (int)ImGuiTableFlags.ImGuiTableFlags_NoSavedSettings;

        ImVec2 outerSize = V2(0f, 0f);
        if (ImGui.BeginTable("##builds", 5, tableFlags, &outerSize, 0f) != 0)
        {
            int stretch = (int)ImGuiTableColumnFlags.ImGuiTableColumnFlags_WidthStretch;
            ImGui.TableSetupColumn("目标 Target", stretch, 1.6f);
            ImGui.TableSetupColumn("配置", stretch, 1.0f);
            ImGui.TableSetupColumn("耗时", stretch, 0.9f);
            ImGui.TableSetupColumn("状态", stretch, 0.9f);
            ImGui.TableSetupColumn("产物", stretch, 1.1f);
            ImGui.TableHeadersRow();

            (string Target, string Config, string Time, string Status, string Artifact)[] rows =
            [
                ("win-x64", "Release", "2.4s", "成功", "imgui.dll"),
                ("linux-x64", "Release", "3.1s", "成功", "libimgui.so"),
                ("android-arm64", "Release", "4.7s", "成功", "libimgui.so"),
                ("osx-arm64", "Release", "—", "未支持", "—"),
                ("net10.0", "Debug", "1.2s", "成功", "ImGui4NET.dll"),
            ];

            for (int i = 0; i < rows.Length; i++)
            {
                ImGui.TableNextRow(0, 26f);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rows[i].Target);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rows[i].Config);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rows[i].Time);

                ImGui.TableNextColumn();
                uint statusColor = rows[i].Status == "成功" ? Mint : Amber;
                ImVec2 cell = ImGui.GetCursorScreenPos();
                CircleFill(dl, V2(cell.x + 4f, cell.y + 9f), 3.5f, statusColor, 12);
                Text(dl, V2(cell.x + 14f, cell.y + 1f), statusColor, rows[i].Status, 12f);
                Dummy(40f, 18f);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(rows[i].Artifact);
            }

            ImGui.EndTable();
        }

        PopCellPadding();
        SetCursorScreen(pos.x, pos.y + height + 12f);
    }

    // -----------------------------------------------------------------------
    // Components tab
    // -----------------------------------------------------------------------

    static void DrawComponentsTab()
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        float avail = ImGui.GetContentRegionAvail().x;
        float half = (avail - 12f) * 0.5f;

        // Two cards side by side. Each card is a child window so the stock
        // widgets flow normally inside it; mixing flow layout with absolute
        // cursor positioning made the controls overlap.
        CardBegin("##card-controls", half, 372f);
        Text(dl, V2(ImGui.GetCursorScreenPos().x + 2f, ImGui.GetCursorScreenPos().y), TextBright,
            "交互控件 Controls", 14f);
        Dummy(1f, 24f);

        ImGui.PushItemWidth(half - 150f);
        ToggleRow("垂直同步 V-Sync", ref _t1, ref _a1);
        ToggleRow("纹理压缩", ref _t2, ref _a2);
        ToggleRow("调试覆盖层", ref _t3, ref _a3);
        ToggleRow("自动缩放 DPI", ref _t4, ref _a4);

        ImGui.Spacing();
        SliderF("渲染质量", ref _sliderQuality, 0f, 1f, "%.2f");
        SliderF("界面缩放", ref _sliderScale, 0.75f, 2f, "%.2f×");
        SliderI("线程数", ref _sliderThreads, 1, 32);

        ImGui.Spacing();
        string[] profiles = ["Balanced", "Performance", "Power Saver"];
        if (ImGui.BeginCombo("配置档 Profile", profiles[_comboProfile]) != 0)
        {
            for (int i = 0; i < profiles.Length; i++)
            {
                ImVec2 selSize = V2(0f, 0f);
                if (ImGui.Selectable(profiles[i], i == _comboProfile ? (byte)1 : (byte)0, 0, &selSize) != 0)
                {
                    _comboProfile = i;
                }
            }

            ImGui.EndCombo();
        }

        CheckboxB("显示 ImGui 演示窗口", ref _showDemo);
        ImGui.PopItemWidth();
        ImGui.EndChild();
        CardEnd();

        ImGui.SameLine(0f, 12f);

        CardBegin("##card-gauges", half, 372f);
        Text(dl, V2(ImGui.GetCursorScreenPos().x + 2f, ImGui.GetCursorScreenPos().y), TextBright,
            "仪表 Gauges", 14f);

        ImVec2 gaugeOrigin = ImGui.GetCursorScreenPos();
        Gauge(dl, V2(gaugeOrigin.x + half * 0.26f, gaugeOrigin.y + 78f), 46f, _sliderQuality, Accent, "质量");
        Gauge(dl, V2(gaugeOrigin.x + half * 0.70f, gaugeOrigin.y + 78f), 46f, _sliderThreads / 32f, Cyan, "线程");

        Dummy(1f, 152f);
        ImGui.PushItemWidth(half - 36f);
        ColorEdit("强调色 Accent", ref _colorR, ref _colorG, ref _colorB);
        ImGui.PopItemWidth();

        // Swatches: a palette the user can click to set the accent color.
        ImVec2 swatch = ImGui.GetCursorScreenPos();
        ImDrawList* swatchDl = ImGui.GetWindowDrawList();
        Text(swatchDl, V2(swatch.x + 2f, swatch.y + 6f), TextDim, "色板", 11f);
        for (int i = 0; i < 8; i++)
        {
            ImVec2 p = V2(swatch.x + 44f + i * 24f, swatch.y + 2f);
            ImGui.SetCursorScreenPos(&p);
            bool hit = InvisibleButton($"##swatch{i}", 18f, 18f);

            float hue = i / 8f;
            uint c = HsvToRgba(hue, 0.62f, 0.95f);
            bool selected = MathF.Abs(hue - RgbToHue(_colorR, _colorG, _colorB)) < 0.0625f;

            if (hit)
            {
                HsvToRgb(hue, 0.62f, 0.95f, out _colorR, out _colorG, out _colorB);
            }

            bool hovered = ImGui.IsItemHovered() != 0;
            Rect(swatchDl, p, V2(p.x + 18f, p.y + 18f), c, 6f);
            uint ring = selected ? Rgba(255, 255, 255, 0xE0)
                : (hovered ? Rgba(255, 255, 255, 0x70) : Rgba(255, 255, 255, 0x22));
            RectOutline(swatchDl, p, V2(p.x + 18f, p.y + 18f), ring, 6f, selected ? 2f : 1f);
        }

        Dummy(1f, 24f);
        ImGui.EndChild();
        CardEnd();

        CardBegin("##card-tree", avail, 168f);
        Text(dl, V2(ImGui.GetCursorScreenPos().x + 2f, ImGui.GetCursorScreenPos().y), TextBright,
            "结构树 Tree", 14f);
        Dummy(1f, 22f);

        if (ImGui.TreeNode("ImGui4NET") != 0)
        {
            if (ImGui.TreeNode("src") != 0)
            {
                ImGui.TextUnformatted("ImGui4NET   core bindings");
                ImGui.TextUnformatted("ImGui4NET.Club   imgui_club extensions");
                ImGui.TextUnformatted("ImGui4NET.Backend.*   14 backends");
                ImGui.TreePop();
            }

            if (ImGui.TreeNode("samples") != 0)
            {
                ImGui.TextUnformatted("ImGui4NET.Sample.OpenGL3");
                ImGui.TreePop();
            }

            ImGui.TreePop();
        }

        ImGui.EndChild();
        CardEnd();
    }

    /// <summary>
    /// Opens a rounded, bordered card as a child window. The caller must call
    /// <see cref="ImGui.EndChild"/> followed by <see cref="CardEnd"/>.
    /// </summary>
    static void CardBegin(string id, float width, float height)
    {
        PushChildBg(Rgba(0x12, 0x15, 0x1F, 0xE0).ToVec4());
        PushWindowPadding(18f, 14f);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_ChildRounding, 14f);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_ChildBorderSize, 1f);
        ImGui.PushStyleColor((int)ImGuiCol.ImGuiCol_Border, Border);

        ImVec2 size = V2(width, height);
        ImGui.BeginChild(id, &size, (int)ImGuiChildFlags.ImGuiChildFlags_Borders, 0);
    }

    /// <summary>Pops everything pushed by <see cref="CardBegin"/>.</summary>
    static void CardEnd()
    {
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
        PopWindowPadding();
        PopChildBg();
    }

    static void ToggleRow(string label, ref bool value, ref float anim)
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        const float trackWidth = 40f;
        const float trackHeight = 20f;

        // Label on the left, switch on the right: both are submitted as items so
        // the cursor advances normally and nothing can overlap.
        ImVec2 rowStart = ImGui.GetCursorScreenPos();
        float labelWidth = ImGui.GetContentRegionAvail().x - trackWidth - 8f;

        Text(dl, V2(rowStart.x, rowStart.y + 2f), Rgba(0xC8, 0xCC, 0xDC), label, 12f);
        Dummy(labelWidth, trackHeight);

        ImGui.SameLine(0f, 0f);
        ImVec2 trackMin = ImGui.GetCursorScreenPos();
        ImVec2 trackMax = V2(trackMin.x + trackWidth, trackMin.y + trackHeight);

        bool clicked = InvisibleButton($"##toggle-{label}", trackWidth, trackHeight);
        bool hovered = ImGui.IsItemHovered() != 0;

        if (clicked)
        {
            value = !value;
        }

        uint track = value
            ? Rgba(0x7C, 0x6C, 0xFF, hovered ? 0xF0 : 0xCC)
            : Rgba(255, 255, 255, hovered ? 0x18 : 0x10);

        Rect(dl, trackMin, trackMax, track, trackHeight * 0.5f);

        float knobX = trackMin.x + trackHeight * 0.5f + (trackWidth - trackHeight) * Math.Clamp(anim, 0f, 1f);
        CircleFill(dl, V2(knobX, trackMin.y + trackHeight * 0.5f), trackHeight * 0.5f - 3f, Rgba(255, 255, 255, 0xF4), 20);
    }

    static void Gauge(ImDrawList* dl, ImVec2 center, float radius, float value, uint color, string caption)
    {
        value = Math.Clamp(value, 0f, 1f);

        CircleOutline(dl, center, radius, Rgba(255, 255, 255, 0x12), 7f, 48);
        DrawArc(dl, center, radius, -MathF.PI * 0.5f, -MathF.PI * 0.5f + value * MathF.PI * 2f, color, 7f, 48);

        string pct = $"{value * 100f:F0}%";
        ImVec2 size = Measure(pct, 17f);
        Text(dl, V2(center.x - size.x * 0.5f, center.y - 12f), Rgba(0xF0, 0xF2, 0xF8), pct, 17f);

        ImVec2 capSize = Measure(caption, 11f);
        Text(dl, V2(center.x - capSize.x * 0.5f, center.y + radius + 10f), TextDim, caption, 11f);
    }

    // -----------------------------------------------------------------------
    // Memory / settings / about tabs
    // -----------------------------------------------------------------------

    static void DrawMemoryTab()
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 start = ImGui.GetCursorScreenPos();

        Text(dl, V2(start.x, start.y), Rgba(0xE8, 0xEA, 0xF2), "imgui_club  Memory Editor", 15f);
        Text(dl, V2(start.x, start.y + 23f), TextDim,
                    "托管移植版（MemoryEditor），支持十六进制/ASCII 双视图与数据预览。", 11f);

                SetCursorScreen(start.x, start.y + 52f);

                _memEditor?.DrawContents(_memData, MemorySize, 0);
            }

    static void DrawSettingsTab()
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 start = ImGui.GetCursorScreenPos();
        float avail = ImGui.GetContentRegionAvail().x;

        ImVec2 min = start;
        ImVec2 max = V2(start.x + avail, start.y + 262f);
        Rect(dl, min, max, Rgba(0x12, 0x15, 0x1F, 0xE0), 14f);
        RectOutline(dl, min, max, Border, 14f, 1f);

        Text(dl, V2(min.x + 18f, min.y + 14f), Rgba(0xE8, 0xEA, 0xF2), "运行时信息 Runtime", 14f);

        SetCursorScreen(min.x + 18f, min.y + 48f);
        ImGui.TextUnformatted("目标框架      net10.0");
        ImGui.TextUnformatted("原生库        imgui.dll");
        ImGui.TextUnformatted($"字体          {(_fontIsYahei ? "Microsoft YaHei (msyh.ttc)" : "fallback")}");
        ImGui.TextUnformatted($"帧时间        {_frameMs:F2} ms");
        ImGui.TextUnformatted($"帧率          {_fps:F1} FPS");
        ImGui.TextUnformatted($"ImGui 版本    {Marshal.PtrToStringUTF8((nint)ImGui.GetVersion())}");

        SetCursorScreen(min.x + 18f, min.y + 210f);
        ImGui.TextUnformatted("所有文字均使用同一字体、不同像素尺寸动态烘焙，用于验证缩放清晰度。");
    }

    static void DrawAboutTab()
    {
        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 start = ImGui.GetCursorScreenPos();
        float avail = ImGui.GetContentRegionAvail().x;

        ImVec2 min = start;
        ImVec2 max = V2(start.x + avail, start.y + 312f);
        Rect(dl, min, max, Rgba(0x12, 0x15, 0x1F, 0xE0), 14f);
        RectOutline(dl, min, max, Border, 14f, 1f);

        float cx = min.x + avail * 0.5f;
        ImVec2 logo = V2(cx, min.y + 78f);
        for (int i = 6; i >= 1; i--)
        {
            CircleFill(dl, logo, 22f + i * 10f, Rgba(0x7C, 0x6C, 0xFF, 4 + i), 48);
        }

        CircleFill(dl, logo, 26f, Accent, 48);

        const string title = "ImGui4NET";
        ImVec2 ts = Measure(title, 22f);
        Text(dl, V2(cx - ts.x * 0.5f, min.y + 126f), Rgba(0xF2, 0xF4, 0xFA), title, 22f);

        const string sub = "Bare-metal .NET bindings for Dear ImGui 1.93.0-docking";
        ImVec2 ss = Measure(sub, 12f);
        Text(dl, V2(cx - ss.x * 0.5f, min.y + 158f), TextDim, sub, 12f);

        string[] tags = ["net8.0", "net10.0", "NativeAOT", "Trim-safe", "MIT"];
        float total = 0f;
        foreach (string tag in tags)
        {
            total += Measure(tag, 11f).x + 22f;
        }

        total += (tags.Length - 1) * 8f;

        float tx = cx - total * 0.5f;
        foreach (string tag in tags)
        {
            float w = Measure(tag, 11f).x + 22f;
            ImVec2 tagMin = V2(tx, min.y + 192f);
            ImVec2 tagMax = V2(tx + w, min.y + 218f);
            Rect(dl, tagMin, tagMax, Rgba(0x7C, 0x6C, 0xFF, 0x22), 13f);
            RectOutline(dl, tagMin, tagMax, Rgba(0x7C, 0x6C, 0xFF, 0x44), 13f, 1f);
            Text(dl, V2(tx + 11f, min.y + 198f), Rgba(0xC8, 0xC4, 0xFF), tag, 11f);
            tx += w + 8f;
        }

        const string footer = "按 Esc 退出";
        ImVec2 fs = Measure(footer, 11f);
        Text(dl, V2(cx - fs.x * 0.5f, min.y + 252f), TextDim, footer, 11f);

        Dummy(avail, 1f);
    }

    // -----------------------------------------------------------------------
    // Right panel
    // -----------------------------------------------------------------------

    static void DrawRightPanel(float height)
    {
        PushChildBg(ColBgPanel);
        PushWindowPadding(16f, 16f);
        BeginChild("##right", RightWidth, height);

        ImDrawList* dl = ImGui.GetWindowDrawList();
        ImVec2 origin = ImGui.GetWindowPos();

        Text(dl, V2(origin.x + 16f, origin.y + 10f), TextDim, "活动 ACTIVITY", 11f);
        ImGui.SetCursorPosY(36f);

        (string When, string What, uint Color)[] events =
        [
            ("刚刚", "构建完成 win-x64", Mint),
            ("1 分钟前", "NuGet 包已推送 1.93.0.1", Accent),
            ("3 分钟前", "绑定重新生成 1204 个符号", Cyan),
            ("8 分钟前", "CI 通过 (ubuntu-latest)", Mint),
            ("12 分钟前", "osx-arm64 目标暂不支持", Amber),
            ("25 分钟前", "示例项目接入 Club", Cyan),
        ];

        foreach ((string when, string what, uint color) in events)
        {
            ImVec2 row = ImGui.GetCursorScreenPos();
            CircleFill(dl, V2(row.x + 4f, row.y + 8f), 3.5f, color, 12);
            Line(dl, V2(row.x + 4f, row.y + 14f), V2(row.x + 4f, row.y + 30f), Rgba(255, 255, 255, 0x10), 1f);

            Text(dl, V2(row.x + 16f, row.y), Rgba(0xC8, 0xCC, 0xDC), what, 12f);
            Text(dl, V2(row.x + 16f, row.y + 17f), TextFaint, when, 10f);

            SetCursorScreen(row.x, row.y + 34f);
            Dummy(1f, 0f);
        }

        const float cardHeight = 138f;
        ImVec2 cardMin = V2(origin.x + 16f, origin.y + height - cardHeight - 16f);
        ImVec2 cardMax = V2(origin.x + RightWidth - 16f, origin.y + height - 16f);
        Rect(dl, cardMin, cardMax, Rgba(0x12, 0x15, 0x1F, 0xE0), 12f);
        RectOutline(dl, cardMin, cardMax, Border, 12f, 1f);

        Text(dl, V2(cardMin.x + 14f, cardMin.y + 12f), Rgba(0xE8, 0xEA, 0xF2), "系统负载", 12f);

        float[] usage = [0.42f, 0.68f, 0.31f];
        string[] labels = ["CPU", "GPU", "MEM"];
        uint[] colors = [Accent, Cyan, Amber];

        for (int i = 0; i < usage.Length; i++)
        {
            float y = cardMin.y + 42f + i * 30f;
            Text(dl, V2(cardMin.x + 14f, y), TextDim, labels[i], 10f);

            ImVec2 barMin = V2(cardMin.x + 54f, y + 3f);
            ImVec2 barMax = V2(cardMax.x - 52f, y + 11f);
            Rect(dl, barMin, barMax, Rgba(255, 255, 255, 0x0C), 4f);

            float animated = Math.Clamp(usage[i] + MathF.Sin(Time * 0.9f + i) * 0.04f, 0f, 1f);
            Rect(dl, barMin, V2(barMin.x + (barMax.x - barMin.x) * animated, barMax.y), colors[i], 4f);

            string pct = $"{animated * 100f:F0}%";
            ImVec2 ps = Measure(pct, 10f);
            Text(dl, V2(cardMax.x - ps.x - 14f, y), TextDim, pct, 10f);
        }

        ImGui.EndChild();
        PopWindowPadding();
        PopChildBg();
    }

    // -----------------------------------------------------------------------
    // Draw-list wrappers
    // -----------------------------------------------------------------------

    static float Time => (float)ImGui.GetTime();

    static void Text(ImDrawList* dl, ImVec2 pos, uint color, string text, float size)
    {
        ImVec2 p = pos;
        // cpu_fine_clip_rect is null-checked natively, so null is safe here.
        ImDrawList.AddText(dl, null, size, &p, color, text, null, 0f, null);
    }

    // Measuring text requires pushing a font size, which is relatively costly.
    // The labels are static, so memoize per (text, size).
    static readonly Dictionary<(string, float), ImVec2> _measureCache = new();

    static ImVec2 Measure(string text, float size)
    {
        if (_measureCache.TryGetValue((text, size), out ImVec2 cached))
        {
            return cached;
        }

        ImGui.PushFont(null, size);
        ImVec2 result = ImGui.CalcTextSize(text, null, 0, -1f);
        ImGui.PopFont();

        _measureCache[(text, size)] = result;
        return result;
    }

    static void Rect(ImDrawList* dl, ImVec2 min, ImVec2 max, uint color, float rounding = 0f)
    {
        ImVec2 a = min;
        ImVec2 b = max;
        ImDrawList.AddRectFilled(dl, &a, &b, color, rounding);
    }

    static void RectOutline(ImDrawList* dl, ImVec2 min, ImVec2 max, uint color, float rounding, float thickness)
    {
        ImVec2 a = min;
        ImVec2 b = max;
        ImDrawList.AddRect(dl, &a, &b, color, rounding, thickness);
    }

    static void RectGrad(ImDrawList* dl, ImVec2 min, ImVec2 max, uint ul, uint ur, uint br, uint bl)
    {
        ImVec2 a = min;
        ImVec2 b = max;
        ImDrawList.AddRectFilledMultiColor(dl, &a, &b, ul, ur, br, bl);
    }

    static void Line(ImDrawList* dl, ImVec2 a, ImVec2 b, uint color, float thickness)
    {
        ImVec2 p1 = a;
        ImVec2 p2 = b;
        ImDrawList.AddLine(dl, &p1, &p2, color, thickness);
    }

    static void CircleFill(ImDrawList* dl, ImVec2 center, float radius, uint color, int segments)
    {
        ImVec2 c = center;
        ImDrawList.AddCircleFilled(dl, &c, radius, color, segments);
    }

    static void CircleOutline(ImDrawList* dl, ImVec2 center, float radius, uint color, float thickness, int segments)
    {
        ImVec2 c = center;
        ImDrawList.AddCircle(dl, &c, radius, color, segments, thickness);
    }

    static void Quad(ImDrawList* dl, ImVec2 a, ImVec2 b, ImVec2 c, ImVec2 d, uint color)
    {
        ImVec2 p1 = a, p2 = b, p3 = c, p4 = d;
        ImDrawList.AddQuadFilled(dl, &p1, &p2, &p3, &p4, color);
    }

    static void DrawArc(ImDrawList* dl, ImVec2 center, float radius, float a0, float a1,
        uint color, float thickness, int segments)
    {
        if (a1 <= a0)
        {
            return;
        }

        // Coarse arcs look identical at these radii and cost a fraction of the
        // draw commands, which matters because several are rebuilt every frame.
        segments = Math.Clamp(segments / 2, 8, 24);

        float step = (a1 - a0) / segments;
        float prevX = center.x + MathF.Cos(a0) * radius;
        float prevY = center.y + MathF.Sin(a0) * radius;

        for (int i = 1; i <= segments; i++)
        {
            float a = a0 + step * i;
            float x = center.x + MathF.Cos(a) * radius;
            float y = center.y + MathF.Sin(a) * radius;
            Line(dl, V2(prevX, prevY), V2(x, y), color, thickness);
            prevX = x;
            prevY = y;
        }
    }

    // -----------------------------------------------------------------------
    // Widget wrappers (ref -> pointer)
    // -----------------------------------------------------------------------

    static bool InvisibleButton(string id, float width, float height)
    {
        ImVec2 size = V2(width, height);
        return ImGui.InvisibleButton(id, &size) != 0;
    }

    static void SetCursor(float x, float y)
    {
        ImVec2 pos = V2(x, y);
        ImGui.SetCursorPos(&pos);
    }

    static void SetCursorScreen(float x, float y)
    {
        ImVec2 pos = V2(x, y);
        ImGui.SetCursorScreenPos(&pos);
    }

    static void Dummy(float width, float height)
    {
        ImVec2 size = V2(width, height);
        ImGui.Dummy(&size);
    }

    static void BeginChild(string id, float width, float height)
    {
        ImVec2 size = V2(width, height);
        ImGui.BeginChild(id, &size, 0, 0);
    }

    static bool SliderF(string label, ref float value, float min, float max, string? format = null)
    {
        float temp = value;
        bool changed = ImGui.SliderFloat(label, &temp, min, max, format) != 0;
        if (changed)
        {
            value = temp;
        }

        return changed;
    }

    static bool SliderI(string label, ref int value, int min, int max)
    {
        int temp = value;
        bool changed = ImGui.SliderInt(label, &temp, min, max) != 0;
        if (changed)
        {
            value = temp;
        }

        return changed;
    }

    static bool CheckboxB(string label, ref bool value)
    {
        bool temp = value;
        bool changed = ImGui.Checkbox(label, &temp) != 0;
        if (changed)
        {
            value = temp;
        }

        return changed;
    }

    static bool ColorEdit(string label, ref float r, ref float g, ref float b)
    {
        float* rgb = stackalloc float[3] { r, g, b };
        // NoSidePreview keeps the RGB fields inside the card; the swatch row
        // below the widget is the primary way to pick a color.
        int flags = (int)ImGuiColorEditFlags.ImGuiColorEditFlags_NoSidePreview
            | (int)ImGuiColorEditFlags.ImGuiColorEditFlags_NoOptions
            | (int)ImGuiColorEditFlags.ImGuiColorEditFlags_NoTooltip;
        bool changed = ImGui.ColorEdit3(label, rgb, flags) != 0;
        if (changed)
        {
            r = rgb[0];
            g = rgb[1];
            b = rgb[2];
        }

        return changed;
    }

    static void PushChildBg(ImVec4 color)
    {
        ImVec4 c = color;
        ImGui.PushStyleColor((int)ImGuiCol.ImGuiCol_ChildBg, &c);
    }

    static void PopChildBg() => ImGui.PopStyleColor();

    static void PushWindowPadding(float x, float y)
    {
        ImVec2 pad = V2(x, y);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_WindowPadding, &pad);
    }

    static void PopWindowPadding() => ImGui.PopStyleVar();

    static void PushCellPadding(float x, float y)
    {
        ImVec2 pad = V2(x, y);
        ImGui.PushStyleVar((int)ImGuiStyleVar.ImGuiStyleVar_CellPadding, &pad);
    }

    static void PopCellPadding() => ImGui.PopStyleVar();

    // -----------------------------------------------------------------------
    // Small helpers
    // -----------------------------------------------------------------------

    static float Approach(float current, float target, float step)
        => current < target ? MathF.Min(current + step, target) : MathF.Max(current - step, target);

    static ImVec2 V2(float x, float y) => new() { x = x, y = y };

    static ImVec4 V4(float x, float y, float z, float w) => new() { x = x, y = y, z = z, w = w };

    static uint Rgba(int r, int g, int b, int a = 255)
        => (uint)((a << 24) | (b << 16) | (g << 8) | r);

    static void SetCol(ImGuiStyle* style, ImGuiCol index, ImVec4 color)
        => style->Colors[(int)index] = color;

    static uint HsvToRgba(float h, float s, float v)
    {
        HsvToRgb(h, s, v, out float r, out float g, out float b);
        return Rgba((int)(r * 255f), (int)(g * 255f), (int)(b * 255f));
    }

    static void HsvToRgb(float h, float s, float v, out float r, out float g, out float b)
    {
        int i = (int)(h * 6f);
        float f = h * 6f - i;
        float p = v * (1f - s);
        float q = v * (1f - f * s);
        float t = v * (1f - (1f - f) * s);

        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
    }

    static float RgbToHue(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min;
        if (d < 0.0001f)
        {
            return 0f;
        }

        float h;
        if (max == r)
        {
            h = ((g - b) / d) % 6f;
        }
        else if (max == g)
        {
            h = (b - r) / d + 2f;
        }
        else
        {
            h = (r - g) / d + 4f;
        }

        h /= 6f;
        return h < 0f ? h + 1f : h;
    }
}

/// <summary>Converts a packed ImU32 color (0xAABBGGRR) to an ImVec4.</summary>
file static class ColorExtensions
{
    public static ImVec4 ToVec4(this uint rgba) => new()
    {
        x = (rgba & 0xFF) / 255f,
        y = ((rgba >> 8) & 0xFF) / 255f,
        z = ((rgba >> 16) & 0xFF) / 255f,
        w = ((rgba >> 24) & 0xFF) / 255f,
    };
}