using FocusHarbor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class PortableUiHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window main = null;
        RewardSubjectNameWindow rename = null;
        try
        {
            main = MainWindowController.LoadWindow();
            if (main.FindName("AddRewardSubjectButton") == null ||
                main.FindName("RewardSubjectManageHint") == null ||
                main.FindName("RewardSubjects") == null)
            {
                Console.WriteLine("PORTABLE_REWARD_UI_MISSING");
                return 2;
            }

            foreach (string pageName in new[] { "OverviewPage", "BrowserPage", "AppsPage", "AlarmPage", "SettingsPage" })
            {
                ((Grid)main.FindName(pageName)).Visibility = Visibility.Collapsed;
            }
            ((Grid)main.FindName("RewardsPage")).Visibility = Visibility.Visible;
            ((Grid)main.FindName("RewardsPage")).Opacity = 1;
            ((TextBlock)main.FindName("PageTitle")).Text = "学习奖励";
            ((TextBlock)main.FindName("RewardSubjectManageHint")).Text = "仅在临时解除限制期间可以添加、删除或重命名";
            ((Button)main.FindName("AddRewardSubjectButton")).IsEnabled = false;
            List<RewardSubjectViewModel> subjects = new List<RewardSubjectViewModel>();
            for (int index = 1; index <= 4; index++)
            {
                subjects.Add(new RewardSubjectViewModel
                {
                    Id = "subject-" + index,
                    Name = "科目" + index,
                    Badge = index.ToString(),
                    Description = "为今天的科目" + index + "任务负责，完成后再诚实确认。",
                    Status = "待确认今日任务",
                    Accent = index % 2 == 0 ? "#FFB8AFFF" : "#FF6FE1D5",
                    ActionText = "确认完成并手写说明",
                    ActionEnabled = true,
                    ManageEnabled = false
                });
            }
            ((ItemsControl)main.FindName("RewardSubjects")).ItemsSource = subjects;
            main.Width = 1080;
            main.Height = 740;
            main.Show();
            application.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
            main.Measure(new Size(1080, 740));
            main.Arrange(new Rect(0, 0, 1080, 740));
            main.UpdateLayout();
            if (args.Length == 1)
            {
                RenderTargetBitmap bitmap = new RenderTargetBitmap(1080, 740, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(main);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream stream = new FileStream(args[0], FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    encoder.Save(stream);
                }
            }

            Style buttonStyle = new Style(typeof(Button));
            rename = new RewardSubjectNameWindow("科目1", buttonStyle, buttonStyle);
            rename.Show();
            rename.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            if (!rename.IsLoaded || !rename.IsVisible)
            {
                Console.WriteLine("RENAME_DIALOG_FAILED");
                return 3;
            }
            Console.WriteLine("PORTABLE_REWARD_UI_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("PORTABLE_REWARD_UI_FAILED: " + exception.GetType().FullName + " · " + exception.Message);
            Console.WriteLine(exception.StackTrace ?? String.Empty);
            return 1;
        }
        finally
        {
            if (rename != null)
            {
                rename.Close();
            }
            if (main != null)
            {
                main.Close();
            }
            application.Shutdown();
        }
    }
}
