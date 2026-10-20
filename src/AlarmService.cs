using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;

namespace FocusHarbor
{
    public static class AlarmService
    {
        private const int AlarmWarmupSeconds = 50;
        private const int WakeBeaconHoldSeconds = 75;
        private static DateTime lastRestartAttemptUtc = DateTime.MinValue;

        [Flags]
        private enum ExecutionState : uint
        {
            Continuous = 0x80000000,
            SystemRequired = 0x00000001,
            AwayModeRequired = 0x00000040
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);

        public static void ApplySchedule(FocusConfig config, string executablePath, bool requestHighestPrivileges)
        {
            if (config != null && config.Alarm != null && config.Alarm.Enabled)
            {
                ValidateAlarmConfig(config.Alarm, true);
                if (requestHighestPrivileges)
                {
                    RunElevatedAlarmHelper(executablePath, "--register-alarm-elevated " + config.Alarm.Hour.ToString(CultureInfo.InvariantCulture) + " " + config.Alarm.Minute.ToString(CultureInfo.InvariantCulture));
                }
                else if (!TaskSchedulerService.TaskExists(TaskSchedulerService.AlarmTaskName) ||
                         !TaskSchedulerService.TaskExists(TaskSchedulerService.AlarmFallbackTaskName))
                {
                    TaskSchedulerService.RegisterDailyAlarm(config.Alarm.Hour, config.Alarm.Minute, executablePath);
                }
                JsonStore.AppendLog("Daily alarm scheduled to wake one minute before " + config.Alarm.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + config.Alarm.Minute.ToString("00", CultureInfo.InvariantCulture) + ", warm up for 50 seconds, and retain a direct fallback" + (requestHighestPrivileges ? " with highest privileges and a SYSTEM wake beacon." : "."));
            }
            else
            {
                if (requestHighestPrivileges)
                {
                    RunElevatedAlarmHelper(executablePath, "--remove-alarm-elevated");
                }
                else
                {
                    try { TaskSchedulerService.DeleteDailyAlarmTasks(); }
                    catch (Exception exception) { JsonStore.AppendLog("Non-elevated alarm task cleanup was deferred: " + exception.Message); }
                }
            }
        }

        public static void ApplySchedule(FocusConfig config, string executablePath)
        {
            ApplySchedule(config, executablePath, false);
        }

        public static void RunWarmup(string executablePath)
        {
            FocusConfig config = JsonStore.LoadConfig();
            if (config.Alarm == null || !config.Alarm.Enabled)
            {
                JsonStore.AppendLog("Alarm warm-up skipped because the alarm is disabled.");
                return;
            }

            JsonStore.AppendLog("Alarm warm-up started in interactive session " + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture) + ".");
            HoldSystemAwake(true);
            try
            {
                DateTime warmupEnds = DateTime.UtcNow.AddSeconds(AlarmWarmupSeconds);
                while (DateTime.UtcNow < warmupEnds)
                {
                    int remaining = (int)Math.Ceiling((warmupEnds - DateTime.UtcNow).TotalMilliseconds);
                    Thread.Sleep(Math.Max(1, Math.Min(500, remaining)));
                }
                JsonStore.AppendLog("Alarm warm-up completed; starting the locked-session audio challenge.");
                RunChallenge(executablePath);
            }
            finally
            {
                HoldSystemAwake(false);
            }
        }

        public static void HoldWakeBeacon()
        {
            HoldSystemAwake(true);
            try
            {
                Thread.Sleep(TimeSpan.FromSeconds(WakeBeaconHoldSeconds));
            }
            finally
            {
                HoldSystemAwake(false);
            }
        }

