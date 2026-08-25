using System.IO;
using System.Text;
using DeskNest.App.Interop;
using Microsoft.Win32;

namespace DeskNest.App.Services;

internal sealed record WallpaperOption(string Key, string Name, string FileName);

internal sealed class DesktopWallpaperService
{
    private static readonly WallpaperOption[] BuiltInOptions =
    [
        new("mist-mountains", "清晨雾山", "mist-mountains.jpg"),
        new("deep-ocean-glow", "深海流光", "deep-ocean-glow.jpg"),
        new("warm-dunes", "暖色沙丘", "warm-dunes.jpg"),
        new("minimal-geometry", "极简几何", "minimal-geometry.jpg"),
        new("neon-city", "霓虹雨城", "neon-city.jpg"),
        new("botanical-light", "日光植影", "botanical-light.jpg"),
        new("cosmic-aurora", "极光幻境", "cosmic-aurora.jpg"),
        new("ink-landscape", "水墨云山", "ink-landscape.jpg")
    ];

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp"
    };

    public IReadOnlyList<WallpaperOption> GetBuiltInOptions() => BuiltInOptions;

    public string? ResolveSelection(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return null;
        }

        var builtIn = BuiltInOptions.FirstOrDefault(option =>
            string.Equals(option.Key, selection, StringComparison.OrdinalIgnoreCase));
        if (builtIn is not null)
        {
            var builtInPath = Path.Combine(AppContext.BaseDirectory, "Wallpapers", builtIn.FileName);
            return File.Exists(builtInPath) ? builtInPath : null;
        }

        return File.Exists(selection) ? Path.GetFullPath(selection) : null;
    }

    public string GetCurrentWallpaperPath()
    {
        var buffer = new StringBuilder(32768);
        return NativeMethods.SystemParametersInfoGet(
            NativeMethods.SpiGetDesktopWallpaper,
            (uint)buffer.Capacity,
            buffer,
            0)
            ? buffer.ToString()
            : string.Empty;
    }

    public bool ApplySelection(string? selection)
    {
        var path = ResolveSelection(selection);
        return path is not null && ApplyPath(path);
    }

    public bool RestorePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return NativeMethods.SystemParametersInfoSet(
                NativeMethods.SpiSetDesktopWallpaper,
                0,
                string.Empty,
                NativeMethods.SpifUpdateIniFile | NativeMethods.SpifSendChange);
        }

        return ApplyPath(path);
    }

    public bool ApplyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var desktop = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            desktop?.SetValue("WallpaperStyle", "10");
            desktop?.SetValue("TileWallpaper", "0");
        }
        catch (Exception)
        {
            // Wallpaper application can still succeed when style registry access is unavailable.
        }

        return NativeMethods.SystemParametersInfoSet(
            NativeMethods.SpiSetDesktopWallpaper,
            0,
            Path.GetFullPath(path),
            NativeMethods.SpifUpdateIniFile | NativeMethods.SpifSendChange);
    }

    public string Import(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        if (!File.Exists(sourcePath) || !SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException("请选择 JPG、PNG 或 BMP 图片。");
        }

        var targetDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskNest",
            "Wallpapers");
        Directory.CreateDirectory(targetDirectory);

        var targetPath = Path.Combine(
            targetDirectory,
            $"custom-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension.ToLowerInvariant()}");
        File.Copy(sourcePath, targetPath, overwrite: false);
        return targetPath;
    }
}
