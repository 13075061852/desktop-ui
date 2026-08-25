namespace DeskNest.Core.Services;

public readonly record struct ZoneStackResizeResult(
    ZoneBounds Desired,
    IReadOnlyList<ZoneBounds> Obstacles);

public static class ZoneStackResizeResolver
{
    private const double Tolerance = 0.01;

    public static ZoneStackResizeResult CompressAdjacent(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumWidth,
        double minimumHeight,
        double adjacencyTolerance = 1)
    {
        if (IsBottomExpansion(current, desired))
        {
            return CompressBottom(current, desired, obstacles, gap, minimumHeight, adjacencyTolerance);
        }

        if (IsTopExpansion(current, desired))
        {
            return CompressTop(current, desired, obstacles, gap, minimumHeight, adjacencyTolerance);
        }

        if (IsRightExpansion(current, desired))
        {
            return CompressRight(current, desired, obstacles, gap, minimumWidth, adjacencyTolerance);
        }

        if (IsLeftExpansion(current, desired))
        {
            return CompressLeft(current, desired, obstacles, gap, minimumWidth, adjacencyTolerance);
        }

        return Unchanged(desired, obstacles);
    }

    public static ZoneStackResizeResult ReflowAdjacent(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumWidth,
        double minimumHeight,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom,
        double adjacencyTolerance = 1)
    {
        var direction = GetExpansionDirection(current, desired);
        if (direction == ExpansionDirection.None)
        {
            return new ZoneStackResizeResult(desired, obstacles);
        }

        var directIndexes = FindAdjacent(obstacles, bounds => IsDirectlyAdjacent(
            direction, current, desired, bounds, gap, adjacencyTolerance));
        if (directIndexes.Length == 0)
        {
            return CompressAdjacent(current, desired, obstacles, gap, minimumWidth, minimumHeight, adjacencyTolerance);
        }

        var pushGroup = new HashSet<int>(directIndexes);
        var added = true;
        while (added)
        {
            added = false;
            foreach (var sourceIndex in pushGroup.ToArray())
            {
                for (var candidateIndex = 0; candidateIndex < obstacles.Count; candidateIndex++)
                {
                    if (pushGroup.Contains(candidateIndex) || !IsDirectlyAhead(
                            direction, obstacles[sourceIndex], obstacles[candidateIndex], gap, adjacencyTolerance))
                    {
                        continue;
                    }

                    pushGroup.Add(candidateIndex);
                    added = true;
                }
            }
        }

        var requestedMovement = GetExpansionAmount(direction, current, desired);
        var availableMovement = requestedMovement;
        foreach (var index in pushGroup)
        {
            var bounds = obstacles[index];
            availableMovement = Math.Min(availableMovement, GetBoundarySpace(
                direction, bounds, minimumX, minimumY, maximumRight, maximumBottom));

            for (var otherIndex = 0; otherIndex < obstacles.Count; otherIndex++)
            {
                if (pushGroup.Contains(otherIndex))
                {
                    continue;
                }

                availableMovement = Math.Min(availableMovement,
                    GetSpaceBeforeObstacle(direction, bounds, obstacles[otherIndex], gap));
            }
        }

        var movement = Math.Clamp(availableMovement, 0, requestedMovement);
        var shifted = obstacles.ToArray();
        foreach (var index in pushGroup)
        {
            shifted[index] = Shift(obstacles[index], direction, movement);
        }

        var compressionCurrent = Expand(current, direction, movement);
        return CompressAdjacent(
            compressionCurrent,
            desired,
            shifted,
            gap,
            minimumWidth,
            minimumHeight,
            adjacencyTolerance);
    }

    public static ZoneStackResizeResult CompressAdjacentBelow(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumHeight,
        double adjacencyTolerance = 1) =>
        CompressBottom(current, desired, obstacles, gap, minimumHeight, adjacencyTolerance);

