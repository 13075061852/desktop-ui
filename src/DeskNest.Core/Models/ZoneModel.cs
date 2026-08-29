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

    /// <summary>Resting (home) position the zone returns to when space frees up.
    /// Only explicit user placement — dragging the header, resizing, splitter
    /// drags — updates the rest bounds; yielding for another zone's resize does
    /// not, so a pushed neighbour can always find its way back.</summary>
    public double RestX { get; set; }

    public double RestY { get; set; }

    public double RestWidth { get; set; }

    public double RestHeight { get; set; }

    public bool HasRestBounds => RestWidth > 0 && RestHeight > 0;

    public void CaptureRestBounds()
    {
        RestX = X;
        RestY = Y;
        RestWidth = Width;
        RestHeight = Height;
    }
}
