namespace DeskNest.Core.Services;

/// <summary>Sticky-snap state carried across one drag session. While a guide is
/// held the edge stays locked to it; once the pointer escapes the release
/// distance the guide is retired for the rest of the session so the edge
/// follows the pointer without tug-of-war.</summary>
public sealed class ZoneSnapHysteresis
{
    public double? HeldVertical { get; set; }
    public double? HeldHorizontal { get; set; }
    public List<double> ReleasedVertical { get; } = new();
    public List<double> ReleasedHorizontal { get; } = new();

    public void Reset()
    {
        HeldVertical = null;
        HeldHorizontal = null;
        ReleasedVertical.Clear();
        ReleasedHorizontal.Clear();
    }
}

public readonly record struct ZoneAlignmentResult(
    ZoneBounds Bounds,
    double? VerticalGuide,
    double? HorizontalGuide);

public static class ZoneAlignmentResolver
{
    private const double Tolerance = 0.01;
    private const double MinimumWidth = 150;
    private const double MinimumHeight = 150;

    public static ZoneAlignmentResult Snap(
        ZoneBounds current,
        ZoneBounds desired,
        ZoneBounds collisionSafe,
        IEnumerable<ZoneBounds> obstacles,
        double snapDistance,
        double gap,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom,
        ZoneSnapHysteresis? hysteresis = null,
        double stickyEscapeDistance = 0)
    {
        var obstacleArray = obstacles.ToArray();
        var result = collisionSafe;
        double? verticalGuide = null;
        double? horizontalGuide = null;

        var horizontalResolved = false;
        if (hysteresis?.HeldVertical is { } stickyVertical)
        {
            var sticky = FindStickyCandidate(
                current, desired, result, obstacleArray, stickyVertical,
                stickyEscapeDistance, minimumX, minimumY, maximumRight, maximumBottom, gap);
            if (sticky is { } held)
            {
                result = held.Bounds;
                verticalGuide = stickyVertical;
                horizontalResolved = true;
            }
            else
            {
                // Pointer escaped: retire this guide for the whole session so
                // it cannot grab the edge back and start a tug-of-war.
                hysteresis.ReleasedVertical.Add(stickyVertical);
                hysteresis.HeldVertical = null;
            }
        }

        if (!horizontalResolved)
        {
            var horizontalCandidate = FindHorizontalCandidate(
                current, desired, result, obstacleArray, snapDistance, IsAvailable,
                hysteresis?.ReleasedVertical);
            if (horizontalCandidate is { } horizontal)
            {
                result = horizontal.Bounds;
                verticalGuide = horizontal.Guide;
                if (hysteresis is not null)
                {
                    hysteresis.HeldVertical = horizontal.Guide;
                }
            }
            else if (hysteresis is not null)
            {
                hysteresis.HeldVertical = null;
            }
        }

        var verticalResolved = false;
        if (hysteresis?.HeldHorizontal is { } stickyHorizontal)
        {
            var sticky = FindStickyCandidateVertical(
                current, desired, result, obstacleArray, stickyHorizontal,
                stickyEscapeDistance, minimumX, minimumY, maximumRight, maximumBottom, gap);
            if (sticky is { } held)
            {
                result = held.Bounds;
                horizontalGuide = stickyHorizontal;
                verticalResolved = true;
            }
            else
            {
                hysteresis.ReleasedHorizontal.Add(stickyHorizontal);
                hysteresis.HeldHorizontal = null;
            }
        }

        if (!verticalResolved)
        {
            var verticalCandidate = FindVerticalCandidate(
                current, desired, result, obstacleArray, snapDistance, IsAvailable,
                hysteresis?.ReleasedHorizontal);
            if (verticalCandidate is { } vertical)
            {
                result = vertical.Bounds;
                horizontalGuide = vertical.Guide;
                if (hysteresis is not null)
                {
                    hysteresis.HeldHorizontal = vertical.Guide;
                }
            }
            else if (hysteresis is not null)
            {
                hysteresis.HeldHorizontal = null;
            }
        }

        return new ZoneAlignmentResult(result, verticalGuide, horizontalGuide);

        bool IsAvailable(ZoneBounds candidate) => ZoneCollisionResolver.IsAvailable(
            candidate,
            obstacleArray,
            gap,
            minimumX,
            minimumY,
            maximumRight,
            maximumBottom);
    }

