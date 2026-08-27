using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskNest.App.Interop;
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

internal sealed class ItemMoveEventArgs(ItemDragPayload payload, int targetIndex) : EventArgs
{
    public ItemDragPayload Payload { get; } = payload;

    public int TargetIndex { get; } = targetIndex;
}

internal sealed class ItemDragCompletedEventArgs(string path, DragDropEffects effects) : EventArgs
{
    public string Path { get; } = path;

    public DragDropEffects Effects { get; } = effects;
}

internal sealed class FolderFilesDroppedEventArgs(DesktopItem folder, IReadOnlyList<string> paths) : EventArgs
{
    public DesktopItem Folder { get; } = folder;

    public IReadOnlyList<string> Paths { get; } = paths;
}

public partial class ZoneCard : UserControl
{
    public const string InternalItemFormat = "DeskNest.InternalDesktopItem";
    internal const double HorizontalDesktopInset = 12;

    private readonly ShellIconService _iconService;
    private readonly DispatcherTimer _dragTrackingTimer;

    private Point _headerDragStart;
    private Point _itemDragStart;
    private UIElement? _capturedHeader;
    private ContextMenu? _openContextMenu;
    private Popup? _dragPreviewPopup;
    private FrameworkElement? _dragSourceElement;
    private ItemDragPayload? _activeDragPayload;
    private int _dropIndicatorIndex = -1;
    private int _liveReflowIndex = -1;
    private int _liveReflowRow = -1;
    private Guid _liveReflowItemId;
    private Guid? _selectedItemId;
    private double _modelStartX;
    private double _modelStartY;
    private ZoneBounds _resizeStartBounds;
    private double _resizeHorizontalChange;
    private double _resizeVerticalChange;
    private bool _draggingHeader;
    private bool _layoutLocked;
    private bool _lightTheme;
    private bool _hasScrollableContent;
    private bool _headerActionsVisible;
    private double _iconSize = 44;
    private Brush _bodyBackground = Brushes.Transparent;
    private Brush _headerBackground = Brushes.Transparent;

    internal ZoneCard(ZoneModel model, ShellIconService iconService)
    {
        Model = model;
        _iconService = iconService;
        InitializeComponent();
        _dragTrackingTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _dragTrackingTimer.Tick += (_, _) => TrackActiveDragPointer();
        foreach (var thumb in ResizeLayer.Children.OfType<Thumb>())
        {
            thumb.DragStarted += OnResizeDragStarted;
            thumb.DragCompleted += OnResizeDragCompleted;
        }
        Loaded += (_, _) => UpdateHeaderPresentation(animate: false);
        Unloaded += (_, _) => HideDropIndicator();
        SizeChanged += (_, _) => UpdateHeaderPresentation(animate: false);
        ApplyModel();
    }

    public ZoneModel Model { get; }

    internal Func<ZoneModel, ZoneBounds, ZoneBounds, ZoneAlignmentResult>? BoundsConstraint { get; set; }
    internal Action<double?, double?>? AlignmentGuidesChanged { get; set; }
    internal Action<ZoneCard>? DragPreviewActivated { get; set; }
    internal Action? DragPreviewEnded { get; set; }
    internal bool HasOpenTransientMenu => _openContextMenu?.IsOpen == true;

    internal event EventHandler? ModelChanged;
    internal event EventHandler? DeleteRequested;
    internal event EventHandler? NewFileRequested;
    internal event EventHandler? NewFolderRequested;
    internal event EventHandler? NewShortcutRequested;
    internal event EventHandler<FilesDroppedEventArgs>? FilesDropped;
    internal event EventHandler<FolderFilesDroppedEventArgs>? FolderFilesDropped;
    internal event EventHandler<ItemMoveEventArgs>? ItemMoveRequested;
    internal event EventHandler<ItemDragCompletedEventArgs>? ItemDragCompleted;
    internal event EventHandler<ItemActionEventArgs>? ItemSelected;
    internal event EventHandler? SelectionClearRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemOpenRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemRenameRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemRevealRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemPropertiesRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemDeleteRequested;
    internal event EventHandler<ItemActionEventArgs>? ItemRemoveRequested;

