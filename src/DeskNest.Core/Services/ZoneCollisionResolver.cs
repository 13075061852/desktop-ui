namespace DeskNest.Core.Services;

public readonly record struct ZoneBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

public static class ZoneCollisionResolver
{
    public static ZoneBounds Constrain(
        ZoneBounds current,
        ZoneBounds desired,
        IEnumerable<ZoneBounds> obstacles,
        double gap,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom)
    {
        var obstacleArray = obstacles.ToArray();
        desired = ClampToCanvas(desired, minimumX, minimumY, maximumRight, maximumBottom);

        // A previously saved layout can be larger than the current work area (for example
        // after changing display scaling). In that case the old position is not a valid
        // collision-solver starting point. If the requested endpoint is valid, let the zone
        // recover to it instead of treating the invalid start as an immovable obstacle.
        if (!IsAvailable(current, obstacleArray, gap, minimumX, minimumY, maximumRight, maximumBottom) &&
            IsAvailable(desired, obstacleArray, gap, minimumX, minimumY, maximumRight, maximumBottom))
        {
            return desired;
        }

        var isMove = Math.Abs(desired.Width - current.Width) < 0.01 &&
                     Math.Abs(desired.Height - current.Height) < 0.01;
        if (isMove)
        {
            return ConstrainMove(
                current,
                desired,
                obstacleArray,
                gap,
                minimumX,
                minimumY,
                maximumRight,
                maximumBottom);
        }

        return ConstrainLinear(
            current,
            desired,
            obstacleArray,
            gap,
            minimumX,
            minimumY,
            maximumRight,
            maximumBottom);
    }

    public static bool IsAvailable(
        ZoneBounds candidate,
        IEnumerable<ZoneBounds> obstacles,
        double gap,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom)
    {
        const double tolerance = 0.01;
        var widthExceedsCanvas = candidate.Width > maximumRight - minimumX + tolerance;
        var heightExceedsCanvas = candidate.Height > maximumBottom - minimumY + tolerance;
        if (candidate.X < minimumX - tolerance || candidate.Y < minimumY - tolerance ||
            (!widthExceedsCanvas && candidate.Right > maximumRight + tolerance) ||
            (!heightExceedsCanvas && candidate.Bottom > maximumBottom + tolerance) ||
            (widthExceedsCanvas && candidate.X > minimumX + tolerance) ||
            (heightExceedsCanvas && candidate.Y > minimumY + tolerance))
        {
            return false;
        }

        return obstacles.All(obstacle =>
            candidate.Right + gap <= obstacle.X + tolerance ||
            candidate.X >= obstacle.Right + gap - tolerance ||
            candidate.Bottom + gap <= obstacle.Y + tolerance ||
            candidate.Y >= obstacle.Bottom + gap - tolerance);
    }

    private static ZoneBounds ConstrainMove(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom)
    {
        var horizontalFirst = ConstrainAxis(current, desired, horizontal: true);
        horizontalFirst = ConstrainAxis(horizontalFirst, desired, horizontal: false);
        horizontalFirst = ConstrainAxis(horizontalFirst, desired, horizontal: true);

        var verticalFirst = ConstrainAxis(current, desired, horizontal: false);
        verticalFirst = ConstrainAxis(verticalFirst, desired, horizontal: true);
        verticalFirst = ConstrainAxis(verticalFirst, desired, horizontal: false);

        return DistanceSquared(horizontalFirst, desired) <= DistanceSquared(verticalFirst, desired)
            ? horizontalFirst
            : verticalFirst;

        ZoneBounds ConstrainAxis(ZoneBounds start, ZoneBounds target, bool horizontal)
        {
            var axisTarget = horizontal
                ? start with { X = target.X }
                : start with { Y = target.Y };
            return ConstrainLinear(
                start,
                axisTarget,
                obstacles,
                gap,
                minimumX,
                minimumY,
                maximumRight,
                maximumBottom);
        }
    }

    private static ZoneBounds ConstrainLinear(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom)
    {
        var largestChange = new[]
        {
            Math.Abs(desired.X - current.X),
            Math.Abs(desired.Y - current.Y),
            Math.Abs(desired.Width - current.Width),
            Math.Abs(desired.Height - current.Height)
        }.Max();
        var steps = Math.Max(1, (int)Math.Ceiling(largestChange / 4));
        var lastValidProgress = 0d;

        for (var step = 1; step <= steps; step++)
        {
            var progress = (double)step / steps;
            var candidate = Interpolate(current, desired, progress);
            if (IsAvailable(candidate, obstacles, gap, minimumX, minimumY, maximumRight, maximumBottom))
            {
                lastValidProgress = progress;
                continue;
            }

            var low = lastValidProgress;
            var high = progress;
            for (var iteration = 0; iteration < 14; iteration++)
            {
                var middle = (low + high) / 2;
                var candidateAtMiddle = Interpolate(current, desired, middle);
                if (IsAvailable(candidateAtMiddle, obstacles, gap, minimumX, minimumY, maximumRight, maximumBottom))
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            return Interpolate(current, desired, low);
        }

        return desired;
    }

    private static double DistanceSquared(ZoneBounds first, ZoneBounds second)
    {
        var deltaX = first.X - second.X;
        var deltaY = first.Y - second.Y;
        return deltaX * deltaX + deltaY * deltaY;
    }

    private static ZoneBounds ClampToCanvas(
        ZoneBounds bounds,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom)
    {
        return bounds with
        {
            X = Math.Clamp(bounds.X, minimumX, Math.Max(minimumX, maximumRight - bounds.Width)),
            Y = Math.Clamp(bounds.Y, minimumY, Math.Max(minimumY, maximumBottom - bounds.Height))
        };
    }

    private static ZoneBounds Interpolate(ZoneBounds start, ZoneBounds end, double progress)
    {
        return new ZoneBounds(
            Lerp(start.X, end.X, progress),
            Lerp(start.Y, end.Y, progress),
            Lerp(start.Width, end.Width, progress),
            Lerp(start.Height, end.Height, progress));
    }

    private static double Lerp(double start, double end, double progress) => start + (end - start) * progress;
}