    private static bool IsDirectlyAdjacent(
        ExpansionDirection direction,
        ZoneBounds current,
        ZoneBounds desired,
        ZoneBounds obstacle,
        double gap,
        double tolerance) => direction switch
        {
            ExpansionDirection.Right => Math.Abs(obstacle.X - (current.Right + gap)) <= tolerance &&
                                        OverlapsVertically(desired, obstacle),
            ExpansionDirection.Left => Math.Abs(obstacle.Right + gap - current.X) <= tolerance &&
                                       OverlapsVertically(desired, obstacle),
            ExpansionDirection.Bottom => Math.Abs(obstacle.Y - (current.Bottom + gap)) <= tolerance &&
                                         OverlapsHorizontally(desired, obstacle),
            ExpansionDirection.Top => Math.Abs(obstacle.Bottom + gap - current.Y) <= tolerance &&
                                      OverlapsHorizontally(desired, obstacle),
            _ => false
        };

    private static bool IsDirectlyAhead(
        ExpansionDirection direction,
        ZoneBounds source,
        ZoneBounds candidate,
        double gap,
        double tolerance) => direction switch
        {
            ExpansionDirection.Right => Math.Abs(candidate.X - (source.Right + gap)) <= tolerance &&
                                        OverlapsVertically(source, candidate),
            ExpansionDirection.Left => Math.Abs(candidate.Right + gap - source.X) <= tolerance &&
                                       OverlapsVertically(source, candidate),
            ExpansionDirection.Bottom => Math.Abs(candidate.Y - (source.Bottom + gap)) <= tolerance &&
                                         OverlapsHorizontally(source, candidate),
            ExpansionDirection.Top => Math.Abs(candidate.Bottom + gap - source.Y) <= tolerance &&
                                      OverlapsHorizontally(source, candidate),
            _ => false
        };

    private static double GetBoundarySpace(
        ExpansionDirection direction,
        ZoneBounds bounds,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom) => direction switch
        {
            ExpansionDirection.Right => maximumRight - bounds.Right,
            ExpansionDirection.Left => bounds.X - minimumX,
            ExpansionDirection.Bottom => maximumBottom - bounds.Bottom,
            ExpansionDirection.Top => bounds.Y - minimumY,
            _ => 0
        };

    private static double GetSpaceBeforeObstacle(
        ExpansionDirection direction,
        ZoneBounds moving,
        ZoneBounds obstacle,
        double gap)
    {
        return direction switch
        {
            ExpansionDirection.Right when OverlapsVertically(moving, obstacle) && obstacle.X >= moving.Right + gap
                => obstacle.X - gap - moving.Right,
            ExpansionDirection.Left when OverlapsVertically(moving, obstacle) && obstacle.Right + gap <= moving.X
                => moving.X - gap - obstacle.Right,
            ExpansionDirection.Bottom when OverlapsHorizontally(moving, obstacle) && obstacle.Y >= moving.Bottom + gap
                => obstacle.Y - gap - moving.Bottom,
            ExpansionDirection.Top when OverlapsHorizontally(moving, obstacle) && obstacle.Bottom + gap <= moving.Y
                => moving.Y - gap - obstacle.Bottom,
            _ => double.PositiveInfinity
        };
    }

    private static ZoneBounds Shift(ZoneBounds bounds, ExpansionDirection direction, double amount) => direction switch
    {
        ExpansionDirection.Right => bounds with { X = bounds.X + amount },
        ExpansionDirection.Left => bounds with { X = bounds.X - amount },
        ExpansionDirection.Bottom => bounds with { Y = bounds.Y + amount },
        ExpansionDirection.Top => bounds with { Y = bounds.Y - amount },
        _ => bounds
    };

    private static ZoneBounds Expand(ZoneBounds bounds, ExpansionDirection direction, double amount) => direction switch
    {
        ExpansionDirection.Right => bounds with { Width = bounds.Width + amount },
        ExpansionDirection.Left => bounds with { X = bounds.X - amount, Width = bounds.Width + amount },
        ExpansionDirection.Bottom => bounds with { Height = bounds.Height + amount },
        ExpansionDirection.Top => bounds with { Y = bounds.Y - amount, Height = bounds.Height + amount },
        _ => bounds
    };

