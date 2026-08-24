using DeskNest.Core.Models;

namespace DeskNest.Core.Services;

public sealed class DesktopScanner(RuleClassifier classifier)
{
    public IReadOnlyList<DesktopItem> ScanDefaultDesktops()
    {
        var paths = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };

        return Scan(paths);
    }

    public IReadOnlyList<DesktopItem> Scan(IEnumerable<string> directories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DesktopItem>();

        foreach (var directory in directories.Where(Directory.Exists))
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (!ShouldInclude(entry))
                {
                    continue;
                }

                var fullPath = Path.GetFullPath(entry);
                if (!seen.Add(fullPath))
                {
                    continue;
                }

                result.Add(DesktopItem.FromPath(fullPath, classifier.Classify(fullPath)));
            }
        }

        return result
            .OrderBy(item => item.CategoryKey, StringComparer.Ordinal)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static bool ShouldInclude(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return !attributes.HasFlag(FileAttributes.Hidden) &&
                   !attributes.HasFlag(FileAttributes.System) &&
                   !string.Equals(Path.GetFileName(path), "desktop.ini", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
