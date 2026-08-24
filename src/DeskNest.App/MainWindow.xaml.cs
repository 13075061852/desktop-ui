using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DeskNest.App.Controls;
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
    private readonly ShellService _shellService = new();
    private readonly DesktopHostService _desktopHost = new();
    private readonly DesktopIconVisibilityService _iconVisibility = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _statusTimer;

    private AppState _state = AppState.CreateDefault();
    private DesktopWatcher? _desktopWatcher;
    private TrayService? _trayService;
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
        _desktopHost.TryEmbed(this);
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

        if (_state.DesktopIconsHidden)
        {
            _iconVisibility.SetVisible(false);
        }

        RenderZones();
        UpdateToolbarState();

        _desktopWatcher = new DesktopWatcher();
        _desktopWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshVisibleItems);
        _trayService = new TrayService(
            () => Dispatcher.Invoke(ShowDeskNest),
            () => Dispatcher.Invoke(() => OrganizeDesktop(showNotification: true)),
            () => Dispatcher.Invoke(ToggleDesktopIcons),
            () => Dispatcher.Invoke(ExitApplication));

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
        DesktopCanvas.Children.Clear();
        var lightTheme = IsLightTheme();

        foreach (var zone in _state.Zones)
        {
            var card = new ZoneCard(zone, _iconService);
            card.SetLayoutLocked(_state.LayoutLocked);
            card.SetVisualOptions(_state.PanelOpacity, _state.IconSize, lightTheme);
            card.ModelChanged += (_, _) => RequestSave();
            card.DeleteRequested += (_, _) => DeleteZone(zone);
            card.FilesDropped += (_, args) => AddPathsToZone(zone, args.Paths);
            card.ItemMoveRequested += (_, args) => MoveItem(zone, args.Payload);
            card.ItemOpenRequested += (_, args) => OpenItem(args.Item);
            card.ItemRevealRequested += (_, args) => RevealItem(args.Item);
            card.ItemRemoveRequested += (_, args) => RemoveItem(zone, args.Item);

            Canvas.SetLeft(card, Math.Max(0, zone.X));
            Canvas.SetTop(card, Math.Max(72, zone.Y));
            DesktopCanvas.Children.Add(card);
        }

        ApplyThemeSurface(lightTheme);
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

    private void OnToggleIconsClick(object sender, RoutedEventArgs e) => ToggleDesktopIcons();

    private void ToggleDesktopIcons()
    {
        var shouldShow = !_iconVisibility.AreIconsVisible;
        if (!_iconVisibility.SetVisible(shouldShow))
        {
            ShowStatus("暂时无法访问 Windows 桌面图标层");
            return;
        }

        _state.DesktopIconsHidden = !shouldShow;
        UpdateToolbarState();
        RequestSave();
        ShowStatus(shouldShow ? "已显示 Windows 原桌面图标" : "已临时隐藏原图标，文件仍安全保留");
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_state) { Owner = this };
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
        LockButton.Content = _state.LayoutLocked ? "◆ 解锁布局" : "◇ 锁定布局";
        IconVisibilityButton.Content = _state.DesktopIconsHidden ? "◉ 显示原图标" : "◉ 隐藏原图标";
    }

    private void ApplyThemeSurface(bool lightTheme)
    {
        ToolbarBorder.Background = new SolidColorBrush(Color.FromArgb(244, 24, 30, 41));
        StatusBorder.Background = new SolidColorBrush(Color.FromArgb(244, 24, 30, 41));
        BrandText.Foreground = Brushes.White;
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
