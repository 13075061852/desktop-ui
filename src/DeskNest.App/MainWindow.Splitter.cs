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
    }

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
            _splitterDrag = null;
        }

        RequestSave();
    }

    /// <summary>
    /// Moves a side-by-side pair along the horizontal axis. <paramref name="offset"/>
    /// is the absolute pointer travel since the drag started (positive = right).
    /// The line first squeezes the neighbour it moves into down to the minimum,
    /// then translates the whole pair until an edge or another zone blocks it.
    /// </summary>
    private void MoveSideBySidePair(SplitterDragState drag, double offset)
    {
        var first = drag.FirstStart;
        var second = drag.SecondStart;
        var newSecondX = second.X;
        var secondWidth = second.Width;
        var newFirstX = first.X;
        var newFirstWidth = first.Width;

        if (offset >= 0)
        {
            // Line moves right: shrink the right-hand zone first, then translate.
            var squeeze = Math.Min(offset, Math.Max(0, second.Width - SplitterMinimumWidth));
            var interimX = second.X + squeeze;
            var interimWidth = second.Right - interimX;

            var shift = offset - squeeze;
            if (shift > 0)
            {
                var ceiling = ComputeRightTranslateLimit(drag, interimWidth);
                shift = Math.Max(0, Math.Min(shift, ceiling - interimX));
                interimX += shift;
            }

            newSecondX = interimX;
            secondWidth = interimWidth;
            newFirstWidth = newSecondX - drag.Gap - first.X;
            if (newSecondX.ApproximatelyEquals(second.X) && newFirstWidth.ApproximatelyEquals(first.Width))
            {
                return;
            }
        }
        else
        {
            // Line moves left: shrink the left-hand zone first, then translate.
            var total = -offset;
            var squeeze = Math.Min(total, Math.Max(0, first.Width - SplitterMinimumWidth));
            var interimWidth = Math.Max(SplitterMinimumWidth, first.Width - squeeze);

            var shift = total - squeeze;
            if (shift > 0)
            {
                var floor = ComputeLeftTranslateFloor(drag);
                shift = Math.Max(0, Math.Min(shift, first.X - floor));
                newFirstX = first.X - shift;
            }

            newFirstWidth = interimWidth;
            newSecondX = newFirstX + newFirstWidth + drag.Gap;
            if (newFirstX.ApproximatelyEquals(first.X) && newFirstWidth.ApproximatelyEquals(first.Width))
            {
                return;
            }
        }

        ApplyZoneVisualBounds(drag.Second, new ZoneBounds(newSecondX, second.Y, secondWidth, second.Height));
        ApplyZoneVisualBounds(drag.First, new ZoneBounds(newFirstX, first.Y, newFirstWidth, first.Height));
    }

    /// <summary>Moves a stacked pair along the vertical axis; same contract as
    /// <see cref="MoveSideBySidePair"/> with positive offsets meaning downward.</summary>
    private void MoveStackedPair(SplitterDragState drag, double offset)
    {
        var first = drag.FirstStart;
        var second = drag.SecondStart;
        const double minimumY = 72;
        var maximumBottom = Math.Max(minimumY + 1, ActualHeight);
        var newSecondY = second.Y;
        var secondHeight = second.Height;
        var newFirstY = first.Y;
        var newFirstHeight = first.Height;

        if (offset >= 0)
        {
            // Line moves down: shrink the bottom zone first, then translate.
            var squeeze = Math.Min(offset, Math.Max(0, second.Height - SplitterMinimumHeight));
            var interimY = second.Y + squeeze;
            var interimHeight = second.Bottom - interimY;

            var shift = offset - squeeze;
            if (shift > 0)
            {
                var ceiling = ComputeDownTranslateLimit(drag, interimHeight, maximumBottom);
                shift = Math.Max(0, Math.Min(shift, ceiling - interimY));
                interimY += shift;
            }

            newSecondY = interimY;
            secondHeight = interimHeight;
            newFirstHeight = newSecondY - drag.Gap - first.Y;
            if (newSecondY.ApproximatelyEquals(second.Y) && newFirstHeight.ApproximatelyEquals(first.Height))
            {
                return;
            }
        }
        else
        {
            // Line moves up: shrink the top zone first, then translate.
            var total = -offset;
            var squeeze = Math.Min(total, Math.Max(0, first.Height - SplitterMinimumHeight));
            var interimHeight = Math.Max(SplitterMinimumHeight, first.Height - squeeze);

            var shift = total - squeeze;
            if (shift > 0)
            {
                var floor = ComputeUpTranslateFloor(drag);
                shift = Math.Max(0, Math.Min(shift, first.Y - floor));
                newFirstY = first.Y - shift;
            }

            newFirstHeight = interimHeight;
            newSecondY = newFirstY + newFirstHeight + drag.Gap;
            if (newFirstY.ApproximatelyEquals(first.Y) && newFirstHeight.ApproximatelyEquals(first.Height))
            {
                return;
            }
        }

        ApplyZoneVisualBounds(drag.Second, new ZoneBounds(second.X, newSecondY, second.Width, secondHeight));
        ApplyZoneVisualBounds(drag.First, new ZoneBounds(first.X, newFirstY, first.Width, newFirstHeight));
    }

    private double ComputeRightTranslateLimit(SplitterDragState drag, double secondaryWidth)
    {
        var limit = Math.Max(ZoneCard.HorizontalDesktopInset + 1, ActualWidth - ZoneCard.HorizontalDesktopInset)
                    - secondaryWidth;
        var top = Math.Max(drag.FirstStart.Y, drag.SecondStart.Y);
        var bottom = Math.Min(drag.FirstStart.Bottom, drag.SecondStart.Bottom);
        foreach (var other in _state.Zones)
        {
            if (other.Id == drag.First.Id || other.Id == drag.Second.Id)
            {
                continue;
            }

            var bounds = GetVisualBounds(other);
            if (Math.Min(bounds.Bottom, bottom) - Math.Max(bounds.Y, top) > 12)
            {
                limit = Math.Min(limit, bounds.X - 12 - secondaryWidth);
            }
        }

        return Math.Max(drag.SecondStart.X, limit);
    }

    private double ComputeLeftTranslateFloor(SplitterDragState drag)
    {
        var floor = ZoneCard.HorizontalDesktopInset;
        var top = Math.Max(drag.FirstStart.Y, drag.SecondStart.Y);
        var bottom = Math.Min(drag.FirstStart.Bottom, drag.SecondStart.Bottom);
        foreach (var other in _state.Zones)
        {
            if (other.Id == drag.First.Id || other.Id == drag.Second.Id)
            {
                continue;
            }

            var bounds = GetVisualBounds(other);
            if (Math.Min(bounds.Bottom, bottom) - Math.Max(bounds.Y, top) > 12)
            {
                floor = Math.Max(floor, bounds.Right + 12);
            }
        }

        return Math.Min(floor, drag.FirstStart.X);
    }

    private double ComputeDownTranslateLimit(
        SplitterDragState drag,
        double secondaryHeight,
        double maximumBottom)
    {
        var limit = maximumBottom - secondaryHeight;
        var left = Math.Max(drag.FirstStart.X, drag.SecondStart.X);
        var right = Math.Min(drag.FirstStart.Right, drag.SecondStart.Right);
        foreach (var other in _state.Zones)
        {
            if (other.Id == drag.First.Id || other.Id == drag.Second.Id)
            {
                continue;
            }

            var bounds = GetVisualBounds(other);
            if (Math.Min(bounds.Right, right) - Math.Max(bounds.X, left) > 12)
            {
                limit = Math.Min(limit, bounds.Y - 12 - secondaryHeight);
            }
        }

        return Math.Max(drag.SecondStart.Y, limit);
    }

    private double ComputeUpTranslateFloor(SplitterDragState drag)
    {
        const double minimumY = 72;
        var floor = minimumY;
        var left = Math.Max(drag.FirstStart.X, drag.SecondStart.X);
        var right = Math.Min(drag.FirstStart.Right, drag.SecondStart.Right);
        foreach (var other in _state.Zones)
        {
            if (other.Id == drag.First.Id || other.Id == drag.Second.Id)
            {
                continue;
            }

            var bounds = GetVisualBounds(other);
            if (Math.Min(bounds.Right, right) - Math.Max(bounds.X, left) > 12)
            {
                floor = Math.Max(floor, bounds.Bottom + 12);
            }
        }

        return Math.Min(floor, drag.FirstStart.Y);
    }



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
