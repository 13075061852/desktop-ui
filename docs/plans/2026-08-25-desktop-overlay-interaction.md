# Interactive Desktop Overlay Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Make DeskNest’s desktop-only overlay reliably draggable and clickable, add basic zone color customization, and render sharp Shell icons.

**Architecture:** Keep DeskNest attached to the Explorer desktop view so normal applications remain above it. Correct WPF routed-input/capture behavior inside zone headers, pass empty overlay areas through to Explorer, and obtain icons from the Windows system image list at extra-large resolution with a safe fallback.

**Tech Stack:** C# 12, .NET 8, WPF, Win32 user32/shell32 interop.

---

### Task 1: Repair zone interaction

**Files:**
- Modify: `src/DeskNest.App/Controls/ZoneCard.xaml.cs`

**Steps:**
1. Ignore header drag initiation when the original click comes from a button or text editor.
2. Capture the header element rather than the whole `UserControl`, so its move/up handlers continue receiving routed events.
3. Release capture from the same element after the drag.
4. Add a color preset submenu that updates `ZoneModel.AccentColor`, refreshes the card, and saves state.
5. Build the WPF project and verify there are no compile errors.

### Task 2: Add empty-area desktop click-through

**Files:**
- Modify: `src/DeskNest.App/MainWindow.xaml`
- Modify: `src/DeskNest.App/MainWindow.xaml.cs`
- Modify: `src/DeskNest.App/Interop/NativeMethods.cs`

**Steps:**
1. Add `WM_NCHITTEST`, `HTCLIENT`, and `HTTRANSPARENT` constants.
2. Install an `HwndSource` message hook when the window handle is initialized.
3. Treat the toolbar and zone rectangles as interactive.
4. Return `HTTRANSPARENT` for the rest of the transparent fullscreen window so Explorer retains desktop interaction.
5. Remove the hook during shutdown.

### Task 3: Load high-resolution Shell icons

**Files:**
- Modify: `src/DeskNest.App/Interop/NativeMethods.cs`
- Modify: `src/DeskNest.App/Services/ShellIconService.cs`

**Steps:**
1. Resolve each file’s system image-list index with `SHGetFileInfo`.
2. Query the extra-large Windows image list and clone the icon handle.
3. Preserve the native bitmap dimensions in the WPF image source.
4. Fall back to the existing large-icon path if the image-list query fails.
5. Always destroy cloned icon handles.

### Task 4: Verify and demonstrate

**Files:**
- Verify: `tests/DeskNest.Tests/Program.cs`
- Build output: `dist/DeskNest-win-x64/`

**Steps:**
1. Run all core tests and expect `6/6 tests passed`.
2. Publish the Windows x64 release.
3. Restart DeskNest.
4. Programmatically drag a zone and verify its persisted coordinates change.
5. Capture a desktop screenshot to verify sharp icons and visible controls.
