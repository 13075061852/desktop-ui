using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DeskNest.App.Services;
using DeskNest.Core.Models;
using DeskNest.Core.Services;

namespace DeskNest.App.Controls;

internal sealed record ItemDragPayload(Guid SourceZoneId, Guid ItemId);

internal sealed class FilesDroppedEventArgs(IReadOnlyList<string> paths) : EventArgs
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

internal sealed class ItemActionEventArgs(DesktopItem item) : EventArgs
{
    public DesktopItem Item { get; } = item;
}

internal sealed class ItemMoveEventArgs(ItemDragPayload payload) : EventArgs
{
    public ItemDragPayload Payload { get; } = payload;
}

public partial class ZoneCard : UserControl
{
    public const string InternalItemFormat = "DeskNest.InternalDesktopItem";
    internal const double HorizontalDesktopInset = 12;

    private readonly ShellIconService _iconService;
    private static readonly (string Name, string Value)[] AccentPresets =
    [
        ("薄荷青", "#76D7C4"),
        ("天空蓝", "#80BFFF"),
        ("暖橙色", "#FFB86B"),
        ("薰衣紫", "#C4A7FF"),
        ("珊瑚红", "#FF8398"),
        ("青柠绿", "#A8D672")
    ];

    private Point _headerDragStart;
    private Point _itemDragStart;
    private UIElement? _capturedHeader;
    private double _modelStartX;
    private double _modelStartY;
    private ZoneBounds _resizeStartBounds;
    private double _resizeHorizontalChange;
    private double _resizeVerticalChange;
    private bool _draggingHeader;
    private bool _layoutLocked;
    private bool _lightTheme;
    private double _iconSize = 44;
    private Brush _bodyBackground = Brushes.Transparent;
    private Brush _headerBackground = Brushes.Transparent;

    internal ZoneCard(ZoneModel model, ShellIconService iconService)
    {
        Model = model;
        _iconService = iconService;
        InitializeComponent();
        foreach (var thumb in ResizeLayer.Children.OfType<Thumb>())
        {
            thumb.DragStarted += OnResizeDragStarted;
            thumb.DragCompleted += OnResizeDragCompleted;
        }
        ApplyModel();
    }

    public ZoneModel Model { get; }

    internal Func<ZoneModel, ZoneBounds, ZoneBounds, ZoneAlignmentResult>? BoundsConstraint { get; set; }
    internal Action<double?, double?>? AlignmentGuidesChanged { get; set; }

    internal event EventHandler? ModelChanged;
    internal event EventHandler? DeleteRequested;
    internal event EventHandler<FilesDroppedEventArgs>? FilesDropped;
    internal event EventHandler<ItemMoveEventArgs>? ItemMoveRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemOpenRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemRevealRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemRemoveRequested;

    public void SetLayoutLocked(bool locked)
    {
        _layoutLocked = locked;
        ResizeLayer.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        Cursor = locked ? Cursors.Arrow : Cursors.SizeAll;
    }

