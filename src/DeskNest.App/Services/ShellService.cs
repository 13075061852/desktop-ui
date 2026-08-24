using System.Diagnostics;
using System.IO;

namespace DeskNest.App.Services;

internal sealed class ShellService
{
    public bool Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return false;
        }

        return Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public bool Reveal(string path)
    {
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
