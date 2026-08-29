namespace DeskNest.Core.Services;

/// <summary>Simulated splitter-rail layout for a group of flush zones: every
/// zone on the first side and second side of the line, plus the bystander
/// bounds, parallel to the bystander list the caller passed in.</summary>
public sealed record ZoneSplitterGroupLayout(
    IReadOnlyList<ZoneBounds> Firsts,
    IReadOnlyList<ZoneBounds> Seconds,
    IReadOnlyList<ZoneBounds> Bystanders);

/// <summary>Simulated splitter-rail layout: the dragged pair bounds plus the
/// bystander bounds, parallel to the bystander list the caller passed in.</summary>
public sealed record ZoneSplitterLayout(
    ZoneBounds First,
    ZoneBounds Second,
    IReadOnlyList<ZoneBounds> Bystanders);

/// <summary>
/// Pure geometry for the splitter rails between flush zone neighbours. A rail
/// travels on the drag axis - X for a side-by-side pair (vertical rail), Y for
/// a stacked pair (horizontal rail). The rail drags every flush zone on both
/// sides as one group ("one line drags many"), never covers a bystander and
/// every zone keeps the minimum gap: bystanders standing in the drag path are
/// pushed along the drag axis (cascading), and any push that would run out of
/// room makes the whole layout invalid, which lets the caller search for the
/// extreme rail position that still fits.
/// </summary>
public static class ZoneSplitterResolver
{
    public const double MinimumWidth = 150;
    public const double MinimumHeight = 150;
    public const double Gap = 12;

