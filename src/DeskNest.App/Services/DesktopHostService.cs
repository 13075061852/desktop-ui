using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskNest.App.Interop;

namespace DeskNest.App.Services;

internal sealed class DesktopHostService
{
    private nint _hostHandle;
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

        _hostHandle = FindDesktopWorker();
        if (_hostHandle == 0)
        {
            PlaceAtBottom();
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlStyle).ToInt64();
        style = (style & ~NativeMethods.WsPopup) | NativeMethods.WsChild;
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GwlStyle, new nint(style));

        var exStyle = NativeMethods.GetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle).ToInt64();
        exStyle |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        NativeMethods.SetWindowLongPtr(_windowHandle, NativeMethods.GwlExStyle, new nint(exStyle));

        NativeMethods.SetParent(_windowHandle, _hostHandle);
        IsEmbedded = NativeMethods.GetParent(_windowHandle) == _hostHandle;
        if (!IsEmbedded)
        {
            PlaceAtBottom();
            return false;
        }

        ResizeToVirtualScreen();
        return true;
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
        if (_hostHandle != 0 && NativeMethods.IsWindow(_hostHandle) &&
            NativeMethods.GetParent(_windowHandle) == _hostHandle)
        {
            return IsEmbedded;
        }

        IsEmbedded = false;
        return TryEmbed(window);
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

    private static nint FindDesktopWorker()
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != 0)
        {
            NativeMethods.SendMessageTimeout(
                progman,
                NativeMethods.WmSpawnWorker,
                0,
                0,
                NativeMethods.SmtoNormal,
                1000,
                out _);
        }

        nint worker = 0;
        NativeMethods.EnumWindows((topLevel, _) =>
        {
            var shellView = NativeMethods.FindWindowEx(topLevel, 0, "SHELLDLL_DefView", null);
            if (shellView == 0)
            {
                return true;
            }

            worker = NativeMethods.FindWindowEx(0, topLevel, "WorkerW", null);
            return worker == 0;
        }, 0);

        return worker != 0 ? worker : progman;
    }
}
