using FocusHarbor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class FeatureHarness
{
    public static int Main(string[] args)
    {
        string mode = args.Length == 0 ? String.Empty : args[0];
        if (String.Equals(mode, "scan", StringComparison.OrdinalIgnoreCase))
        {
            List<BrowserTargetConfig> browsers = DiscoveryService.DiscoverBrowsers();
            foreach (BrowserTargetConfig browser in browsers)
            {
                Console.WriteLine(browser.DisplayName + "|" + browser.PolicyKind + "|" + browser.ExecutablePath);
            }
            Console.WriteLine("COUNT=" + browsers.Count);
            return browsers.Count == 0 ? 2 : 0;
        }
        if (String.Equals(mode, "file-lock", StringComparison.OrdinalIgnoreCase))
        {
            string testPath = Path.Combine(Path.GetTempPath(), "FocusHarbor-lock-test-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(testPath, "lock test");
            Dictionary<string, FileStream> locks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
            try
            {
                FocusConfig config = new FocusConfig
                {
                    BrowserRestrictionEnabled = false,
                    AppGuardEnabled = true,
                    AppRules = new List<AppRuleConfig>
                    {
                        new AppRuleConfig
                        {
                            Id = "test-file",
                            DisplayName = "test file",
                            RootPath = Path.GetDirectoryName(testPath),
                            TargetKind = "File",
                            TargetPath = testPath,
                            Mode = "AlwaysBlocked",
                            Enabled = true,
                            IsCustom = true,
                            ProcessNames = new List<string>(),
                            AllowedWindows = new List<TimeWindowConfig>()
                        }
                    },
                    BrowserTargets = new List<BrowserTargetConfig>()
                };
                MethodInfo enforce = typeof(GuardService).GetMethod("Enforce", BindingFlags.NonPublic | BindingFlags.Static);
                enforce.Invoke(null, new object[] { config, locks });
                try
                {
                    using (FileStream stream = new FileStream(testPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                    }
                    Console.WriteLine("LOCK_FAILED");
                    return 3;
                }
                catch (IOException)
                {
                    Console.WriteLine("LOCK_OK");
                    return 0;
                }
            }
            finally
            {
                foreach (FileStream stream in locks.Values)
                {
                    stream.Dispose();
                }
                if (File.Exists(testPath))
                {
                    File.Delete(testPath);
                }
            }
        }
        if (String.Equals(mode, "focus-lock", StringComparison.OrdinalIgnoreCase))
        {
            const string marker = "focus-harbor-lock-test.invalid";
            FocusSecuritySnapshot snapshot = JsonStore.LoadFocusLock();
            if (snapshot == null)
            {
                Console.WriteLine("NO_LOCK_SNAPSHOT");
                return 4;
            }
            try
            {
                FocusConfig modified = JsonStore.LoadConfig();
                modified.BrowserAllowlist.Add(marker);
                JsonStore.SaveConfig(modified);
                DateTime deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    System.Threading.Thread.Sleep(250);
                    FocusConfig current = JsonStore.LoadConfig();
                    if (!current.BrowserAllowlist.Contains(marker))
                    {
                        Console.WriteLine("FOCUS_LOCK_OK");
                        return 0;
                    }
                }
                Console.WriteLine("FOCUS_LOCK_FAILED");
                return 5;
            }
            finally
            {
                FocusConfig current = JsonStore.LoadConfig();
                if (current.BrowserAllowlist.Contains(marker))
                {
                    JsonStore.ApplySecurity(current, snapshot);
                    JsonStore.SaveConfig(current);
                }
            }
        }
        if (String.Equals(mode, "defaults", StringComparison.OrdinalIgnoreCase))
        {
            FocusConfig defaults = DefaultConfigFactory.Create();
            string[] expectedSubjects = { "科目1", "科目2", "科目3", "科目4" };
            bool subjectsPassed = defaults.RewardSubjects != null && defaults.RewardSubjects.Select(subject => subject.Name).SequenceEqual(expectedSubjects) && defaults.RewardSubjectSequence == 4;
            bool allowlistPassed = new[] { "deepseek.com", "qianwen.com", "yuanbao.tencent.com" }.All(domain => defaults.BrowserAllowlist.Contains(domain));
            bool noAppRules = defaults.AppRules != null && defaults.AppRules.Count == 0;
            bool passed = !defaults.BrowserRestrictionEnabled && !defaults.AppGuardEnabled && noAppRules && subjectsPassed && allowlistPassed && defaults.DailyPauseLimit == 3 && defaults.Alarm != null && !defaults.Alarm.Enabled && defaults.Alarm.DepositFolderPolicyVersion == 2 && defaults.Alarm.ChallengeMinutes == 15 && defaults.Alarm.QuarantineHours == 120;
            Console.WriteLine(passed ? "DEFAULTS_OFF_OK" : "DEFAULTS_OFF_FAILED");
            return passed ? 0 : 6;
        }
        if (String.Equals(mode, "task-delete-idempotent", StringComparison.OrdinalIgnoreCase))
        {
            TaskSchedulerService.DeleteTask(TaskSchedulerService.LegacyLongTermRestoreTaskName);
            TaskSchedulerService.DeleteTask(TaskSchedulerService.LegacyLongTermRestoreTaskName);
            bool passed = !TaskSchedulerService.TaskExists(TaskSchedulerService.LegacyLongTermRestoreTaskName);
            Console.WriteLine(passed ? "TASK_DELETE_IDEMPOTENT_OK" : "TASK_DELETE_IDEMPOTENT_FAILED");
            return passed ? 0 : 7;
        }
        if (String.Equals(mode, "rule-time-parse", StringComparison.OrdinalIgnoreCase))
        {
            MethodInfo parser = typeof(MainWindowController).GetMethod("TryParseAllowedWindows", BindingFlags.NonPublic | BindingFlags.Static);
            object[] validArguments = { "11:00-11:30\n18:30-18:40", null, null };
            object[] invalidArguments = { "11:00-12:00\n11:30-13:00", null, null };
            bool validPassed = (bool)parser.Invoke(null, validArguments);
            bool invalidRejected = !(bool)parser.Invoke(null, invalidArguments);
            List<TimeWindowConfig> parsed = validArguments[1] as List<TimeWindowConfig>;
            bool passed = validPassed && invalidRejected && parsed != null && parsed.Count == 2 && parsed[0].StartMinute == 660 && parsed[1].EndMinute == 1120;
            Console.WriteLine(passed ? "RULE_TIME_PARSE_OK" : "RULE_TIME_PARSE_FAILED");
            return passed ? 0 : 8;
        }
        if (String.Equals(mode, "icons", StringComparison.OrdinalIgnoreCase))
        {
            List<InstalledSoftwareEntry> software = DiscoveryService.DiscoverInstalledSoftware();
            int checkedCount = 0;
            int iconCount = 0;
            foreach (InstalledSoftwareEntry item in software)
            {
                if (checkedCount >= 20)
                {
                    break;
                }
                checkedCount++;
                if (item.IconSource != null)
                {
                    iconCount++;
                }
            }
            bool passed = checkedCount > 0 && iconCount > 0;
            Console.WriteLine((passed ? "ICONS_OK" : "ICONS_FAILED") + " " + iconCount + "/" + checkedCount);
            return passed ? 0 : 9;
        }
        if (String.Equals(mode, "alarm-validation", StringComparison.OrdinalIgnoreCase))
        {
            bool rejected = false;
            try
            {
                AlarmService.ValidateAlarmConfig(new AlarmConfig
                {
                    Enabled = true,
                    Hour = 7,
                    Minute = 0,
                    ChallengeText = new string('早', 150),
                    MusicPath = Path.Combine(Path.GetTempPath(), "missing-alarm.mp3"),
                    DepositFolder = Path.GetTempPath(),
                    ChallengeMinutes = 15,
                    QuarantineHours = 120
                }, true);
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            AlarmConfig disabled = new AlarmConfig { Hour = 23, Minute = 59, ChallengeMinutes = 1, QuarantineHours = 1 };
            AlarmService.ValidateAlarmConfig(disabled, false);
            bool passed = rejected && disabled.ChallengeMinutes == 15 && disabled.QuarantineHours == 120 && AlarmService.NormalizeText("a\r\nb\rc") == "a\nb\nc";
            Console.WriteLine(passed ? "ALARM_VALIDATION_OK" : "ALARM_VALIDATION_FAILED");
            return passed ? 0 : 10;
        }
        if (String.Equals(mode, "alarm-edit-window", StringComparison.OrdinalIgnoreCase))
        {
            MethodInfo method = typeof(MainWindowController).GetMethod("IsAlarmEditMinute", BindingFlags.NonPublic | BindingFlags.Static);
            bool passed = (bool)method.Invoke(null, new object[] { 0 }) &&
                          (bool)method.Invoke(null, new object[] { 119 }) &&
                          !(bool)method.Invoke(null, new object[] { 120 }) &&
                          !(bool)method.Invoke(null, new object[] { 1380 }) &&
                          !(bool)method.Invoke(null, new object[] { 1439 });
            Console.WriteLine(passed ? "ALARM_EDIT_WINDOW_OK" : "ALARM_EDIT_WINDOW_FAILED");
            return passed ? 0 : 11;
        }
        if (String.Equals(mode, "guard-recovery-task", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2 || !File.Exists(args[1]))
            {
                return 64;
            }
            bool registered = false;
            try
            {
                TaskSchedulerService.RegisterGuardRecovery(args[1]);
                registered = TaskSchedulerService.TaskExists(TaskSchedulerService.GuardRecoveryTaskName);
            }
            finally
            {
                TaskSchedulerService.DeleteTask(TaskSchedulerService.GuardRecoveryTaskName);
            }
            bool removed = !TaskSchedulerService.TaskExists(TaskSchedulerService.GuardRecoveryTaskName);
            Console.WriteLine(registered && removed ? "GUARD_RECOVERY_TASK_OK" : "GUARD_RECOVERY_TASK_FAILED");
            return registered && removed ? 0 : 12;
        }
        return 1;
    }
}
