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

        var delta = drag.IsHorizontalLine ? e.VerticalChange : e.HorizontalChange;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        if (drag.IsHorizontalLine)
        {
            MoveStackedPair(drag, delta);
        }
        else
        {
            MoveSideBySidePair(drag, delta);
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

    private void MoveSideBySidePair(SplitterDragState drag, double delta)
    {
        var firstStart = drag.FirstStart;
        var secondStart = drag.SecondStart;

        if (delta > 0)
        {
            // Moving the line right squeezes the right-hand zone first; once
            // it reaches the minimum width the pair translates together until
            // the window edge or another zone stops it.
            var squeeze = Math.Min(delta, Math.Max(0, secondStart.Width - SplitterMinimumWidth));
            var newSecondX = secondStart.X + squeeze;
            var translate = delta - squeeze;
            if (translate > 0)
            {
                var ceiling = ComputeRightTranslateLimit(drag, SplitterMinimumWidth);
                newSecondX += Math.Max(0, ceiling - newSecondX);
            }

            var secondWidth = secondStart.Right - newSecondX;
            var newFirstWidth = newSecondX - drag.Gap - firstStart.X;
            if (secondWidth < SplitterMinimumWidth - 0.5 ||
                newFirstWidth < SplitterMinimumWidth - 0.5 ||
                (Math.Abs(secondStart.X - newSecondX) < 0.01 && Math.Abs(firstStart.Width - newFirstWidth) < 0.01))
            {
                return;
            }

            ApplyZoneVisualBounds(drag.Second, new ZoneBounds(newSecondX, secondStart.Y, secondWidth, secondStart.Height));
            ApplyZoneVisualBounds(drag.First, new ZoneBounds(firstStart.X, firstStart.Y, newFirstWidth, firstStart.Height));
        }
        else
        {
            // Moving the line left squeezes the left-hand zone first, then the
            // pair translates left with the left zone held at its minimum.
            var squeeze = Math.Min(-delta, Math.Max(0, firstStart.Width - SplitterMinimumWidth));
            var translate = -delta - squeeze;
            var newFirstX = firstStart.X - translate;
            var floor = ComputeLeftTranslateFloor(drag);
            newFirstX = Math.Max(newFirstX, floor);
            var firstWidth = Math.Max(SplitterMinimumWidth, firstStart.Right - newFirstX);
            var newSecondX = newFirstX + firstWidth + drag.Gap;
            if (Math.Abs(firstStart.X - newFirstX) < 0.01 && Math.Abs(firstStart.Width - firstWidth) < 0.01)
            {
                return;
            }

            ApplyZoneVisualBounds(drag.First, new ZoneBounds(newFirstX, firstStart.Y, firstWidth, firstStart.Height));
            ApplyZoneVisualBounds(drag.Second, new ZoneBounds(newSecondX, secondStart.Y, secondStart.Right - newSecondX, secondStart.Height));
        }
    }

    private void MoveStackedPair(SplitterDragState drag, double delta)
    {
        var firstStart = drag.FirstStart;
        var secondStart = drag.SecondStart;
        const double minimumY = 72;
        var maximumBottom = Math.Max(minimumY + 1, ActualHeight);

        if (delta > 0)
        {
            // Moving the line down squeezes the bottom zone first, then the
            // pair translates down together.
            var squeeze = Math.Min(delta, Math.Max(0, secondStart.Height - SplitterMinimumHeight));
            var newSecondY = secondStart.Y + squeeze;
            var translate = delta - squeeze;
            if (translate > 0)
            {
                var ceiling = ComputeDownTranslateLimit(drag, SplitterMinimumHeight, maximumBottom);
                newSecondY += Math.Max(0, ceiling - newSecondY);
            }

            var secondHeight = secondStart.Bottom - newSecondY;
            var newFirstHeight = newSecondY - drag.Gap - firstStart.Y;
            if (secondHeight < SplitterMinimumHeight - 0.5 ||
                newFirstHeight < SplitterMinimumHeight - 0.5 ||
                (Math.Abs(secondStart.Y - newSecondY) < 0.01 && Math.Abs(firstStart.Height - newFirstHeight) < 0.01))
            {
                return;
            }

            ApplyZoneVisualBounds(drag.Second, new ZoneBounds(secondStart.X, newSecondY, secondStart.Width, secondHeight));
            ApplyZoneVisualBounds(drag.First, new ZoneBounds(firstStart.X, firstStart.Y, firstStart.Width, newFirstHeight));
        }
        else
        {
            var squeeze = Math.Min(-delta, Math.Max(0, firstStart.Height - SplitterMinimumHeight));
            var translate = -delta - squeeze;
            var newFirstY = firstStart.Y - translate;
            var floor = ComputeUpTranslateFloor(drag);
            newFirstY = Math.Max(newFirstY, floor);
            var firstHeight = Math.Max(SplitterMinimumHeight, firstStart.Bottom - newFirstY);
            var newSecondY = newFirstY + firstHeight + drag.Gap;
            if (Math.Abs(firstStart.Y - newFirstY) < 0.01 && Math.Abs(firstStart.Height - firstHeight) < 0.01)
            {
                return;
            }

            ApplyZoneVisualBounds(drag.First, new ZoneBounds(firstStart.X, newFirstY, firstStart.Width, firstHeight));
            ApplyZoneVisualBounds(drag.Second, new ZoneBounds(secondStart.X, newSecondY, secondStart.Width, secondStart.Bottom - newSecondY));
        }
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
