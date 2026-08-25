using DeskNest.App.Interop;

namespace DeskNest.App.Services;

internal sealed class DesktopIconVisibilityService
{
    private bool? _initialVisibility;

    public bool AreIconsVisible
    {
        get
        {
            var listView = FindDesktopListView();
            return listView == 0 || NativeMethods.IsWindowVisible(listView);
        }
    }

    public bool SetVisible(bool visible) => SetVisibleCore(visible, rememberInitialState: true);

    public bool SetVisibleWithoutTracking(bool visible) => SetVisibleCore(visible, rememberInitialState: false);

    private bool SetVisibleCore(bool visible, bool rememberInitialState)
    {
        var listView = FindDesktopListView();
        if (listView == 0)
        {
            return false;
        }

        if (rememberInitialState)
        {
            _initialVisibility ??= NativeMethods.IsWindowVisible(listView);
        }

        NativeMethods.ShowWindow(listView, visible ? NativeMethods.SwShow : NativeMethods.SwHide);
        return NativeMethods.IsWindowVisible(listView) == visible;
    }

    public void RestoreInitialState()
    {
        if (_initialVisibility is not { } initialVisibility)
        {
            return;
        }

        var listView = FindDesktopListView();
        if (listView != 0)
        {
            NativeMethods.ShowWindow(listView, initialVisibility ? NativeMethods.SwShow : NativeMethods.SwHide);
        }
    }

    private static nint FindDesktopListView()
    {
        nint shellView = 0;
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != 0)
        {
            shellView = NativeMethods.FindWindowEx(progman, 0, "SHELLDLL_DefView", null);
        }

        if (shellView == 0)
        {
            NativeMethods.EnumWindows((window, _) =>
            {
                shellView = NativeMethods.FindWindowEx(window, 0, "SHELLDLL_DefView", null);
                return shellView == 0;
            }, 0);
        }

        return shellView == 0
            ? 0
            : NativeMethods.FindWindowEx(shellView, 0, "SysListView32", "FolderView");
    }
}
