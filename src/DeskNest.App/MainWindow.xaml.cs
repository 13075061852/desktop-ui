using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
    private sealed record DesktopMappingAddition(ZoneModel Zone, DesktopItem Item);

    private const int DeleteHotKeyId = 0x444E;

    private readonly JsonStateStore _stateStore = new();
    private readonly RuleClassifier _classifier = new();
    private readonly ShellIconService _iconService = new();
    private int _toolbarFlightVersion;
    private bool? _appliedLightTheme;
    private readonly ShellService _shellService = new();
    private readonly DesktopHostService _desktopHost = new();
    private readonly DesktopIconVisibilityService _iconVisibility = new();
    private readonly DesktopWallpaperService _wallpaperService = new();
    private readonly StartupService _startupService = new();
    private readonly ShellNewService _shellNewService = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _menuDismissTimer;
    private readonly DispatcherTimer _windowGuardTimer;

    private AppState _state = AppState.CreateDefault();
    private DesktopWatcher? _desktopWatcher;
    private TrayService? _trayService;
    private HwndSource? _windowSource;
    private nint _mainWindowHandle;
    private bool _exitRequested;
    private bool _loaded;
    private bool _transientMenuPointerArmed;
    private bool _selectionPointerWasDown;
    private bool _deleteHotKeyRegistered;
    private Guid? _selectedZoneId;
    private Guid? _selectedItemId;
    private nint _selectionForegroundWindow;

    public MainWindow()
    {
        InitializeComponent();
        DragGhostLayer.Attach(DragGhostCanvas);
        // Some full-window helper layers can become the native drag hit target even when
        // the pointer is visually over a zone. Keep a handled-events-too preview handler
        // as a fallback and route the drag by the actual screen position.
        RootGrid.AddHandler(
            DragDrop.PreviewDragOverEvent,
            new DragEventHandler(OnRootPreviewDragOver),
            handledEventsToo: true);

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
        _menuDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _menuDismissTimer.Tick += (_, _) =>
        {
            EnsureCustomDesktopWindowVisible();
            DismissTransientMenusOnOutsidePointerDown();
            DismissSelectionOnContextChange();
            UpdateInteractionWatch();
        };
        // 45ms 交互轮询只在有瞬态菜单或选中项时启用（见 UpdateInteractionWatch）；
        // 空闲时常驻的只有低频窗口守卫，避免无谓唤醒。
        _windowGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _windowGuardTimer.Tick += (_, _) => EnsureCustomDesktopWindowVisible();
        _windowGuardTimer.Start();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        IsVisibleChanged += (_, args) => DebugUiLog($"window IsVisible -> {args.NewValue}");
        StateChanged += (_, _) => DebugUiLog($"window state -> {WindowState}");
        Deactivated += (_, _) => CloseTransientMenus();
        PreviewMouseLeftButtonDown += OnWindowPreviewMouseLeftButtonDown;
        Closing += OnClosing;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        var handle = new WindowInteropHelper(this).Handle;
        _mainWindowHandle = handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(OnWindowMessage);
        _desktopHost.TryEmbed(this);
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmHotKey && wParam.ToInt32() == DeleteHotKeyId)
        {
            handled = true;
            Dispatcher.BeginInvoke(DeleteSelectedItem);
            return 0;
        }

        if (message == NativeMethods.WmActivateApp && wParam == 0)
        {
            Dispatcher.BeginInvoke(CloseTransientMenus);
            return 0;
        }

        if ((message == NativeMethods.WmShowWindow && wParam == 0 ||
             message == NativeMethods.WmSize && wParam.ToInt32() == NativeMethods.SizeMinimized) &&
            _state.DesktopIconsHidden &&
            !_exitRequested)
        {
            Dispatcher.BeginInvoke(
                DispatcherPriority.ApplicationIdle,
                new Action(EnsureCustomDesktopWindowVisible));
        }

        if (message != NativeMethods.WmNcHitTest)
        {
            return 0;
        }

        var packedPoint = lParam.ToInt64();
        var screenHitPoint = new Point(
            unchecked((short)(packedPoint & 0xFFFF)),
            unchecked((short)((packedPoint >> 16) & 0xFFFF)));
        var clientHitPoint = PointFromScreen(screenHitPoint);

        handled = true;
        return IsInteractivePoint(clientHitPoint)
            ? NativeMethods.HtClient
            : NativeMethods.HtTransparent;
    }

    private void OnRootPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(ZoneCard.InternalItemFormat) is not ItemDragPayload payload)
        {
            return;
        }

        // If the routed source is already inside a card, that card's normal handler owns the
        // event. Otherwise the source is likely a splitter/helper layer or the canvas itself.
        if (FindZoneCardAncestor(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        var point = e.GetPosition(this);
        var target = DesktopCanvas.Children
            .OfType<ZoneCard>()
            .LastOrDefault(card => ContainsPoint(card, point));
        if (target is null)
        {
            ClearAllDragPreviews();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        try
        {
            if (target.TryHandleInternalDragOver(PointToScreen(point), payload))
            {
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            }
        }
        catch (InvalidOperationException)
        {
            // The target can be rebuilt while a drag event is in flight.
            ClearAllDragPreviews();
        }
    }

    private static ZoneCard? FindZoneCardAncestor(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ZoneCard card)
            {
                return card;
            }
        }

        return null;
    }

    private void ClearDragPreviewsExcept(ZoneCard activeCard)
    {
        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            if (card != activeCard)
            {
                card.ClearDragPreview();
            }
        }
    }

    private void ClearAllDragPreviews()
    {
        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.ClearDragPreview();
        }
    }

    /// <summary>瞬态菜单或选中项存在时才启用 45ms 交互轮询；空闲时完全停表。</summary>
    private void UpdateInteractionWatch()
    {
        var shouldWatch = _selectedItemId is not null ||
                          DesktopCanvas.Children.OfType<ZoneCard>().Any(card => card.HasOpenTransientMenu);
        if (shouldWatch)
        {
            if (!_menuDismissTimer.IsEnabled)
            {
                _menuDismissTimer.Start();
                DebugUiLog("interaction watch on");
            }
        }
        else if (_menuDismissTimer.IsEnabled)
        {
            _menuDismissTimer.Stop();
            DebugUiLog("interaction watch off");
        }
    }

    private void CloseTransientMenus()
    {
        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.CloseTransientMenus();
        }

        UpdateInteractionWatch();
    }

    private void SelectItem(ZoneCard selectedCard, ZoneModel zone, DesktopItem item)
    {
        _selectedZoneId = zone.Id;
        _selectedItemId = item.Id;
        _selectionForegroundWindow = NativeMethods.GetForegroundWindow();
        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.SetSelectedItem(card == selectedCard ? item.Id : null);
        }

        UpdateInteractionWatch();

        if (!_deleteHotKeyRegistered && _mainWindowHandle != 0)
        {
            _deleteHotKeyRegistered = NativeMethods.RegisterHotKey(
                _mainWindowHandle,
                DeleteHotKeyId,
                modifiers: 0,
                NativeMethods.VkDelete);
        }
    }

    private void ClearItemSelection()
    {
        _selectedZoneId = null;
        _selectedItemId = null;
        _selectionForegroundWindow = 0;
        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.SetSelectedItem(null);
        }

        UpdateInteractionWatch();

        if (_deleteHotKeyRegistered)
        {
            NativeMethods.UnregisterHotKey(_mainWindowHandle, DeleteHotKeyId);
            _deleteHotKeyRegistered = false;
        }
    }

    private void DeleteSelectedItem()
    {
        if (_selectedZoneId is not Guid zoneId || _selectedItemId is not Guid itemId)
        {
            return;
        }

        var zone = _state.Zones.FirstOrDefault(candidate => candidate.Id == zoneId);
        var item = zone?.Items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (zone is null || item is null)
        {
            ClearItemSelection();
            return;
        }

        if (DesktopItem.IsShellLocation(item.Path))
        {
            ShowStatus("系统项目不能使用 Delete 键删除");
            return;
        }

        DeleteItem(zone, item);
    }

    private void OnWindowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (var current = e.OriginalSource as DependencyObject;
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { Tag: DesktopItem })
            {
                return;
            }
        }

        ClearItemSelection();
    }

    private void DismissSelectionOnContextChange()
    {
        if (_selectedItemId is null)
        {
            _selectionPointerWasDown = IsPointerButtonDown();
            return;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        if (_selectionForegroundWindow != 0 &&
            foreground != 0 &&
            foreground != _selectionForegroundWindow &&
            foreground != _mainWindowHandle)
        {
            ClearItemSelection();
            return;
        }

        var pointerDown = IsPointerButtonDown();
        if (!pointerDown)
        {
            _selectionPointerWasDown = false;
            return;
        }

        if (_selectionPointerWasDown)
        {
            return;
        }

        _selectionPointerWasDown = true;
        if (!NativeMethods.GetCursorPos(out var point))
        {
            ClearItemSelection();
            return;
        }

        var clickedWindow = NativeMethods.WindowFromPoint(point);
        NativeMethods.GetWindowThreadProcessId(clickedWindow, out var processId);
        if (processId != (uint)Environment.ProcessId)
        {
            ClearItemSelection();
        }
    }

    private void DismissTransientMenusOnOutsidePointerDown()
    {
        var hasOpenMenu = DesktopCanvas.Children
            .OfType<ZoneCard>()
            .Any(card => card.HasOpenTransientMenu);
        if (!hasOpenMenu)
        {
            _transientMenuPointerArmed = false;
            return;
        }

        var pointerDown = IsPointerButtonDown();
        if (!pointerDown)
        {
            // Ignore the click that originally opened the menu. A later press is an outside-click candidate.
            _transientMenuPointerArmed = true;
            return;
        }

        if (!_transientMenuPointerArmed)
        {
            return;
        }

        _transientMenuPointerArmed = false;
        if (!NativeMethods.GetCursorPos(out var point))
        {
            CloseTransientMenus();
            return;
        }

        var clickedWindow = NativeMethods.WindowFromPoint(point);
        if (clickedWindow == 0 || clickedWindow == _mainWindowHandle)
        {
            CloseTransientMenus();
            return;
        }

        NativeMethods.GetWindowThreadProcessId(clickedWindow, out var processId);
        if (processId != (uint)Environment.ProcessId)
        {
            CloseTransientMenus();
        }
    }

    private static bool IsPointerButtonDown() =>
        IsKeyDown(NativeMethods.VkLeftButton) ||
        IsKeyDown(NativeMethods.VkRightButton) ||
        IsKeyDown(NativeMethods.VkMiddleButton);

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private bool IsInteractivePoint(Point point)
    {
        if (_state.DesktopIconsHidden)
        {
            return true;
        }

        if (ContainsPoint(ToolbarBorder, point))
        {
            return true;
        }

        return DesktopCanvas.Children
            .OfType<ZoneCard>()
            .Any(card => ContainsPoint(card, point));
    }

    private void CreateShellNewFile(ShellNewDefinition definition, ZoneModel? requestedZone = null)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var baseName = MakeSafeFileName("新建 " + definition.DisplayName);
        var path = GetUniquePath(desktop, baseName, definition.OutputExtension);
        if (!_shellNewService.CreateFile(definition, path))
        {
            ShowStatus($"无法创建文件“{Path.GetFileName(path)}”，请检查桌面目录权限");
            return;
        }

        var targetZone = requestedZone
                         ?? _state.Zones.FirstOrDefault(zone =>
                             zone.CategoryKey == _classifier.Classify(path))
                         ?? _state.Zones.FirstOrDefault(zone => zone.CategoryKey == "other")
                         ?? _state.Zones.FirstOrDefault();
        if (targetZone is null)
        {
            ShowStatus($"文件已创建：{Path.GetFileName(path)}");
            return;
        }

        var item = DesktopItem.FromPath(path, targetZone.CategoryKey);
        targetZone.Items.Add(item);
        NotifyShellDirectoriesChanged(desktop);
        _iconService.Invalidate();
        var addition = new DesktopMappingAddition(targetZone, item);
        RenderZones();
        HighlightMappingZones([addition]);
        RequestSave();
        ShowStatus(FormatMappingStatus([addition]));
    }

    private static string MakeSafeFileName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalidCharacters.Contains(character) ? '_' : character);
        }

        return builder.ToString().Trim();
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
        if (_shellNewService.EnsureSystemShellNewOverrides())
        {
            NativeMethods.SHChangeNotify(
                NativeMethods.ShcneAssocChanged,
                NativeMethods.ShcnfIdList,
                null,
                0);
        }

        var startupConfigured = _startupService.Apply(_state.LaunchAtStartup);
        var staleMappingsRemoved = RemoveMissingMappings();
        var shellItemsChanged = EnsureSystemShellItems();
        var desktopMappingsAdded = AddUnmappedDesktopItems();
        if (!string.IsNullOrWhiteSpace(_state.WallpaperSelection))
        {
            await _wallpaperService.ApplySelectionModernAsync(_state.WallpaperSelection);
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
        if (layoutAdjusted || shellItemsChanged || desktopMappingsAdded.Count > 0 || staleMappingsRemoved > 0)
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

        if (desktopMappingsAdded.Count > 0 && zonesRendered)
        {
            HighlightMappingZones(desktopMappingsAdded);
            ShowStatus(FormatMappingStatus(desktopMappingsAdded));
        }
        else if (!startupConfigured)
        {
            ShowStatus("开机自启动设置暂时无法更新");
        }
    }

    private void RenderZones()
    {
        UpdateAlignmentGuides(null, null);
        DesktopCanvas.Children.Clear();
        _zoneCardMap.Clear();
        var lightTheme = IsLightTheme();

        foreach (var zone in _state.Zones)
        {
            // A saved layout can drift outside the current canvas (display
            // change, work-area change). Write the clamped position back to
            // the model: rendering at one position while GetVisualBounds
            // reports another desyncs splitter rails, guides and the next drag.
            if (zone.X < ZoneCard.HorizontalDesktopInset)
            {
                zone.X = ZoneCard.HorizontalDesktopInset;
            }

            if (zone.Y < 72)
            {
                zone.Y = 72;
            }

            var card = new ZoneCard(zone, _iconService)
            {
                BoundsConstraint = ConstrainZoneBounds,
                ShellNewDefinitionsProvider = _shellNewService.GetDefinitions
            };
            card.SetLayoutLocked(_state.LayoutLocked);
            card.SetVisualOptions(_state.PanelOpacity, _state.IconSize, lightTheme);
            card.AlignmentGuidesChanged = UpdateAlignmentGuides;
            card.DragPreviewActivated = ClearDragPreviewsExcept;
            card.DragPreviewEnded = ClearAllDragPreviews;
            card.ModelChanged += (_, _) =>
            {
                UpdateZoneSplitters();
                RequestSave();
            };
            card.ZoneGestureCompleted += (_, _) => CommitZoneRest(zone);
            card.CollapseStateChanged += (_, _) => OnZoneCollapseToggled(zone, card);
            card.DeleteRequested += (_, _) => DeleteZone(zone);
            card.NewFileRequested += (_, _) => CreateFileInZone(zone);
            card.NewFolderRequested += (_, _) => CreateFolderInZone(zone);
            card.NewShortcutRequested += (_, _) => CreateShortcutInZone(zone);
            card.ShellNewRequested += (_, args) => CreateShellNewFile(args.Definition, zone);
            card.FilesDropped += (_, args) => AddPathsToZone(zone, args.Paths);
            card.FolderFilesDropped += (_, args) => MoveFilesIntoFolder(args.Folder, args.Paths);
            card.ItemMoveRequested += (_, args) => MoveItem(zone, args.Payload, args.TargetIndex);
            card.ItemDragCompleted += (_, args) => HandleItemDragCompleted(args);
            card.ItemSelected += (_, args) => SelectItem(card, zone, args.Item);
            card.SelectionClearRequested += (_, _) => ClearItemSelection();
            card.TransientMenuStateChanged += (_, _) => UpdateInteractionWatch();
            card.ItemOpenRequested += (_, args) => OpenItem(args.Item);
            card.ItemRenameRequested += (_, args) => RenameItem(zone, args.Item);
            card.ItemRevealRequested += (_, args) => RevealItem(args.Item);
            card.ItemPropertiesRequested += (_, args) => ShowItemProperties(args.Item);
            card.ItemEmptyRecycleBinRequested += (_, args) => EmptyRecycleBin(args.Item);
            card.ItemDeleteRequested += (_, args) => DeleteItem(zone, args.Item);
            card.ItemRemoveRequested += (_, args) => RemoveItem(zone, args.Item);
            if (_selectedZoneId == zone.Id)
            {
                card.SetSelectedItem(_selectedItemId);
            }

            Canvas.SetLeft(card, zone.X);
            Canvas.SetTop(card, zone.Y);
            DesktopCanvas.Children.Add(card);
            _zoneCardMap[zone.Id] = card;
        }

        DesktopCanvas.Visibility = _state.DesktopIconsHidden
            ? Visibility.Visible
            : Visibility.Collapsed;
        ZoneSplitterCanvas.Visibility = DesktopCanvas.Visibility;
        if (ZoneSplitterCanvas.Visibility != Visibility.Visible)
        {
            ClearZoneSplitters();
        }
        else
        {
            UpdateZoneSplitters();
        }
        ApplyThemeSurface(lightTheme);
    }

    private ZoneAlignmentResult ConstrainZoneBounds(
        ZoneModel zone,
        ZoneBounds current,
        ZoneBounds desired,
        ZoneSnapHysteresis hysteresis)
    {
        var otherZones = _state.Zones
            .Where(other => other.Id != zone.Id)
            .ToArray();
        var obstacles = otherZones
            .Select(GetVisualBounds)
            .ToArray();
        IReadOnlyList<ZoneBounds> obstacleList = obstacles;

        var minimumX = ZoneCard.HorizontalDesktopInset;
        const double minimumY = 72;
        var maximumRight = MaximumZoneRight;
        var maximumBottom = Math.Max(minimumY + 1, MaximumZoneBottom);

        // A freshly expanded zone may legitimately cover a neighbour (expand
        // covers instead of displacing). While the current bounds overlap, a
        // same-size move must follow the pointer instead of jamming: every
        // candidate would otherwise fail the gap check and freeze the zone.
        var currentAvailable = ZoneCollisionResolver.IsAvailable(
            current, obstacleList, 12, minimumX, minimumY, maximumRight, maximumBottom);
        var isSameSizeMove = Math.Abs(current.Width - desired.Width) < 0.01 &&
                             Math.Abs(current.Height - desired.Height) < 0.01;
        if (!currentAvailable && isSameSizeMove)
        {
            return ZoneAlignmentResolver.Snap(
                current,
                desired,
                desired,
                obstacleList,
                snapDistance: 10,
                gap: 12,
                minimumX,
                minimumY,
                maximumRight,
                maximumBottom,
                hysteresis,
                stickyEscapeDistance: 8);
        }

        var stackResize = ZoneStackResizeResolver.ReflowAdjacent(
            current,
            desired,
            obstacles,
            gap: 12,
            minimumWidth: 150,
            minimumHeight: 150,
            minimumX,
            minimumY,
            maximumRight,
            maximumBottom);
        desired = stackResize.Desired;
        obstacleList = ZoneLayoutRelaxer.Relax(
            stackResize.Obstacles,
            otherZones.Select(GetRestBounds).ToArray(),
            stackResize.Desired,
            12,
            minimumX,
            minimumY,
            maximumRight,
            maximumBottom);
        ApplyCompressedObstacleBounds(otherZones, obstacleList);

        var collisionSafe = ZoneCollisionResolver.Constrain(
            current,
            desired,
            obstacleList,
            12,
            minimumX,
            minimumY,
            maximumRight,
            maximumBottom);
        return ZoneAlignmentResolver.Snap(
            current,
            desired,
            collisionSafe,
            obstacleList,
            snapDistance: 10,
            gap: 12,
            minimumX,
            minimumY,
            maximumRight,
            maximumBottom,
            hysteresis,
            // Standard gaps between flush neighbours put guide pairs only
            // 12px apart, so a hold region that wide lets guides chain-lock
            // the edge and block alignment with a neighbour on the other
            // side. 8px keeps the anti-flicker hold without trapping the
            // edge: the pointer escapes after a few pixels of travel.
            stickyEscapeDistance: 8);
    }

    /// <summary>Expand never displaces or covers the zones below: the expanded
    /// height is limited to the free space above the nearest blocker (and the
    /// work-area bottom). The user's intended height stays in the rest bounds
    /// so the zone can grow back once the blocker moves away.</summary>
    private void OnZoneCollapseToggled(ZoneModel zone, ZoneCard card)
    {
        Canvas.SetZIndex(card, zone.IsCollapsed ? 0 : 1);
        if (zone.IsCollapsed)
        {
            return;
        }

        var limitBottom = MaximumZoneBottom;
        foreach (var other in _state.Zones)
        {
            if (other.Id == zone.Id)
            {
                continue;
            }

            var otherBounds = GetVisualBounds(other);
            var standsBelow = otherBounds.Y >= zone.Y + 52 - 0.01;
            var overlapsHorizontally = zone.X < otherBounds.Right - 0.01 &&
                                       zone.X + zone.Width > otherBounds.X + 0.01;
            if (standsBelow && overlapsHorizontally)
            {
                limitBottom = Math.Min(limitBottom, otherBounds.Y - 12);
            }
        }

        var available = limitBottom - zone.Y;
        if (zone.Height > available + 0.01)
        {
            zone.Height = Math.Max(52, available);
        }
    }

    private static ZoneBounds GetRestBounds(ZoneModel zone)
    {
        if (!zone.HasRestBounds)
        {
            zone.CaptureRestBounds();
        }

        return new ZoneBounds(zone.RestX, zone.RestY, zone.RestWidth, zone.RestHeight);
    }

    /// <summary>A direct user gesture (header move, resize) finished: the final
    /// bounds become the zone's home, the arrangement the layout relaxes
    /// toward when space frees up later.</summary>
    private void CommitZoneRest(ZoneModel zone)
    {
        zone.CaptureRestBounds();
        RelaxNeighboursTowardRest();
        RequestSave();
    }

    /// <summary>Walks every non-interacting zone back toward its rest bounds
    /// where possible; used after a gesture commits, after a zone deletion and
    /// whenever the layout may have freed space.</summary>
    private void RelaxNeighboursTowardRest()
    {
        var minimumX = ZoneCard.HorizontalDesktopInset;
        const double minimumY = 72;
        var maximumRight = MaximumZoneRight;
        var maximumBottom = Math.Max(minimumY + 1, MaximumZoneBottom);
        if (_state.Zones.Count == 0)
        {
            return;
        }

        // No anchored zone: every zone may relax. The virtual anchor sits far
        // outside the canvas so it never blocks a candidate.
        var anchor = new ZoneBounds(minimumX - 10000, minimumY - 10000, 0, 0);
        var relaxed = ZoneLayoutRelaxer.Relax(
            _state.Zones.Select(GetVisualBounds).ToArray(),
            _state.Zones.Select(GetRestBounds).ToArray(),
            anchor,
            12,
            minimumX,
            minimumY,
            maximumRight,
            maximumBottom);
        ApplyCompressedObstacleBounds(_state.Zones, relaxed);
    }

    private void ApplyCompressedObstacleBounds(IReadOnlyList<ZoneModel> zones, IReadOnlyList<ZoneBounds> bounds)
    {
        for (var index = 0; index < zones.Count; index++)
        {
            var zone = zones[index];
            var adjusted = bounds[index];
            var current = GetVisualBounds(zone);
            if (Math.Abs(current.X - adjusted.X) < 0.01 &&
                Math.Abs(current.Y - adjusted.Y) < 0.01 &&
                Math.Abs(current.Width - adjusted.Width) < 0.01 &&
                Math.Abs(current.Height - adjusted.Height) < 0.01)
            {
                continue;
            }

            zone.X = adjusted.X;
            zone.Y = adjusted.Y;
            zone.Width = adjusted.Width;
            if (!zone.IsCollapsed)
            {
                zone.Height = adjusted.Height;
            }

            var card = DesktopCanvas.Children
                .OfType<ZoneCard>()
                .FirstOrDefault(value => value.Model.Id == zone.Id);
            if (card is null)
            {
                continue;
            }

            Canvas.SetLeft(card, zone.X);
            Canvas.SetTop(card, zone.Y);
            card.Width = zone.Width;
            card.Height = zone.IsCollapsed ? 52 : zone.Height;
            if (zone.ViewMode == "List" && Math.Abs(current.Width - adjusted.Width) >= 0.01)
            {
                card.RefreshItems();
            }
        }

        UpdateZoneSplitters();
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
        var maximumBottom = Math.Max(73, MaximumZoneBottom);
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
                    // Keep the home position aligned with the rigid shift so
                    // relaxation does not pull zones back off-screen.
                    if (zone.HasRestBounds)
                    {
                        zone.RestX += horizontalShift;
                    }
                }

                adjusted = true;
            }

            var topmost = _state.Zones.Min(zone => zone.Y);
            var bottommost = _state.Zones.Max(zone => zone.Y + (zone.IsCollapsed ? 52 : zone.Height));
            var verticalShift = topmost < 72
                ? 72 - topmost
                : bottommost > maximumBottom
                    ? maximumBottom - bottommost
                    : 0;
            if (Math.Abs(verticalShift) > 0.01 &&
                topmost + verticalShift >= 72 && bottommost + verticalShift <= maximumBottom)
            {
                foreach (var zone in _state.Zones)
                {
                    zone.Y += verticalShift;
                    if (zone.HasRestBounds)
                    {
                        zone.RestY += verticalShift;
                    }
                }

                adjusted = true;
            }
        }

        var placed = new List<ZoneBounds>();

        foreach (var zone in _state.Zones)
        {
            var bounds = GetVisualBounds(zone);
            // Prefer clamping an out-of-bounds zone back inside the desktop
            // over re-placing it: a tiny overflow (a few pixels saved before a
            // constraint change) should nudge the zone, not shuffle the board.
            var clamped = false;
            if (bounds.Right > maximumRight)
            {
                zone.X = Math.Max(minimumX, maximumRight - bounds.Width);
                bounds = GetVisualBounds(zone);
                clamped = true;
            }
            if (bounds.Bottom > maximumBottom)
            {
                zone.Y = Math.Max(72, maximumBottom - bounds.Height);
                bounds = GetVisualBounds(zone);
                clamped = true;
            }

            if (clamped)
            {
                // Accept the clamped zone even if the gap to a neighbour
                // shrinks a pixel below the standard spacing; the alternative
                // re-placement scan would shuffle the whole board over a
                // trivial overflow.
                adjusted = true;
                placed.Add(bounds);
                continue;
            }

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

        // A rest position that drifted out of the canvas (or clashes with the
        // normalized layout) would pull zones around right after startup;
        // re-anchor invalid home positions to the normalized arrangement.
        foreach (var zone in _state.Zones)
        {
            if (!zone.HasRestBounds)
            {
                zone.CaptureRestBounds();
                continue;
            }

            var rest = new ZoneBounds(zone.RestX, zone.RestY, zone.RestWidth, zone.RestHeight);
            var restInside = rest.X >= minimumX - 0.01 && rest.Y >= 72 - 0.01 &&
                             rest.Right <= maximumRight + 0.01 && rest.Bottom <= maximumBottom + 0.01;
            if (!restInside)
            {
                zone.CaptureRestBounds();
            }
        }

        return adjusted;
    }

    private static ZoneBounds GetVisualBounds(ZoneModel zone)
    {
        return new ZoneBounds(zone.X, zone.Y, zone.Width, zone.IsCollapsed ? 52 : zone.Height);
    }

    private bool EnsureSystemShellItems()
    {
        var fallbackTarget = _state.Zones.FirstOrDefault(zone =>
                                zone.Name.Contains("系统工具", StringComparison.OrdinalIgnoreCase))
                            ?? _state.Zones.FirstOrDefault(zone => zone.CategoryKey == "other")
                            ?? _state.Zones.FirstOrDefault();
        if (fallbackTarget is null)
        {
            return false;
        }

        var changed = false;
        var shellScanner = new DesktopShellScanner();
        var shellItems = shellScanner.Scan();
        if (shellScanner.LastScanSucceeded)
        {
            var staleShellItems = _state.Zones
                .SelectMany(zone => zone.Items.Select(item => (Zone: zone, Item: item)))
                .Where(entry => DesktopItem.IsShellLocation(entry.Item.Path))
                .Where(entry => !shellItems.Any(shellItem => HasEquivalentShellItem(entry.Item, shellItem)))
                .ToArray();
            foreach (var staleShellItem in staleShellItems)
            {
                staleShellItem.Zone.Items.Remove(staleShellItem.Item);
                changed = true;
            }
        }
        else if (shellItems.Count == 0)
        {
            // Keep the recycle bin available if a shell enumeration is temporarily unavailable.
            shellItems =
            [
                new ShellDesktopItem(DesktopItem.RecycleBinShellPath, "回收站", "other")
            ];
        }

        foreach (var shellItem in shellItems)
        {
            if (HasEquivalentShellItem(shellItem))
            {
                continue;
            }

            var target = _state.Zones.FirstOrDefault(zone => zone.CategoryKey == shellItem.CategoryKey)
                         ?? fallbackTarget;
            target.Items.Add(DesktopItem.FromShellLocation(
                shellItem.Path,
                shellItem.DisplayName,
                target.CategoryKey));
            changed = true;
        }

        return changed;
    }

    private bool HasEquivalentShellItem(ShellDesktopItem shellItem) => _state.Zones
        .SelectMany(zone => zone.Items)
        .Any(item => HasEquivalentShellItem(item, shellItem));

    private static bool HasEquivalentShellItem(DesktopItem existingItem, ShellDesktopItem candidate)
    {
        return string.Equals(existingItem.Path, candidate.Path, StringComparison.OrdinalIgnoreCase) ||
               IsSameKnownShellItem(existingItem.Path, candidate.Path);
    }

    private static bool IsSameKnownShellItem(string existingPath, string candidatePath)
    {
        var existingCanonical = CanonicalKnownShellPath(existingPath);
        var candidateCanonical = CanonicalKnownShellPath(candidatePath);
        return existingCanonical is not null &&
               string.Equals(existingCanonical, candidateCanonical, StringComparison.OrdinalIgnoreCase);
    }

    private static string? CanonicalKnownShellPath(string path)
    {
        if (path.Contains("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DesktopItem.ThisPcShellPath, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopItem.ThisPcShellPath;
        }

        if (path.Contains("{645FF040-5081-101B-9F08-00AA002F954E}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DesktopItem.RecycleBinShellPath, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopItem.RecycleBinShellPath;
        }

        if (path.Contains("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DesktopItem.NetworkShellPath, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopItem.NetworkShellPath;
        }

        if (path.Contains("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("{26EE0668-A00A-44D7-9371-BEB064C98683}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DesktopItem.ControlPanelShellPath, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopItem.ControlPanelShellPath;
        }

        if (path.Contains("{59031A47-3F72-44A7-89C5-5595FE6B30EE}", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, DesktopItem.UserFilesShellPath, StringComparison.OrdinalIgnoreCase))
        {
            return DesktopItem.UserFilesShellPath;
        }

        return null;
    }

    private IReadOnlyList<DesktopMappingAddition> AddUnmappedDesktopItems()
    {
        var scanner = new DesktopScanner(_classifier);
        var scannedItems = scanner.ScanDefaultDesktops();
        var mappedPaths = _state.Zones
            .SelectMany(zone => zone.Items)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var additions = new List<DesktopMappingAddition>();
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
            additions.Add(new DesktopMappingAddition(target, item));
        }

        return additions;
    }

    private int RemoveMissingMappings()
    {
        var removed = 0;
        foreach (var zone in _state.Zones)
        {
            removed += zone.Items.RemoveAll(item =>
                !DesktopItem.IsShellLocation(item.Path) &&
                !File.Exists(item.Path) &&
                !Directory.Exists(item.Path));
        }

        return removed;
    }

    private int RemoveMappingsForMovedPath(string sourcePath, bool isDirectory)
    {
        var removed = 0;
        foreach (var zone in _state.Zones)
        {
            removed += zone.Items.RemoveAll(item =>
                !DesktopItem.IsShellLocation(item.Path) &&
                (PathsEqual(item.Path, sourcePath) ||
                 isDirectory && IsPathInside(sourcePath, item.Path)));
        }

        return removed;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                NormalizePathForComparison(left),
                NormalizePathForComparison(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string NormalizePathForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void NotifyShellDirectoriesChanged(params string?[] directories)
    {
        foreach (var directory in directories
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            NativeMethods.SHChangeNotify(
                NativeMethods.ShcneUpdatedir,
                NativeMethods.ShcnfPathW,
                directory,
                0);
        }
    }

    private void OrganizeDesktop(bool showNotification)
    {
        var additions = AddUnmappedDesktopItems();
        RenderZones();
        if (additions.Count > 0)
        {
            HighlightMappingZones(additions);
        }

        RequestSave();
        ShowStatus(additions.Count == 0 ? "桌面映射已是最新状态" : FormatMappingStatus(additions));
        if (showNotification && additions.Count > 0)
        {
            _trayService?.ShowMessage("一键整理完成", $"新增 {additions.Count} 个映射，没有移动任何文件。");
        }
    }

    private void CreateFileInZone(ZoneModel zone)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var defaultPath = GetUniquePath(desktop, "新建文本文档", ".txt");
        var dialog = new RenameItemWindow(
            Path.GetFileName(defaultPath),
            preservesExtension: false,
            dialogTitle: "新建文件",
            actionTitle: "创建文件",
            hint: "输入文件名，可包含文件扩展名")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = Path.Combine(desktop, dialog.NewName);
        try
        {
            using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            zone.Items.Add(DesktopItem.FromPath(path, zone.CategoryKey));
            RefreshZone(zone);
            RequestSave();
            ShowStatus($"已在“{zone.Name}”中新建文件");
        }
        catch (IOException)
        {
            System.Windows.MessageBox.Show(
                this,
                "目标位置已经存在同名文件，请换一个名称。",
                "无法新建文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("无法新建文件，请检查桌面目录权限");
        }
    }

    private void CreateFolderInZone(ZoneModel zone)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var defaultPath = GetUniquePath(desktop, "新建文件夹", string.Empty);
        var dialog = new RenameItemWindow(
            Path.GetFileName(defaultPath),
            preservesExtension: false,
            dialogTitle: "新建文件夹",
            actionTitle: "创建文件夹",
            hint: "输入文件夹名称")
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var path = Path.Combine(desktop, dialog.NewName);
        if (File.Exists(path) || Directory.Exists(path))
        {
            System.Windows.MessageBox.Show(
                this,
                "目标位置已经存在同名项目，请换一个名称。",
                "无法新建文件夹",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            zone.Items.Add(DesktopItem.FromPath(path, zone.CategoryKey));
            RefreshZone(zone);
            RequestSave();
            ShowStatus($"已在“{zone.Name}”中新建文件夹");
        }
        catch (IOException)
        {
            System.Windows.MessageBox.Show(
                this,
                "目标位置已经存在同名项目，请换一个名称。",
                "无法新建文件夹",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("无法新建文件夹，请检查桌面目录权限");
        }
    }

    private void CreateShortcutInZone(ZoneModel zone)
    {
        var targetDialog = new RenameItemWindow(
            string.Empty,
            preservesExtension: false,
            dialogTitle: "新建快捷方式",
            actionTitle: "快捷方式地址",
            hint: "输入程序、文件或文件夹的完整地址",
            validator: value =>
            {
                var path = NormalizeShortcutTarget(value);
                return File.Exists(path) || Directory.Exists(path);
            },
            validationMessage: "请输入一个真实存在的程序、文件或文件夹地址。")
        {
            Owner = this
        };
        if (targetDialog.ShowDialog() != true)
        {
            return;
        }

        var targetPath = NormalizeShortcutTarget(targetDialog.NewName);
        var defaultName = Directory.Exists(targetPath)
            ? new DirectoryInfo(targetPath).Name
            : Path.GetFileNameWithoutExtension(targetPath);
        var nameDialog = new RenameItemWindow(
            defaultName,
            preservesExtension: true,
            dialogTitle: "新建快捷方式",
            actionTitle: "快捷方式名称",
            hint: "输入名称，.lnk 扩展名会自动添加")
        {
            Owner = this
        };
        if (nameDialog.ShowDialog() != true)
        {
            return;
        }

        var fileName = nameDialog.NewName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
            ? nameDialog.NewName
            : nameDialog.NewName + ".lnk";
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var shortcutPath = Path.Combine(desktop, fileName);
        if (File.Exists(shortcutPath))
        {
            System.Windows.MessageBox.Show(
                this,
                "桌面上已经存在同名快捷方式，请换一个名称。",
                "无法新建快捷方式",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!_shellService.CreateShortcut(shortcutPath, targetPath))
        {
            ShowStatus("快捷方式创建失败，请检查桌面目录权限");
            return;
        }

        zone.Items.Add(DesktopItem.FromPath(shortcutPath, zone.CategoryKey));
        _iconService.Invalidate();
        RefreshZone(zone);
        RequestSave();
        ShowStatus($"已在“{zone.Name}”中新建快捷方式");
    }

    private static string NormalizeShortcutTarget(string value)
    {
        var unquoted = value.Trim().Trim('"');
        return Environment.ExpandEnvironmentVariables(unquoted);
    }

    private static string GetUniquePath(string directory, string baseName, string extension)
    {
        var path = Path.Combine(directory, baseName + extension);
        for (var index = 2; File.Exists(path) || Directory.Exists(path); index++)
        {
            path = Path.Combine(directory, $"{baseName} ({index}){extension}");
        }

        return path;
    }

    private void AddPathsToZone(ZoneModel targetZone, IReadOnlyList<string> paths)
    {
        var desktop = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var moved = 0;
        var mapped = 0;
        var skipped = 0;
        var additions = new List<DesktopMappingAddition>();

        foreach (var sourcePath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                skipped++;
                continue;
            }

            string source;
            try
            {
                source = Path.GetFullPath(sourcePath);
            }
            catch (ArgumentException)
            {
                skipped++;
                continue;
            }

            if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), desktop,
                    StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var sourceParent = Path.GetDirectoryName(source)?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var destination = source;
            var isDirectory = Directory.Exists(source);
            var isAlreadyOnDesktop = string.Equals(sourceParent, desktop, StringComparison.OrdinalIgnoreCase);

            if (!isAlreadyOnDesktop)
            {
                var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                destination = isDirectory
                    ? GetUniquePath(desktop, name, string.Empty)
                    : GetUniquePath(desktop, Path.GetFileNameWithoutExtension(source), Path.GetExtension(source));
                try
                {
                    if (isDirectory)
                    {
                        Directory.Move(source, destination);
                    }
                    else
                    {
                        File.Move(source, destination);
                    }

                    NotifyShellDirectoriesChanged(sourceParent, Path.GetDirectoryName(destination));
                    moved++;
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }
            }

            RemoveMappingsForMovedPath(source, isDirectory);
            var mappedItem = DesktopItem.FromPath(destination, targetZone.CategoryKey);
            targetZone.Items.Add(mappedItem);
            additions.Add(new DesktopMappingAddition(targetZone, mappedItem));
            mapped++;
        }

        if (mapped > 0)
        {
            _iconService.Invalidate();
            RenderZones();
            HighlightMappingZones(additions);
            RequestSave();
        }

        var message = mapped == 0
            ? "没有可移动的文件"
            : FormatMappingStatus(additions) + (skipped == 0 ? string.Empty : $"，{skipped} 个项目跳过");
        ShowStatus(message);
    }

    private void MoveFilesIntoFolder(DesktopItem targetFolder, IReadOnlyList<string> paths)
    {
        var targetDirectory = Path.GetFullPath(targetFolder.Path);
        if (!Directory.Exists(targetDirectory))
        {
            ShowStatus("目标文件夹不可用");
            return;
        }

        var moved = 0;
        var skipped = 0;
        foreach (var sourcePath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
            {
                skipped++;
                continue;
            }

            string source;
            try
            {
                source = Path.GetFullPath(sourcePath);
            }
            catch (Exception) when (sourcePath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            if (string.Equals(source, targetDirectory, StringComparison.OrdinalIgnoreCase) ||
                Directory.Exists(source) &&
                (IsPathInside(targetDirectory, source) || IsPathInside(source, targetDirectory)))
            {
                skipped++;
                continue;
            }

            var sourceParent = Path.GetDirectoryName(source);
            if (string.Equals(sourceParent, targetDirectory, StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var sourceName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                skipped++;
                continue;
            }

            var isDirectory = Directory.Exists(source);
            var destination = !isDirectory
                ? GetUniquePath(targetDirectory, Path.GetFileNameWithoutExtension(source), Path.GetExtension(source))
                : GetUniquePath(targetDirectory, sourceName, string.Empty);

            try
            {
                if (Directory.Exists(source))
                {
                    Directory.Move(source, destination);
                }
                else
                {
                    File.Move(source, destination);
                }

                NotifyShellDirectoriesChanged(sourceParent, Path.GetDirectoryName(destination));
            }
            catch (IOException)
            {
                skipped++;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            RemoveMappingsForMovedPath(source, isDirectory);

            // The item now lives inside the mapped folder, so it should not remain as a
            // separate desktop mapping in any zone.
            moved++;
        }

        if (moved > 0)
        {
            _iconService.Invalidate();
            RenderZones();
            RequestSave();
        }

        ShowStatus(moved == 0
            ? "没有文件被移动到目标文件夹"
            : skipped == 0
                ? $"已移动 {moved} 个项目到“{targetFolder.DisplayName}”"
                : $"已移动 {moved} 个项目，{skipped} 个项目跳过");
    }

    private static bool IsPathInside(string parent, string candidate)
    {
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private void HandleItemDragCompleted(ItemDragCompletedEventArgs args)
    {
        // Explorer may report Copy/None for directory drops even when the source directory
        // was moved successfully. The source path is the reliable signal for cleanup.
        if (DesktopItem.IsShellLocation(args.Path) ||
            File.Exists(args.Path) ||
            Directory.Exists(args.Path))
        {
            return;
        }

        var removed = RemoveMappingsForMovedPath(args.Path, args.IsDirectory);
        if (removed == 0)
        {
            return;
        }

        ClearItemSelection();
        RenderZones();
        RequestSave();
        ShowStatus("已立即移除拖走的文件映射");
    }

    private void MoveItem(ZoneModel targetZone, ItemDragPayload payload, int targetIndex)
    {
        var sourceZone = _state.Zones.FirstOrDefault(zone => zone.Id == payload.SourceZoneId);
        var item = sourceZone?.Items.FirstOrDefault(value => value.Id == payload.ItemId);
        if (sourceZone is null || item is null)
        {
            return;
        }

        if (!ZoneItemOrderService.Move(sourceZone.Items, targetZone.Items, payload.ItemId, targetIndex))
        {
            return;
        }

        item.CategoryKey = targetZone.CategoryKey;
        if (sourceZone.Id == targetZone.Id)
        {
            RefreshZone(targetZone);
            ShowStatus("分区内顺序已调整");
        }
        else
        {
            RefreshZone(sourceZone);
            RefreshZone(targetZone);
            ShowStatus($"已移动到“{targetZone.Name}”");
        }

        RequestSave();
    }

    private void OpenItem(DesktopItem item)
    {
        if (!_shellService.Open(item.Path))
        {
            ShowStatus("文件已失效，可右键移出映射");
        }
    }

    private void RenameItem(ZoneModel zone, DesktopItem item)
    {
        var isDirectory = Directory.Exists(item.Path);
        var isFile = File.Exists(item.Path);
        if (!isDirectory && !isFile)
        {
            ShowStatus("无法重命名：文件可能已被移动或删除");
            return;
        }

        var currentName = isDirectory
            ? new DirectoryInfo(item.Path).Name
            : Path.GetFileNameWithoutExtension(item.Path);
        var dialog = new RenameItemWindow(currentName, preservesExtension: isFile) { Owner = this };
        if (dialog.ShowDialog() != true || string.Equals(dialog.NewName, currentName, StringComparison.Ordinal))
        {
            return;
        }

        var parent = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            ShowStatus("无法重命名此项目");
            return;
        }

        var extension = isFile ? Path.GetExtension(item.Path) : string.Empty;
        var destination = Path.Combine(parent, dialog.NewName + extension);
        try
        {
            if (isDirectory)
            {
                Directory.Move(item.Path, destination);
            }
            else
            {
                File.Move(item.Path, destination);
            }
        }
        catch (UnauthorizedAccessException)
        {
            var elevate = System.Windows.MessageBox.Show(
                this,
                "该项目位于公共桌面或受保护位置，重命名需要管理员权限。\n\n是否授权后继续？",
                "需要管理员权限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (elevate != MessageBoxResult.Yes || !_shellService.RenameWithElevation(item.Path, destination))
            {
                ShowStatus("重命名已取消或未获得管理员权限");
                return;
            }
        }
        catch (IOException)
        {
            var destinationExists = File.Exists(destination) || Directory.Exists(destination);
            System.Windows.MessageBox.Show(
                this,
                destinationExists
                    ? "目标位置已经存在同名文件或文件夹，请换一个名称。"
                    : "文件当前可能正在使用，暂时无法重命名。",
                "无法重命名",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        item.Path = Path.GetFullPath(destination);
        item.DisplayName = dialog.NewName;
        _iconService.Invalidate();
        RefreshZone(zone);
        RequestSave();
        ShowStatus($"已重命名为“{dialog.NewName}”");
    }

    private void RevealItem(DesktopItem item)
    {
        if (!_shellService.Reveal(item.Path))
        {
            ShowStatus("无法定位：文件可能已被移动或删除");
        }
    }

    private void ShowItemProperties(DesktopItem item)
    {
        if (!_shellService.ShowProperties(item.Path))
        {
            ShowStatus("无法打开属性：文件可能已被移动或删除");
        }
    }

    private void EmptyRecycleBin(DesktopItem item)
    {
        if (_shellService.IsRecycleBinEmpty() == true)
        {
            ShowStatus("回收站已经是干净的状态了");
            return;
        }

        var result = System.Windows.MessageBox.Show(
            this,
            "确定要清空回收站吗？其中的项目将被永久删除。",
            "清空回收站",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_shellService.EmptyRecycleBin())
        {
            ShowStatus("回收站清空失败");
            return;
        }

        _iconService.Invalidate();
        RefreshVisibleItems();
        ShowStatus("回收站已清空");
    }

    private void DeleteItem(ZoneModel zone, DesktopItem item)
    {
        if (!item.Exists)
        {
            zone.Items.Remove(item);
            RefreshZone(zone);
            RequestSave();
            ShowStatus("已清理失效映射");
            return;
        }

        if (!_shellService.MoveToRecycleBin(item.Path))
        {
            ShowStatus("删除失败，文件可能正在使用或权限不足");
            return;
        }

        ClearItemSelection();
        zone.Items.Remove(item);
        RefreshZone(zone);
        RequestSave();
        ShowStatus("文件已移入回收站");
    }

    private void RefreshZone(ZoneModel zone)
    {
        DesktopCanvas.Children
            .OfType<ZoneCard>()
            .FirstOrDefault(card => card.Model.Id == zone.Id)
            ?.RefreshItems();
    }

    private void RemoveItem(ZoneModel zone, DesktopItem item)
    {
        ClearItemSelection();
        zone.Items.Remove(item);
        RenderZones();
        RequestSave();
        ShowStatus("已移出分区，原文件保持不变");
    }

    private void DeleteZone(ZoneModel zone)
    {
        _state.Zones.Remove(zone);
        // Freeing the space lets neighbours that had yielded for this zone
        // walk back toward their home positions.
        RelaxNeighboursTowardRest();
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
        var zone = new ZoneModel
        {
            CategoryKey = categoryKey,
            Name = name,
            AccentColor = accent,
            Width = 320,
            Height = 230
        };
        zone.CaptureRestBounds();
        return zone;
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

    private static bool _uiLogBroken;

    private static void DebugUiLog(string message)
    {
        if (_uiLogBroken)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskNest");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "ui-debug.log");
            if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
            {
                File.Delete(file);
            }

            File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            _uiLogBroken = true;
        }
    }

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
        DebugUiLog($"toggle mode enter={enterCustomMode} canvas={DesktopCanvas.Visibility} nativeVisible={_mainWindowHandle != 0 && NativeMethods.IsWindowVisible(_mainWindowHandle)}");
        ShowStatus(enterCustomMode ? "已切换到自定义模式" : "已切换到系统模式");
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_state, PreviewAppearance, ApplyWallpaperImmediatelyAsync) { Owner = this };
        if (settings.ShowDialog() != true)
        {
            return;
        }

        var startupConfigured = _startupService.Apply(_state.LaunchAtStartup);
        RenderZones();
        RequestSave();
        ShowStatus(startupConfigured
            ? "外观设置已保存"
            : "外观设置已保存，但开机自启动设置未能更新");
    }

    private void RefreshVisibleItems()
    {
        _iconService.Invalidate();
        var additions = AddUnmappedDesktopItems();
        var removed = RemoveMissingMappings();
        if (additions.Count > 0 || removed > 0)
        {
            RenderZones();
            if (additions.Count > 0)
            {
                HighlightMappingZones(additions);
            }

            RequestSave();
            ShowStatus(additions.Count > 0
                ? FormatMappingStatus(additions)
                : $"已清理 {removed} 个失效映射");
            return;
        }

        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.RefreshItems();
        }
    }

    private void HighlightMappingZones(IReadOnlyList<DesktopMappingAddition> additions)
    {
        foreach (var group in additions.GroupBy(addition => addition.Zone))
        {
            var card = DesktopCanvas.Children
                .OfType<ZoneCard>()
                .FirstOrDefault(value => value.Model.Id == group.Key.Id);
            if (card is null)
            {
                continue;
            }

            card.PlayMappingAddedHighlight();
            foreach (var addition in group)
            {
                card.PlayItemAddedHighlight(addition.Item.Id);
            }

            card.RevealItem(group.Last().Item.Id);
        }
    }

    private static string FormatMappingStatus(IReadOnlyList<DesktopMappingAddition> additions)
    {
        var parts = additions
            .GroupBy(addition => addition.Zone)
            .Select(group =>
            {
                var names = group
                    .Take(3)
                    .Select(addition => $"“{GetMappingName(addition.Item)}”")
                    .ToList();
                var suffix = group.Count() > names.Count ? $"等 {group.Count()} 个项目" : string.Empty;
                return $"{string.Join("、", names)}{suffix} → “{group.Key.Name}”";
            });
        return "已映射：" + string.Join("；", parts);
    }

    private static string GetMappingName(DesktopItem item)
    {
        if (DesktopItem.IsShellLocation(item.Path))
        {
            return item.DisplayName;
        }

        var fileName = Path.GetFileName(item.Path);
        return string.IsNullOrWhiteSpace(fileName) ? item.DisplayName : fileName;
    }

    private void UpdateToolbarState()
    {
        ApplyToolbarAlignment();
        var customMode = _state.DesktopIconsHidden;
        // Keep the desktop surface truly transparent so Explorer owns the native blank-area
        // context menu in both desktop modes.
        RootGrid.Background = Brushes.Transparent;
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
        ToolbarLogo.BeginAnimation(OpacityProperty, null);
        ToolbarLogo.Opacity = 1;
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
        var movingRight = distance < 0;
        ToolbarBorder.IsHitTestVisible = false;
        ToolbarTranslation.X = distance;
        // The left-flying rocket occupies the logo slot. Keep its layout space but hide the
        // stationary logo so the two symbols never overlap during the flight.
        if (!movingRight)
        {
            ToolbarLogo.Opacity = 0;
        }
        CreateToolbarRocketEffects(oldLeft, targetLeft, oldTop, movingRight, duration, easing);

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
            RestoreToolbarLogo();
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
        var rocket = CreateToolbarRocket(movingRight);
        // Keep the rocket inside the screen at both end alignments instead of clipping its nose
        // beyond the desktop edge. It rides on the leading edge as if it is pulling the toolbar.
        var rocketStart = movingRight ? oldLeft + width - rocket.Width : oldLeft;
        var rocketTarget = movingRight ? targetLeft + width - rocket.Width : targetLeft;
        Canvas.SetLeft(rocket, rocketStart);
        Canvas.SetTop(rocket, centerY - rocket.Height / 2);
        ToolbarRocketCanvas.Children.Add(rocket);
        rocket.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(rocketStart, rocketTarget, duration)
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.Stop
        });
        rocket.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            Duration = duration,
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0.96, KeyTime.FromPercent(0)),
                new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.72)),
                new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1))
            }
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

    private Canvas CreateToolbarRocket(bool movingRight)
    {
        const double rocketWidth = 43;
        const double rocketHeight = 27;
        var rocket = new Canvas
        {
            Width = rocketWidth,
            Height = rocketHeight,
            RenderTransformOrigin = new Point(0.5, 0.5),
            Effect = new DropShadowEffect
            {
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.75,
                Color = Color.FromRgb(77, 230, 200)
            }
        };

        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(new ScaleTransform(movingRight ? 1 : -1, 1));
        var tilt = new RotateTransform(0, rocketWidth / 2, rocketHeight / 2);
        transformGroup.Children.Add(tilt);
        rocket.RenderTransform = transformGroup;
        tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(500),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new SplineDoubleKeyFrame(-1.4, KeyTime.FromPercent(0)),
                new SplineDoubleKeyFrame(1.4, KeyTime.FromPercent(1))
            }
        });

        var outerFlame = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 11,8 C 7,7 2,8 0,13.5 C 2,19 7,20 11,18 Z"),
            Fill = new LinearGradientBrush(
                Color.FromRgb(255, 91, 51),
                Color.FromRgb(255, 210, 77),
                new Point(0, 0.5),
                new Point(1, 0.5)),
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.9,
                Color = Color.FromRgb(255, 129, 56)
            },
            RenderTransformOrigin = new Point(1, 0.5)
        };
        var flameScale = new ScaleTransform(1, 1);
        outerFlame.RenderTransform = flameScale;
        flameScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, 1.12, TimeSpan.FromMilliseconds(85))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
        rocket.Children.Add(outerFlame);

        var innerFlame = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 11,10.5 C 8,10 5,11.5 3.5,13.5 C 5.5,16 8,17 11,16.5 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(255, 248, 181)),
            Opacity = 0.95
        };
        rocket.Children.Add(innerFlame);

        var topFin = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 15,7 L 10,1.5 C 16,1 20,3 23,5.5 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(45, 184, 164))
        };
        rocket.Children.Add(topFin);
        var bottomFin = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 15,20 L 10,25.5 C 16,26 20,24 23,21.5 Z"),
            Fill = new SolidColorBrush(Color.FromRgb(45, 184, 164))
        };
        rocket.Children.Add(bottomFin);

        var body = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 9,8 C 16,4 29,3.5 40,13.5 C 29,23.5 16,23 9,19 L 12.5,13.5 Z"),
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(238, 255, 251), 0),
                    new GradientStop(Color.FromRgb(139, 235, 215), 0.55),
                    new GradientStop(Color.FromRgb(65, 188, 169), 1)
                }
            },
            Stroke = new SolidColorBrush(Color.FromRgb(23, 123, 111)),
            StrokeThickness = 0.8
        };
        rocket.Children.Add(body);

        var seam = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 13,7 C 16,11 16,16 13,20"),
            Stroke = new SolidColorBrush(Color.FromArgb(150, 25, 133, 119)),
            StrokeThickness = 1
        };
        rocket.Children.Add(seam);

        var window = new System.Windows.Shapes.Ellipse
        {
            Width = 7.5,
            Height = 7.5,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(220, 255, 250), 0),
                    new GradientStop(Color.FromRgb(47, 137, 175), 0.58),
                    new GradientStop(Color.FromRgb(14, 55, 83), 1)
                }
            },
            Stroke = new SolidColorBrush(Color.FromRgb(226, 255, 250)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(window, 24);
        Canvas.SetTop(window, 9.75);
        rocket.Children.Add(window);
        return rocket;
    }

    private void RestoreToolbarLogo()
    {
        ToolbarLogo.BeginAnimation(OpacityProperty, null);
        ToolbarLogo.Opacity = 1;
        ToolbarLogo.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }

    private void StopToolbarFlight()
    {
        _toolbarFlightVersion++;
        ToolbarTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        ToolbarTranslation.X = 0;
        ToolbarRocketCanvas.Children.Clear();
        ToolbarLogo.BeginAnimation(OpacityProperty, null);
        ToolbarLogo.Opacity = 1;
        ToolbarBorder.IsHitTestVisible = true;
    }

    /// <summary>纯系统级壁纸切换：走 Windows 设置同款的 WinRT 个性化接口，
    /// DWM 自带过渡动画；WinRT 不可用时回退到 SPI（无广播）。</summary>
    private async Task<bool> ApplyWallpaperImmediatelyAsync(string? selection)
    {
        var targetPath = _wallpaperService.ResolveSelection(selection);
        if (targetPath is null)
        {
            return false;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ok = await _wallpaperService.ApplyPathModernAsync(targetPath);
        var modern = ok;
        if (!ok)
        {
            ok = await Task.Run(() => _wallpaperService.ApplyPath(targetPath));
        }

        stopwatch.Stop();
        DebugUiLog($"wallpaper apply took {stopwatch.ElapsedMilliseconds}ms modern={modern} ok={ok} path={Path.GetFileName(targetPath)}");
        if (!ok)
        {
            ShowStatus("背景图片暂时无法应用");
            return false;
        }

        return true;
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
        var themeChanged = _appliedLightTheme is { } previousTheme && previousTheme != lightTheme;
        if (themeChanged)
        {
            BeginThemeTransition();
        }

        DebugUiLog($"preview appearance toolbar={animateToolbar} themeChanged={themeChanged} light={lightTheme}");

        foreach (var card in DesktopCanvas.Children.OfType<ZoneCard>())
        {
            card.SetVisualOptions(_state.PanelOpacity, _state.IconSize, lightTheme);
        }

        ApplyThemeSurface(lightTheme);
        if (themeChanged)
        {
            PlayThemeTransition();
        }
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
        _appliedLightTheme = lightTheme;
    }

    private void BeginThemeTransition()
    {
        if (!IsLoaded || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        ThemeTransitionImage.BeginAnimation(OpacityProperty, null);
        ThemeTransitionImage.Source = null;
        ThemeTransitionImage.Visibility = Visibility.Collapsed;
        UpdateLayout();

        var snapshot = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight)),
            96,
            96,
            PixelFormats.Pbgra32);
        snapshot.Render(RootGrid);
        ThemeTransitionImage.Source = snapshot;
        ThemeTransitionImage.Opacity = 1;
        ThemeTransitionImage.Visibility = Visibility.Visible;
        DebugUiLog($"theme snapshot {(int)ActualWidth}x{(int)ActualHeight}");
    }

    private void PlayThemeTransition()
    {
        if (ThemeTransitionImage.Source is null)
        {
            return;
        }

        DebugUiLog("theme fade start");
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            ThemeTransitionImage.BeginAnimation(OpacityProperty, null);
            ThemeTransitionImage.Opacity = 0;
            ThemeTransitionImage.Source = null;
            ThemeTransitionImage.Visibility = Visibility.Collapsed;
            DebugUiLog("theme fade done");
        };
        ThemeTransitionImage.BeginAnimation(OpacityProperty, fade);
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

    private void EnsureCustomDesktopWindowVisible()
    {
        if (!_loaded || !_state.DesktopIconsHidden || _exitRequested || _mainWindowHandle == 0 ||
            NativeMethods.IsWindowVisible(_mainWindowHandle))
        {
            return;
        }

        DebugUiLog("ensure window: hidden detected, restoring");
        try
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (!IsVisible)
            {
                Show();
            }

            _desktopHost.Reattach(this);
            _desktopHost.ResizeToVirtualScreen();
            _iconVisibility.SetVisible(false);
            UpdateToolbarState();
        }
        catch (InvalidOperationException)
        {
            // The window may be in the middle of a shell transition; the next timer tick retries.
        }
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

        ClearItemSelection();
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
