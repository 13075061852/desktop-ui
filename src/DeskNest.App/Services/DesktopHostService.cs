using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskNest.App.Interop;

namespace DeskNest.App.Services;

internal sealed class DesktopHostService
{
    private nint _windowHandle;
    private Window? _window;

    public bool IsEmbedded { get; private set; }

    public bool TryEmbed(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _windowHandle = new WindowInteropHelper(window).Handle;
        if (_windowHandle == 0)
        {
            return false;
        }

        ApplyBackgroundWindowStyle();

        // Keep the overlay as an independent bottom-most window. Parenting a transparent WPF
        // window to Explorer's DefView/WorkerW can leave a visible but non-interactive ghost when
        // Explorer rebuilds its desktop hierarchy after wallpaper or Show Desktop changes.
        PlaceAtBottom();
        return false;
    }

    public void ResizeToVirtualScreen()
    {
        if (_windowHandle == 0 || _window is null)
        {
            return;
        }

        // SystemParameters reports device-independent WPF units. Passing those values to
        // SetWindowPos (which expects physical pixels) shrinks the overlay on scaled displays.
        // Let WPF perform the DPI conversion, then only adjust the native z-order below.
        _window.Left = IsEmbedded ? 0 : SystemParameters.VirtualScreenLeft;
        _window.Top = IsEmbedded ? 0 : SystemParameters.VirtualScreenTop;
        _window.Width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        _window.Height = Math.Max(1, SystemParameters.VirtualScreenHeight);
        PlaceAtBottom();
    }

    public bool Reattach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        if (_windowHandle == 0)
        {
            _windowHandle = new WindowInteropHelper(window).Handle;
        }

        PlaceAtBottom();
        return false;
    }

    private void ApplyBackgroundWindowStyle()
    {
        var extendedStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle).ToInt64();
        var backgroundStyle = extendedStyle | NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle, new nint(backgroundStyle));
    }

    private void PlaceAtBottom()
    {
        IsEmbedded = false;
        ApplyBackgroundWindowStyle();
        NativeMethods.SetWindowPos(
            _windowHandle,
            NativeMethods.HwndBottom,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize |
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

}
