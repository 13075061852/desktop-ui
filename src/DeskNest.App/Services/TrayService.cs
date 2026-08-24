using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using DeskNest.App.Interop;

namespace DeskNest.App.Services;

internal sealed class TrayService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint CallbackMessage = NativeMethods.WmApp + 37;

    private readonly Action _show;
    private readonly Action _organize;
    private readonly Action _toggleIcons;
    private readonly Action _exit;
    private readonly HwndSource _messageWindow;
    private NativeMethods.NotifyIconData _iconData;
    private bool _disposed;

    public TrayService(Action show, Action organize, Action toggleIcons, Action exit)
    {
        _show = show;
        _organize = organize;
        _toggleIcons = toggleIcons;
        _exit = exit;

        var parameters = new HwndSourceParameters("DeskNest.TrayMessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new nint(-3)
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WindowProcedure);

        _iconData = new NativeMethods.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            Window = _messageWindow.Handle,
            Id = TrayIconId,
            Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
            CallbackMessage = CallbackMessage,
            Icon = NativeMethods.LoadIcon(0, new nint(32512)),
            Tip = "栖格 · DeskNest",
            Info = string.Empty,
            InfoTitle = string.Empty
        };

        NativeMethods.Shell_NotifyIcon(NativeMethods.NimAdd, ref _iconData);
        _iconData.TimeoutOrVersion = NativeMethods.NotifyIconVersion4;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimSetVersion, ref _iconData);
    }

    public void ShowMessage(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        _iconData.Flags = NativeMethods.NifInfo;
        _iconData.InfoTitle = title;
        _iconData.Info = message;
        _iconData.InfoFlags = NativeMethods.NiifInfo;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimModify, ref _iconData);
        _iconData.Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip;
    }

    private nint WindowProcedure(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != CallbackMessage)
        {
            return 0;
        }

        var mouseMessage = unchecked((int)(lParam.ToInt64() & 0xFFFF));
        if (mouseMessage == NativeMethods.WmLeftButtonDoubleClick)
        {
            _show();
            handled = true;
        }
        else if (mouseMessage == NativeMethods.WmRightButtonUp)
        {
            ShowContextMenu();
            handled = true;
        }

        return 0;
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };
        menu.Items.Add(CreateMenuItem("显示栖格", _show));
        menu.Items.Add(CreateMenuItem("一键整理", _organize));
        menu.Items.Add(CreateMenuItem("显示/隐藏原图标", _toggleIcons));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("退出", _exit));
        menu.IsOpen = true;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref _iconData);
        _messageWindow.RemoveHook(WindowProcedure);
        _messageWindow.Dispose();
    }
}
