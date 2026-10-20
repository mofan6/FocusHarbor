using FocusHarbor;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class CloseSaveHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "live-probe")
        {
            foreach (string path in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusHarbor", "FocusHarbor.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "FocusHarbor-Portable.exe") })
            {
                Stopwatch watch = Stopwatch.StartNew();
                bool running = GuardService.IsGuardRunning(path);
                Console.WriteLine(Path.GetFileName(path) + " guard=" + running + " read_ms=" + watch.ElapsedMilliseconds);
            }
            return 0;
        }
        if (args.Length == 1 && args[0] == "live-close")
        {
            TestLiveUnchangedClose();
            return 0;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "FocusHarbor-close-audit-" + Guid.NewGuid().ToString("N"));
        RedirectState(temporary);
        try
        {
            TestDraftMerge();
            TestUnchangedPersistence();
            TestDuplicateGuardPreservesIdentity();
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            TestResponsiveClose();
            TestSaveFailureAndRetry();
            app.Shutdown();
            Console.WriteLine("CLOSE_SAVE_AUDIT_OK");
            return 0;
        }
        finally
        {
            // Only harness-owned state files; retain the empty audit directories.
            foreach (string path in Directory.GetFiles(temporary, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected link in audit state.");
                File.Delete(path);
            }
        }
    }

    private static void TestLiveUnchangedClose()
    {
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusHarbor", "FocusHarbor.exe");
        using (Mutex uiMutex = new Mutex(false, @"Local\FocusHarborUi"))
        {
            bool taken = MutexSafety.Wait(uiMutex, 0);
            Assert(taken, "close benchmark requires the normal UI to be closed");
            try
            {
                Assert(GuardService.IsGuardRunning(installed), "live guard must be running before the benchmark");
                FocusConfig config = JsonStore.LoadConfig();
                byte[] before = File.ReadAllBytes(JsonStore.ConfigPath);
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Window window = MainWindowController.LoadWindow();
                MainWindowController controller = new MainWindowController(window, config, installed, true);
                bool closed = false;
                window.Closed += delegate { closed = true; };
                window.Show();
                Stopwatch watch = Stopwatch.StartNew();
                window.Close();
                long returnMilliseconds = watch.ElapsedMilliseconds;
                PumpUntil(delegate { return !window.IsVisible; });
                long hiddenMilliseconds = watch.ElapsedMilliseconds;
                PumpUntil(delegate { return closed; });
                long completedMilliseconds = watch.ElapsedMilliseconds;
                Assert(before.SequenceEqual(File.ReadAllBytes(JsonStore.ConfigPath)), "live unchanged close modified user settings");
                Assert(GuardService.IsGuardRunning(installed), "live close stopped the guard");
                Console.WriteLine("LIVE_CLOSE_RETURN_MS=" + returnMilliseconds + " HIDDEN_MS=" + hiddenMilliseconds + " COMPLETED_MS=" + completedMilliseconds + " USER_CONFIG_UNCHANGED=True GUARD_RUNNING=True");
                GC.KeepAlive(controller);
                app.Shutdown();
            }
            finally { if (taken) uiMutex.ReleaseMutex(); }
        }
    }

    private static void RedirectState(string temporary)
    {
        FieldInfo[] fields = typeof(JsonStore).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string)).ToArray();
        Dictionary<FieldInfo, string> originals = fields.ToDictionary(field => field, field => (string)field.GetValue(null));
        string state = JsonStore.StateDirectory;
        string pause = JsonStore.PauseStateDirectory;
        string guard = JsonStore.GuardStateDirectory;
        foreach (KeyValuePair<FieldInfo, string> field in originals)
        {
            string oldRoot = field.Value.StartsWith(state, StringComparison.OrdinalIgnoreCase) ? state :
                field.Value.StartsWith(pause, StringComparison.OrdinalIgnoreCase) ? pause : guard;
            string folder = oldRoot == state ? "main" : oldRoot == pause ? "pause" : "guard";
            string replacement = Path.Combine(temporary, folder) + field.Value.Substring(oldRoot.Length);
            field.Key.SetValue(null, replacement);
        }
        JsonStore.EnsureDirectories();
    }

    private static UiCloseSaveRequest Request(FocusConfig original)
    {
        FocusConfig copy = JsonStore.CloneConfig(original);
        return new UiCloseSaveRequest { Configuration = copy, OriginalAlarm = copy.Alarm };
    }

    private static void TestDraftMerge()
    {
        FocusConfig original = DefaultConfigFactory.Create();
        UiCloseSaveRequest request = Request(original);
        FocusConfig latest = JsonStore.CloneConfig(original);
        latest.Alarm.Hour = 9;
        RewardService.AddSubjectToConfig(latest);
        latest.BrowserTargets.Add(new BrowserTargetConfig { Id = "discovered", DisplayName = "Browser", ExecutablePath = @"C:\Apps\Browser.exe", PolicyKind = "BlockProcess", PolicyRoot = String.Empty });
        FocusConfig unchanged = UiCloseSaveService.MergeDraft(request, latest, false);
        Assert(unchanged.Alarm.Hour == 9 && unchanged.RewardSubjects.Count == 5 && unchanged.BrowserTargets.Count == 1, "closing a stale UI erased background updates");
        request.SecurityEdited = true;
        request.Configuration.BrowserAllowlist.Add("draft.invalid");
        FocusConfig edited = UiCloseSaveService.MergeDraft(request, latest, false);
        Assert(edited.BrowserAllowlist.Contains("draft.invalid") && edited.BrowserTargets.Count == 1 && edited.RewardSubjects.Count == 5, "draft save lost edits or background state");
        FocusConfig locked = UiCloseSaveService.MergeDraft(request, latest, true);
        Assert(!locked.BrowserAllowlist.Contains("draft.invalid"), "close draft bypassed the focus lock");
        edited.BrowserAllowlist.Clear();
        Assert(request.Configuration.BrowserAllowlist.Count > 0 && latest.BrowserAllowlist.Count > 0, "merge shared mutable UI state");
        AlarmConfig oldAlarm = JsonStore.CloneConfig(original).Alarm;
        oldAlarm.DepositFolderPolicyVersion = 0;
        Assert(UiCloseSaveService.AlarmFieldsEqual(oldAlarm, original.Alarm), "untouched legacy alarm triggered close-save validation");
        request.PendingAlarm = JsonStore.CloneConfig(original).Alarm;
        request.PendingAlarm.Hour = 8;
        bool rejected = false;
        try { UiCloseSaveService.MergeDraft(request, latest, false); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "concurrent alarm update was overwritten");
        Console.WriteLine("DRAFT_MERGE_OK");
    }

    private static void TestUnchangedPersistence()
    {
        FocusConfig stored = DefaultConfigFactory.Create();
        stored.Alarm.DepositFolderPolicyVersion = 0;
        JsonStore.SaveConfig(stored);
        byte[] original = File.ReadAllBytes(JsonStore.ConfigPath);
        DateTime written = File.GetLastWriteTimeUtc(JsonStore.ConfigPath);
        Stopwatch watch = Stopwatch.StartNew();
        FocusConfig result = UiCloseSaveService.Save(Request(stored), @"C:\nonexistent\FocusHarbor.exe");
        Assert(written == File.GetLastWriteTimeUtc(JsonStore.ConfigPath) && original.SequenceEqual(File.ReadAllBytes(JsonStore.ConfigPath)), "unchanged close rewrote configuration");
        Assert(result.Alarm.DepositFolderPolicyVersion == 0, "unchanged close upgraded an unconfirmed alarm");
        Console.WriteLine("UNCHANGED_SAVE_MS=" + watch.ElapsedMilliseconds);
    }

    private static void TestResponsiveClose()
    {
        Window window = MainWindowController.LoadWindow();
        bool closed = false;
        int saves = 0;
        int heartbeat = 0;
        ManualResetEvent entered = new ManualResetEvent(false);
        ManualResetEvent release = new ManualResetEvent(false);
        MainWindowController controller = new MainWindowController(window, DefaultConfigFactory.Create(), @"C:\nonexistent\FocusHarbor.exe", true,
            delegate(UiCloseSaveRequest request, string path)
            {
                Interlocked.Increment(ref saves);
                Assert(!request.SecurityEdited && request.PendingAlarm == null, "unchanged UI was classified as edited");
                entered.Set();
                if (!release.WaitOne(5000)) throw new IOException("Harness release timed out.");
                return request.Configuration;
            });
        window.Closed += delegate { closed = true; };
        window.Show();
        DispatcherTimer beat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        beat.Tick += delegate { heartbeat++; };
        beat.Start();
        Stopwatch watch = Stopwatch.StartNew();
        window.Close();
        long closeMilliseconds = watch.ElapsedMilliseconds;
        Assert(closeMilliseconds < 500, "close handler blocked the dispatcher");
        PumpUntil(delegate { return !window.IsVisible && entered.WaitOne(0) && heartbeat >= 3; });
        window.Close();
        Assert(!closed && saves == 1, "second close bypassed or duplicated persistence");
        release.Set();
        PumpUntil(delegate { return closed; });
        beat.Stop();
        Console.WriteLine("CLOSE_RETURN_MS=" + closeMilliseconds + " DISPATCHER_HEARTBEATS=" + heartbeat + " SAVES=" + saves);
        entered.Dispose();
        release.Dispose();
        GC.KeepAlive(controller);
    }

    private static void TestDuplicateGuardPreservesIdentity()
    {
        string name = @"Local\FocusHarborDuplicateAudit-" + Guid.NewGuid().ToString("N");
        ManualResetEvent ready = new ManualResetEvent(false);
        ManualResetEvent release = new ManualResetEvent(false);
        Thread owner = new Thread(delegate()
        {
            using (Mutex mutex = new Mutex(false, name))
            {
                mutex.WaitOne();
                ready.Set();
                release.WaitOne();
                mutex.ReleaseMutex();
            }
        });
        owner.Start();
        Assert(ready.WaitOne(2000), "guard mutex fixture did not start");
        const string marker = "12345|67890";
        JsonStore.AtomicWrite(JsonStore.GuardPidPath, marker, System.Text.Encoding.UTF8);
        try
        {
            GuardService.RunGuardLoop(@"C:\nonexistent\FocusHarbor.exe", name);
            Assert(File.Exists(JsonStore.GuardPidPath) && File.ReadAllText(JsonStore.GuardPidPath).Trim() == marker, "duplicate guard removed the active PID marker");
        }
        finally
        {
            release.Set();
            owner.Join(2000);
            ready.Dispose();
            release.Dispose();
        }
        Console.WriteLine("DUPLICATE_GUARD_IDENTITY_OK");
    }

    private static void TestSaveFailureAndRetry()
    {
        Window window = MainWindowController.LoadWindow();
        bool closed = false;
        int attempts = 0;
        MainWindowController controller = new MainWindowController(window, DefaultConfigFactory.Create(), @"C:\nonexistent\FocusHarbor.exe", true,
            delegate(UiCloseSaveRequest request, string path)
            {
                Assert(request.SecurityEdited && request.Configuration.BrowserAllowlist.Contains("unsaved.invalid"), "retry lost the original draft");
                if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Simulated disk error");
                return request.Configuration;
            });
        ObservableCollection<string> list = (ObservableCollection<string>)typeof(MainWindowController)
            .GetField("allowlist", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(controller);
        list.Add("unsaved.invalid");
        window.Closed += delegate { closed = true; };
        window.Show();
        window.Close();
        PumpUntil(delegate { return attempts == 1 && window.IsVisible && window.IsEnabled; });
        Assert(!closed && list.Contains("unsaved.invalid"), "failed save closed the window or discarded edits");
        window.Close();
        PumpUntil(delegate { return closed; });
        Assert(attempts == 2, "save failure did not allow one retry");
        Console.WriteLine("SAVE_FAILURE_RETRY_OK");
        GC.KeepAlive(controller);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        DispatcherFrame frame = new DispatcherFrame();
        Stopwatch deadline = Stopwatch.StartNew();
        DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        bool passed = false;
        timer.Tick += delegate
        {
            if (condition()) { passed = true; timer.Stop(); frame.Continue = false; }
            else if (deadline.ElapsedMilliseconds > 8000) { timer.Stop(); frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert(passed, "dispatcher test timed out");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
