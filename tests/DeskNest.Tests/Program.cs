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
    ("state store round-trips state", () =>
    {
        WithTempDirectory(root =>
        {
            var path = Path.Combine(root, "state.json");
            var store = new JsonStateStore(path);
            var expected = AppState.CreateDefault();
            expected.LayoutLocked = true;
            expected.Zones[0].Name = "高频工具";

            store.SaveAsync(expected).GetAwaiter().GetResult();
            var actual = store.LoadAsync().GetAwaiter().GetResult();

            Assert.Equal(true, actual.LayoutLocked);
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
}
