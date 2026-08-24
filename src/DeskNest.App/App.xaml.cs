using System.Threading;
using System.Windows;

namespace DeskNest.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Local\\DeskNest.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            System.Windows.MessageBox.Show(
                "栖格已经在运行，请查看桌面或系统托盘。",
                "栖格 · DeskNest",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
