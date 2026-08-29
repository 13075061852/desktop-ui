using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Shapes;
using DeskNest.App.Controls;
using DeskNest.Core.Models;
using DeskNest.Core.Services;

namespace DeskNest.App;

// ---------------------------------------------------------------------------
// Zone splitter rails: hidden dashed lines living in the tight gap between
// flush neighbours. Hovering reveals the rail; dragging it squeezes the
// leading neighbour down to its minimum size first and then translates the
// whole pair, stopping at window edges or other zones. Side-by-side pairs
// get a vertical rail that resizes widths, stacked pairs get a horizontal
// rail that resizes heights.
// ---------------------------------------------------------------------------
public partial class MainWindow
{
    private const double SplitterMaxGap = 18;
    private const double SplitterMinimumOverlap = 60;

    private readonly Dictionary<Guid, ZoneCard> _zoneCardMap = new();
    private readonly Dictionary<string, ZoneSplitterRail> _zoneSplitters = new(StringComparer.Ordinal);
    private SplitterDragState? _splitterDrag;

    private sealed class ZoneSplitterRail
    {
        public required Thumb Strip;
        public required Line DashLine;
        public required Guid FirstId;
        public required Guid SecondId;
        public required bool IsHorizontalLine;
        public bool DragActive;
    }

    private sealed class SplitterDragState
    {
        public required ZoneSplitterRail Rail;
        public required bool IsHorizontalLine;
        public required double Gap;

        /// <summary>Every flush zone on the first side of the line (above a
        /// horizontal rail / left of a vertical rail). Collinear zones form
        /// one group: they grow with the line and shrink to their minimum
        /// first, so one line drags them all in lockstep.</summary>
        public required List<ZoneModel> Firsts;

        /// <summary>Every flush zone on the second side of the line; they
        /// shrink from the line, translate once at minimum, and grow when the
        /// line moves back.</summary>
        public required List<ZoneModel> Seconds;

        public List<ZoneBounds> FirstStarts { get; } = new();

        public List<ZoneBounds> SecondStarts { get; } = new();

        /// <summary>Total pointer travel since drag start. DragDelta reports per-event
        /// deltas, so they must be accumulated to get an absolute drag position.</summary>
        public double AccumulatedOffset;

        /// <summary>Every zone's bounds at drag start; at drag completion the
        /// zones that moved capture the new arrangement as their rest bounds.</summary>
        public Dictionary<Guid, ZoneBounds> StartSnapshot { get; } = new();
    }

    private const double RiderAttachTolerance = 3;

