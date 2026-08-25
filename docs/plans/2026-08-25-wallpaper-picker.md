# Desktop Wallpaper Picker Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the settings privacy card with a Windows desktop wallpaper picker containing four bundled backgrounds and custom image import.

**Architecture:** Bundle optimized JPEG wallpapers beside the executable. A Win32 wallpaper service previews and applies selections; `AppState` persists either a built-in key or copied custom image path. Settings restores the prior wallpaper on cancel.

**Tech Stack:** C# 12, .NET 8 WPF, Win32 `SystemParametersInfo`, generated JPEG assets.

---

### Task 1: Bundle default wallpaper assets
- Generate four 16:10 wallpapers in distinct visual styles.
- Optimize to JPEG and copy them to the publish output through `DeskNest.App.csproj`.

### Task 2: Add wallpaper persistence and Windows integration
- Add wallpaper selection fields to `AppState` normalization.
- Implement built-in path resolution, current wallpaper retrieval, wallpaper application, and safe custom-image copying.
- Add unit tests for built-in selection resolution.

### Task 3: Build the settings wallpaper picker
- Remove the privacy card.
- Add four preview cards, selected state, and an import button.
- Preview immediately, restore the previous wallpaper on cancel, and persist on save.
- Follow the active light/dark settings theme.

### Task 4: Verify and package
- Run all tests.
- Publish the x64 package and ensure `Wallpapers/*.jpg` are present.
- Launch, preview a bundled wallpaper, test cancel restoration, and verify the settings layout visually.
