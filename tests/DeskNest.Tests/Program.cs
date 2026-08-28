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
