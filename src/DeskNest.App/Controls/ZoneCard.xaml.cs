using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskNest.App.Services;
using DeskNest.Core.Models;

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

    private readonly ShellIconService _iconService;
    private Point _headerDragStart;
    private Point _itemDragStart;
    private double _modelStartX;
    private double _modelStartY;
    private bool _draggingHeader;
    private bool _layoutLocked;
    private bool _lightTheme;
    private double _iconSize = 44;

    internal ZoneCard(ZoneModel model, ShellIconService iconService)
    {
        Model = model;
        _iconService = iconService;
        InitializeComponent();
        ApplyModel();
    }

    public ZoneModel Model { get; }

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
        ResizeThumb.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
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
        CardBorder.Background = new SolidColorBrush(color);
        TitleText.Foreground = foreground;
        TitleEditor.Foreground = foreground;
        CountText.Foreground = lightTheme ? new SolidColorBrush(Color.FromRgb(82, 94, 112)) : (Brush)FindResource("TextMutedBrush");
        CollapseButton.Foreground = foreground;
        MoreButton.Foreground = foreground;
        RenderItems();
    }

    public void RefreshItems()
    {
        RenderItems();
    }

    private void ApplyModel()
    {
        Width = Model.Width;
        Height = Model.IsCollapsed ? 58 : Model.Height;
        TitleText.Text = Model.Name;
        CountText.Text = Model.Items.Count.ToString();
        CollapseButton.Content = Model.IsCollapsed ? "⌄" : "⌃";
        BodyScroller.Visibility = Model.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ResizeThumb.Visibility = Model.IsCollapsed || _layoutLocked ? Visibility.Collapsed : Visibility.Visible;

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
        if (_layoutLocked || e.ClickCount > 1 || Parent is not IInputElement parent)
        {
            return;
        }

        _headerDragStart = e.GetPosition(parent);
        _modelStartX = Model.X;
        _modelStartY = Model.Y;
        _draggingHeader = true;
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHeader || e.LeftButton != MouseButtonState.Pressed || Parent is not IInputElement parent)
        {
            return;
        }

        var current = e.GetPosition(parent);
        Model.X = Math.Max(0, _modelStartX + current.X - _headerDragStart.X);
        Model.Y = Math.Max(72, _modelStartY + current.Y - _headerDragStart.Y);
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
        Mouse.Capture(null);
        ModelChanged?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnResizeDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_layoutLocked || Model.IsCollapsed)
        {
            return;
        }

        Model.Width = Math.Clamp(Model.Width + e.HorizontalChange, 240, 900);
        Model.Height = Math.Clamp(Model.Height + e.VerticalChange, 150, 700);
        Width = Model.Width;
        Height = Model.Height;
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        Model.IsCollapsed = !Model.IsCollapsed;
        ApplyModel();
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var rename = new MenuItem { Header = "重命名" };
        rename.Click += (_, _) => BeginRename();
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
        menu.Items.Add(clear);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        menu.PlacementTarget = MoreButton;
        menu.IsOpen = true;
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
