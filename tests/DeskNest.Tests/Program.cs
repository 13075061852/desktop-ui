using DeskNest.Core.Models;
using DeskNest.Core.Services;

var tests = new List<(string Name, Action Run)>
{
    ("default state creates five stable zones", () =>
    {
        var state = AppState.CreateDefault();
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

            store.SaveAsync(expected).GetAwaiter().GetResult();
            var actual = store.LoadAsync().GetAwaiter().GetResult();

            Assert.Equal(true, actual.LayoutLocked);
            Assert.Equal("mist-mountains", actual.WallpaperSelection);
            Assert.Equal("Right", actual.ToolbarAlignment);
            Assert.Equal("高频工具", actual.Zones[0].Name);
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
