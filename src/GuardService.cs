using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace FocusHarbor
{
    public static class GuardService
    {
        private const string StartupValueName = "FocusHarborGuard";
        private const string LegacyStartupValueName = "CodexAppTimeGuard";
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private static DateTime lastReportWrite = DateTime.MinValue;
        private static string lastIssueSummary = String.Empty;
        private static string verifiedGuardKey = String.Empty;

        public static void RunGuardLoop(string executablePath)
        {
            RunGuardLoop(executablePath, @"Local\FocusHarborAppGuard");
        }

        internal static void RunGuardLoop(string executablePath, string guardMutexName)
        {
            using (Mutex mutex = new Mutex(false, guardMutexName))
            {
                bool lockTaken = false;
                string ownedIdentity = null;
                Dictionary<string, FileStream> fileLocks = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    lockTaken = MutexSafety.Wait(mutex, 0);
                    if (!lockTaken)
                    {
                        return;
                    }

                    JsonStore.EnsureDirectories();
                    if (File.Exists(JsonStore.GuardPidPath))
                    {
                        JsonStore.AppendLog("GUARD RECOVERY started after the previous guard identity became stale.");
                    }
                    using (Process currentProcess = Process.GetCurrentProcess())
                    {
                        ownedIdentity = currentProcess.Id.ToString(CultureInfo.InvariantCulture) + "|" + currentProcess.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                        JsonStore.AtomicWrite(JsonStore.GuardPidPath, ownedIdentity, Encoding.UTF8);
                    }
                    FocusConfig config = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                    config = RefreshDiscoveredBrowsers(config);
                    DateTime lastConfigCheck = DateTime.MinValue;
                    DateTime lastPolicyCheck = DateTime.MinValue;
                    DateTime lastDiscoveryCheck = DateTime.UtcNow;
                    DateTime lastConfigWrite = File.GetLastWriteTimeUtc(JsonStore.ConfigPath);

                    while (true)
                    {
                        try
                        {
                        try
                        {
                            AlarmService.EnsureActiveChallengeRunning(executablePath);
                        }
                        catch (Exception exception)
                        {
                            JsonStore.AppendLog("Alarm challenge supervision failed: " + exception.Message);
                        }
                        EnforceCodexPenalty();
                        if ((DateTime.UtcNow - lastConfigCheck).TotalSeconds >= 3)
                        {
                            lastConfigCheck = DateTime.UtcNow;
                            DateTime writeTime = File.GetLastWriteTimeUtc(JsonStore.ConfigPath);
                            if (writeTime != lastConfigWrite)
                            {
                                try
                                {
                                    config = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                                    lastConfigWrite = writeTime;
                                }
                                catch
                                {
                                }
                            }
                        }

                        if (!config.AppGuardEnabled && !config.BrowserRestrictionEnabled)
                        {
                            ReleaseFileLocks(fileLocks);
                            PublishReport(FileAccessGuardService.Synchronize(config, true));
                            FocusTimeService.Update(false, false);
                            Thread.Sleep(1000);
                            continue;
                        }

                        DateTimeOffset? pauseUntil = RestrictionService.ReadPauseDeadline();
                        if (pauseUntil.HasValue && pauseUntil.Value > DateTimeOffset.Now)
                        {
                            ReleaseFileLocks(fileLocks);
                            Dictionary<string, string> restoreErrors = FileAccessGuardService.Synchronize(config, true);
                            PublishReport(restoreErrors);
                            FocusTimeService.Update(false, false);
                            Thread.Sleep(750);
                            continue;
                        }

                        if (pauseUntil.HasValue && pauseUntil.Value <= DateTimeOffset.Now)
                        {
                            try
                            {
                                RestrictionService.RestoreTemporary(executablePath);
                                config = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                            }
                            catch
                            {
                            }
                        }

                        config = RestrictionService.EnforceFocusLock(config);
                        if ((DateTime.UtcNow - lastDiscoveryCheck).TotalMinutes >= 15)
                        {
                            lastDiscoveryCheck = DateTime.UtcNow;
                            config = RefreshDiscoveredBrowsers(config);
                        }
                        if (config.BrowserRestrictionEnabled && (DateTime.UtcNow - lastPolicyCheck).TotalSeconds >= 6)
                        {
                            lastPolicyCheck = DateTime.UtcNow;
                            if (!RestrictionService.BrowserPoliciesMatch(config))
                            {
                                RestrictionService.ApplyBrowserPolicies(config);
                                JsonStore.AppendLog("Repaired browser policy changes during focus mode.");
                            }
                        }
                        Enforce(config, fileLocks);
                        FocusTimeService.Update(true, false);
                        Thread.Sleep(900);
                        }
                        catch (Exception exception)
                        {
                            ReleaseFileLocks(fileLocks);
                            JsonStore.AppendLog("Guard loop recovered from an enforcement error: " + exception);
                            Thread.Sleep(1500);
                        }
                    }
                }
                finally
                {
                    ReleaseFileLocks(fileLocks);
                    try
                    {
                        // A duplicate launch owns neither the mutex nor the PID
                        // marker. Never erase the active guard's identity on exit.
                        if (lockTaken && ownedIdentity != null && File.Exists(JsonStore.GuardPidPath) &&
                            String.Equals(File.ReadAllText(JsonStore.GuardPidPath).Trim(), ownedIdentity, StringComparison.Ordinal))
                        {
                            File.Delete(JsonStore.GuardPidPath);
                        }
                    }
                    catch
                    {
                    }
                    if (lockTaken)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }

        public static void EnableGuard(string executablePath)
        {
            using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
            {
                runKey.SetValue(StartupValueName, Quote(executablePath) + " --guard", RegistryValueKind.String);
            }

            try
            {
                TaskSchedulerService.RegisterGuardRecovery(executablePath);
            }
            catch (Exception exception)
            {
                // A scheduler failure must not prevent the immediately launched
                // guard from enforcing the current configuration.
                JsonStore.AppendLog("Guard recovery task registration failed: " + exception.Message);
            }

            if (!IsGuardRunning(executablePath))
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(executablePath, "--guard");
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(startInfo);

                DateTime deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline)
                {
                    if (IsGuardRunning(executablePath))
                    {
                        break;
                    }
                    Thread.Sleep(100);
                }
            }
        }

        public static void DisableGuard(string executablePath)
        {
            Dictionary<string, string> restoreErrors = FileAccessGuardService.Synchronize(null, true);
            if (restoreErrors.Count != 0) throw new IOException(String.Join("；", restoreErrors.Values));
            using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, true))
            {
                if (runKey != null)
                {
                    DeleteValueIfPresent(runKey, StartupValueName);
                }
            }
            try
            {
                TaskSchedulerService.DeleteTask(TaskSchedulerService.GuardRecoveryTaskName);
            }
            catch (Exception exception)
            {
                JsonStore.AppendLog("Guard recovery task removal failed: " + exception.Message);
            }
            StopGuardFromPid(executablePath);
            if (IsGuardRunning(executablePath)) throw new IOException("后台守卫尚未退出，已保留进程身份记录，请重试。");
            try
            {
                if (File.Exists(JsonStore.GuardPidPath))
                {
                    File.Delete(JsonStore.GuardPidPath);
                }
            }
            catch
            {
            }
        }

        public static bool IsGuardRunning(string executablePath)
        {
            try
            {
                if (!File.Exists(JsonStore.GuardPidPath))
                {
                    return false;
                }
                int pid;
                long startedTicks;
                if (!TryReadGuardIdentity(out pid, out startedTicks))
                {
                    return false;
                }
                using (Process process = Process.GetProcessById(pid))
                {
                    return IsOwnedGuardProcess(process, executablePath, startedTicks);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsCompatibleGuardExecutable(string actualPath, string expectedPath)
        {
            if (PathsEqual(actualPath, expectedPath)) return true;
            try
            {
                string ownName = Assembly.GetExecutingAssembly().GetName().Name;
                return AssemblyName.GetAssemblyName(actualPath).Name == ownName && AssemblyName.GetAssemblyName(expectedPath).Name == ownName;
            }
            catch { return false; }
        }

        private static bool IsOwnedGuardProcess(Process process, string expectedPath, long startedTicks)
        {
            long actualTicks = process.StartTime.ToUniversalTime().Ticks;
            if (startedTicks > 0 && actualTicks != startedTicks) return false;
            string actualPath = ProcessIdentityReader.ReadImagePath(process);
            if (PathsEqual(actualPath, expectedPath))
                return startedTicks > 0 || HasModeArgument(ProcessCommandLineReader.Read(process.Id), "--guard");
            string key = process.Id + "|" + actualTicks + "|" + actualPath + "|" + expectedPath;
            if (verifiedGuardKey == key) return true;
            if (!IsCompatibleGuardExecutable(actualPath, expectedPath) || !HasModeArgument(ProcessCommandLineReader.Read(process.Id), "--guard")) return false;
            verifiedGuardKey = key;
            return true;
        }

        public static void RemoveLegacyStartupAndStopGuard()
        {
            try
            {
                using (RegistryKey runKey = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, true))
                {
                    if (runKey != null)
                    {
                        DeleteValueIfPresent(runKey, LegacyStartupValueName);
                    }
                }

                string legacyPidPath = Path.Combine(JsonStore.GuardStateDirectory, "guard.pid");
                if (!File.Exists(legacyPidPath))
                {
                    return;
                }
                int pid;
                if (!Int32.TryParse(File.ReadAllText(legacyPidPath).Trim(), out pid))
                {
                    return;
                }
                using (Process process = Process.GetProcessById(pid))
                {
                    if (!String.Equals(process.ProcessName, "powershell", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                    string commandLine = ProcessCommandLineReader.Read(pid);
                    if (commandLine != null && commandLine.IndexOf("app_time_guard.ps1", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
            }
            catch
            {
            }
        }

        private static void Enforce(FocusConfig config, IDictionary<string, FileStream> fileLocks)
        {
            List<AppRuleConfig> activeRules = config.AppGuardEnabled && config.AppRules != null
                ? config.AppRules.Where(rule => rule != null && rule.Enabled && !String.Equals(rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<AppRuleConfig>();
            List<AppRuleConfig> blockingRules = activeRules.Where(ShouldBlockNow).ToList();
            bool hasPackagedRules = blockingRules.Any(rule => String.Equals(rule.TargetKind, "PackagedApplication", StringComparison.OrdinalIgnoreCase));
            Dictionary<string, string> accessErrors = FileAccessGuardService.Synchronize(config, false);
            Dictionary<string, string> report = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppRuleConfig rule in blockingRules)
                if (!String.IsNullOrWhiteSpace(rule.TargetPath) && String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase) &&
                    !accessErrors.ContainsKey(rule.TargetPath)) report[rule.TargetPath] = "已限制新读取 · 已打开的内容需先关闭";

            List<BrowserTargetConfig> blockedBrowsers = config.BrowserRestrictionEnabled && config.BrowserTargets != null
                ? config.BrowserTargets.Where(browser => browser != null && String.Equals(browser.PolicyKind, "BlockProcess", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrWhiteSpace(browser.ExecutablePath)).ToList()
                : new List<BrowserTargetConfig>();
            Process[] processes = Process.GetProcesses();
            int ownPid;
            using (Process current = Process.GetCurrentProcess()) ownPid = current.Id;
            foreach (Process process in processes)
            {
                try
                {
                    if (process.Id == ownPid) continue;
                    // Packaged applications can reject MainModule access even
                    // when the guard and target belong to the same user.
                    string processPath = ProcessIdentityReader.ReadImagePath(process);
                    string packageFamily = hasPackagedRules ? ProcessIdentityReader.ReadPackageFamilyName(process.Id) : null;
                    if (String.IsNullOrWhiteSpace(processPath) && String.IsNullOrWhiteSpace(packageFamily))
                    {
                        continue;
                    }

                    BrowserTargetConfig blockedBrowser = blockedBrowsers.FirstOrDefault(browser => PathsEqual(processPath, browser.ExecutablePath));
                    if (blockedBrowser != null)
                    {
                        int browserPid = process.Id;
                        process.Kill();
                        JsonStore.AppendLog("BLOCK BROWSER " + blockedBrowser.DisplayName + " PID=" + browserPid.ToString(CultureInfo.InvariantCulture) + " PATH=" + processPath);
                        continue;
                    }

                    foreach (AppRuleConfig rule in blockingRules)
                    {
                        if (String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (!MatchesProcessIdentity(rule, process.ProcessName, processPath, packageFamily))
                        {
                            continue;
                        }

                        int pid = process.Id;
                        process.Kill();
                        JsonStore.AppendLog("BLOCK " + rule.DisplayName + " PID=" + pid.ToString(CultureInfo.InvariantCulture) + " PATH=" + processPath);
                        break;
                    }
                }
                catch (Exception exception)
                {
                    string path = ProcessIdentityReader.ReadImagePath(process);
                    if (!String.IsNullOrWhiteSpace(path) && blockingRules.Any(rule => PathsEqual(path, rule.TargetPath)))
                        report[path] = "进程拦截失败：" + exception.Message + "（目标可能以更高权限运行）";
                }
                finally
                {
                    process.Dispose();
                }
            }
            SynchronizeFileLocks(blockingRules, fileLocks, accessErrors, report);
            foreach (KeyValuePair<string, string> error in accessErrors)
                if (!report.ContainsKey(error.Key)) report[error.Key] = error.Value;
            PublishReport(report);
        }

        private static void EnforceCodexPenalty()
        {
            DateTimeOffset blockedUntil;
            if (!AlarmService.IsCodexBlockedNow(out blockedUntil))
            {
                return;
            }
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    // The Codex desktop package is hosted by ChatGPT.exe.  Reading
                    // MainModule can fail for packaged processes even when they
                    // belong to the current user, so use both the process image
                    // path and the Windows package identity.  The latter also
                    // survives Store version-directory changes.
                    string processPath = ProcessIdentityReader.ReadImagePath(process);
                    string packageFullName = ProcessIdentityReader.ReadPackageFullName(process.Id);
                    if (!IsCodexProcess(process.ProcessName, processPath, packageFullName))
                    {
                        continue;
                    }
                    int pid = process.Id;
                    process.Kill();
                    JsonStore.AppendLog("BLOCK CODEX PID=" + pid.ToString(CultureInfo.InvariantCulture) + " PATH=" + processPath + " UNTIL=" + blockedUntil.ToString("o", CultureInfo.InvariantCulture));
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        internal static bool IsCodexProcess(string processName, string processPath)
        {
            return IsCodexProcess(processName, processPath, null);
        }

        internal static bool IsCodexProcess(string processName, string processPath, string packageFullName)
        {
            if (IsCodexPackage(packageFullName))
            {
                return true;
            }
            if (String.IsNullOrWhiteSpace(processPath))
            {
                return false;
            }
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(processPath);
            }
            catch
            {
                return false;
            }
            if (fullPath.IndexOf(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            string localCodexRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex");
            if (!IsPathInsideRoot(fullPath, localCodexRoot))
            {
                return false;
            }
            // Everything executable beneath this vendor-owned root is part of
            // Codex (CLI host, code-mode host, computer-use runtime, and future
            // helpers).  Avoid a brittle list of helper names.
            return !String.IsNullOrWhiteSpace(processName);
        }

        internal static bool IsCodexPackage(string packageFullName)
        {
            return !String.IsNullOrWhiteSpace(packageFullName) &&
                   packageFullName.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ShouldBlockNow(AppRuleConfig rule)
        {
            return String.Equals(rule.Mode, "AlwaysBlocked", StringComparison.OrdinalIgnoreCase) ||
                   (String.Equals(rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase) && !IsWithinAllowedWindow(rule));
        }

        internal static bool MatchesProcessIdentity(AppRuleConfig rule, string processName, string processPath, string packageFamily)
        {
            if (String.Equals(rule.TargetKind, "PackagedApplication", StringComparison.OrdinalIgnoreCase))
            {
                // Match the stable package family, not the versioned WindowsApps
                // directory, and never terminate an unrelated shared host by name.
                return !String.IsNullOrWhiteSpace(rule.PackageFamilyName) &&
                    String.Equals(rule.PackageFamilyName, packageFamily, StringComparison.OrdinalIgnoreCase);
            }
            if (String.Equals(rule.TargetKind, "Executable", StringComparison.OrdinalIgnoreCase))
            {
                return PathsEqual(processPath, rule.TargetPath);
            }
            return rule.ProcessNames != null &&
                   rule.ProcessNames.Any(name => String.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) &&
                   IsPathInsideRoot(processPath, rule.RootPath);
        }

        internal static void SynchronizeFileLocks(IEnumerable<AppRuleConfig> blockingRules, IDictionary<string, FileStream> fileLocks,
            IDictionary<string, string> accessErrors, IDictionary<string, string> report)
        {
            HashSet<string> desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppRuleConfig rule in blockingRules.Where(rule => String.Equals(rule.TargetKind, "Executable", StringComparison.OrdinalIgnoreCase) ||
                (String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase) && accessErrors.ContainsKey(rule.TargetPath ?? String.Empty))))
            {
                try
                {
                    if (!String.IsNullOrWhiteSpace(rule.TargetPath) && File.Exists(rule.TargetPath))
                    {
                        desired.Add(Path.GetFullPath(rule.TargetPath));
                    }
                }
                catch (Exception exception)
                {
                    report[rule.TargetPath ?? String.Empty] = "无法锁定目标：" + exception.Message;
                }
            }

            foreach (string path in fileLocks.Keys.Where(path => !desired.Contains(path)).ToList())
            {
                try { fileLocks[path].Dispose(); } catch { }
                fileLocks.Remove(path);
            }
            foreach (string path in desired.Where(path => !fileLocks.ContainsKey(path)))
            {
                try
                {
                    fileLocks[path] = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    JsonStore.AppendLog("LOCK FILE " + path);
                }
                catch (Exception exception)
                {
                    if (!report.ContainsKey(path)) report[path] = "尚未锁定：" + exception.Message + "；请关闭占用程序后重试";
                }
            }
            foreach (string path in fileLocks.Keys)
                if (!report.ContainsKey(path)) report[path] = "已独占锁定 · 禁止新读取或启动";
            foreach (AppRuleConfig rule in blockingRules)
                if (!String.IsNullOrWhiteSpace(rule.TargetPath) && !File.Exists(rule.TargetPath) &&
                    (String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase) || String.Equals(rule.TargetKind, "Executable", StringComparison.OrdinalIgnoreCase)))
                    report[rule.TargetPath] = "目标文件不存在 · 请在解除期间检查路径";
        }

        private static void PublishReport(Dictionary<string, string> entries)
        {
            string summary = String.Join("|", entries.OrderBy(item => item.Key).Select(item => item.Key + "=" + item.Value));
            if (summary == lastIssueSummary && (DateTime.UtcNow - lastReportWrite).TotalSeconds < 3) return;
            if (summary != lastIssueSummary)
                foreach (KeyValuePair<string, string> item in entries.Where(item => item.Value.Contains("失败") || item.Value.Contains("尚未") || item.Value.Contains("未生效")))
                    JsonStore.AppendLog("GUARD ISSUE " + item.Key + " " + item.Value);
            JsonStore.Save(JsonStore.GuardReportPath, new GuardRuntimeReport { UpdatedUtc = DateTime.UtcNow.Ticks, Entries = entries });
            lastIssueSummary = summary;
            lastReportWrite = DateTime.UtcNow;
        }

        private static void ReleaseFileLocks(IDictionary<string, FileStream> fileLocks)
        {
            foreach (FileStream stream in fileLocks.Values.ToList())
            {
                try { stream.Dispose(); } catch { }
            }
            fileLocks.Clear();
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return String.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static FocusConfig RefreshDiscoveredBrowsers(FocusConfig config)
        {
            try
            {
                int discoveredCount;
                if (!DiscoveryService.RefreshBrowserTargets(config, false, out discoveredCount))
                {
                    return config;
                }
                FocusConfig latest;
                using (Mutex mutex = new Mutex(false, MutexSafety.ConfigMutexName))
                {
                    bool taken = false;
                    try
                    {
                        taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                        if (!taken)
                        {
                            throw new IOException("保存浏览器扫描结果时配置正忙。");
                        }
                        latest = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                        DiscoveryService.MergeBrowserTargets(latest, config.BrowserTargets);
                        latest.BrowserDiscoveryCompleted = true;
                        JsonStore.SaveConfig(latest);
                        if (RestrictionService.IsFocusLocked(latest))
                        {
                            JsonStore.SaveFocusLock(latest);
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
                DateTimeOffset? pauseUntil = RestrictionService.ReadPauseDeadline();
                if (latest.BrowserRestrictionEnabled && !(pauseUntil.HasValue && pauseUntil.Value > DateTimeOffset.Now))
                {
                    RestrictionService.ApplyBrowserPolicies(latest);
                }
                JsonStore.AppendLog("Background discovery added browser coverage; total=" + latest.BrowserTargets.Count.ToString(CultureInfo.InvariantCulture) + ".");
                return latest;
            }
            catch (Exception exception)
            {
                JsonStore.AppendLog("Background browser discovery failed: " + exception.Message);
                return config;
            }
        }

        private static bool IsWithinAllowedWindow(AppRuleConfig rule)
        {
            if (rule.AllowedWindows == null)
            {
                return false;
            }
            int currentMinute = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            return rule.AllowedWindows.Any(window => currentMinute >= window.StartMinute && currentMinute < window.EndMinute);
        }

        private static bool IsPathInsideRoot(string processPath, string rootPath)
        {
            try
            {
                string root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(processPath);
                return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void StopGuardFromPid(string executablePath)
        {
            try
            {
                if (!File.Exists(JsonStore.GuardPidPath))
                {
                    return;
                }
                int pid;
                long startedTicks;
                if (!TryReadGuardIdentity(out pid, out startedTicks))
                {
                    return;
                }
                using (Process process = Process.GetProcessById(pid))
                {
                    if (IsOwnedGuardProcess(process, executablePath, startedTicks))
                    {
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                }
            }
            catch
            {
            }
        }

        private static void DeleteValueIfPresent(RegistryKey key, string valueName)
        {
            if (key.GetValueNames().Any(name => String.Equals(name, valueName, StringComparison.OrdinalIgnoreCase)))
            {
                key.DeleteValue(valueName, false);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        internal static bool HasModeArgument(string commandLine, string mode)
        {
            if (String.IsNullOrWhiteSpace(commandLine) || String.IsNullOrWhiteSpace(mode))
            {
                return false;
            }
            string padded = " " + commandLine.Trim() + " ";
            return padded.IndexOf(" " + mode + " ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   padded.IndexOf(" " + mode + "\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryReadGuardIdentity(out int pid, out long startedTicks)
        {
            pid = 0;
            startedTicks = 0;
            string[] pieces = File.ReadAllText(JsonStore.GuardPidPath).Trim().Split('|');
            if (pieces.Length == 0 || !Int32.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) || pid <= 0)
            {
                return false;
            }
            if (pieces.Length > 1)
            {
                Int64.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out startedTicks);
            }
            return true;
        }
    }

    public sealed class GuardRuntimeReport
    {
        public long UpdatedUtc { get; set; }
        public Dictionary<string, string> Entries { get; set; }
    }

    internal static class ProcessCommandLineReader
    {
        public static string Read(int pid)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid.ToString(CultureInfo.InvariantCulture)))
                {
                    foreach (System.Management.ManagementObject item in searcher.Get())
                    {
                        return item["CommandLine"] as string;
                    }
                }
            }
            catch
            {
            }
            return null;
        }
    }

    internal static class ProcessIdentityReader
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInsufficientBuffer = 122;

        public static string ReadImagePath(Process process)
        {
            if (process == null)
            {
                return null;
            }
            try
            {
                return process.MainModule.FileName;
            }
            catch
            {
            }

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }
                StringBuilder path = new StringBuilder(32768);
                int capacity = path.Capacity;
                return QueryFullProcessImageName(handle, 0, path, ref capacity) ? path.ToString() : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }
        }

        public static string ReadPackageFullName(int pid)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (handle == IntPtr.Zero)
                {
                    return null;
                }
                int length = 0;
                int result = GetPackageFullName(handle, ref length, null);
                if (result != ErrorInsufficientBuffer || length <= 1)
                {
                    return null;
                }
                StringBuilder packageName = new StringBuilder(length);
                result = GetPackageFullName(handle, ref length, packageName);
                return result == 0 ? packageName.ToString() : null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    CloseHandle(handle);
                }
            }
        }

        public static string ReadPackageFamilyName(int pid)
        {
            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
                if (handle == IntPtr.Zero) return null;
                int length = 0;
                if (GetPackageFamilyName(handle, ref length, null) != ErrorInsufficientBuffer || length <= 1) return null;
                StringBuilder family = new StringBuilder(length);
                return GetPackageFamilyName(handle, ref length, family) == 0 ? family.ToString() : null;
            }
            catch { return null; }
            finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(IntPtr processHandle, int flags, StringBuilder executableName, ref int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFullName(IntPtr processHandle, ref int packageFullNameLength, StringBuilder packageFullName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackageFamilyName(IntPtr processHandle, ref int packageFamilyNameLength, StringBuilder packageFamilyName);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
