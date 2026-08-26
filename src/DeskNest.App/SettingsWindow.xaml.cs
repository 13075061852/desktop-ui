using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DeskNest.App.Services;
using DeskNest.Core.Models;
using Microsoft.Win32;

namespace DeskNest.App;

public partial class SettingsWindow : System.Windows.Window
{
    private readonly AppState _state;
    private readonly DesktopWallpaperService _wallpaperService = new();
    private readonly Action<bool>? _previewAppearance;
    private readonly Func<string?, bool>? _applyWallpaper;
    private readonly string _originalWallpaperPath;
    private readonly string _originalTheme;
    private readonly double _originalPanelOpacity;
    private readonly double _originalIconSize;
    private readonly string _originalToolbarAlignment;
    private string _pendingWallpaperSelection;
    private bool _settingsCommitted;
    private bool _wallpaperPreviewChanged;
    private bool _wallpaperRestoreQueued;
    private bool? _appliedLightTheme;

    public SettingsWindow(
        AppState state,
        Action<bool>? previewAppearance = null,
        Func<string?, bool>? applyWallpaper = null)
    {
        _state = state;
        _previewAppearance = previewAppearance;
        _applyWallpaper = applyWallpaper;
        _pendingWallpaperSelection = state.WallpaperSelection;
        _originalWallpaperPath = _wallpaperService.GetCurrentWallpaperPath();
        _originalTheme = state.Theme;
        _originalPanelOpacity = state.PanelOpacity;
        _originalIconSize = state.IconSize;
        _originalToolbarAlignment = state.ToolbarAlignment;
        InitializeComponent();

        (state.Theme switch
        {
            "Dark" => ThemeDarkChoice,
            "Light" => ThemeLightChoice,
            _ => ThemeSystemChoice
        }).IsChecked = true;
        (state.ToolbarAlignment switch
        {
            "Left" => ToolbarLeftChoice,
            "Right" => ToolbarRightChoice,
            _ => ToolbarCenterChoice
        }).IsChecked = true;
        OpacitySlider.Value = state.PanelOpacity;
        IconSizeSlider.Value = state.IconSize;
        UpdateLabels();
        ApplyWindowTheme();
        BuildWallpaperOptions();
        Closing += OnWindowClosing;
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (IsLoaded)
        {
            PreviewCurrentAppearance();
        }
    }