    /// <summary>Keeps an already-held vertical (X-axis) guide while the pointer
    /// stays within the escape distance, even if another guide is now closer.
    /// Honours which edge is actually moving so a resize never gets swallowed
    /// by a guide that happens to sit near the stationary edge.</summary>
    private static SnapCandidate? FindStickyCandidate(
        ZoneBounds current,
        ZoneBounds desired,
        ZoneBounds constrained,
        IReadOnlyList<ZoneBounds> obstacles,
        double guide,
        double escapeDistance,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom,
        double gap)
    {
        var isMove = NearlyEqual(current.Width, desired.Width) &&
                     NearlyEqual(current.Height, desired.Height);
        var leftChanged = isMove || !NearlyEqual(current.X, desired.X);
        var rightChanged = isMove || !NearlyEqual(current.Right, desired.Right);

        SnapCandidate? best = null;
        if (leftChanged && Math.Abs(desired.X - guide) <= escapeDistance)
        {
            var width = isMove ? constrained.Width : constrained.Right - guide;
            Consider(new ZoneBounds(guide, constrained.Y, width, constrained.Height));
        }

        if (best is null && rightChanged && Math.Abs(desired.Right - guide) <= escapeDistance)
        {
            var width = isMove ? constrained.Width : guide - constrained.X;
            var x = isMove ? guide - constrained.Width : constrained.X;
            Consider(new ZoneBounds(x, constrained.Y, width, constrained.Height));
        }

        return best;

        void Consider(ZoneBounds bounds)
        {
            if ((!isMove && bounds.X < minimumX - Tolerance) ||
                bounds.Right > maximumRight + Tolerance ||
                bounds.Width < MinimumWidth - Tolerance ||
                !ZoneCollisionResolver.IsAvailable(
                    bounds, obstacles, gap, minimumX, minimumY, maximumRight, maximumBottom))
            {
                return;
            }

            best = new SnapCandidate(bounds, guide, 0);
        }
    }

    /// <summary>Keeps an already-held horizontal (Y-axis) guide; mirror of
    /// <see cref="FindStickyCandidate"/>.</summary>
    private static SnapCandidate? FindStickyCandidateVertical(
        ZoneBounds current,
        ZoneBounds desired,
        ZoneBounds constrained,
        IReadOnlyList<ZoneBounds> obstacles,
        double guide,
        double escapeDistance,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom,
        double gap)
    {
        var isMove = NearlyEqual(current.Width, desired.Width) &&
                     NearlyEqual(current.Height, desired.Height);
        var topChanged = isMove || !NearlyEqual(current.Y, desired.Y);
        var bottomChanged = isMove || !NearlyEqual(current.Bottom, desired.Bottom);

        SnapCandidate? best = null;
        if (topChanged && Math.Abs(desired.Y - guide) <= escapeDistance)
        {
            var height = isMove ? constrained.Height : constrained.Bottom - guide;
            Consider(new ZoneBounds(constrained.X, guide, constrained.Width, height));
        }

        if (best is null && bottomChanged && Math.Abs(desired.Bottom - guide) <= escapeDistance)
        {
            var height = isMove ? constrained.Height : guide - constrained.Y;
            var y = isMove ? guide - constrained.Height : constrained.Y;
            Consider(new ZoneBounds(constrained.X, y, constrained.Width, height));
        }

        return best;

        void Consider(ZoneBounds bounds)
        {
            if ((!isMove && bounds.Y < minimumY - Tolerance) ||
                bounds.Bottom > maximumBottom + Tolerance ||
                bounds.Height < MinimumHeight - Tolerance ||
                !ZoneCollisionResolver.IsAvailable(
                    bounds, obstacles, gap, minimumX, minimumY, maximumRight, maximumBottom))
            {
                return;
            }

            best = new SnapCandidate(bounds, guide, 0);
        }
    }

