using System.IO;
using System.Runtime.InteropServices;
using DeskNest.Core.Models;
using Microsoft.Win32;

namespace DeskNest.App.Services;

internal sealed record ShellDesktopItem(string Path, string DisplayName, string CategoryKey);

internal sealed class DesktopShellScanner
{
    private const uint ShcontfFolders = 0x00000020;
    private const uint ShcontfNonFolders = 0x00000040;
    private const uint SigdnNormalDisplay = 0x00000000;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    private const string ThisPcClassId = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private const string RecycleBinClassId = "{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string NetworkClassId = "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}";
    private const string ControlPanelClassId = "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}";
    private const string ControlPanelHomeClassId = "{26EE0668-A00A-44D7-9371-BEB064C98683}";
    private const string UserFilesClassId = "{59031A47-3F72-44A7-89C5-5595FE6B30EE}";
    private const string DesktopNameSpaceKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace";
    private const string HideDesktopIconsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons";

    private static readonly HashSet<string> BuiltInClassIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ThisPcClassId,
        RecycleBinClassId,
        NetworkClassId,
        ControlPanelClassId,
        ControlPanelHomeClassId,
        UserFilesClassId
    };

    public bool LastScanSucceeded { get; private set; }

    public IReadOnlyList<ShellDesktopItem> Scan()
    {
        var result = new List<ShellDesktopItem>();
        if (SHGetDesktopFolder(out var desktopFolder) < 0 || desktopFolder is null)
        {
            return result;
        }

        try
        {
            if (desktopFolder.EnumObjects(
                    IntPtr.Zero,
                    ShcontfFolders | ShcontfNonFolders,
                    out var enumerator) < 0 || enumerator is null)
            {
                return result;
            }

            try
            {
                while (enumerator.Next(1, out var itemId, out var fetched) == 0 && fetched == 1)
                {
                    try
                    {
                        var parsingName = GetName(itemId, SigdnDesktopAbsoluteParsing);
                        if (string.IsNullOrWhiteSpace(parsingName) ||
                            File.Exists(parsingName) ||
                            Directory.Exists(parsingName))
                        {
                            continue;
                        }

                        var displayName = GetName(itemId, SigdnNormalDisplay);
                        if (string.IsNullOrWhiteSpace(displayName) || !IsDesktopShellItem(parsingName))
                        {
                            continue;
                        }

                        var shellPath = NormalizeShellPath(parsingName);
                        result.Add(new ShellDesktopItem(
                            shellPath,
                            displayName,
                            Classify(shellPath)));
                    }
                    finally
                    {
                        if (itemId != IntPtr.Zero)
                        {
                            Marshal.FreeCoTaskMem(itemId);
                        }
                    }
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(enumerator);
            }
        }
        catch (COMException)
        {
            return result;
        }
        catch (Exception)
        {
            return result;
        }
        finally
        {
            Marshal.FinalReleaseComObject(desktopFolder);
        }

        LastScanSucceeded = true;
        return result
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string? GetName(IntPtr itemId, uint nameType)
    {
        if (SHGetNameFromIDList(itemId, nameType, out var namePointer) < 0 || namePointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(namePointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
    }

    private static bool IsDesktopShellItem(string parsingName)
    {
        var classId = ExtractClassId(parsingName);
        if (classId is null)
        {
            return false;
        }

        if (BuiltInClassIds.Contains(classId))
        {
            return IsBuiltInVisible(classId);
        }

        return IsRegisteredInCurrentUserDesktopNamespace(classId);
    }

    private static bool IsBuiltInVisible(string classId)
    {
        var policyClassId = classId.Equals(ControlPanelHomeClassId, StringComparison.OrdinalIgnoreCase)
            ? ControlPanelClassId
            : classId;
        // Windows only shows Computer and Recycle Bin by default. Other standard
        // shell icons appear here only after the user explicitly enables them.
        var defaultVisible = classId.Equals(ThisPcClassId, StringComparison.OrdinalIgnoreCase) ||
                             classId.Equals(RecycleBinClassId, StringComparison.OrdinalIgnoreCase);
        var hasVisibilityValue = false;

        foreach (var subKeyName in new[] { "NewStartPanel", "ClassicStartMenu" })
        {
            using var key = Registry.CurrentUser.OpenSubKey($"{HideDesktopIconsKey}\\{subKeyName}");
            if (key?.GetValue(policyClassId) is not int value)
            {
                continue;
            }

            hasVisibilityValue = true;
            if (value != 0)
            {
                return false;
            }
        }

        return hasVisibilityValue || defaultVisible;
    }

    private static bool IsRegisteredInCurrentUserDesktopNamespace(string classId)
    {
        using var key = Registry.CurrentUser.OpenSubKey(DesktopNameSpaceKey);
        return key?.GetSubKeyNames()
            .Any(name => string.Equals(name, classId, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string? ExtractClassId(string parsingName)
    {
        var start = parsingName.IndexOf('{');
        var end = parsingName.IndexOf('}', start + 1);
        return start >= 0 && end > start
            ? parsingName[start..(end + 1)]
            : null;
    }

    private static string NormalizeShellPath(string parsingName)
    {
        var classId = ExtractClassId(parsingName);
        return classId switch
        {
            _ when string.Equals(classId, ThisPcClassId, StringComparison.OrdinalIgnoreCase) => DesktopItem.ThisPcShellPath,
            _ when string.Equals(classId, RecycleBinClassId, StringComparison.OrdinalIgnoreCase) => DesktopItem.RecycleBinShellPath,
            _ when string.Equals(classId, NetworkClassId, StringComparison.OrdinalIgnoreCase) => DesktopItem.NetworkShellPath,
            _ when string.Equals(classId, ControlPanelClassId, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(classId, ControlPanelHomeClassId, StringComparison.OrdinalIgnoreCase) => DesktopItem.ControlPanelShellPath,
            _ when string.Equals(classId, UserFilesClassId, StringComparison.OrdinalIgnoreCase) => DesktopItem.UserFilesShellPath,
            _ => parsingName
        };
    }

    private static string Classify(string shellPath)
    {
        return shellPath switch
        {
            DesktopItem.ThisPcShellPath => "folders",
            DesktopItem.UserFilesShellPath => "folders",
            DesktopItem.RecycleBinShellPath => "other",
            _ => "other"
        };
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder([MarshalAs(UnmanagedType.Interface)] out IShellFolder desktopFolder);

    [DllImport("shell32.dll")]
    private static extern int SHGetNameFromIDList(
        IntPtr itemId,
        uint sigdnName,
        out IntPtr namePointer);

    [ComImport]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig]
        int ParseDisplayName(
            IntPtr hwnd,
            IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string displayName,
            ref uint charactersEaten,
            out IntPtr itemId,
            ref uint attributes);

        [PreserveSig]
        int EnumObjects(
            IntPtr hwnd,
            uint flags,
            [MarshalAs(UnmanagedType.Interface)] out IEnumIDList enumerator);

        [PreserveSig]
        int BindToObject(IntPtr itemId, IntPtr pbc, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig]
        int BindToStorage(IntPtr itemId, IntPtr pbc, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr itemId1, IntPtr itemId2);

        [PreserveSig]
        int CreateViewObject(IntPtr hwndOwner, ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig]
        int GetAttributesOf(uint itemCount, IntPtr itemIds, ref uint attributes);

        [PreserveSig]
        int GetUIObjectOf(
            IntPtr hwndOwner,
            uint itemCount,
            IntPtr itemIds,
            ref Guid interfaceId,
            ref uint reserved,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [PreserveSig]
        int GetDisplayNameOf(IntPtr itemId, uint flags, IntPtr name);

        [PreserveSig]
        int SetNameOf(
            IntPtr hwnd,
            IntPtr itemId,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            uint flags,
            out IntPtr newItemId);
    }

    [ComImport]
    [Guid("000214F2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumIDList
    {
        [PreserveSig]
        int Next(uint count, out IntPtr itemId, out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumIDList enumerator);
    }
}
