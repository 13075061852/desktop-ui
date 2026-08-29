namespace DeskNest.Core.Services;

/// <summary>
/// Restores yielded zones to their resting (home) bounds once space frees up.
/// A resize pushes or squeezes neighbours so the dragged zone can follow the
/// pointer; this resolver walks the neighbours back toward their rest bounds
/// whenever the rest position fits again. Recovery is iterative: freeing one
/// zone can release the next, so a whole pushed column walks home in order.
/// </summary>
public static class ZoneLayoutRelaxer
{
    /// <summary>
    /// Attempts to move every obstacle closer to its rest bounds without
    /// breaking the minimum gap against the anchored (dragged) zone, the other
    /// obstacles, or the canvas. Zones that cannot fit keep their current
    /// bounds, so the result is always a valid layout.
    /// </summary>
    /// <param name="current">Obstacle bounds as they stand right now (already pushed/squeezed).</param>
    /// <param name="rest">Rest bounds parallel to <paramref name="current"/>; entries without a
    /// rest position (zero width/height) stay where they are.</param>
    /// <param name="anchor">Bounds of the zone being dragged or resized; treated as immovable.</param>
    public static IReadOnlyList<ZoneBounds> Relax(
        IReadOnlyList<ZoneBounds> current,
        IReadOnlyList<ZoneBounds> rest,
        ZoneBounds anchor,
        double gap,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom)
    {
        if (current.Count == 0)
        {
            return current;
        }

        var working = current.ToArray();
        var count = working.Length;
        var moved = true;
        // Each pass can free space that lets another zone step home; n + 2
        // passes settle any ordering of the cascade.
        for (var pass = 0; moved && pass <= count; pass++)
        {
            moved = false;
            for (var index = 0; index < count; index++)
            {
                var candidate = rest[index];
                if (candidate.Width <= 0 || candidate.Height <= 0 ||
                    NearlyEqual(candidate.X, working[index].X) && NearlyEqual(candidate.Y, working[index].Y) &&
                    NearlyEqual(candidate.Width, working[index].Width) &&
                    NearlyEqual(candidate.Height, working[index].Height))
                {
                    continue;
                }

                if (!IsInsideCanvas(candidate, minimumX, minimumY, maximumRight, maximumBottom))
                {
                    continue;
                }

                var others = new List<ZoneBounds>(count + 1) { anchor };
                for (var other = 0; other < count; other++)
                {
                    if (other != index)
                    {
                        others.Add(working[other]);
                    }
                }

                if (!ZoneCollisionResolver.IsAvailable(
                        candidate, others, gap, minimumX, minimumY, maximumRight, maximumBottom))
                {
                    continue;
                }

                working[index] = candidate;
                moved = true;
            }
        }

        return working;
    }

    private static bool IsInsideCanvas(
        ZoneBounds bounds,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom) =>
        bounds.X >= minimumX - 0.01 &&
        bounds.Y >= minimumY - 0.01 &&
        bounds.Right <= maximumRight + 0.01 &&
        bounds.Bottom <= maximumBottom + 0.01;

    private static bool NearlyEqual(double first, double second) => Math.Abs(first - second) < 0.01;
}
