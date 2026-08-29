using DeskNest.Core.Models;
using DeskNest.Core.Services;

var tests = new List<(string Name, Action Run)>
{
    ("default state creates five stable zones", () =>
    {
        var state = AppState.CreateDefault();
        Assert.Equal(true, state.LaunchAtStartup);
        Assert.Equal(5, state.Zones.Count);
        Assert.SequenceEqual(
            new[] { "apps", "documents", "images", "folders", "other" },
            state.Zones.Select(zone => zone.CategoryKey));
    }),
    ("classifier recognizes common desktop types", () =>
    {
        var classifier = new RuleClassifier();
        Assert.Equal("apps", classifier.Classify(@"C:\Desktop\Editor.LNK"));
        Assert.Equal("documents", classifier.Classify(@"C:\Desktop\报价单.PDF"));
        Assert.Equal("images", classifier.Classify(@"C:\Desktop\海报.PsD"));
        Assert.Equal("other", classifier.Classify(@"C:\Desktop\archive.zip"));
    }),
    ("classifier recognizes folders", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskNestTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal("folders", new RuleClassifier().Classify(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }),
    ("shell desktop items remain valid without a file-system path", () =>
    {
        var recycleBin = DesktopItem.FromShellLocation(
            DesktopItem.RecycleBinShellPath,
            "回收站");

        Assert.Equal(true, recycleBin.Exists);
        Assert.Equal("回收站", recycleBin.DisplayName);
    }),
    ("desktop scanner includes every visible direct desktop item", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskNestTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var visibleFile = Path.Combine(root, "visible.txt");
        var visibleFolder = Path.Combine(root, "visible-folder");
        var hiddenFile = Path.Combine(root, "hidden.txt");
        File.WriteAllText(visibleFile, string.Empty);
        Directory.CreateDirectory(visibleFolder);
        File.WriteAllText(hiddenFile, string.Empty);
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        try
        {
            var items = new DesktopScanner(new RuleClassifier()).Scan([root]);
            Assert.SequenceEqual(
                new[] { visibleFile, visibleFolder }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
                items.Select(item => item.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            File.SetAttributes(hiddenFile, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }),
    ("item ordering repositions an item inside the same zone", () =>
    {
        var first = new DesktopItem { DisplayName = "A" };
        var second = new DesktopItem { DisplayName = "B" };
        var third = new DesktopItem { DisplayName = "C" };
        var items = new List<DesktopItem> { first, second, third };

        Assert.Equal(true, ZoneItemOrderService.Move(items, items, first.Id, 3));
        Assert.SequenceEqual(new[] { second.Id, third.Id, first.Id }, items.Select(item => item.Id));
    }),
    ("item ordering inserts an item at a target-zone position", () =>
    {
        var moved = new DesktopItem { DisplayName = "Moved" };
        var before = new DesktopItem { DisplayName = "Before" };
        var after = new DesktopItem { DisplayName = "After" };
        var source = new List<DesktopItem> { moved };
        var target = new List<DesktopItem> { before, after };

        Assert.Equal(true, ZoneItemOrderService.Move(source, target, moved.Id, 1));
        Assert.Equal(0, source.Count);
        Assert.SequenceEqual(new[] { before.Id, moved.Id, after.Id }, target.Select(item => item.Id));
    }),
    ("zone collision keeps a twelve pixel gap while moving", () =>
    {
        var current = new ZoneBounds(0, 72, 200, 200);
        var obstacle = new ZoneBounds(300, 72, 200, 200);
        var result = ZoneCollisionResolver.Constrain(
            current,
            new ZoneBounds(520, 72, 200, 200),
            new[] { obstacle },
            12,
            0,
            72,
            1200,
            900);

        Assert.Near(88, result.X, 0.1);
    }),
    ("zone collision recovers an oversized zone to the top boundary", () =>
    {
        var current = new ZoneBounds(0, 220, 200, 900);
        var result = ZoneCollisionResolver.Constrain(
            current,
            current with { Y = 0 },
            Array.Empty<ZoneBounds>(),
            12,
            0,
            72,
            1200,
            900);

        Assert.Near(72, result.Y, 0.1);
    }),
    ("zone collision lets vertical movement continue when horizontal movement is blocked", () =>
    {
        var current = new ZoneBounds(0, 72, 200, 200);
        var obstacle = new ZoneBounds(300, 72, 200, 200);
        var result = ZoneCollisionResolver.Constrain(
            current,
            new ZoneBounds(520, 150, 200, 200),
            new[] { obstacle },
            12,
            0,
            72,
            1200,
            900);

        Assert.Near(88, result.X, 0.1);
        Assert.Near(150, result.Y, 0.1);
    }),
    ("zone collision lets horizontal movement continue when vertical movement is blocked", () =>
    {
        var current = new ZoneBounds(0, 72, 200, 200);
        var obstacle = new ZoneBounds(0, 350, 200, 200);
        var result = ZoneCollisionResolver.Constrain(
            current,
            new ZoneBounds(100, 600, 200, 200),
            new[] { obstacle },
            12,
            0,
            72,
            1200,
            900);

        Assert.Near(100, result.X, 0.1);
        Assert.Near(138, result.Y, 0.1);
    }),
    ("zone collision resumes blocked movement after sliding past an obstacle", () =>
    {
        var current = new ZoneBounds(0, 72, 200, 200);
        var obstacle = new ZoneBounds(300, 72, 200, 200);
        var result = ZoneCollisionResolver.Constrain(
            current,
            new ZoneBounds(520, 300, 200, 200),
            new[] { obstacle },
            12,
            0,
            72,
            1200,
            900);

        Assert.Near(520, result.X, 0.1);
        Assert.Near(300, result.Y, 0.1);
    }),
    ("zone collision limits resizing before another zone", () =>
    {
        var current = new ZoneBounds(0, 72, 200, 200);
        var obstacle = new ZoneBounds(300, 72, 200, 200);
        var result = ZoneCollisionResolver.Constrain(
            current,
            new ZoneBounds(0, 72, 500, 200),
            new[] { obstacle },
            12,
            0,
            72,
            1200,
            900);

        Assert.Near(288, result.Width, 0.1);
    }),
    ("zone collision preserves visible left and right desktop insets", () =>
    {
        var current = new ZoneBounds(100, 72, 200, 200);
        var left = ZoneCollisionResolver.Constrain(
            current, current with { X = 0 }, Array.Empty<ZoneBounds>(), 12, 12, 72, 1188, 900);
        var right = ZoneCollisionResolver.Constrain(
            current, current with { X = 1100 }, Array.Empty<ZoneBounds>(), 12, 12, 72, 1188, 900);

        Assert.Near(12, left.X, 0.1);
        Assert.Near(988, right.X, 0.1);
    }),
    ("stack resize compresses an adjacent lower zone while preserving its bottom", () =>
    {
        var current = new ZoneBounds(10, 72, 250, 300);
        var lower = new ZoneBounds(10, 384, 250, 400);
        var result = ZoneStackResizeResolver.CompressAdjacentBelow(
            current,
            current with { Height = 380 },
            new[] { lower },
            gap: 12,
            minimumHeight: 150);

        Assert.Near(452, result.Desired.Bottom, 0.1);
        Assert.Near(464, result.Obstacles[0].Y, 0.1);
        Assert.Near(320, result.Obstacles[0].Height, 0.1);
        Assert.Near(784, result.Obstacles[0].Bottom, 0.1);
    }),
    ("stack resize stops when the lower zone reaches minimum height", () =>
    {
        var current = new ZoneBounds(10, 72, 250, 300);
        var lower = new ZoneBounds(10, 384, 250, 200);
        var result = ZoneStackResizeResolver.CompressAdjacentBelow(
            current,
            current with { Height = 500 },
            new[] { lower },
            gap: 12,
            minimumHeight: 150);

        Assert.Near(422, result.Desired.Bottom, 0.1);
        Assert.Near(434, result.Obstacles[0].Y, 0.1);
        Assert.Near(150, result.Obstacles[0].Height, 0.1);
    }),
    ("stack resize ignores zones that are not directly below", () =>
    {
        var current = new ZoneBounds(10, 72, 250, 300);
        var lower = new ZoneBounds(400, 384, 250, 300);
        var desired = current with { Height = 420 };
        var result = ZoneStackResizeResolver.CompressAdjacentBelow(
            current, desired, new[] { lower }, 12, 150);

        Assert.Equal(desired, result.Desired);
        Assert.Equal(lower, result.Obstacles[0]);
    }),
    ("adjacent resize compresses a zone on the right", () =>
    {
        var current = new ZoneBounds(10, 72, 250, 300);
        var right = new ZoneBounds(272, 72, 400, 300);
        var result = ZoneStackResizeResolver.CompressAdjacent(
            current, current with { Width = 350 }, new[] { right }, 12, 200, 150);

        Assert.Near(360, result.Desired.Right, 0.1);
        Assert.Near(372, result.Obstacles[0].X, 0.1);
        Assert.Near(300, result.Obstacles[0].Width, 0.1);
        Assert.Near(672, result.Obstacles[0].Right, 0.1);
    }),
    ("adjacent resize compresses a zone on the left", () =>
    {
        var current = new ZoneBounds(400, 72, 250, 300);
        var left = new ZoneBounds(50, 72, 338, 300);
        var result = ZoneStackResizeResolver.CompressAdjacent(
            current, new ZoneBounds(300, 72, 350, 300), new[] { left }, 12, 200, 150);

        Assert.Near(300, result.Desired.X, 0.1);
        Assert.Near(238, result.Obstacles[0].Width, 0.1);
        Assert.Near(288, result.Obstacles[0].Right, 0.1);
    }),
    ("adjacent resize compresses a zone above", () =>
    {
        var current = new ZoneBounds(10, 400, 250, 250);
        var above = new ZoneBounds(10, 72, 250, 316);
        var result = ZoneStackResizeResolver.CompressAdjacent(
            current, new ZoneBounds(10, 300, 250, 350), new[] { above }, 12, 200, 150);

        Assert.Near(300, result.Desired.Y, 0.1);
        Assert.Near(216, result.Obstacles[0].Height, 0.1);
        Assert.Near(288, result.Obstacles[0].Bottom, 0.1);
    }),
    ("adjacent reflow moves a right zone before changing its width", () =>
    {
        var current = new ZoneBounds(10, 72, 250, 300);
        var right = new ZoneBounds(272, 72, 300, 300);
        var result = ZoneStackResizeResolver.ReflowAdjacent(
            current, current with { Width = 350 }, new[] { right }, 12, 200, 150,
            0, 72, 900, 900);

        Assert.Near(372, result.Obstacles[0].X, 0.1);
        Assert.Near(300, result.Obstacles[0].Width, 0.1);
        Assert.Near(360, result.Desired.Right, 0.1);
    }),
    ("adjacent reflow uses free space first and then compresses", () =>
    {
        var current = new ZoneBounds(10, 72, 250, 300);
        var right = new ZoneBounds(272, 72, 300, 300);
        var result = ZoneStackResizeResolver.ReflowAdjacent(
            current, current with { Width = 350 }, new[] { right }, 12, 200, 150,
            0, 72, 622, 900);

        Assert.Near(372, result.Obstacles[0].X, 0.1);
        Assert.Near(250, result.Obstacles[0].Width, 0.1);
        Assert.Near(622, result.Obstacles[0].Right, 0.1);
        Assert.Near(360, result.Desired.Right, 0.1);
    }),
    ("adjacent reflow moves a connected row as one group", () =>
    {
        var current = new ZoneBounds(10, 72, 250, 300);
        var first = new ZoneBounds(272, 72, 220, 300);
        var second = new ZoneBounds(504, 72, 220, 300);
        var result = ZoneStackResizeResolver.ReflowAdjacent(
            current, current with { Width = 310 }, new[] { first, second }, 12, 200, 150,
            0, 72, 900, 900);

        Assert.Near(332, result.Obstacles[0].X, 0.1);
        Assert.Near(564, result.Obstacles[1].X, 0.1);
        Assert.Near(220, result.Obstacles[0].Width, 0.1);
        Assert.Near(220, result.Obstacles[1].Width, 0.1);
    }),
    ("expansion sweep carries swept-over neighbours instead of covering them", () =>
    {
        // Mirror of a real session: stretching the left zone rightwards while
        // its edge already sits over the neighbour's left half.
        var current = new ZoneBounds(13, 72, 339, 727);
        var folder = new ZoneBounds(267, 293, 238, 507);
        var remote = new ZoneBounds(373, 72, 150, 222);
        var desired = current with { Width = 487 };
        var result = ZoneStackResizeResolver.ReflowAdjacent(
            current, desired, new[] { folder, remote }, 12, 150, 150, 0, 72, 1200, 900);

        // Both zones in the sweep path ride ahead of the target edge by the
        // standard gap instead of being covered.
        Assert.Near(512, result.Obstacles[0].X, 0.1);
        Assert.Near(618, result.Obstacles[1].X, 0.1);
        Assert.Near(500, result.Desired.Right, 0.1);
    }),
    ("zone alignment snaps moving edges within ten pixels", () =>
    {
        var current = new ZoneBounds(20, 400, 200, 200);
        var desired = new ZoneBounds(94, 400, 200, 200);
        var obstacle = new ZoneBounds(100, 72, 200, 200);
        var result = ZoneAlignmentResolver.Snap(
            current, desired, desired, new[] { obstacle }, 10, 12, 0, 72, 1200, 900);

        Assert.Near(100, result.Bounds.X, 0.1);
        Assert.Near(100, result.VerticalGuide ?? -1, 0.1);
    }),
    ("zone alignment releases after moving beyond ten pixels", () =>
    {
        var current = new ZoneBounds(20, 400, 200, 200);
        var desired = new ZoneBounds(111, 400, 200, 200);
        var obstacle = new ZoneBounds(100, 72, 200, 200);
        var result = ZoneAlignmentResolver.Snap(
            current, desired, desired, new[] { obstacle }, 10, 12, 0, 72, 1200, 900);

        Assert.Near(111, result.Bounds.X, 0.1);
        Assert.Equal<double?>(null, result.VerticalGuide);
    }),
    ("zone alignment snaps horizontal and vertical axes independently", () =>
    {
        var current = new ZoneBounds(20, 400, 200, 150);
        var desired = new ZoneBounds(94, 294, 200, 150);
        var obstacles = new[]
        {
            new ZoneBounds(100, 72, 200, 200),
            new ZoneBounds(500, 100, 200, 200)
        };
        var result = ZoneAlignmentResolver.Snap(
            current, desired, desired, obstacles, 10, 12, 0, 72, 1200, 900);

        Assert.Near(100, result.Bounds.X, 0.1);
        Assert.Near(300, result.Bounds.Y, 0.1);
        Assert.Near(100, result.VerticalGuide ?? -1, 0.1);
        Assert.Near(300, result.HorizontalGuide ?? -1, 0.1);
    }),
    ("zone alignment snaps a resized edge", () =>
    {
        var current = new ZoneBounds(100, 72, 200, 200);
        var desired = new ZoneBounds(100, 72, 395, 200);
        var obstacle = new ZoneBounds(500, 400, 200, 200);
        var result = ZoneAlignmentResolver.Snap(
            current, desired, desired, new[] { obstacle }, 10, 12, 0, 72, 1200, 900);

        Assert.Near(400, result.Bounds.Width, 0.1);
        Assert.Near(500, result.VerticalGuide ?? -1, 0.1);
    }),
    ("zone alignment rejects a snap that violates collision spacing", () =>
    {
        var current = new ZoneBounds(0, 72, 200, 200);
        var desired = new ZoneBounds(94, 72, 200, 200);
        var obstacle = new ZoneBounds(300, 72, 200, 200);
        var collisionSafe = new ZoneBounds(88, 72, 200, 200);
        var result = ZoneAlignmentResolver.Snap(
            current, desired, collisionSafe, new[] { obstacle }, 10, 12, 0, 72, 1200, 900);

        Assert.Near(88, result.Bounds.X, 0.1);
        Assert.Equal<double?>(null, result.VerticalGuide);
    }),
    ("zone alignment keeps a sticky guide within the escape distance", () =>
    {
        var current = new ZoneBounds(20, 400, 200, 200);
        var desired = new ZoneBounds(112, 400, 200, 200);
        var obstacles = new[]
        {
            new ZoneBounds(100, 72, 200, 200),
            new ZoneBounds(320, 72, 200, 200)
        };
        var hysteresis = new ZoneSnapHysteresis { HeldVertical = 100 };
        var result = ZoneAlignmentResolver.Snap(
            current, desired, desired, obstacles, 10, 12, 0, 72, 1200, 900,
            hysteresis, 16);

        // 12px past the held guide (outside the 10px snap threshold) and with
        // a closer alternative guide, the held guide still wins.
        Assert.Near(100, result.Bounds.X, 0.1);
        Assert.Near(100, result.VerticalGuide ?? -1, 0.1);
        Assert.Near(100, hysteresis.HeldVertical ?? -1, 0.1);
    }),
    ("zone alignment releases past the escape distance and re-engages on return", () =>
    {
        var current = new ZoneBounds(20, 400, 200, 200);
        var obstacle = new ZoneBounds(100, 72, 200, 200);
        var hysteresis = new ZoneSnapHysteresis { HeldVertical = 100 };

        // Pointer escapes 28px: the edge lets go and follows the pointer.
        var escaped = new ZoneBounds(130, 400, 200, 200);
        var released = ZoneAlignmentResolver.Snap(
            current, escaped, escaped, new[] { obstacle }, 10, 12, 0, 72, 1200, 900,
            hysteresis, 28);
        Assert.Near(130, released.Bounds.X, 0.1);
        Assert.Equal<double?>(null, released.VerticalGuide);
        Assert.Equal(false, hysteresis.HeldVertical.HasValue);

        // Returning within the snap threshold deliberately re-engages.
        var returning = new ZoneBounds(94, 400, 200, 200);
        var reengaged = ZoneAlignmentResolver.Snap(
            current, returning, returning, new[] { obstacle }, 10, 12, 0, 72, 1200, 900,
            hysteresis, 28);
        Assert.Near(100, reengaged.Bounds.X, 0.1);
        Assert.Near(100, reengaged.VerticalGuide ?? -1, 0.1);
    }),
    ("sticky guide near a stationary edge does not block the moving edge", () =>
    {
        // Resizing the right edge; the held guide sits exactly on the left
        // (stationary) edge and must not swallow the active-edge snap.
        var current = new ZoneBounds(100, 72, 200, 200);
        var desired = new ZoneBounds(100, 72, 295, 200);
        var obstacles = new[]
        {
            new ZoneBounds(400, 400, 200, 200),
            new ZoneBounds(100, 400, 200, 200)
        };
        var hysteresis = new ZoneSnapHysteresis { HeldVertical = 100 };
        var result = ZoneAlignmentResolver.Snap(
            current, desired, desired, obstacles, 10, 12, 0, 72, 1200, 900,
            hysteresis, 16);

        Assert.Near(400, result.Bounds.Right, 0.1);
        Assert.Near(300, result.Bounds.Width, 0.1);
        Assert.Near(400, result.VerticalGuide ?? -1, 0.1);
    }),
    ("splitter rail for a stacked pair follows the pointer upward", () =>
    {
        // Stacked pair: A on top, B below. The horizontal rail travels on the
        // Y axis; comparing the desired Y against an X coordinate used to make
        // the line jump to a bogus position and throw the top zone off-screen.
        var first = new ZoneBounds(100, 72, 300, 300);
        var second = new ZoneBounds(100, 384, 300, 300);
        var lineMid = ZoneSplitterResolver.ResolveRailPosition(
            first, second, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            desiredMid: 328, movingForward: false, 12, 72, 1200, 800);

        Assert.Near(328, lineMid, 0.1);
    }),
    ("splitter layout validates stacked pairs against the correct axis limits", () =>
    {
        // A pair wider than the canvas height used to be compared against the
        // Y-axis limit, making every horizontal rail position invalid.
        var first = new ZoneBounds(12, 72, 700, 300);
        var second = new ZoneBounds(12, 384, 700, 300);
        var ok = ZoneSplitterResolver.TryLayout(
            first, second, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            lineMid: 478, movingForward: true, 12, 72, 1200, 800, out var layout);

        Assert.Equal(true, ok);
        Assert.Near(472, layout.First.Bottom, 0.1);
        Assert.Near(484, layout.Second.Y, 0.1);
        Assert.Near(684, layout.Second.Bottom, 0.1);
    }),
    ("splitter rail pushes a bystander below a stacked pair", () =>
    {
        // Dragging the horizontal rail down grows the lower zone; the flush
        // bystander below rides ahead keeping the 12px gap.
        var first = new ZoneBounds(100, 72, 300, 300);
        var second = new ZoneBounds(100, 384, 300, 200);
        var bystander = new ZoneBounds(100, 596, 300, 150);
        var lineMid = ZoneSplitterResolver.ResolveRailPosition(
            first, second, new[] { bystander }, 12, isHorizontalLine: true,
            desiredMid: 478, movingForward: true, 12, 72, 1200, 800);

        // The line advances until the growing edge reaches the bystander;
        // further travel is possible only with the bystander pushed ahead.
        var ok = ZoneSplitterResolver.TryLayout(
            first, second, new[] { bystander }, 12, isHorizontalLine: true,
            lineMid, movingForward: true, 12, 72, 1200, 800, out var layout);

        Assert.Equal(true, ok);
        Assert.Near(lineMid + 168, layout.Bystanders[0].Y, 0.6);
        Assert.Near(lineMid + 156, layout.Second.Bottom, 0.6);
    }),
    ("splitter rail keeps side-by-side pairs on the X axis", () =>
    {
        var first = new ZoneBounds(12, 72, 300, 300);
        var second = new ZoneBounds(312, 72, 300, 300);
        var lineMid = ZoneSplitterResolver.ResolveRailPosition(
            first, second, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: false,
            desiredMid: 366, movingForward: true, 12, 72, 1200, 800);

        Assert.Near(366, lineMid, 0.1);
    }),
    ("splitter rail pushes a bystander right of a side-by-side pair", () =>
    {
        var first = new ZoneBounds(12, 72, 300, 300);
        var second = new ZoneBounds(312, 72, 200, 300);
        var bystander = new ZoneBounds(524, 72, 200, 300);
        var lineMid = ZoneSplitterResolver.ResolveRailPosition(
            first, second, new[] { bystander }, 12, isHorizontalLine: false,
            desiredMid: 406, movingForward: true, 12, 72, 1200, 800);
        var ok = ZoneSplitterResolver.TryLayout(
            first, second, new[] { bystander }, 12, isHorizontalLine: false,
            lineMid, movingForward: true, 12, 72, 1200, 800, out var layout);

        Assert.Equal(true, ok);
        Assert.Near(lineMid + 168, layout.Bystanders[0].X, 0.6);
    }),
    ("layout relaxer restores a pushed neighbour once space frees up", () =>
    {
        // The exact pipeline ConstrainZoneBounds runs: expand pushes the
        // neighbour aside, shrinking back must let it walk home again.
        var current = new ZoneBounds(10, 72, 250, 300);
        var right = new ZoneBounds(272, 72, 300, 300);
        var rest = right;
        var expanded = ZoneStackResizeResolver.ReflowAdjacent(
            current, current with { Width = 350 }, new[] { right }, 12, 150, 150,
            0, 72, 900, 900);
        var pushed = expanded.Obstacles[0];
        var relaxed = ZoneLayoutRelaxer.Relax(
            new[] { pushed }, new[] { rest }, current with { Width = 250 },
            12, 0, 72, 900, 900);

        Assert.Equal(rest, relaxed[0]);
    }),
    ("layout relaxer keeps a neighbour yielded while the space is occupied", () =>
    {
        var rest = new ZoneBounds(272, 72, 300, 300);
        var pushed = new ZoneBounds(372, 72, 300, 300);
        var expandedZone = new ZoneBounds(10, 72, 350, 300);
        var relaxed = ZoneLayoutRelaxer.Relax(
            new[] { pushed }, new[] { rest }, expandedZone,
            12, 0, 72, 900, 900);

        Assert.Equal(pushed, relaxed[0]);
    }),
    ("layout relaxer recovers a cascade once the leading zone steps home", () =>
    {
        // Two zones pushed by the same expansion: freeing space lets both
        // walk home in order, one pass at a time.
        var firstRest = new ZoneBounds(272, 72, 220, 300);
        var secondRest = new ZoneBounds(504, 72, 220, 300);
        var shrunkZone = new ZoneBounds(10, 72, 250, 300);
        var relaxed = ZoneLayoutRelaxer.Relax(
            new[] { new ZoneBounds(372, 72, 220, 300), new ZoneBounds(604, 72, 220, 300) },
            new[] { firstRest, secondRest },
            shrunkZone,
            12, 0, 72, 900, 900);

        Assert.Equal(firstRest, relaxed[0]);
        Assert.Equal(secondRest, relaxed[1]);
    }),
    ("layout relaxer ignores zones without rest bounds", () =>
    {
        var current = new ZoneBounds(372, 72, 220, 300);
        var relaxed = ZoneLayoutRelaxer.Relax(
            new[] { current }, new[] { ZoneBounds.Empty }, new ZoneBounds(10, 72, 250, 300),
            12, 0, 72, 900, 900);

        Assert.Equal(current, relaxed[0]);
    }),
    ("splitter group drags two top zones above one wide bottom zone", () =>
    {
        // 1拖2: two side-by-side zones sit flush above one wide zone; the
        // collinear rail must shrink BOTH top zones in lockstep instead of
        // jamming against the untouched sibling.
        var firsts = new[] { new ZoneBounds(12, 72, 300, 373), new ZoneBounds(324, 72, 300, 373) };
        var seconds = new[] { new ZoneBounds(12, 457, 612, 343) };
        var lineMid = ZoneSplitterResolver.ResolveGroupRailPosition(
            firsts, seconds, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            desiredMid: 420, movingForward: false, 12, 72, 1200, 800);
        var ok = ZoneSplitterResolver.TryGroupLayout(
            firsts, seconds, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            lineMid, movingForward: false, 12, 72, 1200, 800, out var layout);

        Assert.Equal(true, ok);
        Assert.Near(420, lineMid, 0.5);
        Assert.Near(414, layout.Firsts[0].Bottom, 0.1);
        Assert.Near(414, layout.Firsts[1].Bottom, 0.1);
        Assert.Near(426, layout.Seconds[0].Y, 0.1);
    }),
    ("splitter group keeps unrelated aligned rows still", () =>
    {
        // A zone in another row whose edge merely aligns with the line is a
        // bystander outside the band: the group layout must not move it.
        var firsts = new[] { new ZoneBounds(12, 72, 300, 300) };
        var seconds = new[] { new ZoneBounds(12, 384, 300, 300) };
        var bystander = new ZoneBounds(12, 700, 300, 150);
        var ok = ZoneSplitterResolver.TryGroupLayout(
            firsts, seconds, new[] { bystander }, 12, isHorizontalLine: true,
            lineMid: 378, movingForward: true, 12, 72, 1200, 800, out var layout);

        Assert.Equal(true, ok);
        Assert.Equal(bystander, layout.Bystanders[0]);
    }),
    ("splitter identity layout stays valid for an expand-clamped zone", () =>
    {
        // A zone clamped below the 150 minimum (expand limit) sits at the
        // canvas bottom; stretching it to 150 used to invalidate the start
        // layout and freeze the rail completely.
        var firsts = new[] { new ZoneBounds(12, 72, 300, 373) };
        var seconds = new[] { new ZoneBounds(12, 457, 300, 88) };
        var midGapStart = 457 - 6;
        var ok = ZoneSplitterResolver.TryGroupLayout(
            firsts, seconds, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            midGapStart, movingForward: false, 12, 72, 1200, 800, out var identity);

        Assert.Equal(true, ok);
        Assert.Equal(seconds[0], identity.Seconds[0]);
        Assert.Equal(firsts[0], identity.Firsts[0]);

        // Moving up still works: the first shrinks, the small second grows
        // but never past its start footprint unless the line demands it.
        var moved = ZoneSplitterResolver.TryGroupLayout(
            firsts, seconds, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            midGapStart - 30, movingForward: false, 12, 72, 1200, 800, out var layout);
        Assert.Equal(true, moved);
        Assert.Near(415, layout.Firsts[0].Bottom, 0.1);
    }),
    ("splitter identity layout stays valid with a pre-existing tight gap", () =>
    {
        // Startup clamping can leave a pair a pixel under the standard gap;
        // the rail must still drag, and never worsen that pair.
        var firsts = new[] { new ZoneBounds(12, 72, 300, 373) };
        var seconds = new[] { new ZoneBounds(12, 456, 300, 300) }; // 11px gap
        var midGapStart = 456 - 6;
        var ok = ZoneSplitterResolver.TryGroupLayout(
            firsts, seconds, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            midGapStart, movingForward: false, 12, 72, 1200, 800, out _);

        Assert.Equal(true, ok);

        // Moving up opens the gap: valid.
        var up = ZoneSplitterResolver.TryGroupLayout(
            firsts, seconds, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            midGapStart - 20, movingForward: false, 12, 72, 1200, 800, out _);
        Assert.Equal(true, up);
    }),
    ("splitter group preserves per-zone offsets from the line", () =>
    {
        // A second collected within the 3px tolerance must not teleport onto
        // the line's exact geometry: its offset rides along instead.
        var firsts = new[] { new ZoneBounds(12, 72, 300, 300) };
        var seconds = new[] { new ZoneBounds(12, 384, 300, 300), new ZoneBounds(340, 386, 300, 300) };
        var ok = ZoneSplitterResolver.TryGroupLayout(
            firsts, seconds, Array.Empty<ZoneBounds>(), 12, isHorizontalLine: true,
            lineMid: 378, movingForward: false, 12, 72, 1200, 800, out var identity);

        Assert.Equal(true, ok);
        Assert.Near(384, identity.Seconds[0].Y, 0.1);
        Assert.Near(386, identity.Seconds[1].Y, 0.1);
    }),
    ("state store round-trips state", () =>
    {
        WithTempDirectory(root =>
        {
            var path = Path.Combine(root, "state.json");
            var store = new JsonStateStore(path);
            var expected = AppState.CreateDefault();
            expected.LayoutLocked = true;
            expected.WallpaperSelection = "mist-mountains";
            expected.ToolbarAlignment = "Right";
            expected.Zones[0].Name = "高频工具";
            expected.Zones[0].ViewMode = "List";
            expected.Zones[0].Width = 120;

            store.SaveAsync(expected).GetAwaiter().GetResult();
            var actual = store.LoadAsync().GetAwaiter().GetResult();

            Assert.Equal(true, actual.LayoutLocked);
            Assert.Equal("mist-mountains", actual.WallpaperSelection);
            Assert.Equal("Right", actual.ToolbarAlignment);
            Assert.Equal("高频工具", actual.Zones[0].Name);
            Assert.Equal("List", actual.Zones[0].ViewMode);
            Assert.Near(150, actual.Zones[0].Width, 0.1);
        });
    }),
    ("state store recovers malformed primary from backup", () =>
    {
        WithTempDirectory(root =>
        {
            var path = Path.Combine(root, "state.json");
            var store = new JsonStateStore(path);
            var first = AppState.CreateDefault();
            first.Theme = "Dark";
            store.SaveAsync(first).GetAwaiter().GetResult();

            var second = AppState.CreateDefault();
            second.Theme = "Light";
            store.SaveAsync(second).GetAwaiter().GetResult();
            File.WriteAllText(path, "{not-json");

            var recovered = store.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal("Dark", recovered.Theme);
        });
    }),
    ("missing state returns safe defaults", () =>
    {
        WithTempDirectory(root =>
        {
            var store = new JsonStateStore(Path.Combine(root, "missing.json"));
            var state = store.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal(5, state.Zones.Count);
        });
    })
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL  {test.Name}\n      {exception.Message}");
    }
}

Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} tests passed.");
return failed == 0 ? 0 : 1;

static void WithTempDirectory(Action<string> action)
{
    var root = Path.Combine(Path.GetTempPath(), "DeskNestTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        action(root);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

internal static class Assert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException("Sequences are not equal.");
        }
    }

    public static void Near(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException($"Expected '{expected}' ± {tolerance}, received '{actual}'.");
        }
    }
}
