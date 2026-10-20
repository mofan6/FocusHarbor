using FocusHarbor;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class UiRenderHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2)
        {
            return 64;
        }
        Application app = new Application();
        Window window = MainWindowController.LoadWindow();
        MainWindowController controller = new MainWindowController(window, JsonStore.LoadConfig(), Path.GetFullPath("dist\\FocusHarbor.exe"), false);
        ((Grid)window.FindName("OverviewPage")).Visibility = Visibility.Collapsed;
        ((Grid)window.FindName("BrowserPage")).Visibility = Visibility.Collapsed;
        ((Grid)window.FindName("AppsPage")).Visibility = Visibility.Collapsed;
        ((Grid)window.FindName("RewardsPage")).Visibility = Visibility.Collapsed;
        ((Grid)window.FindName("SettingsPage")).Visibility = Visibility.Collapsed;
        Grid alarm = (Grid)window.FindName("AlarmPage");
        Grid rewards = (Grid)window.FindName("RewardsPage");
        bool showRewards = args.Length == 2 && args[1].StartsWith("rewards", StringComparison.OrdinalIgnoreCase);
        Grid target = showRewards ? rewards : alarm;
        target.Visibility = Visibility.Visible;
        target.Opacity = 1;
        ((TextBlock)window.FindName("PageTitle")).Text = showRewards ? "学习奖励" : "晨间闹钟";
        window.Width = 1080;
        window.Height = 740;
        window.Show();
        app.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        window.Measure(new Size(1080, 740));
        window.Arrange(new Rect(0, 0, 1080, 740));
        window.UpdateLayout();
        if (showRewards && args[1].IndexOf("hover", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Button hoverTarget = (Button)window.FindName("UseRewardButton");
            hoverTarget.IsEnabled = true;
            hoverTarget.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
        RenderTargetBitmap bitmap = new RenderTargetBitmap(1080, 740, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = new FileStream(args[0], FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }
        window.Close();
        app.Shutdown();
        GC.KeepAlive(controller);
        return 0;
    }
}
