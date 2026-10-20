using FocusHarbor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class LogicAuditHarness
{
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "codex-read")
        {
            int matches = 0;
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    string path = ProcessIdentityReader.ReadImagePath(process);
                    string package = ProcessIdentityReader.ReadPackageFullName(process.Id);
                    if (GuardService.IsCodexProcess(process.ProcessName, path, package))
                    {
                        matches++;
                        Console.WriteLine(process.ProcessName + "|" + (package ?? String.Empty) + "|" + (path ?? String.Empty));
                    }
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            Console.WriteLine("CODEX_READ_ONLY_MATCHES=" + matches);
            return matches > 0 ? 0 : 10;
        }
        if (args.Length == 2 && args[0] == "live-read")
        {
            FocusConfig live = JsonStore.LoadConfig();
            FocusStatus status = RestrictionService.GetStatus(live, args[1]);
            Assert(status.TemporaryPauseActive || !live.BrowserRestrictionEnabled || RestrictionService.BrowserPoliciesMatch(live), "live browser policies differ from saved configuration outside a temporary pause");
            if (RestrictionService.IsFocusLocked(live))
            {
                Assert(JsonStore.SecurityEquals(live, JsonStore.LoadFocusLock()), "live focus lock differs from saved configuration");
            }
            Assert(GuardService.IsGuardRunning(args[1]), "live guard process identity is invalid");
            Console.WriteLine("LIVE_STATE_OK PAUSE_ACTIVE=" + status.TemporaryPauseActive + " ORDINARY_USED=" + status.UsedToday + " ORDINARY_REMAINING=" + status.RemainingToday + " FORFEITED=" + status.DailyAllowanceForfeited);
            return 0;
        }
        Console.WriteLine("mutex");
        TestAbandonedMutexRecovery();
        Console.WriteLine("atomic");
        TestConcurrentAtomicWrites();
        Console.WriteLine("merge");
        TestDiscoveryMergeIsAdditive();
        Console.WriteLine("normalize");
        TestNormalizationFailsClosed();
        Console.WriteLine("exe-rule-migration");
        TestExecutableRuleMigration();
        Console.WriteLine("snapshot");
        TestSecurityComparisonDoesNotMutateSnapshot();
        Console.WriteLine("mode");
        TestGuardModeParsing();
        Console.WriteLine("codex-identity");
        TestCodexProcessIdentity();
        Console.WriteLine("alarm-boundary");
        TestAlarmWakeBoundary();
        Console.WriteLine("quarantine-safety");
        TestQuarantineSafetyPolicy();
        Console.WriteLine("reward-subjects");
        TestRewardSubjects();
        Console.WriteLine("LOGIC_AUDIT_OK");
        return 0;
    }

    private static void TestAlarmWakeBoundary()
    {
        DateTime beforeAlarm = new DateTime(2026, 9, 12, 8, 10, 0, DateTimeKind.Local);
        DateTime alarm = TaskSchedulerService.GetNextAlarmBoundary(beforeAlarm, 8, 26);
        Assert(alarm == new DateTime(2026, 9, 12, 8, 26, 0, DateTimeKind.Local), "same-day alarm boundary was shifted");
        Assert(alarm.AddMinutes(-TaskSchedulerService.AlarmWakeLeadMinutes) == new DateTime(2026, 9, 12, 8, 25, 0, DateTimeKind.Local), "warm-up did not start one minute early");

        DateTime afterMidnightAlarm = new DateTime(2026, 9, 12, 0, 1, 0, DateTimeKind.Local);
        DateTime nextMidnight = TaskSchedulerService.GetNextAlarmBoundary(afterMidnightAlarm, 0, 0);
        Assert(nextMidnight == new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Local), "next-day alarm boundary was not selected");
        Assert(nextMidnight.AddMinutes(-TaskSchedulerService.AlarmWakeLeadMinutes) == new DateTime(2026, 9, 12, 23, 59, 0, DateTimeKind.Local), "midnight warm-up boundary did not cross days correctly");
    }

    private static void TestQuarantineSafetyPolicy()
    {
        MethodInfo protectedLocation = typeof(AlarmService).GetMethod("EnsureNotProtectedLocation", BindingFlags.NonPublic | BindingFlags.Static);
        Assert(protectedLocation != null, "protected-location validator is missing");
        bool desktopRejected = false;
        try
        {
            protectedLocation.Invoke(null, new object[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) });
        }
        catch (TargetInvocationException exception)
        {
            desktopRejected = exception.InnerException is InvalidOperationException;
        }
        Assert(desktopRejected, "desktop was accepted as a deposit folder");

        bool cloudRejected = false;
        try
        {
            protectedLocation.Invoke(null, new object[] { @"E:\Study\OneDrive\Deposit" });
        }
        catch (TargetInvocationException exception)
        {
            cloudRejected = exception.InnerException is InvalidOperationException;
        }
        Assert(cloudRejected, "a cloud-sync path was accepted as a deposit folder");
        Assert(TaskSchedulerService.GuardRecoveryTaskName == "FocusHarbor-Guard-Recovery", "guard recovery task identity changed unexpectedly");

        FocusConfig legacy = DefaultConfigFactory.Create();
        legacy.Version = 5;
        legacy.Alarm.Enabled = true;
        legacy.Alarm.DepositFolderPolicyVersion = 0;
        MethodInfo normalize = typeof(JsonStore).GetMethod("NormalizeConfig", BindingFlags.NonPublic | BindingFlags.Static);
        normalize.Invoke(null, new object[] { legacy });
        Assert(legacy.Version == 8 && !legacy.Alarm.Enabled && legacy.Alarm.DepositFolderPolicyVersion == 0, "legacy alarm was not disabled for explicit whole-folder reconfirmation");
    }

    private static void TestRewardSubjects()
    {
        FocusConfig config = DefaultConfigFactory.Create();
        Assert(config.RewardSubjects.Select(subject => subject.Name).SequenceEqual(new[] { "科目1", "科目2", "科目3", "科目4" }), "default reward subjects are incorrect");
        Assert(RewardService.AddSubjectToConfig(config).Success, "adding a reward subject failed");
        RewardSubjectConfig added = config.RewardSubjects.Last();
        Assert(added.Id == "subject-5" && added.Name == "科目5" && config.RewardSubjectSequence == 5, "new subject sequence is incorrect");
        Assert(RewardService.RenameSubjectInConfig(config, added.Id, "物理").Success && added.Name == "物理", "renaming a subject failed");
        Assert(!RewardService.RenameSubjectInConfig(config, added.Id, "科目1").Success, "duplicate subject name was accepted");
        Assert(RewardService.DeleteSubjectFromConfig(config, added.Id).Success && config.RewardSubjects.Count == 4, "deleting a subject failed");
    }

    private static void TestAbandonedMutexRecovery()
    {
        string name = @"Local\FocusHarborAudit-" + Guid.NewGuid().ToString("N");
        Thread owner = new Thread(delegate()
        {
            Mutex abandoned = new Mutex(false, name);
            abandoned.WaitOne();
        });
        owner.Start();
        owner.Join();
        using (Mutex mutex = new Mutex(false, name))
        {
            Assert(MutexSafety.Wait(mutex, TimeSpan.FromSeconds(2)), "abandoned mutex was not recovered");
            mutex.ReleaseMutex();
        }
    }

    private static void TestConcurrentAtomicWrites()
    {
        string directory = Path.Combine(Path.GetTempPath(), "FocusHarbor-atomic-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "state.json");
        try
        {
            string[] values = Enumerable.Range(0, 48).Select(index => "value-" + index.ToString("00") + new string('x', 200)).ToArray();
            Parallel.ForEach(values, value => JsonStore.AtomicWrite(path, value, Encoding.UTF8));
            string result = File.ReadAllText(path, Encoding.UTF8);
            Assert(values.Contains(result), "atomic write produced mixed content");
            Assert(Directory.GetFiles(directory, "*.new").Length == 0, "atomic write left temporary files");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            foreach (string temporary in Directory.GetFiles(directory, "*.new", SearchOption.TopDirectoryOnly))
            {
                File.Delete(temporary);
            }
            Directory.Delete(directory, false);
        }
    }

    private static void TestDiscoveryMergeIsAdditive()
    {
        FocusConfig config = DefaultConfigFactory.Create();
        BrowserTargetConfig first = Browser("one", @"C:\Apps\one.exe");
        BrowserTargetConfig duplicate = Browser("one-copy", @"C:\Apps\one.exe");
        BrowserTargetConfig second = Browser("two", @"C:\Apps\two.exe");
        config.BrowserTargets.Add(first);
        bool changed = DiscoveryService.MergeBrowserTargets(config, new[] { duplicate, second });
        Assert(changed && config.BrowserTargets.Count == 2, "browser merge lost or duplicated targets");
    }

    private static BrowserTargetConfig Browser(string id, string path)
    {
        return new BrowserTargetConfig { Id = id, DisplayName = id, ExecutablePath = path, PolicyKind = "BlockProcess", PolicyRoot = String.Empty };
    }

    private static void TestNormalizationFailsClosed()
    {
        FocusConfig config = DefaultConfigFactory.Create();
        config.DailyPauseLimit = 99;
        config.PauseMinutes = 999;
        config.AppRules = new List<AppRuleConfig>
        {
            new AppRuleConfig
            {
                Mode = "unknown",
                TargetKind = "unknown",
                ProcessNames = new List<string> { null, " app ", "APP" },
                AllowedWindows = new List<TimeWindowConfig> { null, new TimeWindowConfig { StartMinute = -1, EndMinute = 5 }, new TimeWindowConfig { StartMinute = 60, EndMinute = 90 } }
            }
        };
        MethodInfo normalize = typeof(JsonStore).GetMethod("NormalizeConfig", BindingFlags.NonPublic | BindingFlags.Static);
        normalize.Invoke(null, new object[] { config });
        AppRuleConfig rule = config.AppRules[0];
        Assert(config.DailyPauseLimit == 3 && config.PauseMinutes == 30, "fixed pause limits were not normalized");
        Assert(rule.Mode == "AlwaysBlocked" && rule.TargetKind == "ApplicationGroup", "invalid rule did not fail closed");
        Assert(rule.ProcessNames.Count == 1 && rule.AllowedWindows.Count == 1, "invalid rule members were not normalized");
    }

    private static void TestExecutableRuleMigration()
    {
        const string executablePath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.908.4834.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        FocusConfig config = DefaultConfigFactory.Create();
        config.AppRules = new List<AppRuleConfig>
        {
            new AppRuleConfig
            {
                Id = "legacy-file-rule",
                DisplayName = "ChatGPT.exe",
                RootPath = Path.GetDirectoryName(executablePath),
                TargetKind = "File",
                TargetPath = executablePath,
                Mode = "AlwaysBlocked",
                Enabled = true,
                IsCustom = true,
                ProcessNames = new List<string>(),
                AllowedWindows = new List<TimeWindowConfig>()
            }
        };
        FocusSecuritySnapshot legacySnapshot = JsonStore.CaptureSecurity(config);
        MethodInfo normalize = typeof(JsonStore).GetMethod("NormalizeConfig", BindingFlags.NonPublic | BindingFlags.Static);
        normalize.Invoke(null, new object[] { config });
        AppRuleConfig migrated = config.AppRules.Single();
        Assert(migrated.TargetKind == "Executable", "legacy .exe file rule was not migrated to an executable rule");
        Assert(migrated.ProcessNames.Contains("ChatGPT", StringComparer.OrdinalIgnoreCase), "migrated executable rule has no process identity");
        Assert(JsonStore.SecurityEquals(config, legacySnapshot), "focus-lock comparison did not normalize a legacy .exe rule");

        AppRuleConfig created = MainWindowController.CreateCustomPathRule(executablePath);
        Assert(created.TargetKind == "Executable" && created.ProcessNames.Contains("ChatGPT"), "new .exe selections are still being created as file rules");
        AppRuleConfig document = MainWindowController.CreateCustomPathRule(@"C:\Study\notes.pdf");
        Assert(document.TargetKind == "File" && document.ProcessNames.Count == 0, "ordinary files were incorrectly converted to executable rules");
    }

    private static void TestSecurityComparisonDoesNotMutateSnapshot()
    {
        FocusConfig config = DefaultConfigFactory.Create();
        FocusSecuritySnapshot snapshot = JsonStore.CaptureSecurity(config);
        string createdAt = snapshot.CreatedAt;
        Assert(JsonStore.SecurityEquals(config, snapshot), "equal security snapshots were rejected");
        Assert(snapshot.CreatedAt == createdAt, "security comparison mutated the persisted snapshot");
    }

    private static void TestGuardModeParsing()
    {
        Assert(GuardService.HasModeArgument("\"C:\\Focus Harbor\\FocusHarbor.exe\" --guard", "--guard"), "guard mode was not recognized");
        Assert(!GuardService.HasModeArgument("\"C:\\Focus Harbor\\FocusHarbor.exe\" --alarm", "--guard"), "alarm process was mistaken for guard");
    }

    private static void TestCodexProcessIdentity()
    {
        Assert(
            GuardService.IsCodexProcess(
                "ChatGPT",
                @"C:\Program Files\WindowsApps\OpenAI.Codex_26.908.4834.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),
            "the packaged Codex ChatGPT.exe host was not recognized");
        Assert(
            GuardService.IsCodexProcess("ChatGPT", null, "OpenAI.Codex_26.908.4834.0_x64__2p2nqsd0c76g0"),
            "the Codex package identity fallback was not recognized");
        Assert(
            !GuardService.IsCodexProcess("ChatGPT", @"C:\Program Files\ChatGPT\ChatGPT.exe", "OpenAI.ChatGPT_1.0.0.0_x64__example"),
            "an unrelated ChatGPT installation was mistaken for Codex");
        string localHelper = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "runtimes",
            "codex-computer-use-swift.exe");
        Assert(
            GuardService.IsCodexProcess("codex-computer-use-swift", localHelper),
            "a Codex helper beneath the trusted local root was not recognized");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
