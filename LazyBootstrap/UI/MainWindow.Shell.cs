using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SukiUI.Controls;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private bool _startupSequenceStarted;
        private bool _pendingEnvironmentScanErrorDialog;
        private bool _isRestoringSideMenuSelection;
        private bool _isNavigationLocked;
        private ShellPage _selectedPage = ShellPage.Launch;
        private SukiSideMenuItem _lastUnlockedSideMenuItem;
        private readonly object _busySync = new object();
        private readonly List<BusyEntry> _busyEntries = new List<BusyEntry>();
        private int _nextBusyId;
        private void ApplyGlobalBusyStateToUi(bool isBusy, string text)
        {
            if (GlobalBusyArea == null)
            {
                return;
            }

            GlobalBusyArea.IsBusy = isBusy;
            GlobalBusyArea.BusyText = text ?? string.Empty;
        }

        private void ApplyRuntimeProgressStateToUi(bool isBusy, string text, double progressValue)
        {
            if (RuntimeInstallOverlay != null)
            {
                RuntimeInstallOverlay.IsVisible = isBusy;
                RuntimeInstallOverlay.Opacity = isBusy ? 1 : 0;
            }

            SetRuntimeInstallProgress(text, progressValue);
        }

        private void OnMainSideMenuSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRestoringSideMenuSelection || MainSideMenu == null)
            {
                return;
            }

            if (MainSideMenu.SelectedItem is not SukiSideMenuItem selectedItem)
            {
                return;
            }

            if (_isNavigationLocked)
            {
                RestoreLockedSideMenuSelection();
                return;
            }

            _lastUnlockedSideMenuItem = selectedItem;
            _selectedPage = ResolveShellPage(selectedItem);
            OnSelectedPageChanged();
        }

        private void ApplySideMenuNavigationLock()
        {
            if (MainSideMenu == null)
            {
                return;
            }

            var items = MainSideMenu.Items?
                .OfType<SukiSideMenuItem>()
                .ToList() ?? new List<SukiSideMenuItem>();

            if (!_isNavigationLocked)
            {
                foreach (var item in items)
                {
                    item.IsEnabled = true;
                }

                if (MainSideMenu.SelectedItem is SukiSideMenuItem selectedItem)
                {
                    _lastUnlockedSideMenuItem = selectedItem;
                    _selectedPage = ResolveShellPage(selectedItem);
                }

                return;
            }

            if (_lastUnlockedSideMenuItem == null)
            {
                _lastUnlockedSideMenuItem = MainSideMenu.SelectedItem as SukiSideMenuItem
                    ?? items.FirstOrDefault();
            }

            foreach (var item in items)
            {
                item.IsEnabled = ReferenceEquals(item, _lastUnlockedSideMenuItem);
            }

            RestoreLockedSideMenuSelection();
        }

        private void RestoreLockedSideMenuSelection()
        {
            if (MainSideMenu == null || _lastUnlockedSideMenuItem == null)
            {
                return;
            }

            if (ReferenceEquals(MainSideMenu.SelectedItem, _lastUnlockedSideMenuItem))
            {
                return;
            }

            try
            {
                _isRestoringSideMenuSelection = true;
                MainSideMenu.SelectedItem = _lastUnlockedSideMenuItem;
            }
            finally
            {
                _isRestoringSideMenuSelection = false;
            }
        }

        private ShellPage ResolveShellPage(SukiSideMenuItem selectedItem)
        {
            if (selectedItem?.Tag is ShellPage page)
            {
                return page;
            }

            return _selectedPage;
        }

        private void OnSelectedPageChanged()
        {
            if (_selectedPage == ShellPage.Settings)
            {
                OnSettingsPageSelected();
            }
        }

        private async void OnWindowOpened(object sender, EventArgs e)
        {
            try
            {
                Opened -= OnWindowOpened;

                if (_appConfig.IsReadOnlySession)
                {
                    await ShowConfigReadOnlyDialogAsync();
                }

                if (_pendingEnvironmentScanErrorDialog)
                {
                    _pendingEnvironmentScanErrorDialog = false;
                    await ShowEnvironmentScanErrorDialogAsync();
                }

                if (_pendingNativeTouchDeprecatedDialog)
                {
                    _pendingNativeTouchDeprecatedDialog = false;
                    await ShowMessageDialogAsync(
                        "“原生触控输入”选项已弃用",
                        "新版本spice2x已默认启用原生触控，现有选项已自动关闭并移除",
                        "我知道了");
                }
                else if (!string.IsNullOrWhiteSpace(_pendingNativeTouchMigrationError))
                {
                    string error = _pendingNativeTouchMigrationError;
                    _pendingNativeTouchMigrationError = string.Empty;
                    await ShowMessageDialogAsync(
                        "旧选项处理失败",
                        error,
                        "我知道了",
                        NotificationType.Warning);
                }

                QueueAutoLaunchIfEnabled();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startup dialog display failed.");
            }
        }

        private void QueueAutoLaunchIfEnabled()
        {
            if (!_settingsState.AutoLaunch)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_settingsState.AutoLaunch && _launchUiState.CanStartLaunch)
                {
                    _ = StartLaunchAsync(false);
                }
            }, DispatcherPriority.Background);
        }

        private async Task ShowConfigReadOnlyDialogAsync()
        {
            string reason = _appConfig.ReadOnlyReason;
            string content =
                "config.toml 被占用或无法读取，当前会话将使用临时内存配置。\n\n" +
                "你仍可继续使用程序，但所有修改将无法保存。";

            if (!string.IsNullOrWhiteSpace(reason))
            {
                content += $"\n\n原因：{reason}";
            }

            await ShowMessageDialogAsync(
                "配置文件无法保存",
                content,
                "我知道了",
                NotificationType.Warning,
                "Flat");
        }

        private async Task ShowEnvironmentScanErrorDialogAsync()
        {
            const string errorContent =
                "(*´ - `*)∩ 啊哇哇。。。Near 检测到你的系统可能缺少必要的运行环境！\n\n" +
                "(∩^-^)∩(∩^-^)∩ Noah 建议的操作步骤：\n" +
                "- 在工具页点击「安装运行库」按钮安装必要运行环境\n" +
                "- 确保已安装最新的显卡驱动程序\n" +
                "- 如为 AMD/Intel 显卡请启用“显卡兼容层”功能\n\n" +
                "如“系统媒体功能包”异常：\n" +
                "- 检查“Windows 设置”中是否已启用“媒体功能包”\n\n" +
                "请注意！由于硬件不同，检查结果可能会误报！\n" +
                "如果所有游戏运行正常没有问题，请忽略以上提示。";

            bool openDiagPage = await ShowDialogAsync(
                "环境检查提示",
                errorContent,
                "查看异常项",
                "关闭",
                NotificationType.Error,
                "Flat");

            if (openDiagPage)
            {
                GoToDiagPageCore();
            }
        }

        private void GoToDiagPageCore()
        {
            try
            {
                if (MainSideMenu == null)
                {
                    return;
                }

                var target = MainSideMenu.Items?
                    .OfType<SukiSideMenuItem>()
                    .FirstOrDefault(item => item.Tag is ShellPage.Diag);

                if (target != null)
                {
                    MainSideMenu.SelectedItem = target;
                }
            }
            catch
            {
            }
        }

        internal async Task PrepareForDisplayAsync()
        {
            if (_startupSequenceStarted)
            {
                return;
            }

            _startupSequenceStarted = true;

            try
            {
                await InitializeSettingsStartupAsync();
                await InitializeLaunchStartupAsync();

                await WarmSettingsDeferredAsync();
                await WarmDisplayDeferredAsync();
                await InitializeDiagnosticStartupAsync();
                ApplyAboutVersion();
                InitializeDisplayLayoutControls();
                _pendingEnvironmentScanErrorDialog = HasEnvironmentScanErrors;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pre-show initialization failed.");
                throw;
            }

        }

        private void InitializeCustomComponents()
        {
            _isLoadingSettings = true;
            InitializeSettingsComponents();
            _isLoadingSettings = false;

            InitializeExitRestoreBinding();
            HideLaunchLogArea(clearOutput: true);
            InitializeLaunchControls();

            FinalizeInitialViewState();
        }

        private void FinalizeInitialViewState()
        {
            ApplyGlobalBusyStateToUi(false, string.Empty);
            ApplyRuntimeProgressStateToUi(false, string.Empty, 0d);
            ApplySideMenuNavigationLock();

            Closing += OnWindowClosing;
        }

        private BusyLease BeginBusy(BusyPresentation presentation, string text = "", double progressValue = 0d)
        {
            BusyEntry entry;
            lock (_busySync)
            {
                entry = new BusyEntry(++_nextBusyId, presentation, text, progressValue);
                _busyEntries.Add(entry);
            }

            RefreshBusyState();
            return new BusyLease(this, entry.Id);
        }

        private void SetNavigationLocked(bool locked)
        {
            if (_isNavigationLocked == locked)
            {
                return;
            }

            _isNavigationLocked = locked;
            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplySideMenuNavigationLock();
            }
            else
            {
                Dispatcher.UIThread.Post(ApplySideMenuNavigationLock);
            }
        }

        private void UpdateBusy(int id, string text, double? progressValue)
        {
            lock (_busySync)
            {
                var entry = _busyEntries.FirstOrDefault(candidate => candidate.Id == id);
                if (entry == null)
                {
                    return;
                }

                entry.Text = text ?? string.Empty;
                if (progressValue.HasValue)
                {
                    entry.ProgressValue = Math.Clamp(progressValue.Value, 0d, 100d);
                }
            }

            RefreshBusyState();
        }

        private void EndBusy(int id)
        {
            lock (_busySync)
            {
                _busyEntries.RemoveAll(entry => entry.Id == id);
            }

            RefreshBusyState();
        }

        private void RefreshBusyState()
        {
            BusyEntry global;
            BusyEntry runtime;
            bool navigationLocked;
            lock (_busySync)
            {
                global = _busyEntries.LastOrDefault(entry => entry.Presentation == BusyPresentation.GlobalOverlay);
                runtime = _busyEntries.LastOrDefault(entry => entry.Presentation == BusyPresentation.RuntimeProgress);
                navigationLocked = _busyEntries.Any(entry => entry.Presentation == BusyPresentation.NavigationLock);
            }

            void Apply()
            {
                ApplyGlobalBusyStateToUi(global != null, global?.Text ?? string.Empty);
                ApplyRuntimeProgressStateToUi(runtime != null, runtime?.Text ?? string.Empty, runtime?.ProgressValue ?? 0d);
                SetNavigationLocked(navigationLocked);
            }

            if (Dispatcher.UIThread.CheckAccess())
            {
                Apply();
            }
            else
            {
                Dispatcher.UIThread.Post(Apply);
            }
        }

        private enum BusyPresentation
        {
            GlobalOverlay,
            NavigationLock,
            RuntimeProgress
        }

        private sealed class BusyEntry
        {
            public BusyEntry(int id, BusyPresentation presentation, string text, double progressValue)
            {
                Id = id;
                Presentation = presentation;
                Text = text ?? string.Empty;
                ProgressValue = Math.Clamp(progressValue, 0d, 100d);
            }

            public int Id { get; }
            public BusyPresentation Presentation { get; }
            public string Text { get; set; }
            public double ProgressValue { get; set; }
        }

        private sealed class BusyLease : IDisposable
        {
            private MainWindow _owner;
            private readonly int _id;

            public BusyLease(MainWindow owner, int id)
            {
                _owner = owner;
                _id = id;
            }

            public void UpdateText(string text) => _owner?.UpdateBusy(_id, text, null);

            public void UpdateProgress(string text, double progressValue) =>
                _owner?.UpdateBusy(_id, text, progressValue);

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                owner?.EndBusy(_id);
            }
        }
    }
}
