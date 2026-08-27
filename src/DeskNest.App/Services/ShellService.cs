using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using DeskNest.Core.Models;

namespace DeskNest.App.Services;

internal sealed class ShellService
{
    public bool Open(string path)
    {
        if (DesktopItem.IsShellLocation(path))
        {
            var shellArgument = $"\"{path.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
            return Start(new ProcessStartInfo("explorer.exe", shellArgument) { UseShellExecute = true });
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool Reveal(string path)
    {
        if (DesktopItem.IsShellLocation(path))
        {
            return Open(path);
        }

        if (Directory.Exists(path))
        {
            return Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }

        if (!File.Exists(path))
        {
            return false;
        }

        return Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public bool ShowProperties(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "properties"
        });
    }

    public bool CreateShortcut(string shortcutPath, string targetPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = targetPath;
            dynamicShortcut.WorkingDirectory = Directory.Exists(targetPath)
                ? targetPath
                : Path.GetDirectoryName(targetPath) ?? string.Empty;
            dynamicShortcut.IconLocation = targetPath;
            dynamicShortcut.Save();
            return File.Exists(shortcutPath);
        }
        catch (Exception exception) when (
            exception is COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public bool RenameWithElevation(string source, string destination)
    {
        var escapedSource = source.Replace("'", "''", StringComparison.Ordinal);
        var escapedDestination = destination.Replace("'", "''", StringComparison.Ordinal);
        var command = $"Move-Item -LiteralPath '{escapedSource}' -Destination '{escapedDestination}' -ErrorAction Stop";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

        try
        {
            using var process = Process.Start(new ProcessStartInfo(
                "powershell.exe",
                $"-NoProfile -NonInteractive -EncodedCommand {encodedCommand}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0 && (File.Exists(destination) || Directory.Exists(destination));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public bool MoveToRecycleBin(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return !File.Exists(path);
            }

            if (Directory.Exists(path))
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                return !Directory.Exists(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    private static bool Start(ProcessStartInfo startInfo)
    {
        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
