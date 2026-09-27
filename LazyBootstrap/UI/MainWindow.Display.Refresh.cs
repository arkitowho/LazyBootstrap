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
        private long _displayRevision;
        private bool _isRefreshingDisplays;
        private bool _displayTransactionActive;
        private Task _displayRefreshTask = Task.CompletedTask;
        private bool _displayRefreshQueued;
        private string _pendingDisplayRefreshReason;

        private bool IsDisplayDetectionPaused => _displayRefreshCoordinator?.IsPaused == true;

        private void SynchronizeDisplayDetectionWithLaunchState()
        {
            if (_displayRefreshCoordinator == null || _displayRefreshCoordinator.IsDisposed) return;
            if (!_displayRefreshCoordinator.SetLaunchState(_launchUiState.IsLaunching,
                    _launchUiState.IsGameRunning, _launchWorkflowCts != null)) return;
            _displayRevision++;
            UpdateDisplayLayoutControlsEnabled();
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
            if (_displayRefreshCoordinator.IsDisposed ||
                _allowImmediateWindowClose || _isWindowCloseAnimationRunning) return;
            _pendingDisplayRefreshReason = reason;
            DispatchPendingDisplayRefresh();
        }

        private void DispatchPendingDisplayRefresh()
        {
            if (_pendingDisplayRefreshReason == null || _displayRefreshQueued ||
                _isRefreshingDisplays || _displayTransactionActive || IsDisplayDetectionPaused || _displayRefreshCoordinator.IsDisposed) return;
            _displayRefreshQueued = true;
            // Coalesce deferred refresh requests after launch/restore cleanup, without polling or retries.
            Dispatcher.UIThread.Post(() =>
            {
                _displayRefreshQueued = false;
                if (_pendingDisplayRefreshReason == null || _isRefreshingDisplays || _displayTransactionActive ||
                    IsDisplayDetectionPaused || _displayRefreshCoordinator.IsDisposed) return;
                _ = RequestDisplayListRefreshAsync(_pendingDisplayRefreshReason);
            }, DispatcherPriority.Background);
        }

        private void DisposeDisplayRefresh()
        {
            _displayRevision++;
            _displayRefreshCoordinator?.Dispose();
            _pendingDisplayRefreshReason = null;
        }

        private async void OnRefreshDisplaysClick(object sender, RoutedEventArgs e)
            => await RequestDisplayListRefreshAsync("Manual");

        private async Task RequestDisplayListRefreshAsync(string reason, bool waitForCurrent = false)
        {
            if (IsDisplayDetectionPaused)
            {
                _pendingDisplayRefreshReason = reason;
                return;
            }
            // A refresh started before a transaction may be discarded by the revision check.
            // Wait for it before reading the post-transaction state once.
            while (_isRefreshingDisplays)
            {
                if (!waitForCurrent) return;
                await _displayRefreshTask;
            }
            if (IsDisplayDetectionPaused)
            {
                _pendingDisplayRefreshReason = reason;
                return;
            }
            if (_displayRefreshCoordinator.IsDisposed || _displayTransactionActive ||
                _allowImmediateWindowClose || _isWindowCloseAnimationRunning) return;
            _pendingDisplayRefreshReason = null;
            _displayRefreshTask = RefreshDisplayListWithStatusAsync(reason);
            await _displayRefreshTask;
        }

        private async Task RefreshDisplayListWithStatusAsync(string reason)
        {
            _isRefreshingDisplays = true;
            UpdateDisplayLayoutControlsEnabled();
            try
            {
                await RefreshDisplaysAsync(reason);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Display refresh failed. Reason={Reason}", reason);
                if (!_displayRefreshCoordinator.IsDisposed && !IsDisplayDetectionPaused)
                    DisplayDiscoveryStatusText.Text = "检测失败，请稍后重新检测。";
            }
            finally
            {
                _isRefreshingDisplays = false;
                if (!_displayRefreshCoordinator.IsDisposed)
                {
                    UpdateDisplayLayoutControlsEnabled();
                    if (DisplayDiscoveryStatusText.Text == "正在检测显示器…") UpdateDisplayDiscoveryStatus();
                    DispatchPendingDisplayRefresh();
                }
            }
        }

        private async Task RefreshDisplaysAsync(string reason)
        {
            if (_displayTransactionActive || IsDisplayDetectionPaused) return;
            long revision = ++_displayRevision;
            DisplayDiscoveryStatusText.Text = "正在检测显示器…";
            var result = await _displayRefreshCoordinator.RunAsync(_displayConfigurationService.GetDisplays);
            if (_displayTransactionActive || IsDisplayDetectionPaused || revision != _displayRevision || _displayRefreshCoordinator.IsDisposed) return;
            _logger.LogInformation("Display discovery refreshed. Reason={Reason}, Status={Status}, Adapters={Adapters}, Desktop={Desktop}, Count={Count}, Error={Error}",
                reason, result.Status, result.AdapterCount, result.DesktopCount, result.Displays.Count, result.ErrorMessage);
            foreach (var display in result.Displays)
                _logger.LogDebug("Display mapping: {Identity} -> {Device}", display.PersistentId, display.DeviceName);
            ApplyDiscoveredDisplays(result);
            ApplyDisplayStateToUi();
            await HandleConfigurationChangedAsync(_displayState, true, true, persist: false);
            if (_displayRefreshCoordinator.IsDisposed || _displayTransactionActive || IsDisplayDetectionPaused) return;
            ApplyDisplayStateToUi();
            UpdateDisplayDiscoveryStatus();
        }

        private void UpdateDisplayDiscoveryStatus()
        {
            if (_displayRefreshCoordinator.IsDisposed) return;
            if (IsDisplayDetectionPaused)
            {
                DisplayDiscoveryStatusText.Text = "游戏启动或运行中，显示器检测已暂停。";
                return;
            }
            DisplayDiscoveryStatusText.Text = string.IsNullOrWhiteSpace(_displayCatalog.StatusMessage)
                ? $"已检测到 {_displayCatalog.Displays.Count} 个显示输出"
                : "检测暂不完整，已保留原列表；可点击重新检测。";
            ToolTip.SetTip(DisplayDiscoveryStatusText, _displayCatalog.StatusMessage);
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
