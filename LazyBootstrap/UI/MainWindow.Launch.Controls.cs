using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using SukiUI.Controls;
using SukiUI.MessageBox;
using LazyBootstrap.Platform;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private bool _isLaunchLogVisible;
        private bool _isLaunchLogAppendAnimating;
        private bool _isLaunchLogAppendAnimationPending;

        /// <summary>Initializes the launch workflow for the startup sequence (invoked by the shell before showing).</summary>
        private Task InitializeLaunchStartupAsync()
            => InitializeLaunchWorkflowAsync(_launchUiState);

        /// <summary>Runs the launch-related cleanup that must complete before the window closes.</summary>
        private Task HandleLaunchClosingAsync()
            => HandleLaunchWorkflowClosingAsync(BuildDisplayConfigurationRequest());

        private void OnToggleLaunchLogClick(object sender, RoutedEventArgs e)
            => _ = ToggleLaunchLogAsync(_launchUiState);

        private void OnOpenLogClick(object sender, RoutedEventArgs e) => _ = ShowLaunchLogDialogAsync();

        private async Task ShowLaunchLogDialogAsync()
        {
            var document = await LoadLaunchLogAsync();
            if (document == null)
            {
                return;
            }

            var logViewer = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                Text = document.Content,
                MinWidth = 960,
                MinHeight = 520,
                MaxWidth = 1200,
                MaxHeight = 680,
                [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
                [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto
            };
            logViewer.CaretIndex = logViewer.Text?.Length ?? 0;

            var openFolderButton = SukiMessageBoxButtonsFactory.CreateButton(
                "打开日志文件夹",
                SukiMessageBoxResult.Yes,
                "Flat");
            var result = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
            {
                UseAlternativeHeaderStyle = true,
                IconPreset = SukiMessageBoxIcons.Information,
                Header = "log.txt",
                Content = logViewer,
                FooterLeftItemsSource = [new SelectableTextBlock { Text = $"路径: {document.Path}" }],
                ActionButtonsSource = [openFolderButton]
            });

            if (result is SukiMessageBoxResult.Yes)
            {
                ProcessExecutionHelper.OpenLogFolderAndSelectFile(document.Path);
            }
        }

        private void OnKillProcessesClick(object sender, RoutedEventArgs e)
            => _ = StopAndKillProcessesAsync();

        private void OnStartClick(object sender, RoutedEventArgs e)
            => _ = StartLaunchAsync(false);

        private void OnStartAsphyxiaDevClick(object sender, RoutedEventArgs e)
            => _ = StartLaunchAsync(true);

        private async Task StartLaunchAsync(bool asphyxiaDevOnly)
        {
            if (!_launchUiState.CanStartLaunch || _launchWorkflowLifetime.IsBusy || _isWindowCloseAnimationRunning || _allowImmediateWindowClose)
            {
                return;
            }

            if (_displayRestoreStates.Count > 0)
            {
                ShowWarningToast("显示器还原未完成", "请先点击停止，重试还原显示器设置后再启动。");
                return;
            }
            try
            {
                await _launchWorkflowLifetime.RunAsync(() => RunLaunchWorkflowAsync(
                    _launchUiState,
                    new LaunchRequest(
                        _settingsState.NoAsphyxia,
                        _settingsState.UseSystemSpiceConfig,
                        _settingsState.DisableSpiceFso,
                        _settingsState.ServerAddress,
                        BuildDisplayConfigurationRequest(),
                        asphyxiaDevOnly)));
            }
            finally { NotifyLaunchStateChanged(_launchUiState); }
        }

        private void InitializeLaunchControls()
        {
            RemoveHandler(InputElement.PointerPressedEvent, OnLaunchMessageOverlayPointerPressed);
            AddHandler(InputElement.PointerPressedEvent, OnLaunchMessageOverlayPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            ApplyLaunchStateToUi();
            _ = ApplyLaunchLogVisibilityAsync();
            _ = ApplyLaunchLogTextAsync();
            _ = ApplyLaunchMessageOverlayAsync();
        }

        private void ReleaseLaunchControls()
        {
            RemoveHandler(InputElement.PointerPressedEvent, OnLaunchMessageOverlayPointerPressed);
            ArcadeMessageOverlay?.StopAnimation();
        }

        private void ApplyLaunchStateToUi()
        {
            bool canStart = _launchUiState.CanStartLaunch && !_launchWorkflowLifetime.IsBusy && !_isWindowCloseAnimationRunning;
            SetNavigationLocked(!canStart);

            if (StartButton != null)
            {
                StartButton.IsEnabled = canStart;
            }

            if (StartAsphyxiaDevMenuItem != null)
            {
                StartAsphyxiaDevMenuItem.IsEnabled = canStart;
            }
        }

        private async Task ApplyLaunchLogVisibilityAsync()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await ApplyLaunchLogVisibilityAsync());
                return;
            }

            if (_launchUiState.IsLaunchLogVisible)
            {
                await ShowLaunchLogAreaWithAnimationAsync(syncState: false);
                return;
            }

            HideLaunchLogArea(syncState: false);
        }

        private async Task ApplyLaunchLogTextAsync()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await ApplyLaunchLogTextAsync());
                return;
            }

            if (LogOutputTextBlock == null)
            {
                return;
            }

            LogOutputTextBlock.Text = _launchUiState.LaunchLogText ?? string.Empty;

            if (LaunchLogScrollViewer != null)
            {
                LaunchLogScrollViewer.Offset = new Vector(LaunchLogScrollViewer.Offset.X, double.MaxValue);
            }

            if (!string.IsNullOrWhiteSpace(LogOutputTextBlock.Text))
            {
                await AnimateLaunchLogAppendAsync();
            }
        }

        private async Task AnimateLaunchLogAppendAsync()
        {
            if (LogOutputTextBlock == null)
            {
                return;
            }

            if (_isLaunchLogAppendAnimating)
            {
                _isLaunchLogAppendAnimationPending = true;
                return;
            }

            _isLaunchLogAppendAnimating = true;
            try
            {
                do
                {
                    _isLaunchLogAppendAnimationPending = false;
                    LogOutputTextBlock.RenderTransformOrigin = Avalonia.RelativePoint.Center;
                    var scale = LogOutputTextBlock.RenderTransform as ScaleTransform;
                    if (scale == null)
                    {
                        scale = new ScaleTransform(0.985, 0.985);
                        LogOutputTextBlock.RenderTransform = scale;
                    }

                    LogOutputTextBlock.Opacity = 0.55;
                    scale.ScaleX = 0.985;
                    scale.ScaleY = 0.985;

                    const int steps = 6;
                    for (int i = 0; i <= steps; i++)
                    {
                        double t = (double)i / steps;
                        double eased = 1 - Math.Pow(1 - t, 3);
                        LogOutputTextBlock.Opacity = 0.55 + (0.45 * eased);
                        double currentScale = 0.985 + (0.015 * eased);
                        scale.ScaleX = currentScale;
                        scale.ScaleY = currentScale;
                        await Task.Delay(12);
                    }

                    LogOutputTextBlock.Opacity = 1;
                    scale.ScaleX = 1;
                    scale.ScaleY = 1;
                }
                while (_isLaunchLogAppendAnimationPending);
            }
            finally
            {
                _isLaunchLogAppendAnimating = false;
            }
        }

        private async Task ShowLaunchLogAreaWithAnimationAsync(bool syncState = true)
        {
            if (LaunchLogContainer == null)
            {
                return;
            }

            if (_isLaunchLogVisible && LaunchLogContainer.IsVisible)
            {
                return;
            }

            _isLaunchLogVisible = true;
            if (syncState)
            {
                _launchUiState.IsLaunchLogVisible = true;
                _launchUiState.ToggleLaunchLogText = "隐藏启动日志";
            }

            if (ToggleLaunchLogButton != null)
            {
                ToggleLaunchLogButton.Content = _launchUiState.ToggleLaunchLogText;
            }

            LaunchLogContainer.IsVisible = true;
            LaunchLogContainer.Opacity = 0;
            LaunchLogContainer.RenderTransformOrigin = Avalonia.RelativePoint.TopLeft;

            var scale = LaunchLogContainer.RenderTransform as ScaleTransform;
            if (scale == null)
            {
                scale = new ScaleTransform(0.12, 0.12);
                LaunchLogContainer.RenderTransform = scale;
            }

            scale.ScaleX = 0.12;
            scale.ScaleY = 0.12;

            const int steps = 14;
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                double eased = 1 - Math.Pow(1 - t, 3);
                double currentScale = 0.12 + (0.88 * eased);
                LaunchLogContainer.Opacity = eased;
                scale.ScaleX = currentScale;
                scale.ScaleY = currentScale;
                await Task.Delay(16);
            }

            LaunchLogContainer.Opacity = 1;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        }

        private void HideLaunchLogArea(bool clearOutput = false, bool syncState = true)
        {
            _isLaunchLogVisible = false;
            if (syncState)
            {
                _launchUiState.IsLaunchLogVisible = false;
                _launchUiState.ToggleLaunchLogText = "显示启动日志";
            }

            if (ToggleLaunchLogButton != null)
            {
                ToggleLaunchLogButton.Content = _launchUiState.ToggleLaunchLogText;
            }

            if (LaunchLogContainer == null)
            {
                return;
            }

            LaunchLogContainer.IsVisible = false;
            LaunchLogContainer.Opacity = 0;
            LaunchLogContainer.RenderTransformOrigin = Avalonia.RelativePoint.TopLeft;
            LaunchLogContainer.RenderTransform = new ScaleTransform(0.12, 0.12);

            if (clearOutput)
            {
                ClearLaunchOutput(syncState);
            }
        }

        private void ClearLaunchOutput(bool syncState = true)
        {
            if (syncState)
            {
                _launchUiState.LaunchLogText = string.Empty;
            }

            if (LogOutputTextBlock != null)
            {
                LogOutputTextBlock.Text = _launchUiState.LaunchLogText ?? string.Empty;
            }
        }

        private async Task ApplyLaunchMessageOverlayAsync()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(async () => await ApplyLaunchMessageOverlayAsync());
                return;
            }

            if (_launchUiState.IsMessageVisible)
            {
                ShowLaunchMessageOverlay();
                return;
            }

            HideLaunchMessageOverlay();
        }

        private void ShowLaunchMessageOverlay()
        {
            if (ArcadeMessageOverlay == null)
            {
                return;
            }

            ArcadeMessageOverlay.Show(
                _launchUiState.MessageType,
                _launchUiState.MessageTitle,
                _launchUiState.MessageAccentText,
                _launchUiState.MessageBodyText);
        }

        private void DismissLaunchMessageOverlay()
        {
            if (!_launchUiState.IsMessageVisible)
            {
                return;
            }

            HideLaunchMessageOverlay();
            _ = DismissLaunchMessageAsync(_launchUiState);
        }

        private void HideLaunchMessageOverlay()
        {
            ArcadeMessageOverlay?.Hide();
        }

        private void OnLaunchMessageOverlayPointerPressed(object sender, PointerPressedEventArgs e)
        {
            if (!_launchUiState.IsMessageVisible)
            {
                return;
            }

            DismissLaunchMessageOverlay();
            e.Handled = true;
        }
    }
}
