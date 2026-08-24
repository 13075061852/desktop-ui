using System.Text.Json.Serialization;

namespace DeskNest.Core.Models;

public sealed class DesktopItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Path { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string CategoryKey { get; set; } = "other";

    [JsonIgnore]
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    public static DesktopItem FromPath(string path, string categoryKey)
    {
        var normalizedPath = System.IO.Path.GetFullPath(path);
        var name = System.IO.Path.GetFileNameWithoutExtension(normalizedPath);
        if (Directory.Exists(normalizedPath))
        {
            name = new DirectoryInfo(normalizedPath).Name;
        }

        return new DesktopItem
        {
            Path = normalizedPath,
            DisplayName = string.IsNullOrWhiteSpace(name) ? normalizedPath : name,
            CategoryKey = categoryKey
        };
    }
}
