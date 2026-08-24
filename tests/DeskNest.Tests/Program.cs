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
