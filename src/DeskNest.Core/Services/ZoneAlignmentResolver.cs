namespace DeskNest.Core.Services;

/// <summary>Sticky-snap state carried between drag frames. While a guide value
/// is present the resolver keeps snapping that edge to it until the pointer
/// escapes a larger release distance, which prevents flicker near the snap
/// threshold and jumping between adjacent guides.</summary>
public readonly record struct ZoneSnapHysteresis(
    double? VerticalGuide,
    double? HorizontalGuide);

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
        ZoneSnapHysteresis hysteresis = default,
        double stickyEscapeDistance = 0)
    {
        var obstacleArray = obstacles.ToArray();
        var result = collisionSafe;
        double? verticalGuide = null;
        double? horizontalGuide = null;

        var horizontalResolved = false;
        if (hysteresis.VerticalGuide is { } stickyVertical)
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
        }

        if (!horizontalResolved)
        {
            var horizontalCandidate = FindHorizontalCandidate(
                current, desired, result, obstacleArray, snapDistance, IsAvailable);
            if (horizontalCandidate is { } horizontal)
            {
                result = horizontal.Bounds;
                verticalGuide = horizontal.Guide;
            }
        }

        var verticalResolved = false;
        if (hysteresis.HorizontalGuide is { } stickyHorizontal)
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
        }

        if (!verticalResolved)
        {
            var verticalCandidate = FindVerticalCandidate(
                current, desired, result, obstacleArray, snapDistance, IsAvailable);
            if (verticalCandidate is { } vertical)
            {
                result = vertical.Bounds;
                horizontalGuide = vertical.Guide;
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
    /// stays within the escape distance, even if another guide is now closer.</summary>
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

        SnapCandidate? best = null;
        if (Math.Abs(desired.X - guide) <= escapeDistance)
        {
            Consider(new ZoneBounds(guide, constrained.Y, constrained.Width, constrained.Height));
        }

        if (best is null && Math.Abs(desired.Right - guide) <= escapeDistance)
        {
            Consider(new ZoneBounds(guide - constrained.Width, constrained.Y, constrained.Width, constrained.Height));
        }

        return best;

        void Consider(ZoneBounds bounds)
        {
            if ((!isMove && bounds.X < minimumX - Tolerance) ||
                bounds.Right > maximumRight + Tolerance ||
                (!isMove && bounds.Width < MinimumWidth - Tolerance) ||
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

        SnapCandidate? best = null;
        if (Math.Abs(desired.Y - guide) <= escapeDistance)
        {
            Consider(new ZoneBounds(constrained.X, guide, constrained.Width, constrained.Height));
        }

        if (best is null && Math.Abs(desired.Bottom - guide) <= escapeDistance)
        {
            Consider(new ZoneBounds(constrained.X, guide - constrained.Height, constrained.Width, constrained.Height));
        }

        return best;

        void Consider(ZoneBounds bounds)
        {
            if ((!isMove && bounds.Y < minimumY - Tolerance) ||
                bounds.Bottom > maximumBottom + Tolerance ||
                (!isMove && bounds.Height < MinimumHeight - Tolerance) ||
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
        Func<ZoneBounds, bool> isAvailable)
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
        Func<ZoneBounds, bool> isAvailable)
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
