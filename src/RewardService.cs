using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace FocusHarbor
{
    public static class RewardService
    {
        public const int RewardMinutes = 10;
        public const int MinimumStatementLength = 80;
        public const int MaximumStatementLength = 300;

        public static RewardDailyState GetTodayState()
        {
            FocusConfig config;
            try { config = JsonStore.LoadConfig(); }
            catch { config = DefaultConfigFactory.Create(); }
            return GetTodayState(config);
        }

        public static RewardDailyState GetTodayState(FocusConfig config)
        {
            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            try
            {
                if (File.Exists(JsonStore.RewardStatePath))
                {
                    RewardDailyState state = JsonStore.Load<RewardDailyState>(JsonStore.RewardStatePath);
                    if (state != null && String.Equals(state.Date, today, StringComparison.Ordinal))
                    {
                        state.Claims = NormalizeClaims(state.Claims, GetSubjects(config));
                        return state;
                    }
                }
            }
            catch
            {
            }
            return new RewardDailyState { Version = 1, Date = today, Claims = new List<RewardClaim>() };
        }

        public static List<RewardSubjectConfig> GetSubjects(FocusConfig config)
        {
            IEnumerable<RewardSubjectConfig> subjects = config == null || config.RewardSubjects == null
                ? Enumerable.Empty<RewardSubjectConfig>()
                : config.RewardSubjects;
            return subjects
                .Where(subject => subject != null && !String.IsNullOrWhiteSpace(subject.Id))
                .Select(subject => new RewardSubjectConfig { Id = subject.Id, Name = subject.Name })
                .ToList();
        }

        public static bool CanManageSubjects()
        {
            DateTimeOffset? pauseUntil = RestrictionService.ReadPauseDeadline();
            return pauseUntil.HasValue && pauseUntil.Value > DateTimeOffset.Now;
        }

        public static OperationResult AddSubject()
        {
            return MutateSubjects(AddSubjectToConfig);
        }

        public static OperationResult RenameSubject(string subjectId, string requestedName)
        {
            string name = (requestedName ?? String.Empty).Trim();
            if (name.Length == 0 || name.Length > 24 || name.IndexOf('\r') >= 0 || name.IndexOf('\n') >= 0)
            {
                return OperationResult.Fail("科目名称需要为 1–24 个字符，且不能换行。", 2);
            }
            return MutateSubjects(delegate(FocusConfig current) { return RenameSubjectInConfig(current, subjectId, name); });
        }

        public static OperationResult DeleteSubject(string subjectId)
        {
            return MutateSubjects(delegate(FocusConfig current) { return DeleteSubjectFromConfig(current, subjectId); });
        }

        internal static OperationResult AddSubjectToConfig(FocusConfig current)
        {
            current.RewardSubjects = current.RewardSubjects ?? new List<RewardSubjectConfig>();
            int sequence = Math.Max(0, current.RewardSubjectSequence);
            string id;
            do
            {
                sequence++;
                id = "subject-" + sequence.ToString(CultureInfo.InvariantCulture);
            }
            while (current.RewardSubjects.Any(subject => subject != null && String.Equals(subject.Id, id, StringComparison.OrdinalIgnoreCase)));

            string name = "科目" + sequence.ToString(CultureInfo.InvariantCulture);
            current.RewardSubjectSequence = sequence;
            current.RewardSubjects.Add(new RewardSubjectConfig { Id = id, Name = name });
            return OperationResult.Ok(name + "已添加，可以点击铅笔按钮重命名。");
        }

        internal static OperationResult RenameSubjectInConfig(FocusConfig current, string subjectId, string name)
        {
            RewardSubjectConfig subject = (current.RewardSubjects ?? new List<RewardSubjectConfig>())
                .FirstOrDefault(item => item != null && String.Equals(item.Id, subjectId, StringComparison.OrdinalIgnoreCase));
            if (subject == null)
            {
                return OperationResult.Fail("这个科目已经不存在。", 2);
            }
            if ((current.RewardSubjects ?? new List<RewardSubjectConfig>()).Any(item =>
                item != null && !String.Equals(item.Id, subject.Id, StringComparison.OrdinalIgnoreCase) && String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return OperationResult.Fail("已经存在同名科目。", 2);
            }
            string previous = subject.Name;
            subject.Name = name;
            return OperationResult.Ok((previous ?? "科目") + "已重命名为" + name + "。");
        }

        internal static OperationResult DeleteSubjectFromConfig(FocusConfig current, string subjectId)
        {
            RewardSubjectConfig subject = (current.RewardSubjects ?? new List<RewardSubjectConfig>())
                .FirstOrDefault(item => item != null && String.Equals(item.Id, subjectId, StringComparison.OrdinalIgnoreCase));
            if (subject == null)
            {
                return OperationResult.Fail("这个科目已经不存在。", 2);
            }
            current.RewardSubjects.Remove(subject);
            return OperationResult.Ok((subject.Name ?? "科目") + "已删除；今天已经获得的奖励仍会保留。");
        }

        public static OperationResult Claim(string subjectId, string statement)
        {
            FocusConfig currentConfig;
            try { currentConfig = JsonStore.LoadConfig(); }
            catch (Exception exception) { return OperationResult.Fail("读取科目配置失败：" + exception.Message, 1); }
            RewardSubjectConfig configuredSubject = GetSubjects(currentConfig)
                .FirstOrDefault(subject => String.Equals(subject.Id, subjectId, StringComparison.OrdinalIgnoreCase));
            if (configuredSubject == null)
            {
                return OperationResult.Fail("未识别的科目。", 2);
            }
            string subjectName = configuredSubject.Name;
            string normalized = AlarmService.NormalizeText(statement).Trim();
            if (normalized.Length < MinimumStatementLength || normalized.Length > MaximumStatementLength)
            {
                return OperationResult.Fail("请手写 " + MinimumStatementLength.ToString(CultureInfo.InvariantCulture) + "–" + MaximumStatementLength.ToString(CultureInfo.InvariantCulture) + " 字；当前为 " + normalized.Length.ToString(CultureInfo.InvariantCulture) + " 字。", 2);
            }

            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborDailyRewards"))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(10));
                    if (!taken)
                    {
                        return OperationResult.Fail("奖励记录正在更新，请稍后重试。", 1);
                    }
                    currentConfig = JsonStore.LoadConfig();
                    configuredSubject = GetSubjects(currentConfig)
                        .FirstOrDefault(subject => String.Equals(subject.Id, subjectId, StringComparison.OrdinalIgnoreCase));
                    if (configuredSubject == null)
                    {
                        return OperationResult.Fail("这个科目已经被删除，无法领取奖励。", 2);
                    }
                    subjectName = configuredSubject.Name;
                    RewardDailyState state = GetTodayState();
                    if (state.Claims.Any(item => item != null && String.Equals(item.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return OperationResult.Fail(subjectName + "今天已经确认过，不能重复领取。", 2);
                    }
                    state.Claims.Add(new RewardClaim
                    {
                        SubjectId = subjectId,
                        SubjectName = subjectName,
                        Statement = normalized,
                        ClaimedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
                        Used = false,
                        UsedAt = String.Empty
                    });
                    JsonStore.Save(JsonStore.RewardStatePath, state);
                    JsonStore.AppendLog("Earned one 10-minute reward for " + subjectName + ".");
                    return OperationResult.Ok(subjectName + "已确认，获得一份 10 分钟临时解除奖励。");
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Reward claim failed: " + exception.Message);
                    return OperationResult.Fail("领取奖励失败：" + exception.Message, 1);
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

        public static OperationResult UseOne(FocusConfig config, string executablePath)
        {
            using (Mutex mutex = new Mutex(false, @"Local\FocusHarborDailyRewards"))
            {
                bool taken = false;
                RewardDailyState state = null;
                RewardClaim claim = null;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(10));
                    if (!taken)
                    {
                        return OperationResult.Fail("奖励记录正在更新，请稍后重试。", 1);
                    }
                    state = GetTodayState();
                    claim = state.Claims.FirstOrDefault(item => item != null && !item.Used);
                    if (claim == null)
                    {
                        return OperationResult.Fail("今天没有待使用的 10 分钟奖励。", 2);
                    }

                    claim.Used = true;
                    claim.UsedAt = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture);
                    JsonStore.Save(JsonStore.RewardStatePath, state);

                    OperationResult pause = RestrictionService.PauseWithReward(config, executablePath, RewardMinutes);
                    if (!pause.Success)
                    {
                        claim.Used = false;
                        claim.UsedAt = String.Empty;
                        JsonStore.Save(JsonStore.RewardStatePath, state);
                        return pause;
                    }
                    JsonStore.AppendLog("Used the 10-minute reward earned for " + claim.SubjectName + ".");
                    return pause;
                }
                catch (Exception exception)
                {
                    if (state != null && claim != null && claim.Used)
                    {
                        try
                        {
                            claim.Used = false;
                            claim.UsedAt = String.Empty;
                            JsonStore.Save(JsonStore.RewardStatePath, state);
                        }
                        catch
                        {
                        }
                    }
                    JsonStore.AppendLog("Reward use failed: " + exception.Message);
                    return OperationResult.Fail("使用奖励失败：" + exception.Message, 1);
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

        public static string GetSubjectName(string subjectId)
        {
            try
            {
                RewardSubjectConfig subject = GetSubjects(JsonStore.LoadConfig())
                    .FirstOrDefault(item => String.Equals(item.Id, subjectId, StringComparison.OrdinalIgnoreCase));
                return subject == null ? "科目" : subject.Name;
            }
            catch
            {
                return "科目";
            }
        }

        private static OperationResult MutateSubjects(Func<FocusConfig, OperationResult> mutation)
        {
            if (!CanManageSubjects())
            {
                return OperationResult.Fail("科目只能在限制临时解除期间添加、删除或重命名。", 3);
            }
            using (Mutex mutex = new Mutex(false, MutexSafety.ConfigMutexName))
            {
                bool taken = false;
                try
                {
                    taken = MutexSafety.Wait(mutex, TimeSpan.FromSeconds(15));
                    if (!taken)
                    {
                        return OperationResult.Fail("科目配置正在更新，请稍后重试。", 1);
                    }
                    if (!CanManageSubjects())
                    {
                        return OperationResult.Fail("临时解除已经结束，科目配置没有更改。", 3);
                    }
                    FocusConfig current = JsonStore.LoadConfig();
                    OperationResult result = mutation(current);
                    if (!result.Success)
                    {
                        return result;
                    }
                    JsonStore.SaveConfig(current);
                    JsonStore.AppendLog("Reward subject configuration changed: " + result.Message);
                    return result;
                }
                catch (Exception exception)
                {
                    JsonStore.AppendLog("Reward subject update failed: " + exception.Message);
                    return OperationResult.Fail("修改科目失败：" + exception.Message, 1);
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

        private static List<RewardClaim> NormalizeClaims(IEnumerable<RewardClaim> claims, IEnumerable<RewardSubjectConfig> subjects)
        {
            List<RewardClaim> result = new List<RewardClaim>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, RewardSubjectConfig> subjectById = (subjects ?? Enumerable.Empty<RewardSubjectConfig>())
                .Where(subject => subject != null && !String.IsNullOrWhiteSpace(subject.Id))
                .GroupBy(subject => subject.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (RewardClaim claim in claims ?? Enumerable.Empty<RewardClaim>())
            {
                if (claim == null || String.IsNullOrWhiteSpace(claim.SubjectId) || !seen.Add(claim.SubjectId))
                {
                    continue;
                }
                RewardSubjectConfig configured;
                if (subjectById.TryGetValue(claim.SubjectId, out configured))
                {
                    claim.SubjectName = configured.Name;
                }
                else if (String.IsNullOrWhiteSpace(claim.SubjectName))
                {
                    claim.SubjectName = "已删除科目";
                }
                claim.Statement = claim.Statement ?? String.Empty;
                claim.ClaimedAt = claim.ClaimedAt ?? String.Empty;
                claim.UsedAt = claim.UsedAt ?? String.Empty;
                result.Add(claim);
            }
            return result;
        }
    }
}
