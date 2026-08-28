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
    private const double SplitterMinimumWidth = 150;
    private const double SplitterMinimumHeight = 150;
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

    /// <summary>A bystander zone whose edge sits on the splitter line; it rides
    /// along with the line so the whole column/row stays aligned.</summary>
    private sealed class SplitterRider
    {
        public required ZoneModel Zone;

        /// <summary>Line position at the moment this rider attached; only the
        /// movement accumulated afterwards moves the rider.</summary>
        public double AppliedLineMid;
    }

    private sealed class SplitterDragState
    {
        public required ZoneSplitterRail Rail;
        public required ZoneModel First;
        public required ZoneModel Second;
        public required bool IsHorizontalLine;
        public required double Gap;
        public required ZoneBounds FirstStart;
        public required ZoneBounds SecondStart;

        /// <summary>Total pointer travel since drag start. DragDelta reports per-event
        /// deltas, so they must be accumulated to get an absolute drag position.</summary>
        public double AccumulatedOffset;

        /// <summary>Bystander zones riding the line; collected at drag start and
        /// whenever the moving line sweeps over another zone's edge.</summary>
        public List<SplitterRider> Riders { get; } = new();
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
            var bandLeft = Math.Max(boundsA.X, boundsB.X);
            var bandRight = Math.Min(boundsA.Right, boundsB.Right);
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
        var gap = rail.IsHorizontalLine
            ? GetVisualBounds(second).Y - GetVisualBounds(first).Bottom
            : GetVisualBounds(second).X - GetVisualBounds(first).Right;
        _splitterDrag = new SplitterDragState
        {
            Rail = rail,
            First = first,
            Second = second,
            IsHorizontalLine = rail.IsHorizontalLine,
            Gap = Math.Max(2, gap),
            FirstStart = GetVisualBounds(first),
            SecondStart = GetVisualBounds(second)
        };
        var lineMidStart = rail.IsHorizontalLine
            ? GetVisualBounds(second).Y - _splitterDrag.Gap / 2
            : GetVisualBounds(second).X - _splitterDrag.Gap / 2;
        CollectRiders(_splitterDrag, lineMidStart);
    }

    private void CollectRiders(SplitterDragState drag, double lineMidStart)
    {
        var secondEdge = lineMidStart + drag.Gap / 2;
        var firstEdge = lineMidStart - drag.Gap / 2;
        foreach (var zone in _state.Zones)
        {
            TryAttachRider(drag, zone, secondEdge, firstEdge, lineMidStart);
        }
    }

    private void TryAttachRider(
        SplitterDragState drag,
        ZoneModel zone,
        double secondEdge,
        double firstEdge,
        double currentLineMid)
    {
        if (zone.Id == drag.First.Id || zone.Id == drag.Second.Id || zone.IsCollapsed)
        {
            return;
        }

        if (drag.Riders.Any(rider => rider.Zone.Id == zone.Id))
        {
            return;
        }

        var bounds = GetVisualBounds(zone);
        var onSecondSide = Math.Abs(
            (drag.IsHorizontalLine ? bounds.Y : bounds.X) - secondEdge) <= RiderAttachTolerance;
        var onFirstSide = !onSecondSide && Math.Abs(
            (drag.IsHorizontalLine ? bounds.Bottom : bounds.Right) - firstEdge) <= RiderAttachTolerance;
        if (onSecondSide || onFirstSide)
        {
            drag.Riders.Add(new SplitterRider
            {
                Zone = zone,
                AppliedLineMid = currentLineMid
            });
        }
    }

    private void MoveRidersWithLine(SplitterDragState drag, double lineMid, double shift)
    {
        // The line is a full-height/width grid line: any zone whose edge sits
        // on it - from drag start or swept over mid-drag - rides along with
        // the line, keeping its relative position. Riders follow the LINE, not
        // just the translation: during the squeeze phase the line moves while
        // the pair's outer edges stay anchored, and a rider left behind would
        // be covered by the growing neighbour (the reported overlap).
        var secondEdge = lineMid + drag.Gap / 2;
        var firstEdge = lineMid - drag.Gap / 2;
        foreach (var zone in _state.Zones)
        {
            TryAttachRider(drag, zone, secondEdge, firstEdge, lineMid);
        }

        foreach (var rider in drag.Riders)
        {
            var delta = lineMid - rider.AppliedLineMid;
            if (Math.Abs(delta) < 0.01)
            {
                continue;
            }

            var bounds = GetVisualBounds(rider.Zone);
            ZoneBounds candidate;
            if (drag.IsHorizontalLine)
            {
                var y = Math.Clamp(
                    bounds.Y + delta,
                    72,
                    Math.Max(73, MaximumZoneBottom - bounds.Height));
                candidate = new ZoneBounds(bounds.X, y, bounds.Width, bounds.Height);
            }
            else
            {
                var x = Math.Clamp(
                    bounds.X + delta,
                    ZoneCard.HorizontalDesktopInset,
                    Math.Max(ZoneCard.HorizontalDesktopInset + 1, MaximumZoneRight - bounds.Width));
                candidate = new ZoneBounds(x, bounds.Y, bounds.Width, bounds.Height);
            }

            // If the ride ran out of room (everything piled at a boundary),
            // stay put and keep following once space frees up again.
            var blocked = _state.Zones.Any(other =>
                other.Id != rider.Zone.Id &&
                !other.IsCollapsed &&
                Intersects(candidate, GetVisualBounds(other)));
            if (blocked)
            {
                continue;
            }

            rider.AppliedLineMid = lineMid;
            ApplyZoneVisualBounds(rider.Zone, candidate);
        }
    }

    private static bool Intersects(ZoneBounds first, ZoneBounds second) =>
        first.X < second.Right - ToleranceValue &&
        first.Right > second.X + ToleranceValue &&
        first.Y < second.Bottom - ToleranceValue &&
        first.Bottom > second.Y + ToleranceValue;

    private const double ToleranceValue = 0.01;

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
            _splitterDrag = null;
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
    /// Moves a side-by-side pair along the horizontal axis. <paramref name="offset"/>
    /// is the absolute pointer travel since the drag started (positive = right).
    /// Phase 1 redistributes space with the pair's outer edges anchored; once
    /// the squeezed zone reaches its minimum width, phase 2 translates the
    /// whole pair until the moving outer edge reaches the window boundary.
    /// </summary>
    private void MoveSideBySidePair(SplitterDragState drag, double offset)
    {
        var first = drag.FirstStart;
        var second = drag.SecondStart;

        var midGapStart = second.X - drag.Gap / 2;
        var desiredMid = midGapStart + offset;
        var lineMid = ResolveRailPosition(drag, desiredMid, movingForward: offset >= 0);

        // The rail is the inner boundary: the zone on the drag side keeps its outer
        // edge anchored and grows with the rail; the other zone shrinks to its
        // minimum and then translates (pushed along), so a bystander like
        // 文本文档 never shifts as a whole when its right rail is dragged.
        ZoneBounds newFirst;
        ZoneBounds newSecond;
        if (offset >= 0)
        {
            var secondLeft = lineMid + drag.Gap / 2;
            var secondRight = Math.Max(second.Right, secondLeft + SplitterMinimumWidth);
            newFirst = new ZoneBounds(
                first.X,
                first.Y,
                Math.Max(SplitterMinimumWidth, lineMid - drag.Gap / 2 - first.X),
                first.Height);
            newSecond = new ZoneBounds(secondLeft, second.Y, secondRight - secondLeft, second.Height);
        }
        else
        {
            var firstRight = lineMid - drag.Gap / 2;
            var firstLeft = Math.Min(first.X, firstRight - SplitterMinimumWidth);
            newFirst = new ZoneBounds(firstLeft, first.Y, firstRight - firstLeft, first.Height);
            newSecond = new ZoneBounds(
                second.X,
                second.Y,
                Math.Max(SplitterMinimumWidth, lineMid + drag.Gap / 2 - second.X),
                second.Height);
        }

        if (BoundsApproximatelyEqual(newFirst, GetVisualBounds(drag.First)) &&
            BoundsApproximatelyEqual(newSecond, GetVisualBounds(drag.Second)))
        {
            return;
        }

        ApplyZoneVisualBounds(drag.First, newFirst);
        ApplyZoneVisualBounds(drag.Second, newSecond);
        TryLayoutAndPush(drag, lineMid, movingForward: offset >= 0, apply: true);
        MoveRidersWithLine(drag, lineMid, 0);
    }

    /// <summary>Redistributes the vertical space of a stacked pair; the top zone
    /// keeps its top edge anchored and grows with the rail, the bottom zone
    /// shrinks to its minimum and then translates (pushed along).</summary>
    private void MoveStackedPair(SplitterDragState drag, double offset)
    {
        var first = drag.FirstStart;
        var second = drag.SecondStart;

        var midGapStart = second.Y - drag.Gap / 2;
        var desiredMid = midGapStart + offset;
        var lineMid = ResolveRailPosition(drag, desiredMid, movingForward: offset >= 0);

        ZoneBounds newFirst;
        ZoneBounds newSecond;
        if (offset >= 0)
        {
            var secondTop = lineMid + drag.Gap / 2;
            var secondBottom = Math.Max(second.Bottom, secondTop + SplitterMinimumHeight);
            newFirst = new ZoneBounds(
                first.X,
                first.Y,
                first.Width,
                Math.Max(SplitterMinimumHeight, lineMid - drag.Gap / 2 - first.Y));
            newSecond = new ZoneBounds(second.X, secondTop, second.Width, secondBottom - secondTop);
        }
        else
        {
            var firstBottom = lineMid - drag.Gap / 2;
            var firstTop = Math.Min(first.Y, firstBottom - SplitterMinimumHeight);
            newFirst = new ZoneBounds(first.X, firstTop, first.Width, firstBottom - firstTop);
            newSecond = new ZoneBounds(
                second.X,
                second.Y,
                second.Width,
                Math.Max(SplitterMinimumHeight, lineMid + drag.Gap / 2 - second.Y));
        }

        if (BoundsApproximatelyEqual(newFirst, GetVisualBounds(drag.First)) &&
            BoundsApproximatelyEqual(newSecond, GetVisualBounds(drag.Second)))
        {
            return;
        }

        ApplyZoneVisualBounds(drag.First, newFirst);
        ApplyZoneVisualBounds(drag.Second, newSecond);
        TryLayoutAndPush(drag, lineMid, movingForward: offset >= 0, apply: true);
        MoveRidersWithLine(drag, lineMid, 0);
    }

    /// <summary>
    /// Finds the extreme rail position the pointer may reach. The rail follows
    /// the pointer, but the layout must stay valid: the pair never covers a
    /// bystander and every zone keeps the 12px minimum gap (bystanders are
    /// pushed along in the drag direction). Binary search converges on the
    /// largest (or smallest) valid line position.
    /// </summary>
    private double ResolveRailPosition(SplitterDragState drag, double desiredMid, bool movingForward)
    {
        var first = drag.FirstStart;
        var second = drag.SecondStart;
        var midGapStart = second.X - drag.Gap / 2;

        if (movingForward)
        {
            if (desiredMid <= midGapStart + 0.01)
            {
                return midGapStart;
            }

            var high = Math.Min(desiredMid, MaximumZoneRight - SplitterMinimumWidth - drag.Gap / 2);
            if (high <= midGapStart + 0.01)
            {
                return midGapStart;
            }

            var low = midGapStart;
            if (TryLayoutAndPush(drag, high, true, apply: false))
            {
                return high;
            }

            for (var iteration = 0; iteration < 32; iteration++)
            {
                var mid = (low + high) / 2;
                if (TryLayoutAndPush(drag, mid, true, apply: false))
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

        var backwardLow = Math.Max(
            desiredMid,
            ZoneCard.HorizontalDesktopInset + SplitterMinimumWidth + drag.Gap / 2);
        if (backwardLow >= midGapStart - 0.01)
        {
            return midGapStart;
        }

        var backwardHigh = midGapStart;
        if (TryLayoutAndPush(drag, backwardLow, false, apply: false))
        {
            return backwardLow;
        }

        for (var iteration = 0; iteration < 32; iteration++)
        {
            var mid = (backwardLow + backwardHigh) / 2;
            if (TryLayoutAndPush(drag, mid, false, apply: false))
            {
                backwardHigh = mid;
            }
            else
            {
                backwardLow = mid;
            }
        }

        return backwardHigh;
    }

    /// <summary>
    /// Limit for the squeeze phase (line position mid). The pair's growing zone
    /// must stop 12px before the nearest bystander zone in its own row/column
    /// band, otherwise the squeeze would cover that zone before the translation
    /// phase even starts.
    /// </summary>
    /// <summary>
    /// Simulates the layout for a candidate rail position and optionally applies
    /// it. The pair never covers a bystander and every zone keeps the 12px
    /// minimum gap: bystanders standing in the drag path are pushed along the
    /// drag axis (cascading), and any push that would run out of room makes the
    /// whole layout invalid, which lets the caller search for the extreme rail
    /// position that still fits.
    /// </summary>
    private bool TryLayoutAndPush(SplitterDragState drag, double lineMid, bool movingForward, bool apply)
    {
        const double gap = 12;
        var first = drag.FirstStart;
        var second = drag.SecondStart;

        ZoneBounds firstRect;
        ZoneBounds secondRect;
        if (drag.IsHorizontalLine)
        {
            var firstBottom = lineMid - drag.Gap / 2;
            var secondTop = lineMid + drag.Gap / 2;
            var secondBottom = Math.Max(second.Bottom, secondTop + SplitterMinimumHeight);
            firstRect = new ZoneBounds(
                first.X,
                first.Y,
                first.Width,
                Math.Max(SplitterMinimumHeight, firstBottom - first.Y));
            secondRect = new ZoneBounds(second.X, secondTop, second.Width, secondBottom - secondTop);
        }
        else
        {
            var firstRight = lineMid - drag.Gap / 2;
            var secondLeft = lineMid + drag.Gap / 2;
            var secondRight = Math.Max(second.Right, secondLeft + SplitterMinimumWidth);
            firstRect = new ZoneBounds(
                first.X,
                first.Y,
                Math.Max(SplitterMinimumWidth, firstRight - first.X),
                first.Height);
            secondRect = new ZoneBounds(secondLeft, second.Y, secondRight - secondLeft, second.Height);
        }

        if (firstRect.Width < SplitterMinimumWidth - 0.01 ||
            firstRect.Height < SplitterMinimumHeight - 0.01 ||
            secondRect.Width < SplitterMinimumWidth - 0.01 ||
            secondRect.Height < SplitterMinimumHeight - 0.01)
        {
            return false;
        }

        var boundaryMax = drag.IsHorizontalLine ? MaximumZoneBottom : MaximumZoneRight;
        if (firstRect.X < ZoneCard.HorizontalDesktopInset - 0.01 ||
            firstRect.Y < 72 - 0.01 ||
            secondRect.Right > boundaryMax + 0.01 ||
            secondRect.Bottom > MaximumZoneBottom + 0.01)
        {
            return false;
        }

        var bystanders = _state.Zones
            .Where(zone => !zone.IsCollapsed &&
                           zone.Id != drag.First.Id &&
                           zone.Id != drag.Second.Id &&
                           !drag.Riders.Any(rider => rider.Zone.Id == zone.Id))
            .Select(zone => (Zone: zone, Bounds: GetVisualBounds(zone)))
            .ToList();
        var block = new List<ZoneBounds> { firstRect, secondRect };
        if (apply)
        {
            ApplyZoneVisualBounds(drag.First, firstRect);
            ApplyZoneVisualBounds(drag.Second, secondRect);
        }

        for (var iteration = 0; iteration <= bystanders.Count; iteration++)
        {
            var changed = false;
            for (var index = 0; index < bystanders.Count; index++)
            {
                var (zone, bounds) = bystanders[index];
                double? binding = null;
                foreach (var member in block)
                {
                    var bandOverlap = drag.IsHorizontalLine
                        ? bounds.X < member.Right && bounds.Right > member.X
                        : bounds.Y < member.Bottom && bounds.Bottom > member.Y;
                    if (!bandOverlap)
                    {
                        continue;
                    }

                    if (movingForward)
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

                if (movingForward)
                {
                    var required = binding.Value + gap;
                    if (bounds.X < required - 0.01)
                    {
                        var newX = required;
                        if (newX > boundaryMax - bounds.Width + 0.01)
                        {
                            return false;
                        }

                        bounds = bounds with { X = newX };
                        if (apply)
                        {
                            ApplyZoneVisualBounds(zone, bounds);
                        }

                        bystanders[index] = (zone, bounds);
                        block.Add(bounds);
                        changed = true;
                    }
                }
                else
                {
                    var required = binding.Value - gap - bounds.Width;
                    if (bounds.X > required + 0.01)
                    {
                        var newX = required;
                        if (newX < ZoneCard.HorizontalDesktopInset - 0.01)
                        {
                            return false;
                        }

                        bounds = bounds with { X = newX };
                        if (apply)
                        {
                            ApplyZoneVisualBounds(zone, bounds);
                        }

                        bystanders[index] = (zone, bounds);
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

        // Final validation: every pair of zones keeps the minimum gap.
        var all = new List<ZoneBounds> { firstRect, secondRect };
        all.AddRange(bystanders.Select(item => item.Bounds));
        for (var i = 0; i < all.Count; i++)
        {
            for (var j = i + 1; j < all.Count; j++)
            {
                var a = all[i];
                var b = all[j];
                var clear = a.Right + gap <= b.X + 0.01 ||
                            b.Right + gap <= a.X + 0.01 ||
                            a.Bottom + gap <= b.Y + 0.01 ||
                            b.Bottom + gap <= a.Y + 0.01;
                if (!clear)
                {
                    return false;
                }
            }
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
