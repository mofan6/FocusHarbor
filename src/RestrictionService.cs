using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FocusHarbor
{
    public static class RestrictionService
    {
        public static readonly string[] PolicyPaths =
        {
            @"HKCU:\SOFTWARE\Policies\Google\Chrome\URLBlocklist",
            @"HKCU:\SOFTWARE\Policies\Google\Chrome\URLAllowlist",
            @"HKCU:\SOFTWARE\Policies\Microsoft\Edge\URLBlocklist",
            @"HKCU:\SOFTWARE\Policies\Microsoft\Edge\URLAllowlist",
            @"HKCU:\SOFTWARE\Policies\Vivaldi\URLBlocklist",
            @"HKCU:\SOFTWARE\Policies\Vivaldi\URLAllowlist",
            @"HKCU:\SOFTWARE\Policies\Quark\URLBlocklist",
            @"HKCU:\SOFTWARE\Policies\Quark\URLAllowlist"
        };

        private static readonly string[] BrowserRoots =
        {
            @"SOFTWARE\Policies\Google\Chrome",
            @"SOFTWARE\Policies\Microsoft\Edge",
            @"SOFTWARE\Policies\Vivaldi",
            @"SOFTWARE\Policies\Quark"
        };

        public static List<string> ReadCurrentAllowlist()
        {
            List<string> values = new List<string>();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(BrowserRoots[0] + @"\URLAllowlist", false))
            {
                if (key == null)
                {
                    return values;
                }

                string[] names = key.GetValueNames();
                Array.Sort(names, CompareRegistryValueNames);
                foreach (string name in names)
                {
                    object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value != null)
                    {
                        values.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
                    }
                }
            }
            return values;
        }

        public static bool BrowserPoliciesExist()
        {
            FocusConfig config = File.Exists(JsonStore.ConfigPath) ? JsonStore.LoadConfig() : DefaultConfigFactory.Create();
            return BrowserPoliciesExist(config);
        }

        public static bool BrowserPoliciesExist(FocusConfig config)
        {
            foreach (string root in GetChromiumRoots(config))
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(root + @"\URLBlocklist", false))
                {
                    if (key != null)
                    {
                        return true;
                    }
                }
            }
            foreach (string root in GetFirefoxRoots(config))
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(root + @"\WebsiteFilter\Block", false))
                {
                    if (key != null)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public static void ApplyBrowserPolicies(FocusConfig config)
        {
            if (!config.BrowserRestrictionEnabled)
            {
                RemoveBrowserPolicies(config);
                return;
            }

            List<string> allowlist = NormalizeAllowlist(config.BrowserAllowlist);
            foreach (string root in GetChromiumRoots(config))
            {
                using (RegistryKey browserRoot = Registry.CurrentUser.CreateSubKey(root))
                {
                    DeleteSubKeyIfPresent(browserRoot, "URLBlocklist");
                    DeleteSubKeyIfPresent(browserRoot, "URLAllowlist");

                    using (RegistryKey blockKey = browserRoot.CreateSubKey("URLBlocklist"))
                    {
                        blockKey.SetValue("1", "http://*", RegistryValueKind.String);
                        blockKey.SetValue("2", "https://*", RegistryValueKind.String);
                    }

                    using (RegistryKey allowKey = browserRoot.CreateSubKey("URLAllowlist"))
                    {
                        for (int index = 0; index < allowlist.Count; index++)
                        {
                            allowKey.SetValue((index + 1).ToString(CultureInfo.InvariantCulture), allowlist[index], RegistryValueKind.String);
                        }
                    }
                }
            }
            foreach (string root in GetFirefoxRoots(config))
            {
                using (RegistryKey firefoxRoot = Registry.CurrentUser.CreateSubKey(root))
                using (RegistryKey websiteFilter = firefoxRoot.CreateSubKey("WebsiteFilter"))
                {
                    DeleteSubKeyIfPresent(websiteFilter, "Block");
                    DeleteSubKeyIfPresent(websiteFilter, "Exceptions");
                    using (RegistryKey blockKey = websiteFilter.CreateSubKey("Block"))
                    {
                        blockKey.SetValue("1", "<all_urls>", RegistryValueKind.String);
                    }
                    using (RegistryKey exceptionsKey = websiteFilter.CreateSubKey("Exceptions"))
                    {
                        List<string> patterns = ToFirefoxPatterns(allowlist);
                        for (int index = 0; index < patterns.Count; index++)
                        {
                            exceptionsKey.SetValue((index + 1).ToString(CultureInfo.InvariantCulture), patterns[index], RegistryValueKind.String);
                        }
                    }
                }
            }
            int browserCount = config.BrowserTargets == null ? 0 : config.BrowserTargets.Count;
            JsonStore.AppendLog("Applied browser restrictions to " + browserCount.ToString(CultureInfo.InvariantCulture) + " discovered browser target(s).");
        }

        public static void RemoveBrowserPolicies()
        {
            FocusConfig config = File.Exists(JsonStore.ConfigPath) ? JsonStore.LoadConfig() : DefaultConfigFactory.Create();
            RemoveBrowserPolicies(config);
        }

        public static void RemoveBrowserPolicies(FocusConfig config)
        {
            foreach (string root in GetChromiumRoots(config))
            {
                using (RegistryKey browserRoot = Registry.CurrentUser.OpenSubKey(root, true))
                {
                    if (browserRoot == null)
                    {
                        continue;
                    }
                    DeleteSubKeyIfPresent(browserRoot, "URLBlocklist");
                    DeleteSubKeyIfPresent(browserRoot, "URLAllowlist");
                }
            }
            foreach (string root in GetFirefoxRoots(config))
            {
                using (RegistryKey firefoxRoot = Registry.CurrentUser.OpenSubKey(root, true))
                {
                    if (firefoxRoot == null)
                    {
                        continue;
                    }
                    DeleteSubKeyIfPresent(firefoxRoot, "WebsiteFilter");
                }
            }
            JsonStore.AppendLog("Removed browser restriction policy keys.");
        }

        public static OperationResult PauseAll(FocusConfig config, string executablePath)
        {
            DateTimeOffset alarmBlockUntil;
            if (AlarmService.IsTemporaryPauseBlockedNow(out alarmBlockUntil))
            {
                return OperationResult.Fail("晨间打字任务已超时，今天不能再临时解除限制。明天 00:00 后恢复额度。", 4);
            }
            try
            {
                config = EnforceFocusLock(JsonStore.LoadConfig());
            }
            catch (Exception exception)
            {
                return OperationResult.Fail("读取当前限制配置失败：" + exception.Message, 1);
            }
            if (!config.BrowserRestrictionEnabled && !config.AppGuardEnabled)
            {
                return OperationResult.Fail("当前没有启用任何限制。", 3);
            }

            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborTemporaryPause"))
            {
                bool lockTaken = false;
                try
                {
                    lockTaken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!lockTaken)
                    {
                        return OperationResult.Fail("临时解除正在被另一个进程处理，请稍后重试。", 1);
                    }

                    JsonStore.EnsureDirectories();
                    if (DailyAllowanceService.IsForfeitedToday())
                        return OperationResult.Fail("你已主动放弃今日普通解除额度，今天不能恢复；明天 00:00 自动重置。学习奖励不受影响。", 5);
                    config = EnforceFocusLock(JsonStore.LoadConfig());
                    if (!config.BrowserRestrictionEnabled && !config.AppGuardEnabled)
                    {
                        return OperationResult.Fail("当前没有启用任何限制。", 3);
                    }
                    DailyUsageState usage = LoadDailyUsage();
                    string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    int usedToday = usage.Date == today ? usage.Count : 0;
                    if (usedToday >= config.DailyPauseLimit)
                    {
                        OperationResult limit = OperationResult.Fail(
                            "今天已达到每天最多 " + config.DailyPauseLimit.ToString(CultureInfo.InvariantCulture) + " 次的限制。已有暂停不会被提前结束，明天可重新使用。",
                            2);
                        limit.UsedToday = usedToday;
                        limit.RemainingToday = 0;
                        return limit;
                    }

                    DateTimeOffset? previousDeadline = ReadPauseDeadline();
                    bool previousPauseActive = previousDeadline.HasValue && previousDeadline.Value > DateTimeOffset.Now;
                    bool snapshotAlreadyExisted = File.Exists(JsonStore.SnapshotPath);

                    if (!snapshotAlreadyExisted)
                    {
                        BrowserPolicySnapshot snapshot = CaptureBrowserSnapshot(config);
                        JsonStore.Save(JsonStore.SnapshotPath, snapshot);
                    }
                    else
                    {
                        ValidateSnapshot(JsonStore.Load<BrowserPolicySnapshot>(JsonStore.SnapshotPath));
                    }

                    int newCount = usedToday + 1;
                    bool usageReserved = false;
                    JsonStore.Save(
                        JsonStore.UsagePath,
                        new DailyUsageState
                        {
                            Date = today,
                            Count = newCount,
                            UpdatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture)
                        });
                    usageReserved = true;

                    DateTimeOffset pauseUntil = DateTimeOffset.Now.AddMinutes(config.PauseMinutes);
                    bool changesApplied = false;
                    try
                    {
                        JsonStore.AtomicWrite(JsonStore.PauseUntilPath, pauseUntil.ToString("o", CultureInfo.InvariantCulture), Encoding.ASCII);
                        changesApplied = true;
                        RemoveBrowserPolicies(config);
                        JsonStore.DeleteFocusLock();
                        TaskSchedulerService.RegisterOnce(
                            TaskSchedulerService.TemporaryRestoreTaskName,
                            pauseUntil.LocalDateTime,
                            executablePath,
                            "--restore-temp",
                            "Restore Focus Harbor restrictions after a temporary pause.");

                        FocusTimeService.Update(false, true);

                        OperationResult result = OperationResult.Ok(
                            "所有限制已暂时解除 " + config.PauseMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟。\n自动恢复时间：" + pauseUntil.LocalDateTime.ToString("HH:mm:ss") + "\n今日已使用 " + newCount.ToString(CultureInfo.InvariantCulture) + "/" + config.DailyPauseLimit.ToString(CultureInfo.InvariantCulture) + " 次。");
                        result.PauseUntil = pauseUntil;
                        result.UsedToday = newCount;
                        result.RemainingToday = Math.Max(0, config.DailyPauseLimit - newCount);
                        JsonStore.AppendLog("Temporary pause started until " + pauseUntil.ToString("o", CultureInfo.InvariantCulture) + ".");
                        return result;
                    }
                    catch
                    {
                        try
                        {
                            if (changesApplied)
                            {
                                if (previousPauseActive && previousDeadline.HasValue)
                                {
                                    JsonStore.AtomicWrite(JsonStore.PauseUntilPath, previousDeadline.Value.ToString("o", CultureInfo.InvariantCulture), Encoding.ASCII);
                                    RemoveBrowserPolicies(config);
                                    TaskSchedulerService.RegisterOnce(
                                        TaskSchedulerService.TemporaryRestoreTaskName,
                                        previousDeadline.Value.LocalDateTime,
                                        executablePath,
                                        "--restore-temp",
                                        "Restore Focus Harbor restrictions after a temporary pause.");
                                }
                                else
                                {
                                    RestoreTemporaryInternal(executablePath);
                                }
                            }
                        }
                        catch (Exception rollbackException)
                        {
                            JsonStore.AppendLog("Temporary pause runtime rollback failed: " + rollbackException.Message);
                        }
                        if (usageReserved)
                        {
                            try { JsonStore.Save(JsonStore.UsagePath, usage); }
                            catch (Exception rollbackException) { JsonStore.AppendLog("Temporary pause quota rollback failed: " + rollbackException.Message); }
                        }
                        if (!snapshotAlreadyExisted && !previousPauseActive)
                        {
                            try { SafeDeleteFile(JsonStore.SnapshotPath); }
                            catch (Exception rollbackException) { JsonStore.AppendLog("Temporary pause snapshot cleanup failed: " + rollbackException.Message); }
                        }
                        throw;
                    }
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Temporary pause failed: " + exception.Message);
                    return OperationResult.Fail("临时解除失败：" + exception.Message, 1);
                }
                finally
                {
                    if (lockTaken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public static OperationResult PauseWithReward(FocusConfig config, string executablePath, int minutes)
        {
            DateTimeOffset alarmBlockUntil;
            if (AlarmService.IsTemporaryPauseBlockedNow(out alarmBlockUntil))
            {
                return OperationResult.Fail("晨间任务超时惩罚仍在生效，今天不能使用奖励解除限制。", 4);
            }
            try
            {
                config = EnforceFocusLock(JsonStore.LoadConfig());
            }
            catch (Exception exception)
            {
                return OperationResult.Fail("读取当前限制配置失败：" + exception.Message, 1);
            }
            if (config == null || (!config.BrowserRestrictionEnabled && !config.AppGuardEnabled))
            {
                return OperationResult.Fail("当前没有启用任何限制，奖励暂不消耗。", 3);
            }
            int safeMinutes = Math.Max(1, Math.Min(10, minutes));

            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborTemporaryPause"))
            {
                bool lockTaken = false;
                try
                {
                    lockTaken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!lockTaken)
                    {
                        return OperationResult.Fail("临时解除正在被另一个进程处理，请稍后重试。", 1);
                    }

                    JsonStore.EnsureDirectories();
                    config = EnforceFocusLock(JsonStore.LoadConfig());
                    if (!config.BrowserRestrictionEnabled && !config.AppGuardEnabled)
                    {
                        return OperationResult.Fail("当前没有启用任何限制，奖励暂不消耗。", 3);
                    }
                    DateTimeOffset? previousDeadline = ReadPauseDeadline();
                    bool previousPauseActive = previousDeadline.HasValue && previousDeadline.Value > DateTimeOffset.Now;
                    bool snapshotAlreadyExisted = File.Exists(JsonStore.SnapshotPath);
                    if (!snapshotAlreadyExisted)
                    {
                        JsonStore.Save(JsonStore.SnapshotPath, CaptureBrowserSnapshot(config));
                    }
                    else
                    {
                        ValidateSnapshot(JsonStore.Load<BrowserPolicySnapshot>(JsonStore.SnapshotPath));
                    }

                    DateTimeOffset baseTime = previousPauseActive && previousDeadline.HasValue ? previousDeadline.Value : DateTimeOffset.Now;
                    DateTimeOffset pauseUntil = baseTime.AddMinutes(safeMinutes);
                    bool changesApplied = false;
                    try
                    {
                        JsonStore.AtomicWrite(JsonStore.PauseUntilPath, pauseUntil.ToString("o", CultureInfo.InvariantCulture), Encoding.ASCII);
                        changesApplied = true;
                        RemoveBrowserPolicies(config);
                        JsonStore.DeleteFocusLock();
                        TaskSchedulerService.RegisterOnce(
                            TaskSchedulerService.TemporaryRestoreTaskName,
                            pauseUntil.LocalDateTime,
                            executablePath,
                            "--restore-temp",
                            "Restore Focus Harbor restrictions after a study reward pause.");
                        FocusTimeService.Update(false, true);

                        OperationResult result = OperationResult.Ok(
                            "学习奖励已生效：增加 " + safeMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟临时解除。\n自动恢复时间：" + pauseUntil.LocalDateTime.ToString("HH:mm:ss"));
                        result.PauseUntil = pauseUntil;
                        JsonStore.AppendLog("Study reward extended temporary pause until " + pauseUntil.ToString("o", CultureInfo.InvariantCulture) + ".");
                        return result;
                    }
                    catch
                    {
                        try
                        {
                            if (changesApplied)
                            {
                                if (previousPauseActive && previousDeadline.HasValue)
                                {
                                    JsonStore.AtomicWrite(JsonStore.PauseUntilPath, previousDeadline.Value.ToString("o", CultureInfo.InvariantCulture), Encoding.ASCII);
                                    RemoveBrowserPolicies(config);
                                    TaskSchedulerService.RegisterOnce(
                                        TaskSchedulerService.TemporaryRestoreTaskName,
                                        previousDeadline.Value.LocalDateTime,
                                        executablePath,
                                        "--restore-temp",
                                        "Restore Focus Harbor restrictions after a temporary pause.");
                                }
                                else
                                {
                                    RestoreTemporaryInternal(executablePath);
                                }
                            }
                        }
                        catch (Exception rollbackException)
                        {
                            JsonStore.AppendLog("Study reward runtime rollback failed: " + rollbackException.Message);
                        }
                        if (!snapshotAlreadyExisted && !previousPauseActive)
                        {
                            try { SafeDeleteFile(JsonStore.SnapshotPath); }
                            catch (Exception rollbackException) { JsonStore.AppendLog("Study reward snapshot cleanup failed: " + rollbackException.Message); }
                        }
                        throw;
                    }
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Study reward pause failed: " + exception.Message);
                    return OperationResult.Fail("奖励解除失败：" + exception.Message, 1);
                }
                finally
                {
                    if (lockTaken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public static OperationResult RestoreTemporary(string executablePath)
        {
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborTemporaryPause"))
            {
                bool lockTaken = false;
                try
                {
                    lockTaken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!lockTaken)
                    {
                        return OperationResult.Fail("恢复任务正忙，请稍后重试。", 1);
                    }
                    RestoreTemporaryInternal(executablePath);
                    return OperationResult.Ok("临时解除已结束，原有限制已经恢复。");
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Temporary restore failed: " + exception.Message);
                    return OperationResult.Fail("恢复限制失败：" + exception.Message, 1);
                }
                finally
                {
                    if (lockTaken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public static OperationResult ResumeFocusNow(string executablePath)
        {
            return ResumeFocusNow(executablePath, RestoreTemporaryInternal);
        }

        internal static OperationResult ResumeFocusNow(string executablePath, Action<string> restore)
        {
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborTemporaryPause"))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken) return OperationResult.Fail("临时解除正在更新，请稍后重试。", 1);
                    DateTimeOffset? deadline = ReadPauseDeadline();
                    if (!deadline.HasValue) return OperationResult.Ok("当前没有临时解除，专注状态保持不变。");
                    restore(executablePath);
                    return OperationResult.Ok("已提前结束临时解除并恢复专注。已消耗的普通额度和学习奖励不返还。");
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Manual focus resume failed: " + exception);
                    return OperationResult.Fail("恢复专注失败，请重试：" + exception.Message, 1);
                }
                finally { if (taken) mutex.ReleaseMutex(); }
            }
        }

        public static OperationResult ForfeitTodayAllowance()
        {
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborTemporaryPause"))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken) return OperationResult.Fail("今日额度正在更新，请稍后重试。", 1);
                    DailyAllowanceService.ForfeitToday();
                    JsonStore.AppendLog("Today's ordinary temporary-pause allowance was voluntarily forfeited.");
                    return OperationResult.Ok("已放弃今天剩余的全部 30 分钟额度。重启或恢复无限制状态均不会返还，明天 00:00 重置。当前倒计时及学习奖励不受影响。");
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Daily allowance forfeiture failed: " + exception);
                    return OperationResult.Fail("保存今日额度状态时发生错误，请检查当前额度后重试：" + exception.Message, 1);
                }
                finally { if (taken) mutex.ReleaseMutex(); }
            }
        }

        public static FocusStatus GetStatus(FocusConfig config, string executablePath)
        {
            DailyUsageState usage = LoadDailyUsage();
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            int used = usage.Date == today ? Math.Max(0, usage.Count) : 0;
            DateTimeOffset? pauseUntil = ReadPauseDeadline();
            bool pauseActive = pauseUntil.HasValue && pauseUntil.Value > DateTimeOffset.Now;
            DateTimeOffset alarmBlockUntil;
            bool pauseBlocked = AlarmService.IsTemporaryPauseBlockedNow(out alarmBlockUntil);
            bool forfeited = DailyAllowanceService.IsForfeitedToday();

            return new FocusStatus
            {
                BrowserRestrictionsActive = BrowserPoliciesExist(config),
                AppGuardRunning = GuardService.IsGuardRunning(executablePath),
                TemporaryPauseActive = pauseActive,
                PauseUntil = pauseUntil,
                UsedToday = used,
                RemainingToday = pauseBlocked || forfeited ? 0 : Math.Max(0, config.DailyPauseLimit - used),
                DailyAllowanceForfeited = forfeited,
                AllowedUrlCount = config.BrowserAllowlist == null ? 0 : config.BrowserAllowlist.Count,
                DiscoveredBrowserCount = config.BrowserTargets == null ? 0 : config.BrowserTargets.Count,
                ConfigurationLocked = IsFocusLocked(config)
            };
        }

        public static OperationResult RestoreAll(string executablePath)
        {
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborTemporaryPause"))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken)
                    {
                        return OperationResult.Fail("限制状态正在更新，请稍后重试。", 1);
                    }
                    using (Mutex configMutex = new Mutex(false, MutexSafety.ConfigMutexName))
                    {
                        bool configTaken = false;
                        try
                        {
                            configTaken = MutexSafety.Wait(configMutex, TimeSpan.FromSeconds(15));
                            if (!configTaken)
                            {
                                return OperationResult.Fail("配置正在更新，请稍后重试。", 1);
                            }
                            TaskSchedulerService.DeleteTask(TaskSchedulerService.TemporaryRestoreTaskName);
                            FocusConfig config = JsonStore.LoadConfig();
                            RemoveBrowserPolicies(config);
                            SafeDeleteFile(JsonStore.PauseUntilPath);
                            SafeDeleteFile(JsonStore.SnapshotPath);
                            // Usage and voluntary forfeiture survive a full restore:
                            // disabling and re-enabling protection must not refill quota.

                            config.BrowserRestrictionEnabled = false;
                            config.AppGuardEnabled = false;
                            JsonStore.SaveConfig(config);
                            if ((config.Alarm != null && config.Alarm.Enabled) || AlarmService.IsChallengeActive())
                            {
                                GuardService.EnableGuard(executablePath);
                            }
                            else
                            {
                                GuardService.DisableGuard(executablePath);
                            }
                            JsonStore.DeleteFocusLock();
                            FocusTimeService.Update(false, true);
                        }
                        finally
                        {
                            if (configTaken)
                            {
                                configMutex.ReleaseMutex();
                            }
                        }
                    }

                    JsonStore.AppendLog("All restrictions were permanently restored.");
                    return OperationResult.Ok("所有浏览器和应用限制已恢复为无限制状态。");
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Full restoration failed: " + exception.Message);
                    return OperationResult.Fail("恢复全部限制失败：" + exception.Message, 1);
                }
                finally
                {
                    if (taken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public static void ApplyConfig(FocusConfig config, string executablePath)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            using (Mutex mutex = new Mutex(false, MutexSafety.ConfigMutexName))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken)
                    {
                        throw new IOException("配置正在由另一个进程更新，请稍后重试。");
                    }
                    FocusConfig persisted = JsonStore.LoadConfig();
                    FocusSecuritySnapshot persistedSecurity = JsonStore.LoadFocusLock() ?? JsonStore.CaptureSecurity(persisted);
                    // Reconcile legitimate background discovery before comparing
                    // the protected portion. Otherwise an alarm-only save from an
                    // older UI instance can look like a forbidden security edit.
                    DiscoveryService.MergeBrowserTargets(config, persisted.BrowserTargets);
                    config.BrowserDiscoveryCompleted = config.BrowserDiscoveryCompleted || persisted.BrowserDiscoveryCompleted;
                    if (IsFocusLocked(persisted) && !JsonStore.SecurityEquals(config, persistedSecurity))
                    {
                        throw new InvalidOperationException("专注状态下配置已锁定。请先使用临时解除，再修改浏览器或应用守卫规则。");
                    }

                    // Browser discovery runs in the guard process. Never let an
                    // older UI instance erase targets found after it was opened.
                    config.BrowserAllowlist = NormalizeAllowlist(config.BrowserAllowlist);
                    bool saved = false;
                    try
                    {
                        JsonStore.SaveConfig(config);
                        saved = true;
                        if (IsFocusLocked(config))
                        {
                            // Establish the immutable baseline before touching
                            // external policy/process state, closing the crash
                            // window in which no focus snapshot existed.
                            JsonStore.SaveFocusLock(config);
                        }
                        ApplyRuntimeConfiguration(config, executablePath);
                    }
                    catch (Exception exception)
                    {
                        string rollbackFailure = String.Empty;
                        if (saved)
                        {
                            try
                            {
                                JsonStore.SaveConfig(persisted);
                                ApplyRuntimeConfiguration(persisted, executablePath);
                            }
                            catch (Exception rollbackException)
                            {
                                rollbackFailure = "；自动恢复旧配置也失败：" + rollbackException.Message;
                                JsonStore.AppendLog("Configuration rollback failed: " + rollbackException);
                            }
                        }
                        throw new InvalidOperationException(exception.Message + rollbackFailure, exception);
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
        }

        private static void ApplyRuntimeConfiguration(FocusConfig config, string executablePath)
        {
            DateTimeOffset? pauseUntil = ReadPauseDeadline();
            bool pauseActive = pauseUntil.HasValue && pauseUntil.Value > DateTimeOffset.Now;
            if (!pauseActive)
            {
                ApplyBrowserPolicies(config);
            }
            else
            {
                RemoveBrowserPolicies(config);
            }

            if (config.AppGuardEnabled || config.BrowserRestrictionEnabled || (config.Alarm != null && config.Alarm.Enabled) || AlarmService.IsChallengeActive())
            {
                GuardService.EnableGuard(executablePath);
            }
            else
            {
                GuardService.DisableGuard(executablePath);
            }

            if (IsFocusLocked(config))
            {
                JsonStore.SaveFocusLock(config);
            }
            else
            {
                JsonStore.DeleteFocusLock();
            }
            FocusTimeService.Update(IsFocusLocked(config), true);
        }

        public static bool BrowserPoliciesMatch(FocusConfig config)
        {
            if (config == null || !config.BrowserRestrictionEnabled)
            {
                return !BrowserPoliciesExist(config ?? DefaultConfigFactory.Create());
            }
            List<string> allowlist = NormalizeAllowlist(config.BrowserAllowlist);
            foreach (string root in GetChromiumRoots(config))
            {
                if (!RegistryListEquals(root + @"\URLBlocklist", new[] { "http://*", "https://*" }) ||
                    !RegistryListEquals(root + @"\URLAllowlist", allowlist))
                {
                    return false;
                }
            }
            List<string> firefoxPatterns = ToFirefoxPatterns(allowlist);
            foreach (string root in GetFirefoxRoots(config))
            {
                if (!RegistryListEquals(root + @"\WebsiteFilter\Block", new[] { "<all_urls>" }) ||
                    !RegistryListEquals(root + @"\WebsiteFilter\Exceptions", firefoxPatterns))
                {
                    return false;
                }
            }
            return true;
        }

        public static bool IsFocusLocked(FocusConfig config)
        {
            DateTimeOffset? pauseUntil = ReadPauseDeadline();
            bool pauseActive = pauseUntil.HasValue && pauseUntil.Value > DateTimeOffset.Now;
            return !pauseActive && config != null && (config.BrowserRestrictionEnabled || config.AppGuardEnabled);
        }

        public static FocusConfig EnforceFocusLock(FocusConfig config)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }
            using (Mutex mutex = new Mutex(false, MutexSafety.ConfigMutexName))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken)
                    {
                        throw new IOException("专注锁正在由另一个进程更新，请稍后重试。");
                    }
                    if (!IsFocusLocked(config))
                    {
                        JsonStore.DeleteFocusLock();
                        return config;
                    }
                    FocusSecuritySnapshot snapshot = JsonStore.LoadFocusLock();
                    if (snapshot == null)
                    {
                        JsonStore.SaveFocusLock(config);
                        return config;
                    }
                    if (!JsonStore.SecurityEquals(config, snapshot))
                    {
                        JsonStore.ApplySecurity(config, snapshot);
                        JsonStore.SaveConfig(config);
                        JsonStore.AppendLog("Reverted a configuration change attempted during focus mode.");
                    }
                    return config;
                }
                finally
                {
                    if (taken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public static List<string> NormalizeAllowlist(IEnumerable<string> values)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values == null)
            {
                return result;
            }

            foreach (string rawValue in values)
            {
                string value = rawValue == null ? String.Empty : rawValue.Trim();
                if (value.Length == 0 || value.Length > 512)
                {
                    continue;
                }
                if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
                {
                    continue;
                }
                if (seen.Add(value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        public static DateTimeOffset? ReadPauseDeadline()
        {
            try
            {
                if (!File.Exists(JsonStore.PauseUntilPath))
                {
                    return null;
                }
                string text = File.ReadAllText(JsonStore.PauseUntilPath, Encoding.ASCII).Trim();
                DateTimeOffset value;
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value))
                {
                    return value;
                }
            }
            catch
            {
            }
            return null;
        }

        private static void RestoreTemporaryInternal(string executablePath)
        {
            using (Mutex mutex = new Mutex(false, MutexSafety.ConfigMutexName))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken)
                    {
                        throw new IOException("配置正在更新，暂时无法恢复限制。");
                    }
                    SafeDeleteFile(JsonStore.PauseUntilPath);
                    if (File.Exists(JsonStore.ConfigPath))
                    {
                        FocusConfig config = JsonStore.LoadConfig();
                        ApplyBrowserPolicies(config);
                        if (IsFocusLocked(config))
                        {
                            JsonStore.SaveFocusLock(config);
                        }
                        FocusTimeService.Update(IsFocusLocked(config), true);
                    }
                    else if (File.Exists(JsonStore.SnapshotPath))
                    {
                        RestoreBrowserSnapshot(JsonStore.Load<BrowserPolicySnapshot>(JsonStore.SnapshotPath));
                    }

                    TaskSchedulerService.DeleteTask(TaskSchedulerService.TemporaryRestoreTaskName);
                    SafeDeleteFile(JsonStore.SnapshotPath);
                    JsonStore.AppendLog("Temporary pause ended and restrictions were restored.");
                }
                finally
                {
                    if (taken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        private static BrowserPolicySnapshot CaptureBrowserSnapshot(FocusConfig config)
        {
            BrowserPolicySnapshot snapshot = new BrowserPolicySnapshot
            {
                Version = 1,
                CreatedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                Policies = new List<PolicyKeySnapshot>()
            };

            foreach (string displayPath in GetPolicyPaths(config))
            {
                string relativePath = ToRelativeRegistryPath(displayPath);
                PolicyKeySnapshot keySnapshot = new PolicyKeySnapshot
                {
                    Path = displayPath,
                    Exists = false,
                    Values = new List<PolicyValueSnapshot>()
                };
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(relativePath, false))
                {
                    if (key != null)
                    {
                        keySnapshot.Exists = true;
                        string[] names = key.GetValueNames();
                        Array.Sort(names, CompareRegistryValueNames);
                        foreach (string name in names)
                        {
                            keySnapshot.Values.Add(new PolicyValueSnapshot
                            {
                                Name = name,
                                Kind = key.GetValueKind(name).ToString(),
                                Value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                            });
                        }
                    }
                }
                snapshot.Policies.Add(keySnapshot);
            }
            return snapshot;
        }

        private static void RestoreBrowserSnapshot(BrowserPolicySnapshot snapshot)
        {
            ValidateSnapshot(snapshot);
            foreach (PolicyKeySnapshot record in snapshot.Policies)
            {
                string displayPath = record.Path;
                string relativePath = ToRelativeRegistryPath(displayPath);
                string parentPath = relativePath.Substring(0, relativePath.LastIndexOf('\\'));
                string childName = relativePath.Substring(relativePath.LastIndexOf('\\') + 1);
                using (RegistryKey parent = Registry.CurrentUser.CreateSubKey(parentPath))
                {
                    DeleteSubKeyIfPresent(parent, childName);
                    if (!record.Exists)
                    {
                        continue;
                    }
                    using (RegistryKey key = parent.CreateSubKey(childName))
                    {
                        foreach (PolicyValueSnapshot value in record.Values)
                        {
                            RegistryValueKind kind = (RegistryValueKind)Enum.Parse(typeof(RegistryValueKind), value.Kind, true);
                            key.SetValue(value.Name, value.Value, kind);
                        }
                    }
                }
            }
        }

        private static void ValidateSnapshot(BrowserPolicySnapshot snapshot)
        {
            if (snapshot == null || snapshot.Policies == null || snapshot.Policies.Count == 0)
            {
                throw new InvalidDataException("浏览器策略快照不完整。");
            }
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PolicyKeySnapshot record in snapshot.Policies)
            {
                string path = record == null ? String.Empty : record.Path;
                if (!path.StartsWith(@"HKCU:\SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase) || !seen.Add(path))
                {
                    throw new InvalidDataException("浏览器策略快照包含未知或重复路径。");
                }
            }
        }

        private static List<string> GetChromiumRoots(FocusConfig config)
        {
            List<string> roots = new List<string>(BrowserRoots);
            if (config != null && config.BrowserTargets != null)
            {
                roots.AddRange(config.BrowserTargets
                    .Where(item => item != null && String.Equals(item.PolicyKind, "ChromiumPolicy", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(item.PolicyRoot))
                    .Select(item => item.PolicyRoot));
            }
            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> GetFirefoxRoots(FocusConfig config)
        {
            if (config == null || config.BrowserTargets == null)
            {
                return new List<string>();
            }
            return config.BrowserTargets
                .Where(item => item != null && String.Equals(item.PolicyKind, "FirefoxPolicy", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(item.PolicyRoot))
                .Select(item => item.PolicyRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> GetPolicyPaths(FocusConfig config)
        {
            List<string> paths = new List<string>();
            foreach (string root in GetChromiumRoots(config))
            {
                paths.Add(@"HKCU:\" + root + @"\URLBlocklist");
                paths.Add(@"HKCU:\" + root + @"\URLAllowlist");
            }
            foreach (string root in GetFirefoxRoots(config))
            {
                paths.Add(@"HKCU:\" + root + @"\WebsiteFilter\Block");
                paths.Add(@"HKCU:\" + root + @"\WebsiteFilter\Exceptions");
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> ToFirefoxPatterns(IEnumerable<string> allowlist)
        {
            List<string> result = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in allowlist ?? Enumerable.Empty<string>())
            {
                string value = (raw ?? String.Empty).Trim();
                if (value.Length == 0)
                {
                    continue;
                }
                if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    AddFirefoxPattern(result, seen, value.EndsWith("*") ? value : value + "*");
                    continue;
                }
                if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    AddFirefoxPattern(result, seen, value.EndsWith("*") ? value : value.TrimEnd('/') + "/*");
                    continue;
                }
                string host = value.TrimStart('.');
                AddFirefoxPattern(result, seen, "*://" + host + "/*");
                AddFirefoxPattern(result, seen, "*://*." + host + "/*");
            }
            return result;
        }

        private static bool RegistryListEquals(string relativePath, IEnumerable<string> expectedValues)
        {
            List<string> expected = expectedValues == null ? new List<string>() : expectedValues.ToList();
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(relativePath, false))
            {
                if (key == null)
                {
                    return false;
                }
                string[] names = key.GetValueNames();
                Array.Sort(names, CompareRegistryValueNames);
                if (names.Length != expected.Count)
                {
                    return false;
                }
                for (int index = 0; index < names.Length; index++)
                {
                    if (!String.Equals(names[index], (index + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
                        !String.Equals(Convert.ToString(key.GetValue(names[index], String.Empty)), expected[index], StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private static void AddFirefoxPattern(ICollection<string> result, ISet<string> seen, string pattern)
        {
            if (seen.Add(pattern))
            {
                result.Add(pattern);
            }
        }

        private static DailyUsageState LoadDailyUsage()
        {
            try
            {
                if (File.Exists(JsonStore.UsagePath))
                {
                    DailyUsageState usage = JsonStore.Load<DailyUsageState>(JsonStore.UsagePath);
                    if (usage != null && usage.Count >= 0)
                    {
                        return usage;
                    }
                }
            }
            catch
            {
            }
            return new DailyUsageState { Date = String.Empty, Count = 0, UpdatedAt = String.Empty };
        }

        private static string ToRelativeRegistryPath(string displayPath)
        {
            const string prefix = @"HKCU:\";
            if (!displayPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Unsupported registry path: " + displayPath);
            }
            return displayPath.Substring(prefix.Length);
        }

        private static void DeleteSubKeyIfPresent(RegistryKey parent, string name)
        {
            if (parent == null)
            {
                return;
            }
            string[] names = parent.GetSubKeyNames();
            if (names.Any(item => String.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
            {
                parent.DeleteSubKeyTree(name, false);
            }
        }

        private static int CompareRegistryValueNames(string left, string right)
        {
            int leftNumber;
            int rightNumber;
            if (Int32.TryParse(left, out leftNumber) && Int32.TryParse(right, out rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }
            return StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private static void SafeDeleteFile(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
