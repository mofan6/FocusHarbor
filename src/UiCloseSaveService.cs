using System;
using System.IO;
using System.Threading;

namespace FocusHarbor
{
    internal sealed class UiCloseSaveRequest
    {
        public FocusConfig Configuration;
        public bool SecurityEdited;
        public AlarmConfig OriginalAlarm;
        public AlarmConfig PendingAlarm;
    }

    internal static class UiCloseSaveService
    {
        internal static bool AlarmFieldsEqual(AlarmConfig left, AlarmConfig right)
        {
            if (left == null || right == null) return left == right;
            return left.Enabled == right.Enabled && left.Hour == right.Hour && left.Minute == right.Minute &&
                String.Equals(left.MusicPath ?? String.Empty, right.MusicPath ?? String.Empty, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(left.DepositFolder ?? String.Empty, right.DepositFolder ?? String.Empty, StringComparison.OrdinalIgnoreCase) &&
                String.Equals(AlarmService.NormalizeText(left.ChallengeText).Trim(), AlarmService.NormalizeText(right.ChallengeText).Trim(), StringComparison.Ordinal);
        }

        // Apply only actual drafts to the latest stored state. Background discovery,
        // reward edits and alarm changes made since the UI opened remain intact.
        internal static FocusConfig MergeDraft(UiCloseSaveRequest request, FocusConfig latest, bool focusLocked)
        {
            FocusConfig result = JsonStore.CloneConfig(latest);
            if (request.SecurityEdited && !focusLocked)
            {
                FocusConfig draft = JsonStore.CloneConfig(request.Configuration);
                DiscoveryService.MergeBrowserTargets(draft, latest.BrowserTargets);
                JsonStore.ApplySecurity(result, JsonStore.CaptureSecurity(draft));
                result.BrowserDiscoveryCompleted = draft.BrowserDiscoveryCompleted || latest.BrowserDiscoveryCompleted;
            }
            if (request.PendingAlarm != null)
            {
                if (!AlarmFieldsEqual(latest.Alarm, request.OriginalAlarm))
                    throw new InvalidOperationException("闹钟已在后台更新，请重新打开晨间闹钟页面后保存。");
                result.Alarm = new AlarmConfig
                {
                    Enabled = request.PendingAlarm.Enabled,
                    Hour = request.PendingAlarm.Hour,
                    Minute = request.PendingAlarm.Minute,
                    MusicPath = request.PendingAlarm.MusicPath,
                    DepositFolder = request.PendingAlarm.DepositFolder,
                    DepositFolderPolicyVersion = request.PendingAlarm.DepositFolderPolicyVersion,
                    ChallengeText = request.PendingAlarm.ChallengeText,
                    ChallengeMinutes = 15,
                    QuarantineHours = 120
                };
            }
            return result;
        }

        internal static FocusConfig Save(UiCloseSaveRequest request, string executablePath)
        {
            bool scheduleApplied = false;
            try
            {
                // Folder validation and UAC can take time. Do not hold the shared
                // configuration mutex while waiting for either operation.
                if (request.PendingAlarm != null)
                {
                    AlarmService.ValidateAlarmConfig(request.PendingAlarm, request.PendingAlarm.Enabled);
                    FocusConfig beforeSchedule = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                    FocusConfig nextSchedule = MergeDraft(request, beforeSchedule, RestrictionService.IsFocusLocked(beforeSchedule));
                    AlarmConfig previous = beforeSchedule.Alarm;
                    AlarmConfig next = nextSchedule.Alarm;
                    if (previous == null || previous.Enabled != next.Enabled || previous.Hour != next.Hour || previous.Minute != next.Minute)
                    {
                        AlarmService.ApplySchedule(nextSchedule, executablePath, true);
                        scheduleApplied = true;
                    }
                }

                FocusConfig saved;
                using (Mutex mutex = new Mutex(false, MutexSafety.ConfigMutexName))
                {
                    bool taken = false;
                    try
                    {
                        taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                        if (!taken) throw new IOException("配置正在更新，请稍后重试关闭。");
                        FocusConfig latest = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                        saved = MergeDraft(request, latest, RestrictionService.IsFocusLocked(latest));
                        bool securityChanged = !JsonStore.SecurityEquals(saved, JsonStore.CaptureSecurity(latest));
                        bool alarmChanged = request.PendingAlarm != null && !AlarmFieldsEqual(saved.Alarm, latest.Alarm);
                        if (securityChanged || alarmChanged)
                            RestrictionService.ApplyConfig(saved, executablePath);
                        // An unchanged close already has durable configuration.
                        // In particular, never re-register the guard task here.
                    }
                    finally
                    {
                        if (taken) mutex.ReleaseMutex();
                    }
                }
                FocusTimeService.Update(RestrictionService.IsFocusLocked(saved), true);
                return saved;
            }
            catch (Exception exception)
            {
                AlarmScheduleException scheduleException = exception as AlarmScheduleException;
                if (scheduleApplied || (scheduleException != null && scheduleException.MayHaveChangedTasks))
                {
                    try { AlarmService.ApplySchedule(JsonStore.LoadConfig(), executablePath, true); }
                    catch (Exception rollbackException)
                    {
                        JsonStore.AppendLog("Close-save alarm schedule rollback failed: " + rollbackException);
                        throw new InvalidOperationException(exception.Message + "；闹钟计划恢复失败：" + rollbackException.Message, exception);
                    }
                }
                throw;
            }
        }
    }
}
