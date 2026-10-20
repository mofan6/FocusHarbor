using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FocusHarbor
{
    public static class BootstrapService
    {
        public static FocusConfig InitializeUi(string executablePath)
        {
            JsonStore.EnsureDirectories();
            FocusConfig config = JsonStore.LoadConfig();

            // Version 3 permanently removes the former 123-day restoration feature.
            try
            {
                TaskSchedulerService.DeleteTask(TaskSchedulerService.LegacyLongTermRestoreTaskName);
            }
            catch (Exception exception)
            {
                // A stale legacy-cleanup failure must not make the current app
                // unusable; it is retried on every later launch.
                JsonStore.AppendLog("Legacy task cleanup was deferred: " + exception.Message);
            }

            config = RestrictionService.EnforceFocusLock(config);
            DateTimeOffset? filePause = RestrictionService.ReadPauseDeadline();
            if (!config.AppGuardEnabled || (filePause.HasValue && filePause.Value > DateTimeOffset.Now))
                foreach (var issue in FileAccessGuardService.Synchronize(config, true))
                    JsonStore.AppendLog("File access recovery requires attention: " + issue.Key + " " + issue.Value);
            AlarmService.ApplySchedule(config, executablePath);
            AlarmService.EnsureActiveChallengeRunning(executablePath);
            AlarmService.EnsureQuarantineRecoveryTasks(executablePath);

            bool forceInitialDiscovery = !config.BrowserDiscoveryCompleted;
            int discoveredBrowserCount;
            bool discoveryChanged = DiscoveryService.RefreshBrowserTargets(config, forceInitialDiscovery, out discoveredBrowserCount);
            if (discoveryChanged)
            {
                using (System.Threading.Mutex mutex = new System.Threading.Mutex(false, MutexSafety.ConfigMutexName))
                {
                    bool taken = false;
                    try
                    {
                        taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                        if (!taken)
                        {
                            throw new IOException("保存浏览器扫描结果时配置正忙。");
                        }
                        FocusConfig latest = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                        DiscoveryService.MergeBrowserTargets(latest, config.BrowserTargets);
                        latest.BrowserDiscoveryCompleted = true;
                        config = latest;
                        JsonStore.SaveConfig(config);
                        if (RestrictionService.IsFocusLocked(config))
                        {
                            JsonStore.SaveFocusLock(config);
                        }
                    }
                    finally
                    {
                        if (taken)
                        {
                            mutex.ReleaseMutex();
                        }
                    }
                }
                JsonStore.AppendLog("Browser discovery now covers " + config.BrowserTargets.Count.ToString(CultureInfo.InvariantCulture) + " browser target(s); latest scan found " + discoveredBrowserCount.ToString(CultureInfo.InvariantCulture) + ".");

                DateTimeOffset? discoveryPauseUntil = RestrictionService.ReadPauseDeadline();
                bool discoveryPauseActive = discoveryPauseUntil.HasValue && discoveryPauseUntil.Value > DateTimeOffset.Now;
                if (!discoveryPauseActive && config.BrowserRestrictionEnabled)
                {
                    RestrictionService.ApplyBrowserPolicies(config);
                }
            }

            if (config.AppGuardEnabled || config.BrowserRestrictionEnabled || (config.Alarm != null && config.Alarm.Enabled) || AlarmService.IsChallengeActive())
            {
                GuardService.EnableGuard(executablePath);
                if (GuardService.IsGuardRunning(executablePath))
                {
                    GuardService.RemoveLegacyStartupAndStopGuard();
                }
            }

            DateTimeOffset? pauseUntil = RestrictionService.ReadPauseDeadline();
            if (pauseUntil.HasValue && pauseUntil.Value > DateTimeOffset.Now)
            {
                TaskSchedulerService.RegisterOnce(
                    TaskSchedulerService.TemporaryRestoreTaskName,
                    pauseUntil.Value.LocalDateTime,
                    executablePath,
                    "--restore-temp",
                    "Restore Focus Harbor restrictions after a temporary pause.");
            }

            JsonStore.SaveConfig(config);
            FocusTimeService.Update(RestrictionService.IsFocusLocked(config), true);
            JsonStore.AppendLog("Focus Harbor UI initialized from " + executablePath + ".");
            return config;
        }
    }
}