    private static SnapCandidate? FindHorizontalCandidate(
        ZoneBounds current,
        ZoneBounds desired,
        ZoneBounds constrained,
        IReadOnlyList<ZoneBounds> obstacles,
        double threshold,
        Func<ZoneBounds, bool> isAvailable,
        IReadOnlyList<double>? releasedGuides = null)
    {
        var isMove = NearlyEqual(current.Width, desired.Width) &&
                     NearlyEqual(current.Height, desired.Height);
        var leftChanged = isMove || !NearlyEqual(current.X, desired.X);
        var rightChanged = isMove || !NearlyEqual(current.Right, desired.Right);
        SnapCandidate? best = null;

        foreach (var obstacle in obstacles)
        {
            foreach (var guide in new[] { obstacle.X, obstacle.Right })
            {
                if (releasedGuides is not null && releasedGuides.Any(r => Math.Abs(r - guide) < Tolerance))
                {
                    continue;
                }

                if (leftChanged)
                {
                    var distance = Math.Abs(desired.X - guide);
                    var width = isMove ? constrained.Width : constrained.Right - guide;
                    Consider(new ZoneBounds(guide, constrained.Y, width, constrained.Height), guide, distance);
                }

                if (rightChanged)
                {
                    var distance = Math.Abs(desired.Right - guide);
                    var width = guide - constrained.X;
                    var x = isMove ? guide - constrained.Width : constrained.X;
                    Consider(new ZoneBounds(x, constrained.Y, isMove ? constrained.Width : width, constrained.Height), guide, distance);
                }
            }
        }

        return best;

        void Consider(ZoneBounds bounds, double guide, double distance)
        {
            if (distance > threshold || (!isMove && bounds.Width < MinimumWidth - Tolerance) || !isAvailable(bounds) ||
                best is { Distance: var bestDistance } && distance >= bestDistance)
            {
                return;
            }

            best = new SnapCandidate(bounds, guide, distance);
        }
    }

    private static SnapCandidate? FindVerticalCandidate(
        ZoneBounds current,
        ZoneBounds desired,
        ZoneBounds constrained,
        IReadOnlyList<ZoneBounds> obstacles,
        double threshold,
        Func<ZoneBounds, bool> isAvailable,
        IReadOnlyList<double>? releasedGuides = null)
    {
        var isMove = NearlyEqual(current.Width, desired.Width) &&
                     NearlyEqual(current.Height, desired.Height);
        var topChanged = isMove || !NearlyEqual(current.Y, desired.Y);
        var bottomChanged = isMove || !NearlyEqual(current.Bottom, desired.Bottom);
        SnapCandidate? best = null;

        foreach (var obstacle in obstacles)
        {
            foreach (var guide in new[] { obstacle.Y, obstacle.Bottom })
            {
                if (releasedGuides is not null && releasedGuides.Any(r => Math.Abs(r - guide) < Tolerance))
                {
                    continue;
                }

                if (topChanged)
                {
                    var distance = Math.Abs(desired.Y - guide);
                    var height = isMove ? constrained.Height : constrained.Bottom - guide;
                    Consider(new ZoneBounds(constrained.X, guide, constrained.Width, height), guide, distance);
                }

                if (bottomChanged)
                {
                    var distance = Math.Abs(desired.Bottom - guide);
                    var height = guide - constrained.Y;
                    var y = isMove ? guide - constrained.Height : constrained.Y;
                    Consider(new ZoneBounds(constrained.X, y, constrained.Width, isMove ? constrained.Height : height), guide, distance);
                }
            }
        }

        return best;

        void Consider(ZoneBounds bounds, double guide, double distance)
        {
            if (distance > threshold || (!isMove && bounds.Height < MinimumHeight - Tolerance) || !isAvailable(bounds) ||
                best is { Distance: var bestDistance } && distance >= bestDistance)
            {
                return;
            }

            best = new SnapCandidate(bounds, guide, distance);
        }
    }

    private static bool NearlyEqual(double first, double second) => Math.Abs(first - second) < Tolerance;

    private readonly record struct SnapCandidate(ZoneBounds Bounds, double Guide, double Distance);
}
