using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskNest.App.Interop;

namespace DeskNest.App.Services;

internal sealed class ShellIconService
{
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var result = NativeMethods.SHGetFileInfo(
            path,
            0,
            out var fileInfo,
            (uint)Marshal.SizeOf<NativeMethods.ShellFileInfo>(),
            NativeMethods.ShgfiIcon | NativeMethods.ShgfiSmallIcon);

        if (result == 0 || fileInfo.IconHandle == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            _cache[path] = source;
            return source;
        }
        finally
        {
            NativeMethods.DestroyIcon(fileInfo.IconHandle);
        }
    }

    public void Invalidate(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _cache.Clear();
        }
        else
        {
            _cache.Remove(path);
        }
    }
}
