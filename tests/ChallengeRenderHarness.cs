using FocusHarbor;
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class ChallengeRenderHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            return 64;
        }
        Application app = new Application();
        AlarmChallengeState state = new AlarmChallengeState
        {
            Active = true,
            StartedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            DeadlineAt = DateTimeOffset.Now.AddMinutes(15).ToString("o", CultureInfo.InvariantCulture),
            ChallengeText = new string('早', 160),
            MusicPath = String.Empty,
            DepositFolder = @"E:\FocusHarborDeposit",
            QuarantineHours = 120
        };
        Type windowType = typeof(AlarmService).Assembly.GetType("FocusHarbor.AlarmChallengeWindow", true);
        Window window = (Window)Activator.CreateInstance(windowType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { state, Path.GetFullPath("dist\\FocusHarbor.exe") }, CultureInfo.InvariantCulture);
        window.Show();
        app.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        TextBox input = (TextBox)windowType.GetField("input", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window);
        input.Text = "早早错";
        app.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
        window.Measure(new Size(1040, 720));
        window.Arrange(new Rect(0, 0, 1040, 720));
        window.UpdateLayout();
        RenderTargetBitmap bitmap = new RenderTargetBitmap(1040, 720, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = new FileStream(args[0], FileMode.Create, FileAccess.Write, FileShare.None))
        {
            encoder.Save(stream);
        }
        windowType.GetField("mayClose", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, true);
        window.Close();
        app.Shutdown();
        return 0;
    }
}
