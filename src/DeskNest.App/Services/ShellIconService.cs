using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskNest.App.Interop;
using DeskNest.Core.Models;

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

        var source = string.Equals(path, DesktopItem.RecycleBinShellPath, StringComparison.OrdinalIgnoreCase)
            ? TryGetRecycleBinIcon()
            : TryGetSystemImageListIcon(path) ?? TryGetLargeShellIcon(path);
        if (source is not null)
        {
            source.Freeze();
            _cache[path] = source;
        }

        return source;
    }

    private static BitmapSource? TryGetRecycleBinIcon()
    {
        var iconInfo = new NativeMethods.StockIconInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.StockIconInfo>(),
            Path = string.Empty
        };
        if (NativeMethods.SHGetStockIconInfo(
                NativeMethods.SiidRecycler,
                NativeMethods.ShgsiIcon | NativeMethods.ShgsiLargeIcon,
                ref iconInfo) < 0 || iconInfo.IconHandle == 0)
        {
            return null;
        }

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                iconInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            NativeMethods.DestroyIcon(iconInfo.IconHandle);
        }
    }

    private static BitmapSource? TryGetSystemImageListIcon(string path)
    {
        var result = NativeMethods.SHGetFileInfo(
            path,
            0,
            out var fileInfo,
            (uint)Marshal.SizeOf<NativeMethods.ShellFileInfo>(),
            NativeMethods.ShgfiSysIconIndex);
        if (result == 0)
        {
            return null;
        }

        foreach (var imageListSize in new[] { NativeMethods.ShilExtraLarge, NativeMethods.ShilLarge })
        {
            NativeMethods.IImageList? imageList = null;
            nint iconHandle = 0;
            try
            {
                var interfaceId = typeof(NativeMethods.IImageList).GUID;
                if (NativeMethods.SHGetImageList(imageListSize, ref interfaceId, out imageList) < 0 ||
                    imageList.GetIcon(fileInfo.IconIndex, NativeMethods.IldTransparent, out iconHandle) < 0 ||
                    iconHandle == 0)
                {
                    continue;
                }

                return Imaging.CreateBitmapSourceFromHIcon(
                    iconHandle,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            catch (COMException)
            {
                // Older Explorer versions may not expose the requested image-list size.
            }
            finally
            {
                if (iconHandle != 0)
                {
                    NativeMethods.DestroyIcon(iconHandle);
                }

                if (imageList is not null && Marshal.IsComObject(imageList))
                {
                    Marshal.FinalReleaseComObject(imageList);
                }
            }
        }

        return null;
    }

    private static BitmapSource? TryGetLargeShellIcon(string path)
    {
        var result = NativeMethods.SHGetFileInfo(
            path,
            0,
            out var fileInfo,
            (uint)Marshal.SizeOf<NativeMethods.ShellFileInfo>(),
            NativeMethods.ShgfiIcon | NativeMethods.ShgfiLargeIcon);
        if (result == 0 || fileInfo.IconHandle == 0)
        {
            return null;
        }

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                fileInfo.IconHandle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
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
