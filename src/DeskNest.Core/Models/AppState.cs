namespace DeskNest.Core.Models;

public sealed class AppState
{
    public int SchemaVersion { get; set; } = 1;

    public bool LayoutLocked { get; set; }

    public bool DesktopIconsHidden { get; set; }

    public string Theme { get; set; } = "System";

    public double PanelOpacity { get; set; } = 0.84;

    public double IconSize { get; set; } = 44;

    public List<ZoneModel> Zones { get; set; } = [];

    public static AppState CreateDefault()
    {
        return new AppState
        {
            Zones =
            [
                CreateZone("apps", "常用软件", "#76D7C4", 48, 116, 330, 260),
                CreateZone("documents", "工作文档", "#80BFFF", 402, 116, 360, 260),
                CreateZone("images", "图片素材", "#FFB86B", 786, 116, 330, 260),
                CreateZone("folders", "文件夹", "#C4A7FF", 48, 400, 330, 240),
                CreateZone("other", "临时文件", "#FF8398", 402, 400, 360, 240)
            ]
        };
    }

    private static ZoneModel CreateZone(
        string categoryKey,
        string name,
        string color,
        double x,
        double y,
        double width,
        double height)
    {
        return new ZoneModel
        {
            CategoryKey = categoryKey,
            Name = name,
            AccentColor = color,
            X = x,
            Y = y,
            Width = width,
            Height = height
        };
    }
}