    internal void SetSelectedItem(Guid? itemId)
    {
        _selectedItemId = itemId;
        foreach (var border in ItemsPanel.Children
                     .OfType<Border>()
                     .Where(element => element.Tag is DesktopItem))
        {
            ApplyItemSurfaceVisual(border, isHovered: false);
        }
    }

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
        TitleEditor.Background = new SolidColorBrush(lightTheme
            ? Color.FromArgb(232, 255, 255, 255)
            : Color.FromArgb(38, 255, 255, 255));
        TitleText.Foreground = foreground;
        TitleEditor.Foreground = foreground;
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
        UpdateSlimScrollBarVisibility();
        ResizeLayer.Visibility = Model.IsCollapsed || _layoutLocked ? Visibility.Collapsed : Visibility.Visible;

        RenderItems();
    }

    private void RenderItems()
    {
        ItemsPanel.Children.Clear();
        var iconTileWidth = Math.Max(76, _iconSize + 34);
        var listTileWidth = Math.Max(120, Model.Width - 28);

        foreach (var item in Model.Items)
        {
            var tile = Model.ViewMode == "List"
                ? BuildListItemTile(item, listTileWidth)
                : BuildIconItemTile(item, iconTileWidth);
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

    private FrameworkElement BuildIconItemTile(DesktopItem item, double tileWidth)
    {
        var icon = BuildItemIcon(item, _iconSize);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        var label = BuildItemLabel(item, TextAlignment.Center);
        label.Margin = new Thickness(2, 5, 2, 0);

        var content = new StackPanel();
        content.Children.Add(icon);
        content.Children.Add(label);
        return BuildInteractiveItemSurface(item, content, tileWidth, _iconSize + 44, new Thickness(5, 9, 5, 6));
    }

    private FrameworkElement BuildListItemTile(DesktopItem item, double tileWidth)
    {
        var iconSize = Math.Clamp(_iconSize * 0.72, 28, 42);
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildItemIcon(item, iconSize);
        icon.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(icon);

        var label = BuildItemLabel(item, TextAlignment.Left);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(11, 0, 4, 0);
        Grid.SetColumn(label, 1);
        content.Children.Add(label);

        return BuildInteractiveItemSurface(item, content, tileWidth, iconSize + 18, new Thickness(9, 6, 8, 6));
    }

    private Image BuildItemIcon(DesktopItem item, double size)
    {
        return new Image
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Source = _iconService.GetIcon(item.Path),
            Opacity = item.Exists ? 1 : 0.42
        };
    }

    private TextBlock BuildItemLabel(DesktopItem item, TextAlignment alignment)
    {
        return new TextBlock
        {
            Text = item.DisplayName,
            TextAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11,
            Foreground = _lightTheme
                ? new SolidColorBrush(item.Exists ? Color.FromRgb(27, 34, 46) : Color.FromRgb(103, 113, 130))
                : item.Exists ? (Brush)FindResource("TextPrimaryBrush") : (Brush)FindResource("TextMutedBrush"),
            ToolTip = item.Exists ? item.Path : $"文件已失效：{item.Path}"
        };
    }

    private Border BuildInteractiveItemSurface(
        DesktopItem item,
        UIElement content,
        double width,
        double minimumHeight,
        Thickness padding)
    {
        var border = new Border
        {
            Width = width,
            MinHeight = minimumHeight,
            Padding = padding,
            Margin = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Background = Brushes.Transparent,
            Child = content,
            Tag = item,
            Cursor = Cursors.Hand,
            AllowDrop = Directory.Exists(item.Path)
        };

        if (border.AllowDrop)
        {
            border.DragOver += OnFolderDragOver;
            border.Drop += OnFolderDrop;
        }

        border.MouseEnter += (_, _) => ApplyItemSurfaceVisual(border, isHovered: true);
        border.MouseLeave += (_, _) => ApplyItemSurfaceVisual(border, isHovered: false);
        ApplyItemSurfaceVisual(border, isHovered: false);
        border.MouseLeftButtonDown += OnItemMouseDown;
        border.MouseRightButtonDown += (_, _) =>
            ItemSelected?.Invoke(this, new ItemActionEventArgs(item));
        border.MouseMove += OnItemMouseMove;
        border.ContextMenu = BuildItemContextMenu(item);
        return border;
    }

    private void ApplyItemSurfaceVisual(Border border, bool isHovered)
    {
        var isSelected = border.Tag is DesktopItem item && _selectedItemId == item.Id;
        if (isSelected)
        {
            border.Background = new SolidColorBrush(_lightTheme
                ? Color.FromArgb(48, 40, 154, 137)
                : Color.FromArgb(62, 118, 215, 196));
            border.BorderBrush = (Brush)FindResource("AccentBrush");
            border.BorderThickness = new Thickness(1);
            return;
        }

        border.Background = isHovered ? (Brush)FindResource("PanelHoverBrush") : Brushes.Transparent;
        border.BorderBrush = Brushes.Transparent;
        border.BorderThickness = new Thickness(1);
    }

    private ContextMenu BuildItemContextMenu(DesktopItem item)
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "打开" };
        open.Click += (_, _) => ItemOpenRequested?.Invoke(this, new ItemActionEventArgs(item));
        menu.Items.Add(open);
        if (DesktopItem.IsShellLocation(item.Path))
        {
            return RegisterTransientMenu(menu);
        }

        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => ItemRenameRequested?.Invoke(this, new ItemActionEventArgs(item));
        var reveal = new MenuItem { Header = "打开所在位置" };
        reveal.Click += (_, _) => ItemRevealRequested?.Invoke(this, new ItemActionEventArgs(item));
        var properties = new MenuItem { Header = "属性" };
        properties.Click += (_, _) => ItemPropertiesRequested?.Invoke(this, new ItemActionEventArgs(item));
        var delete = new MenuItem { Header = "删除（移入回收站）", Foreground = (Brush)FindResource("DangerBrush") };
        delete.Click += (_, _) => ItemDeleteRequested?.Invoke(this, new ItemActionEventArgs(item));
        var remove = new MenuItem { Header = "移出分区（不删除文件）" };
        remove.Click += (_, _) => ItemRemoveRequested?.Invoke(this, new ItemActionEventArgs(item));
        menu.Items.Add(rename);
        menu.Items.Add(reveal);
        menu.Items.Add(properties);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.Items.Add(remove);
        return RegisterTransientMenu(menu);
    }

    internal void CloseTransientMenus()
    {
        if (_openContextMenu is not null)
        {
            _openContextMenu.IsOpen = false;
            _openContextMenu = null;
        }
    }

    private ContextMenu RegisterTransientMenu(ContextMenu menu)
    {
        menu.StaysOpen = false;
        menu.Opened += (_, _) =>
        {
            if (_openContextMenu is not null && _openContextMenu != menu)
            {
                _openContextMenu.IsOpen = false;
            }

            _openContextMenu = menu;
        };
        menu.Closed += (_, _) =>
        {
            if (_openContextMenu == menu)
            {
                _openContextMenu = null;
            }
        };
        return menu;
    }

    private void OnHeaderMouseEnter(object sender, MouseEventArgs e)
    {
        _headerActionsVisible = true;
        UpdateHeaderPresentation(animate: true);
    }

    private void OnHeaderMouseLeave(object sender, MouseEventArgs e)
    {
        _headerActionsVisible = false;
        UpdateHeaderPresentation(animate: true);
    }

    private void UpdateHeaderPresentation(bool animate)
    {
        if (HeaderSurface.ActualWidth <= 0)
        {
            return;
        }

        HeaderAccessories.IsHitTestVisible = _headerActionsVisible;
        HeaderTitleHost.MaxWidth = Math.Max(36, HeaderSurface.ActualWidth - (_headerActionsVisible ? 90 : 28));
        HeaderTitleHost.UpdateLayout();

        var centeredOffset = Math.Max(0,
            (HeaderSurface.ActualWidth - HeaderTitleHost.ActualWidth) / 2 - HeaderTitleHost.Margin.Left);
        var titleTarget = _headerActionsVisible ? 0 : centeredOffset;
        var accessoriesTarget = _headerActionsVisible ? 1d : 0d;

        AnimateHeaderValue(HeaderTitleTranslation, TranslateTransform.XProperty, titleTarget, animate ? 170 : 0);
        AnimateHeaderValue(HeaderAccessories, OpacityProperty, accessoriesTarget, animate ? 140 : 0);
    }

    private static void AnimateHeaderValue(
        DependencyObject target,
        DependencyProperty property,
        double value,
        double durationMilliseconds)
    {
        var current = (double)target.GetValue(property);
        BeginAnimation(target, property, null);
        target.SetValue(property, value);
        if (durationMilliseconds <= 0 || Math.Abs(current - value) < 0.01)
        {
            return;
        }

        BeginAnimation(target, property, new DoubleAnimation(
            current,
            value,
            TimeSpan.FromMilliseconds(durationMilliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private static void BeginAnimation(
        DependencyObject target,
        DependencyProperty property,
        AnimationTimeline? animation)
    {
        if (target is Animatable animatable)
        {
            animatable.BeginAnimation(property, animation);
        }
        else if (target is UIElement element)
        {
            element.BeginAnimation(property, animation);
        }
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
        if (Model.ViewMode == "List")
        {
            RenderItems();
        }
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
            width = Math.Max(150, _resizeStartBounds.Width - _resizeHorizontalChange);
            x = _resizeStartBounds.Right - width;
            if (x < HorizontalDesktopInset)
            {
                x = HorizontalDesktopInset;
                width = _resizeStartBounds.Right - HorizontalDesktopInset;
            }
        }
        else if (direction is "Right" or "TopRight" or "BottomRight")
        {
            width = Math.Max(150, _resizeStartBounds.Width + _resizeHorizontalChange);
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
        _hasScrollableContent = maximum > 0.5;
        UpdateSlimScrollBarVisibility();
    }

    private void OnCardMouseEnter(object sender, MouseEventArgs e) => UpdateSlimScrollBarVisibility();

    private void OnCardMouseLeave(object sender, MouseEventArgs e)
    {
        if (!SlimScrollBar.IsMouseCaptureWithin)
        {
            SlimScrollBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSlimScrollBarLostMouseCapture(object sender, MouseEventArgs e) => UpdateSlimScrollBarVisibility();

    private void UpdateSlimScrollBarVisibility()
    {
        SlimScrollBar.Visibility = !Model.IsCollapsed && _hasScrollableContent &&
                                   (IsMouseOver || SlimScrollBar.IsMouseCaptureWithin)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnSlimScroll(object sender, ScrollEventArgs e)
    {
        BodyScroller.ScrollToVerticalOffset(e.NewValue);
    }

    private void OnBodyRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject;
             current is not null && current != this;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: DesktopItem })
            {
                return;
            }
        }

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = false
        };
        var newFile = new MenuItem { Header = "新建文件" };
        newFile.Click += (_, _) => NewFileRequested?.Invoke(this, EventArgs.Empty);
        var newFolder = new MenuItem { Header = "新建文件夹" };
        newFolder.Click += (_, _) => NewFolderRequested?.Invoke(this, EventArgs.Empty);
        var newShortcut = new MenuItem { Header = "新建快捷方式" };
        newShortcut.Click += (_, _) => NewShortcutRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(newFile);
        menu.Items.Add(newFolder);
        menu.Items.Add(newShortcut);
        RegisterTransientMenu(menu);
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => BeginRename();
        var viewMode = new MenuItem { Header = "显示方式" };
        var iconMode = new MenuItem
        {
            Header = "图标",
            IsCheckable = true,
            IsChecked = Model.ViewMode != "List"
        };
        iconMode.Click += (_, _) => SetViewMode("Icons");
        var listMode = new MenuItem
        {
            Header = "列表",
            IsCheckable = true,
            IsChecked = Model.ViewMode == "List"
        };
        listMode.Click += (_, _) => SetViewMode("List");
        viewMode.Items.Add(iconMode);
        viewMode.Items.Add(listMode);

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
        menu.Items.Add(viewMode);
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.PlacementTarget = MoreButton;
        RegisterTransientMenu(menu);
        menu.IsOpen = true;
    }

    private void SetViewMode(string viewMode)
    {
        if (Model.ViewMode == viewMode)
        {
            return;
        }

        Model.ViewMode = viewMode;
        RenderItems();
        ModelChanged?.Invoke(this, EventArgs.Empty);
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
            Dispatcher.BeginInvoke(() => UpdateHeaderPresentation(animate: false));
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
        if (sender is FrameworkElement { Tag: DesktopItem selectedItem })
        {
            ItemSelected?.Invoke(this, new ItemActionEventArgs(selectedItem));
        }

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
        if (File.Exists(item.Path) || Directory.Exists(item.Path))
        {
            data.SetFileDropList(new StringCollection { item.Path });
        }

        SelectionClearRequested?.Invoke(this, EventArgs.Empty);
        StartDragPreview(item, (FrameworkElement)sender);
        GiveFeedback += OnItemDragGiveFeedback;
        var effects = DragDropEffects.None;
        try
        {
            effects = DragDrop.DoDragDrop(this, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            GiveFeedback -= OnItemDragGiveFeedback;
            StopDragPreview();
            HideDropIndicator();
            DragPreviewEnded?.Invoke();
        }

        ItemDragCompleted?.Invoke(this, new ItemDragCompletedEventArgs(item.Path, effects));
    }

    private void StartDragPreview(DesktopItem item, FrameworkElement sourceElement)
    {
        _dragSourceElement = sourceElement;
        sourceElement.Opacity = 0.18;

        var iconSize = Math.Clamp(_iconSize, 36, 54);
        var content = new StackPanel();
        content.Children.Add(new Image
        {
            Width = iconSize,
            Height = iconSize,
            Source = _iconService.GetIcon(item.Path),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = item.DisplayName,
            MaxWidth = Math.Max(86, sourceElement.ActualWidth),
            Margin = new Thickness(4, 5, 4, 0),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _lightTheme ? new SolidColorBrush(Color.FromRgb(27, 34, 46)) : Brushes.White,
            FontSize = 11
        });

        _dragPreviewPopup = new Popup
        {
            AllowsTransparency = true,
            IsHitTestVisible = false,
            Placement = PlacementMode.AbsolutePoint,
            StaysOpen = true,
            Child = new Border
            {
                MinWidth = Math.Max(92, sourceElement.ActualWidth),
                Padding = new Thickness(9),
                CornerRadius = new CornerRadius(12),
                Background = _headerBackground,
                BorderBrush = (Brush)FindResource("AccentBrush"),
                BorderThickness = new Thickness(1),
                Opacity = 0.92,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 20,
                    ShadowDepth = 7,
                    Opacity = 0.48,
                    Color = Color.FromRgb(5, 8, 13)
                },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1.04, 1.04),
                Child = content
            }
        };
        UpdateDragPreviewPosition();
        _dragPreviewPopup.IsOpen = true;
        Dispatcher.BeginInvoke(MakeDragPreviewClickThrough, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void MakeDragPreviewClickThrough()
    {
        if (_dragPreviewPopup?.Child is not Visual previewVisual ||
            PresentationSource.FromVisual(previewVisual) is not HwndSource source)
        {
            return;
        }

        var extendedStyle = NativeMethods.GetWindowLongPtr(source.Handle, NativeMethods.GwlExStyle).ToInt64();
        NativeMethods.SetWindowLongPtr(
            source.Handle,
            NativeMethods.GwlExStyle,
            new nint(extendedStyle |
                     NativeMethods.WsExTransparent |
                     NativeMethods.WsExNoActivate |
                     NativeMethods.WsExToolWindow));
    }

    private void OnItemDragGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        UpdateDragPreviewPosition();
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void UpdateDragPreviewPosition()
    {
        if (_dragPreviewPopup is null || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        // Keep the preview close to the pointer without covering its native hit-test point.
        // The popup is also marked WS_EX_TRANSPARENT after creation so drop targets keep receiving events.
        _dragPreviewPopup.HorizontalOffset = cursor.X / dpi.DpiScaleX + 18;
        _dragPreviewPopup.VerticalOffset = cursor.Y / dpi.DpiScaleY + 18;
    }

    private void StopDragPreview()
    {
        if (_dragPreviewPopup is not null)
        {
            _dragPreviewPopup.IsOpen = false;
            _dragPreviewPopup.Child = null;
            _dragPreviewPopup = null;
        }

        if (_dragSourceElement is not null)
        {
            _dragSourceElement.Opacity = 1;
            _dragSourceElement = null;
        }
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var paths = GetFileDropPaths(e);
        if (paths.Length == 0 || sender is not FrameworkElement { Tag: DesktopItem folder } ||
            !Directory.Exists(folder.Path))
        {
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnFolderDrop(object sender, DragEventArgs e)
    {
        var paths = GetFileDropPaths(e);
        if (paths.Length == 0 || sender is not FrameworkElement { Tag: DesktopItem folder } ||
            !Directory.Exists(folder.Path))
        {
            return;
        }

        FolderFilesDropped?.Invoke(this, new FolderFilesDroppedEventArgs(folder, paths));
        e.Handled = true;
    }

    private static string[] GetFileDropPaths(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            return paths.Where(path => File.Exists(path) || Directory.Exists(path)).ToArray();
        }

        if (e.Data.GetData(DataFormats.FileDrop) is StringCollection collection)
        {
            return collection.Cast<string>()
                .Where(path => File.Exists(path) || Directory.Exists(path))
                .ToArray();
        }

        return [];
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var supportsDrop = e.Data.GetDataPresent(InternalItemFormat) || e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = supportsDrop ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Data.GetData(InternalItemFormat) is ItemDragPayload payload)
        {
            _activeDragPayload = payload;
            if (!_dragTrackingTimer.IsEnabled)
            {
                _dragTrackingTimer.Start();
            }

            UpdateDragReflow(payload, e.GetPosition(ItemsPanel));
        }
        else
        {
            HideDropIndicator();
        }

        e.Handled = true;
    }

    private void TrackActiveDragPointer()
    {
        if (_activeDragPayload is not { } payload || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        // RenderZones can replace a card while the dispatcher still has one final timer tick
        // queued. A detached visual cannot perform screen-to-local coordinate conversion.
        if (!IsLoaded || PresentationSource.FromVisual(this) is null)
        {
            HideDropIndicator();
            return;
        }

        try
        {
            var screenPoint = new Point(cursor.X, cursor.Y);
            var cardPoint = PointFromScreen(screenPoint);
            if (cardPoint.X < 0 || cardPoint.Y < 0 || cardPoint.X > ActualWidth || cardPoint.Y > ActualHeight)
            {
                HideDropIndicator();
                return;
            }

            UpdateDragReflow(payload, ItemsPanel.PointFromScreen(screenPoint));
        }
        catch (InvalidOperationException)
        {
            // The visual may have been detached between the guard and the conversion.
            HideDropIndicator();
        }
    }

    private void UpdateDragReflow(ItemDragPayload payload, Point pointer)
    {
        DragPreviewActivated?.Invoke(this);
        var placement = GetItemDropPlacement(pointer);
        ShowDropIndicator(placement);
        ApplyLiveItemReflow(payload, placement);
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave bubbles whenever the pointer crosses a child tile. Resetting the preview for
        // those internal transitions makes every tile jump back and reflow again on the next frame.
        // Only clear the mobile-style preview after the pointer has actually left the whole card.
        var point = e.GetPosition(this);
        if (point.X < 0 || point.Y < 0 || point.X > ActualWidth || point.Y > ActualHeight)
        {
            HideDropIndicator();
        }
    }

    private DropPlacement GetItemDropPlacement(DragEventArgs e) =>
        GetItemDropPlacement(e.GetPosition(ItemsPanel));

    private DropPlacement GetItemDropPlacement(Point pointer)
    {
        var itemElements = ItemsPanel.Children
            .OfType<FrameworkElement>()
            .Where(element => element.Tag is DesktopItem)
            .ToArray();
        if (itemElements.Length == 0)
        {
            return new DropPlacement(0, null, PlaceAfter: true, RowIndex: 0);
        }

        // Use immutable layout slots. Animated elements never become hit-test boundaries.
        var slots = itemElements
            .Select((element, index) => new ItemLayoutSlot(index, element, GetLayoutOrigin(element)))
            .ToArray();

        if (Model.ViewMode == "List")
        {
            var candidateIndex = slots.Length;
            foreach (var slot in slots)
            {
                if (pointer.Y < slot.Origin.Y + slot.Element.ActualHeight / 2)
                {
                    candidateIndex = slot.Index;
                    break;
                }
            }

            // A dead band around each midpoint prevents tiny pointer changes from toggling two slots.
            if (_liveReflowIndex >= 0 && candidateIndex != _liveReflowIndex)
            {
                const double hysteresis = 9;
                if (candidateIndex > _liveReflowIndex)
                {
                    var boundarySlot = slots[Math.Min(_liveReflowIndex, slots.Length - 1)];
                    var boundary = boundarySlot.Origin.Y + boundarySlot.Element.ActualHeight / 2;
                    if (pointer.Y < boundary + hysteresis)
                    {
                        candidateIndex = _liveReflowIndex;
                    }
                }
                else
                {
                    var boundarySlot = slots[Math.Min(candidateIndex, slots.Length - 1)];
                    var boundary = boundarySlot.Origin.Y + boundarySlot.Element.ActualHeight / 2;
                    if (pointer.Y > boundary - hysteresis)
                    {
                        candidateIndex = _liveReflowIndex;
                    }
                }
            }

            candidateIndex = Math.Clamp(candidateIndex, 0, slots.Length);
            return candidateIndex == slots.Length
                ? new DropPlacement(candidateIndex, slots[^1].Element, PlaceAfter: true, RowIndex: candidateIndex)
                : new DropPlacement(candidateIndex, slots[candidateIndex].Element, PlaceAfter: false, RowIndex: candidateIndex);
        }

        var rows = new List<List<ItemLayoutSlot>>();
        foreach (var slot in slots)
        {
            var row = rows.LastOrDefault();
            if (row is null || Math.Abs(row[0].Origin.Y - slot.Origin.Y) > 2)
            {
                row = [];
                rows.Add(row);
            }

            row.Add(slot);
        }

        var rowCenters = rows
            .Select(row => row.Average(slot => slot.Origin.Y + slot.Element.ActualHeight / 2))
            .ToArray();
        var selectedRowIndex = Enumerable.Range(0, rows.Count)
            .MinBy(index => Math.Abs(pointer.Y - rowCenters[index]));

        // Switching rows requires crossing the midpoint plus a small dead band. This is the key
        // to stable vertical reordering when icons are animating in opposite directions.
        if (_liveReflowRow >= 0 && _liveReflowRow < rows.Count && selectedRowIndex != _liveReflowRow)
        {
            const double rowHysteresis = 10;
            if (selectedRowIndex > _liveReflowRow && _liveReflowRow + 1 < rows.Count)
            {
                var boundary = (rowCenters[_liveReflowRow] + rowCenters[_liveReflowRow + 1]) / 2;
                if (pointer.Y < boundary + rowHysteresis)
                {
                    selectedRowIndex = _liveReflowRow;
                }
            }
            else if (selectedRowIndex < _liveReflowRow && _liveReflowRow > 0)
            {
                var boundary = (rowCenters[_liveReflowRow - 1] + rowCenters[_liveReflowRow]) / 2;
                if (pointer.Y > boundary - rowHysteresis)
                {
                    selectedRowIndex = _liveReflowRow;
                }
            }
        }

        var selectedRow = rows[selectedRowIndex];
        foreach (var slot in selectedRow)
        {
            if (pointer.X < slot.Origin.X + slot.Element.ActualWidth / 2)
            {
                return new DropPlacement(slot.Index, slot.Element, PlaceAfter: false, RowIndex: selectedRowIndex);
            }
        }

        var rowLast = selectedRow[^1];
        return new DropPlacement(
            rowLast.Index + 1,
            rowLast.Element,
            PlaceAfter: true,
            RowIndex: selectedRowIndex);
    }

    private void ApplyLiveItemReflow(ItemDragPayload payload, DropPlacement placement)
    {
        if (_liveReflowIndex == placement.Index && _liveReflowItemId == payload.ItemId)
        {
            return;
        }

        _liveReflowIndex = placement.Index;
        _liveReflowRow = placement.RowIndex;
        _liveReflowItemId = payload.ItemId;
        var elements = ItemsPanel.Children
            .OfType<FrameworkElement>()
            .Where(element => element.Tag is DesktopItem)
            .ToArray();
        if (elements.Length == 0)
        {
            return;
        }

        var slots = elements.Select(GetLayoutOrigin).ToList();
        var sourceIndex = payload.SourceZoneId == Model.Id
            ? Array.FindIndex(elements, element => element.Tag is DesktopItem item && item.Id == payload.ItemId)
            : -1;

        if (sourceIndex >= 0)
        {
            var insertionIndex = Math.Clamp(placement.Index, 0, elements.Length);
            if (sourceIndex < insertionIndex)
            {
                insertionIndex--;
            }

            var projected = elements.ToList();
            var source = projected[sourceIndex];
            projected.RemoveAt(sourceIndex);
            projected.Insert(Math.Clamp(insertionIndex, 0, projected.Count), source);
            for (var index = 0; index < elements.Length; index++)
            {
                var destinationIndex = projected.IndexOf(elements[index]);
                AnimateItemToSlot(elements[index], slots[index], slots[destinationIndex]);
            }

            return;
        }

        slots.Add(GetNextSlot(elements, slots));
        var targetIndex = Math.Clamp(placement.Index, 0, elements.Length);
        for (var index = 0; index < elements.Length; index++)
        {
            var destinationIndex = index >= targetIndex ? index + 1 : index;
            AnimateItemToSlot(elements[index], slots[index], slots[destinationIndex]);
        }
    }

    private Point GetLayoutOrigin(FrameworkElement element)
    {
        var origin = element.TranslatePoint(new Point(), ItemsPanel);
        if (element.RenderTransform is TranslateTransform translation)
        {
            origin.X -= translation.X;
            origin.Y -= translation.Y;
        }

        return origin;
    }

    private Point GetNextSlot(IReadOnlyList<FrameworkElement> elements, IReadOnlyList<Point> slots)
    {
        var lastIndex = elements.Count - 1;
        var last = elements[lastIndex];
        var lastSlot = slots[lastIndex];
        if (Model.ViewMode == "List")
        {
            var step = elements.Count > 1
                ? Math.Max(1, slots[lastIndex].Y - slots[lastIndex - 1].Y)
                : last.ActualHeight + last.Margin.Top + last.Margin.Bottom;
            return new Point(lastSlot.X, lastSlot.Y + step);
        }

        var horizontalStep = last.ActualWidth + last.Margin.Left + last.Margin.Right;
        var nextX = lastSlot.X + horizontalStep;
        if (nextX + last.ActualWidth <= ItemsPanel.ActualWidth + 0.5)
        {
            return new Point(nextX, lastSlot.Y);
        }

        var firstX = slots.Min(point => point.X);
        var verticalStep = last.ActualHeight + last.Margin.Top + last.Margin.Bottom;
        return new Point(firstX, lastSlot.Y + verticalStep);
    }

    private static void AnimateItemToSlot(FrameworkElement element, Point origin, Point destination)
    {
        var translation = element.RenderTransform as TranslateTransform;
        if (translation is null)
        {
            translation = new TranslateTransform();
            element.RenderTransform = translation;
        }

        AnimateTranslation(translation, TranslateTransform.XProperty, destination.X - origin.X);
        AnimateTranslation(translation, TranslateTransform.YProperty, destination.Y - origin.Y);
    }

    private static void AnimateTranslation(
        TranslateTransform translation,
        DependencyProperty property,
        double target)
    {
        var current = (double)translation.GetValue(property);
        translation.BeginAnimation(property, null);
        translation.SetValue(property, target);
        if (Math.Abs(current - target) < 0.1)
        {
            return;
        }

        translation.BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(115))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private void ResetLiveItemReflow()
    {
        _liveReflowIndex = -1;
        _liveReflowRow = -1;
        _liveReflowItemId = Guid.Empty;
        foreach (var element in ItemsPanel.Children.OfType<FrameworkElement>().Where(element => element.Tag is DesktopItem))
        {
            element.RenderTransform = null;
        }
    }

    private void ShowDropIndicator(DropPlacement placement)
    {
        if (_dropIndicatorIndex == placement.Index)
        {
            return;
        }

        _dropIndicatorIndex = placement.Index;

        double left;
        double top;
        double width;
        double height;
        if (placement.Target is null)
        {
            left = 12;
            top = 14;
            width = Math.Max(24, BodyArea.ActualWidth - 24);
            height = 3;
        }
        else
        {
            var origin = placement.Target.TranslatePoint(new Point(), BodyArea);
            if (Model.ViewMode == "List")
            {
                left = 10;
                top = origin.Y + (placement.PlaceAfter ? placement.Target.ActualHeight : 0) - 1.5;
                width = Math.Max(24, BodyArea.ActualWidth - 20);
                height = 3;
            }
            else
            {
                left = origin.X + (placement.PlaceAfter ? placement.Target.ActualWidth : 0) - 1.5;
                top = origin.Y + 6;
                width = 3;
                height = Math.Max(24, placement.Target.ActualHeight - 12);
            }
        }

        DropIndicator.Width = width;
        DropIndicator.Height = height;
        AnimateIndicatorPosition(Canvas.LeftProperty, left);
        AnimateIndicatorPosition(Canvas.TopProperty, top);
        DropIndicator.Visibility = Visibility.Visible;
        DropIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(420))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void AnimateIndicatorPosition(DependencyProperty property, double target)
    {
        var current = (double)DropIndicator.GetValue(property);
        if (double.IsNaN(current))
        {
            current = target;
        }

        DropIndicator.BeginAnimation(property, null);
        DropIndicator.SetValue(property, target);
        DropIndicator.BeginAnimation(property, new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(110))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    internal void ClearDragPreview() => HideDropIndicator();

    private void HideDropIndicator()
    {
        _dragTrackingTimer.Stop();
        _activeDragPayload = null;
        _dropIndicatorIndex = -1;
        ResetLiveItemReflow();
        DropIndicator.BeginAnimation(OpacityProperty, null);
        DropIndicator.Opacity = 0;
        DropIndicator.Visibility = Visibility.Collapsed;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var placement = GetItemDropPlacement(e);
        HideDropIndicator();
        if (e.Data.GetData(InternalItemFormat) is ItemDragPayload payload)
        {
            ItemMoveRequested?.Invoke(this, new ItemMoveEventArgs(payload, placement.Index));
            e.Handled = true;
            return;
        }

        var paths = GetFileDropPaths(e);
        if (paths.Length > 0)
        {
            FilesDropped?.Invoke(this, new FilesDroppedEventArgs(paths));
            e.Handled = true;
        }
    }

    private readonly record struct ItemLayoutSlot(int Index, FrameworkElement Element, Point Origin);

    private readonly record struct DropPlacement(
        int Index,
        FrameworkElement? Target,
        bool PlaceAfter,
        int RowIndex);
}
