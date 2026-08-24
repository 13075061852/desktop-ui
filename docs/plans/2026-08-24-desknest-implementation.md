# DeskNest MVP Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build and package a lightweight Windows 10/11 desktop icon organizer with Fluent-style desktop zones and non-destructive file mappings.

**Architecture:** Use a dependency-free .NET 8 solution with a platform-neutral core library, a WPF application, and a console test harness. Keep all persistent state local in JSON; isolate Win32 desktop hosting, Shell operations, icon visibility, and tray integration behind focused services.

**Tech Stack:** C# 12, .NET 8, WPF, Win32 P/Invoke, System.Text.Json, Windows Forms NotifyIcon, custom console test harness.

---

### Task 1: Bootstrap repository and toolchain

**Files:**
- Create: `.gitignore`
- Create: `README.md`
- Create: `DeskNest.sln`
- Create: `src/DeskNest.Core/DeskNest.Core.csproj`
- Create: `src/DeskNest.App/DeskNest.App.csproj`
- Create: `tests/DeskNest.Tests/DeskNest.Tests.csproj`

**Steps:**
1. Initialize Git and add ignore rules for build output, user settings, and packages.
2. Install a local .NET 8 SDK because the machine currently has only runtimes.
3. Create the solution and three projects without third-party NuGet packages.
4. Add project references from the app and tests to core.
5. Run `dotnet restore` and verify success.

### Task 2: Build and test core models and classifier

**Files:**
- Create: `src/DeskNest.Core/Models/AppState.cs`
- Create: `src/DeskNest.Core/Models/ZoneModel.cs`
- Create: `src/DeskNest.Core/Models/DesktopItem.cs`
- Create: `src/DeskNest.Core/Services/RuleClassifier.cs`
- Create: `src/DeskNest.Core/Services/DesktopScanner.cs`
- Create: `tests/DeskNest.Tests/Program.cs`

**Steps:**
1. Add failing assertions for default state creation and extension classification.
2. Implement default zones with stable category keys and geometry.
3. Implement case-insensitive classification for apps, documents, images, folders, and fallback items.
4. Implement desktop scanning with path normalization and duplicate removal.
5. Run the test harness and verify every assertion passes.
6. Commit the core behavior.

### Task 3: Add safe persistence and tests

**Files:**
- Create: `src/DeskNest.Core/Services/JsonStateStore.cs`
- Modify: `tests/DeskNest.Tests/Program.cs`

**Steps:**
1. Add failing assertions for JSON round-trip, malformed-primary backup recovery, and missing-state defaults.
2. Implement asynchronous load/save with a temporary file and `.bak` backup.
3. Normalize loaded models and recover safely from invalid JSON.
4. Run the test harness and verify every assertion passes.
5. Commit persistence behavior.

### Task 4: Implement Win32 and Shell services

**Files:**
- Create: `src/DeskNest.App/Interop/NativeMethods.cs`
- Create: `src/DeskNest.App/Services/DesktopHostService.cs`
- Create: `src/DeskNest.App/Services/DesktopIconVisibilityService.cs`
- Create: `src/DeskNest.App/Services/ShellService.cs`
- Create: `src/DeskNest.App/Services/ShellIconService.cs`
- Create: `src/DeskNest.App/Services/DesktopWatcher.cs`
- Create: `src/DeskNest.App/Services/TrayService.cs`

**Steps:**
1. Define only the required User32, Shell32, and DWM P/Invoke signatures.
2. Implement WorkerW discovery, WPF handle reparenting, child styles, and safe fallback.
3. Implement reversible visibility control for the Explorer desktop list view.
4. Implement safe Shell open, reveal, and native icon extraction.
5. Implement debounced desktop file watching.
6. Implement a disposable tray icon and menu.
7. Build with warnings treated as actionable.

### Task 5: Build the Fluent desktop UI

**Files:**
- Create: `src/DeskNest.App/App.xaml`
- Create: `src/DeskNest.App/App.xaml.cs`
- Create: `src/DeskNest.App/Themes/DeskNestTheme.xaml`
- Create: `src/DeskNest.App/MainWindow.xaml`
- Create: `src/DeskNest.App/MainWindow.xaml.cs`
- Create: `src/DeskNest.App/Controls/ZoneCard.xaml`
- Create: `src/DeskNest.App/Controls/ZoneCard.xaml.cs`
- Create: `src/DeskNest.App/SettingsWindow.xaml`
- Create: `src/DeskNest.App/SettingsWindow.xaml.cs`

**Steps:**
1. Add application-level typography, color, card, toolbar, and button resources.
2. Build a transparent desktop surface with a compact top toolbar and unobtrusive status pill.
3. Build draggable/resizable/collapsible zone cards and adaptive item grids.
4. Support external file drop, internal mapping transfer, double-click open, and context actions.
5. Bind one-click classification, layout lock, icon visibility, new zone, settings, and save commands.
6. Implement single-instance startup, tray lifecycle, state loading, and clean recovery on exit.
7. Build and resolve all XAML/compiler errors.

### Task 6: Package and verify the MVP

**Files:**
- Create: `scripts/build-release.ps1`
- Create: `docs/USER-GUIDE.md`
- Create: `assets/README.md`

**Steps:**
1. Add a deterministic Release publish script for `win-x64`, framework-dependent and ready-to-run disabled for size.
2. Run core tests in Release configuration.
3. Publish to `dist/DeskNest-win-x64`.
4. Measure artifact size and inspect expected executable/configuration files.
5. Launch a smoke test, verify the process stays alive, then close it cleanly.
6. Document install, use, data location, limitations, and uninstall steps.
7. Run final `git status`, tests, build, and publish checks.
8. Commit the delivered MVP.
