namespace DeskNest.Core.Services;

public sealed class RuleClassifier
{
    private static readonly HashSet<string> AppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lnk", ".url", ".exe", ".appref-ms", ".msi"
    };

    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".pdf", ".txt", ".md",
        ".rtf", ".csv", ".odt", ".ods", ".wps", ".et", ".dps"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif",
        ".tiff", ".heic", ".raw", ".psd", ".ai", ".xd", ".sketch"
    };

    public string Classify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Directory.Exists(path))
        {
            return "folders";
        }

        var extension = Path.GetExtension(path);
        if (AppExtensions.Contains(extension))
        {
            return "apps";
        }

        if (DocumentExtensions.Contains(extension))
        {
            return "documents";
        }

        if (ImageExtensions.Contains(extension))
        {
            return "images";
        }

        return "other";
    }
}