    private void OnIconSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateLabels();
        if (IsLoaded)
        {
            PreviewCurrentAppearance();
        }
    }

    private void OnThemeSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            PreviewCurrentAppearance();
        }
    }

    private void OnToolbarAlignmentChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            PreviewCurrentAppearance(animateToolbar: true);
        }
    }

    private void PreviewCurrentAppearance(bool animateToolbar = false)
    {
        var targetLightTheme = IsLightTheme(GetSelectedTheme());
        var themeChanged = _appliedLightTheme is { } previousTheme && previousTheme != targetLightTheme;
        if (themeChanged)
        {
            BeginThemeTransition();
        }

        _state.Theme = GetSelectedTheme();
        _state.PanelOpacity = OpacitySlider.Value;
        _state.IconSize = IconSizeSlider.Value;
        _state.ToolbarAlignment = GetSelectedToolbarAlignment();
        ApplyWindowTheme();
        if (themeChanged)
        {
            PlayThemeTransition();
        }

        _previewAppearance?.Invoke(animateToolbar);
    }

    private string GetSelectedTheme()
    {
        return new[] { ThemeSystemChoice, ThemeDarkChoice, ThemeLightChoice }
            .FirstOrDefault(choice => choice.IsChecked == true)?.Tag?.ToString()
            ?? "System";
    }

    private string GetSelectedToolbarAlignment()
    {
        return new[] { ToolbarLeftChoice, ToolbarCenterChoice, ToolbarRightChoice }
            .FirstOrDefault(choice => choice.IsChecked == true)?.Tag?.ToString()
            ?? "Center";
    }

    private void ApplyWindowTheme()
    {
        var theme = GetSelectedTheme();
        var isLight = IsLightTheme(theme);

        var surfaceAlpha = (byte)(255 * Math.Clamp(OpacitySlider.Value, 0, 1));
        Resources["SettingsSurfaceBrush"] = isLight
            ? Brush(surfaceAlpha, 247, 249, 252)
            : Brush(surfaceAlpha, 23, 29, 39);
        Resources["SettingsElevatedBrush"] = isLight
            ? Brush(surfaceAlpha, 240, 243, 247)
            : Brush(surfaceAlpha, 36, 44, 57);
        Resources["SettingsInputBrush"] = isLight
            ? Brush(surfaceAlpha, 255, 255, 255)
            : Brush(surfaceAlpha, 32, 41, 54);
        Resources["SettingsSolidInputBrush"] = Brush(isLight ? "#FFFFFFFF" : "#FF202936");
        Resources["SettingsTextBrush"] = Brush(isLight ? "#FF1E2836" : "#FFF7F9FC");
        Resources["SettingsMutedBrush"] = Brush(isLight ? "#FF667386" : "#FFAAB5C5");
        Resources["SettingsStrokeBrush"] = Brush(isLight ? "#281E2836" : "#38FFFFFF");
        Resources["SettingsHoverBrush"] = Brush(isLight ? "#161E2836" : "#24FFFFFF");
        Resources["SettingsTrackBrush"] = Brush(isLight ? "#345B6878" : "#405B6878");
        _appliedLightTheme = isLight;
    }

    private void BeginThemeTransition()
    {
        if (!IsLoaded || RootBorder.ActualWidth <= 0 || RootBorder.ActualHeight <= 0)
        {
            return;
        }

        ThemeTransitionImage.BeginAnimation(OpacityProperty, null);
        ThemeTransitionImage.Source = null;
        ThemeTransitionImage.Visibility = Visibility.Collapsed;
        UpdateLayout();

        var snapshot = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(RootBorder.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(RootBorder.ActualHeight)),
            96,
            96,
            PixelFormats.Pbgra32);
        snapshot.Render(RootBorder);
        ThemeTransitionImage.Source = snapshot;
        ThemeTransitionImage.Opacity = 1;
        ThemeTransitionImage.Visibility = Visibility.Visible;
    }

    private void PlayThemeTransition()
    {
        if (ThemeTransitionImage.Source is null)
        {
            return;
        }

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
        };
        ThemeTransitionImage.BeginAnimation(OpacityProperty, fade);
    }

    private static bool IsLightTheme(string theme)
    {
        if (theme == "Light")
        {
            return true;
        }

        if (theme == "Dark")
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

    private static SolidColorBrush Brush(string color)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
    }

    private void BuildWallpaperOptions()
    {
        WallpaperOptionsPanel.Items.Clear();
        foreach (var option in _wallpaperService.GetBuiltInOptions())
        {
            var imagePath = _wallpaperService.ResolveSelection(option.Key);
            if (imagePath is null)
            {
                continue;
            }

            var radio = new RadioButton
            {
                Style = (Style)FindResource("WallpaperOptionStyle"),
                Tag = option.Key,
                Content = option.Name,
                Background = new ImageBrush(LoadImage(imagePath))
                {
                    Stretch = Stretch.UniformToFill,
                    AlignmentX = AlignmentX.Center,
                    AlignmentY = AlignmentY.Center
                },
                IsChecked = string.Equals(_pendingWallpaperSelection, option.Key, StringComparison.OrdinalIgnoreCase),
                GroupName = "DesktopWallpaper"
            };
            radio.Checked += OnWallpaperOptionChecked;
            WallpaperOptionsPanel.Items.Add(radio);
        }

        UpdateImportButtonState();
    }

    private static BitmapImage LoadImage(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void OnWallpaperOptionChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string selection })
        {
            return;
        }

        if (!(_applyWallpaper?.Invoke(selection) ?? _wallpaperService.ApplySelection(selection)))
        {
            System.Windows.MessageBox.Show("背景图片暂时无法应用。", "栖格设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _pendingWallpaperSelection = selection;
        UpdateWallpaperPreviewState();
        UpdateImportButtonState();
    }

    private void OnImportWallpaperClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择桌面背景",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var importedPath = _wallpaperService.Import(dialog.FileName);
            if (!(_applyWallpaper?.Invoke(importedPath) ?? _wallpaperService.ApplyPath(importedPath)))
            {
                throw new IOException("Windows 无法应用所选背景图片。");
            }

            _pendingWallpaperSelection = importedPath;
            UpdateWallpaperPreviewState();
            foreach (var option in WallpaperOptionsPanel.Items.OfType<RadioButton>())
            {
                option.IsChecked = false;
            }

            UpdateImportButtonState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Windows.MessageBox.Show(exception.Message, "无法导入背景", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateWallpaperPreviewState()
    {
        var previewPath = _wallpaperService.ResolveSelection(_pendingWallpaperSelection);
        _wallpaperPreviewChanged = !string.Equals(
            previewPath,
            _originalWallpaperPath,
            StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateImportButtonState()
    {
        var isBuiltIn = _wallpaperService.GetBuiltInOptions().Any(option =>
            string.Equals(option.Key, _pendingWallpaperSelection, StringComparison.OrdinalIgnoreCase));
        ImportWallpaperButton.Content = !isBuiltIn && File.Exists(_pendingWallpaperSelection)
            ? "已选择自定义背景"
            : "导入图片";
    }

    private void UpdateLabels()
    {
        if (OpacityValue is null || IconSizeValue is null)
        {
            return;
        }

        OpacityValue.Text = $"{OpacitySlider.Value:P0}";
        IconSizeValue.Text = $"{IconSizeSlider.Value:0} px";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        _state.Theme = GetSelectedTheme();
        _state.PanelOpacity = OpacitySlider.Value;
        _state.IconSize = IconSizeSlider.Value;
        _state.ToolbarAlignment = GetSelectedToolbarAlignment();
        _state.WallpaperSelection = _pendingWallpaperSelection;
        _settingsCommitted = true;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_settingsCommitted)
        {
            return;
        }

        _state.Theme = _originalTheme;
        _state.PanelOpacity = _originalPanelOpacity;
        _state.IconSize = _originalIconSize;
        _state.ToolbarAlignment = _originalToolbarAlignment;
        Dispatcher.BeginInvoke(
            () => _previewAppearance?.Invoke(false),
            System.Windows.Threading.DispatcherPriority.Background);

        if (!_wallpaperPreviewChanged || _wallpaperRestoreQueued)
        {
            return;
        }

        _wallpaperRestoreQueued = true;
        var originalPath = _originalWallpaperPath;
        if (_applyWallpaper is not null)
        {
            Dispatcher.BeginInvoke(
                () => _applyWallpaper(originalPath),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            _ = Task.Run(() => _wallpaperService.RestorePath(originalPath));
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