    public void SetVisualOptions(double opacity, double iconSize, bool lightTheme)
    {
        _iconSize = iconSize;
        _lightTheme = lightTheme;
        var color = lightTheme
            ? Color.FromArgb((byte)(255 * opacity), 244, 247, 251)
            : Color.FromArgb((byte)(255 * opacity), 27, 34, 46);
        var foreground = lightTheme ? new SolidColorBrush(Color.FromRgb(27, 34, 46)) : Brushes.White;
        var muted = lightTheme
            ? new SolidColorBrush(Color.FromRgb(92, 104, 120))
            : (Brush)FindResource("TextMutedBrush");

        _bodyBackground = new SolidColorBrush(color);
        var surfaceAlpha = (byte)(255 * opacity);
        _headerBackground = new SolidColorBrush(lightTheme
            ? Color.FromArgb(surfaceAlpha, 248, 250, 252)
            : Color.FromArgb(surfaceAlpha, 35, 44, 58));
        CardBorder.Background = Model.IsCollapsed ? _headerBackground : _bodyBackground;
        CardBorder.BorderBrush = new SolidColorBrush(lightTheme
            ? Color.FromArgb(112, 255, 255, 255)
            : Color.FromArgb(62, 255, 255, 255));
        HeaderSurface.Background = Model.IsCollapsed ? Brushes.Transparent : _headerBackground;
        HeaderDivider.Background = new SolidColorBrush(lightTheme
            ? Color.FromArgb(24, 55, 70, 88)
            : Color.FromArgb(34, 255, 255, 255));
        CountBadge.Background = new SolidColorBrush(lightTheme
            ? Color.FromArgb(18, 54, 68, 85)
            : Color.FromArgb(30, 255, 255, 255));
        TitleEditor.Background = new SolidColorBrush(lightTheme
            ? Color.FromArgb(232, 255, 255, 255)
            : Color.FromArgb(38, 255, 255, 255));
        TitleText.Foreground = foreground;
        TitleEditor.Foreground = foreground;
        CountText.Foreground = muted;
        CollapseButton.Foreground = muted;
        MoreButton.Foreground = muted;
        RenderItems();
    }

    public void RefreshItems()
    {
        RenderItems();
    }

