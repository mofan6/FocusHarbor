using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FocusHarbor
{
    public sealed class MainWindowController
    {
        private readonly Window window;
        private readonly string executablePath;
        private FocusConfig config;
        private ObservableCollection<string> allowlist;
        private ObservableCollection<AppRuleViewModel> appRuleItems;
        private List<InstalledSoftwareEntry> installedSoftware;
        private readonly string tutorialMarkdown;
        private bool closingSaveStarted;
        private bool closingSaveCompleted;
        private bool restrictionOperationRunning;
        private bool softwareDiscoveryRunning;
        private FocusSecuritySnapshot savedUiSecurity;
        private readonly Func<UiCloseSaveRequest, string, FocusConfig> closeSave;
        private readonly DispatcherTimer statusTimer;
        private DispatcherTimer toastTimer;
        private string rewardStateDate = String.Empty;
        private int rewardEarnedDisplayed = -1;
        private bool? rewardManageDisplayed;

        private readonly Grid overviewPage;
        private readonly Grid browserPage;
        private readonly Grid appsPage;
        private readonly Grid alarmPage;
        private readonly Grid rewardsPage;
        private readonly Grid settingsPage;
        private readonly Button overviewNav;
        private readonly Button browserNav;
        private readonly Button appsNav;
        private readonly Button alarmNav;
        private readonly Button rewardsNav;
        private readonly Button settingsNav;
        private readonly TextBlock pageTitle;

        public MainWindowController(Window window, FocusConfig config, string executablePath, bool persistenceEnabled)
            : this(window, config, executablePath, persistenceEnabled, UiCloseSaveService.Save)
        {
        }

        internal MainWindowController(Window window, FocusConfig config, string executablePath, bool persistenceEnabled,
            Func<UiCloseSaveRequest, string, FocusConfig> closeSave)
        {
            this.window = window;
            this.config = config;
            this.executablePath = executablePath;
            this.closeSave = closeSave;
            tutorialMarkdown = LoadEmbeddedTutorialMarkdown();

            overviewPage = Find<Grid>("OverviewPage");
            browserPage = Find<Grid>("BrowserPage");
            appsPage = Find<Grid>("AppsPage");
            alarmPage = Find<Grid>("AlarmPage");
            rewardsPage = Find<Grid>("RewardsPage");
            settingsPage = Find<Grid>("SettingsPage");
            overviewNav = Find<Button>("OverviewNav");
            browserNav = Find<Button>("BrowserNav");
            appsNav = Find<Button>("AppsNav");
            alarmNav = Find<Button>("AlarmNav");
            rewardsNav = Find<Button>("RewardsNav");
            settingsNav = Find<Button>("SettingsNav");
            pageTitle = Find<TextBlock>("PageTitle");

            AttachWindowControls();
            AttachNavigation();
            AttachActions();
            LoadConfigIntoUi();
            UpdateStatus();

            statusTimer = new DispatcherTimer(DispatcherPriority.Background);
            statusTimer.Interval = TimeSpan.FromSeconds(1);
            statusTimer.Tick += delegate { UpdateStatus(); };
            statusTimer.Start();

            window.SourceInitialized += delegate { ApplyDwmEffects(); };
            if (persistenceEnabled)
            {
                window.Closing += SaveBeforeClose;
            }
            window.Closed += delegate { statusTimer.Stop(); };
        }

        public static Window LoadWindow()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("FocusHarbor.MainWindow.xaml"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Embedded XAML resource was not found.");
                }
                return (Window)XamlReader.Load(stream);
            }
        }

        private void AttachWindowControls()
        {
            Grid titleBar = Find<Grid>("TitleBar");
            titleBar.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args)
            {
                if (args.ClickCount == 2)
                {
                    ToggleMaximize();
                }
                else if (args.LeftButton == MouseButtonState.Pressed)
                {
                    window.DragMove();
                }
            };

            Find<Button>("MinimizeButton").Click += delegate { window.WindowState = WindowState.Minimized; };
            Find<Button>("MaximizeButton").Click += delegate { ToggleMaximize(); };
            Find<Button>("CloseButton").Click += delegate { window.Close(); };
        }

        private void AttachNavigation()
        {
            overviewNav.Click += delegate { Navigate("总览", overviewPage, overviewNav); };
            browserNav.Click += delegate { Navigate("浏览器", browserPage, browserNav); };
            appsNav.Click += delegate { Navigate("应用守卫", appsPage, appsNav); };
            alarmNav.Click += delegate { Navigate("晨间闹钟", alarmPage, alarmNav); };
            rewardsNav.Click += delegate { Navigate("学习奖励", rewardsPage, rewardsNav); RefreshRewardPage(); };
            settingsNav.Click += delegate { Navigate("设置", settingsPage, settingsNav); };
        }

        private void AttachActions()
        {
            Find<Button>("PauseNowButton").Click += delegate { StartTemporaryPause(); };
            Find<Button>("ResumeFocusButton").Click += delegate { ResumeFocusManually(); };
            Find<Button>("ForfeitAllowanceButton").Click += delegate { ForfeitAllowanceManually(); };
            Find<Button>("RefreshInstalledAppsButton").Click += delegate { RefreshInstalledSoftware(true); };
            Find<Button>("AddDomainButton").Click += delegate { AddDomain(); };
            Find<Button>("RemoveDomainButton").Click += delegate { RemoveSelectedDomain(); };
            Find<Button>("ApplyBrowserButton").Click += delegate { ApplyBrowserSettings(); };
            Find<Button>("ApplyAppsButton").Click += delegate { ApplyAppSettings(); };
            Find<Button>("AddInstalledAppButton").Click += delegate { AddInstalledApplication(); };
            Find<Button>("AddFileButton").Click += delegate { AddGuardedFile(); };
            Find<Button>("RemoveAppRuleButton").Click += delegate { RemoveSelectedAppRule(); };
            Find<Button>("ReapplyButton").Click += delegate { ReapplyAll(); };
            Find<Button>("RestoreAllButton").Click += delegate { RestoreAllNow(); };
            Find<Button>("ChooseAlarmMusicButton").Click += delegate { ChooseAlarmMusic(); };
            Find<Button>("ChooseDepositFolderButton").Click += delegate { ChooseDepositFolder(); };
            Find<Button>("SaveAlarmButton").Click += delegate { SaveAlarmSettings(); };
            Find<Button>("RefreshQuarantineButton").Click += delegate { RefreshQuarantineList(); };
            Find<Button>("RestoreQuarantineButton").Click += delegate { RestoreSelectedQuarantine(); };
            Find<Button>("UseRewardButton").Click += delegate { UseStudyReward(); };
            Find<Button>("AddRewardSubjectButton").Click += delegate { AddRewardSubject(); };
            Find<ItemsControl>("RewardSubjects").AddHandler(Button.ClickEvent, new RoutedEventHandler(HandleRewardSubjectClick));

            Find<TextBox>("DomainInput").KeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Enter)
                {
                    AddDomain();
                    args.Handled = true;
                }
            };

            Find<ListBox>("AppRulesBox").SelectionChanged += delegate { UpdateRemoveRuleButton(); };
            Find<ListBox>("AppRulesBox").AddHandler(Button.ClickEvent, new RoutedEventHandler(HandleAppRuleButtonClick));
            Find<TextBox>("TutorialSearchBox").TextChanged += delegate { FilterTutorials(); };
            Find<TextBox>("AlarmChallengeText").TextChanged += delegate { UpdateAlarmCharacterCount(); };
            Find<ListBox>("QuarantineList").SelectionChanged += delegate { UpdateQuarantineRestoreButton(); };
        }

        private void LoadConfigIntoUi()
        {
            allowlist = new ObservableCollection<string>(RestrictionService.NormalizeAllowlist(config.BrowserAllowlist));
            ListBox allowlistBox = Find<ListBox>("AllowlistBox");
            allowlistBox.ItemsSource = allowlist;

            Find<CheckBox>("BrowserEnabledToggle").IsChecked = config.BrowserRestrictionEnabled;
            Find<CheckBox>("AppGuardEnabledToggle").IsChecked = config.AppGuardEnabled;

            appRuleItems = new ObservableCollection<AppRuleViewModel>(
                (config.AppRules ?? new List<AppRuleConfig>()).Where(rule => rule != null).Select(rule => new AppRuleViewModel(rule)));
            Find<ListBox>("AppRulesBox").ItemsSource = appRuleItems;

            if (installedSoftware == null) installedSoftware = new List<InstalledSoftwareEntry>();
            ComboBox softwarePicker = Find<ComboBox>("InstalledSoftwarePicker");
            softwarePicker.ItemsSource = installedSoftware;
            if (installedSoftware.Count > 0)
            {
                softwarePicker.SelectedIndex = 0;
            }

            Find<TextBlock>("AllowlistCountText").Text = allowlist.Count.ToString(CultureInfo.InvariantCulture);
            Find<TextBlock>("BrowserCoverageText").Text = "已覆盖电脑中发现的 " + (config.BrowserTargets == null ? 0 : config.BrowserTargets.Count).ToString(CultureInfo.InvariantCulture) + " 个浏览器；本地 HTML、PDF 保持可用。";
            Find<TextBlock>("PauseMinutesText").Text = config.PauseMinutes.ToString(CultureInfo.InvariantCulture) + " 分钟";
            Find<TextBlock>("DailyPauseLimitText").Text = config.DailyPauseLimit.ToString(CultureInfo.InvariantCulture) + " 次";
            Find<CheckBox>("AlarmEnabledToggle").IsChecked = config.Alarm != null && config.Alarm.Enabled;
            AlarmConfig alarm = config.Alarm ?? DefaultConfigFactory.Create().Alarm;
            Find<TextBox>("AlarmTimeInput").Text = alarm.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + alarm.Minute.ToString("00", CultureInfo.InvariantCulture);
            Find<TextBox>("AlarmMusicPathText").Text = alarm.MusicPath ?? String.Empty;
            Find<TextBox>("AlarmDepositFolderText").Text = alarm.DepositFolder ?? String.Empty;
            Find<TextBox>("AlarmChallengeText").Text = alarm.ChallengeText ?? String.Empty;
            UpdateAlarmCharacterCount();
            RefreshQuarantineList();
            RefreshRewardPage();
            FilterTutorials();

            int tier = RenderCapability.Tier >> 16;
            Find<TextBlock>("GpuTierText").Text = "DirectX GPU Tier " + tier.ToString(CultureInfo.InvariantCulture);
            savedUiSecurity = JsonStore.CaptureSecurity(config);
            RefreshInstalledSoftware(false);
        }

        private void UpdateStatus()
        {
            FocusStatus status = RestrictionService.GetStatus(config, executablePath);
            bool protectionsConfigured = config.BrowserRestrictionEnabled || config.AppGuardEnabled;
            bool focusActive = status.ConfigurationLocked;
            TextBlock heroBadge = Find<TextBlock>("HeroBadge");
            TextBlock heroSubtitle = Find<TextBlock>("HeroSubtitle");
            TextBlock countdown = Find<TextBlock>("CountdownText");
            TextBlock countdownCaption = Find<TextBlock>("CountdownCaption");
            Button pauseButton = Find<Button>("PauseNowButton");
            System.Windows.Shapes.Ellipse pauseRing = Find<System.Windows.Shapes.Ellipse>("PauseRing");

            if (status.TemporaryPauseActive && status.PauseUntil.HasValue)
            {
                TimeSpan remaining = status.PauseUntil.Value - DateTimeOffset.Now;
                if (remaining < TimeSpan.Zero)
                {
                    remaining = TimeSpan.Zero;
                }
                countdown.Text = ((int)remaining.TotalMinutes).ToString("00") + ":" + remaining.Seconds.ToString("00");
                countdownCaption.Text = "距离自动恢复";
                heroBadge.Text = "●  临时解除中";
                heroBadge.Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 132));
                heroSubtitle.Text = "所有限制暂时停止，到期后自动恢复原配置。";
                pauseButton.Content = "重新计时 30 分钟";
                pauseRing.Stroke = new SolidColorBrush(Color.FromRgb(255, 184, 108));
            }
            else if (focusActive)
            {
                countdown.Text = "专注中";
                countdownCaption.Text = "限制保持";
                heroBadge.Text = "●  防护已启用";
                heroBadge.Foreground = new SolidColorBrush(Color.FromRgb(189, 249, 233));
                heroSubtitle.Text = "浏览器白名单与应用守卫正在协同工作。";
                pauseButton.Content = "暂时解除 30 分钟";
                pauseRing.Stroke = new SolidColorBrush(Color.FromRgb(83, 217, 203));
            }
            else
            {
                countdown.Text = "待开启";
                countdownCaption.Text = "前往设置";
                heroBadge.Text = "●  等待开启";
                heroBadge.Foreground = new SolidColorBrush(Color.FromRgb(193, 202, 220));
                heroSubtitle.Text = "所有防护默认关闭；完成配置后由你决定何时开始专注。";
                pauseButton.Content = "暂无可解除限制";
                pauseRing.Stroke = new SolidColorBrush(Color.FromRgb(75, 87, 110));
            }

            pauseButton.IsEnabled = protectionsConfigured && status.RemainingToday > 0;
            DateTimeOffset alarmBlockUntil;
            if (AlarmService.IsTemporaryPauseBlockedNow(out alarmBlockUntil))
            {
                pauseButton.Content = "今日临时解除已锁定";
            }
            else if (status.DailyAllowanceForfeited) pauseButton.Content = "今日额度已放弃";
            Find<Button>("ResumeFocusButton").Visibility = status.TemporaryPauseActive ? Visibility.Visible : Visibility.Collapsed;
            Find<Button>("ResumeFocusButton").IsEnabled = status.TemporaryPauseActive && !restrictionOperationRunning;
            Find<Button>("ForfeitAllowanceButton").IsEnabled = !status.DailyAllowanceForfeited && status.RemainingToday > 0 && !restrictionOperationRunning;
            Find<Button>("ForfeitAllowanceButton").Content = status.DailyAllowanceForfeited ? "今日额度已放弃" : "放弃今日剩余额度";
            Find<TextBlock>("DailyAllowanceText").Text = status.DailyAllowanceForfeited ? "今日已放弃 · 明日重置" : "今日剩余 " + status.RemainingToday.ToString(CultureInfo.InvariantCulture) + " 次";
            SetUsageDots(status.DailyAllowanceForfeited ? config.DailyPauseLimit : status.UsedToday, config.DailyPauseLimit);

            AlarmConfig alarm = config.Alarm ?? DefaultConfigFactory.Create().Alarm;
            Find<TextBlock>("AlarmScheduleStatusText").Text = alarm.Enabled
                ? "每天 " + alarm.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + alarm.Minute.ToString("00", CultureInfo.InvariantCulture) + " · 最高权限双任务唤醒 · 15 分钟打字"
                : "未启用 · 开启时会请求管理员授权并配置双任务唤醒";

            Find<TextBlock>("BrowserStatusLarge").Text = !config.BrowserRestrictionEnabled ? "尚未开启" : (status.TemporaryPauseActive ? "暂时解除" : "限制已启用");
            Find<TextBlock>("BrowserStatusDetail").Text = status.AllowedUrlCount.ToString(CultureInfo.InvariantCulture) + " 条规则 · " + status.DiscoveredBrowserCount.ToString(CultureInfo.InvariantCulture) + " 个浏览器";
            Find<TextBlock>("BrowserPreviewText").Text = BuildBrowserPreview();

            int enabledRules = config.AppRules == null ? 0 : config.AppRules.Count(rule => rule != null && rule.Enabled && !String.Equals(rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase));
            Find<TextBlock>("GuardStatusLarge").Text = !config.AppGuardEnabled ? "尚未开启" : (status.TemporaryPauseActive ? "暂时解除" : "守卫运行中");
            Find<TextBlock>("AppPreviewText").Text = BuildAppPreview();
            Find<TextBlock>("AppStatusDetail").Text = enabledRules.ToString(CultureInfo.InvariantCulture) + " 条启用 · " + (config.AppRules == null ? 0 : config.AppRules.Count).ToString(CultureInfo.InvariantCulture) + " 条已设置";

            long totalSeconds = FocusTimeService.Update(focusActive, false);
            Find<TextBlock>("FocusTotalText").Text = FormatFocusDuration(totalSeconds);
            Find<TextBlock>("FocusAccountingText").Text = focusActive ? "正在计入本次专注时间" : (status.TemporaryPauseActive ? "临时解除期间暂停累计" : "开启任一防护后开始累计");
            UpdateConfigurationLock(status);

            TextBlock sidebarStatus = Find<TextBlock>("SidebarStatus");
            System.Windows.Shapes.Ellipse sidebarDot = Find<System.Windows.Shapes.Ellipse>("SidebarStatusDot");
            if (status.TemporaryPauseActive)
            {
                sidebarStatus.Text = "临时解除中";
                sidebarDot.Fill = new SolidColorBrush(Color.FromRgb(255, 184, 108));
            }
            else if (focusActive)
            {
                sidebarStatus.Text = "专注记录中";
                sidebarDot.Fill = new SolidColorBrush(Color.FromRgb(51, 214, 200));
            }
            else
            {
                sidebarStatus.Text = "等待开启";
                sidebarDot.Fill = new SolidColorBrush(Color.FromRgb(105, 119, 147));
            }

            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            bool canManageRewards = RewardService.CanManageSubjects();
            if (!String.Equals(rewardStateDate, today, StringComparison.Ordinal) || rewardManageDisplayed != canManageRewards)
            {
                RefreshRewardPage();
            }
            else
            {
                UpdateRewardSummary(RewardService.GetTodayState());
            }
        }

        private void SetUsageDots(int used, int limit)
        {
            for (int index = 1; index <= 4; index++)
            {
                System.Windows.Shapes.Ellipse dot = Find<System.Windows.Shapes.Ellipse>("UsageDot" + index.ToString(CultureInfo.InvariantCulture));
                dot.Fill = index <= used
                    ? new SolidColorBrush(Color.FromRgb(124, 107, 255))
                    : new SolidColorBrush(Color.FromRgb(45, 53, 73));
                dot.Opacity = index <= limit ? 1.0 : 0.28;
                dot.Visibility = index <= limit ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void StartTemporaryPause()
        {
            Button button = Find<Button>("PauseNowButton");
            button.IsEnabled = false;
            object previousContent = button.Content;
            button.Content = "正在应用…";
            window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));

            OperationResult result = RestrictionService.PauseAll(config, executablePath);
            button.Content = previousContent;
            ShowToast(result.Message, result.Success);
            UpdateStatus();
        }

        private void ResumeFocusManually()
        {
            if (restrictionOperationRunning || !RestrictionService.ReadPauseDeadline().HasValue) return;
            CaptureUiConfiguration();
            UiCloseSaveRequest draft = new UiCloseSaveRequest
            {
                Configuration = JsonStore.CloneConfig(config),
                SecurityEdited = !JsonStore.SecurityEquals(config, savedUiSecurity),
                OriginalAlarm = CloneAlarmConfig(config.Alarm ?? DefaultConfigFactory.Create().Alarm)
            };
            FocusConfig persisted = null;
            RunRestrictionOperation(delegate
            {
                // Save pending browser/app edits while the pause is still active,
                // before the settings are sealed again. Alarm drafts stay in UI.
                persisted = UiCloseSaveService.Save(draft, executablePath);
                return RestrictionService.ResumeFocusNow(executablePath);
            }, delegate
            {
                config = persisted;
                savedUiSecurity = JsonStore.CaptureSecurity(config);
            });
        }

        private void ForfeitAllowanceManually()
        {
            if (restrictionOperationRunning) return;
            MessageBoxResult confirmation = MessageBox.Show(window,
                "确定放弃今天剩余的全部 30 分钟普通解除额度吗？\n\n确认后，今天无法撤销，关闭或重启软件也不会恢复；明天 00:00 自动重置。\n当前正在进行的临时解除和独立的学习奖励不受影响。如需立即专注，请另点“立即恢复专注”。",
                "放弃今日额度 · 当天不可撤销", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
            RunRestrictionOperation(delegate { return RestrictionService.ForfeitTodayAllowance(); }, null);
        }

        private void RunRestrictionOperation(Func<OperationResult> operation, Action onSuccess)
        {
            if (restrictionOperationRunning || closingSaveStarted) return;
            restrictionOperationRunning = true;
            statusTimer.Stop();
            window.IsEnabled = false;
            try
            {
                Thread worker = new Thread(delegate()
                {
                    OperationResult result;
                    try { result = operation(); }
                    catch (Exception exception)
                    {
                        JsonStore.AppendLog("Manual focus operation failed: " + exception);
                        result = OperationResult.Fail("操作未完成，请重试：" + exception.Message, 1);
                    }
                    window.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        restrictionOperationRunning = false;
                        window.IsEnabled = true;
                        statusTimer.Start();
                        if (result.Success && onSuccess != null) onSuccess();
                        ShowToast(result.Message, result.Success);
                        UpdateStatus();
                    }));
                });
                worker.IsBackground = true;
                worker.SetApartmentState(ApartmentState.STA);
                worker.Start();
            }
            catch (Exception exception)
            {
                restrictionOperationRunning = false;
                window.IsEnabled = true;
                statusTimer.Start();
                ShowToast("无法开始操作，请重试：" + exception.Message, false);
            }
        }

        private void RefreshInstalledSoftware(bool report)
        {
            if (softwareDiscoveryRunning || closingSaveStarted) return;
            softwareDiscoveryRunning = true;
            Find<Button>("RefreshInstalledAppsButton").IsEnabled = false;
            string[] ruleIcons = (config.AppRules ?? new List<AppRuleConfig>())
                .Where(rule => String.Equals(rule.TargetKind, "PackagedApplication", StringComparison.OrdinalIgnoreCase))
                .Select(rule => rule.TargetPath).ToArray();
            Thread worker = new Thread(delegate()
            {
                List<InstalledSoftwareEntry> discovered;
                string error = null;
                try
                {
                    discovered = DiscoveryService.DiscoverInstalledSoftware();
                    foreach (InstalledSoftwareEntry app in discovered) { ImageSource icon = app.IconSource; GC.KeepAlive(icon); }
                    foreach (string id in ruleIcons) IconService.GetPackagedAppIcon(id);
                }
                catch (Exception exception) { discovered = new List<InstalledSoftwareEntry>(); error = exception.Message; }
                window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    softwareDiscoveryRunning = false;
                    if (closingSaveStarted || closingSaveCompleted) return;
                    ComboBox picker = Find<ComboBox>("InstalledSoftwarePicker");
                    InstalledSoftwareEntry selected = picker.SelectedItem as InstalledSoftwareEntry;
                    installedSoftware = discovered;
                    picker.ItemsSource = installedSoftware;
                    picker.SelectedItem = selected == null ? null : installedSoftware.FirstOrDefault(app =>
                        String.Equals(app.ExecutablePath, selected.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals(app.AppUserModelId, selected.AppUserModelId, StringComparison.OrdinalIgnoreCase));
                    if (picker.SelectedItem == null && installedSoftware.Count > 0) picker.SelectedIndex = 0;
                    foreach (AppRuleViewModel item in appRuleItems) item.Refresh();
                    Find<Button>("RefreshInstalledAppsButton").IsEnabled = !RestrictionService.IsFocusLocked(config);
                    if (report) ShowToast(error == null ? "已刷新 " + installedSoftware.Count.ToString(CultureInfo.InvariantCulture) + " 个软件，其中商店应用 " + installedSoftware.Count(app => app.IsPackaged).ToString(CultureInfo.InvariantCulture) + " 个。" : "刷新失败：" + error, error == null);
                }));
            });
            worker.IsBackground = true;
            worker.SetApartmentState(ApartmentState.STA);
            try { worker.Start(); }
            catch { softwareDiscoveryRunning = false; throw; }
        }

        private void RefreshRewardPage()
        {
            RewardDailyState state = RewardService.GetTodayState(config);
            rewardStateDate = state.Date ?? String.Empty;
            bool canManage = RewardService.CanManageSubjects();
            rewardManageDisplayed = canManage;
            Find<Button>("AddRewardSubjectButton").IsEnabled = canManage;
            Find<TextBlock>("RewardSubjectManageHint").Text = canManage
                ? "临时解除生效中：现在可以添加、删除或重命名科目"
                : "仅在临时解除限制期间可以添加、删除或重命名";
            List<RewardSubjectViewModel> subjects = new List<RewardSubjectViewModel>();
            List<RewardSubjectConfig> configured = RewardService.GetSubjects(config);
            for (int index = 0; index < configured.Count; index++)
            {
                AddRewardSubject(subjects, state, configured[index], index, canManage);
            }
            Find<ItemsControl>("RewardSubjects").ItemsSource = subjects;
            UpdateRewardSummary(state);
        }

        private static void AddRewardSubject(List<RewardSubjectViewModel> target, RewardDailyState state, RewardSubjectConfig configured, int index, bool canManage)
        {
            RewardClaim claim = (state.Claims ?? new List<RewardClaim>())
                .FirstOrDefault(item => item != null && String.Equals(item.SubjectId, configured.Id, StringComparison.OrdinalIgnoreCase));
            string name = String.IsNullOrWhiteSpace(configured.Name) ? "未命名科目" : configured.Name;
            target.Add(new RewardSubjectViewModel
            {
                Id = configured.Id,
                Name = name,
                Badge = GetRewardBadge(name, index),
                Description = "为今天的" + name + "任务负责，完成后再诚实确认。",
                Status = claim == null ? "待确认今日任务" : (claim.Used ? "今日奖励已使用" : "已获得 10 分钟奖励"),
                Accent = index % 2 == 0 ? "#FF6FE1D5" : "#FFB8AFFF",
                ActionText = claim == null ? "确认完成并手写说明" : (claim.Used ? "今天已完成" : "奖励待使用"),
                ActionEnabled = claim == null,
                ManageEnabled = canManage
            });
        }

        private static string GetRewardBadge(string name, int index)
        {
            if (!String.IsNullOrWhiteSpace(name))
            {
                string number = new string(name.Reverse().TakeWhile(Char.IsDigit).Reverse().ToArray());
                if (number.Length > 0)
                {
                    return number.Length > 2 ? number.Substring(number.Length - 2) : number;
                }
                return name.Substring(0, 1).ToUpperInvariant();
            }
            return (index + 1).ToString(CultureInfo.InvariantCulture);
        }

        private void UpdateRewardSummary(RewardDailyState state)
        {
            List<RewardClaim> claims = state == null || state.Claims == null ? new List<RewardClaim>() : state.Claims;
            List<RewardSubjectConfig> subjects = RewardService.GetSubjects(config);
            HashSet<string> activeSubjectIds = new HashSet<string>(subjects.Select(subject => subject.Id), StringComparer.OrdinalIgnoreCase);
            int earned = claims.Count(item => item != null && activeSubjectIds.Contains(item.SubjectId));
            int available = claims.Count(item => item != null && !item.Used);
            Find<TextBlock>("RewardAvailableText").Text = available.ToString(CultureInfo.InvariantCulture) + " 份";
            Find<TextBlock>("RewardMinutesTotalText").Text = "可用 " + (available * RewardService.RewardMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟";
            Find<TextBlock>("RewardProgressText").Text = "今日已认真确认 " + earned.ToString(CultureInfo.InvariantCulture) + " / " + subjects.Count.ToString(CultureInfo.InvariantCulture) + " 科";
            System.Windows.Shapes.Rectangle progress = Find<System.Windows.Shapes.Rectangle>("RewardProgressFill");
            double targetWidth = subjects.Count == 0 ? 0 : 210.0 * Math.Min(1.0, (double)earned / subjects.Count);
            if (rewardEarnedDisplayed != earned)
            {
                DoubleAnimation animation = new DoubleAnimation(progress.Width, targetWidth, TimeSpan.FromMilliseconds(360))
                {
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
                };
                animation.Completed += delegate { progress.BeginAnimation(FrameworkElement.WidthProperty, null); progress.Width = targetWidth; };
                progress.BeginAnimation(FrameworkElement.WidthProperty, animation);
                rewardEarnedDisplayed = earned;
            }
            else if (!progress.HasAnimatedProperties)
            {
                progress.Width = targetWidth;
            }

            Button useButton = Find<Button>("UseRewardButton");
            DateTimeOffset blockedUntil;
            bool blocked = AlarmService.IsTemporaryPauseBlockedNow(out blockedUntil);
            useButton.IsEnabled = available > 0 && !blocked && (config.BrowserRestrictionEnabled || config.AppGuardEnabled);
            Find<TextBlock>("RewardUseButtonText").Text = blocked
                ? "今日奖励使用已锁定"
                : (available > 0 ? "使用一份 · 解除 10 分钟" : "先完成一科并手写说明");
        }

        private void HandleRewardSubjectClick(object sender, RoutedEventArgs args)
        {
            Button source = args.OriginalSource as Button;
            RewardSubjectViewModel subject = source == null ? null : source.DataContext as RewardSubjectViewModel;
            if (source == null || subject == null)
            {
                return;
            }
            args.Handled = true;
            string action = source.Tag as string;
            if (String.Equals(action, "rename-reward-subject", StringComparison.Ordinal))
            {
                RenameRewardSubject(subject);
                return;
            }
            if (String.Equals(action, "delete-reward-subject", StringComparison.Ordinal))
            {
                DeleteRewardSubject(subject);
                return;
            }
            if (!String.Equals(action, "claim-reward", StringComparison.Ordinal))
            {
                return;
            }
            RewardStatementWindow dialog = new RewardStatementWindow(
                subject.Name,
                (Style)window.FindResource("PrimaryButton"),
                (Style)window.FindResource("IconButton"));
            dialog.Owner = window;
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            OperationResult result = RewardService.Claim(subject.Id, dialog.Statement);
            ShowToast(result.Message, result.Success);
            RefreshRewardPage();
        }

        private void AddRewardSubject()
        {
            OperationResult result = RewardService.AddSubject();
            if (result.Success)
            {
                config = JsonStore.LoadConfig();
            }
            ShowToast(result.Message, result.Success);
            RefreshRewardPage();
        }

        private void RenameRewardSubject(RewardSubjectViewModel subject)
        {
            if (!RewardService.CanManageSubjects())
            {
                ShowToast("科目只能在限制临时解除期间重命名。", false);
                RefreshRewardPage();
                return;
            }
            RewardSubjectNameWindow dialog = new RewardSubjectNameWindow(
                subject.Name,
                (Style)window.FindResource("PrimaryButton"),
                (Style)window.FindResource("SecondaryButton"));
            dialog.Owner = window;
            if (dialog.ShowDialog() != true)
            {
                return;
            }
            OperationResult result = RewardService.RenameSubject(subject.Id, dialog.SubjectName);
            if (result.Success)
            {
                config = JsonStore.LoadConfig();
            }
            ShowToast(result.Message, result.Success);
            RefreshRewardPage();
        }

        private void DeleteRewardSubject(RewardSubjectViewModel subject)
        {
            if (!RewardService.CanManageSubjects())
            {
                ShowToast("科目只能在限制临时解除期间删除。", false);
                RefreshRewardPage();
                return;
            }
            if (MessageBox.Show(window, "确定删除“" + subject.Name + "”吗？今天已经领取的奖励不会丢失。", "删除科目", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }
            OperationResult result = RewardService.DeleteSubject(subject.Id);
            if (result.Success)
            {
                config = JsonStore.LoadConfig();
            }
            ShowToast(result.Message, result.Success);
            RefreshRewardPage();
        }

        private void UseStudyReward()
        {
            Button button = Find<Button>("UseRewardButton");
            button.IsEnabled = false;
            TextBlock label = Find<TextBlock>("RewardUseButtonText");
            string oldContent = label.Text;
            label.Text = "正在兑换…";
            window.Dispatcher.Invoke(DispatcherPriority.Render, new Action(delegate { }));
            OperationResult result = RewardService.UseOne(config, executablePath);
            label.Text = oldContent;
            ShowToast(result.Message, result.Success);
            RefreshRewardPage();
            UpdateStatus();
        }

        private void ChooseAlarmMusic()
        {
            if (!EnsureAlarmEditable())
            {
                return;
            }
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择闹铃音乐",
                Filter = "音频文件|*.mp3;*.wav;*.wma;*.m4a;*.aac;*.flac|FLAC 无损音频|*.flac|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(window) == true)
            {
                Find<TextBox>("AlarmMusicPathText").Text = dialog.FileName;
            }
        }

        private void ChooseDepositFolder()
        {
            if (!EnsureAlarmEditable())
            {
                return;
            }
            using (System.Windows.Forms.FolderBrowserDialog dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "选择 E 盘中的专用押金文件夹。超时后会隔离整个文件夹 120 小时；请勿选择桌面、文档、云盘、软件目录或包含链接的目录。";
                dialog.ShowNewFolderButton = true;
                string current = Find<TextBox>("AlarmDepositFolderText").Text;
                if (!String.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                {
                    dialog.SelectedPath = current;
                }
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    Find<TextBox>("AlarmDepositFolderText").Text = dialog.SelectedPath;
                }
            }
        }

        private void SaveAlarmSettings()
        {
            if (!EnsureAlarmEditable())
            {
                return;
            }
            AlarmConfig next;
            string validationMessage;
            if (!TryBuildAlarmConfigFromUi(out next, out validationMessage))
            {
                ShowToast(validationMessage, false);
                return;
            }
            string resultMessage;
            bool saved = PersistAlarmSettings(next, true, out resultMessage);
            ShowToast(resultMessage, saved);
            if (!saved)
            {
                LoadAlarmConfigIntoUi();
            }
            UpdateStatus();
        }

        private bool PersistAlarmSettings(AlarmConfig next, bool forceScheduleRepair, out string resultMessage)
        {
            AlarmConfig previous = CloneAlarmConfig(config.Alarm ?? DefaultConfigFactory.Create().Alarm);
            bool scheduleApplied = false;
            try
            {
                AlarmService.ValidateAlarmConfig(next, next.Enabled);
                config.Alarm = next;
                bool scheduleChanged = previous.Enabled != next.Enabled || previous.Hour != next.Hour || previous.Minute != next.Minute;
                if (forceScheduleRepair || scheduleChanged)
                {
                    AlarmService.ApplySchedule(config, executablePath, true);
                    scheduleApplied = true;
                }
                ApplyConfigAndRemember();
                resultMessage = next.Enabled ? "晨间闹钟已用最高权限保存，并启用双任务唤醒。" : "晨间闹钟已关闭，唤醒任务已移除。";
                return true;
            }
            catch (Exception exception)
            {
                config.Alarm = previous;
                List<string> rollbackErrors = new List<string>();
                try
                {
                    ApplyConfigAndRemember();
                }
                catch (Exception rollbackException)
                {
                    rollbackErrors.Add("旧配置恢复失败：" + rollbackException.Message);
                }

                AlarmScheduleException scheduleException = exception as AlarmScheduleException;
                if (scheduleApplied || (scheduleException != null && scheduleException.MayHaveChangedTasks))
                {
                    try
                    {
                        AlarmService.ApplySchedule(config, executablePath, true);
                    }
                    catch (Exception rollbackException)
                    {
                        rollbackErrors.Add("旧唤醒任务恢复失败：" + rollbackException.Message);
                    }
                }
                string rollbackDetail = rollbackErrors.Count == 0 ? String.Empty : "；" + String.Join("；", rollbackErrors);
                resultMessage = "保存失败：" + exception.Message + rollbackDetail;
                return false;
            }
        }

        private bool TryBuildAlarmConfigFromUi(out AlarmConfig alarm, out string validationMessage)
        {
            alarm = null;
            validationMessage = String.Empty;
            int alarmMinute;
            if (!TryParseClock(Find<TextBox>("AlarmTimeInput").Text, out alarmMinute))
            {
                validationMessage = "闹钟时间格式无效，请使用 07:00 这样的格式。";
                return false;
            }
            alarm = new AlarmConfig
            {
                Enabled = Find<CheckBox>("AlarmEnabledToggle").IsChecked == true,
                Hour = alarmMinute / 60,
                Minute = alarmMinute % 60,
                MusicPath = (Find<TextBox>("AlarmMusicPathText").Text ?? String.Empty).Trim(),
                DepositFolder = (Find<TextBox>("AlarmDepositFolderText").Text ?? String.Empty).Trim(),
                DepositFolderPolicyVersion = 2,
                ChallengeText = AlarmService.NormalizeText(Find<TextBox>("AlarmChallengeText").Text).Trim(),
                ChallengeMinutes = 15,
                QuarantineHours = 120
            };
            return true;
        }

        private static AlarmConfig CloneAlarmConfig(AlarmConfig source)
        {
            return new AlarmConfig
            {
                Enabled = source.Enabled,
                Hour = source.Hour,
                Minute = source.Minute,
                MusicPath = source.MusicPath ?? String.Empty,
                DepositFolder = source.DepositFolder ?? String.Empty,
                DepositFolderPolicyVersion = source.DepositFolderPolicyVersion,
                ChallengeText = source.ChallengeText ?? String.Empty,
                ChallengeMinutes = 15,
                QuarantineHours = 120
            };
        }

        private void LoadAlarmConfigIntoUi()
        {
            AlarmConfig alarm = config.Alarm ?? DefaultConfigFactory.Create().Alarm;
            Find<CheckBox>("AlarmEnabledToggle").IsChecked = alarm.Enabled;
            Find<TextBox>("AlarmTimeInput").Text = alarm.Hour.ToString("00", CultureInfo.InvariantCulture) + ":" + alarm.Minute.ToString("00", CultureInfo.InvariantCulture);
            Find<TextBox>("AlarmMusicPathText").Text = alarm.MusicPath ?? String.Empty;
            Find<TextBox>("AlarmDepositFolderText").Text = alarm.DepositFolder ?? String.Empty;
            Find<TextBox>("AlarmChallengeText").Text = alarm.ChallengeText ?? String.Empty;
            UpdateAlarmCharacterCount();
        }

        private void UpdateAlarmCharacterCount()
        {
            string value = AlarmService.NormalizeText(Find<TextBox>("AlarmChallengeText").Text).Trim();
            TextBlock count = Find<TextBlock>("AlarmCharacterCountText");
            count.Text = value.Length.ToString(CultureInfo.InvariantCulture) + " / 150 字符";
            count.Foreground = new SolidColorBrush(value.Length >= 150 ? Color.FromRgb(101, 225, 213) : Color.FromRgb(142, 154, 178));
        }

        private void RefreshQuarantineList()
        {
            List<AlarmQuarantineViewModel> items = AlarmService.GetQuarantineRecords()
                .Where(record => record != null && !record.Restored)
                .Select(record => new AlarmQuarantineViewModel(record))
                .ToList();
            Find<ListBox>("QuarantineList").ItemsSource = items;
            UpdateQuarantineRestoreButton();
        }

        private void UpdateQuarantineRestoreButton()
        {
            AlarmQuarantineViewModel selected = Find<ListBox>("QuarantineList").SelectedItem as AlarmQuarantineViewModel;
            Find<Button>("RestoreQuarantineButton").IsEnabled = selected != null && selected.CanRestore && IsAlarmEditWindow();
        }

        private void RestoreSelectedQuarantine()
        {
            if (!EnsureAlarmEditable())
            {
                return;
            }
            AlarmQuarantineViewModel selected = Find<ListBox>("QuarantineList").SelectedItem as AlarmQuarantineViewModel;
            if (selected == null)
            {
                ShowToast("请先选择一个隔离项目。", false);
                return;
            }
            OperationResult result = AlarmService.RestoreQuarantinedFile(selected.Record.Id);
            ShowToast(result.Message, result.Success);
            RefreshQuarantineList();
        }

        private void AddDomain()
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            TextBox input = Find<TextBox>("DomainInput");
            string value = (input.Text ?? String.Empty).Trim();
            if (!IsValidPolicyRule(value))
            {
                ShowToast("请输入有效域名、URL 或策略模式。", false);
                return;
            }
            if (allowlist.Any(item => String.Equals(item, value, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast("该规则已经在白名单中。", false);
                return;
            }
            allowlist.Add(value);
            input.Clear();
            Find<TextBlock>("AllowlistCountText").Text = allowlist.Count.ToString(CultureInfo.InvariantCulture);
            ShowToast("已加入列表，点击“应用更改”后生效。", true);
        }

        private void RemoveSelectedDomain()
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            ListBox list = Find<ListBox>("AllowlistBox");
            string selected = list.SelectedItem as string;
            if (selected == null)
            {
                ShowToast("请先选择要移除的规则。", false);
                return;
            }
            allowlist.Remove(selected);
            Find<TextBlock>("AllowlistCountText").Text = allowlist.Count.ToString(CultureInfo.InvariantCulture);
            ShowToast("已从列表移除，点击“应用更改”后生效。", true);
        }

        private void ApplyBrowserSettings()
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            config.BrowserRestrictionEnabled = Find<CheckBox>("BrowserEnabledToggle").IsChecked == true;
            config.BrowserAllowlist = allowlist.ToList();
            try
            {
                ApplyConfigAndRemember();
                ShowToast("浏览器规则已应用。", true);
            }
            catch (Exception exception)
            {
                ShowToast("应用失败：" + exception.Message, false);
                ReloadPersistedConfigurationAfterFailure();
            }
            UpdateStatus();
        }

        private void ApplyAppSettings()
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            config.AppGuardEnabled = Find<CheckBox>("AppGuardEnabledToggle").IsChecked == true;
            config.AppRules = appRuleItems.Select(item => item.Rule).ToList();
            try
            {
                ApplyConfigAndRemember();
                ShowToast("应用守卫规则已保存。", true);
            }
            catch (Exception exception)
            {
                ShowToast("保存失败：" + exception.Message, false);
                ReloadPersistedConfigurationAfterFailure();
            }
            UpdateStatus();
        }

        private void ReapplyAll()
        {
            try
            {
                ApplyConfigAndRemember();
                ShowToast("全部规则已重新应用。", true);
            }
            catch (Exception exception)
            {
                ShowToast("重新应用失败：" + exception.Message, false);
            }
            UpdateStatus();
        }

        private void ReloadPersistedConfigurationAfterFailure()
        {
            try
            {
                config = RestrictionService.EnforceFocusLock(JsonStore.LoadConfig());
                LoadConfigIntoUi();
            }
            catch (Exception reloadException)
            {
                JsonStore.AppendLog("Could not reload persisted configuration after a failed save: " + reloadException.Message);
            }
        }

        private void RestoreAllNow()
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            MessageBoxResult confirmation = MessageBox.Show(
                window,
                "这会立即移除浏览器限制、停止应用守卫并取消自动启动。\n\n确定继续吗？",
                "恢复无限制状态",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            OperationResult result = RestrictionService.RestoreAll(executablePath);
            config = JsonStore.LoadConfig();
            LoadConfigIntoUi();
            ShowToast(result.Message, result.Success);
            UpdateStatus();
        }

        private void AddInstalledApplication()
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            InstalledSoftwareEntry selected = Find<ComboBox>("InstalledSoftwarePicker").SelectedItem as InstalledSoftwareEntry;
            if (selected == null || (!selected.IsPackaged && !File.Exists(selected.ExecutablePath)))
            {
                ShowToast("请先选择一个有效的已安装软件。", false);
                return;
            }
            if (selected.IsPackaged)
            {
                if (appRuleItems.Any(item => String.Equals(item.Rule.TargetKind, "PackagedApplication", StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(item.Rule.PackageFamilyName, selected.PackageFamilyName, StringComparison.OrdinalIgnoreCase)))
                {
                    ShowToast("这个商店应用包已经被现有守卫规则覆盖。", false);
                    return;
                }
                appRuleItems.Add(new AppRuleViewModel(PackagedAppService.CreateRule(selected)));
                ShowToast("已添加商店应用“" + selected.DisplayName + "”。同一应用包的组件也会按此规则守卫，保存后生效。", true);
                return;
            }
            if (IsExecutableAlreadyCovered(selected.ExecutablePath))
            {
                ShowToast("这个程序已经被现有守卫规则覆盖。", false);
                return;
            }
            AppRuleConfig rule = new AppRuleConfig
            {
                Id = "custom-app-" + Guid.NewGuid().ToString("N"),
                DisplayName = selected.DisplayName,
                RootPath = Path.GetDirectoryName(selected.ExecutablePath),
                TargetKind = "Executable",
                TargetPath = Path.GetFullPath(selected.ExecutablePath),
                Mode = "AlwaysBlocked",
                Enabled = true,
                IsCustom = true,
                ProcessNames = new List<string> { Path.GetFileNameWithoutExtension(selected.ExecutablePath) },
                AllowedWindows = new List<TimeWindowConfig>()
            };
            appRuleItems.Add(new AppRuleViewModel(rule));
            ShowToast("已添加“" + selected.DisplayName + "”，保存后将在专注期间禁止启动。", true);
        }

        private void AddGuardedFile()
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择专注期间禁止访问的文件",
                Filter = "所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(window) != true)
            {
                return;
            }
            string selectedPath = Path.GetFullPath(dialog.FileName);
            if (String.Equals(selectedPath, Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase) ||
                IsPathInside(selectedPath, JsonStore.StateDirectory) || IsPathInside(selectedPath, JsonStore.PauseStateDirectory) || IsPathInside(selectedPath, JsonStore.GuardStateDirectory))
            {
                ShowToast("不能把 Focus Harbor 自身或其状态文件加入守卫。", false);
                return;
            }
            bool selectedExecutable = JsonStore.IsExecutableTargetPath(selectedPath);
            if ((selectedExecutable && IsExecutableAlreadyCovered(selectedPath)) ||
                (!selectedExecutable && appRuleItems.Any(item => String.Equals(item.Rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase) && String.Equals(item.Rule.TargetPath, selectedPath, StringComparison.OrdinalIgnoreCase))))
            {
                ShowToast(selectedExecutable ? "这个程序已经被现有守卫规则覆盖。" : "这个文件已经在守卫列表中。", false);
                return;
            }
            AppRuleConfig rule = CreateCustomPathRule(selectedPath);
            appRuleItems.Add(new AppRuleViewModel(rule));
            ShowToast(
                selectedExecutable
                    ? "程序已加入列表，保存后将在专注期间禁止启动。"
                    : "文件已加入列表，保存后将在专注期间禁止访问。",
                true);
        }

        internal static AppRuleConfig CreateCustomPathRule(string selectedPath)
        {
            string fullPath = Path.GetFullPath(selectedPath);
            bool executable = JsonStore.IsExecutableTargetPath(fullPath);
            return new AppRuleConfig
            {
                Id = (executable ? "custom-app-" : "custom-file-") + Guid.NewGuid().ToString("N"),
                DisplayName = Path.GetFileName(fullPath),
                RootPath = Path.GetDirectoryName(fullPath),
                TargetKind = executable ? "Executable" : "File",
                TargetPath = fullPath,
                Mode = "AlwaysBlocked",
                Enabled = true,
                IsCustom = true,
                ProcessNames = executable
                    ? new List<string> { Path.GetFileNameWithoutExtension(fullPath) }
                    : new List<string>(),
                AllowedWindows = new List<TimeWindowConfig>()
            };
        }

        private void HandleAppRuleButtonClick(object sender, RoutedEventArgs args)
        {
            Button button = args.OriginalSource as Button ?? args.Source as Button;
            if (button == null) return;
            string action = Convert.ToString(button.Tag, CultureInfo.InvariantCulture);
            if (String.Equals(action, "rule-delete", StringComparison.Ordinal))
            {
                RemoveAppRule(button.DataContext as AppRuleViewModel);
                args.Handled = true;
                return;
            }
            if (!String.Equals(action, "rule-settings", StringComparison.Ordinal))
            {
                return;
            }
            args.Handled = true;
            AppRuleViewModel item = button.DataContext as AppRuleViewModel;
            if (item == null || !EnsureSettingsEditable())
            {
                return;
            }
            ShowRuleSettingsDialog(item);
        }

        private void ShowRuleSettingsDialog(AppRuleViewModel item)
        {
            AppRuleConfig rule = item.Rule;
            Window dialog = new Window
            {
                Owner = window,
                Title = "规则设置 · " + rule.DisplayName,
                Width = 520,
                Height = 530,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brushes.Transparent,
                AllowsTransparency = true,
                FontFamily = window.FontFamily,
                Foreground = new SolidColorBrush(Color.FromRgb(241, 244, 251))
            };

            Border surface = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(12, 18, 32)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(120, 124, 107, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(22)
            };
            surface.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 34,
                ShadowDepth = 8,
                Opacity = 0.62
            };
            dialog.Content = surface;

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            surface.Child = layout;

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = rule.DisplayName, FontSize = 20, FontWeight = FontWeights.Bold });
            heading.Children.Add(new TextBlock { Text = "设置专注期间允许启动的时间", Foreground = new SolidColorBrush(Color.FromRgb(132, 146, 174)), FontSize = 11, Margin = new Thickness(0, 5, 0, 0) });
            header.Children.Add(heading);
            Button close = new Button { Content = "\uE711", Style = (Style)window.FindResource("IconButton") };
            close.Click += delegate { dialog.DialogResult = false; };
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            layout.Children.Add(header);

            StackPanel modeArea = new StackPanel();
            Grid.SetRow(modeArea, 2);
            modeArea.Children.Add(new TextBlock { Text = "限制方式", Foreground = new SolidColorBrush(Color.FromRgb(162, 173, 196)), FontSize = 11, Margin = new Thickness(2, 0, 0, 7) });
            ComboBox modePicker = new ComboBox { Height = 42, Style = (Style)window.FindResource("ModernComboBox"), DisplayMemberPath = "Label" };
            List<RuleModeChoice> choices = new List<RuleModeChoice>
            {
                new RuleModeChoice("AlwaysBlocked", "任何时间都禁止启动"),
                new RuleModeChoice("Scheduled", "仅允许指定时段")
            };
            modePicker.ItemsSource = choices;
            modePicker.SelectedIndex = String.Equals(rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            modeArea.Children.Add(modePicker);
            layout.Children.Add(modeArea);

            TextBlock timeLabel = new TextBlock { Text = "允许时间（每行一个 HH:mm-HH:mm）", Foreground = new SolidColorBrush(Color.FromRgb(162, 173, 196)), FontSize = 11, Margin = new Thickness(2, 0, 0, 7) };
            Grid.SetRow(timeLabel, 4);
            layout.Children.Add(timeLabel);

            Grid timeArea = new Grid();
            timeArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            timeArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            timeArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(timeArea, 6);
            TextBox timeInput = new TextBox
            {
                Style = (Style)window.FindResource(typeof(TextBox)),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Padding = new Thickness(13),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                Text = FormatAllowedWindows(rule.AllowedWindows)
            };
            Border timeInputShell = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(13, 22, 40)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 102, 115, 142)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(11),
                Child = timeInput
            };
            timeInput.GotKeyboardFocus += delegate
            {
                timeInputShell.BorderBrush = new SolidColorBrush(Color.FromRgb(136, 120, 255));
                timeInputShell.Background = new SolidColorBrush(Color.FromRgb(17, 28, 49));
            };
            timeInput.LostKeyboardFocus += delegate
            {
                timeInputShell.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 102, 115, 142));
                timeInputShell.Background = new SolidColorBrush(Color.FromRgb(13, 22, 40));
            };
            timeArea.Children.Add(timeInputShell);
            TextBlock validation = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(255, 151, 164)),
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 7, 0, 0)
            };
            Grid.SetRow(validation, 1);
            timeArea.Children.Add(validation);
            layout.Children.Add(timeArea);

            Border hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(34, 51, 214, 200)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(55, 51, 214, 200)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(11, 8, 11, 8),
                Margin = new Thickness(0, 10, 0, 0),
                Child = new TextBlock
                {
                    Text = "允许时段之外会被守卫拦截；时间段不能重叠，也不能跨越午夜。",
                    Foreground = new SolidColorBrush(Color.FromRgb(139, 195, 192)),
                    FontSize = 10,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            timeArea.Children.Add(hint);
            Grid.SetRow(hint, 2);

            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetRow(actions, 8);
            Button cancel = new Button { Content = "取消", Width = 96, Style = (Style)window.FindResource("SecondaryButton") };
            Button save = new Button { Content = "保存到规则", Width = 126, Margin = new Thickness(10, 0, 0, 0), Style = (Style)window.FindResource("PrimaryButton") };
            cancel.Click += delegate { dialog.DialogResult = false; };
            save.Click += delegate
            {
                RuleModeChoice choice = modePicker.SelectedItem as RuleModeChoice;
                if (choice == null)
                {
                    return;
                }
                List<TimeWindowConfig> windows = new List<TimeWindowConfig>();
                string error;
                if (String.Equals(choice.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase) && !TryParseAllowedWindows(timeInput.Text, out windows, out error))
                {
                    validation.Text = error;
                    return;
                }
                bool wasUnrestricted = String.Equals(rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase);
                rule.Mode = choice.Mode;
                rule.AllowedWindows = windows;
                if (wasUnrestricted)
                {
                    rule.Enabled = true;
                }
                item.Refresh();
                dialog.DialogResult = true;
            };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            layout.Children.Add(actions);

            Action updateModeUi = delegate
            {
                RuleModeChoice selected = modePicker.SelectedItem as RuleModeChoice;
                bool scheduled = selected != null && String.Equals(selected.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase);
                timeInput.IsEnabled = scheduled;
                timeInputShell.Opacity = scheduled ? 1.0 : 0.55;
                timeLabel.Opacity = scheduled ? 1.0 : 0.48;
                validation.Text = String.Empty;
            };
            modePicker.SelectionChanged += delegate { updateModeUi(); };
            updateModeUi();

            bool? saved = dialog.ShowDialog();
            if (saved == true)
            {
                ShowToast("规则设置已更新，点击“保存守卫规则”后生效。", true);
                UpdateStatus();
            }
        }

        private static string FormatAllowedWindows(IEnumerable<TimeWindowConfig> windows)
        {
            if (windows == null)
            {
                return String.Empty;
            }
            return String.Join(Environment.NewLine, windows.OrderBy(item => item.StartMinute).Select(item => FormatMinute(item.StartMinute) + "-" + FormatMinute(item.EndMinute)));
        }

        private static string FormatMinute(int minute)
        {
            int safe = Math.Max(0, Math.Min(1439, minute));
            return (safe / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (safe % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static bool TryParseAllowedWindows(string text, out List<TimeWindowConfig> windows, out string error)
        {
            windows = new List<TimeWindowConfig>();
            error = String.Empty;
            string normalized = (text ?? String.Empty).Replace('\u2013', '-').Replace('\u2014', '-');
            string[] lines = normalized.Split(new[] { '\r', '\n', ';', '\uFF1B', ',', '\uFF0C' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                string[] parts = line.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
                int startMinute;
                int endMinute;
                if (parts.Length != 2 || !TryParseClock(parts[0], out startMinute) || !TryParseClock(parts[1], out endMinute))
                {
                    error = "无法识别“" + line + "”。请使用 11:00-11:30 格式。";
                    return false;
                }
                if (startMinute >= endMinute)
                {
                    error = "开始时间必须早于结束时间，且时间段不能跨越午夜。";
                    return false;
                }
                windows.Add(new TimeWindowConfig { StartMinute = startMinute, EndMinute = endMinute, Label = FormatMinute(startMinute) + "–" + FormatMinute(endMinute) });
            }
            if (windows.Count == 0)
            {
                error = "请至少输入一个允许时间段。";
                return false;
            }
            windows = windows.OrderBy(item => item.StartMinute).ToList();
            for (int index = 1; index < windows.Count; index++)
            {
                if (windows[index].StartMinute < windows[index - 1].EndMinute)
                {
                    error = "允许时间段不能互相重叠。";
                    return false;
                }
            }
            return true;
        }

        private static bool TryParseClock(string value, out int minute)
        {
            minute = 0;
            string[] pieces = (value ?? String.Empty).Trim().Split(':');
            int hour;
            int partMinute;
            if (pieces.Length != 2 || !Int32.TryParse(pieces[0], NumberStyles.None, CultureInfo.InvariantCulture, out hour) || !Int32.TryParse(pieces[1], NumberStyles.None, CultureInfo.InvariantCulture, out partMinute))
            {
                return false;
            }
            if (hour < 0 || hour > 23 || partMinute < 0 || partMinute > 59)
            {
                return false;
            }
            minute = hour * 60 + partMinute;
            return true;
        }

        private void RemoveSelectedAppRule()
        {
            RemoveAppRule(Find<ListBox>("AppRulesBox").SelectedItem as AppRuleViewModel);
        }

        internal static bool CanRemoveAppRule(AppRuleConfig rule, bool configurationLocked)
        {
            // Rule removal is independent of installation state and legacy
            // IsCustom flags. It never deletes the underlying app or file.
            return rule != null && !configurationLocked;
        }

        private void RemoveAppRule(AppRuleViewModel selected)
        {
            if (!EnsureSettingsEditable())
            {
                return;
            }
            if (selected == null)
            {
                ShowToast("请先选择一条规则，或点击规则右侧的删除按钮。", false);
                return;
            }
            if (!CanRemoveAppRule(selected.Rule, RestrictionService.IsFocusLocked(config))) return;
            if (MessageBox.Show(window, "确定移除“" + selected.DisplayName + "”的守卫规则吗？\n\n只删除规则，不卸载软件，也不删除任何文件。旧版预置规则和对应软件已卸载的规则同样可以移除。",
                "移除守卫规则", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            appRuleItems.Remove(selected);
            ShowToast("规则已移除，保存后生效。", true);
            UpdateRemoveRuleButton();
        }

        private void UpdateConfigurationLock(FocusStatus status)
        {
            bool editable = !status.ConfigurationLocked;
            bool alarmEditable = IsAlarmEditWindow();
            Grid browserPanel = Find<Grid>("BrowserSettingsPanel");
            Grid appsPanel = Find<Grid>("AppsEditorPanel");
            Grid alarmPanel = Find<Grid>("AlarmSettingsPanel");
            DependencyObject focusedElement = Keyboard.FocusedElement as DependencyObject;
            if (focusedElement != null && ((!editable && (browserPanel.IsAncestorOf(focusedElement) || appsPanel.IsAncestorOf(focusedElement))) || (!alarmEditable && alarmPanel.IsAncestorOf(focusedElement))))
            {
                Keyboard.ClearFocus();
            }
            Find<CheckBox>("BrowserEnabledToggle").IsEnabled = editable;
            browserPanel.IsHitTestVisible = editable;
            browserPanel.Focusable = editable;
            KeyboardNavigation.SetTabNavigation(browserPanel, editable ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
            browserPanel.Opacity = 1.0;
            Find<TextBox>("DomainInput").IsReadOnly = !editable;
            Find<Button>("AddDomainButton").IsEnabled = editable;
            Find<Button>("RemoveDomainButton").IsEnabled = editable;
            Find<Button>("ApplyBrowserButton").IsEnabled = editable;
            Find<Border>("BrowserLockBanner").Visibility = editable ? Visibility.Collapsed : Visibility.Visible;

            Find<CheckBox>("AppGuardEnabledToggle").IsEnabled = editable;
            appsPanel.IsHitTestVisible = editable;
            appsPanel.Focusable = editable;
            KeyboardNavigation.SetTabNavigation(appsPanel, editable ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.None);
            appsPanel.Opacity = 1.0;
            Find<ComboBox>("InstalledSoftwarePicker").Focusable = editable;
            Find<ListBox>("AppRulesBox").Focusable = editable;
            Find<Button>("AddInstalledAppButton").IsEnabled = editable;
            Find<Button>("RefreshInstalledAppsButton").IsEnabled = editable && !softwareDiscoveryRunning;
            Find<Button>("AddFileButton").IsEnabled = editable;
            Find<Grid>("AppAddControls").Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
            Find<Border>("AppAddLockedSurface").Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
            Find<Border>("AppsLockBanner").Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
            Find<Button>("ApplyAppsButton").IsEnabled = editable;
            Find<CheckBox>("AlarmEnabledToggle").IsEnabled = alarmEditable;
            alarmPanel.Focusable = true;
            KeyboardNavigation.SetTabNavigation(alarmPanel, alarmEditable ? KeyboardNavigationMode.Continue : KeyboardNavigationMode.Once);
            Find<TextBox>("AlarmTimeInput").IsReadOnly = !alarmEditable;
            Find<TextBox>("AlarmChallengeText").IsReadOnly = !alarmEditable;
            Find<Button>("ChooseAlarmMusicButton").IsEnabled = alarmEditable;
            Find<Button>("ChooseDepositFolderButton").IsEnabled = alarmEditable;
            Find<Button>("SaveAlarmButton").IsEnabled = alarmEditable;
            Find<Border>("AlarmLockBanner").Visibility = alarmEditable ? Visibility.Collapsed : Visibility.Visible;
            Find<TextBlock>("AlarmLockBannerText").Text = "晨间闹钟配置仅可在每天 00:00–02:00 修改。";
            Find<Button>("RestoreAllButton").IsEnabled = editable && (config.BrowserRestrictionEnabled || config.AppGuardEnabled);
            UpdateRemoveRuleButton();
            UpdateQuarantineRestoreButton();
        }

        private void UpdateRemoveRuleButton()
        {
            AppRuleViewModel selected = Find<ListBox>("AppRulesBox").SelectedItem as AppRuleViewModel;
            bool editable = !RestrictionService.IsFocusLocked(config);
            Find<Button>("RemoveAppRuleButton").IsEnabled = CanRemoveAppRule(selected == null ? null : selected.Rule, !editable);
        }

        private bool EnsureSettingsEditable()
        {
            if (!RestrictionService.IsFocusLocked(config))
            {
                return true;
            }
            ShowToast("专注状态下配置已锁定。请先使用“暂时解除 30 分钟”。", false);
            return false;
        }

        private bool EnsureAlarmEditable()
        {
            if (!IsAlarmEditWindow())
            {
                ShowToast("晨间闹钟页面仅在每天 00:00–02:00 可以修改。", false);
                return false;
            }
            return true;
        }

        private static bool IsAlarmEditWindow()
        {
            int minute = DateTime.Now.Hour * 60 + DateTime.Now.Minute;
            return IsAlarmEditMinute(minute);
        }

        private static bool IsAlarmEditMinute(int minute)
        {
            return minute >= 0 && minute < 2 * 60;
        }

        private bool IsExecutableAlreadyCovered(string executable)
        {
            string fullPath = Path.GetFullPath(executable);
            foreach (AppRuleViewModel item in appRuleItems)
            {
                AppRuleConfig rule = item.Rule;
                if (String.Equals(rule.TargetKind, "Executable", StringComparison.OrdinalIgnoreCase) && String.Equals(rule.TargetPath, fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (String.Equals(rule.TargetKind, "ApplicationGroup", StringComparison.OrdinalIgnoreCase) &&
                    rule.ProcessNames != null && rule.ProcessNames.Any(name => String.Equals(name, Path.GetFileNameWithoutExtension(fullPath), StringComparison.OrdinalIgnoreCase)) &&
                    IsPathInside(fullPath, rule.RootPath))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPathInside(string candidate, string directory)
        {
            try
            {
                string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(candidate).StartsWith(root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private string BuildBrowserPreview()
        {
            List<string> preview = new List<string>();
            foreach (string rule in (allowlist ?? new ObservableCollection<string>()).Take(3))
            {
                string value = rule ?? String.Empty;
                if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
                {
                    value = "本地 HTML / PDF";
                }
                else
                {
                    value = value.Replace("https://", String.Empty).Replace("http://", String.Empty).TrimStart('.').TrimEnd('/');
                }
                preview.Add("•  " + value);
            }
            return preview.Count == 0 ? "暂无放行规则" : String.Join(Environment.NewLine, preview);
        }

        private string BuildAppPreview()
        {
            if (config.AppRules == null)
            {
                return "暂无启用规则";
            }
            List<string> preview = config.AppRules
                .Where(rule => rule != null && rule.Enabled && !String.Equals(rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .Select(rule => "•  " + rule.DisplayName + " · " + RuleModeLabel(rule))
                .ToList();
            return preview.Count == 0 ? "暂无启用规则\n可在应用守卫中选择项目" : String.Join(Environment.NewLine, preview);
        }

        private static string RuleModeLabel(AppRuleConfig rule)
        {
            if (String.Equals(rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase))
            {
                return String.Equals(rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase) ? "按时段访问" : "禁止访问";
            }
            if (String.Equals(rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase))
            {
                return "按时段";
            }
            return "禁止启动";
        }

        private static string FormatFocusDuration(long totalSeconds)
        {
            TimeSpan duration = TimeSpan.FromSeconds(Math.Max(0, totalSeconds));
            if (duration.TotalMinutes < 1)
            {
                return Math.Max(0, (int)duration.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " 秒";
            }
            if (duration.TotalHours < 1)
            {
                return ((int)duration.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " 分钟";
            }
            if (duration.TotalDays < 1)
            {
                return ((int)duration.TotalHours).ToString(CultureInfo.InvariantCulture) + " 小时 " + duration.Minutes.ToString(CultureInfo.InvariantCulture) + " 分";
            }
            return ((int)duration.TotalDays).ToString(CultureInfo.InvariantCulture) + " 天 " + duration.Hours.ToString(CultureInfo.InvariantCulture) + " 小时";
        }

        private void FilterTutorials()
        {
            TextBox search = Find<TextBox>("TutorialSearchBox");
            string query = (search.Text ?? String.Empty).Trim();
            Find<TextBlock>("TutorialSearchHint").Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            string markdown = FilterMarkdownSections(tutorialMarkdown, query);
            Find<FlowDocumentScrollViewer>("TutorialDocumentViewer").Document = RenderMarkdown(markdown);
        }

        private static string LoadEmbeddedTutorialMarkdown()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream("FocusHarbor.UserGuide.md"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Embedded UserGuide.md resource was not found.");
                }
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string FilterMarkdownSections(string markdown, string query)
        {
            string normalized = (markdown ?? String.Empty).Replace("\r\n", "\n");
            if (String.IsNullOrWhiteSpace(query))
            {
                return normalized;
            }
            string[] terms = query.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string[] sections = normalized.Split(new[] { "\n## " }, StringSplitOptions.None);
            List<string> matches = new List<string>();
            for (int index = 1; index < sections.Length; index++)
            {
                string section = "## " + sections[index];
                if (terms.All(term => section.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0))
                {
                    matches.Add(section);
                }
            }
            string firstLine = sections.Length == 0 ? "# Focus Harbor 使用教程" : sections[0].Split('\n')[0];
            if (matches.Count == 0)
            {
                return firstLine + "\n\n## 没有找到相关内容\n\n请尝试更短的关键词，例如 `白名单`、`应用`、`时间` 或 `暂停`。";
            }
            return firstLine + "\n\n" + String.Join("\n\n", matches);
        }

        private static FlowDocument RenderMarkdown(string markdown)
        {
            FlowDocument document = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(177, 188, 210)),
                LineHeight = 19
            };
            bool inCode = false;
            foreach (string originalLine in (markdown ?? String.Empty).Replace("\r\n", "\n").Split('\n'))
            {
                string line = originalLine ?? String.Empty;
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                    continue;
                }
                if (String.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Paragraph paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                string content = line.Trim();
                if (inCode)
                {
                    paragraph.FontFamily = new FontFamily("Consolas");
                    paragraph.FontSize = 10.5;
                    paragraph.Foreground = new SolidColorBrush(Color.FromRgb(201, 211, 230));
                    paragraph.Background = new SolidColorBrush(Color.FromArgb(135, 9, 16, 30));
                    paragraph.Padding = new Thickness(9, 5, 9, 5);
                }
                else if (content.StartsWith("### ", StringComparison.Ordinal))
                {
                    content = content.Substring(4);
                    paragraph.FontSize = 13;
                    paragraph.FontWeight = FontWeights.Bold;
                    paragraph.Foreground = new SolidColorBrush(Color.FromRgb(226, 232, 244));
                    paragraph.Margin = new Thickness(0, 9, 0, 6);
                }
                else if (content.StartsWith("## ", StringComparison.Ordinal))
                {
                    content = content.Substring(3);
                    paragraph.FontSize = 16;
                    paragraph.FontWeight = FontWeights.Bold;
                    paragraph.Foreground = new SolidColorBrush(Color.FromRgb(238, 241, 249));
                    paragraph.Margin = new Thickness(0, 15, 0, 8);
                }
                else if (content.StartsWith("# ", StringComparison.Ordinal))
                {
                    content = content.Substring(2);
                    paragraph.FontSize = 22;
                    paragraph.FontWeight = FontWeights.Bold;
                    paragraph.Foreground = new SolidColorBrush(Color.FromRgb(112, 225, 214));
                    paragraph.Margin = new Thickness(0, 0, 0, 14);
                }
                else if (content.StartsWith("- ", StringComparison.Ordinal))
                {
                    content = "•  " + content.Substring(2);
                    paragraph.Margin = new Thickness(11, 0, 0, 6);
                }
                else if (content.StartsWith("> ", StringComparison.Ordinal))
                {
                    content = content.Substring(2);
                    paragraph.Foreground = new SolidColorBrush(Color.FromRgb(125, 218, 209));
                    paragraph.Background = new SolidColorBrush(Color.FromArgb(35, 51, 214, 200));
                    paragraph.Padding = new Thickness(10, 6, 10, 6);
                }
                else
                {
                    int dot = content.IndexOf('.');
                    int number;
                    if (dot > 0 && dot < 4 && Int32.TryParse(content.Substring(0, dot), out number))
                    {
                        paragraph.Margin = new Thickness(11, 0, 0, 6);
                    }
                }
                AppendMarkdownInlines(paragraph, content);
                document.Blocks.Add(paragraph);
            }
            return document;
        }

        private static void AppendMarkdownInlines(Paragraph paragraph, string text)
        {
            int position = 0;
            while (position < text.Length)
            {
                int boldStart = text.IndexOf("**", position, StringComparison.Ordinal);
                int codeStart = text.IndexOf('`', position);
                int markerStart;
                bool bold;
                if (boldStart >= 0 && (codeStart < 0 || boldStart < codeStart))
                {
                    markerStart = boldStart;
                    bold = true;
                }
                else if (codeStart >= 0)
                {
                    markerStart = codeStart;
                    bold = false;
                }
                else
                {
                    paragraph.Inlines.Add(new Run(text.Substring(position)));
                    break;
                }
                if (markerStart > position)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(position, markerStart - position)));
                }
                string marker = bold ? "**" : "`";
                int end = text.IndexOf(marker, markerStart + marker.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    paragraph.Inlines.Add(new Run(text.Substring(markerStart)));
                    break;
                }
                Run run = new Run(text.Substring(markerStart + marker.Length, end - markerStart - marker.Length));
                if (bold)
                {
                    run.FontWeight = FontWeights.Bold;
                    run.Foreground = new SolidColorBrush(Color.FromRgb(234, 237, 246));
                }
                else
                {
                    run.FontFamily = new FontFamily("Consolas");
                    run.Foreground = new SolidColorBrush(Color.FromRgb(139, 224, 216));
                    run.Background = new SolidColorBrush(Color.FromArgb(80, 51, 214, 200));
                }
                paragraph.Inlines.Add(run);
                position = end + marker.Length;
            }
        }

        private void SaveBeforeClose(object sender, CancelEventArgs args)
        {
            if (closingSaveCompleted) return;
            // Repeated close requests cannot bypass an in-flight durable save.
            args.Cancel = true;
            if (restrictionOperationRunning)
            {
                ShowToast("正在保存专注状态，请完成后再关闭。", false);
                return;
            }
            if (closingSaveStarted) return;
            try
            {
                AlarmConfig pendingAlarm = null;
                if (IsAlarmEditWindow())
                {
                    AlarmConfig alarmDraft;
                    string validationMessage;
                    if (!TryBuildAlarmConfigFromUi(out alarmDraft, out validationMessage))
                    {
                        ShowToast("晨间闹钟尚未保存：" + validationMessage, false);
                        return;
                    }
                    // Merely displaying a legacy policy version is not a user edit.
                    if (!UiCloseSaveService.AlarmFieldsEqual(alarmDraft, config.Alarm)) pendingAlarm = alarmDraft;
                }
                CaptureUiConfiguration();
                UiCloseSaveRequest request = new UiCloseSaveRequest
                {
                    Configuration = JsonStore.CloneConfig(config),
                    SecurityEdited = !JsonStore.SecurityEquals(config, savedUiSecurity),
                    OriginalAlarm = CloneAlarmConfig(config.Alarm ?? DefaultConfigFactory.Create().Alarm),
                    PendingAlarm = pendingAlarm
                };
                closingSaveStarted = true;
                statusTimer.Stop();
                if (toastTimer != null) toastTimer.Stop();
                window.IsEnabled = false;
                // WPF forbids Hide/Close inside its Closing event. Defer the hide
                // until the canceled event has returned, then keep the dispatcher
                // alive until persistence has completed.
                window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
                {
                    try
                    {
                        window.Hide();
                        Thread saver = new Thread(delegate() { CompleteCloseSave(request); });
                        saver.IsBackground = true;
                        saver.SetApartmentState(ApartmentState.STA);
                        saver.Start();
                    }
                    catch (Exception exception)
                    {
                        JsonStore.AppendLog("Close-save startup failed: " + exception);
                        RestoreWindowAfterCloseSaveFailure(exception);
                    }
                }));
            }
            catch (Exception exception)
            {
                JsonStore.AppendLog("Close-save failed: " + exception.Message);
                RestoreWindowAfterCloseSaveFailure(exception);
            }
        }

        private void CompleteCloseSave(UiCloseSaveRequest request)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            FocusConfig saved = null;
            Exception failure = null;
            try
            {
                saved = closeSave(request, executablePath);
                JsonStore.AppendLog("UI close-save completed in " + elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms (security draft: " + request.SecurityEdited + ", alarm draft: " + (request.PendingAlarm != null) + ").");
            }
            catch (Exception exception)
            {
                failure = exception;
                JsonStore.AppendLog("Close-save failed: " + exception);
            }
            window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate
            {
                if (failure == null)
                {
                    config = saved;
                    closingSaveCompleted = true;
                    window.Close();
                }
                else
                {
                    RestoreWindowAfterCloseSaveFailure(failure);
                }
            }));
        }

        private void RestoreWindowAfterCloseSaveFailure(Exception failure)
        {
            closingSaveStarted = false;
            window.IsEnabled = true;
            if (!window.IsVisible) window.Show();
            window.Activate();
            statusTimer.Start();
            ShowToast("设置未保存，窗口已保留，请重试：" + failure.Message, false);
        }

        private void ApplyConfigAndRemember()
        {
            RestrictionService.ApplyConfig(config, executablePath);
            savedUiSecurity = JsonStore.CaptureSecurity(config);
        }

        private void CaptureUiConfiguration()
        {
            config.BrowserRestrictionEnabled = Find<CheckBox>("BrowserEnabledToggle").IsChecked == true;
            config.BrowserAllowlist = allowlist == null ? new List<string>() : allowlist.ToList();
            config.AppGuardEnabled = Find<CheckBox>("AppGuardEnabledToggle").IsChecked == true;
            config.AppRules = appRuleItems == null ? new List<AppRuleConfig>() : appRuleItems.Select(item => item.Rule).ToList();
        }

        private void Navigate(string title, Grid targetPage, Button activeButton)
        {
            Grid[] pages = { overviewPage, browserPage, appsPage, alarmPage, rewardsPage, settingsPage };
            Button[] buttons = { overviewNav, browserNav, appsNav, alarmNav, rewardsNav, settingsNav };
            foreach (Grid page in pages)
            {
                page.Visibility = page == targetPage ? Visibility.Visible : Visibility.Collapsed;
            }
            foreach (Button button in buttons)
            {
                button.Tag = button == activeButton ? "active" : null;
            }
            pageTitle.Text = title;

            targetPage.Opacity = 0;
            TranslateTransform transform = new TranslateTransform(18, 0);
            targetPage.RenderTransform = transform;
            DoubleAnimation opacityAnimation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            DoubleAnimation movementAnimation = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            movementAnimation.Completed += delegate
            {
                // Remove animation clocks after the transition. Keeping an Opacity clock on
                // the page forces text into a transparent intermediate surface and disables
                // ClearType sub-pixel rendering even when the final opacity is exactly 1.
                targetPage.BeginAnimation(UIElement.OpacityProperty, null);
                targetPage.Opacity = 1;
                transform.BeginAnimation(TranslateTransform.XProperty, null);
                transform.X = 0;
            };
            targetPage.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
            transform.BeginAnimation(TranslateTransform.XProperty, movementAnimation);
        }

        private void ShowToast(string message, bool success)
        {
            Border toast = Find<Border>("ToastHost");
            TextBlock text = Find<TextBlock>("ToastText");
            text.Text = message.Replace("\n", "  ·  ");
            text.Foreground = success ? new SolidColorBrush(Color.FromRgb(215, 255, 245)) : new SolidColorBrush(Color.FromRgb(255, 184, 190));
            toast.Visibility = Visibility.Visible;
            toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(toast.Opacity, 1, TimeSpan.FromMilliseconds(170)));

            if (toastTimer != null)
            {
                toastTimer.Stop();
            }
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.4) };
            toastTimer.Tick += delegate
            {
                toastTimer.Stop();
                DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
                fade.Completed += delegate { toast.Visibility = Visibility.Collapsed; };
                toast.BeginAnimation(UIElement.OpacityProperty, fade);
            };
            toastTimer.Start();
        }

        private bool RuleEnabled(string id)
        {
            AppRuleConfig rule = config.AppRules.FirstOrDefault(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            return rule != null && rule.Enabled;
        }

        private void SetRuleEnabled(string id, bool enabled)
        {
            AppRuleConfig rule = config.AppRules.FirstOrDefault(item => String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            if (rule != null)
            {
                rule.Enabled = enabled;
            }
        }

        private static bool IsValidPolicyRule(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > 512 || value.IndexOfAny(new[] { '\r', '\n', '\t', ' ' }) >= 0)
            {
                return false;
            }
            if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return value.IndexOf('.') > 0 || value.StartsWith(".", StringComparison.Ordinal);
        }

        private void ToggleMaximize()
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            Find<Button>("MaximizeButton").Content = window.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }

        private void ApplyDwmEffects()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                int enabled = 1;
                DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
                int cornerPreference = 2;
                DwmSetWindowAttribute(handle, 33, ref cornerPreference, sizeof(int));
                int backdropType = 2;
                DwmSetWindowAttribute(handle, 38, ref backdropType, sizeof(int));
            }
            catch
            {
            }
        }

        private T Find<T>(string name) where T : class
        {
            T value = window.FindName(name) as T;
            if (value == null)
            {
                throw new InvalidOperationException("UI element was not found: " + name);
            }
            return value;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
    }

    public sealed class AppRuleViewModel : INotifyPropertyChanged
    {
        public AppRuleViewModel(AppRuleConfig rule)
        {
            Rule = rule;
        }

        public AppRuleConfig Rule { get; private set; }
        public string DisplayName { get { return Rule.DisplayName; } }
        public ImageSource IconSource { get { return IconService.GetRuleIcon(Rule); } }
        public string Badge
        {
            get
            {
                if (String.Equals(Rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase))
                {
                    return "文";
                }
                return String.IsNullOrWhiteSpace(Rule.DisplayName) ? "·" : Rule.DisplayName.Substring(0, 1).ToUpperInvariant();
            }
        }
        public string Description
        {
            get
            {
                if (String.Equals(Rule.TargetKind, "PackagedApplication", StringComparison.OrdinalIgnoreCase))
                {
                    if (String.Equals(Rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase))
                        return "全天允许，不受应用守卫限制 · Microsoft Store";
                    string windows = Rule.AllowedWindows == null ? String.Empty : String.Join(" · ", Rule.AllowedWindows.Select(item => item.Label));
                    return (String.Equals(Rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase) ? "仅允许 " + windows : "专注期间禁止启动") + " · Microsoft Store · 按应用包守卫";
                }
                if (String.Equals(Rule.TargetKind, "File", StringComparison.OrdinalIgnoreCase))
                {
                    if (String.Equals(Rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase))
                    {
                        string fileWindows = Rule.AllowedWindows == null ? String.Empty : String.Join(" · ", Rule.AllowedWindows.Select(item => item.Label));
                        return "仅允许 " + fileWindows + " 访问 · " + Rule.TargetPath;
                    }
                    return "专注期间禁止访问 · " + Rule.TargetPath;
                }
                if (String.Equals(Rule.TargetKind, "Executable", StringComparison.OrdinalIgnoreCase))
                {
                    if (String.Equals(Rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase))
                    {
                        string executableWindows = Rule.AllowedWindows == null ? String.Empty : String.Join(" · ", Rule.AllowedWindows.Select(item => item.Label));
                        return "仅允许 " + executableWindows + " 启动 · " + Rule.TargetPath;
                    }
                    return "专注期间禁止启动 · " + Rule.TargetPath;
                }
                if (String.Equals(Rule.Mode, "Scheduled", StringComparison.OrdinalIgnoreCase))
                {
                    string windows = Rule.AllowedWindows == null ? String.Empty : String.Join(" · ", Rule.AllowedWindows.Select(item => item.Label));
                    return "仅允许 " + windows;
                }
                if (String.Equals(Rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase))
                {
                    return "全天允许，不受应用守卫限制";
                }
                return "专注期间任何时刻均禁止启动";
            }
        }
        public bool Enabled
        {
            get { return Rule.Enabled; }
            set
            {
                if (Rule.Enabled == value)
                {
                    return;
                }
                Rule.Enabled = value;
                RaisePropertyChanged("Enabled");
            }
        }
        public bool CanToggle
        {
            get { return !String.Equals(Rule.Mode, "Unrestricted", StringComparison.OrdinalIgnoreCase); }
        }

        public void Refresh()
        {
            RaisePropertyChanged("Description");
            RaisePropertyChanged("CanToggle");
            RaisePropertyChanged("Enabled");
            RaisePropertyChanged("IconSource");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public sealed class RuleModeChoice
    {
        public RuleModeChoice(string mode, string label)
        {
            Mode = mode;
            Label = label;
        }

        public string Mode { get; private set; }
        public string Label { get; private set; }
    }

    public sealed class AlarmQuarantineViewModel
    {
        public AlarmQuarantineViewModel(QuarantineRecord record)
        {
            Record = record;
        }

        public QuarantineRecord Record { get; private set; }
        public string FileName
        {
            get
            {
                return String.Equals(Record.QuarantineKind, "Directory", StringComparison.OrdinalIgnoreCase)
                    ? "文件夹 · " + Record.FileName
                    : Record.FileName;
            }
        }
        public bool CanRestore
        {
            get
            {
                DateTimeOffset available;
                return !Record.Restored && DateTimeOffset.TryParse(Record.RestoreAvailableAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out available) && available <= DateTimeOffset.Now;
            }
        }
        public string StatusText
        {
            get
            {
                DateTimeOffset available;
                if (!DateTimeOffset.TryParse(Record.RestoreAvailableAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out available))
                {
                    return "恢复时间未知";
                }
                return CanRestore ? "现在可以手动恢复 · 原位置：" + Record.OriginalPath : "预计自动恢复：" + available.LocalDateTime.ToString("yyyy-MM-dd HH:mm") + " · 120 小时隔离";
            }
        }
    }

    public sealed class RewardSubjectViewModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Badge { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public string Accent { get; set; }
        public string ActionText { get; set; }
        public bool ActionEnabled { get; set; }
        public bool ManageEnabled { get; set; }
    }

    public sealed class RewardSubjectNameWindow : Window
    {
        private readonly TextBox editor;
        private readonly TextBlock errorText;

        public RewardSubjectNameWindow(string currentName, Style primaryButtonStyle, Style secondaryButtonStyle)
        {
            Title = "重命名科目";
            Width = 440;
            Height = 238;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Background = Brush("#FF0A0F1D");
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(20),
                GlassFrameThickness = new Thickness(0)
            });

            Grid layout = new Grid { Margin = new Thickness(24) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            StackPanel heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = "重命名科目", FontSize = 19, FontWeight = FontWeights.Bold });
            heading.Children.Add(new TextBlock { Text = "名称为 1–24 个字符；以后仍可在临时解除期间修改。", FontSize = 10.5, Foreground = Brush("#FF8794AC"), Margin = new Thickness(0, 6, 0, 0) });
            layout.Children.Add(heading);

            Border inputShell = new Border { Background = Brush("#FF10192B"), BorderBrush = Brush("#667C6BFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11) };
            editor = new TextBox
            {
                Text = currentName ?? String.Empty,
                MaxLength = 24,
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                SelectionBrush = Brush("#FF7563F6"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(13, 0, 13, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 13
            };
            inputShell.Child = editor;
            Grid.SetRow(inputShell, 2);
            layout.Children.Add(inputShell);

            errorText = new TextBlock { Foreground = Brush("#FFFF929D"), FontSize = 10.5, Margin = new Thickness(2, 7, 0, 0) };
            Grid.SetRow(errorText, 3);
            layout.Children.Add(errorText);

            StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            Button cancel = new Button { Content = "取消", Width = 92, Height = 40, Style = secondaryButtonStyle };
            Button save = new Button { Content = "保存名称", Width = 112, Height = 40, Margin = new Thickness(9, 0, 0, 0), Style = primaryButtonStyle };
            cancel.Click += delegate { DialogResult = false; };
            save.Click += delegate { Submit(); };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            Grid.SetRow(actions, 5);
            layout.Children.Add(actions);

            Border frame = new Border
            {
                Background = Brush("#FF0A0F1D"),
                BorderBrush = Brush("#526A7892"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Child = layout
            };
            Content = frame;
            Loaded += delegate
            {
                editor.Focus();
                editor.SelectAll();
                Keyboard.Focus(editor);
            };
            PreviewKeyDown += delegate(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Escape)
                {
                    DialogResult = false;
                    args.Handled = true;
                }
                else if (args.Key == Key.Enter)
                {
                    Submit();
                    args.Handled = true;
                }
            };
        }

        public string SubjectName { get; private set; }

        private void Submit()
        {
            string name = (editor.Text ?? String.Empty).Trim();
            if (name.Length == 0)
            {
                errorText.Text = "请输入科目名称。";
                editor.Focus();
                return;
            }
            SubjectName = name;
            DialogResult = true;
        }

        private static Brush Brush(string value)
        {
            return (Brush)new BrushConverter().ConvertFromString(value);
        }
    }

    public sealed class RewardStatementWindow : Window
    {
        private readonly TextBox editor;
        private readonly TextBlock countText;
        private readonly TextBlock validationText;
        private readonly Button submitButton;

        public RewardStatementWindow(string subjectName, Style primaryButtonStyle, Style iconButtonStyle)
        {
            Title = "学习奖励 · " + subjectName;
            Width = 650;
            Height = 520;
            MinWidth = 560;
            MinHeight = 450;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Background = Brush("#FF090E1B");
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            Grid root = new Grid { Margin = new Thickness(1) };
            Border frame = new Border
            {
                Background = new LinearGradientBrush(Color.FromRgb(24, 31, 52), Color.FromRgb(10, 17, 31), 120),
                BorderBrush = Brush("#55FFFFFF"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(22),
                Padding = new Thickness(28)
            };
            frame.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(3, 7, 16), BlurRadius = 30, ShadowDepth = 0, Opacity = 0.72 };
            root.Children.Add(frame);

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            frame.Child = layout;

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = subjectName + " · 今日任务确认", FontSize = 23, FontWeight = FontWeights.Bold });
            heading.Children.Add(new TextBlock { Text = "写下你今天真正完成了什么、怎样避免敷衍，以及下一次如何继续。", FontSize = 12, Foreground = Brush("#FF98A7C2"), Margin = new Thickness(0, 8, 0, 0) });
            header.Children.Add(heading);
            Button close = new Button { Content = "\uE8BB", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Width = 38, Height = 36, Foreground = Brush("#FFC4CDE0"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
            close.Style = iconButtonStyle;
            close.Click += delegate { DialogResult = false; };
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            header.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs args) { if (args.LeftButton == MouseButtonState.Pressed) DragMove(); };
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            Border guidance = new Border { Background = Brush("#182AD9C9"), BorderBrush = Brush("#4033D6C8"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13), Padding = new Thickness(14, 10, 14, 10) };
            guidance.Child = new TextBlock { Text = "这份说明只对今天负责。无需华丽，但必须诚实、具体，并由你亲手输入。完成后获得 1 份 10 分钟奖励。", Foreground = Brush("#FFB9DFDB"), FontSize = 11, TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
            Grid.SetRow(guidance, 2);
            layout.Children.Add(guidance);

            Border editorShell = new Border { Background = Brush("#B20C1425"), BorderBrush = Brush("#687C6BFF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Padding = new Thickness(4) };
            editor = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = Brushes.Transparent,
                Foreground = Brush("#FFF2F5FC"),
                CaretBrush = Brush("#FF7DE4D9"),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(15, 13, 15, 13),
                FontSize = 14,
                MaxLength = RewardService.MaximumStatementLength,
                ContextMenu = null,
                AllowDrop = false,
                AcceptsTab = false
            };
            Block.SetLineHeight(editor, 25);
            editorShell.Child = editor;
            Grid.SetRow(editorShell, 4);
            layout.Children.Add(editorShell);

            Grid footer = new Grid();
            footer.ColumnDefinitions.Add(new ColumnDefinition());
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel status = new StackPanel();
            countText = new TextBlock { Text = "0 / 300 字", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brush("#FF8F9BB4") };
            validationText = new TextBlock { Text = "至少手写 80 字，禁止复制、剪切、粘贴和拖放", FontSize = 10.5, Foreground = Brush("#FF718099"), Margin = new Thickness(0, 5, 0, 0) };
            status.Children.Add(countText);
            status.Children.Add(validationText);
            footer.Children.Add(status);
            submitButton = new Button { Content = "确认诚实完成 · 领取 10 分钟", Width = 245, Height = 46, Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(1), BorderBrush = Brush("#707C6BFF"), Background = Brush("#FF7563F6"), Cursor = Cursors.Hand, IsEnabled = false };
            submitButton.Style = primaryButtonStyle;
            submitButton.Click += delegate { Statement = editor.Text; DialogResult = true; };
            Grid.SetColumn(submitButton, 1);
            footer.Children.Add(submitButton);
            Grid.SetRow(footer, 6);
            layout.Children.Add(footer);

            Content = root;
            DataObject.AddPastingHandler(editor, BlockDataTransfer);
            DataObject.AddCopyingHandler(editor, BlockDataTransfer);
            editor.CommandBindings.Add(BlockCommand(ApplicationCommands.Paste));
            editor.CommandBindings.Add(BlockCommand(ApplicationCommands.Copy));
            editor.CommandBindings.Add(BlockCommand(ApplicationCommands.Cut));
            editor.PreviewKeyDown += BlockClipboardShortcuts;
            editor.TextChanged += UpdateState;
            Loaded += delegate
            {
                editor.Focus();
                TranslateTransform motion = new TranslateTransform(0, 14);
                frame.RenderTransform = motion;
                frame.Opacity = 0;
                DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                DoubleAnimation lift = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                lift.Completed += delegate { frame.BeginAnimation(UIElement.OpacityProperty, null); frame.Opacity = 1; motion.BeginAnimation(TranslateTransform.YProperty, null); motion.Y = 0; };
                frame.BeginAnimation(UIElement.OpacityProperty, fade);
                motion.BeginAnimation(TranslateTransform.YProperty, lift);
            };
        }

        public string Statement { get; private set; }

        private void UpdateState(object sender, TextChangedEventArgs args)
        {
            int length = AlarmService.NormalizeText(editor.Text).Trim().Length;
            countText.Text = length.ToString(CultureInfo.InvariantCulture) + " / " + RewardService.MaximumStatementLength.ToString(CultureInfo.InvariantCulture) + " 字";
            bool valid = length >= RewardService.MinimumStatementLength && length <= RewardService.MaximumStatementLength;
            submitButton.IsEnabled = valid;
            countText.Foreground = Brush(valid ? "#FF6FE1D5" : "#FF8F9BB4");
            validationText.Text = valid ? "内容长度合格，只在你确认今天没有糊弄自己时提交" : "还需亲手输入 " + Math.Max(0, RewardService.MinimumStatementLength - length).ToString(CultureInfo.InvariantCulture) + " 字 · 禁止复制粘贴";
        }

        private static void BlockDataTransfer(object sender, DataObjectEventArgs args)
        {
            args.CancelCommand();
            args.Handled = true;
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

        private static Brush Brush(string value)
        {
            return (Brush)new BrushConverter().ConvertFromString(value);
        }
    }
}
