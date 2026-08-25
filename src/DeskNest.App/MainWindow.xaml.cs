using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DeskNest.App.Controls;
using DeskNest.App.Interop;
using DeskNest.App.Services;
using DeskNest.Core.Models;
using DeskNest.Core.Services;
using Microsoft.Win32;

namespace DeskNest.App;

public partial class MainWindow : System.Windows.Window
{
    private readonly JsonStateStore _stateStore = new();
    private readonly RuleClassifier _classifier = new();
    private readonly ShellIconService _iconService = new();
    private int _toolbarFlightVersion;
    private readonly ShellService _shellService = new();
    private readonly DesktopHostService _desktopHost = new();
    private readonly DesktopIconVisibilityService _iconVisibility = new();
    private readonly DesktopWallpaperService _wallpaperService = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;

    private AppState _state = AppState.CreateDefault();
    private DesktopWatcher? _desktopWatcher;
    private TrayService? _trayService;
    private HwndSource? _windowSource;
    private bool _exitRequested;
    private bool _loaded;

    public MainWindow()
    {
        InitializeComponent();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(550) };
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            await SaveStateAsync();
        };
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusBorder.Visibility = Visibility.Collapsed;
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OnWindowMessage);
        _desktopHost.TryEmbed(this);
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != NativeMethods.WmNcHitTest)
        {
            return 0;
        }

        var packedPoint = lParam.ToInt64();
        var screenPoint = new Point(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
        var clientPoint = PointFromScreen(screenPoint);

        handled = true;
        return IsInteractivePoint(clientPoint)
            ? NativeMethods.HtClient
            : NativeMethods.HtTransparent;
    }

    private bool IsInteractivePoint(Point point)
    {
        if (ContainsPoint(ToolbarBorder, point))
        {
            return true;
        }

        return DesktopCanvas.Children
            .OfType<ZoneCard>()
            .Any(card => ContainsPoint(card, point));
    }

    private bool ContainsPoint(FrameworkElement element, Point point)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            var bounds = element.TransformToAncestor(this)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return bounds.Contains(point);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var isFirstRun = !File.Exists(_stateStore.StatePath);
        _state = await _stateStore.LoadAsync();
        if (!string.IsNullOrWhiteSpace(_state.WallpaperSelection))
        {
            _wallpaperService.ApplySelection(_state.WallpaperSelection);
        }

        var layoutAdjusted = NormalizeZoneLayout();

        var shouldHideDesktopIcons = _state.DesktopIconsHidden;
        // A previous process may have left Explorer's icon view hidden. Start from the safe system
        // view, then switch to the saved mode only after all custom zones have rendered.
        _iconVisibility.SetVisibleWithoutTracking(true);

        var zonesRendered = false;
        try
        {
            RenderZones();
            zonesRendered = true;
        }
        catch (Exception exception)
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskNest");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(Path.Combine(logDirectory, "startup-error.log"), exception.ToString());

            _state.DesktopIconsHidden = false;
            DesktopCanvas.Visibility = Visibility.Collapsed;
            RequestSave();
        }

        UpdateToolbarState();
        await Dispatcher.Yield(DispatcherPriority.Render);
        if (zonesRendered && shouldHideDesktopIcons)
        {
            _iconVisibility.SetVisible(false);
        }
        if (layoutAdjusted)
        {
            RequestSave();
        }

        _desktopWatcher = new DesktopWatcher();
        _desktopWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshVisibleItems);
        _trayService = new TrayService(
            () => Dispatcher.Invoke(ShowDeskNest),
            () => Dispatcher.Invoke(() => OrganizeDesktop(showNotification: true)),
            () => Dispatcher.Invoke(ToggleDesktopMode),
            () => Dispatcher.Invoke(ExitApplication));
        _ = TrimWorkingSetAfterStartupAsync();

        if (isFirstRun)
        {
            OrganizeDesktop(showNotification: false);
            ShowStatus("已安全创建映射，桌面文件未被移动");
        }
        else
        {
            ShowStatus(_desktopHost.IsEmbedded ? "已挂载到 Windows 桌面" : "正在使用安全置底模式");
        }
    }

    private void RenderZones()
    {
        UpdateAlignmentGuides(null, null);
        DesktopCanvas.Children.Clear();
        var lightTheme = IsLightTheme();

        foreach (var zone in _state.Zones)
        {
            var card = new ZoneCard(zone, _iconService)
            {
                BoundsConstraint = ConstrainZoneBounds
            };
            card.SetLayoutLocked(_state.LayoutLocked);
            card.SetVisualOptions(_state.PanelOpacity, _state.IconSize, lightTheme);
            card.AlignmentGuidesChanged = UpdateAlignmentGuides;
            card.ModelChanged += (_, _) => RequestSave();
            card.DeleteRequested += (_, _) => DeleteZone(zone);
            card.FilesDropped += (_, args) => AddPathsToZone(zone, args.Paths);
            card.ItemMoveRequested += (_, args) => MoveItem(zone, args.Payload);
            card.ItemOpenRequested += (_, args) => OpenItem(args.Item);
            card.ItemRevealRequested += (_, args) => RevealItem(args.Item);
            card.ItemRemoveRequested += (_, args) => RemoveItem(zone, args.Item);

            Canvas.SetLeft(card, Math.Max(ZoneCard.HorizontalDesktopInset, zone.X));
            Canvas.SetTop(card, Math.Max(72, zone.Y));
            DesktopCanvas.Children.Add(card);
        }

        DesktopCanvas.Visibility = _state.DesktopIconsHidden
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyThemeSurface(lightTheme);
    }

    private ZoneAlignmentResult ConstrainZoneBounds(ZoneModel zone, ZoneBounds current, ZoneBounds desired)
    {
        var obstacles = _state.Zones
            .Where(other => other.Id != zone.Id)
            .Select(GetVisualBounds)
            .ToArray();
        var minimumX = ZoneCard.HorizontalDesktopInset;
        var maximumRight = Math.Max(minimumX + 1, ActualWidth - ZoneCard.HorizontalDesktopInset);
        var maximumBottom = Math.Max(73, ActualHeight);
        var collisionSafe = ZoneCollisionResolver.Constrain(
            current,
            desired,
            obstacles,
            12,
            minimumX,
            72,
            maximumRight,
            maximumBottom);
        return ZoneAlignmentResolver.Snap(
            current,
            desired,
            collisionSafe,
            obstacles,
            10,
            12,
            minimumX,
            72,
            maximumRight,
            maximumBottom);
    }

    private void UpdateAlignmentGuides(double? verticalGuide, double? horizontalGuide)
    {
        if (verticalGuide is { } x)
        {
            VerticalAlignmentGuide.X1 = x;
            VerticalAlignmentGuide.X2 = x;
            VerticalAlignmentGuide.Y1 = 72;
            VerticalAlignmentGuide.Y2 = Math.Max(73, ActualHeight);
            VerticalAlignmentGuide.Visibility = Visibility.Visible;
        }
        else
        {
            VerticalAlignmentGuide.Visibility = Visibility.Collapsed;
        }

        if (horizontalGuide is { } y)
        {
            HorizontalAlignmentGuide.X1 = 0;
            HorizontalAlignmentGuide.X2 = Math.Max(1, ActualWidth);
            HorizontalAlignmentGuide.Y1 = y;
            HorizontalAlignmentGuide.Y2 = y;
            HorizontalAlignmentGuide.Visibility = Visibility.Visible;
        }
        else
        {
            HorizontalAlignmentGuide.Visibility = Visibility.Collapsed;
        }
    }

    private bool NormalizeZoneLayout()
    {
        var minimumX = ZoneCard.HorizontalDesktopInset;
        var desktopWidth = ActualWidth > 0 ? ActualWidth : SystemParameters.VirtualScreenWidth;
        var maximumRight = Math.Max(minimumX + 1, desktopWidth - ZoneCard.HorizontalDesktopInset);
        var maximumBottom = Math.Max(73, ActualHeight > 0 ? ActualHeight : SystemParameters.VirtualScreenHeight);
        var adjusted = false;

        if (_state.Zones.Count > 0)
        {
            var leftmost = _state.Zones.Min(zone => zone.X);
            var rightmost = _state.Zones.Max(zone => zone.X + zone.Width);
            var horizontalShift = leftmost < minimumX
                ? minimumX - leftmost
                : rightmost > maximumRight
                    ? maximumRight - rightmost
                    : 0;
            if (Math.Abs(horizontalShift) > 0.01 &&
                leftmost + horizontalShift >= minimumX && rightmost + horizontalShift <= maximumRight)
            {
                foreach (var zone in _state.Zones)
                {
                    zone.X += horizontalShift;
                }

                adjusted = true;
            }
        }

        var placed = new List<ZoneBounds>();

        foreach (var zone in _state.Zones)
        {
            var bounds = GetVisualBounds(zone);
            if (ZoneCollisionResolver.IsAvailable(bounds, placed, 12, minimumX, 72, maximumRight, maximumBottom))
            {
                placed.Add(bounds);
                continue;
            }

            var found = false;
            for (var y = 72d; y <= maximumBottom - bounds.Height && !found; y += 12)
            {
                for (var x = minimumX; x <= maximumRight - bounds.Width; x += 12)
                {
                    var candidate = bounds with { X = x, Y = y };
                    if (!ZoneCollisionResolver.IsAvailable(candidate, placed, 12, minimumX, 72, maximumRight, maximumBottom))
                    {
                        continue;
                    }

                    zone.X = x;
                    zone.Y = y;
                    bounds = candidate;
                    adjusted = true;
                    found = true;
                    break;
                }
            }

            placed.Add(bounds);
        }

        return adjusted;
    }

    private static ZoneBounds GetVisualBounds(ZoneModel zone)
    {
        return new ZoneBounds(zone.X, zone.Y, zone.Width, zone.IsCollapsed ? 52 : zone.Height);
    }

    private void OrganizeDesktop(bool showNotification)
    {
        var scanner = new DesktopScanner(_classifier);
        var scannedItems = scanner.ScanDefaultDesktops();
        var mappedPaths = _state.Zones
            .SelectMany(zone => zone.Items)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var item in scannedItems)
        {
            if (!mappedPaths.Add(item.Path))
            {
                continue;
            }

            var target = _state.Zones.FirstOrDefault(zone => zone.CategoryKey == item.CategoryKey)
                         ?? _state.Zones.FirstOrDefault(zone => zone.CategoryKey == "other");
            if (target is null)
            {
                target = CreateZone("other", "临时文件", "#FF8398");
                _state.Zones.Add(target);
            }

            target.Items.Add(item);
            added++;
        }

        RenderZones();
        RequestSave();
        var message = added == 0 ? "桌面映射已是最新状态" : $"已新增 {added} 个安全映射";
        ShowStatus(message);
        if (showNotification && added > 0)
        {
            _trayService?.ShowMessage("一键整理完成", $"新增 {added} 个映射，没有移动任何文件。");
        }
    }

    private void AddPathsToZone(ZoneModel targetZone, IReadOnlyList<string> paths)
    {
        var added = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                continue;
            }

            foreach (var zone in _state.Zones)
            {
                zone.Items.RemoveAll(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));
            }

            targetZone.Items.Add(DesktopItem.FromPath(path, targetZone.CategoryKey));
            added++;
        }

        RenderZones();
        RequestSave();
        ShowStatus(added == 0 ? "没有可添加的文件" : $"已添加 {added} 个映射到“{targetZone.Name}”");
    }

    private void MoveItem(ZoneModel targetZone, ItemDragPayload payload)
    {
        var sourceZone = _state.Zones.FirstOrDefault(zone => zone.Id == payload.SourceZoneId);
        var item = sourceZone?.Items.FirstOrDefault(value => value.Id == payload.ItemId);
        if (sourceZone is null || item is null || sourceZone.Id == targetZone.Id)
        {
            return;
        }

        sourceZone.Items.Remove(item);
        item.CategoryKey = targetZone.CategoryKey;
        targetZone.Items.Add(item);
        RenderZones();
        RequestSave();
        ShowStatus($"已移动到“{targetZone.Name}”");
    }

    private void OpenItem(DesktopItem item)
    {
        if (!_shellService.Open(item.Path))
        {
            ShowStatus("文件已失效，可右键移出映射");
        }
    }

    private void RevealItem(DesktopItem item)
    {
        if (!_shellService.Reveal(item.Path))
        {
            ShowStatus("无法定位：文件可能已被移动或删除");
        }
    }

    private void RemoveItem(ZoneModel zone, DesktopItem item)
    {
        zone.Items.Remove(item);
        RenderZones();
        RequestSave();
        ShowStatus("已移出分区，原文件保持不变");
    }

    private void DeleteZone(ZoneModel zone)
    {
        var result = System.Windows.MessageBox.Show(
            $"删除分区“{zone.Name}”？\n\n只会移除 {zone.Items.Count} 个映射，不会删除真实文件。",
            "删除分区",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _state.Zones.Remove(zone);
        RenderZones();
        RequestSave();
        ShowStatus("分区已删除，真实文件未受影响");
    }

    private void OnNewZoneClick(object sender, RoutedEventArgs e)
    {
        var index = _state.Zones.Count;
        var zone = CreateZone("other", "新分区", "#76D7C4");
        zone.X = 48 + index % 4 * 286;
        zone.Y = 116 + index / 4 * 244;
        _state.Zones.Add(zone);
        NormalizeZoneLayout();
        RenderZones();
        RequestSave();
        ShowStatus("已创建新分区，双击标题即可重命名");
    }

    private static ZoneModel CreateZone(string categoryKey, string name, string accent)
    {
        return new ZoneModel
        {
            CategoryKey = categoryKey,
            Name = name,
            AccentColor = accent,
            Width = 320,
            Height = 230
        };
    }

    private void OnOrganizeClick(object sender, RoutedEventArgs e) => OrganizeDesktop(showNotification: false);

    private void OnToggleLockClick(object sender, RoutedEventArgs e)
    {
        _state.LayoutLocked = !_state.LayoutLocked;
        UpdateToolbarState();
        RenderZones();
        RequestSave();
        ShowStatus(_state.LayoutLocked ? "布局已锁定" : "布局已解锁，可拖动和缩放分区");
    }

    private void OnToggleDesktopModeClick(object sender, RoutedEventArgs e) => ToggleDesktopMode();

    private void ToggleDesktopMode()
    {
        var enterCustomMode = !_state.DesktopIconsHidden;
        if (enterCustomMode)
        {
            DesktopCanvas.Visibility = Visibility.Visible;
            if (!_iconVisibility.SetVisible(false))
            {
                DesktopCanvas.Visibility = Visibility.Collapsed;
                ShowStatus("暂时无法切换 Windows 桌面图标层");
                return;
            }

            _state.DesktopIconsHidden = true;
        }
        else
        {
            if (!_iconVisibility.SetVisible(true))
            {
                ShowStatus("暂时无法切换 Windows 桌面图标层");
                return;
            }

            _state.DesktopIconsHidden = false;
            DesktopCanvas.Visibility = Visibility.Collapsed;
        }

        UpdateToolbarState();
        RequestSave();
        ShowStatus(enterCustomMode ? "已切换到自定义模式" : "已切换到系统模式");
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_state, PreviewAppearance) { Owner = this };
        if (settings.ShowDialog() != true)
        {
            return;
        }

        RenderZones();
        RequestSave();
        ShowStatus("外观设置已保存");
    }

    private void RefreshVisibleItems()
    {
        _iconService.Invalidate();
        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.RefreshItems();
        }
    }

    private void UpdateToolbarState()
    {
        ApplyToolbarAlignment();
        var customMode = _state.DesktopIconsHidden;
        LockButtonText.Text = _state.LayoutLocked ? "解锁布局" : "锁定布局";
        LockButtonIcon.Data = Geometry.Parse(_state.LayoutLocked
            ? "M 2,5 L 10,5 L 10,12 L 2,12 Z M 3,5 L 3,3.5 C 3,1.5 4.5,1 6,1 C 7.5,1 9,2 9,3.5"
            : "M 2,5 L 10,5 L 10,12 L 2,12 Z M 3,5 L 3,3.5 C 3,1.5 4.5,1 6,1 C 7.5,1 9,2 9,3.5 L 9,5");
        LockButton.ToolTip = _state.LayoutLocked ? "解锁布局" : "锁定布局";
        DesktopModeText.Text = customMode ? "自定义模式" : "系统模式";
        DesktopModeIcon.Data = Geometry.Parse(customMode
            ? "M 2,5 C 4,7 10,7 12,5 M 3,8 C 5,10 9,10 11,8 M 5,11 C 6.5,12 7.5,12 9,11 M 6,2 L 8,2"
            : "M 1,1 L 6,1 L 6,6 L 1,6 Z M 8,1 L 13,1 L 13,6 L 8,6 Z M 1,8 L 6,8 L 6,13 L 1,13 Z M 8,8 L 13,8 L 13,13 L 8,13 Z");
        DesktopModeButton.ToolTip = customMode ? "切换到系统模式" : "切换到自定义模式";
        NewZoneButton.IsEnabled = customMode;
        OrganizeButton.IsEnabled = customMode;
        LockButton.IsEnabled = customMode;
    }

    private void ApplyToolbarAlignment()
    {
        StopToolbarFlight();
        SetToolbarAlignment();
    }

    private void SetToolbarAlignment()
    {
        ToolbarBorder.HorizontalAlignment = _state.ToolbarAlignment switch
        {
            "Left" => System.Windows.HorizontalAlignment.Left,
            "Right" => System.Windows.HorizontalAlignment.Right,
            _ => System.Windows.HorizontalAlignment.Center
        };
    }

    private void AnimateToolbarAlignment()
    {
        if (!IsLoaded || ToolbarBorder.ActualWidth <= 0)
        {
            ApplyToolbarAlignment();
            return;
        }

        UpdateLayout();
        var oldLeft = ToolbarBorder.TransformToAncestor(this).Transform(new Point()).X;
        var oldTop = ToolbarBorder.TransformToAncestor(this).Transform(new Point()).Y;

        _toolbarFlightVersion++;
        var flightVersion = _toolbarFlightVersion;
        ToolbarTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        ToolbarTranslation.X = 0;
        ToolbarRocketCanvas.Children.Clear();
        SetToolbarAlignment();
        UpdateLayout();

        var targetLeft = ToolbarBorder.TransformToAncestor(this).Transform(new Point()).X;
        var distance = oldLeft - targetLeft;
        if (Math.Abs(distance) < 0.5)
        {
            ToolbarBorder.IsHitTestVisible = true;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(900);
        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        ToolbarBorder.IsHitTestVisible = false;
        ToolbarTranslation.X = distance;
        CreateToolbarRocketEffects(oldLeft, targetLeft, oldTop, distance < 0, duration, easing);

        var movement = new DoubleAnimation(distance, 0, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        };
        movement.Completed += (_, _) =>
        {
            if (flightVersion != _toolbarFlightVersion)
            {
                return;
            }

            ToolbarTranslation.X = 0;
            ToolbarRocketCanvas.Children.Clear();
            ToolbarBorder.IsHitTestVisible = true;
        };
        ToolbarTranslation.BeginAnimation(TranslateTransform.XProperty, movement);
    }

    private void CreateToolbarRocketEffects(
        double oldLeft,
        double targetLeft,
        double top,
        bool movingRight,
        TimeSpan duration,
        IEasingFunction easing)
    {
        var width = ToolbarBorder.ActualWidth;
        var centerY = top + ToolbarBorder.ActualHeight / 2;
        var arrow = new System.Windows.Shapes.Path
        {
            Width = 12,
            Height = 14,
            Stretch = Stretch.Fill,
            Data = Geometry.Parse(movingRight ? "M 0,0 L 12,7 L 0,14 Z" : "M 12,0 L 0,7 L 12,14 Z"),
            Fill = (Brush)FindResource("AccentBrush"),
            Effect = new DropShadowEffect
            {
                BlurRadius = 7,
                ShadowDepth = 0,
                Opacity = 0.8,
                Color = Color.FromRgb(118, 215, 196)
            }
        };
        var arrowStart = movingRight ? oldLeft + width + 5 : oldLeft - 17;
        var arrowTarget = movingRight ? targetLeft + width + 5 : targetLeft - 17;
        Canvas.SetLeft(arrow, arrowStart);
        Canvas.SetTop(arrow, centerY - 7);
        ToolbarRocketCanvas.Children.Add(arrow);
        arrow.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(arrowStart, arrowTarget, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        });
        arrow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
        {
            BeginTime = TimeSpan.FromMilliseconds(750),
            FillBehavior = FillBehavior.Stop
        });

        var verticalOffsets = new[]
        {
            -9d, -4d, 4d, 9d, -7d, 7d, -2d, 2d, -10d,
            10d, 0d, -6d, 6d, -3d, 3d, -8d, 8d, 0d
        };
        var particleBeginTimes = new[]
        {
            20d, 100d, 180d, 250d, 320d, 380d, 440d, 495d, 545d,
            590d, 630d, 665d, 700d, 730d, 755d, 775d, 795d, 810d
        };
        var particleColors = new[]
        {
            Color.FromRgb(217, 255, 247),
            Color.FromRgb(118, 215, 196),
            Colors.White
        };
        for (var index = 0; index < verticalOffsets.Length; index++)
        {
            var size = index % 4 == 0 ? 8d : index % 3 == 0 ? 7d : index % 2 == 0 ? 6d : 5d;
            var beginMilliseconds = particleBeginTimes[index];
            var progress = easing.Ease(beginMilliseconds / duration.TotalMilliseconds);
            var toolbarLeft = oldLeft + (targetLeft - oldLeft) * progress;
            var origin = movingRight ? toolbarLeft - size - 8 : toolbarLeft + width + 8;
            var destination = origin + (movingRight ? -1 : 1) * (38 + index * 3);
            var particleColor = particleColors[index % particleColors.Length];
            var particle = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = Math.Max(4, size * 0.72),
                Fill = new SolidColorBrush(particleColor),
                Opacity = 0,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 11,
                    ShadowDepth = 0,
                    Opacity = 1,
                    Color = particleColor
                }
            };
            Canvas.SetLeft(particle, origin);
            Canvas.SetTop(particle, centerY + verticalOffsets[index] - particle.Height / 2);
            ToolbarRocketCanvas.Children.Add(particle);
            var particleDuration = TimeSpan.FromMilliseconds(
                Math.Min(310, duration.TotalMilliseconds - beginMilliseconds));
            particle.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(origin, destination, particleDuration)
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMilliseconds),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            });
            particle.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(beginMilliseconds),
                Duration = particleDuration,
                FillBehavior = FillBehavior.Stop,
                KeyFrames =
                {
                    new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)),
                    new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.08)),
                    new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.55)),
                    new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1))
                }
            });
        }
    }

    private void StopToolbarFlight()
    {
        _toolbarFlightVersion++;
        ToolbarTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        ToolbarTranslation.X = 0;
        ToolbarRocketCanvas.Children.Clear();
        ToolbarBorder.IsHitTestVisible = true;
    }

    private void PreviewAppearance(bool animateToolbar)
    {
        if (animateToolbar)
        {
            AnimateToolbarAlignment();
        }
        else
        {
            ApplyToolbarAlignment();
        }
        var lightTheme = IsLightTheme();
        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.SetVisualOptions(_state.PanelOpacity, _state.IconSize, lightTheme);
        }

        ApplyThemeSurface(lightTheme);
    }

    private void ApplyThemeSurface(bool lightTheme)
    {
        var surfaceAlpha = (byte)(255 * _state.PanelOpacity);
        var surface = new SolidColorBrush(lightTheme
            ? Color.FromArgb(surfaceAlpha, 250, 252, 255)
            : Color.FromArgb(surfaceAlpha, 24, 30, 41));
        var foreground = new SolidColorBrush(lightTheme
            ? Color.FromRgb(30, 40, 54)
            : Color.FromRgb(247, 249, 252));
        var divider = new SolidColorBrush(lightTheme
            ? Color.FromArgb(32, 30, 40, 54)
            : Color.FromArgb(42, 255, 255, 255));
        var stroke = new SolidColorBrush(lightTheme
            ? Color.FromArgb(42, 30, 40, 54)
            : Color.FromArgb(54, 255, 255, 255));

        ToolbarBorder.Background = surface;
        ToolbarBorder.BorderBrush = stroke;
        ToolbarDivider.Background = divider;
        StatusBorder.Background = surface;
        StatusBorder.BorderBrush = stroke;
        StatusText.Foreground = foreground;

        foreach (var button in new[] { NewZoneButton, LockButton, DesktopModeButton, SettingsButton })
        {
            button.Foreground = foreground;
        }

        System.Windows.Application.Current.Resources["ToolbarHoverBrush"] = new SolidColorBrush(lightTheme
            ? Color.FromArgb(20, 30, 40, 54)
            : Color.FromArgb(34, 255, 255, 255));
        System.Windows.Application.Current.Resources["MenuSurfaceBrush"] = new SolidColorBrush(lightTheme
            ? Color.FromRgb(250, 252, 255)
            : Color.FromRgb(24, 33, 45));
        System.Windows.Application.Current.Resources["MenuHoverBrush"] = new SolidColorBrush(lightTheme
            ? Color.FromRgb(232, 238, 245)
            : Color.FromRgb(42, 57, 73));
        System.Windows.Application.Current.Resources["MenuTextBrush"] = new SolidColorBrush(lightTheme
            ? Color.FromRgb(30, 40, 54)
            : Color.FromRgb(247, 249, 252));
        System.Windows.Application.Current.Resources["MenuMutedBrush"] = new SolidColorBrush(lightTheme
            ? Color.FromRgb(100, 112, 128)
            : Color.FromRgb(170, 181, 197));
        System.Windows.Application.Current.Resources["MenuStrokeBrush"] = new SolidColorBrush(lightTheme
            ? Color.FromArgb(40, 30, 40, 54)
            : Color.FromArgb(63, 255, 255, 255));
    }

    private bool IsLightTheme()
    {
        if (_state.Theme == "Light")
        {
            return true;
        }

        if (_state.Theme == "Dark")
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RequestSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static async Task TrimWorkingSetAfterStartupAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        GC.Collect(2, GCCollectionMode.Optimized, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
    }

    private async Task SaveStateAsync()
    {
        try
        {
            await _stateStore.SaveAsync(_state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowStatus("布局暂时无法保存，请检查本地磁盘权限");
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusBorder.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ShowDeskNest()
    {
        if (!IsVisible)
        {
            Show();
        }

        _desktopHost.Reattach(this);
        _desktopHost.ResizeToVirtualScreen();
        ShowStatus("栖格正在桌面运行");
    }

    private void ExitApplication()
    {
        if (_exitRequested)
        {
            return;
        }

        _exitRequested = true;
        _saveTimer.Stop();
        try
        {
            _stateStore.SaveAsync(_state).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Exit must remain safe even when local storage is unavailable.
        }

        _iconVisibility.RestoreInitialState();
        _desktopWatcher?.Dispose();
        _trayService?.Dispose();
        _windowSource?.RemoveHook(OnWindowMessage);
        _windowSource = null;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exitRequested)
        {
            e.Cancel = true;
            ShowStatus("栖格常驻系统托盘；如需退出，请使用托盘菜单");
        }
    }
}
