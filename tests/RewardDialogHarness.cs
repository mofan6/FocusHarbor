using FocusHarbor;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class RewardDialogHarness
{
    [STAThread]
    public static int Main()
    {
        Application application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Style buttonStyle = new Style(typeof(Button));
        RewardStatementWindow dialog = new RewardStatementWindow("高数", buttonStyle, buttonStyle);
        try
        {
            dialog.Show();
            dialog.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
            if (!dialog.IsLoaded || !dialog.IsVisible)
            {
                Console.WriteLine("REWARD_DIALOG_NOT_VISIBLE");
                return 2;
            }

            Console.WriteLine("REWARD_DIALOG_OK");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine("REWARD_DIALOG_FAILED: " + exception);
            return 1;
        }
        finally
        {
            dialog.Close();
            application.Shutdown();
        }
    }
}
