using System.IO;
using System.Text;
using Microsoft.Win32;

namespace DeskNest.App.Services;

internal sealed record ShellNewDefinition(
    string RegisteredExtension,
    string OutputExtension,
    string DisplayName,
    object? Data,
    string? TemplatePath,
    string? Command);

internal sealed class ShellNewService
{
    private static readonly string[] PreferredExtensions =
    [
        ".accdb",
        ".bmp",
        ".doc",
        ".docx",
        ".mdb",
        ".pdfwpsshellnew",
        ".ppt",
        ".pptx",
        ".pptxwpsaicreateshellnew",
        ".pub",
        ".rtf",
        ".txt",
        ".xls",
        ".xlsx"
    ];

    private static readonly ShellNewDefinition[] FallbackDefinitions =
    [
        new(".accdb", ".accdb", "Microsoft Access Database", null, null, null),
        new(".bmp", ".bmp", "BMP 图像", null, null, null),
        new(".doc", ".doc", "DOC 文档", null, null, null),
        new(".docx", ".docx", "DOCX 文档", null, null, null),
        new(".mdb", ".mdb", "Microsoft Access Database", null, null, null),
        new(".pdfwpsshellnew", ".pdf", "WPS PDF 文档", null, null, null),
        new(".ppt", ".ppt", "PPT 演示文稿", null, null, null),
        new(".pptx", ".pptx", "PPTX 演示文稿", null, null, null),
        new(".pptxwpsaicreateshellnew", ".pptx", "WPS AI 生成 PPT", null, null, null),
        new(".pub", ".pub", "Microsoft Publisher Document", null, null, null),
        new(".rtf", ".rtf", "RTF 文件", "{\\rtf1}", null, null),
        new(".txt", ".txt", "文本文档", null, null, null),
        new(".xls", ".xls", "XLS 工作表", null, null, null),
        new(".xlsx", ".xlsx", "XLSX 工作表", null, null, null)
    ];

    public IReadOnlyList<ShellNewDefinition> GetDefinitions()
    {
        var definitions = new List<ShellNewDefinition>();
        foreach (var extension in PreferredExtensions)
        {
            var definition = ReadDefinition(extension) ??
                              FallbackDefinitions.FirstOrDefault(value =>
                                  string.Equals(value.RegisteredExtension, extension, StringComparison.OrdinalIgnoreCase));
            if (definition is not null)
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    public bool CreateFile(ShellNewDefinition definition, string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(definition.TemplatePath) && File.Exists(definition.TemplatePath))
            {
                File.Copy(definition.TemplatePath, path, overwrite: false);
                return true;
            }

            if (definition.Data is byte[] bytes)
            {
                File.WriteAllBytes(path, bytes);
                return true;
            }

            if (definition.Data is string text)
            {
                File.WriteAllText(path, text, Encoding.Default);
                return true;
            }

            // ShellNew command handlers from third-party suites are intentionally not invoked.
            // Creating the file directly avoids their desktop-layer-specific failures while
            // preserving the requested extension and mapping behavior.
            using (File.Create(path))
            {
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static ShellNewDefinition? ReadDefinition(string extension)
    {
        try
        {
            using var extensionKey = Registry.ClassesRoot.OpenSubKey(extension);
            var className = extensionKey?.GetValue(string.Empty) as string;
            using var directShellNew = extensionKey?.OpenSubKey("ShellNew");
            using var classKey = string.IsNullOrWhiteSpace(className)
                ? null
                : Registry.ClassesRoot.OpenSubKey(className);
            using var classShellNew = classKey?.OpenSubKey("shell\\new");
            using var shellNew = directShellNew ?? classShellNew;
            if (shellNew is null)
            {
                return null;
            }

            var displayName = ReadDisplayName(shellNew, classKey, extension);
            var templatePath = ExpandPath(shellNew.GetValue("FileName") as string);
            var command = shellNew.GetValue("Command") as string;
            var data = shellNew.GetValue("Data");
            var outputExtension = extension switch
            {
                ".pdfwpsshellnew" => ".pdf",
                ".pptxwpsaicreateshellnew" => ".pptx",
                _ => extension
            };
            return new ShellNewDefinition(
                extension,
                outputExtension,
                displayName,
                data,
                templatePath,
                command);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ReadDisplayName(RegistryKey shellNew, RegistryKey? classKey, string extension)
    {
        var itemName = shellNew.GetValue("ItemName") as string;
        var resolvedItemName = ResolvePlainName(itemName);
        if (!string.IsNullOrWhiteSpace(resolvedItemName))
        {
            return resolvedItemName;
        }

        var fallbackName = FallbackDefinitions.FirstOrDefault(value =>
            string.Equals(value.RegisteredExtension, extension, StringComparison.OrdinalIgnoreCase))?.DisplayName;
        if (fallbackName is not null &&
            (extension.Contains("wpsshellnew", StringComparison.OrdinalIgnoreCase) ||
             extension.Contains("wpsaicreateshellnew", StringComparison.OrdinalIgnoreCase)))
        {
            return fallbackName;
        }

        var className = ResolvePlainName(classKey?.GetValue(string.Empty) as string);
        if (!string.IsNullOrWhiteSpace(className))
        {
            return className;
        }

        return fallbackName ?? extension.TrimStart('.').ToUpperInvariant();
    }

    private static string? ResolvePlainName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('@'))
        {
            return null;
        }

        return value.Replace("(&N)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(&E)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string? ExpandPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? null
            : Environment.ExpandEnvironmentVariables(path);
    }
}