    /// <summary>
    /// Finds the extreme rail position the pointer may reach for a group drag.
    /// The rail follows the pointer, but the layout must stay valid. Binary
    /// search converges on the largest (or smallest) valid line position.
    /// </summary>
    public static double ResolveGroupRailPosition(
        IReadOnlyList<ZoneBounds> firsts,
        IReadOnlyList<ZoneBounds> seconds,
        IReadOnlyList<ZoneBounds> bystanders,
        double gap,
        bool isHorizontalLine,
        double desiredMid,
        bool movingForward,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom)
    {
        // The rail origin lives on the drag axis. Mixing the axes here made a
        // stacked rail compare the desired Y against an X coordinate, so the
        // line jumped to a bogus position and threw the top zone off-screen.
        var secondEdge = isHorizontalLine ? seconds[0].Y : seconds[0].X;
        var midGapStart = secondEdge - gap / 2;

        if (movingForward)
        {
            if (desiredMid <= midGapStart + 0.01)
            {
                return midGapStart;
            }

            var forwardLimit = isHorizontalLine
                ? maximumBottom - MinimumHeight
                : maximumRight - MinimumWidth;
            var high = Math.Min(desiredMid, forwardLimit - gap / 2);
            if (high <= midGapStart + 0.01)
            {
                return midGapStart;
            }

            var low = midGapStart;
            if (TryGroupLayout(
                    firsts, seconds, bystanders, gap, isHorizontalLine,
                    high, true, minimumX, minimumY, maximumRight, maximumBottom, out _))
            {
                return high;
            }

            for (var iteration = 0; iteration < 32; iteration++)
            {
                var mid = (low + high) / 2;
                if (TryGroupLayout(
                        firsts, seconds, bystanders, gap, isHorizontalLine,
                        mid, true, minimumX, minimumY, maximumRight, maximumBottom, out _))
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            return low;
        }

        if (desiredMid >= midGapStart - 0.01)
        {
            return midGapStart;
        }

        var backwardLimit = isHorizontalLine ? minimumY : minimumX;
        var backwardSpan = isHorizontalLine ? MinimumHeight : MinimumWidth;
        var backwardLow = Math.Max(desiredMid, backwardLimit + backwardSpan + gap / 2);
        if (backwardLow >= midGapStart - 0.01)
        {
            return midGapStart;
        }

        var backwardHigh = midGapStart;
        if (TryGroupLayout(
                firsts, seconds, bystanders, gap, isHorizontalLine,
                backwardLow, false, minimumX, minimumY, maximumRight, maximumBottom, out _))
        {
            return backwardLow;
        }

        for (var iteration = 0; iteration < 32; iteration++)
        {
            var mid = (backwardLow + backwardHigh) / 2;
            if (TryGroupLayout(
                    firsts, seconds, bystanders, gap, isHorizontalLine,
                    mid, false, minimumX, minimumY, maximumRight, maximumBottom, out _))
            {
                backwardLow = mid;
            }
            else
            {
                backwardHigh = mid;
            }
        }

        return backwardHigh;
    }

    /// <summary>Single-pair overload kept for callers and tests that drag one
    /// flush pair; delegates to the group resolver.</summary>
    public static double ResolveRailPosition(
        ZoneBounds firstStart,
        ZoneBounds secondStart,
        IReadOnlyList<ZoneBounds> bystanders,
        double gap,
        bool isHorizontalLine,
        double desiredMid,
        bool movingForward,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom) => ResolveGroupRailPosition(
        [firstStart],
        [secondStart],
        bystanders,
        gap,
        isHorizontalLine,
        desiredMid,
        movingForward,
        minimumX,
        minimumY,
        maximumRight,
        maximumBottom);

    /// <summary>
    /// Simulates the layout for a candidate rail position across a group of
    /// flush zones. Every first keeps its outer edge anchored and grows with
    /// the line; every second shrinks to its minimum size and then translates
    /// (pushed along). Bystanders in the drag path are pushed far enough to
    /// preserve the gap.
    /// <para>The start layout is the baseline: per-zone offsets from the line
    /// are preserved (no teleporting of tolerance-collected members), zones
    /// are never stretched past their start size, and every pair must keep at
    /// least its start clearance — so an imperfect start state can never jam
    /// the rail, it just limits how far it travels.</para>
    /// </summary>
    public static bool TryGroupLayout(
        IReadOnlyList<ZoneBounds> firsts,
        IReadOnlyList<ZoneBounds> seconds,
        IReadOnlyList<ZoneBounds> bystanders,
        double gap,
        bool isHorizontalLine,
        double lineMid,
        bool movingForward,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom,
        out ZoneSplitterGroupLayout layout)
    {
        layout = new ZoneSplitterGroupLayout(firsts, seconds, bystanders);

        // The line position the untouched start layout corresponds to; the
        // per-zone offsets are measured against it so nothing teleports.
        var midGapStart = (isHorizontalLine ? seconds[0].Y : seconds[0].X) - gap / 2;

        var firstRects = new List<ZoneBounds>(firsts.Count);
        foreach (var first in firsts)
        {
            // A zone clamped below the standard minimum (expand limit) must
            // not be stretched by the rail; its floor is its start size.
            var floor = Math.Min(isHorizontalLine ? MinimumHeight : MinimumWidth,
                isHorizontalLine ? first.Height : first.Width);
            if (isHorizontalLine)
            {
                var offset = first.Bottom - (midGapStart - gap / 2);
                var firstBottom = lineMid - gap / 2 + offset;
                firstRects.Add(new ZoneBounds(
                    first.X,
                    first.Y,
                    first.Width,
                    Math.Max(floor, firstBottom - first.Y)));
            }
            else
            {
                var offset = first.Right - (midGapStart - gap / 2);
                var firstRight = lineMid - gap / 2 + offset;
                firstRects.Add(new ZoneBounds(
                    first.X,
                    first.Y,
                    Math.Max(floor, firstRight - first.X),
                    first.Height));
            }
        }

        var secondRects = new List<ZoneBounds>(seconds.Count);
        foreach (var second in seconds)
        {
            var floor = Math.Min(isHorizontalLine ? MinimumHeight : MinimumWidth,
                isHorizontalLine ? second.Height : second.Width);
            if (isHorizontalLine)
            {
                var offset = second.Y - (midGapStart + gap / 2);
                var secondTop = lineMid + gap / 2 + offset;
                var secondBottom = Math.Max(second.Bottom, secondTop + floor);
                secondRects.Add(new ZoneBounds(second.X, secondTop, second.Width, secondBottom - secondTop));
            }
            else
            {
                var offset = second.X - (midGapStart + gap / 2);
                var secondLeft = lineMid + gap / 2 + offset;
                var secondRight = Math.Max(second.Right, secondLeft + floor);
                secondRects.Add(new ZoneBounds(secondLeft, second.Y, secondRight - secondLeft, second.Height));
            }
        }

        foreach (var rect in firstRects.Concat(secondRects))
        {
            // All four sides are checked in absolute canvas coordinates for
            // either orientation: comparing an X value against the Y-axis
            // limit (or vice versa) made the layout permanently invalid for
            // wide stacked pairs, which jammed the rail or let it jump.
            if (rect.X < minimumX - 0.01 ||
                rect.Y < minimumY - 0.01 ||
                rect.Right > maximumRight + 0.01 ||
                rect.Bottom > maximumBottom + 0.01)
            {
                return false;
            }
        }

        var adjusted = bystanders.ToArray();
        var block = new List<ZoneBounds>();
        block.AddRange(firstRects);
        block.AddRange(secondRects);

        for (var iteration = 0; iteration <= adjusted.Length; iteration++)
        {
            var changed = false;
            for (var index = 0; index < adjusted.Length; index++)
            {
                var bounds = adjusted[index];
                double? binding = null;
                foreach (var member in block)
                {
                    // The band runs along the line: a horizontal rail pushes
                    // zones sharing its X band, a vertical rail pushes zones
                    // sharing its Y band. The push itself travels on the drag
                    // axis (Y for horizontal, X for vertical).
                    var bandOverlap = isHorizontalLine
                        ? bounds.X < member.Right && bounds.Right > member.X
                        : bounds.Y < member.Bottom && bounds.Bottom > member.Y;
                    if (!bandOverlap)
                    {
                        continue;
                    }

                    if (isHorizontalLine)
                    {
                        if (movingForward)
                        {
                            if (bounds.Y >= member.Bottom - 0.01 &&
                                (binding is null || member.Bottom > binding.Value))
                            {
                                binding = member.Bottom;
                            }
                        }
                        else if (bounds.Bottom <= member.Y + 0.01 &&
                                 (binding is null || member.Y < binding.Value))
                        {
                            binding = member.Y;
                        }
                    }
                    else if (movingForward)
                    {
                        if (bounds.X >= member.Right - 0.01 &&
                            (binding is null || member.Right > binding.Value))
                        {
                            binding = member.Right;
                        }
                    }
                    else if (bounds.Right <= member.X + 0.01 &&
                             (binding is null || member.X < binding.Value))
                    {
                        binding = member.X;
                    }
                }

                if (binding is null)
                {
                    continue;
                }

                if (isHorizontalLine)
                {
                    if (movingForward)
                    {
                        var required = binding.Value + gap;
                        if (bounds.Y < required - 0.01)
                        {
                            if (required > maximumBottom - bounds.Height + 0.01)
                            {
                                return false;
                            }

                            bounds = bounds with { Y = required };
                            adjusted[index] = bounds;
                            block.Add(bounds);
                            changed = true;
                        }
                    }
                    else
                    {
                        var required = binding.Value - gap - bounds.Height;
                        if (bounds.Y > required + 0.01)
                        {
                            if (required < minimumY - 0.01)
                            {
                                return false;
                            }

                            bounds = bounds with { Y = required };
                            adjusted[index] = bounds;
                            block.Add(bounds);
                            changed = true;
                        }
                    }
                }
                else if (movingForward)
                {
                    var required = binding.Value + gap;
                    if (bounds.X < required - 0.01)
                    {
                        if (required > maximumRight - bounds.Width + 0.01)
                        {
                            return false;
                        }

                        bounds = bounds with { X = required };
                        adjusted[index] = bounds;
                        block.Add(bounds);
                        changed = true;
                    }
                }
                else
                {
                    var required = binding.Value - gap - bounds.Width;
                    if (bounds.X > required + 0.01)
                    {
                        if (required < minimumX - 0.01)
                        {
                            return false;
                        }

                        bounds = bounds with { X = required };
                        adjusted[index] = bounds;
                        block.Add(bounds);
                        changed = true;
                    }
                }
            }

            if (!changed)
            {
                break;
            }
        }

        // Final validation: every pair keeps at least its start clearance.
        // The start layout is the baseline — pairs that already sit closer
        // than the standard gap (startup clamping, expand limits) must not
        // be made worse, but must not freeze the rail either; normal pairs
        // keep the full minimum gap.
        var starts = new List<ZoneBounds>(firsts.Count + seconds.Count + bystanders.Count);
        starts.AddRange(firsts);
        starts.AddRange(seconds);
        starts.AddRange(bystanders);
        var all = new List<ZoneBounds>(starts.Count);
        all.AddRange(firstRects);
        all.AddRange(secondRects);
        all.AddRange(adjusted);
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                var startClear = Clearance(starts[i], starts[j]);
                var required = Math.Min(gap, startClear);
                if (Clearance(all[i], all[j]) < required - 0.01)
                {
                    return false;
                }
            }
        }

