using System.IO;
using Microsoft.Win32;

namespace DeskNest.App.Services;

internal sealed class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskNest";

    public bool Apply(bool enabled)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (runKey is null)
            {
                return false;
            }

            if (!enabled)
            {
                runKey.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                return false;
            }

            runKey.SetValue(ValueName, Quote(executablePath!), RegistryValueKind.String);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string Quote(string path) => $"\"{path.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