    private static double GetExpansionAmount(ExpansionDirection direction, ZoneBounds current, ZoneBounds desired) =>
        direction switch
        {
            ExpansionDirection.Right => desired.Right - current.Right,
            ExpansionDirection.Left => current.X - desired.X,
            ExpansionDirection.Bottom => desired.Bottom - current.Bottom,
            ExpansionDirection.Top => current.Y - desired.Y,
            _ => 0
        };

    private static ExpansionDirection GetExpansionDirection(ZoneBounds current, ZoneBounds desired)
    {
        if (IsBottomExpansion(current, desired)) return ExpansionDirection.Bottom;
        if (IsTopExpansion(current, desired)) return ExpansionDirection.Top;
        if (IsRightExpansion(current, desired)) return ExpansionDirection.Right;
        if (IsLeftExpansion(current, desired)) return ExpansionDirection.Left;
        return ExpansionDirection.None;
    }

    private enum ExpansionDirection
    {
        None,
        Left,
        Right,
        Top,
        Bottom
    }

    private static ZoneStackResizeResult CompressBottom(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumHeight,
        double adjacencyTolerance)
    {
        if (!IsBottomExpansion(current, desired))
        {
            return Unchanged(desired, obstacles);
        }

        var indexes = FindAdjacent(obstacles, bounds =>
            Math.Abs(bounds.Y - (current.Bottom + gap)) <= adjacencyTolerance &&
            OverlapsHorizontally(desired, bounds));
        if (indexes.Length == 0)
        {
            return Unchanged(desired, obstacles);
        }

        var targetEdge = Math.Min(desired.Bottom,
            indexes.Min(index => obstacles[index].Bottom - minimumHeight - gap));
        if (targetEdge <= current.Bottom + Tolerance)
        {
            return Unchanged(desired, obstacles);
        }

        var adjusted = obstacles.ToArray();
        foreach (var index in indexes)
        {
            var obstacle = obstacles[index];
            var newTop = targetEdge + gap;
            adjusted[index] = obstacle with { Y = newTop, Height = obstacle.Bottom - newTop };
        }

        return new ZoneStackResizeResult(desired with { Height = targetEdge - desired.Y }, adjusted);
    }

    private static ZoneStackResizeResult CompressTop(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumHeight,
        double adjacencyTolerance)
    {
        var indexes = FindAdjacent(obstacles, bounds =>
            Math.Abs(bounds.Bottom + gap - current.Y) <= adjacencyTolerance &&
            OverlapsHorizontally(desired, bounds));
        if (indexes.Length == 0)
        {
            return Unchanged(desired, obstacles);
        }

        var targetEdge = Math.Max(desired.Y,
            indexes.Max(index => obstacles[index].Y + minimumHeight + gap));
        if (targetEdge >= current.Y - Tolerance)
        {
            return Unchanged(desired, obstacles);
        }

        var adjusted = obstacles.ToArray();
        foreach (var index in indexes)
        {
            var obstacle = obstacles[index];
            var newBottom = targetEdge - gap;
            adjusted[index] = obstacle with { Height = newBottom - obstacle.Y };
        }

        return new ZoneStackResizeResult(
            desired with { Y = targetEdge, Height = desired.Bottom - targetEdge }, adjusted);
    }

