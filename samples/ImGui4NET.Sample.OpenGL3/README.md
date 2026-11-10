# ImGui4NET.Sample.OpenGL3

**English** | [简体中文](README.zh-CN.md)

A complete Dear ImGui application built directly on the raw ImGui4NET bindings:
OpenTK supplies the window and the OpenGL 4.1 context, and every pixel of the UI
is drawn through `ImDrawList`.

![Dashboard](docs/dashboard.png)

## What it demonstrates

- **Bare-metal bindings** — `ImGui.CreateContext`, `ImGuiIO`, `ImGui.NewFrame`
  and `ImGui_ImplOpenGL3.*` are called exactly as the C++ API defines them. There
  is no wrapper layer.
- **Custom-drawn chrome** — the top bar, left rail, stat cards, sparklines,
  progress rings and the two-series chart are all `ImDrawList` primitives, so the
  layout does not depend on the stock widget metrics.
- **Microsoft YaHei** — `ShowcaseUi.LoadFonts` loads `msyh.ttc` from the Windows
  font directory with the Simplified Chinese glyph range, falling back to other
  CJK fonts and finally to the embedded default. All text sizes are baked from
  that single font through the dynamic atlas added in Dear ImGui 1.92.
- **imgui_club** — the Memory tab embeds `MemoryEditor` from `ImGui4NET.Club`.
- **Screenshot capture** — `Screenshot.Capture` reads the back buffer with
  `glReadPixels` and writes a PNG, which is how the images below were produced.

## Requirements

- .NET 10 SDK
- A GPU/driver with OpenGL 4.1
- Windows for the Microsoft YaHei font path; other platforms fall back
  automatically

## Running

```bash
dotnet run --project samples/ImGui4NET.Sample.OpenGL3
```

Press `Esc` to quit and `F12` to save a screenshot next to the working
directory.

## Pages

### Dashboard

Four stat cards with animated sparklines and progress rings, a live two-series
throughput chart, and a table of recent CI builds.

![Dashboard](docs/dashboard.png)

### Components

Custom toggle switches, sliders, a profile combo, circular gauges, an accent
colour picker with a clickable palette, and a tree view. Each card is a child
window so the stock widgets flow normally inside it.

![Components](docs/components.png)

### Memory

The `imgui_club` Memory Editor, driven through its pure C# port
(`MemoryEditor.DrawContents`).

![Memory](docs/memory.png)

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | OpenTK host: context creation, per-frame input feed, render loop |
| `ShowcaseUi.cs` | The whole dashboard: theme, fonts, layout, custom widgets |
| `Screenshot.cs` | `glReadPixels` back-buffer capture to PNG |