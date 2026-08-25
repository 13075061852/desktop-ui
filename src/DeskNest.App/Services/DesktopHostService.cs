using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskNest.App.Interop;

namespace DeskNest.App.Services;

internal sealed class DesktopHostService
{
    private nint _windowHandle;

    public bool IsEmbedded { get; private set; }

    public bool TryEmbed(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _windowHandle = new WindowInteropHelper(window).Handle;
        if (_windowHandle == 0)
        {
            return false;
        }

        // Keep the overlay as an independent bottom-most window. Parenting a transparent WPF
        // window to Explorer's DefView/WorkerW can leave a visible but non-interactive ghost when
        // Explorer rebuilds its desktop hierarchy after wallpaper or Show Desktop changes.
        PlaceAtBottom();
        return false;
    }

    public void ResizeToVirtualScreen()
    {
        if (_windowHandle == 0)
        {
            return;
        }

        var x = IsEmbedded ? 0 : (int)SystemParameters.VirtualScreenLeft;
        var y = IsEmbedded ? 0 : (int)SystemParameters.VirtualScreenTop;
        NativeMethods.SetWindowPos(
            _windowHandle,
            IsEmbedded ? 0 : NativeMethods.HwndBottom,
            x,
            y,
            Math.Max(1, (int)SystemParameters.VirtualScreenWidth),
            Math.Max(1, (int)SystemParameters.VirtualScreenHeight),
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    public bool Reattach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_windowHandle == 0)
        {
            _windowHandle = new WindowInteropHelper(window).Handle;
        }

        PlaceAtBottom();
        return false;
    }

    private void PlaceAtBottom()
    {
        IsEmbedded = false;
        NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndBottom,
            (int)SystemParameters.VirtualScreenLeft,
            (int)SystemParameters.VirtualScreenTop,
            (int)SystemParameters.VirtualScreenWidth,
            (int)SystemParameters.VirtualScreenHeight,
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

}
