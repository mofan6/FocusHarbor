using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace FocusHarbor
{
    public sealed class FocusConfig
    {
        public int Version { get; set; }
        public bool BrowserRestrictionEnabled { get; set; }
        public List<string> BrowserAllowlist { get; set; }
        public bool BrowserDiscoveryCompleted { get; set; }
        public List<BrowserTargetConfig> BrowserTargets { get; set; }
        public bool AppGuardEnabled { get; set; }
        public List<AppRuleConfig> AppRules { get; set; }
        public int PauseMinutes { get; set; }
        public int DailyPauseLimit { get; set; }
        public AlarmConfig Alarm { get; set; }
        public List<RewardSubjectConfig> RewardSubjects { get; set; }
        public int RewardSubjectSequence { get; set; }
    }

    public sealed class AlarmConfig
    {
        public bool Enabled { get; set; }
        public int Hour { get; set; }
        public int Minute { get; set; }
        public string MusicPath { get; set; }
        public string ChallengeText { get; set; }
        public string DepositFolder { get; set; }
        public int DepositFolderPolicyVersion { get; set; }
        public int ChallengeMinutes { get; set; }
        public int QuarantineHours { get; set; }
    }

    public sealed class AlarmChallengeState
    {
        public int Version { get; set; }
        public bool Active { get; set; }
        public bool PenaltyApplied { get; set; }
        public int RunnerPid { get; set; }
        public string RunnerProcessStartedAt { get; set; }
        public string StartedAt { get; set; }
        public string DeadlineAt { get; set; }
        public string CompletedAt { get; set; }
        public string PenaltyMessage { get; set; }
        public string ChallengeText { get; set; }
        public string MusicPath { get; set; }
        public string DepositFolder { get; set; }
        public int DepositFolderPolicyVersion { get; set; }
        public int QuarantineHours { get; set; }
    }

    public sealed class AlarmPenaltyState
    {
        public int Version { get; set; }
        public bool ChallengeRequired { get; set; }
        public string ChallengeStartedAt { get; set; }
        public string FailedAt { get; set; }
        public string PauseBlockedUntil { get; set; }
        public string CodexBlockedUntil { get; set; }
        public bool QuarantineAttempted { get; set; }
        public string QuarantineRecordId { get; set; }
        public string QuarantineMessage { get; set; }
    }

    public sealed class QuarantineRecord
    {
        public string Id { get; set; }
        public string ChallengeStartedAt { get; set; }
        public string QuarantineKind { get; set; }
        public string FileName { get; set; }
        public string OriginalPath { get; set; }
        public string DepositRoot { get; set; }
        public string QuarantinePath { get; set; }
        public long Length { get; set; }
        public int EntryCount { get; set; }
        public string QuarantinedAt { get; set; }
        public string RestoreAvailableAt { get; set; }
        public bool RestoreInProgress { get; set; }
        public bool Restored { get; set; }
        public string RestoredAt { get; set; }
    }

    public sealed class QuarantineState
    {
        public int Version { get; set; }
        public List<QuarantineRecord> Records { get; set; }
    }

    public sealed class AppRuleConfig
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string RootPath { get; set; }
        public string Mode { get; set; }
        public bool Enabled { get; set; }
        public string TargetKind { get; set; }
        public string TargetPath { get; set; }
        public string PackageFamilyName { get; set; }
        public bool IsCustom { get; set; }
        public List<string> ProcessNames { get; set; }
        public List<TimeWindowConfig> AllowedWindows { get; set; }
    }

    public sealed class BrowserTargetConfig
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string ExecutablePath { get; set; }
        public string PolicyKind { get; set; }
        public string PolicyRoot { get; set; }
    }

    public sealed class InstalledSoftwareEntry
    {
        public string DisplayName { get; set; }
        public string ExecutablePath { get; set; }
        public string AppUserModelId { get; set; }
        public string PackageFamilyName { get; set; }
        public bool IsPackaged { get { return !String.IsNullOrWhiteSpace(PackageFamilyName); } }
        public string LocationText { get { return IsPackaged ? "Microsoft Store · " + AppUserModelId : ExecutablePath; } }

        public string DisplayText
        {
            get { return DisplayName + "  ·  " + LocationText; }
        }

        public string Initial
        {
            get { return String.IsNullOrWhiteSpace(DisplayName) ? "·" : DisplayName.Substring(0, 1).ToUpperInvariant(); }
        }

        public ImageSource IconSource
        {
            get { return IsPackaged ? IconService.GetPackagedAppIcon(AppUserModelId) : IconService.GetFileIcon(ExecutablePath); }
        }
    }

    public sealed class FocusSecuritySnapshot
    {
        public int Version { get; set; }
        public string CreatedAt { get; set; }
        public bool BrowserRestrictionEnabled { get; set; }
        public List<string> BrowserAllowlist { get; set; }
        public List<BrowserTargetConfig> BrowserTargets { get; set; }
        public bool AppGuardEnabled { get; set; }
        public List<AppRuleConfig> AppRules { get; set; }
    }

    public sealed class TimeWindowConfig
    {
        public int StartMinute { get; set; }
        public int EndMinute { get; set; }
        public string Label { get; set; }
    }

    public sealed class DailyUsageState
    {
        public string Date { get; set; }
        public int Count { get; set; }
        public string UpdatedAt { get; set; }
    }

    public sealed class DailyPauseForfeitureState
    {
        public string Date { get; set; }
        public string ForfeitedAt { get; set; }
    }

    public sealed class RewardClaim
    {
        public string SubjectId { get; set; }
        public string SubjectName { get; set; }
        public string Statement { get; set; }
        public string ClaimedAt { get; set; }
        public bool Used { get; set; }
        public string UsedAt { get; set; }
    }

    public sealed class RewardDailyState
    {
        public int Version { get; set; }
        public string Date { get; set; }
        public List<RewardClaim> Claims { get; set; }
    }

    public sealed class RewardSubjectConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    public sealed class FocusTimeState
    {
        public int Version { get; set; }
        public long TotalSeconds { get; set; }
        public string LastAccountedAt { get; set; }
        public bool WasFocusing { get; set; }
    }

    public sealed class BrowserPolicySnapshot
    {
        public int Version { get; set; }
        public string CreatedAt { get; set; }
        public List<PolicyKeySnapshot> Policies { get; set; }
    }

    public sealed class PolicyKeySnapshot
    {
        public string Path { get; set; }
        public bool Exists { get; set; }
        public List<PolicyValueSnapshot> Values { get; set; }
    }

    public sealed class PolicyValueSnapshot
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public object Value { get; set; }
    }

    public sealed class OperationResult
    {
        public bool Success { get; set; }
        public int ExitCode { get; set; }
        public string Message { get; set; }
        public DateTimeOffset? PauseUntil { get; set; }
        public int UsedToday { get; set; }
        public int RemainingToday { get; set; }

        public static OperationResult Ok(string message)
        {
            return new OperationResult { Success = true, ExitCode = 0, Message = message };
        }

        public static OperationResult Fail(string message, int exitCode)
        {
            return new OperationResult { Success = false, ExitCode = exitCode, Message = message };
        }
    }

    public sealed class FocusStatus
    {
        public bool BrowserRestrictionsActive { get; set; }
        public bool AppGuardRunning { get; set; }
        public bool TemporaryPauseActive { get; set; }
        public DateTimeOffset? PauseUntil { get; set; }
        public int UsedToday { get; set; }
        public int RemainingToday { get; set; }
        public bool DailyAllowanceForfeited { get; set; }
        public int AllowedUrlCount { get; set; }
        public int DiscoveredBrowserCount { get; set; }
        public bool ConfigurationLocked { get; set; }
    }

    public static class DefaultConfigFactory
    {
        public static FocusConfig Create()
        {
            FocusConfig config = new FocusConfig();
            config.Version = 8;
            config.BrowserRestrictionEnabled = false;
            config.BrowserAllowlist = new List<string>
            {
                "chatgpt.com",
                "chat.openai.com",
                ".auth.openai.com",
                ".auth0.openai.com",
                "oaistatic.com",
                "oaiusercontent.com",
                ".challenges.cloudflare.com",
                ".gemini.google.com",
                ".accounts.google.com",
                ".apis.google.com",
                "gstatic.com",
                "googleusercontent.com",
                "https://www.google.com/recaptcha/",
                "http://127.0.0.1:3080",
                "google.com",
                "google.cn",
                "googleapis.com",
                "office.chaoxing.com",
                "cxlowcode.com",
                "passport2.chaoxing.com",
                "file:///*",
                "pantum.cn",
                "deepseek.com",
                "chat.deepseek.com",
                "qianwen.com",
                "yuanbao.tencent.com"
            };
            config.BrowserDiscoveryCompleted = false;
            config.BrowserTargets = new List<BrowserTargetConfig>();
            config.AppGuardEnabled = false;
            config.PauseMinutes = 30;
            config.DailyPauseLimit = 3;
            config.Alarm = new AlarmConfig
            {
                Enabled = false,
                Hour = 7,
                Minute = 0,
                MusicPath = String.Empty,
                ChallengeText = String.Empty,
                DepositFolder = String.Empty,
                DepositFolderPolicyVersion = 2,
                ChallengeMinutes = 15,
                QuarantineHours = 120
            };
            config.RewardSubjects = CreateDefaultRewardSubjects();
            config.RewardSubjectSequence = 4;
            // A distributable build must never inherit app-specific assumptions
            // from the developer's machine. Users add only the targets they want.
            config.AppRules = new List<AppRuleConfig>();
            return config;
        }

        public static List<RewardSubjectConfig> CreateDefaultRewardSubjects()
        {
            return new List<RewardSubjectConfig>
            {
                new RewardSubjectConfig { Id = "subject-1", Name = "科目1" },
                new RewardSubjectConfig { Id = "subject-2", Name = "科目2" },
                new RewardSubjectConfig { Id = "subject-3", Name = "科目3" },
                new RewardSubjectConfig { Id = "subject-4", Name = "科目4" }
            };
        }
    }
}
