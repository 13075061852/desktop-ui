using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DeskNest.Core.Models;

internal static class Program
{
    // Construct controls without starting DeskNest or changing the user's desktop.
    [STAThread]
    private static int Main()
    {
        try
        {
            var application = new Application();
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/DeskNest.App;component/Themes/DeskNestTheme.xaml", UriKind.Relative)
            });
            foreach (var key in new[] { "ToolbarButtonStyle", "PrimaryButtonStyle", "IconButtonStyle" })
            {
                var button = new Button { Content = "测试", Style = (Style)application.FindResource(key) };
                button.ApplyTemplate();
                button.Measure(new Size(240, 60));
                button.Arrange(new Rect(0, 0, 240, 60));
                button.IsEnabled = false;
                Console.WriteLine($"PASS  {key} template");
            }

            var assembly = typeof(DeskNest.App.App).Assembly;
            var cardType = assembly.GetType("DeskNest.App.Controls.ZoneCard", throwOnError: true)!;
            var icons = Activator.CreateInstance(assembly.GetType("DeskNest.App.Services.ShellIconService")!);
            var model = new ZoneModel { Name = "列表测试", Width = 240, Height = 240, ViewMode = "List" };
            var card = (UserControl)Activator.CreateInstance(cardType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { model, icons }, null)!;
            cardType.GetMethod("SetVisualOptions")!.Invoke(card, new object[] { 0.9, 44d, true });
            card.Measure(new Size(240, 240));
            card.Arrange(new Rect(0, 0, 240, 240));
            card.UpdateLayout();
            var scroller = (ScrollViewer)card.FindName("BodyScroller");
            var width = (double)cardType.GetMethod("GetListTileWidth", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(card, null)!;
            if (width <= 0 || width + 4 > scroller.ViewportWidth + 0.1)
                throw new InvalidOperationException("List tile exceeds its viewport.");
            cardType.GetMethod("SetVisualOptions")!.Invoke(card, new object[] { 0.9, 44d, false });
            Console.WriteLine("PASS  zone light/dark rendering and list viewport sizing");
            application.Shutdown();
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