    private static ZoneStackResizeResult CompressRight(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumWidth,
        double adjacencyTolerance)
    {
        var indexes = FindAdjacent(obstacles, bounds =>
            Math.Abs(bounds.X - (current.Right + gap)) <= adjacencyTolerance &&
            OverlapsVertically(desired, bounds));
        if (indexes.Length == 0)
        {
            return Unchanged(desired, obstacles);
        }

        var targetEdge = Math.Min(desired.Right,
            indexes.Min(index => obstacles[index].Right - minimumWidth - gap));
        if (targetEdge <= current.Right + Tolerance)
        {
            return Unchanged(desired, obstacles);
        }

        var adjusted = obstacles.ToArray();
        foreach (var index in indexes)
        {
            var obstacle = obstacles[index];
            var newLeft = targetEdge + gap;
            adjusted[index] = obstacle with { X = newLeft, Width = obstacle.Right - newLeft };
        }

        return new ZoneStackResizeResult(desired with { Width = targetEdge - desired.X }, adjusted);
    }

    private static ZoneStackResizeResult CompressLeft(
        ZoneBounds current,
        ZoneBounds desired,
        IReadOnlyList<ZoneBounds> obstacles,
        double gap,
        double minimumWidth,
        double adjacencyTolerance)
    {
        var indexes = FindAdjacent(obstacles, bounds =>
            Math.Abs(bounds.Right + gap - current.X) <= adjacencyTolerance &&
            OverlapsVertically(desired, bounds));
        if (indexes.Length == 0)
        {
            return Unchanged(desired, obstacles);
        }

        var targetEdge = Math.Max(desired.X,
            indexes.Max(index => obstacles[index].X + minimumWidth + gap));
        if (targetEdge >= current.X - Tolerance)
        {
            return Unchanged(desired, obstacles);
        }

        var adjusted = obstacles.ToArray();
        foreach (var index in indexes)
        {
            var obstacle = obstacles[index];
            var newRight = targetEdge - gap;
            adjusted[index] = obstacle with { Width = newRight - obstacle.X };
        }

        return new ZoneStackResizeResult(
            desired with { X = targetEdge, Width = desired.Right - targetEdge }, adjusted);
    }

    private static int[] FindAdjacent(IReadOnlyList<ZoneBounds> obstacles, Func<ZoneBounds, bool> predicate) =>
        obstacles.Select((bounds, index) => (bounds, index))
            .Where(value => predicate(value.bounds))
            .Select(value => value.index)
            .ToArray();

    private static bool IsBottomExpansion(ZoneBounds current, ZoneBounds desired) =>
        NearlyEqual(current.X, desired.X) && NearlyEqual(current.Y, desired.Y) &&
        NearlyEqual(current.Width, desired.Width) && desired.Bottom > current.Bottom + Tolerance;

    private static bool IsTopExpansion(ZoneBounds current, ZoneBounds desired) =>
        NearlyEqual(current.X, desired.X) && NearlyEqual(current.Width, desired.Width) &&
        NearlyEqual(current.Bottom, desired.Bottom) && desired.Y < current.Y - Tolerance;

    private static bool IsRightExpansion(ZoneBounds current, ZoneBounds desired) =>
        NearlyEqual(current.X, desired.X) && NearlyEqual(current.Y, desired.Y) &&
        NearlyEqual(current.Height, desired.Height) && desired.Right > current.Right + Tolerance;

    private static bool IsLeftExpansion(ZoneBounds current, ZoneBounds desired) =>
        NearlyEqual(current.Y, desired.Y) && NearlyEqual(current.Height, desired.Height) &&
        NearlyEqual(current.Right, desired.Right) && desired.X < current.X - Tolerance;

    private static bool OverlapsHorizontally(ZoneBounds first, ZoneBounds second) =>
        first.X < second.Right - Tolerance && first.Right > second.X + Tolerance;

    private static bool OverlapsVertically(ZoneBounds first, ZoneBounds second) =>
        first.Y < second.Bottom - Tolerance && first.Bottom > second.Y + Tolerance;

    private static ZoneStackResizeResult Unchanged(ZoneBounds desired, IReadOnlyList<ZoneBounds> obstacles) =>
        new(desired, obstacles);

    private static bool NearlyEqual(double first, double second) => Math.Abs(first - second) < Tolerance;
}
