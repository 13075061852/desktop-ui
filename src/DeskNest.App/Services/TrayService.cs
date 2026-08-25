using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskNest.App.Interop;

namespace DeskNest.App.Services;

internal sealed class TrayService : IDisposable
{
    private const uint TrayIconId = 1;
    private const uint CallbackMessage = NativeMethods.WmApp + 37;

    private readonly Action _show;
    private readonly Action _organize;
    private readonly Action _toggleDesktopMode;
    private readonly Action _exit;
    private readonly HwndSource _messageWindow;
    private readonly DispatcherTimer _menuDismissTimer;
    private readonly nint _trayIconHandle;
    private readonly bool _ownsTrayIconHandle;
    private NativeMethods.NotifyIconData _iconData;
    private ContextMenu? _contextMenu;
    private nint _contextMenuHandle;
    private bool _disposed;
    private bool _menuPointerArmed;

    public TrayService(Action show, Action organize, Action toggleDesktopMode, Action exit)
    {
        _show = show;
        _organize = organize;
        _toggleDesktopMode = toggleDesktopMode;
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
        _menuDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _menuDismissTimer.Tick += (_, _) => DismissMenuOnOutsidePointerDown();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "DeskNest.ico");
        _trayIconHandle = File.Exists(iconPath)
            ? NativeMethods.LoadImage(
                0,
                iconPath,
                NativeMethods.ImageIcon,
                0,
                0,
                NativeMethods.LrLoadFromFile | NativeMethods.LrDefaultSize)
            : 0;
        _ownsTrayIconHandle = _trayIconHandle != 0;
        if (_trayIconHandle == 0)
        {
            _trayIconHandle = NativeMethods.LoadIcon(0, new nint(32512));
        }

        _iconData = new NativeMethods.NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            Window = _messageWindow.Handle,
            Id = TrayIconId,
            Flags = NativeMethods.NifMessage | NativeMethods.NifIcon | NativeMethods.NifTip,
            CallbackMessage = CallbackMessage,
            Icon = _trayIconHandle,
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
        if (_contextMenu is not null)
        {
            _contextMenu.IsOpen = false;
        }

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };
        menu.Items.Add(CreateMenuItem("显示栖格", _show));
        menu.Items.Add(CreateMenuItem("一键整理", _organize));
        menu.Items.Add(CreateMenuItem("切换桌面模式", _toggleDesktopMode));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("退出", _exit));
        menu.Opened += (_, _) => menu.Dispatcher.BeginInvoke(() =>
        {
            _contextMenuHandle = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? 0;
        });
        menu.Closed += (_, _) =>
        {
            if (_contextMenu == menu)
            {
                _contextMenu = null;
                _contextMenuHandle = 0;
                _menuPointerArmed = false;
                _menuDismissTimer.Stop();
            }
        };
        _contextMenu = menu;
        _menuPointerArmed = false;
        _menuDismissTimer.Start();
        menu.IsOpen = true;
    }

    private void DismissMenuOnOutsidePointerDown()
    {
        if (_contextMenu?.IsOpen != true)
        {
            _menuDismissTimer.Stop();
            return;
        }

        var pointerDown = IsPointerButtonDown();
        if (!pointerDown)
        {
            _menuPointerArmed = true;
            return;
        }

        if (!_menuPointerArmed)
        {
            return;
        }

        _menuPointerArmed = false;
        if (!NativeMethods.GetCursorPos(out var point) ||
            NativeMethods.WindowFromPoint(point) != _contextMenuHandle)
        {
            _contextMenu.IsOpen = false;
        }
    }

    private static bool IsPointerButtonDown() =>
        IsKeyDown(NativeMethods.VkLeftButton) ||
        IsKeyDown(NativeMethods.VkRightButton) ||
        IsKeyDown(NativeMethods.VkMiddleButton);

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

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
        _menuDismissTimer.Stop();
        if (_contextMenu is not null)
        {
            _contextMenu.IsOpen = false;
            _contextMenu = null;
        }
        NativeMethods.Shell_NotifyIcon(NativeMethods.NimDelete, ref _iconData);
        if (_ownsTrayIconHandle)
        {
            NativeMethods.DestroyIcon(_trayIconHandle);
        }
        _messageWindow.RemoveHook(WindowProcedure);
        _messageWindow.Dispose();
    }
}
