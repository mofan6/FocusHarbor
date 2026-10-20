using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace FocusHarbor
{
    public static class JsonStore
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };

        public static readonly string StateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FocusHarbor");

        // Reuse the legacy pause state paths so an in-flight pause survives migration.
        public static readonly string PauseStateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexTemporaryRestrictionPause");

        public static readonly string GuardStateDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexAppTimeGuard");

        public static readonly string ConfigPath = Path.Combine(StateDirectory, "config.json");
        public static readonly string ConfigBackupPath = Path.Combine(StateDirectory, "config.backup.json");
        public static readonly string FocusLockPath = Path.Combine(StateDirectory, "focus-security-lock.json");
        public static readonly string FocusLockBackupPath = Path.Combine(StateDirectory, "focus-security-lock.backup.json");
        public static readonly string BrowserScanStampPath = Path.Combine(StateDirectory, "browser-scan-stamp.txt");
        public static readonly string FocusTimePath = Path.Combine(StateDirectory, "focus-time.json");
        public static readonly string FocusTimeBackupPath = Path.Combine(StateDirectory, "focus-time.backup.json");
        public static readonly string LogPath = Path.Combine(StateDirectory, "activity.log");
        public static readonly string GuardPidPath = Path.Combine(StateDirectory, "guard.pid");
        public static readonly string FileAccessPath = Path.Combine(StateDirectory, "file-access-recovery.json");
        public static readonly string FileAccessBackupPath = Path.Combine(StateDirectory, "file-access-recovery.backup.json");
        public static readonly string GuardReportPath = Path.Combine(StateDirectory, "guard-report.json");
        public static readonly string SnapshotPath = Path.Combine(PauseStateDirectory, "browser-policy-snapshot.json");
        public static readonly string UsagePath = Path.Combine(PauseStateDirectory, "daily-usage.json");
        public static readonly string UsageBackupPath = Path.Combine(PauseStateDirectory, "daily-usage.backup.json");
        public static readonly string PauseForfeiturePath = Path.Combine(PauseStateDirectory, "daily-forfeiture.json");
        public static readonly string PauseForfeitureBackupPath = Path.Combine(PauseStateDirectory, "daily-forfeiture.backup.json");
        public static readonly string PauseUntilPath = Path.Combine(GuardStateDirectory, "pause-until.txt");
        public static readonly string AlarmChallengePath = Path.Combine(StateDirectory, "alarm-challenge.json");
        public static readonly string AlarmChallengeBackupPath = Path.Combine(StateDirectory, "alarm-challenge.backup.json");
        public static readonly string AlarmPenaltyPath = Path.Combine(StateDirectory, "alarm-penalty.json");
        public static readonly string AlarmPenaltyBackupPath = Path.Combine(StateDirectory, "alarm-penalty.backup.json");
        public static readonly string QuarantineStatePath = Path.Combine(StateDirectory, "quarantine.json");
        public static readonly string QuarantineStateBackupPath = Path.Combine(StateDirectory, "quarantine.backup.json");
        public static readonly string RewardStatePath = Path.Combine(StateDirectory, "daily-rewards.json");
        public static readonly string RewardStateBackupPath = Path.Combine(StateDirectory, "daily-rewards.backup.json");

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(StateDirectory);
            Directory.CreateDirectory(PauseStateDirectory);
            Directory.CreateDirectory(GuardStateDirectory);
        }

        public static FocusConfig LoadConfig()
        {
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
                    EnsureDirectories();
                    FocusConfig config = TryLoadConfig(ConfigPath);
                    if (config != null)
                    {
                        if (!File.Exists(ConfigBackupPath))
                        {
                            try { Save(ConfigBackupPath, config); }
                            catch (Exception exception) { AppendLog("Could not create the backup configuration copy: " + exception.Message); }
                        }
                        return config;
                    }

                    config = TryLoadConfig(ConfigBackupPath);
                    if (config != null)
                    {
                        try { Save(ConfigPath, config); }
                        catch (Exception exception) { AppendLog("Could not repair the primary configuration copy: " + exception.Message); }
                        return config;
                    }

                    if (!File.Exists(ConfigPath) && !File.Exists(ConfigBackupPath))
                    {
                        FocusConfig defaults = DefaultConfigFactory.Create();
                        SaveConfig(defaults);
                        return defaults;
                    }
                    throw new InvalidDataException("主配置及备用配置均无法读取。为避免丢失限制规则，软件没有自动重置配置。");
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

        public static void SaveConfig(FocusConfig config)
        {
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
                    NormalizeConfig(config);
                    Save(ConfigPath, config);
                    Save(ConfigBackupPath, config);
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

        internal static FocusConfig CloneConfig(FocusConfig config)
        {
            return Serializer.Deserialize<FocusConfig>(Serializer.Serialize(config));
        }

        private static FocusConfig TryLoadConfig(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                FocusConfig config = Load<FocusConfig>(path);
                NormalizeConfig(config);
                return config;
            }
            catch (Exception exception)
            {
                AppendLog("Ignored an unreadable configuration copy " + Path.GetFileName(path) + ": " + exception.Message);
                return null;
            }
        }

        public static void SaveFocusLock(FocusConfig config)
        {
            FocusSecuritySnapshot snapshot = CaptureSecurity(config);
            Save(FocusLockPath, snapshot);
            Save(FocusLockBackupPath, snapshot);
        }

        public static FocusSecuritySnapshot LoadFocusLock()
        {
            FocusSecuritySnapshot snapshot = TryLoadFocusLock(FocusLockPath);
            if (snapshot != null)
            {
                if (!File.Exists(FocusLockBackupPath))
                {
                    try { Save(FocusLockBackupPath, snapshot); }
                    catch (Exception exception) { AppendLog("Could not create the backup focus lock copy: " + exception.Message); }
                }
                return snapshot;
            }
            snapshot = TryLoadFocusLock(FocusLockBackupPath);
            if (snapshot != null)
            {
                try { Save(FocusLockPath, snapshot); }
                catch (Exception exception) { AppendLog("Could not repair the primary focus lock copy: " + exception.Message); }
            }
            return snapshot;
        }

        public static void DeleteFocusLock()
        {
            if (File.Exists(FocusLockPath))
            {
                File.Delete(FocusLockPath);
            }
            if (File.Exists(FocusLockBackupPath))
            {
                File.Delete(FocusLockBackupPath);
            }
        }

        private static FocusSecuritySnapshot TryLoadFocusLock(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }
                FocusSecuritySnapshot snapshot = Load<FocusSecuritySnapshot>(path);
                NormalizeSecuritySnapshot(snapshot);
                return snapshot != null && snapshot.Version > 0 ? snapshot : null;
            }
            catch (Exception exception)
            {
                AppendLog("Ignored an unreadable focus lock copy " + Path.GetFileName(path) + ": " + exception.Message);
                return null;
            }
        }

        public static FocusSecuritySnapshot CaptureSecurity(FocusConfig config)
        {
            string json = Serializer.Serialize(new FocusSecuritySnapshot
            {
                Version = 1,
                CreatedAt = DateTimeOffset.Now.ToString("o"),
                BrowserRestrictionEnabled = config.BrowserRestrictionEnabled,
                BrowserAllowlist = config.BrowserAllowlist,
                BrowserTargets = config.BrowserTargets,
                AppGuardEnabled = config.AppGuardEnabled,
                AppRules = config.AppRules
            });
            return Serializer.Deserialize<FocusSecuritySnapshot>(json);
        }

        public static void ApplySecurity(FocusConfig config, FocusSecuritySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            FocusSecuritySnapshot copy = Serializer.Deserialize<FocusSecuritySnapshot>(Serializer.Serialize(snapshot));
            config.BrowserRestrictionEnabled = copy.BrowserRestrictionEnabled;
            config.BrowserAllowlist = copy.BrowserAllowlist ?? new List<string>();
            config.BrowserTargets = copy.BrowserTargets ?? new List<BrowserTargetConfig>();
            config.AppGuardEnabled = copy.AppGuardEnabled;
            config.AppRules = copy.AppRules ?? new List<AppRuleConfig>();
            NormalizeConfig(config);
        }

        public static bool SecurityEquals(FocusConfig config, FocusSecuritySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return false;
            }
            FocusSecuritySnapshot current = CaptureSecurity(config);
            FocusSecuritySnapshot expected = Serializer.Deserialize<FocusSecuritySnapshot>(Serializer.Serialize(snapshot));
            NormalizeSecuritySnapshot(current);
            NormalizeSecuritySnapshot(expected);
            current.CreatedAt = String.Empty;
            expected.CreatedAt = String.Empty;
            return String.Equals(Serializer.Serialize(current), Serializer.Serialize(expected), StringComparison.Ordinal);
        }

        public static T Load<T>(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            return Serializer.Deserialize<T>(json);
        }

        public static void Save<T>(string path, T value)
        {
            string json = Serializer.Serialize(value);
            AtomicWrite(path, json, new UTF8Encoding(true));
        }

        public static void SaveRedundant<T>(string primaryPath, string backupPath, T value)
        {
            Save(primaryPath, value);
            Save(backupPath, value);
        }

        public static T LoadRedundant<T>(string primaryPath, string backupPath) where T : class
        {
            T value = TryLoadState<T>(primaryPath);
            if (value != null)
            {
                if (!File.Exists(backupPath))
                {
                    try { Save(backupPath, value); }
                    catch (Exception exception) { AppendLog("Could not create state backup " + Path.GetFileName(backupPath) + ": " + exception.Message); }
                }
                return value;
            }
            value = TryLoadState<T>(backupPath);
            if (value != null)
            {
                try { Save(primaryPath, value); }
                catch (Exception exception) { AppendLog("Could not repair primary state " + Path.GetFileName(primaryPath) + ": " + exception.Message); }
            }
            return value;
        }

        private static T TryLoadState<T>(string path) where T : class
        {
            try
            {
                return File.Exists(path) ? Load<T>(path) : null;
            }
            catch (Exception exception)
            {
                AppendLog("Ignored unreadable state " + Path.GetFileName(path) + ": " + exception.Message);
                return null;
            }
        }

        public static void AtomicWrite(string path, string content, Encoding encoding)
        {
            string directory = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string fullPath = Path.GetFullPath(path);
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborAtomicWrite-" + StablePathHash(fullPath)))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken)
                    {
                        throw new IOException("状态文件正在由另一个进程写入：" + fullPath);
                    }
                    AtomicWriteCore(fullPath, content, encoding);
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

        private static void AtomicWriteCore(string path, string content, Encoding encoding)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                File.WriteAllText(temporaryPath, content, encoding);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, null, true);
                        return;
                    }
                    catch (PlatformNotSupportedException)
                    {
                    }
                    catch (IOException)
                    {
                    }

                    // Copy-overwrite leaves the previous file intact if the copy
                    // fails. Deleting it before a move could lose all state.
                    File.Copy(temporaryPath, path, true);
                    return;
                }
                try
                {
                    File.Move(temporaryPath, path);
                }
                catch (IOException)
                {
                    // Another writer may have created the target meanwhile.
                    File.Copy(temporaryPath, path, true);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
                catch
                {
                    // A stale unique temp file is harmless and can be replaced on
                    // the next successful write.
                }
            }
        }

        private static string StablePathHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in (value ?? String.Empty).ToUpperInvariant())
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public static void AppendLog(string message)
        {
            try
            {
                EnsureDirectories();
                File.AppendAllText(
                    LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch
            {
                // Logging must never break enforcement.
            }
        }

        private static void NormalizeConfig(FocusConfig config)
        {
            if (config == null)
            {
                throw new InvalidDataException("配置文件为空。\n");
            }
            if (config.BrowserAllowlist == null)
            {
                config.BrowserAllowlist = new System.Collections.Generic.List<string>();
            }
            if (config.BrowserTargets == null)
            {
                config.BrowserTargets = new List<BrowserTargetConfig>();
            }
            config.BrowserTargets = config.BrowserTargets
                .Where(IsValidBrowserTarget)
                .GroupBy(BrowserTargetIdentity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (config.AppRules == null)
            {
                config.AppRules = DefaultConfigFactory.Create().AppRules;
            }
            config.AppRules = config.AppRules.Where(item => item != null).ToList();
            foreach (AppRuleConfig rule in config.AppRules)
            {
                rule.Id = rule.Id ?? String.Empty;
                rule.DisplayName = String.IsNullOrWhiteSpace(rule.DisplayName) ? "未命名规则" : rule.DisplayName.Trim();
                rule.RootPath = rule.RootPath ?? String.Empty;
                if (!String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(rule.TargetKind, "Executable", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(rule.TargetKind, "PackagedApplication", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(rule.TargetKind, "ApplicationGroup", StringComparison.OrdinalIgnoreCase))
                {
                    rule.TargetKind = "ApplicationGroup";
                }
                rule.TargetPath = rule.TargetPath ?? String.Empty;
                rule.PackageFamilyName = (rule.PackageFamilyName ?? String.Empty).Trim();
                if (String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(Path.GetExtension(rule.TargetPath), ".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(rule.TargetPath))
                {
                    try { rule.TargetPath = GuardedPathResolver.Resolve(rule.TargetPath); }
                    catch { /* Keep a broken shortcut removable without losing its rule. */ }
                }
                // Older builds treated every selection made through "添加文件"
                // as a passive file lock, even when the selected file was an EXE.
                // Such rules are skipped by the process scanner, so migrate them
                // before focus-lock comparison and enforcement.
                if (String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase) &&
                    IsExecutableTargetPath(rule.TargetPath))
                {
                    rule.TargetKind = "Executable";
                    try
                    {
                        rule.TargetPath = Path.GetFullPath(rule.TargetPath);
                        rule.RootPath = Path.GetDirectoryName(rule.TargetPath) ?? String.Empty;
                    }
                    catch
                    {
                    }
                }
                if (!String.Equals(rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase) &&
                    !String.Equals(rule.Mode, "AlwaysBlocked", StringComparison.OrdinalIgnoreCase))
                {
                    // Unknown modes must fail closed rather than silently disabling
                    // a rule after a partial or hand-edited configuration write.
                    rule.Mode = "AlwaysBlocked";
                }
                rule.ProcessNames = (rule.ProcessNames ?? new List<string>())
                    .Where(name => !String.IsNullOrWhiteSpace(name))
                    .Select(name => name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (String.Equals(rule.TargetKind, "Executable", StringComparison.OrdinalIgnoreCase) &&
                    IsExecutableTargetPath(rule.TargetPath))
                {
                    string processName = Path.GetFileNameWithoutExtension(rule.TargetPath);
                    if (!String.IsNullOrWhiteSpace(processName) && !rule.ProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                    {
                        rule.ProcessNames.Add(processName);
                    }
                }
                rule.AllowedWindows = (rule.AllowedWindows ?? new List<TimeWindowConfig>())
                    .Where(window => window != null && window.StartMinute >= 0 && window.StartMinute < window.EndMinute && window.EndMinute <= 1440)
                    .OrderBy(window => window.StartMinute)
                    .ToList();
            }
            if (config.RewardSubjects == null)
            {
                config.RewardSubjects = DefaultConfigFactory.CreateDefaultRewardSubjects();
                config.RewardSubjectSequence = Math.Max(config.RewardSubjectSequence, 4);
            }
            config.RewardSubjects = config.RewardSubjects
                .Where(subject => subject != null && !String.IsNullOrWhiteSpace(subject.Id))
                .GroupBy(subject => subject.Id.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            int largestSubjectNumber = Math.Max(0, config.RewardSubjectSequence);
            foreach (RewardSubjectConfig subject in config.RewardSubjects)
            {
                subject.Id = subject.Id.Trim();
                subject.Name = String.IsNullOrWhiteSpace(subject.Name) ? "未命名科目" : subject.Name.Trim();
                if (subject.Name.Length > 24)
                {
                    subject.Name = subject.Name.Substring(0, 24);
                }
                int parsedNumber;
                if (subject.Id.StartsWith("subject-", StringComparison.OrdinalIgnoreCase) &&
                    Int32.TryParse(subject.Id.Substring("subject-".Length), out parsedNumber))
                {
                    largestSubjectNumber = Math.Max(largestSubjectNumber, parsedNumber);
                }
            }
            config.RewardSubjectSequence = largestSubjectNumber;
            // These are product invariants, not editable preferences. Clamping old
            // values prevents a stale v3/v4 file from restoring the former quota.
            config.PauseMinutes = 30;
            config.DailyPauseLimit = 3;
            if (config.Alarm == null)
            {
                config.Alarm = DefaultConfigFactory.Create().Alarm;
            }
            config.Alarm.Hour = Math.Max(0, Math.Min(23, config.Alarm.Hour));
            config.Alarm.Minute = Math.Max(0, Math.Min(59, config.Alarm.Minute));
            config.Alarm.MusicPath = config.Alarm.MusicPath ?? String.Empty;
            config.Alarm.ChallengeText = config.Alarm.ChallengeText ?? String.Empty;
            config.Alarm.DepositFolder = config.Alarm.DepositFolder ?? String.Empty;
            config.Alarm.ChallengeMinutes = 15;
            config.Alarm.QuarantineHours = 120;
            if (config.Version < 2)
            {
                config.Version = 2;
                config.BrowserDiscoveryCompleted = false;
            }
            if (config.Version < 3)
            {
                config.Version = 3;
            }
            if (config.Version < 4)
            {
                config.Version = 4;
            }
            if (config.Version < 5)
            {
                config.Version = 5;
            }
            if (config.Version < 6)
            {
                // The previous setting authorized moving one file, not the whole
                // folder.  Require an explicit save under the new wording before
                // the stronger reversible folder quarantine can run.
                config.Alarm.Enabled = false;
                config.Alarm.DepositFolderPolicyVersion = 0;
                config.Version = 6;
            }
            if (config.Version < 7)
            {
                // The decoy clock is opt-in.  Deserializing an older file leaves
                // the new Boolean false, so an upgrade can never cover the user's
                // real clock without an explicit 23:00-hour choice.
                config.Version = 7;
            }
            if (config.Version < 8)
            {
                // Version 7's separate taskbar overlay was removed completely.
                config.Version = 8;
            }
        }

        private static void NormalizeSecuritySnapshot(FocusSecuritySnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            FocusConfig normalized = DefaultConfigFactory.Create();
            normalized.BrowserRestrictionEnabled = snapshot.BrowserRestrictionEnabled;
            normalized.BrowserAllowlist = snapshot.BrowserAllowlist ?? new List<string>();
            normalized.BrowserTargets = snapshot.BrowserTargets ?? new List<BrowserTargetConfig>();
            normalized.AppGuardEnabled = snapshot.AppGuardEnabled;
            normalized.AppRules = snapshot.AppRules ?? new List<AppRuleConfig>();
            NormalizeConfig(normalized);
            snapshot.BrowserAllowlist = normalized.BrowserAllowlist;
            snapshot.BrowserTargets = normalized.BrowserTargets;
            snapshot.AppRules = normalized.AppRules;
        }

        internal static bool IsExecutableTargetPath(string path)
        {
            string extension = String.IsNullOrWhiteSpace(path) ? String.Empty : Path.GetExtension(path);
            return new[] { ".exe", ".com", ".scr" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsValidBrowserTarget(BrowserTargetConfig target)
        {
            if (target == null)
            {
                return false;
            }
            target.Id = target.Id ?? String.Empty;
            target.DisplayName = target.DisplayName ?? String.Empty;
            target.ExecutablePath = target.ExecutablePath ?? String.Empty;
            target.PolicyKind = target.PolicyKind ?? String.Empty;
            target.PolicyRoot = target.PolicyRoot ?? String.Empty;
            if (String.Equals(target.PolicyKind, "BlockProcess", StringComparison.OrdinalIgnoreCase))
            {
                return Path.IsPathRooted(target.ExecutablePath) && target.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            }
            bool supportedPolicy = String.Equals(target.PolicyKind, "ChromiumPolicy", StringComparison.OrdinalIgnoreCase) ||
                                   String.Equals(target.PolicyKind, "FirefoxPolicy", StringComparison.OrdinalIgnoreCase);
            return supportedPolicy && target.PolicyRoot.StartsWith(@"SOFTWARE\Policies\", StringComparison.OrdinalIgnoreCase) &&
                   target.PolicyRoot.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        private static string BrowserTargetIdentity(BrowserTargetConfig target)
        {
            return String.Equals(target.PolicyKind, "BlockProcess", StringComparison.OrdinalIgnoreCase)
                ? "path:" + target.ExecutablePath
                : "policy:" + target.PolicyKind + ":" + target.PolicyRoot;
        }
    }
}
