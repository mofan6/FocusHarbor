using FocusHarbor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class PauseStoreHarness
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "store-read") return ReadStoreApps();
        RedirectState();
        TestForfeiture();
        TestResume();
        TestPackagedRules();
        TestLegacyRuleDeletion();
        TestUi(args);
        Console.WriteLine("PAUSE_STORE_AUDIT_OK");
        return 0;
    }

    private static void RedirectState()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "FocusHarbor-pause-store-audit-" + Guid.NewGuid().ToString("N"));
        string[] roots = { JsonStore.StateDirectory, JsonStore.PauseStateDirectory, JsonStore.GuardStateDirectory };
        foreach (FieldInfo field in typeof(JsonStore).GetFields(BindingFlags.Public | BindingFlags.Static).Where(item => item.FieldType == typeof(string)))
        {
            string original = (string)field.GetValue(null);
            int index = Array.FindIndex(roots, root => original.Equals(root, StringComparison.OrdinalIgnoreCase) || original.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert(index >= 0, "unrecognized state path");
            field.SetValue(null, Path.Combine(temporary, "state" + index) + original.Substring(roots[index].Length));
        }
        JsonStore.EnsureDirectories();
        Console.WriteLine("ISOLATED_STATE=" + temporary);
    }

    private static void TestForfeiture()
    {
        Assert(!DailyAllowanceService.IsForfeitedToday(), "fresh account forfeited by default");
        FocusConfig config = DefaultConfigFactory.Create();
        config.AppGuardEnabled = true;
        JsonStore.SaveConfig(config);
        DailyUsageState usage = new DailyUsageState { Date = DateTime.Today.ToString("yyyy-MM-dd"), Count = 1 };
        JsonStore.Save(JsonStore.UsagePath, usage);
        string deadline = DateTimeOffset.Now.AddMinutes(22).ToString("o", CultureInfo.InvariantCulture);
        JsonStore.AtomicWrite(JsonStore.PauseUntilPath, deadline, Encoding.ASCII);
        OperationResult result = RestrictionService.ForfeitTodayAllowance();
        Assert(result.Success && DailyAllowanceService.IsForfeitedToday(), "forfeiture did not persist");
        Assert(File.ReadAllText(JsonStore.PauseUntilPath).Trim() == deadline, "forfeiture stopped an active pause");
        FocusStatus status = RestrictionService.GetStatus(config, @"C:\nonexistent\FocusHarbor.exe");
        Assert(status.DailyAllowanceForfeited && status.RemainingToday == 0 && status.UsedToday == 1, "quota status inconsistent");
        OperationResult blocked = RestrictionService.PauseAll(config, @"C:\nonexistent\FocusHarbor.exe");
        Assert(!blocked.Success && blocked.ExitCode == 5, "backend allowed a forfeited ordinary pause");
        Assert(!DailyAllowanceService.IsForfeitedOn(DateTime.Today.AddDays(1)), "valid forfeiture did not expire next day");
        // Simulate a former full-reset path clearing usage, not the independent marker.
        File.Delete(JsonStore.UsagePath);
        JsonStore.SaveConfig(DefaultConfigFactory.Create());
        Assert(DailyAllowanceService.IsForfeitedToday(), "resetting restrictions restored today's forfeited allowance");
        JsonStore.AtomicWrite(JsonStore.PauseForfeiturePath, "broken", Encoding.UTF8);
        Assert(DailyAllowanceService.IsForfeitedToday(), "damaged primary forfeiture bypassed the backup");
        Assert(RestrictionService.ForfeitTodayAllowance().Success, "repeated forfeiture is not idempotent");
        JsonStore.Save(JsonStore.UsagePath, usage);
        Console.WriteLine("FORFEITURE_PERSISTENCE_ROLLOVER_AND_BACKUP_OK");
    }

    private static void TestResume()
    {
        int restores = 0;
        byte[] usage = File.ReadAllBytes(JsonStore.UsagePath);
        OperationResult failed = RestrictionService.ResumeFocusNow("fixture", delegate { throw new IOException("fixture failure"); });
        Assert(!failed.Success && RestrictionService.ReadPauseDeadline().HasValue, "failed resume lost the pending pause");
        OperationResult resumed = RestrictionService.ResumeFocusNow("fixture", delegate(string path)
        {
            Assert(path == "fixture", "resume used the wrong runtime target");
            restores++;
            File.Delete(JsonStore.PauseUntilPath);
        });
        Assert(resumed.Success && restores == 1 && !RestrictionService.ReadPauseDeadline().HasValue, "manual resume did not end the pause");
        Assert(usage.SequenceEqual(File.ReadAllBytes(JsonStore.UsagePath)) && DailyAllowanceService.IsForfeitedToday(), "resume refunded consumed or forfeited allowance");
        Assert(RestrictionService.ResumeFocusNow("fixture", delegate { restores++; }).Success && restores == 1, "duplicate resume reapplied runtime unnecessarily");
        Console.WriteLine("MANUAL_RESUME_NO_REFUND_AND_RETRY_OK");
    }

    private static void TestPackagedRules()
    {
        const string family = "Example.App_123456789abcd";
        InstalledSoftwareEntry entry = PackagedAppService.CreateEntry("Example", family + "!App", family);
        Assert(entry != null && entry.IsPackaged && entry.LocationText.StartsWith("Microsoft Store"), "valid Store app omitted");
        Assert(PackagedAppService.CreateEntry("script", @"D:\Apps\!tool.bat", "") == null, "ordinary file misclassified as Store app");
        Assert(PackagedAppService.CreateEntry("bad", family + "!App", "Other_123456789abcd") == null, "inconsistent package identity accepted");
        AppRuleConfig rule = PackagedAppService.CreateRule(entry);
        Assert(GuardService.MatchesProcessIdentity(rule, "App", null, family), "package with denied image path was not matched");
        Assert(GuardService.MatchesProcessIdentity(rule, "App", @"C:\Program Files\WindowsApps\Example.App_2.0_x64__123456789abcd\app.exe", family), "Store update broke the package rule");
        Assert(!GuardService.MatchesProcessIdentity(rule, "App", @"C:\Other\app.exe", "Other_123456789abcd"), "different package with same EXE was matched");
        Assert(!GuardService.MatchesProcessIdentity(rule, "ApplicationFrameHost", @"C:\Windows\System32\ApplicationFrameHost.exe", null), "shared system host incorrectly matched");
        FocusConfig config = DefaultConfigFactory.Create();
        config.AppRules.Add(rule);
        JsonStore.SaveConfig(config);
        FocusConfig loaded = JsonStore.LoadConfig();
        Assert(loaded.AppRules[0].TargetKind == "PackagedApplication" && loaded.AppRules[0].PackageFamilyName == family, "Store rule lost identity on restart");
        FocusSecuritySnapshot snapshot = JsonStore.CaptureSecurity(loaded);
        FocusConfig copy = DefaultConfigFactory.Create();
        JsonStore.ApplySecurity(copy, snapshot);
        Assert(JsonStore.SecurityEquals(copy, snapshot), "Store identity lost in focus security seal");
        Assert(!GuardService.MatchesProcessIdentity(new AppRuleConfig { TargetKind = "PackagedApplication" }, "App", null, null), "empty identity matched another empty identity");
        Console.WriteLine("STORE_CLASSIFICATION_MATCHING_AND_PERSISTENCE_OK");
    }

    private static void TestUi(string[] args)
    {
        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (Size size in new[] { new Size(1080, 740), new Size(940, 640) })
        {
            Window window = MainWindowController.LoadWindow();
            FocusConfig preview = DefaultConfigFactory.Create();
            preview.AppGuardEnabled = true;
            preview.AppRules.Add(new AppRuleConfig { Id = "legacy", DisplayName = "已卸载的旧软件", TargetKind = "Executable", TargetPath = @"C:\nonexistent\old.exe", IsCustom = false, Enabled = true, Mode = "AlwaysBlocked" });
            preview.AppRules.Add(PackagedAppService.CreateRule(PackagedAppService.CreateEntry("Microsoft To Do", "Microsoft.Todos_8wekyb3d8bbwe!App", "Microsoft.Todos_8wekyb3d8bbwe")));
            MainWindowController controller = new MainWindowController(window, preview, "fixture", false);
            window.Width = size.Width;
            window.Height = size.Height;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -16000;
            window.Top = -16000;
            window.ShowActivated = false;
            window.Show();
            app.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
            window.Measure(size);
            window.Arrange(new Rect(size));
            window.UpdateLayout();
            Button forfeit = (Button)window.FindName("ForfeitAllowanceButton");
            Assert(!forfeit.IsEnabled, "forfeiture UI allows restoring forfeited quota");
            JsonStore.AtomicWrite(JsonStore.PauseUntilPath, DateTimeOffset.Now.AddMinutes(8).ToString("o"), Encoding.ASCII);
            typeof(MainWindowController).GetMethod("UpdateStatus", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(controller, null);
            window.UpdateLayout();
            Button resume = (Button)window.FindName("ResumeFocusButton");
            Assert(resume.Visibility == Visibility.Visible && resume.IsEnabled, "active pause has no resume button");
            Point position = forfeit.TranslatePoint(new Point(), window);
            Assert(position.X >= 0 && position.X + forfeit.ActualWidth <= size.Width && position.Y + forfeit.ActualHeight <= size.Height, "new overview controls clipped");
            if (args.Length == 2 && args[0] == "render")
            {
                RenderTargetBitmap image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                image.Render(window);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                string output = Path.Combine(args[1], "pause-controls-" + (int)size.Width + ".png");
                using (FileStream stream = new FileStream(output, FileMode.CreateNew)) encoder.Save(stream);
                Console.WriteLine("RENDER=" + output);
                ((Grid)window.FindName("OverviewPage")).Visibility = Visibility.Collapsed;
                Grid apps = (Grid)window.FindName("AppsPage");
                apps.Visibility = Visibility.Visible;
                apps.Opacity = 1;
                ((ListBox)window.FindName("AppRulesBox")).SelectedIndex = 0;
                ((TextBlock)window.FindName("PageTitle")).Text = "应用守卫";
                Assert(((Button)window.FindName("RemoveAppRuleButton")).IsEnabled, "selected missing legacy rule still has disabled removal button");
                window.UpdateLayout();
                RenderTargetBitmap appsImage = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                appsImage.Render(window);
                PngBitmapEncoder appsEncoder = new PngBitmapEncoder();
                appsEncoder.Frames.Add(BitmapFrame.Create(appsImage));
                string appsOutput = Path.Combine(args[1], "apps-controls-" + (int)size.Width + ".png");
                using (FileStream stream = new FileStream(appsOutput, FileMode.CreateNew)) appsEncoder.Save(stream);
                Console.WriteLine("RENDER=" + appsOutput);
            }
            File.Delete(JsonStore.PauseUntilPath);
            window.Close();
            GC.KeepAlive(controller);
        }
        app.Shutdown();
        Console.WriteLine("UI_LAYOUT_AND_CONTROL_STATES_OK");
    }

    private static void TestLegacyRuleDeletion()
    {
        AppRuleConfig missingLegacy = new AppRuleConfig
        {
            DisplayName = "Uninstalled legacy app", IsCustom = false,
            TargetKind = "Executable", TargetPath = @"C:\nonexistent\uninstalled.exe"
        };
        Assert(MainWindowController.CanRemoveAppRule(missingLegacy, false), "missing legacy rule cannot be deleted during a pause");
        Assert(!MainWindowController.CanRemoveAppRule(missingLegacy, true), "rule deletion bypasses focus lock");
        Assert(!MainWindowController.CanRemoveAppRule(null, false), "no selection incorrectly enables removal");
        missingLegacy.TargetKind = "PackagedApplication";
        Assert(MainWindowController.CanRemoveAppRule(missingLegacy, false), "uninstalled Store rule cannot be deleted");
        Console.WriteLine("LEGACY_UNINSTALLED_RULE_DELETION_OK");
    }

    private static int ReadStoreApps()
    {
        Stopwatch watch = Stopwatch.StartNew();
        List<InstalledSoftwareEntry> apps = DiscoveryService.DiscoverInstalledSoftware();
        List<InstalledSoftwareEntry> store = apps.Where(entry => entry.IsPackaged).ToList();
        int icons = 0;
        foreach (InstalledSoftwareEntry entry in store)
            if (entry.IconSource != null) icons++;
        Console.WriteLine("INSTALLED_SOFTWARE=" + apps.Count + " STORE_APPS=" + store.Count + " STORE_ICONS=" + icons + " READ_MS=" + watch.ElapsedMilliseconds);
        foreach (InstalledSoftwareEntry entry in store.Take(8)) Console.WriteLine(entry.DisplayText);
        int identities = 0;
        foreach (Process process in Process.GetProcesses())
        {
            try { if (!String.IsNullOrWhiteSpace(ProcessIdentityReader.ReadPackageFamilyName(process.Id))) identities++; }
            finally { process.Dispose(); }
        }
        Console.WriteLine("READ_ONLY_PROCESS_PACKAGE_IDENTITIES=" + identities);
        return store.Count > 0 && identities > 0 ? 0 : 10;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
