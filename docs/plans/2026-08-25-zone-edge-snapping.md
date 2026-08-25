# Zone Edge Snapping Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use executing-plans to implement this plan task-by-task.

**Goal:** Add 10px magnetic four-edge alignment for zone moving and resizing, with independent X/Y snapping and subtle dashed guides.

**Architecture:** Add a pure Core resolver that snaps a collision-safe candidate to obstacle edges and returns optional vertical/horizontal guide positions. Keep the existing 12px collision resolver authoritative by validating each snapped axis independently. ZoneCard will retain raw resize deltas for proper release hysteresis, while MainWindow renders non-interactive guide lines.

**Tech Stack:** .NET 8, WPF, C#, existing console regression tests.

---

### Task 1: Core edge snap resolver

**Files:**
- Create: `src/DeskNest.Core/Services/ZoneAlignmentResolver.cs`
- Modify: `tests/DeskNest.Tests/Program.cs`

1. Add failing tests for move X/Y snapping, resize edge snapping, release beyond 10px, per-axis independence, and collision-invalid snap rejection.
2. Run `dotnet run --project tests/DeskNest.Tests -c Release` and confirm failure.
3. Implement `ZoneAlignmentResolver.Snap` returning bounds plus optional guide coordinates.
4. Validate snapped X and Y independently with `ZoneCollisionResolver.IsAvailable` so the 12px gap remains authoritative.
5. Run tests and confirm all pass.

### Task 2: Preserve raw resize intent and expose guides

**Files:**
- Modify: `src/DeskNest.App/Controls/ZoneCard.xaml`
- Modify: `src/DeskNest.App/Controls/ZoneCard.xaml.cs`

1. Add `DragStarted` and `DragCompleted` handlers to all resize thumbs.
2. Capture resize-start bounds and cumulative pointer deltas so a snapped edge releases after raw movement exceeds 10px.
3. Change the bounds constraint callback to return snap metadata.
4. Raise a guide update callback during drag/resize and clear it on completion.

### Task 3: Render alignment guides

**Files:**
- Modify: `src/DeskNest.App/MainWindow.xaml`
- Modify: `src/DeskNest.App/MainWindow.xaml.cs`

1. Add a hit-test-transparent overlay Canvas above zone cards.
2. Add one vertical and one horizontal 1px dashed mint guide.
3. Run collision constraint first, then edge snapping with a 10px threshold.
4. Show only guides for axes that actually snapped; hide guides when interaction ends.
5. Apply light/dark theme-aware guide opacity.

### Task 4: Verify and publish

1. Run all regression tests.
2. Build Release with zero warnings/errors.
3. Manually verify move snapping, resize snapping, independent axes, guide clearing, and 12px collision behavior.
4. Run `scripts/build-release.ps1` and restart `dist/DeskNest-win-x64/DeskNest.App.exe`.
