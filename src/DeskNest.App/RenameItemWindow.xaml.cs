using System.IO;
using System.Windows;
using System.Windows.Input;

namespace DeskNest.App;

public partial class RenameItemWindow : Window
{
    private readonly Func<string, bool>? _validator;
    private readonly string _validationMessage;

    public RenameItemWindow(
        string currentName,
        bool preservesExtension,
        string dialogTitle = "重命名",
        string actionTitle = "重命名文件",
        string? hint = null,
        Func<string, bool>? validator = null,
        string validationMessage = "请输入有效的文件名称。")
    {
        _validator = validator;
        _validationMessage = validationMessage;
        InitializeComponent();
        Title = dialogTitle;
        DialogTitleText.Text = dialogTitle;
        ActionTitleText.Text = actionTitle;
        NameTextBox.Text = currentName;
        HintText.Text = hint ?? (preservesExtension
            ? "输入新名称，文件扩展名会自动保留"
            : "输入文件夹的新名称");
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string NewName => NameTextBox.Text.Trim();

    private void OnConfirmClick(object sender, RoutedEventArgs e) => Confirm();

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        var name = NewName;
        var isValid = _validator?.Invoke(name) ??
                      (!string.IsNullOrWhiteSpace(name) &&
                       name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                       name is not "." and not "..");
        if (!isValid)
        {
            MessageBox.Show(this, _validationMessage, "输入内容无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            NameTextBox.Focus();
            NameTextBox.SelectAll();
            return;
        }

        DialogResult = true;
    }
}
