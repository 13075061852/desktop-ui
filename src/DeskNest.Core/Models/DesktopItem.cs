using System.Text.Json.Serialization;

namespace DeskNest.Core.Models;

public sealed class DesktopItem
{
    public const string RecycleBinShellPath = "shell:RecycleBinFolder";
    public const string ThisPcShellPath = "shell:MyComputerFolder";
    public const string NetworkShellPath = "shell:NetworkPlacesFolder";
    public const string ControlPanelShellPath = "shell:ControlPanelFolder";
    public const string UserFilesShellPath = "shell:Personal";

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Path { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string CategoryKey { get; set; } = "other";

    [JsonIgnore]
    public bool Exists => IsShellLocation(Path) || File.Exists(Path) || Directory.Exists(Path);

    public static bool IsShellLocation(string path) =>
        path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("::", StringComparison.OrdinalIgnoreCase);

    public static DesktopItem FromShellLocation(
        string shellPath,
        string displayName,
        string categoryKey = "other")
    {
        return new DesktopItem
        {
            Path = shellPath,
            DisplayName = displayName,
            CategoryKey = categoryKey
        };
    }

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
