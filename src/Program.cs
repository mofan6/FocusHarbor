using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace FocusHarbor
{
    public static class Program
    {
        public static string ExecutablePath
        {
            get
            {
                return Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
            }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            string mode = args != null && args.Length > 0 ? args[0].Trim().ToLowerInvariant() : String.Empty;
            try
            {
                if (mode == "--guard")
                {
                    GuardService.RunGuardLoop(ExecutablePath);
                    return 0;
                }
                if (mode == "--alarm")
                {
                    AlarmService.RunChallenge(ExecutablePath);
                    return 0;
                }
                if (mode == "--alarm-warmup")
                {
                    AlarmService.RunWarmup(ExecutablePath);
                    return 0;
                }
                if (mode == "--wake-beacon")
                {
                    // Keep the resumed machine awake long enough for the
                    // interactive locked-session warm-up task to reach the alarm.
                    AlarmService.HoldWakeBeacon();
                    return 0;
                }
                if (mode == "--register-alarm-elevated")
                {
                    if (!TaskSchedulerService.IsCurrentProcessElevated() || args.Length < 3)
                    {
                        return 5;
                    }
                    int hour;
                    int minute;
                    if (!Int32.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour) ||
                        !Int32.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute) ||
                        hour < 0 || hour > 23 || minute < 0 || minute > 59)
                    {
                        return 64;
                    }
                    TaskSchedulerService.RegisterDailyAlarmHighest(hour, minute, ExecutablePath);
                    return 0;
                }
                if (mode == "--remove-alarm-elevated")
                {
                    if (!TaskSchedulerService.IsCurrentProcessElevated())
                    {
                        return 5;
                    }
                    TaskSchedulerService.DeleteDailyAlarmTasks();
                    return 0;
                }
                if (mode == "--restore-temp")
                {
                    return RestrictionService.RestoreTemporary(ExecutablePath).ExitCode;
                }
                if (mode == "--restore-quarantine")
                {
                    if (args.Length < 2)
                    {
                        return 64;
                    }
                    return AlarmService.RestoreQuarantinedFile(args[1]).ExitCode;
                }
                if (mode == "--pause")
                {
                    FocusConfig pauseConfig = BootstrapService.InitializeUi(ExecutablePath);
                    OperationResult pauseResult = RestrictionService.PauseAll(pauseConfig, ExecutablePath);
                    MessageBox.Show(
                        pauseResult.Message,
                        pauseResult.Success ? "临时解除限制" : "无法临时解除",
                        MessageBoxButton.OK,
                        pauseResult.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    return pauseResult.ExitCode;
                }
                if (mode == "--validate-ui")
                {
                    Application validationApplication = new Application();
                    FocusConfig validationConfig = JsonStore.LoadConfig();
                    Window validationWindow = MainWindowController.LoadWindow();
                    MainWindowController validationController = new MainWindowController(validationWindow, validationConfig, ExecutablePath, false);
                    validationWindow.Measure(new Size(1080, 740));
                    validationWindow.Arrange(new Rect(0, 0, 1080, 740));
                    validationWindow.Close();
                    GC.KeepAlive(validationController);
                    validationApplication.Shutdown();
                    return 0;
                }

                using (Mutex uiMutex = new Mutex(false, @"Local\FocusHarborUi"))
                {
                    bool firstInstance = false;
                    try
                    {
                        firstInstance = MutexSafety.Wait(uiMutex, 0);
                        if (!firstInstance)
                        {
                            return 0;
                        }

                        FocusConfig config = BootstrapService.InitializeUi(ExecutablePath);
                        Application application = new Application();
                        application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                        application.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
                        {
                            JsonStore.AppendLog("UI error: " + eventArgs.Exception);
                            MessageBox.Show("软件遇到错误：" + eventArgs.Exception.Message, "Focus Harbor", MessageBoxButton.OK, MessageBoxImage.Error);
                            eventArgs.Handled = true;
                        };

                        Window window = MainWindowController.LoadWindow();
                        MainWindowController controller = new MainWindowController(window, config, ExecutablePath, true);
                        application.Run(window);
                        GC.KeepAlive(controller);
                        return 0;
                    }
                    finally
                    {
                        if (firstInstance)
                        {
                            uiMutex.ReleaseMutex();
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                JsonStore.AppendLog("Fatal error: " + exception);
                if (mode != "--guard" && mode != "--alarm" && mode != "--alarm-warmup" && mode != "--wake-beacon" &&
                    mode != "--register-alarm-elevated" && mode != "--remove-alarm-elevated" &&
                    mode != "--restore-temp" && mode != "--restore-quarantine" && mode != "--validate-ui")
                {
                    MessageBox.Show("启动失败：" + exception.Message, "Focus Harbor", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                return 1;
            }
        }
    }
}
