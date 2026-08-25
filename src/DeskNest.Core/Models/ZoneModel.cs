namespace DeskNest.Core.Models;

public sealed class ZoneModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string CategoryKey { get; set; } = "other";

    public string Name { get; set; } = "新分区";

    public string AccentColor { get; set; } = "#7DD3FC";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 330;

    public double Height { get; set; } = 260;

    public bool IsCollapsed { get; set; }

    public string ViewMode { get; set; } = "Icons";

    public List<DesktopItem> Items { get; set; } = [];
}
