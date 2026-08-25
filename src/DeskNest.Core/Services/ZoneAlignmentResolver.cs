namespace DeskNest.Core.Services;

public readonly record struct ZoneAlignmentResult(
    ZoneBounds Bounds,
    double? VerticalGuide,
    double? HorizontalGuide);

public static class ZoneAlignmentResolver
{
    private const double Tolerance = 0.01;
    private const double MinimumWidth = 200;
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
        double maximumBottom)
    {
        var obstacleArray = obstacles.ToArray();
        var result = collisionSafe;
        double? verticalGuide = null;
        double? horizontalGuide = null;

        var horizontalCandidate = FindHorizontalCandidate(
            current, desired, result, obstacleArray, snapDistance, IsAvailable);
        if (horizontalCandidate is { } horizontal)
        {
            result = horizontal.Bounds;
            verticalGuide = horizontal.Guide;
        }

        var verticalCandidate = FindVerticalCandidate(
            current, desired, result, obstacleArray, snapDistance, IsAvailable);
        if (verticalCandidate is { } vertical)
        {
            result = vertical.Bounds;
            horizontalGuide = vertical.Guide;
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