    private void ApplyModel()
    {
        Width = Model.Width;
        Height = Model.IsCollapsed ? 52 : Model.Height;
        TitleText.Text = Model.Name;
        CountText.Text = Model.Items.Count.ToString();
        CollapseChevron.RenderTransformOrigin = new Point(0.5, 0.5);
        CollapseChevron.RenderTransform = new RotateTransform(Model.IsCollapsed ? 180 : 0);
        if (Model.IsCollapsed)
        {
            CardBorder.CornerRadius = new CornerRadius(10);
            HeaderSurface.CornerRadius = new CornerRadius(10);
            HeaderSurface.Background = Brushes.Transparent;
            CardBorder.Background = _headerBackground;
            CardShadow.BlurRadius = 0;
            CardShadow.ShadowDepth = 0;
            CardShadow.Opacity = 0;
        }
        else
        {
            CardBorder.CornerRadius = new CornerRadius(10);
            HeaderSurface.CornerRadius = new CornerRadius(10, 10, 0, 0);
            HeaderSurface.Background = _headerBackground;
            CardBorder.Background = _bodyBackground;
            CardShadow.BlurRadius = 22;
            CardShadow.ShadowDepth = 5;
            CardShadow.Opacity = 0.2;
        }

        HeaderDivider.Visibility = Model.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        BodyArea.Opacity = 1;
        BodyScroller.Visibility = Model.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SlimScrollBar.Visibility = Model.IsCollapsed ? Visibility.Collapsed : SlimScrollBar.Visibility;
        ResizeLayer.Visibility = Model.IsCollapsed || _layoutLocked ? Visibility.Collapsed : Visibility.Visible;

        try
        {
            AccentDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Model.AccentColor));
        }
        catch (FormatException)
        {
            AccentDot.Fill = (Brush)FindResource("AccentBrush");
        }

        RenderItems();
    }

    private void RenderItems()
    {
        ItemsPanel.Children.Clear();
        CountText.Text = Model.Items.Count.ToString();
        var tileWidth = Math.Max(76, _iconSize + 34);

        foreach (var item in Model.Items.OrderBy(value => value.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            var tile = BuildItemTile(item, tileWidth);
            ItemsPanel.Children.Add(tile);
        }

        if (Model.Items.Count == 0)
        {
            ItemsPanel.Children.Add(new TextBlock
            {
                Text = "拖入文件，或点击“一键整理”",
                Foreground = _lightTheme
                    ? new SolidColorBrush(Color.FromRgb(82, 94, 112))
                    : (Brush)FindResource("TextMutedBrush"),
                FontSize = 12,
                Margin = new Thickness(10, 16, 0, 0)
            });
        }
    }

    private FrameworkElement BuildItemTile(DesktopItem item, double tileWidth)
    {
        var icon = new Image
        {
            Width = _iconSize,
            Height = _iconSize,
            Stretch = Stretch.Uniform,
            Source = _iconService.GetIcon(item.Path),
            Opacity = item.Exists ? 1 : 0.42,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var label = new TextBlock
        {
            Text = item.DisplayName,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11,
            Foreground = _lightTheme
                ? new SolidColorBrush(item.Exists ? Color.FromRgb(27, 34, 46) : Color.FromRgb(103, 113, 130))
                : item.Exists ? (Brush)FindResource("TextPrimaryBrush") : (Brush)FindResource("TextMutedBrush"),
            Margin = new Thickness(2, 5, 2, 0),
            ToolTip = item.Exists ? item.Path : $"文件已失效：{item.Path}"
        };

        var content = new StackPanel();
        content.Children.Add(icon);
        content.Children.Add(label);

        var border = new Border
        {
            Width = tileWidth,
            MinHeight = _iconSize + 44,
            Padding = new Thickness(5, 9, 5, 6),
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Child = content,
            Tag = item,
            Cursor = Cursors.Hand
        };

        border.MouseEnter += (_, _) => border.Background = (Brush)FindResource("PanelHoverBrush");
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonDown += OnItemMouseDown;
        border.MouseMove += OnItemMouseMove;
        border.ContextMenu = BuildItemContextMenu(item);
        return border;
    }

    private ContextMenu BuildItemContextMenu(DesktopItem item)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => ItemOpenRequested?.Invoke(this, new ItemActionEventArgs(item));
        var reveal = new MenuItem { Header = "打开所在位置" };
        reveal.Click += (_, _) => ItemRevealRequested?.Invoke(this, new ItemActionEventArgs(item));
        var remove = new MenuItem { Header = "移出分区（不删除文件）" };
        remove.Click += (_, _) => ItemRemoveRequested?.Invoke(this, new ItemActionEventArgs(item));
        menu.Items.Add(open);
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        return menu;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_layoutLocked || e.ClickCount > 1 || Parent is not IInputElement parent ||
            sender is not UIElement header || IsInteractiveHeaderSource(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _headerDragStart = e.GetPosition(parent);
        _modelStartX = Model.X;
        _modelStartY = Model.Y;
        _draggingHeader = true;
        _capturedHeader = header;
        header.CaptureMouse();
        e.Handled = true;
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHeader || e.LeftButton != MouseButtonState.Pressed || Parent is not IInputElement parent)
        {
            return;
        }

        var currentPoint = e.GetPosition(parent);
        var currentBounds = GetVisualBounds();
        var desiredBounds = currentBounds with
        {
            X = Math.Max(HorizontalDesktopInset, _modelStartX + currentPoint.X - _headerDragStart.X),
            Y = Math.Max(72, _modelStartY + currentPoint.Y - _headerDragStart.Y)
        };
        var alignment = BoundsConstraint?.Invoke(Model, currentBounds, desiredBounds)
                        ?? new ZoneAlignmentResult(desiredBounds, null, null);
        AlignmentGuidesChanged?.Invoke(alignment.VerticalGuide, alignment.HorizontalGuide);
        Model.X = alignment.Bounds.X;
        Model.Y = alignment.Bounds.Y;
        Canvas.SetLeft(this, Model.X);
        Canvas.SetTop(this, Model.Y);
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingHeader)
        {
            return;
        }

        _draggingHeader = false;
        _capturedHeader?.ReleaseMouseCapture();
        _capturedHeader = null;
        AlignmentGuidesChanged?.Invoke(null, null);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnResizeDragStarted(object sender, DragStartedEventArgs e)
    {
        _resizeStartBounds = GetVisualBounds();
        _resizeHorizontalChange = 0;
        _resizeVerticalChange = 0;
    }

    private void OnResizeDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_layoutLocked || Model.IsCollapsed || sender is not FrameworkElement { Tag: string direction })
        {
            return;
        }

        _resizeHorizontalChange += e.HorizontalChange;
        _resizeVerticalChange += e.VerticalChange;
        var currentBounds = GetVisualBounds();
        var desiredBounds = GetDesiredResizeBounds(direction);
        var alignment = BoundsConstraint?.Invoke(Model, currentBounds, desiredBounds)
                        ?? new ZoneAlignmentResult(desiredBounds, null, null);
        AlignmentGuidesChanged?.Invoke(alignment.VerticalGuide, alignment.HorizontalGuide);
        Model.X = alignment.Bounds.X;
        Model.Y = alignment.Bounds.Y;
        Model.Width = alignment.Bounds.Width;
        Model.Height = alignment.Bounds.Height;
        Width = Model.Width;
        Height = Model.Height;
        Canvas.SetLeft(this, Model.X);
        Canvas.SetTop(this, Model.Y);
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnResizeDragCompleted(object sender, DragCompletedEventArgs e)
    {
        AlignmentGuidesChanged?.Invoke(null, null);
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    private ZoneBounds GetDesiredResizeBounds(string direction)
    {
        var x = _resizeStartBounds.X;
        var y = _resizeStartBounds.Y;
        var width = _resizeStartBounds.Width;
        var height = _resizeStartBounds.Height;

        if (direction is "Left" or "TopLeft" or "BottomLeft")
        {
            width = Math.Max(200, _resizeStartBounds.Width - _resizeHorizontalChange);
            x = _resizeStartBounds.Right - width;
            if (x < HorizontalDesktopInset)
            {
                x = HorizontalDesktopInset;
                width = _resizeStartBounds.Right - HorizontalDesktopInset;
            }
        }
        else if (direction is "Right" or "TopRight" or "BottomRight")
        {
            width = Math.Max(200, _resizeStartBounds.Width + _resizeHorizontalChange);
        }

        if (direction is "Top" or "TopLeft" or "TopRight")
        {
            height = Math.Max(150, _resizeStartBounds.Height - _resizeVerticalChange);
            y = _resizeStartBounds.Bottom - height;
            if (y < 72)
            {
                y = 72;
                height = _resizeStartBounds.Bottom - 72;
            }
        }
        else if (direction is "Bottom" or "BottomLeft" or "BottomRight")
        {
            height = Math.Max(150, _resizeStartBounds.Height + _resizeVerticalChange);
        }

        return new ZoneBounds(x, y, width, height);
    }

    private ZoneBounds GetVisualBounds()
    {
        return new ZoneBounds(Model.X, Model.Y, Model.Width, Model.IsCollapsed ? 52 : Model.Height);
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        var collapse = !Model.IsCollapsed;
        Model.IsCollapsed = collapse;
        AnimateCollapseChange(collapse);
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AnimateCollapseChange(bool collapse)
    {
        var duration = TimeSpan.FromMilliseconds(220);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var startHeight = Math.Max(52, ActualHeight);
        var targetHeight = collapse ? 52 : Model.Height;
        var startAngle = CollapseChevron.RenderTransform is RotateTransform currentRotation
            ? currentRotation.Angle
            : collapse ? 0 : 180;

        CollapseButton.IsEnabled = false;
        ResizeLayer.Visibility = Visibility.Collapsed;

        if (!collapse)
        {
            ApplyModel();
            BodyArea.Opacity = 0;
        }

        Height = targetHeight;
        var heightAnimation = new DoubleAnimation(startHeight, targetHeight, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };

        var opacityAnimation = new DoubleAnimation(collapse ? 1 : 0, collapse ? 0 : 1,
            TimeSpan.FromMilliseconds(collapse ? 140 : 170))
        {
            BeginTime = collapse ? TimeSpan.Zero : TimeSpan.FromMilliseconds(50),
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        BodyArea.Opacity = collapse ? 0 : 1;
        BodyArea.BeginAnimation(OpacityProperty, opacityAnimation);

        if (CollapseChevron.RenderTransform is not RotateTransform rotation)
        {
            rotation = new RotateTransform(collapse ? 0 : 180);
            CollapseChevron.RenderTransform = rotation;
        }

        var targetAngle = collapse ? 180 : 0;
        var angleAnimation = new DoubleAnimation(startAngle, targetAngle, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        rotation.Angle = targetAngle;
        rotation.BeginAnimation(RotateTransform.AngleProperty, angleAnimation);

        heightAnimation.Completed += (_, _) =>
        {
            ApplyModel();
            CollapseButton.IsEnabled = true;
        };
        BeginAnimation(HeightProperty, heightAnimation);
    }

    private void OnBodyScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (SlimScrollBar is null)
        {
            return;
        }

        var maximum = Math.Max(0, e.ExtentHeight - e.ViewportHeight);
        SlimScrollBar.Maximum = maximum;
        SlimScrollBar.ViewportSize = Math.Max(0, e.ViewportHeight);
        SlimScrollBar.LargeChange = Math.Max(32, e.ViewportHeight * 0.8);
        SlimScrollBar.SmallChange = 36;
        SlimScrollBar.Value = Math.Clamp(e.VerticalOffset, 0, maximum);
        SlimScrollBar.Visibility = !Model.IsCollapsed && maximum > 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSlimScroll(object sender, ScrollEventArgs e)
    {
        BodyScroller.ScrollToVerticalOffset(e.NewValue);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => BeginRename();
        var colors = new MenuItem { Header = "分区颜色" };
        foreach (var preset in AccentPresets)
        {
            var colorItem = new MenuItem
            {
                Header = preset.Name,
                IsCheckable = true,
                IsChecked = string.Equals(Model.AccentColor, preset.Value, StringComparison.OrdinalIgnoreCase),
                Icon = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(preset.Value))
                }
            };
            colorItem.Click += (_, _) =>
            {
                Model.AccentColor = preset.Value;
                ApplyModel();
                ModelChanged?.Invoke(this, EventArgs.Empty);
            };
            colors.Items.Add(colorItem);
        }

        var clear = new MenuItem { Header = "清空映射" };
        clear.Click += (_, _) =>
        {
            Model.Items.Clear();
            RenderItems();
            ModelChanged?.Invoke(this, EventArgs.Empty);
        };
        var delete = new MenuItem { Header = "删除分区", Foreground = (Brush)FindResource("DangerBrush") };
        delete.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(rename);
        menu.Items.Add(colors);
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.PlacementTarget = MoreButton;
        menu.IsOpen = true;
    }

    private bool IsInteractiveHeaderSource(DependencyObject? source)
    {
        for (var current = source; current is not null && current != this; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or TextBox)
            {
                return true;
            }
        }

        return false;
    }

    private void OnTitleDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginRename();
            e.Handled = true;
        }
    }

    private void BeginRename()
    {
        TitleEditor.Text = Model.Name;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = Visibility.Visible;
        TitleEditor.Focus();
        TitleEditor.SelectAll();
    }

    private void CommitRename()
    {
        if (!string.IsNullOrWhiteSpace(TitleEditor.Text))
        {
            Model.Name = TitleEditor.Text.Trim();
            TitleText.Text = Model.Name;
            ModelChanged?.Invoke(this, EventArgs.Empty);
        }

        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
    }

    private void OnTitleEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitRename();

    private void OnTitleEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRename();
        }
        else if (e.Key == Key.Escape)
        {
            TitleEditor.Visibility = Visibility.Collapsed;
            TitleText.Visibility = Visibility.Visible;
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _itemDragStart = e.GetPosition(this);
        if (e.ClickCount == 2 && sender is FrameworkElement { Tag: DesktopItem item })
        {
            ItemOpenRequested?.Invoke(this, new ItemActionEventArgs(item));
            e.Handled = true;
        }
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement { Tag: DesktopItem item })
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _itemDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _itemDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(InternalItemFormat, new ItemDragPayload(Model.Id, item.Id));
        DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(InternalItemFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(InternalItemFormat) is ItemDragPayload payload)
        {
            ItemMoveRequested?.Invoke(this, new ItemMoveEventArgs(payload));
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            FilesDropped?.Invoke(this, new FilesDroppedEventArgs(paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray()));
            e.Handled = true;
        }
    }
}
