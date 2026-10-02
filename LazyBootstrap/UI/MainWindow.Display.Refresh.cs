using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Services;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private readonly DisplayCatalog _displayCatalog = new();
        private DisplayRefreshCoordinator _displayRefreshCoordinator;
        private readonly DisplayInitialization _displayInitialization;
        private DisplayRefreshOutcome _lastDisplayRefreshOutcome = DisplayRefreshOutcome.Deferred;
        private long _displayRevision;
        private bool _isRefreshingDisplays;
        private bool _displayTransactionActive;
        private Task<DisplayRefreshOutcome> _displayRefreshTask = Task.FromResult(DisplayRefreshOutcome.Deferred);
        private bool _displayRefreshQueued;
        private string _pendingDisplayRefreshReason;

        private bool IsDisplayDetectionPaused => _displayRefreshCoordinator?.IsPaused == true;

        private void SynchronizeDisplayDetectionWithConfiguration()
        {
            _displayRefreshCoordinator.SetConfigurationEnabled(_displayState.IsDisplayConfigurationEnabled);
            _displayRevision++;
            if (!_displayState.IsDisplayConfigurationEnabled)
            {
                _pendingDisplayRefreshReason = null;
                _displayInitialization.Cancel();
            }
            UpdateDisplayDiscoveryStatus();
        }

        private void SynchronizeDisplayDetectionWithLaunchState()
        {
            if (_displayRefreshCoordinator == null || _displayRefreshCoordinator.IsDisposed) return;
            if (!_displayRefreshCoordinator.SetLaunchState(_launchUiState.IsLaunching,
                    _launchUiState.IsGameRunning, _launchWorkflowLifetime.IsBusy)) return;
            _displayRevision++;
            UpdateDisplayLayoutControlsEnabled();
            if (!_displayState.IsDisplayConfigurationEnabled)
            {
                _pendingDisplayRefreshReason = null;
                UpdateDisplayDiscoveryStatus();
                return;
            }
            if (IsDisplayDetectionPaused)
            {
                _pendingDisplayRefreshReason = "LaunchCompleted";
                DisplayDiscoveryStatusText.Text = "游戏启动或运行中，显示器检测已暂停。";
            }
            else
            {
                QueueDisplayListRefresh("LaunchCompleted");
            }
        }

        private void QueueDisplayListRefresh(string reason)
        {
            if (!_displayState.IsDisplayConfigurationEnabled || _displayRefreshCoordinator.IsDisposed ||
                _allowImmediateWindowClose || _isWindowCloseAnimationRunning) return;
            _pendingDisplayRefreshReason = reason;
            DispatchPendingDisplayRefresh();
        }

        private void DispatchPendingDisplayRefresh()
        {
            if (!_displayState.IsDisplayConfigurationEnabled || _pendingDisplayRefreshReason == null || _displayRefreshQueued ||
                _isRefreshingDisplays || _displayTransactionActive || IsDisplayDetectionPaused || _displayRefreshCoordinator.IsDisposed) return;
            _displayRefreshQueued = true;
            // Coalesce deferred refresh requests after launch/restore cleanup, without polling or retries.
            Dispatcher.UIThread.Post(() =>
            {
                _displayRefreshQueued = false;
                if (!_displayState.IsDisplayConfigurationEnabled || _pendingDisplayRefreshReason == null || _isRefreshingDisplays || _displayTransactionActive ||
                    IsDisplayDetectionPaused || _displayRefreshCoordinator.IsDisposed) return;
                _ = RequestDisplayListRefreshAsync(_pendingDisplayRefreshReason);
            }, DispatcherPriority.Background);
        }

        private void DisposeDisplayRefresh()
        {
            _displayRevision++;
            _displayInitialization?.Cancel();
            _displayRefreshCoordinator?.Dispose();
            _pendingDisplayRefreshReason = null;
        }

        private async void OnRefreshDisplaysClick(object sender, RoutedEventArgs e)
            => await RequestDisplayListRefreshAsync("Manual");

        private async Task<DisplayRefreshOutcome> RequestDisplayListRefreshAsync(string reason, bool waitForCurrent = false)
        {
            long generation = _displayInitialization.Generation;
            if (!_displayState.IsDisplayConfigurationEnabled || _displayRefreshCoordinator.IsDisposed || _isWindowCloseAnimationRunning || _allowImmediateWindowClose)
                return DisplayRefreshOutcome.Canceled;
            if (IsDisplayDetectionPaused)
            {
                _pendingDisplayRefreshReason = reason;
                return DisplayRefreshOutcome.Deferred;
            }
            // A refresh started before a transaction may be discarded by the revision check.
            // Wait for it before reading the post-transaction state once.
            while (_isRefreshingDisplays)
            {
                if (!waitForCurrent) return DisplayRefreshOutcome.Deferred;
                await _displayRefreshTask;
            }
            if (!_displayState.IsDisplayConfigurationEnabled || generation != _displayInitialization.Generation) return DisplayRefreshOutcome.Canceled;
            if (IsDisplayDetectionPaused)
            {
                _pendingDisplayRefreshReason = reason;
                return DisplayRefreshOutcome.Deferred;
            }
            if (_displayRefreshCoordinator.IsDisposed || _allowImmediateWindowClose || _isWindowCloseAnimationRunning)
                return DisplayRefreshOutcome.Canceled;
            if (_displayTransactionActive)
            {
                _pendingDisplayRefreshReason = reason;
                return DisplayRefreshOutcome.Deferred;
            }
            _pendingDisplayRefreshReason = null;
            _displayRefreshTask = RefreshDisplayListWithStatusAsync(reason);
            return await _displayRefreshTask;
        }

        private async Task<DisplayRefreshOutcome> RefreshDisplayListWithStatusAsync(string reason)
        {
            _isRefreshingDisplays = true;
            UpdateDisplayLayoutControlsEnabled();
            UpdateDisplayDiscoveryStatus();
            try
            {
                _lastDisplayRefreshOutcome = await RefreshDisplaysAsync(reason);
            }
            catch (OperationCanceledException) { _lastDisplayRefreshOutcome = DisplayRefreshOutcome.Canceled; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Display refresh failed. Reason={Reason}", reason);
                _lastDisplayRefreshOutcome = DisplayRefreshOutcome.Failed;
            }
            finally
            {
                _isRefreshingDisplays = false;
                if (!_displayRefreshCoordinator.IsDisposed)
                {
                    UpdateDisplayLayoutControlsEnabled();
                    UpdateDisplayDiscoveryStatus();
                    DispatchPendingDisplayRefresh();
                }
            }
            return _lastDisplayRefreshOutcome;
        }

        private async Task<DisplayRefreshOutcome> RefreshDisplaysAsync(string reason)
        {
            if (!_displayState.IsDisplayConfigurationEnabled) return DisplayRefreshOutcome.Canceled;
            if (_displayTransactionActive || IsDisplayDetectionPaused) return DisplayRefreshOutcome.Deferred;
            long generation = _displayInitialization.Generation;
            long revision = ++_displayRevision;
            var result = await _displayRefreshCoordinator.RunAsync(_displayConfigurationService.GetDisplays);
            if (_displayTransactionActive || IsDisplayDetectionPaused || revision != _displayRevision || _displayRefreshCoordinator.IsDisposed) return DisplayRefreshOutcome.Canceled;
            _logger.LogInformation("Display discovery refreshed. Reason={Reason}, Status={Status}, Adapters={Adapters}, Desktop={Desktop}, Count={Count}, Error={Error}",
                reason, result.Status, result.AdapterCount, result.DesktopCount, result.Displays.Count, result.ErrorMessage);
            foreach (var display in result.Displays)
                _logger.LogDebug("Display mapping: {Identity} -> {Device}", display.PersistentId, display.DeviceName);
            ApplyDiscoveredDisplays(result);
            ApplyDisplayStateToUi();
            var outcome = await HandleConfigurationChangedAsync(_displayState, true, true, persist: false);
            if (_displayRefreshCoordinator.IsDisposed || _displayTransactionActive || IsDisplayDetectionPaused ||
                !_displayState.IsDisplayConfigurationEnabled || generation != _displayInitialization.Generation || _isWindowCloseAnimationRunning)
                return DisplayRefreshOutcome.Canceled;
            ApplyDisplayStateToUi();
            bool targetsReady = result.Status == DisplayDiscoveryStatus.Complete &&
                DisplayCatalog.ResolveFresh(result, _displayState.SelectedMainDisplay?.Display)?.IsAvailable == true &&
                (!_displayState.IsDualDisplay || DisplayCatalog.ResolveFresh(result, _displayState.SelectedSubDisplay?.Display)?.IsAvailable == true);
            _displayInitialization.TrySave(generation, outcome, targetsReady, () => PersistSelectionState(_displayState));
            return result.Status == DisplayDiscoveryStatus.Failed ? DisplayRefreshOutcome.Failed : outcome;
        }

        private void UpdateDisplayDiscoveryStatus()
        {
            if (_displayRefreshCoordinator.IsDisposed) return;
            ToolTip.SetTip(DisplayDiscoveryStatusText, null);
            if (!_displayState.IsDisplayConfigurationEnabled)
            {
                DisplayDiscoveryStatusText.Text = "显示器配置未启用，不进行检测。";
                ToolTip.SetTip(DisplayDiscoveryStatusText, null);
                return;
            }
            if (IsDisplayDetectionPaused)
            {
                DisplayDiscoveryStatusText.Text = "游戏启动或运行中，显示器检测已暂停。";
                return;
            }
            if (_isRefreshingDisplays)
            {
                DisplayDiscoveryStatusText.Text = "正在检测显示器…";
                return;
            }
            if (_lastDisplayRefreshOutcome == DisplayRefreshOutcome.Failed)
            {
                DisplayDiscoveryStatusText.Text = "检测未完成，请重新检测；已保留原配置。";
                return;
            }
            DisplayDiscoveryStatusText.Text = string.IsNullOrWhiteSpace(_displayCatalog.StatusMessage)
                ? $"已检测到 {_displayCatalog.Displays.Count} 个显示输出"
                : "检测暂不完整，已保留原列表；可点击重新检测。";
            if (!string.IsNullOrWhiteSpace(_displayCatalog.StatusMessage))
                ToolTip.SetTip(DisplayDiscoveryStatusText, "部分显示输出未能读取，请检查连接后重新检测。详情请查看日志。");
        }

        private void ApplyDiscoveredDisplays(DisplayDiscoveryResult result)
        {
            var main = _displayState.SelectedMainDisplay?.Display;
            var sub = _displayState.SelectedSubDisplay?.Display;
            _displayCatalog.Update(result);
            _displayState.Displays.Clear();
            foreach (var display in _displayCatalog.Displays)
                _displayState.Displays.Add(new DisplayChoiceOption(display, BuildDisplayLabel(display)));
            _displayState.SelectedMainDisplay = ResolveConfiguredDisplay(_displayState,
                main?.PersistentId, _displayState.LegacyMainIndex, 0);
            _displayState.SelectedSubDisplay = ResolveConfiguredDisplay(_displayState,
                sub?.PersistentId, _displayState.LegacySubIndex, Math.Min(1, Math.Max(0, _displayCatalog.Displays.Count - 1)));
        }

        private void FinishDisplayTransaction()
        {
            _displayTransactionActive = false;
            if (!_displayRefreshCoordinator.IsDisposed) UpdateDisplayLayoutControlsEnabled();
            DispatchPendingDisplayRefresh();
        }
    }
}
