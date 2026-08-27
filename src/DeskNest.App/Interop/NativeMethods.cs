using System.Runtime.InteropServices;

namespace DeskNest.App.Interop;

internal static class NativeMethods
{
    internal const uint WmSpawnWorker = 0x052C;
    internal const uint SpiGetDesktopWallpaper = 0x0073;
    internal const uint SpiSetDesktopWallpaper = 0x0014;
    internal const uint SpifUpdateIniFile = 0x0001;
    internal const uint SpifSendChange = 0x0002;
    internal const int WmActivateApp = 0x001C;
    internal const int WmNcHitTest = 0x0084;
    internal const int WmHotKey = 0x0312;
    internal const int HtTransparent = -1;
    internal const int HtClient = 1;
    internal const uint SmtoNormal = 0;
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const long WsChild = 0x40000000L;
    internal const long WsPopup = unchecked((long)0x80000000L);
    internal const long WsExTransparent = 0x00000020L;
    internal const long WsExToolWindow = 0x00000080L;
    internal const long WsExNoActivate = 0x08000000L;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpShowWindow = 0x0040;
    internal const int SwHide = 0;
    internal const int SwShow = 5;
    internal static readonly nint HwndBottom = 1;

    internal const uint ShgfiIcon = 0x000000100;
    internal const uint ShgfiLargeIcon = 0x000000000;
    internal const uint ShgfiSmallIcon = 0x000000001;
    internal const uint ShgfiSysIconIndex = 0x000004000;
    internal const uint ShgsiIcon = 0x000000100;
    internal const uint ShgsiLargeIcon = 0x000000000;
    internal const uint SiidComputer = 17;
    internal const uint SiidRecycler = 31;
    internal const int ShilLarge = 0;
    internal const int ShilExtraLarge = 2;
    internal const int IldTransparent = 0x00000001;
    internal const uint NimAdd = 0x00000000;
    internal const uint NimModify = 0x00000001;
    internal const uint NimDelete = 0x00000002;
    internal const uint NimSetVersion = 0x00000004;
    internal const uint NifMessage = 0x00000001;
    internal const uint NifIcon = 0x00000002;
    internal const uint NifTip = 0x00000004;
    internal const uint NifInfo = 0x00000010;
    internal const uint NotifyIconVersion4 = 4;
    internal const uint NiifInfo = 0x00000001;
    internal const uint WmApp = 0x8000;
    internal const int WmLeftButtonDoubleClick = 0x0203;
    internal const int WmRightButtonUp = 0x0205;
    internal const int VkLeftButton = 0x01;
    internal const int VkRightButton = 0x02;
    internal const int VkMiddleButton = 0x04;
    internal const int VkDelete = 0x2E;
    internal const uint ImageIcon = 1;
    internal const uint LrDefaultSize = 0x0040;
    internal const uint LrLoadFromFile = 0x0010;

    internal delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("kernel32.dll")]
    internal static extern nint GetCurrentProcess();

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyWorkingSet(nint process);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint SendMessageTimeout(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeout,
        out nint result);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetParent(nint child, nint newParent);

    [DllImport("user32.dll")]
    internal static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoGet(
        uint action,
        uint parameter,
        System.Text.StringBuilder value,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SystemParametersInfoSet(
        uint action,
        uint parameter,
        string value,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    internal static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("shell32.dll", EntryPoint = "SHGetStockIconInfo", CharSet = CharSet.Unicode)]
    internal static extern int SHGetStockIconInfo(
        uint stockIconId,
        uint flags,
        ref StockIconInfo stockIconInfo);

    [DllImport("shell32.dll", EntryPoint = "SHGetImageList")]
    internal static extern int SHGetImageList(
        int imageList,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IImageList result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint LoadImage(
        nint instance,
        string name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IImageList
    {
        [PreserveSig]
        int Add(nint image, nint mask, out int index);

        [PreserveSig]
        int ReplaceIcon(int index, nint icon, out int newIndex);

        [PreserveSig]
        int SetOverlayImage(int imageIndex, int overlayIndex);

        [PreserveSig]
        int Replace(int index, nint image, nint mask);

        [PreserveSig]
        int AddMasked(nint image, int maskColor, out int index);

        [PreserveSig]
        int Draw(nint drawParameters);

        [PreserveSig]
        int Remove(int index);

        [PreserveSig]
        int GetIcon(int index, int flags, out nint icon);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StockIconInfo
    {
        public uint Size;
        public nint IconHandle;
        public int SystemImageIndex;
        public int IconIndex;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ShellFileInfo
    {
        public nint IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }
}