        layout = new ZoneSplitterGroupLayout(firstRects, secondRects, adjusted);
        return true;
    }

    /// <summary>The largest gap between two rects along either axis; negative
    /// when they overlap.</summary>
    private static double Clearance(ZoneBounds a, ZoneBounds b) =>
        Math.Max(
            Math.Max(b.X - a.Right, a.X - b.Right),
            Math.Max(b.Y - a.Bottom, a.Y - b.Bottom));

    /// <summary>Single-pair overload kept for callers and tests; delegates to
    /// the group layout.</summary>
    public static bool TryLayout(
        ZoneBounds firstStart,
        ZoneBounds secondStart,
        IReadOnlyList<ZoneBounds> bystanders,
        double gap,
        bool isHorizontalLine,
        double lineMid,
        bool movingForward,
        double minimumX,
        double minimumY,
        double maximumRight,
        double maximumBottom,
        out ZoneSplitterLayout layout)
    {
        layout = new ZoneSplitterLayout(firstStart, secondStart, bystanders);
        if (!TryGroupLayout(
                [firstStart],
                [secondStart],
                bystanders,
                gap,
                isHorizontalLine,
                lineMid,
                movingForward,
                minimumX,
                minimumY,
                maximumRight,
                maximumBottom,
                out var group))
        {
            return false;
        }

        layout = new ZoneSplitterLayout(group.Firsts[0], group.Seconds[0], group.Bystanders);
        return true;
    }
}
