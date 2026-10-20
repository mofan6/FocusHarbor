using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace FocusHarbor
{
    public static class TaskSchedulerService
    {
        public const string TemporaryRestoreTaskName = "Codex-Restore-Temporary-Restriction-Pause";
        public const string LegacyLongTermRestoreTaskName = "Codex-Restore-SelfControl-Restrictions";
        public const string AlarmTaskName = "FocusHarbor-Daily-Wake-Alarm";
        public const string AlarmWakeBeaconTaskName = "FocusHarbor-Daily-Wake-Beacon";
        public const string AlarmFallbackTaskName = "FocusHarbor-Daily-Alarm-Fallback";
        public const string GuardRecoveryTaskName = "FocusHarbor-Guard-Recovery";
        public const string QuarantineRestoreTaskPrefix = "FocusHarbor-Restore-Quarantine-";
        public const int AlarmWakeLeadMinutes = 1;

        public static string GetQuarantineRestoreTaskName(string recordId)
        {
            return QuarantineRestoreTaskPrefix + (recordId ?? String.Empty).Replace("\\", String.Empty).Replace("/", String.Empty);
        }

        public static bool IsCurrentProcessElevated()
        {
            WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static void RegisterDailyAlarm(int hour, int minute, string executablePath)
        {
            RegisterDailyAlarmDefinition(
                hour,
                minute,
                executablePath,
                false,
                AlarmTaskName,
                "--alarm-warmup",
                -AlarmWakeLeadMinutes,
                "Wake the computer one minute early and warm up Focus Harbor before the morning challenge.");
            RegisterDailyAlarmDefinition(
                hour,
                minute,
                executablePath,
                false,
                AlarmFallbackTaskName,
                "--alarm",
                0,
                "Fallback launcher for the Focus Harbor morning typing challenge.");
        }

        public static void RegisterDailyAlarmHighest(int hour, int minute, string executablePath)
        {
            // Configure the power plan before replacing an existing working alarm,
            // so a power-policy failure cannot leave a half-updated schedule.
            ConfigureWakeTimers();
            RegisterDailyAlarmDefinition(
                hour,
                minute,
                executablePath,
                true,
                AlarmTaskName,
                "--alarm-warmup",
                -AlarmWakeLeadMinutes,
                "Wake the computer one minute early and warm up Focus Harbor before the morning challenge.");
            RegisterDailyAlarmDefinition(
                hour,
                minute,
                executablePath,
                true,
                AlarmFallbackTaskName,
                "--alarm",
                0,
                "Fallback launcher for the Focus Harbor morning typing challenge.");
            RegisterSystemWakeBeacon(hour, minute, executablePath);
        }

        public static void DeleteDailyAlarmTasks()
        {
            DeleteTask(AlarmTaskName);
            DeleteTask(AlarmWakeBeaconTaskName);
            DeleteTask(AlarmFallbackTaskName);
        }

        public static void RegisterGuardRecovery(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new FileNotFoundException("Focus Harbor 主程序不存在，无法创建守卫恢复任务。", executablePath);
            }
            dynamic service = null;
            dynamic folder = null;
            dynamic definition = null;
            dynamic logonTrigger = null;
            dynamic heartbeatTrigger = null;
            dynamic action = null;
            dynamic registeredTask = null;
            try
            {
                Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(serviceType);
                service.Connect();
                folder = service.GetFolder("\\");
                definition = service.NewTask(0);

                definition.RegistrationInfo.Description = "Restart the Focus Harbor guard after sign-in or an unexpected termination.";
                definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
                definition.Principal.RunLevel = 0; // regular user; administrator can still remove it
                definition.Settings.Enabled = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.ExecutionTimeLimit = "PT0S";
                definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
                definition.Settings.AllowDemandStart = true;
                definition.Settings.RestartCount = 99;
                definition.Settings.RestartInterval = "PT1M";

                logonTrigger = definition.Triggers.Create(9); // TASK_TRIGGER_LOGON
                logonTrigger.UserId = WindowsIdentity.GetCurrent().Name;
                logonTrigger.Enabled = true;

                // The logon trigger covers normal starts.  A daily trigger that
                // repeats every minute supplies a bounded watchdog when Task
                // Manager or a crash terminates the guard process.
                heartbeatTrigger = definition.Triggers.Create(2); // TASK_TRIGGER_DAILY
                heartbeatTrigger.StartBoundary = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-dd'T'HH:mm:ss");
                heartbeatTrigger.DaysInterval = 1;
                heartbeatTrigger.Repetition.Interval = "PT1M";
                heartbeatTrigger.Repetition.Duration = "P1D";
                heartbeatTrigger.Repetition.StopAtDurationEnd = false;
                heartbeatTrigger.Enabled = true;

                action = definition.Actions.Create(0);
                action.Path = executablePath;
                action.Arguments = "--guard";
                action.WorkingDirectory = Path.GetDirectoryName(executablePath);

                registeredTask = folder.RegisterTaskDefinition(GuardRecoveryTaskName, definition, 6, null, null, 3, null);
            }
            finally
            {
                Release(registeredTask);
                Release(action);
                Release(heartbeatTrigger);
                Release(logonTrigger);
                Release(definition);
                Release(folder);
                Release(service);
            }
        }

        private static void RegisterDailyAlarmDefinition(
            int hour,
            int minute,
            string executablePath,
            bool highest,
            string taskName,
            string arguments,
            int offsetMinutes,
            string description)
        {
            dynamic service = null;
            dynamic folder = null;
            dynamic definition = null;
            dynamic trigger = null;
            dynamic action = null;
            dynamic registeredTask = null;
            try
            {
                Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(serviceType);
                service.Connect();
                folder = service.GetFolder("\\");
                definition = service.NewTask(0);

                definition.RegistrationInfo.Description = description;
                definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                // Interactive-token tasks continue to run in the user's session
                // while Windows is locked, which keeps MediaPlayer audio audible.
                definition.Principal.LogonType = 3;
                definition.Principal.RunLevel = highest ? 1 : 0;
                definition.Settings.Enabled = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.WakeToRun = true;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.ExecutionTimeLimit = "PT6H";
                definition.Settings.MultipleInstances = 2;
                definition.Settings.AllowDemandStart = true;
                definition.Settings.RestartCount = 3;
                definition.Settings.RestartInterval = "PT1M";
                definition.Settings.Priority = 0;

                DateTime start = GetNextAlarmBoundary(hour, minute).AddMinutes(offsetMinutes);
                trigger = definition.Triggers.Create(2); // TASK_TRIGGER_DAILY
                trigger.StartBoundary = start.ToString("yyyy-MM-dd'T'HH:mm:ss");
                trigger.DaysInterval = 1;
                trigger.Enabled = true;

                action = definition.Actions.Create(0);
                action.Path = executablePath;
                action.Arguments = arguments;
                action.WorkingDirectory = Path.GetDirectoryName(executablePath);

                registeredTask = folder.RegisterTaskDefinition(taskName, definition, 6, null, null, 3, null);
            }
            finally
            {
                Release(registeredTask);
                Release(action);
                Release(trigger);
                Release(definition);
                Release(folder);
                Release(service);
            }
        }

        private static void RegisterSystemWakeBeacon(int hour, int minute, string executablePath)
        {
            dynamic service = null;
            dynamic folder = null;
            dynamic definition = null;
            dynamic trigger = null;
            dynamic action = null;
            dynamic registeredTask = null;
            try
            {
                Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(serviceType);
                service.Connect();
                folder = service.GetFolder("\\");
                definition = service.NewTask(0);

                definition.RegistrationInfo.Description = "SYSTEM wake beacon for the Focus Harbor morning alarm.";
                definition.Principal.UserId = "SYSTEM";
                definition.Principal.LogonType = 5; // TASK_LOGON_SERVICE_ACCOUNT
                definition.Principal.RunLevel = 1;
                definition.Settings.Enabled = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.WakeToRun = true;
                definition.Settings.DisallowStartIfOnBatteries = false;
                definition.Settings.StopIfGoingOnBatteries = false;
                definition.Settings.ExecutionTimeLimit = "PT5M";
                definition.Settings.MultipleInstances = 2;
                definition.Settings.AllowDemandStart = true;
                definition.Settings.RestartCount = 3;
                definition.Settings.RestartInterval = "PT1M";
                definition.Settings.Priority = 0;

                DateTime start = GetNextAlarmBoundary(hour, minute).AddMinutes(-AlarmWakeLeadMinutes);
                trigger = definition.Triggers.Create(2);
                trigger.StartBoundary = start.ToString("yyyy-MM-dd'T'HH:mm:ss");
                trigger.DaysInterval = 1;
                trigger.Enabled = true;

                action = definition.Actions.Create(0);
                action.Path = executablePath;
                action.Arguments = "--wake-beacon";
                action.WorkingDirectory = Path.GetDirectoryName(executablePath);

                registeredTask = folder.RegisterTaskDefinition(
                    AlarmWakeBeaconTaskName,
                    definition,
                    6,
                    "SYSTEM",
                    null,
                    5,
                    null);
            }
            finally
            {
                Release(registeredTask);
                Release(action);
                Release(trigger);
                Release(definition);
                Release(folder);
                Release(service);
            }
        }

        internal static DateTime GetNextAlarmBoundary(int hour, int minute)
        {
            return GetNextAlarmBoundary(DateTime.Now, hour, minute);
        }

        internal static DateTime GetNextAlarmBoundary(DateTime now, int hour, int minute)
        {
            DateTime start = now.Date.AddHours(hour).AddMinutes(minute);
            if (start <= now)
            {
                start = start.AddDays(1);
            }
            return start;
        }

        private static void ConfigureWakeTimers()
        {
            string powerCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
            RunPowerCfg(powerCfg, "/SETACVALUEINDEX SCHEME_CURRENT SUB_SLEEP RTCWAKE 1");
            RunPowerCfg(powerCfg, "/SETDCVALUEINDEX SCHEME_CURRENT SUB_SLEEP RTCWAKE 1");
            RunPowerCfg(powerCfg, "/SETACTIVE SCHEME_CURRENT");
        }

        private static void RunPowerCfg(string executablePath, string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(executablePath, arguments);
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            using (Process process = Process.Start(startInfo))
            {
                bool exited = process.WaitForExit(15000);
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    throw new InvalidOperationException("无法配置 Windows 唤醒计时器：操作超时。");
                }
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("无法配置 Windows 唤醒计时器。退出代码：" + process.ExitCode.ToString());
                }
            }
        }

        public static void RegisterOnce(string taskName, DateTime localTime, string executablePath, string arguments, string description)
        {
            dynamic service = null;
            dynamic folder = null;
            dynamic definition = null;
            dynamic trigger = null;
            dynamic action = null;
            dynamic registeredTask = null;
            try
            {
                Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(serviceType);
                service.Connect();
                folder = service.GetFolder("\\");
                definition = service.NewTask(0);

                definition.RegistrationInfo.Description = description;
                definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
                definition.Principal.RunLevel = 0; // TASK_RUNLEVEL_LUA
                definition.Settings.Enabled = true;
                definition.Settings.StartWhenAvailable = true;
                definition.Settings.ExecutionTimeLimit = "PT10M";
                definition.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW

                trigger = definition.Triggers.Create(1); // TASK_TRIGGER_TIME
                trigger.StartBoundary = localTime.ToString("yyyy-MM-dd'T'HH:mm:ss");
                trigger.Enabled = true;

                action = definition.Actions.Create(0); // TASK_ACTION_EXEC
                action.Path = executablePath;
                action.Arguments = arguments;
                action.WorkingDirectory = System.IO.Path.GetDirectoryName(executablePath);

                registeredTask = folder.RegisterTaskDefinition(
                    taskName,
                    definition,
                    6, // TASK_CREATE_OR_UPDATE
                    null,
                    null,
                    3,
                    null);
            }
            finally
            {
                Release(registeredTask);
                Release(action);
                Release(trigger);
                Release(definition);
                Release(folder);
                Release(service);
            }
        }

        public static void DeleteTask(string taskName)
        {
            dynamic service = null;
            dynamic folder = null;
            try
            {
                Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(serviceType);
                service.Connect();
                folder = service.GetFolder("\\");
                try
                {
                    folder.DeleteTask(taskName, 0);
                }
                catch (Exception exception)
                {
                    if (!IsMissingTaskException(exception))
                    {
                        throw;
                    }
                    // Missing tasks are already in the desired state.
                }
            }
            finally
            {
                Release(folder);
                Release(service);
            }
        }

        public static bool TaskExists(string taskName)
        {
            dynamic service = null;
            dynamic folder = null;
            dynamic task = null;
            try
            {
                Type serviceType = Type.GetTypeFromProgID("Schedule.Service", true);
                service = Activator.CreateInstance(serviceType);
                service.Connect();
                folder = service.GetFolder("\\");
                try
                {
                    task = folder.GetTask(taskName);
                    return task != null;
                }
                catch (Exception exception)
                {
                    if (IsMissingTaskException(exception))
                    {
                        return false;
                    }
                    throw;
                }
            }
            finally
            {
                Release(task);
                Release(folder);
                Release(service);
            }
        }

        private static bool IsMissingTaskException(Exception exception)
        {
            // The dynamic Task Scheduler COM interop may surface ERROR_FILE_NOT_FOUND
            // as either COMException or FileNotFoundException depending on the call site.
            const int ErrorFileNotFound = unchecked((int)0x80070002);
            return exception != null &&
                   (exception.HResult == ErrorFileNotFound || exception is FileNotFoundException);
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.FinalReleaseComObject(value);
                }
                catch
                {
                }
            }
        }
    }
}