        private static void HoldSystemAwake(bool enabled)
        {
            if (!enabled)
            {
                SetThreadExecutionState(ExecutionState.Continuous);
                return;
            }
            ExecutionState result = SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.AwayModeRequired);
            if (result == 0)
            {
                SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
            }
        }

        private static void RunElevatedAlarmHelper(string executablePath, string arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(executablePath, arguments);
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            startInfo.WorkingDirectory = Path.GetDirectoryName(executablePath);
            startInfo.WindowStyle = ProcessWindowStyle.Hidden;
            try
            {
                using (Process process = Process.Start(startInfo))
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        bool mayHaveChangedTasks = process.ExitCode == 1;
                        throw new AlarmScheduleException(
                            "最高权限唤醒任务配置失败。退出代码：" + process.ExitCode.ToString(CultureInfo.InvariantCulture),
                            mayHaveChangedTasks,
                            null);
                    }
                }
            }
            catch (Win32Exception exception)
            {
                bool cancelled = exception.NativeErrorCode == 1223;
                throw new AlarmScheduleException(
                    cancelled ? "管理员授权已取消，闹钟设置没有更改。" : "无法启动最高权限配置程序：" + exception.Message,
                    false,
                    exception);
            }
        }

        public static void ValidateAlarmConfig(AlarmConfig alarm, bool requireEnabledFields)
        {
            if (alarm == null)
            {
                throw new InvalidOperationException("闹钟配置为空。");
            }
            if (alarm.Hour < 0 || alarm.Hour > 23 || alarm.Minute < 0 || alarm.Minute > 59)
            {
                throw new InvalidOperationException("闹钟时间无效，请使用 00:00 到 23:59。");
            }
            alarm.ChallengeMinutes = 15;
            alarm.QuarantineHours = 120;
            if (!requireEnabledFields)
            {
                return;
            }
            if (alarm.DepositFolderPolicyVersion < 2)
            {
                throw new InvalidOperationException("隔离规则已升级为整个押金文件夹。请重新选择并保存押金文件夹，以确认新的可恢复隔离规则。");
            }
            string challenge = NormalizeText(alarm.ChallengeText).Trim();
            if (challenge.Length < 150)
            {
                throw new InvalidOperationException("打字内容至少需要 150 个字符；当前为 " + challenge.Length.ToString(CultureInfo.InvariantCulture) + " 个。");
            }
            if (String.IsNullOrWhiteSpace(alarm.MusicPath) || !File.Exists(alarm.MusicPath))
            {
                throw new InvalidOperationException("请选择一个存在的本地音乐文件。");
            }
            string depositFolder = ValidateDepositRoot(alarm.DepositFolder, true);
            InspectDepositFolder(depositFolder);
            if (IsPathInside(alarm.MusicPath, depositFolder))
            {
                throw new InvalidOperationException("闹铃音乐不能放在押金文件夹内部，否则隔离时可能中断播放或导致移动失败。");
            }
        }

        public static void RunChallenge(string executablePath)
        {
            HoldSystemAwake(true);
            try
            {
                RunChallengeCore(executablePath);
            }
            finally
            {
                HoldSystemAwake(false);
            }
        }

        private static void RunChallengeCore(string executablePath)
        {
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(false, @"Local\FocusHarborAlarmChallenge"))
            {
                bool lockTaken = false;
                try
                {
                    lockTaken = MutexSafety.Wait(mutex, 0);
                    if (!lockTaken)
                    {
                        return;
                    }

                    FocusConfig config = JsonStore.LoadConfig();
                    AlarmChallengeState state = LoadChallengeState();
                    if (state == null || !state.Active)
                    {
                        if (config.Alarm == null || !config.Alarm.Enabled)
                        {
                            return;
                        }
                        ValidateAlarmConfig(config.Alarm, true);
                        DateTimeOffset started = DateTimeOffset.Now;
                        state = new AlarmChallengeState
                        {
                            Version = 1,
                            Active = true,
                            PenaltyApplied = false,
                            RunnerPid = Process.GetCurrentProcess().Id,
                            RunnerProcessStartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                            StartedAt = started.ToString("o", CultureInfo.InvariantCulture),
                            DeadlineAt = started.AddMinutes(15).ToString("o", CultureInfo.InvariantCulture),
                            CompletedAt = String.Empty,
                            PenaltyMessage = String.Empty,
                            ChallengeText = NormalizeText(config.Alarm.ChallengeText).Trim(),
                            MusicPath = config.Alarm.MusicPath,
                            DepositFolder = config.Alarm.DepositFolder,
                            DepositFolderPolicyVersion = config.Alarm.DepositFolderPolicyVersion,
                            QuarantineHours = 120
                        };
                    }
                    state.RunnerPid = Process.GetCurrentProcess().Id;
                    state.RunnerProcessStartedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                    SaveChallengeState(state);

                    Application application = new Application();
                    application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    AlarmChallengeWindow challengeWindow = new AlarmChallengeWindow(state, executablePath);
                    application.Run(challengeWindow);
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Alarm challenge failed: " + exception);
                    MessageBox.Show("晨间闹钟启动失败：" + exception.Message, "Focus Harbor", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    try
                    {
                        AlarmChallengeState latest = LoadChallengeState();
                        if (latest != null && latest.RunnerPid == Process.GetCurrentProcess().Id)
                        {
                            latest.RunnerPid = 0;
                            latest.RunnerProcessStartedAt = String.Empty;
                            SaveChallengeState(latest);
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

        public static void EnsureActiveChallengeRunning(string executablePath)
        {
            AlarmChallengeState state = LoadChallengeState();
            if (state == null || !state.Active || IsChallengeRunnerAlive(state, executablePath))
            {
                return;
            }
            if ((DateTime.UtcNow - lastRestartAttemptUtc).TotalSeconds < 5)
            {
                return;
            }
            lastRestartAttemptUtc = DateTime.UtcNow;
            ProcessStartInfo startInfo = new ProcessStartInfo(executablePath, "--alarm");
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = false;
            Process.Start(startInfo);
            JsonStore.AppendLog("Restarted the active alarm challenge process.");
        }

        public static bool IsChallengeActive()
        {
            AlarmChallengeState state = LoadChallengeState();
            return state != null && state.Active;
        }

        public static bool IsTemporaryPauseBlockedNow(out DateTimeOffset until)
        {
            until = DateTimeOffset.MinValue;
            try
            {
                if (!File.Exists(JsonStore.AlarmPenaltyPath))
                {
                    return false;
                }
                AlarmPenaltyState state = JsonStore.Load<AlarmPenaltyState>(JsonStore.AlarmPenaltyPath);
                DateTimeOffset value;
                if (state != null && DateTimeOffset.TryParse(state.PauseBlockedUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value) && value > DateTimeOffset.Now)
                {
                    until = value;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        public static bool IsCodexBlockedNow(out DateTimeOffset until)
        {
            until = DateTimeOffset.MinValue;
            try
            {
                if (!File.Exists(JsonStore.AlarmPenaltyPath))
                {
                    return false;
                }
                AlarmPenaltyState state = JsonStore.Load<AlarmPenaltyState>(JsonStore.AlarmPenaltyPath);
                DateTimeOffset value;
                if (state != null && DateTimeOffset.TryParse(state.CodexBlockedUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value) && value > DateTimeOffset.Now)
                {
                    until = value;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }

        public static List<QuarantineRecord> GetQuarantineRecords()
        {
            try
            {
                if (!File.Exists(JsonStore.QuarantineStatePath))
                {
                    return new List<QuarantineRecord>();
                }
                QuarantineState state = JsonStore.Load<QuarantineState>(JsonStore.QuarantineStatePath);
                return state == null || state.Records == null
                    ? new List<QuarantineRecord>()
                    : state.Records.Where(item => item != null).OrderByDescending(item => item.QuarantinedAt).ToList();
            }
            catch
            {
                return new List<QuarantineRecord>();
            }
        }

        public static void EnsureQuarantineRecoveryTasks(string executablePath)
        {
            foreach (QuarantineRecord record in GetQuarantineRecords().Where(item => item != null && !item.Restored))
            {
                try
                {
                    Guid parsedId;
                    DateTimeOffset available;
                    if (!Guid.TryParseExact(record.Id, "N", out parsedId) ||
                        !DateTimeOffset.TryParse(record.RestoreAvailableAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out available))
                    {
                        continue;
                    }
                    if (!QuarantineItemExists(record))
                    {
                        if (record.RestoreInProgress && OriginalItemExists(record) && available <= DateTimeOffset.Now)
                        {
                            RestoreQuarantinedFile(record.Id);
                        }
                        continue;
                    }
                    DateTime runAt = available.LocalDateTime > DateTime.Now ? available.LocalDateTime : DateTime.Now.AddSeconds(15);
                    // RegisterOnce updates an existing task as well. Refreshing it
                    // also repairs an action path left behind after an app update.
                    RegisterQuarantineRecovery(record, executablePath, runAt);
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Could not ensure quarantine recovery task for " + record.Id + ": " + exception.Message);
                }
            }
        }

        public static OperationResult RestoreQuarantinedFile(string id)
        {
            if (String.IsNullOrWhiteSpace(id))
            {
                return OperationResult.Fail("请先选择一个隔离项目。", 2);
            }
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(false, @"Local\FocusHarborQuarantine"))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(10));
                    if (!taken)
                    {
                        return OperationResult.Fail("隔离记录正在被使用，请稍后重试。", 1);
                    }
                    QuarantineState state = LoadQuarantineState();
                    QuarantineRecord record = state.Records.FirstOrDefault(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (record == null || record.Restored)
                    {
                        return OperationResult.Fail("没有找到可恢复的隔离项目。", 2);
                    }
                    DateTimeOffset available;
                    if (!DateTimeOffset.TryParse(record.RestoreAvailableAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out available))
                    {
                        return OperationResult.Fail("隔离记录中的恢复时间无效，已停止恢复。", 2);
                    }
                    if (available > DateTimeOffset.Now)
                    {
                        return OperationResult.Fail("该项目将在 " + available.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + " 后允许恢复。", 2);
                    }
                    string depositRoot = IsDirectoryQuarantine(record)
                        ? ValidateQuarantineParent(record.DepositRoot, true)
                        : ValidateDepositRoot(record.DepositRoot, true);
                    string original = Path.GetFullPath(record.OriginalPath);
                    string quarantine = Path.GetFullPath(record.QuarantinePath);
                    if (!IsPathInside(original, depositRoot) || !IsPathInside(quarantine, Path.Combine(depositRoot, ".focus-harbor-quarantine")))
                    {
                        return OperationResult.Fail("隔离记录的路径校验失败，已停止恢复。", 1);
                    }
                    if (IsDirectoryQuarantine(record) && !PathsEqual(Path.GetDirectoryName(original), depositRoot))
                    {
                        return OperationResult.Fail("隔离文件夹的原始父目录校验失败，已停止恢复。", 1);
                    }
                    if (!QuarantineItemExists(record))
                    {
                        if (record.RestoreInProgress && OriginalItemExists(record))
                        {
                            record.RestoreInProgress = false;
                            record.Restored = true;
                            record.RestoredAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                            SaveQuarantineState(state);
                            try { TaskSchedulerService.DeleteTask(TaskSchedulerService.GetQuarantineRestoreTaskName(record.Id)); } catch { }
                            return OperationResult.Ok("项目已恢复到原位置：" + original);
                        }
                        return OperationResult.Fail("隔离项目不存在，无法恢复。", 2);
                    }
                    if ((File.GetAttributes(quarantine) & FileAttributes.ReparsePoint) != 0)
                    {
                        return OperationResult.Fail("隔离项目是链接或重解析项目，恢复已取消。", 2);
                    }
                    if (IsDirectoryQuarantine(record))
                    {
                        int ignoredEntries;
                        long ignoredLength;
                        InspectDepositFolder(quarantine, out ignoredEntries, out ignoredLength);
                    }
                    if (File.Exists(original) || Directory.Exists(original))
                    {
                        return OperationResult.Fail("原位置已有同名项目。为避免覆盖，恢复已取消。", 2);
                    }
                    string originalParent = Path.GetDirectoryName(original);
                    if (String.IsNullOrWhiteSpace(originalParent) || !Directory.Exists(originalParent) || HasReparsePoint(originalParent))
                    {
                        return OperationResult.Fail("原目录不存在或包含链接，恢复已取消。", 2);
                    }
                    record.RestoreInProgress = true;
                    SaveQuarantineState(state);
                    JsonStore.AppendLog("RESTORE QUARANTINE " + quarantine + " -> " + original);
                    if (IsDirectoryQuarantine(record))
                    {
                        Directory.Move(quarantine, original);
                    }
                    else
                    {
                        File.Move(quarantine, original);
                    }
                    record.RestoreInProgress = false;
                    record.Restored = true;
                    record.RestoredAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                    SaveQuarantineState(state);
                    try { TaskSchedulerService.DeleteTask(TaskSchedulerService.GetQuarantineRestoreTaskName(record.Id)); }
                    catch (Exception exception) { JsonStore.AppendLog("Restored file but could not remove completed recovery task: " + exception.Message); }
                    return OperationResult.Ok("项目已恢复到原位置：" + original);
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Quarantine restore failed: " + exception.Message);
                    return OperationResult.Fail("恢复失败：" + exception.Message, 1);
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

        internal static string ApplyFailurePenalty(AlarmChallengeState challenge, string executablePath)
        {
            OperationResult restoreResult = RestrictionService.RestoreTemporary(executablePath);
            DateTimeOffset now = DateTimeOffset.Now;
            DateTimeOffset pauseBlockedUntil = new DateTimeOffset(DateTime.Today.AddDays(1), now.Offset);
            AlarmPenaltyState penalty = null;
            try
            {
                if (File.Exists(JsonStore.AlarmPenaltyPath))
                {
                    AlarmPenaltyState loaded = JsonStore.Load<AlarmPenaltyState>(JsonStore.AlarmPenaltyPath);
                    if (loaded != null && String.Equals(loaded.ChallengeStartedAt, challenge.StartedAt, StringComparison.Ordinal))
                    {
                        penalty = loaded;
                    }
                }
            }
            catch
            {
            }
            if (penalty == null)
            {
                penalty = new AlarmPenaltyState
                {
                    Version = 2,
                    ChallengeRequired = true,
                    ChallengeStartedAt = challenge.StartedAt ?? String.Empty,
                    FailedAt = now.ToString("o", CultureInfo.InvariantCulture),
                    PauseBlockedUntil = pauseBlockedUntil.ToString("o", CultureInfo.InvariantCulture),
                    CodexBlockedUntil = pauseBlockedUntil.ToString("o", CultureInfo.InvariantCulture),
                    QuarantineAttempted = false,
                    QuarantineRecordId = String.Empty,
                    QuarantineMessage = String.Empty
                };
                // Persist the challenge identity before touching the deposit folder.
                // If the process is interrupted, the retry resumes this exact
                // challenge instead of starting a second quarantine.
                JsonStore.Save(JsonStore.AlarmPenaltyPath, penalty);
            }
            else
            {
                penalty.Version = Math.Max(2, penalty.Version);
                penalty.ChallengeRequired = true;
            }

            string quarantineMessage = penalty.QuarantineMessage ?? String.Empty;
            if (!penalty.QuarantineAttempted)
            {
                if (challenge.DepositFolderPolicyVersion < 2)
                {
                    quarantineMessage = "旧闹钟任务没有确认新的整个文件夹隔离规则，因此本次没有移动任何文件夹。请在 00:00–02:00 重新保存闹钟配置。";
                }
                else
                {
                    try
                    {
                        QuarantineRecord record = QuarantineDepositFolder(challenge.DepositFolder, Math.Max(120, challenge.QuarantineHours), false, challenge.StartedAt);
                        quarantineMessage = "已隔离押金文件夹：" + record.FileName + "（120 小时后自动恢复，也可到期手动取回）";
                        penalty.QuarantineRecordId = record.Id;
                    }
                    catch (Exception exception)
                    {
                        quarantineMessage = "隔离押金文件夹失败，未进行重试：" + exception.Message;
                        JsonStore.AppendLog("Alarm quarantine failed without retry: " + exception.Message);
                    }
                }
                penalty.QuarantineAttempted = true;
                penalty.QuarantineMessage = quarantineMessage;
            }
            JsonStore.Save(JsonStore.AlarmPenaltyPath, penalty);
            string restrictionMessage = restoreResult.Success ? "专注限制已立即恢复" : "恢复专注限制时出现错误：" + restoreResult.Message;
            return restrictionMessage + "；今天不能再临时解除，也不能启动 Codex。\n" + quarantineMessage;
        }

        internal static QuarantineRecord QuarantineDepositFolder(string depositFolder, int quarantineHours, bool allowAnyDrive, string challengeStartedAt)
        {
            using (System.Threading.Mutex mutex = new System.Threading.Mutex(false, @"Local\FocusHarborQuarantine"))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(10));
                    if (!taken)
                    {
                        throw new IOException("隔离记录正在由另一个进程更新。");
                    }

                    QuarantineState state = LoadQuarantineState();
                    QuarantineRecord existing = state.Records.FirstOrDefault(item =>
                        item != null && !String.IsNullOrWhiteSpace(challengeStartedAt) &&
                        String.Equals(item.ChallengeStartedAt, challengeStartedAt, StringComparison.Ordinal));
                    if (existing != null)
                    {
                        string existingRoot = GetQuarantineRoot(existing);
                        ResumeReservedQuarantine(existing, existingRoot);
                        EnsureRecoveryForRecord(existing);
                        return existing;
                    }

                    string root = ValidateDepositRoot(depositFolder, !allowAnyDrive);
                    int entryCount;
                    long totalLength;
                    InspectDepositFolder(root, out entryCount, out totalLength);
                    string parent = Path.GetDirectoryName(root);
                    if (String.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent) || HasReparsePoint(parent))
                    {
                        throw new InvalidOperationException("押金文件夹的父目录无效或包含重解析点。");
                    }

                    string quarantineRoot = Path.Combine(parent, ".focus-harbor-quarantine");
                    string id = Guid.NewGuid().ToString("N");
                    string recordDirectory = Path.Combine(quarantineRoot, id);
                    string quarantinePath = Path.Combine(recordDirectory, Path.GetFileName(root));
                    if (!IsPathInside(quarantinePath, quarantineRoot) || IsPathInside(quarantineRoot, root))
                    {
                        throw new IOException("隔离目标未通过路径安全校验。");
                    }
                    Directory.CreateDirectory(recordDirectory);
                    if (HasReparsePoint(quarantineRoot) || HasReparsePoint(recordDirectory))
                    {
                        throw new IOException("隔离目录包含符号链接、联接点或其他重解析目录。");
                    }
                    try
                    {
                        File.SetAttributes(quarantineRoot, File.GetAttributes(quarantineRoot) | FileAttributes.Hidden);
                    }
                    catch
                    {
                    }

                    DateTimeOffset now = DateTimeOffset.Now;
                    QuarantineRecord record = new QuarantineRecord
                    {
                        Id = id,
                        ChallengeStartedAt = challengeStartedAt ?? String.Empty,
                        QuarantineKind = "Directory",
                        FileName = Path.GetFileName(root),
                        OriginalPath = root,
                        DepositRoot = parent,
                        QuarantinePath = quarantinePath,
                        Length = totalLength,
                        EntryCount = entryCount,
                        QuarantinedAt = now.ToString("o", CultureInfo.InvariantCulture),
                        RestoreAvailableAt = now.AddHours(Math.Max(120, quarantineHours)).ToString("o", CultureInfo.InvariantCulture),
                        RestoreInProgress = false,
                        Restored = false,
                        RestoredAt = String.Empty
                    };

                    // Persist the exact source and destination before the atomic
                    // same-volume directory move.  A retry resumes this record and
                    // never selects a second folder for the same challenge.
                    state.Records.Add(record);
                    SaveQuarantineState(state);
                    try
                    {
                        JsonStore.AppendLog("QUARANTINE DIRECTORY SELECTED " + root + " -> " + quarantinePath);
                        Directory.Move(root, quarantinePath);
                        EnsureRecoveryForRecord(record);
                        return record;
                    }
                    catch
                    {
                        if (!Directory.Exists(quarantinePath))
                        {
                            state.Records.Remove(record);
                            SaveQuarantineState(state);
                        }
                        throw;
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

        private static void ResumeReservedQuarantine(QuarantineRecord record, string quarantineRoot)
        {
            if (record.Restored || QuarantineItemExists(record))
            {
                return;
            }
            string original = Path.GetFullPath(record.OriginalPath);
            string quarantine = Path.GetFullPath(record.QuarantinePath);
            if (!OriginalItemExists(record))
            {
                throw new IOException("已预留的押金项目在原位置和隔离位置都不存在；为避免误伤，不会选择第二个项目。");
            }
            if (!IsPathInside(original, record.DepositRoot) || !IsPathInside(quarantine, quarantineRoot) || HasReparsePoint(Path.GetDirectoryName(quarantine)))
            {
                throw new IOException("已预留的隔离记录未通过路径安全校验。");
            }
            if (IsDirectoryQuarantine(record) && !PathsEqual(Path.GetDirectoryName(original), record.DepositRoot))
            {
                throw new IOException("已预留的隔离文件夹父目录校验失败。");
            }
            JsonStore.AppendLog("QUARANTINE RESUME " + original + " -> " + quarantine);
            if (IsDirectoryQuarantine(record))
            {
                InspectDepositFolder(original);
                Directory.Move(original, quarantine);
            }
            else
            {
                File.Move(original, quarantine);
            }
        }

        private static void EnsureRecoveryForRecord(QuarantineRecord record)
        {
            try
            {
                DateTimeOffset restoreAt;
                if (!record.Restored && QuarantineItemExists(record) &&
                    DateTimeOffset.TryParse(record.RestoreAvailableAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out restoreAt))
                {
                    DateTime runAt = restoreAt.LocalDateTime > DateTime.Now ? restoreAt.LocalDateTime : DateTime.Now.AddSeconds(15);
                    RegisterQuarantineRecovery(record, Process.GetCurrentProcess().MainModule.FileName, runAt);
                }
            }
            catch (Exception exception)
            {
                JsonStore.AppendLog("Automatic quarantine recovery scheduling failed; manual recovery remains available: " + exception.Message);
            }
        }

        internal static void SaveChallengeState(AlarmChallengeState state)
        {
            JsonStore.Save(JsonStore.AlarmChallengePath, state);
        }

        internal static AlarmChallengeState LoadChallengeState()
        {
            try
            {
                return File.Exists(JsonStore.AlarmChallengePath) ? JsonStore.Load<AlarmChallengeState>(JsonStore.AlarmChallengePath) : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsChallengeRunnerAlive(AlarmChallengeState state, string executablePath)
        {
            if (state.RunnerPid <= 0)
            {
                return false;
            }
            try
            {
                using (Process process = Process.GetProcessById(state.RunnerPid))
                {
                    DateTimeOffset started;
                    if (DateTimeOffset.TryParse(state.RunnerProcessStartedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out started))
                    {
                        if (Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) > 1)
                        {
                            return false;
                        }
                        try
                        {
                            return String.Equals(Path.GetFullPath(process.MainModule.FileName), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
                        }
                        catch (Win32Exception)
                        {
                            // A normal-integrity guard cannot inspect MainModule on
                            // the elevated alarm process. PID + start time + image
                            // name still form a safe identity check and avoid a
                            // restart storm while Windows is locked.
                            return String.Equals(process.ProcessName, Path.GetFileNameWithoutExtension(executablePath), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    return GuardService.HasModeArgument(ProcessCommandLineReader.Read(state.RunnerPid), "--alarm");
                }
            }
            catch
            {
                return false;
            }
        }

        private static QuarantineState LoadQuarantineState()
        {
            try
            {
                if (File.Exists(JsonStore.QuarantineStatePath))
                {
                    QuarantineState state = JsonStore.Load<QuarantineState>(JsonStore.QuarantineStatePath);
                    if (state != null)
                    {
                        state.Records = state.Records ?? new List<QuarantineRecord>();
                        return state;
                    }
                }
            }
            catch
            {
            }
            return new QuarantineState { Version = 1, Records = new List<QuarantineRecord>() };
        }

        private static void SaveQuarantineState(QuarantineState state)
        {
            JsonStore.Save(JsonStore.QuarantineStatePath, state);
        }

        private static void RegisterQuarantineRecovery(QuarantineRecord record, string executablePath, DateTime runAt)
        {
            TaskSchedulerService.RegisterOnce(
                TaskSchedulerService.GetQuarantineRestoreTaskName(record.Id),
                runAt,
                executablePath,
                "--restore-quarantine " + record.Id,
                "Automatically restore a Focus Harbor deposit item after its 120-hour quarantine.");
            JsonStore.AppendLog("Quarantine recovery scheduled for " + record.FileName + " at " + runAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ".");
        }

        private static bool IsDirectoryQuarantine(QuarantineRecord record)
        {
            return record != null && String.Equals(record.QuarantineKind, "Directory", StringComparison.OrdinalIgnoreCase);
        }

        private static bool QuarantineItemExists(QuarantineRecord record)
        {
            return record != null && (IsDirectoryQuarantine(record)
                ? Directory.Exists(record.QuarantinePath)
                : File.Exists(record.QuarantinePath));
        }

        private static bool OriginalItemExists(QuarantineRecord record)
        {
            return record != null && (IsDirectoryQuarantine(record)
                ? Directory.Exists(record.OriginalPath)
                : File.Exists(record.OriginalPath));
        }

        private static string GetQuarantineRoot(QuarantineRecord record)
        {
            if (record == null || String.IsNullOrWhiteSpace(record.DepositRoot))
            {
                throw new InvalidDataException("隔离记录缺少押金目录。");
            }
            return Path.Combine(Path.GetFullPath(record.DepositRoot), ".focus-harbor-quarantine");
        }

        private static void InspectDepositFolder(string root)
        {
            int ignoredCount;
            long ignoredLength;
            InspectDepositFolder(root, out ignoredCount, out ignoredLength);
        }

        private static void InspectDepositFolder(string root, out int entryCount, out long totalLength)
        {
            const int MaximumEntries = 100000;
            const long MaximumBytes = 100L * 1024L * 1024L * 1024L;
            entryCount = 0;
            totalLength = 0;
            int regularFiles = 0;
            Stack<string> pending = new Stack<string>();
            pending.Push(Path.GetFullPath(root));
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                foreach (FileSystemInfo item in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    entryCount++;
                    if (entryCount > MaximumEntries)
                    {
                        throw new InvalidOperationException("押金文件夹超过 100000 个项目，为避免恢复风险，不能使用该文件夹。");
                    }
                    FileAttributes attributes = item.Attributes;
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0)
                    {
                        throw new InvalidOperationException("押金文件夹内部不能包含系统项目、符号链接、联接点或其他重解析项目：" + item.FullName);
                    }
                    DirectoryInfo directory = item as DirectoryInfo;
                    if (directory != null)
                    {
                        if (String.Equals(directory.Name, ".focus-harbor-quarantine", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException("押金文件夹内已有旧隔离目录，请先恢复其中项目或选择一个干净文件夹。");
                        }
                        pending.Push(directory.FullName);
                        continue;
                    }
                    FileInfo file = item as FileInfo;
                    if (file != null)
                    {
                        regularFiles++;
                        checked { totalLength += file.Length; }
                        if (totalLength > MaximumBytes)
                        {
                            throw new InvalidOperationException("押金文件夹超过 100 GB，为避免恢复风险，不能使用该文件夹。");
                        }
                    }
                }
            }
            if (regularFiles == 0)
            {
                throw new InvalidOperationException("押金文件夹必须至少包含一个普通文件。");
            }
        }

        private static string ValidateQuarantineParent(string path, bool requireEDrive)
        {
            if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new InvalidOperationException("押金文件夹的父目录不存在。");
            }
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            full = PathsEqual(full, root) ? root : full.TrimEnd(Path.DirectorySeparatorChar);
            if (requireEDrive && !String.Equals(root, @"E:\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("押金文件夹必须位于 E 盘。");
            }
            if (HasReparsePoint(full))
            {
                throw new InvalidOperationException("押金目录不能包含符号链接、联接点或其他重解析目录。");
            }
            EnsureNotProtectedLocation(full);
            return full;
        }

        private static void EnsureNotProtectedLocation(string candidate)
        {
            List<string> protectedRoots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetEnvironmentVariable("OneDrive"),
                Environment.GetEnvironmentVariable("OneDriveConsumer"),
                Environment.GetEnvironmentVariable("OneDriveCommercial")
            };
            string full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            foreach (string protectedRoot in protectedRoots.Where(value => !String.IsNullOrWhiteSpace(value)))
            {
                string protectedFull = Path.GetFullPath(protectedRoot).TrimEnd(Path.DirectorySeparatorChar);
                if (PathsEqual(full, protectedFull) || IsPathInside(full, protectedFull) || IsPathInside(protectedFull, full))
                {
                    throw new InvalidOperationException("桌面、文档、云盘、系统目录和软件目录不能作为押金文件夹或其父目录。");
                }
            }
            string[] segments = full.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            string[] cloudNames = { "OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "iCloudDrive" };
            if (segments.Any(segment => cloudNames.Any(name => String.Equals(segment, name, StringComparison.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException("云同步目录不能作为押金文件夹。");
            }
        }

        private static string ValidateDepositRoot(string path, bool requireEDrive)
        {
            if (String.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                throw new InvalidOperationException("请选择一个存在的押金文件夹。");
            }
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.GetPathRoot(full);
            if (requireEDrive && !String.Equals(root, @"E:\", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("押金文件夹必须位于 E 盘。");
            }
            if (String.Equals(full, root == null ? String.Empty : root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("不能把整个磁盘根目录设为押金文件夹，请选择一个具体子文件夹。");
            }
            if (HasReparsePoint(full))
            {
                throw new InvalidOperationException("押金文件夹不能是符号链接、联接点或其他重解析目录。");
            }
            EnsureNotProtectedLocation(full);
            if (String.Equals(Path.GetFileName(full), ".focus-harbor-quarantine", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("不能把 Focus Harbor 的隔离目录本身设为押金文件夹。");
            }
            string state = Path.GetFullPath(JsonStore.StateDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (IsPathInside(full, state) || IsPathInside(state, full))
            {
                throw new InvalidOperationException("Focus Harbor 的状态目录不能作为押金文件夹。");
            }
            return full;
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return String.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasReparsePoint(string directory)
        {
            DirectoryInfo current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current != null)
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                current = current.Parent;
            }
            return false;
        }

        private static bool IsPathInside(string candidate, string directory)
        {
            try
            {
                string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string path = Path.GetFullPath(candidate);
                return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string NormalizeText(string value)
        {
            return (value ?? String.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }

    internal sealed class AlarmScheduleException : Exception
    {
        public AlarmScheduleException(string message, bool mayHaveChangedTasks, Exception innerException)
            : base(message, innerException)
        {
            MayHaveChangedTasks = mayHaveChangedTasks;
        }

        public bool MayHaveChangedTasks { get; private set; }
    }

    internal sealed class AlarmChallengeWindow : Window
    {
        private readonly AlarmChallengeState state;
        private readonly string executablePath;
        private readonly DateTimeOffset deadline;
        private readonly TextBox input;
        private readonly TextBlock countdown;
        private readonly TextBlock progress;
        private readonly TextBlock status;
        private readonly TextBlock targetDisplay;
        private readonly ScrollViewer targetScroller;
        private readonly List<Inline> targetGlyphs;
        private readonly List<int> targetGlyphStates;
        private readonly Grid progressTrack;
        private readonly Border progressFill;
        private readonly Button stopSound;
        private readonly DispatcherTimer timer;
        private readonly MediaPlayer player;
        private bool mayClose;

        public AlarmChallengeWindow(AlarmChallengeState state, string executablePath)
        {
            this.state = state;
            this.executablePath = executablePath;
            DateTimeOffset parsedDeadline;
            deadline = DateTimeOffset.TryParse(state.DeadlineAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out parsedDeadline) ? parsedDeadline : DateTimeOffset.Now.AddMinutes(15);

            Title = "Focus Harbor · 晨间打字任务";
            Width = 1040;
            Height = 720;
            MinWidth = 820;
            MinHeight = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Topmost = true;
            Background = new SolidColorBrush(Color.FromRgb(7, 11, 22));
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(7),
                CornerRadius = new CornerRadius(18),
                GlassFrameThickness = new Thickness(0)
            });

            Grid root = new Grid { Margin = new Thickness(34) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel title = new StackPanel();
            title.Children.Add(new TextBlock { Text = "早安，现在开始兑现昨晚的决定。", FontSize = 27, FontWeight = FontWeights.Bold });
            title.Children.Add(new TextBlock { Text = "逐字手打下方内容；禁止复制、剪切、粘贴和拖放。完成前普通关闭无效。", FontSize = 13, Foreground = Brush("#FF9BA7BF"), Margin = new Thickness(0, 9, 0, 0) });
            header.Children.Add(title);
            StackPanel clockPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            countdown = new TextBlock { Text = "15:00", FontSize = 35, FontWeight = FontWeights.Bold, Foreground = Brush("#FF67E1D5"), HorizontalAlignment = HorizontalAlignment.Right };
            clockPanel.Children.Add(countdown);
            clockPanel.Children.Add(new TextBlock { Text = "超时将执行三项后果", Foreground = Brush("#FFFFB86C"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right });
            Grid.SetColumn(clockPanel, 1);
            header.Children.Add(clockPanel);
            root.Children.Add(header);

            Border targetCard = Card();
            targetCard.Padding = new Thickness(20);
            targetDisplay = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("#FF68758D"),
                FontSize = 17,
                LineHeight = 29
            };
            targetGlyphs = new List<Inline>();
            targetGlyphStates = new List<int>();
            string displayText = AlarmService.NormalizeText(state.ChallengeText).Trim();
            foreach (char character in displayText)
            {
                Inline glyph;
                if (character == '\n')
                {
                    glyph = new LineBreak();
                }
                else
                {
                    string visible = character == ' ' ? "\u00A0" : (character == '\t' ? "    " : character.ToString());
                    glyph = new Run(visible);
                }
                glyph.Foreground = Brush("#FF68758D");
                glyph.Background = Brushes.Transparent;
                targetDisplay.Inlines.Add(glyph);
                targetGlyphs.Add(glyph);
                targetGlyphStates.Add(0);
            }
            targetScroller = new ScrollViewer
            {
                Content = targetDisplay,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false
            };
            targetCard.Child = targetScroller;
            Grid.SetRow(targetCard, 2);
            root.Children.Add(targetCard);

            Border inputCard = Card();
            inputCard.Padding = new Thickness(16);
            inputCard.BorderBrush = Brush("#887C6BFF");
            input = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                SelectionBrush = Brush("#FF7563F6"),
                BorderThickness = new Thickness(0),
                FontSize = 16,
                AcceptsTab = true,
                AllowDrop = false,
                ContextMenu = null
            };
            TextBlock.SetLineHeight(input, 27);
            SpellCheck.SetIsEnabled(input, false);
            inputCard.Child = input;
            Grid.SetRow(inputCard, 4);
            root.Children.Add(inputCard);

            Grid footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel messages = new StackPanel();
            progress = new TextBlock { Foreground = Brush("#FF9DA8C0"), FontSize = 12 };
            status = new TextBlock { Foreground = Brush("#FFFFB86C"), FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
            messages.Children.Add(progress);
            progressTrack = new Grid { Height = 6, Margin = new Thickness(0, 9, 16, 2), Background = Brush("#FF202A3D") };
            progressFill = new Border
            {
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(3),
                Background = new LinearGradientBrush(Color.FromRgb(124, 107, 255), Color.FromRgb(51, 214, 200), 0)
            };
            progressTrack.Children.Add(progressFill);
            messages.Children.Add(progressTrack);
            messages.Children.Add(status);
            footer.Children.Add(messages);
            stopSound = new Button
            {
                Content = "停止闹铃声音",
                Width = 154,
                Height = 44,
                Background = Brush("#FF2B344A"),
                Foreground = Brushes.White,
                BorderBrush = Brush("#557C6BFF"),
                BorderThickness = new Thickness(1),
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand
            };
            stopSound.Template = RoundedButtonTemplate();
            Grid.SetColumn(stopSound, 1);
            footer.Children.Add(stopSound);
            Grid.SetRow(footer, 5);
            root.Children.Add(footer);
            Border windowFrame = new Border
            {
                Background = Brush("#FF070B16"),
                BorderBrush = Brush("#5064728D"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Child = root
            };
            Content = windowFrame;

            DataObject.AddPastingHandler(input, delegate(object sender, DataObjectPastingEventArgs args) { args.CancelCommand(); });
            input.PreviewKeyDown += BlockClipboardShortcuts;
            input.PreviewMouseRightButtonDown += delegate(object sender, MouseButtonEventArgs args) { args.Handled = true; };
            input.PreviewDrop += delegate(object sender, DragEventArgs args) { args.Handled = true; };
            input.CommandBindings.Add(BlockCommand(ApplicationCommands.Paste));
            input.CommandBindings.Add(BlockCommand(ApplicationCommands.Copy));
            input.CommandBindings.Add(BlockCommand(ApplicationCommands.Cut));
            input.TextChanged += delegate { UpdateProgress(); };
            stopSound.Click += delegate { StopAudio(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.F4 && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
                {
                    args.Handled = true;
                }
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs args)
            {
                if (!mayClose)
                {
                    args.Cancel = true;
                    status.Text = "任务尚未完成，窗口不能关闭。";
                    Activate();
                    Topmost = true;
                    input.Focus();
                }
            };

            player = new MediaPlayer();
            player.MediaEnded += delegate { StopAudio(); };
            player.MediaFailed += delegate { status.Text = "音乐播放失败，但打字任务仍会继续。"; DisableStopSound("闹铃声音不可用"); };
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += delegate { Tick(); };
            Loaded += delegate
            {
                input.Focus();
                Keyboard.Focus(input);
                TryPlayAudio();
                UpdateProgress();
                Tick();
                timer.Start();
            };
        }

        private void Tick()
        {
            TimeSpan remaining = deadline - DateTimeOffset.Now;
            if (remaining > TimeSpan.Zero)
            {
                countdown.Text = ((int)remaining.TotalMinutes).ToString("00", CultureInfo.InvariantCulture) + ":" + remaining.Seconds.ToString("00", CultureInfo.InvariantCulture);
                return;
            }
            countdown.Text = "已超时";
            countdown.Foreground = Brush("#FFFF7E89");
            if (!state.PenaltyApplied)
            {
                state.PenaltyApplied = true;
                state.PenaltyMessage = AlarmService.ApplyFailurePenalty(state, executablePath);
                AlarmService.SaveChallengeState(state);
            }
            status.Text = "超时后果已执行：" + (state.PenaltyMessage ?? String.Empty) + "\n完成补打后才能关闭。";
        }

        private void UpdateProgress()
        {
            string expected = AlarmService.NormalizeText(state.ChallengeText).Trim();
            string typed = AlarmService.NormalizeText(input.Text);
            int matching = 0;
            int firstWrong = -1;
            for (int index = 0; index < expected.Length; index++)
            {
                int visualState;
                if (index < typed.Length)
                {
                    if (typed[index] == expected[index])
                    {
                        visualState = 1;
                        matching++;
                    }
                    else
                    {
                        visualState = 2;
                        if (firstWrong < 0)
                        {
                            firstWrong = index;
                        }
                    }
                }
                else if (index == typed.Length)
                {
                    visualState = 3;
                }
                else
                {
                    visualState = 0;
                }
                SetGlyphState(index, visualState);
            }

            int typedForAccuracy = Math.Max(1, typed.Length);
            int accuracy = typed.Length == 0 ? 100 : (int)Math.Round(100.0 * matching / typedForAccuracy);
            bool hasError = firstWrong >= 0 || typed.Length > expected.Length;
            string issue = firstWrong >= 0
                ? " · 第 " + (firstWrong + 1).ToString(CultureInfo.InvariantCulture) + " 字不正确"
                : (typed.Length > expected.Length ? " · 多输入了 " + (typed.Length - expected.Length).ToString(CultureInfo.InvariantCulture) + " 字" : String.Empty);
            progress.Text = "进度 " + Math.Min(typed.Length, expected.Length).ToString(CultureInfo.InvariantCulture) + "/" + expected.Length.ToString(CultureInfo.InvariantCulture) + " · 正确 " + matching.ToString(CultureInfo.InvariantCulture) + " · 准确率 " + accuracy.ToString(CultureInfo.InvariantCulture) + "%" + issue;
            progress.Foreground = hasError ? Brush("#FFFF8792") : Brush("#FF9DA8C0");
            AnimateProgress(expected.Length == 0 ? 0 : Math.Min(1.0, (double)typed.Length / expected.Length), hasError);

            if (targetGlyphs.Count > 0)
            {
                int followIndex = Math.Min(Math.Max(0, typed.Length), targetGlyphs.Count - 1);
                try { targetGlyphs[followIndex].BringIntoView(); } catch { }
            }
            if (!String.Equals(typed, expected, StringComparison.Ordinal))
            {
                return;
            }

            // The timer runs every 250 ms. Without this direct check, text entered
            // just after the deadline but before the next timer tick could be
            // incorrectly accepted as on time.
            if (DateTimeOffset.Now >= deadline && !state.PenaltyApplied)
            {
                Tick();
            }

            state.Active = false;
            state.CompletedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
            state.RunnerPid = 0;
            AlarmService.SaveChallengeState(state);
            try
            {
                if (File.Exists(JsonStore.AlarmPenaltyPath))
                {
                    AlarmPenaltyState penalty = JsonStore.Load<AlarmPenaltyState>(JsonStore.AlarmPenaltyPath);
                    if (penalty != null && String.Equals(penalty.ChallengeStartedAt, state.StartedAt, StringComparison.Ordinal))
                    {
                        penalty.ChallengeRequired = false;
                        JsonStore.Save(JsonStore.AlarmPenaltyPath, penalty);
                    }
                }
            }
            catch
            {
            }
            StopAudio();
            timer.Stop();
            mayClose = true;
            MessageBox.Show(this, state.PenaltyApplied ? "补打完成。专注限制继续生效，今天仍不能临时解除。" : "打字任务已完成，早安。", "Focus Harbor", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }

        private void SetGlyphState(int index, int visualState)
        {
            if (index < 0 || index >= targetGlyphs.Count || targetGlyphStates[index] == visualState)
            {
                return;
            }
            Inline glyph = targetGlyphs[index];
            targetGlyphStates[index] = visualState;

            Color destination;
            if (visualState == 1)
            {
                destination = Color.FromRgb(248, 251, 255);
            }
            else if (visualState == 2)
            {
                destination = Color.FromRgb(255, 101, 118);
            }
            else if (visualState == 3)
            {
                destination = Color.FromRgb(170, 158, 255);
            }
            else
            {
                destination = Color.FromRgb(104, 117, 141);
            }

            SolidColorBrush previous = glyph.Foreground as SolidColorBrush;
            Color start = previous == null ? Color.FromRgb(104, 117, 141) : previous.Color;
            SolidColorBrush animated = new SolidColorBrush(start);
            glyph.Foreground = animated;
            ColorAnimation transition = new ColorAnimation(start, destination, TimeSpan.FromMilliseconds(visualState == 1 ? 190 : 120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transition.Completed += delegate
            {
                animated.BeginAnimation(SolidColorBrush.ColorProperty, null);
                animated.Color = destination;
            };
            animated.BeginAnimation(SolidColorBrush.ColorProperty, transition);

            if (visualState == 2)
            {
                glyph.Background = new SolidColorBrush(Color.FromArgb(54, 255, 74, 96));
            }
            else if (visualState == 3)
            {
                SolidColorBrush pulse = new SolidColorBrush(Color.FromArgb(30, 124, 107, 255));
                ColorAnimation pulseAnimation = new ColorAnimation(
                    Color.FromArgb(24, 124, 107, 255),
                    Color.FromArgb(82, 124, 107, 255),
                    TimeSpan.FromMilliseconds(620))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                pulse.BeginAnimation(SolidColorBrush.ColorProperty, pulseAnimation);
                glyph.Background = pulse;
            }
            else
            {
                glyph.Background = Brushes.Transparent;
            }
        }

        private void AnimateProgress(double ratio, bool hasError)
        {
            progressFill.Background = hasError
                ? (Brush)new SolidColorBrush(Color.FromRgb(255, 101, 118))
                : new LinearGradientBrush(Color.FromRgb(124, 107, 255), Color.FromRgb(51, 214, 200), 0);
            double trackWidth = progressTrack.ActualWidth;
            if (trackWidth <= 0)
            {
                return;
            }
            double current = Double.IsNaN(progressFill.Width) ? 0 : progressFill.Width;
            double destination = Math.Max(0, Math.Min(trackWidth, trackWidth * ratio));
            DoubleAnimation animation = new DoubleAnimation(current, destination, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += delegate
            {
                progressFill.BeginAnimation(FrameworkElement.WidthProperty, null);
                progressFill.Width = destination;
            };
            progressFill.BeginAnimation(FrameworkElement.WidthProperty, animation);
        }

        private void TryPlayAudio()
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(state.MusicPath) && File.Exists(state.MusicPath))
                {
                    player.Open(new Uri(Path.GetFullPath(state.MusicPath), UriKind.Absolute));
                    player.Volume = 1.0;
                    player.Play();
                    return;
                }
            }
            catch
            {
            }
            DisableStopSound("闹铃声音不可用");
            status.Text = "未能播放闹铃音乐，但打字倒计时已经开始。";
        }

        private void StopAudio()
        {
            try { player.Stop(); } catch { }
            DisableStopSound("闹铃声音已停止");
        }

        private void DisableStopSound(string label)
        {
            stopSound.Content = label;
            stopSound.IsHitTestVisible = false;
            stopSound.Opacity = 0.55;
        }

        private static void BlockClipboardShortcuts(object sender, KeyEventArgs args)
        {
            ModifierKeys modifiers = Keyboard.Modifiers;
            bool controlAction = (modifiers & ModifierKeys.Control) != 0 && (args.Key == Key.V || args.Key == Key.C || args.Key == Key.X || args.Key == Key.Insert);
            bool shiftPaste = (modifiers & ModifierKeys.Shift) != 0 && args.Key == Key.Insert;
            if (controlAction || shiftPaste || args.Key == Key.Apps)
            {
                args.Handled = true;
            }
        }

        private static CommandBinding BlockCommand(RoutedUICommand command)
        {
            CommandBinding binding = new CommandBinding(command);
            binding.CanExecute += delegate(object sender, CanExecuteRoutedEventArgs args) { args.CanExecute = false; args.Handled = true; };
            binding.Executed += delegate(object sender, ExecutedRoutedEventArgs args) { args.Handled = true; };
            return binding;
        }

        private static Border Card()
        {
            return new Border
            {
                Background = Brush("#D9151D31"),
                BorderBrush = Brush("#35FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18)
            };
        }

        private static ControlTemplate RoundedButtonTemplate()
        {
            ControlTemplate template = new ControlTemplate(typeof(Button));
            FrameworkElementFactory surface = new FrameworkElementFactory(typeof(Border), "Surface");
            surface.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            surface.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
            surface.SetValue(Border.BorderThicknessProperty, new TemplateBindingExtension(Control.BorderThicknessProperty));
            surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(22));
            surface.SetValue(Border.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            surface.AppendChild(presenter);
            template.VisualTree = surface;

            Trigger hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#FF39445D"), "Surface"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("#987C6BFF"), "Surface"));
            template.Triggers.Add(hover);

            Trigger pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BackgroundProperty, Brush("#FF222A3D"), "Surface"));
            template.Triggers.Add(pressed);
            return template;
        }

        private static Brush Brush(string value)
        {
            return (Brush)new BrushConverter().ConvertFromString(value);
        }
    }
}