    private static ControlTemplate CreateInvisibleThumbTemplate()
    {
        var frame = new FrameworkElementFactory(typeof(Border));
        frame.SetBinding(Border.BackgroundProperty, new Binding("Background")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)
        });
        return new ControlTemplate(typeof(Thumb))
        {
            VisualTree = frame
        };
    }

    private void UpdateZoneSplitters()
    {
        if (_state.LayoutLocked || !_state.DesktopIconsHidden)
        {
            ClearZoneSplitters();
            return;
        }
        var zones = _state.Zones;
        var liveKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < zones.Count; i++)
        {
            for (var j = i + 1; j < zones.Count; j++)
            {
                var a = zones[i];
                var b = zones[j];
                if (a.IsCollapsed || b.IsCollapsed)
                {
                    continue;
                }

                var va = GetVisualBounds(a);
                var vb = GetVisualBounds(b);
                var overlapY = Math.Min(va.Bottom, vb.Bottom) - Math.Max(va.Y, vb.Y);
                var overlapX = Math.Min(va.Right, vb.Right) - Math.Max(va.X, vb.X);

                // Side-by-side neighbours produce a vertical rail; stacked
                // neighbours produce a horizontal rail.
                var gapRight = vb.X - va.Right;
                var gapLeft = va.X - vb.Right;
                if (overlapY >= SplitterMinimumOverlap && IsTightGap(gapRight))
                {
                    liveKeys.Add(EnsureSplitterRail(a, b, isHorizontalLine: false, va, vb, gapRight));
                }
                else if (overlapY >= SplitterMinimumOverlap && IsTightGap(gapLeft))
                {
                    liveKeys.Add(EnsureSplitterRail(b, a, isHorizontalLine: false, vb, va, gapLeft));
                }

                var gapDown = vb.Y - va.Bottom;
                var gapUp = va.Y - vb.Bottom;
                if (overlapX >= SplitterMinimumOverlap && IsTightGap(gapDown))
                {
                    liveKeys.Add(EnsureSplitterRail(a, b, isHorizontalLine: true, va, vb, gapDown));
                }
                else if (overlapX >= SplitterMinimumOverlap && IsTightGap(gapUp))
                {
                    liveKeys.Add(EnsureSplitterRail(b, a, isHorizontalLine: true, vb, va, gapUp));
                }
            }
        }

        foreach (var staleKey in _zoneSplitters.Keys.Where(key => !liveKeys.Contains(key)).ToList())
        {
            RemoveSplitterRail(staleKey);
        }
    }

    private static bool IsTightGap(double gap) => gap >= -2 && gap <= SplitterMaxGap;

    private string EnsureSplitterRail(
        ZoneModel first,
        ZoneModel second,
        bool isHorizontalLine,
        ZoneBounds boundsA,
        ZoneBounds boundsB,
        double gap)
    {
        var firstKey = first.Id.CompareTo(second.Id) < 0 ? first.Id : second.Id;
        var secondKey = first.Id.CompareTo(second.Id) < 0 ? second.Id : first.Id;
        var key = $"{firstKey}|{secondKey}|{(isHorizontalLine ? 'H' : 'V')}";
        if (!_zoneSplitters.TryGetValue(key, out var rail))
        {
            var accent = TryFindResource("AccentBrush") as Brush
                         ?? new SolidColorBrush(Color.FromRgb(118, 215, 196));
            var dashLine = new Line
            {
                Stroke = accent,
                StrokeThickness = 2.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Opacity = 0,
                IsHitTestVisible = false,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 4,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = Colors.Black
                }
            };
            var strip = new Thumb
            {
                Background = Brushes.Transparent,
                Tag = key,
                // The default system Thumb template paints a visible chrome over the hit
                // strip; an empty template keeps the rail purely a hover-revealed line.
                Template = CreateInvisibleThumbTemplate()
            };
            rail = new ZoneSplitterRail
            {
                Strip = strip,
                DashLine = dashLine,
                FirstId = first.Id,
                SecondId = second.Id,
                IsHorizontalLine = isHorizontalLine
            };
            strip.DragStarted += OnSplitterDragStarted;
            strip.DragDelta += OnSplitterDragDelta;
            strip.DragCompleted += OnSplitterDragCompleted;
            strip.MouseEnter += OnSplitterMouseEnter;
            strip.MouseLeave += OnSplitterMouseLeave;
            ZoneSplitterCanvas.Children.Add(dashLine);
            ZoneSplitterCanvas.Children.Add(strip);
            _zoneSplitters[key] = rail;
        }

        rail.IsHorizontalLine = isHorizontalLine;
        rail.FirstId = first.Id;
        rail.SecondId = second.Id;

        if (isHorizontalLine)
        {
            var railLeft = Math.Min(boundsA.X, boundsB.X) + 6;
            var railWidth = Math.Max(60, Math.Min(boundsA.Right, boundsB.Right) - railLeft - 6);
            var bandThickness = Math.Max(10, gap + 8);
            var midGap = boundsB.Y - gap / 2;

            SetRailGeometry(rail, railLeft, midGap - bandThickness / 2, railWidth, bandThickness);
            rail.Strip.Cursor = Cursors.SizeNS;
            rail.DashLine.X1 = railLeft + 4;
            rail.DashLine.X2 = railLeft + railWidth - 4;
            rail.DashLine.Y1 = midGap;
            rail.DashLine.Y2 = midGap;
        }
        else
        {
            var midGap = boundsB.X - gap / 2;
            var bandThickness = Math.Max(10, gap + 8);
            var railTop = Math.Min(boundsA.Y, boundsB.Y) + 6;
            var railHeight = Math.Max(60, Math.Min(boundsA.Bottom, boundsB.Bottom) - railTop - 6);

            SetRailGeometry(rail, midGap - bandThickness / 2, railTop, bandThickness, railHeight);
            rail.Strip.Cursor = Cursors.SizeWE;
            rail.DashLine.Y1 = railTop + 4;
            rail.DashLine.Y2 = railTop + railHeight - 4;
            rail.DashLine.X1 = midGap;
            rail.DashLine.X2 = midGap;
        }

        return key;
    }

    private static void SetRailGeometry(
        ZoneSplitterRail rail,
        double x,
        double y,
        double width,
        double height)
    {
        Canvas.SetLeft(rail.Strip, x);
        Canvas.SetTop(rail.Strip, y);
        rail.Strip.Width = width;
        rail.Strip.Height = height;
    }

    private void RemoveSplitterRail(string key)
    {
        if (!_zoneSplitters.Remove(key, out var rail))
        {
            return;
        }

        rail.Strip.DragStarted -= OnSplitterDragStarted;
        rail.Strip.DragDelta -= OnSplitterDragDelta;
        rail.Strip.DragCompleted -= OnSplitterDragCompleted;
        rail.Strip.MouseEnter -= OnSplitterMouseEnter;
        rail.Strip.MouseLeave -= OnSplitterMouseLeave;
        ZoneSplitterCanvas.Children.Remove(rail.Strip);
        ZoneSplitterCanvas.Children.Remove(rail.DashLine);
    }

    private void ClearZoneSplitters()
    {
        foreach (var key in _zoneSplitters.Keys.ToList())
        {
            RemoveSplitterRail(key);
        }

        ZoneSplitterCanvas.Children.Clear();
        _zoneSplitters.Clear();
        _splitterDrag = null;
    }

    private void OnSplitterMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Thumb { Tag: string key } && _zoneSplitters.TryGetValue(key, out var rail))
        {
            AnimateSplitterLine(rail, 0.9);
        }
    }

    private void OnSplitterMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Thumb { Tag: string key } && _zoneSplitters.TryGetValue(key, out var rail) && !rail.DragActive)
        {
            AnimateSplitterLine(rail, 0);
        }
    }

    private void AnimateSplitterLine(ZoneSplitterRail rail, double targetOpacity)
    {
        var current = rail.DashLine.Opacity;
        rail.DashLine.BeginAnimation(OpacityProperty, null);
        rail.DashLine.Opacity = current;
        rail.DashLine.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(current, targetOpacity, TimeSpan.FromMilliseconds(130)));
    }

    private void OnSplitterDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not Thumb { Tag: string key } || !_zoneSplitters.TryGetValue(key, out var rail))
        {
            return;
        }

        var first = _state.Zones.FirstOrDefault(zone => zone.Id == rail.FirstId);
        var second = _state.Zones.FirstOrDefault(zone => zone.Id == rail.SecondId);
        if (first is null || second is null)
        {
            return;
        }

        rail.DragActive = true;
        AnimateSplitterLine(rail, 0.9);
        var firstStart = GetVisualBounds(first);
        var secondStart = GetVisualBounds(second);
        var gap = rail.IsHorizontalLine
            ? secondStart.Y - firstStart.Bottom
            : secondStart.X - firstStart.Right;
        _splitterDrag = new SplitterDragState
        {
            Rail = rail,
            IsHorizontalLine = rail.IsHorizontalLine,
            Gap = Math.Max(2, gap),
            Firsts = [first],
            Seconds = [second]
        };
        _splitterDrag.FirstStarts.Add(firstStart);
        _splitterDrag.SecondStarts.Add(secondStart);
        foreach (var zone in _state.Zones)
        {
            _splitterDrag.StartSnapshot[zone.Id] = GetVisualBounds(zone);
        }

        var lineMidStart = rail.IsHorizontalLine
            ? secondStart.Y - _splitterDrag.Gap / 2
            : secondStart.X - _splitterDrag.Gap / 2;
        CollectFlushGroups(_splitterDrag, lineMidStart);
    }

    /// <summary>Grows the drag groups to a fixed point: every zone whose edge
    /// sits on the line AND whose band overlaps the already-collected members
    /// joins. Only those zones the moving line would actually collide with —
    /// zones in another row/column that merely align with the line coordinate
    /// stay put.</summary>
    private void CollectFlushGroups(SplitterDragState drag, double lineMidStart)
    {
        var firstEdge = lineMidStart - drag.Gap / 2;
        var secondEdge = lineMidStart + drag.Gap / 2;
        var isHorizontal = drag.IsHorizontalLine;
        var bandMin = double.MaxValue;
        var bandMax = double.MinValue;
        foreach (var start in drag.FirstStarts.Concat(drag.SecondStarts))
        {
            bandMin = Math.Min(bandMin, isHorizontal ? start.X : start.Y);
            bandMax = Math.Max(bandMax, isHorizontal ? start.Right : start.Bottom);
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var zone in _state.Zones)
            {
                if (zone.IsCollapsed || drag.Firsts.Contains(zone) || drag.Seconds.Contains(zone))
                {
                    continue;
                }

                var bounds = GetVisualBounds(zone);
                var memberMin = isHorizontal ? bounds.X : bounds.Y;
                var memberMax = isHorizontal ? bounds.Right : bounds.Bottom;
                if (memberMin >= bandMax - 0.01 || memberMax <= bandMin + 0.01)
                {
                    continue;
                }

                var firstEdgeCoord = isHorizontal ? bounds.Bottom : bounds.Right;
                var secondEdgeCoord = isHorizontal ? bounds.Y : bounds.X;
                if (Math.Abs(firstEdgeCoord - firstEdge) <= RiderAttachTolerance)
                {
                    drag.Firsts.Add(zone);
                    drag.FirstStarts.Add(bounds);
                }
                else if (Math.Abs(secondEdgeCoord - secondEdge) <= RiderAttachTolerance)
                {
                    drag.Seconds.Add(zone);
                    drag.SecondStarts.Add(bounds);
                }
                else
                {
                    continue;
                }

                bandMin = Math.Min(bandMin, memberMin);
                bandMax = Math.Max(bandMax, memberMax);
                changed = true;
            }
        }
    }

    private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_splitterDrag is not { } drag)
        {
            return;
        }

        var raw = drag.IsHorizontalLine ? e.VerticalChange : e.HorizontalChange;
        if (Math.Abs(raw) < 0.01)
        {
            e.Handled = true;
            return;
        }

        drag.AccumulatedOffset += raw;

        if (drag.IsHorizontalLine)
        {
            MoveStackedPair(drag, drag.AccumulatedOffset);
        }
        else
        {
            MoveSideBySidePair(drag, drag.AccumulatedOffset);
        }

        UpdateZoneSplitters();
        RequestSave();
        e.Handled = true;
    }

    private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_splitterDrag is { } drag)
        {
            drag.Rail.DragActive = false;
            AnimateSplitterLine(drag.Rail, 0);
            // A splitter drag is an explicit arrangement: every zone the user
            // moved (pair, riders, pushed bystanders) adopts the new bounds
            // as its home; then let zones pushed in earlier sessions relax.
            foreach (var zone in _state.Zones)
            {
                if (drag.StartSnapshot.TryGetValue(zone.Id, out var start) &&
                    !BoundsApproximatelyEqual(start, GetVisualBounds(zone)))
                {
                    zone.CaptureRestBounds();
                }
            }

            _splitterDrag = null;
            RelaxNeighboursTowardRest();
        }

        RequestSave();
    }

    /// <summary>Right edge a zone may reach (window width minus the desktop inset).</summary>
    private double MaximumZoneRight => Math.Max(
        ZoneCard.HorizontalDesktopInset + 1,
        ActualWidth - ZoneCard.HorizontalDesktopInset);

    /// <summary>Bottom edge a zone may reach: work area bottom (above the taskbar)
    /// minus the desktop inset, matching ConstrainZoneBounds.</summary>
    private double MaximumZoneBottom => Math.Max(
        73,
        Math.Min(ActualHeight, SystemParameters.WorkArea.Bottom - Top)
        - ZoneCard.HorizontalDesktopInset);

    /// <summary>
    /// Moves a side-by-side group along the horizontal axis. <paramref name="offset"/>
    /// is the absolute pointer travel since the drag started (positive = right).
    /// All flush zones on both sides of the line form one group; the group
    /// bounds, pushed bystanders and layout validity are resolved by
    /// <see cref="ZoneSplitterResolver.TryGroupLayout"/> and applied atomically.
    /// </summary>
    private void MoveSideBySidePair(SplitterDragState drag, double offset)
    {
        var midGapStart = drag.SecondStarts[0].X - drag.Gap / 2;
        var desiredMid = midGapStart + offset;
        var lineMid = ResolveGroupRailPosition(drag, desiredMid, movingForward: offset >= 0);

        TryGroupLayoutAndPush(drag, lineMid, movingForward: offset >= 0, apply: true);
    }

    /// <summary>Redistributes the vertical space of a stacked group: all
    /// flush zones on both sides of the line move in lockstep; the group
    /// bounds, pushed bystanders and layout validity are resolved by
    /// <see cref="ZoneSplitterResolver.TryGroupLayout"/> and applied atomically.</summary>
    private void MoveStackedPair(SplitterDragState drag, double offset)
    {
        var midGapStart = drag.SecondStarts[0].Y - drag.Gap / 2;
        var desiredMid = midGapStart + offset;
        var lineMid = ResolveGroupRailPosition(drag, desiredMid, movingForward: offset >= 0);

        TryGroupLayoutAndPush(drag, lineMid, movingForward: offset >= 0, apply: true);
    }

    /// <summary>
    /// Finds the extreme rail position the pointer may reach. The rail follows
    /// the pointer, but the layout must stay valid: the pair never covers a
    /// bystander and every zone keeps the 12px minimum gap (bystanders are
    /// pushed along in the drag direction). Binary search converges on the
    /// largest (or smallest) valid line position. The pure geometry lives in
    /// <see cref="ZoneSplitterResolver"/> so it stays unit-testable.
    /// </summary>
    private double ResolveGroupRailPosition(SplitterDragState drag, double desiredMid, bool movingForward)
    {
        return ZoneSplitterResolver.ResolveGroupRailPosition(
            drag.FirstStarts,
            drag.SecondStarts,
            CollectBystanderBounds(drag),
            drag.Gap,
            drag.IsHorizontalLine,
            desiredMid,
            movingForward,
            ZoneCard.HorizontalDesktopInset,
            72,
            MaximumZoneRight,
            MaximumZoneBottom);
    }

    private ZoneBounds[] CollectBystanderBounds(SplitterDragState drag)
    {
        return _state.Zones
            .Where(zone => !zone.IsCollapsed &&
                           !drag.Firsts.Contains(zone) &&
                           !drag.Seconds.Contains(zone))
            .Select(GetVisualBounds)
            .ToArray();
    }

    /// <summary>
    /// Simulates the layout for a candidate rail position and optionally applies
    /// it via <see cref="ZoneSplitterResolver.TryGroupLayout"/>.
    /// </summary>
    private bool TryGroupLayoutAndPush(SplitterDragState drag, double lineMid, bool movingForward, bool apply)
    {
        var bystanders = _state.Zones
            .Where(zone => !zone.IsCollapsed &&
                           !drag.Firsts.Contains(zone) &&
                           !drag.Seconds.Contains(zone))
            .Select(zone => (Zone: zone, Bounds: GetVisualBounds(zone)))
            .ToList();
        if (!ZoneSplitterResolver.TryGroupLayout(
                drag.FirstStarts,
                drag.SecondStarts,
                bystanders.Select(item => item.Bounds).ToArray(),
                drag.Gap,
                drag.IsHorizontalLine,
                lineMid,
                movingForward,
                ZoneCard.HorizontalDesktopInset,
                72,
                MaximumZoneRight,
                MaximumZoneBottom,
                out var layout))
        {
            return false;
        }

        if (!apply)
        {
            return true;
        }

        for (var index = 0; index < drag.Firsts.Count; index++)
        {
            ApplyZoneVisualBounds(drag.Firsts[index], layout.Firsts[index]);
        }

        for (var index = 0; index < drag.Seconds.Count; index++)
        {
            ApplyZoneVisualBounds(drag.Seconds[index], layout.Seconds[index]);
        }

        for (var index = 0; index < bystanders.Count; index++)
        {
            ApplyZoneVisualBounds(bystanders[index].Zone, layout.Bystanders[index]);
        }

        return true;
    }


    private static bool BoundsApproximatelyEqual(ZoneBounds first, ZoneBounds second) =>
        first.X.ApproximatelyEquals(second.X) &&
        first.Y.ApproximatelyEquals(second.Y) &&
        first.Width.ApproximatelyEquals(second.Width) &&
        first.Height.ApproximatelyEquals(second.Height);

    private void ApplyZoneVisualBounds(ZoneModel zone, ZoneBounds bounds)
    {
        var current = GetVisualBounds(zone);
        zone.X = bounds.X;
        zone.Y = bounds.Y;
        zone.Width = bounds.Width;
        if (!zone.IsCollapsed)
        {
            zone.Height = bounds.Height;
        }

        if (!_zoneCardMap.TryGetValue(zone.Id, out var card))
        {
            return;
        }

        Canvas.SetLeft(card, zone.X);
        Canvas.SetTop(card, zone.Y);
        card.Width = zone.Width;
        card.Height = zone.IsCollapsed ? 52 : zone.Height;
        if (zone.ViewMode == "List" && Math.Abs(current.Width - bounds.Width) >= 0.01)
        {
            card.RefreshItems();
        }
    }

}

internal static class SplitterMathExtensions
{
    public static bool ApproximatelyEquals(this double value, double other)
        => Math.Abs(value - other) < 0.01;
}
