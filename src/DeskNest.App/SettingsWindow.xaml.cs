using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskNest.Core.Models;

namespace DeskNest.App;

public partial class SettingsWindow : System.Windows.Window
{
    private readonly AppState _state;

    public SettingsWindow(AppState state)
    {
        _state = state;
        InitializeComponent();

        ThemeCombo.SelectedIndex = state.Theme switch
        {
            "Dark" => 1,
            "Light" => 2,
            _ => 0
        };
        OpacitySlider.Value = state.PanelOpacity;
        IconSizeSlider.Value = state.IconSize;
        UpdateLabels();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

    private void OnIconSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateLabels();

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
        _state.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        _state.PanelOpacity = OpacitySlider.Value;
        _state.IconSize = IconSizeSlider.Value;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
