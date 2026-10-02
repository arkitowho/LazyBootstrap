using System;
using SystemEnvironment = System.Environment;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Services;
using LazyBootstrap.Platform;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private const int MaxLogLines = 1200;
        private static readonly TimeSpan StartupRestartProbeDelay = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan LauncherAutoMinimizeDelay = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan AsphyxiaToSpiceLaunchDelay = TimeSpan.FromMilliseconds(300);

        private readonly SpiceCrashLogAnalyzer _spiceCrashLogAnalyzer = null!;
        private readonly GameProcessTracker _gameProcessTracker = null!;
        private readonly WindowsDefenderExclusionService _windowsDefenderExclusionService = null!;

        private readonly Queue<string> _logLines = new Queue<string>(MaxLogLines + 64);
        private Process _gameProcess;
        private CancellationTokenSource _launchWorkflowCts;
        private readonly LaunchWorkflowLifetime _launchWorkflowLifetime;
        private CancellationTokenSource _gameProcessMonitorCts;
        private bool _suppressGameProcessExitHandling;
        private readonly LaunchUiState _launchUiState = new LaunchUiState();
        private DisplayConfigurationRequest _activeLaunchDisplayConfiguration;
        private bool _openLogAfterLaunchMessageDismissal;
        private IReadOnlyDictionary<string, DisplayState> _displayRestoreStates = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);

        private Task InitializeLaunchWorkflowAsync(
            LaunchUiState launchState)
        {
            launchState.ToggleLaunchLogText = launchState.IsLaunchLogVisible ? "隐藏启动日志" : "显示启动日志";
            NotifyLaunchStateChanged(launchState);
            NotifyLaunchLogVisibilityChanged(launchState);
            NotifyLaunchMessageChanged(launchState);
            return Task.CompletedTask;
        }

        private Task DismissLaunchMessageAsync(LaunchUiState launchState)
        {
            if (launchState == null || !launchState.IsMessageVisible)
            {
                return Task.CompletedTask;
            }

            bool shouldOpenLog = _openLogAfterLaunchMessageDismissal;
            ClearLaunchMessage(launchState);

            if (shouldOpenLog)
            {
                _ = ShowLaunchLogDialogAsync();
            }

            return Task.CompletedTask;
        }

        private Task ToggleLaunchLogAsync(LaunchUiState launchState)
        {
            launchState.IsLaunchLogVisible = !launchState.IsLaunchLogVisible;
            launchState.ToggleLaunchLogText = launchState.IsLaunchLogVisible ? "隐藏启动日志" : "显示启动日志";
            NotifyLaunchLogVisibilityChanged(launchState);
            return Task.CompletedTask;
        }

        private async Task<LaunchLogDocument> LoadLaunchLogAsync()
        {
            try
            {
                string logPath = Path.Combine(_paths.GetContentsDirectoryPath(), "log.txt");
                _logger.LogInformation("Opening spice2x log viewer.");
                if (!File.Exists(logPath))
                {
                    _logger.LogWarning("spice2x log file was not found: {LogPath}", logPath);
                    ShowErrorToast("查看日志失败", $"未找到日志文件: {logPath}");
                    return null;
                }

                var content = await File.ReadAllTextAsync(logPath, SpiceLogEncoding.ShiftJis);
                if (string.IsNullOrWhiteSpace(content))
                {
                    content = "(log.txt 为空)";
                }

                return new LaunchLogDocument(logPath, content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open spice2x log viewer.");
                ShowErrorToast("查看日志失败", ex.Message);
                return null;
            }
        }

        private async Task StopAndKillProcessesAsync()
        {
            if (_launchWorkflowLifetime.IsStopping) return;
            try
            {
                ManualStopResult stopResult = null;
                await _launchWorkflowLifetime.StopAsync(() =>
                {
                    CancelLaunchWorkflow();
                    _displayTransactionCancellation?.Cancel();
                    if (_displayTransactionActive) _dialogManager.DismissDialog();
                    NotifyLaunchStateChanged(_launchUiState);
                }, async () => { stopResult = await CompleteManualStopAsync(); });
                if (stopResult == null) return;
                if (stopResult.Succeeded) ShowInfoToast("停止完成", stopResult.Message);
                else ShowWarningToast("停止未完成", stopResult.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manual launch stop cleanup failed.");
                ShowErrorToast("停止未完成", "清理失败，请检查日志后重试停止。");
            }
            finally { NotifyLaunchStateChanged(_launchUiState); }
        }

        private async Task<ManualStopResult> CompleteManualStopAsync()
        {
            if (_displayTransactionCompletion != null) await _displayTransactionCompletion.Task;
            _logger.LogInformation("Manual launch stop and process termination requested.");
            CancelLaunchWorkflow();
            CancelGameProcessMonitoring(suppressExitHandling: true);
            _suppressGameProcessExitHandling = true;
            ClearLaunchMessage(_launchUiState);

            AppendLaunchOutput(_launchUiState, "正在停止启动流程并结束所有进程...", NotificationType.Warning);

            var spiceResult = KillProcessesByName("spice64");
            var asphyxiaResult = KillProcessesByName("asphyxia-core-x64");

            DisposeTrackedGameProcess();
            _gameProcessTracker.ResetManagedAsphyxiaTracking();

            int restored = RestoreAppliedDisplaySettings("停止流程", shouldRestore: true, logToLaunchOutput: true);
            ResetLaunchRuntimeState();
            RestoreLauncherWindow();

            _logger.LogInformation(
                "Manual launch stop and process termination completed. SpiceKilled={SpiceKilled}, AsphyxiaKilled={AsphyxiaKilled}, DisplayRestored={DisplayRestored}",
                spiceResult.TerminatedCount,
                asphyxiaResult.TerminatedCount,
                restored);
            return new ManualStopResult([spiceResult, asphyxiaResult], _displayRestoreStates.Count);
        }

        private async Task RunLaunchWorkflowAsync(LaunchUiState launchState, LaunchRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);

            string launchSessionId = Guid.NewGuid().ToString("N");
            using var launchScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["LaunchSessionId"] = launchSessionId
            });

            var display = request.Display;
            bool asphyxiaDevOnly = request.AsphyxiaDevOnly;
            var launchWorkflowCts = new CancellationTokenSource();
            var cancellationToken = launchWorkflowCts.Token;

            _activeLaunchDisplayConfiguration = display;
            _displayRestoreStates = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);
            ReplaceLaunchWorkflowCancellationSource(launchWorkflowCts);
            CancelGameProcessMonitoring(suppressExitHandling: true);
            _suppressGameProcessExitHandling = false;

            ClearLaunchMessage(launchState);
            _gameProcessTracker.ResetManagedAsphyxiaTracking();
            launchState.IsLaunching = true;
            launchState.IsLaunchLogVisible = true;
            launchState.ToggleLaunchLogText = "隐藏启动日志";
            NotifyLaunchStateChanged(launchState);
            NotifyLaunchLogVisibilityChanged(launchState);
            ClearLaunchLog(launchState);
            AppendLaunchOutput(launchState, "开始启动...");
            _logger.LogInformation("Game launch workflow started. LaunchSessionId={LaunchSessionId}, AsphyxiaDevOnly={AsphyxiaDevOnly}", launchSessionId, asphyxiaDevOnly);

            bool handoffToGameSession = false;
            bool startedAsphyxiaForGame = false;
            bool displaySettingsApplied = false;

            try
            {
                // Pausing invalidates old reads; drain native calls before any game process can start.
                await _displayRefreshCoordinator.WaitForQueriesToFinishAsync(cancellationToken);
                string spicePath = _paths.GetSpicePath();
                string asphyxiaPath = _paths.GetAsphyxiaPath();
                string serverAddress = (request.ServerAddress ?? string.Empty).Trim();
                bool useSystemSpice = request.UseSystemSpiceConfig;
                bool displayConfigurationEnabled = display?.IsDisplayConfigurationEnabled == true;
                _logger.LogInformation(
                    "Launch prerequisites resolved. SpiceExists={SpiceExists}, AsphyxiaExists={AsphyxiaExists}, DisplayConfigurationEnabled={DisplayConfigurationEnabled}, UseSystemSpiceConfig={UseSystemSpiceConfig}",
                    File.Exists(spicePath),
                    File.Exists(asphyxiaPath),
                    displayConfigurationEnabled,
                    useSystemSpice);

                if (!asphyxiaDevOnly && string.IsNullOrWhiteSpace(serverAddress))
                {
                    _logger.LogWarning("Launch aborted because no server address is configured.");
                    WarnLaunchAndAbort(
                        launchState,
                        "服务器地址异常",
                        "未检测到任何 e-amusement 服务器地址存在，请前往设置页设置");
                    return;
                }

                if (!asphyxiaDevOnly && !File.Exists(spicePath))
                {
                    _logger.LogWarning("Launch aborted because spice64.exe was not found: {SpicePath}", spicePath);
                    FailLaunch(launchState, $"未找到 spice64.exe: {spicePath}", "未找到spice64.exe");
                    return;
                }

                if (!asphyxiaDevOnly && request.DisableSpiceFso)
                {
                    AppendLaunchOutput(launchState, "正在应用 spice64 全屏优化设置...");
                    if (_appCompatLayerService.TrySetFsoDisabled(spicePath, true, out var fsoError))
                    {
                        _logger.LogInformation("FSO registry setting ensured before launch.");
                        AppendLaunchOutput(launchState, "已禁用 spice64 全屏优化。");
                    }
                    else
                    {
                        _logger.LogWarning("FSO registry setting failed before launch: {Error}", fsoError);
                        AppendLaunchOutput(
                            launchState,
                            $"禁用全屏优化设置失败: {(string.IsNullOrWhiteSpace(fsoError) ? "未知错误" : fsoError)}",
                            NotificationType.Warning);
                    }
                }

                bool startAsphyxia = asphyxiaDevOnly || !request.NoAsphyxia;
                int existingAsphyxiaCount = 0;
                bool isAsphyxiaCoreAlreadyRunning = startAsphyxia && IsAsphyxiaCoreRunning(out existingAsphyxiaCount);
                _logger.LogInformation("Asphyxia launch decision resolved. StartAsphyxia={StartAsphyxia}, ExistingProcessCount={ExistingProcessCount}", startAsphyxia, existingAsphyxiaCount);

                if (startAsphyxia && !isAsphyxiaCoreAlreadyRunning && !File.Exists(asphyxiaPath))
                {
                    _logger.LogWarning("Launch aborted because asphyxia-core-x64.exe was not found: {AsphyxiaPath}", asphyxiaPath);
                    FailLaunch(launchState, $"未找到 asphyxia-core-x64.exe: {asphyxiaPath}", "未找到asphyxia-core-x64.exe");
                    return;
                }

                if (!asphyxiaDevOnly)
                {
                    AppendLaunchOutput(launchState, "正在检查 Windows Defender 排除项...");
                    var defenderResult = await _windowsDefenderExclusionService.EnsureDirectoryExcludedAsync(_paths.GetContentsDirectoryPath());
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogInformation("Windows Defender exclusion check completed. Status={Status}", defenderResult.Status);
                    AppendLaunchOutput(launchState, defenderResult.Message, defenderResult.Status == WindowsDefenderExclusionStatus.Failed ? NotificationType.Warning : NotificationType.Information);
                }

                if (!asphyxiaDevOnly && display.IsDisplayConfigurationEnabled)
                {
                    AppendLaunchOutput(launchState, "正在应用显示器配置...");
                    var displayResult = await ApplyDisplayTransactionAsync(display, cancellationToken: cancellationToken);
                    bool applySucceeded = displayResult.Succeeded;
                    displaySettingsApplied = applySucceeded;
                    var restoreStates = displayResult.RestoreStates;
                    var displayMessages = displayResult.Messages;
                    _logger.LogInformation(
                        "Display configuration apply for launch completed. Succeeded={Succeeded}, RestoreStateCount={RestoreStateCount}, MessageCount={MessageCount}",
                        applySucceeded,
                        restoreStates.Count,
                        displayMessages.Count);
                    _displayRestoreStates = restoreStates;
                    foreach (var displayMessage in displayMessages)
                    {
                        AppendLaunchOutput(launchState, displayMessage, NotificationType.Warning);
                    }

                    // Always accept restore snapshots before observing cancellation.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!applySucceeded)
                    {
                        FailLaunch(launchState, restoreStates.Count > 0
                            ? "显示器配置未能完整回滚，请检查当前显示器状态后重试。"
                            : "显示器配置失败，请检查目标显示器或重新检测后重试。");
                        return;
                    }

                    if (applySucceeded)
                    {
                        AppendLaunchOutput(launchState, "显示器配置应用完成。");
                        await Task.Delay(5000, cancellationToken);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (startAsphyxia)
                {
                    if (isAsphyxiaCoreAlreadyRunning)
                    {
                        AppendLaunchOutput(
                            launchState,
                            existingAsphyxiaCount > 1
                                ? $"检测到已有 {existingAsphyxiaCount} 个 Asphyxia Core 进程，已跳过重复启动。"
                                : "检测到已有 Asphyxia Core 进程，已跳过重复启动。",
                            NotificationType.Warning);
                    }
                    else
                    {
                        AppendLaunchOutput(launchState, "正在启动 Asphyxia Core...");
                        var asphyxiaStartInfo = asphyxiaDevOnly
                            ? new ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/k \"\"{asphyxiaPath}\" --dev\"",
                                UseShellExecute = true,
                                WorkingDirectory = Path.GetDirectoryName(asphyxiaPath)
                            }
                            : new ProcessStartInfo
                            {
                                FileName = asphyxiaPath,
                                UseShellExecute = true,
                                WorkingDirectory = Path.GetDirectoryName(asphyxiaPath)
                            };

                        var asphyxiaProcess = Process.Start(asphyxiaStartInfo);
                        if (asphyxiaProcess == null)
                        {
                            _logger.LogWarning("Asphyxia Core process creation returned null.");
                            FailLaunch(launchState, "Asphyxia 启动失败，进程未成功创建。");
                            return;
                        }

                        if (!asphyxiaDevOnly)
                        {
                            _gameProcessTracker.TrackManagedAsphyxiaProcess(asphyxiaProcess);
                            startedAsphyxiaForGame = true;
                        }

                        _logger.LogInformation("Asphyxia Core process started. ProcessId={ProcessId}, DevOnly={DevOnly}", asphyxiaProcess.Id, asphyxiaDevOnly);
                        AppendLaunchOutput(launchState, "Asphyxia Core 启动成功");
                    }
                }
                else
                {
                    AppendLaunchOutput(launchState, "已跳过启动 Asphyxia Core。", NotificationType.Warning);
                }

                if (asphyxiaDevOnly)
                {
                    launchState.IsLaunching = false;
                    launchState.IsGameRunning = false;
                    ClearLaunchMessage(launchState);
                    NotifyLaunchStateChanged(launchState);
                    AppendLaunchOutput(launchState, "已按调试模式启动 Asphyxia Core（--dev），未启动 spice64。");
                    return;
                }

                if (startedAsphyxiaForGame)
                {
                    // wait 300ms to start spice2x
                    await Task.Delay(AsphyxiaToSpiceLaunchDelay, cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var argumentsBuilder = new StringBuilder();
                string spiceArgLine = Spice64CommandLine.BuildGameLaunchArguments(useSystemSpice);
                if (!string.IsNullOrWhiteSpace(spiceArgLine))
                {
                    argumentsBuilder.Append(spiceArgLine);
                }

                AppendLaunchOutput(launchState, "正在启动游戏...");
                AppendLaunchOutput(launchState, $"启动参数: {argumentsBuilder}");

                var startInfo = new ProcessStartInfo
                {
                    FileName = spicePath,
                    Arguments = argumentsBuilder.ToString(),
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(spicePath)
                };

                _gameProcessTracker.PrepareTrackedSpiceSession(spicePath);
                _gameProcess = new Process
                {
                    StartInfo = startInfo,
                    EnableRaisingEvents = true
                };

                cancellationToken.ThrowIfCancellationRequested();
                if (!_gameProcess.Start())
                {
                    _logger.LogWarning("spice64 process start returned false.");
                    FailLaunch(launchState, "spice64 启动失败，进程未成功创建。");
                    _gameProcess.Dispose();
                    _gameProcess = null;
                    return;
                }

                _gameProcessTracker.RegisterTrackedSpiceProcess(_gameProcess);
                handoffToGameSession = true;
                _logger.LogInformation("spice64 process started. ProcessId={ProcessId}", _gameProcess.Id);

                launchState.IsLaunching = false;
                launchState.IsGameRunning = true;
                ClearLaunchMessage(launchState);
                NotifyLaunchStateChanged(launchState);
                AppendLaunchOutput(launchState, "游戏已启动并进入运行状态。");
                _ = MinimizeLauncherWindowDelayedAsync();
                StartGameProcessMonitoring();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Game launch workflow was cancelled. LaunchSessionId={LaunchSessionId}", launchSessionId);
                AppendLaunchOutput(launchState, "启动流程已停止。", NotificationType.Warning);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Game launch workflow failed.");
                FailLaunch(launchState, ex.Message, "无法启动游戏，请检查程序文件和运行权限后重试。详情请查看日志。");
            }
            finally
            {
                if (!handoffToGameSession && _gameProcessTracker.HasManagedAsphyxiaProcess())
                {
                    _logger.LogInformation("Launch did not hand off to game session. Stopping managed Asphyxia Core process.");
                    if (_gameProcessTracker.TryStopManagedAsphyxiaProcess(out var stopErrorMessage))
                    {
                        AppendLaunchOutput(launchState, "已回收本次启动的 Asphyxia Core。", NotificationType.Warning);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to stop managed Asphyxia Core process after launch abort.");
                        ShowAsphyxiaStopError(stopErrorMessage);
                    }
                }

                if (!handoffToGameSession
                    && _activeLaunchDisplayConfiguration?.IsDisplayConfigurationEnabled == true
                    && (_activeLaunchDisplayConfiguration.ExitRestore || cancellationToken.IsCancellationRequested)
                    && _displayRestoreStates.Count > 0)
                {
                    RestoreAppliedDisplaySettings("启动未完成", shouldRestore: true, logToLaunchOutput: true);
                }

                if (!handoffToGameSession)
                {
                    _logger.LogInformation("Game launch workflow ended without handoff.");
                    launchState.IsLaunching = false;
                    if (displaySettingsApplied && !cancellationToken.IsCancellationRequested && display?.ExitRestore == false)
                        _displayRestoreStates = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);
                    if (_gameProcess != null)
                    {
                        _gameProcess.Dispose();
                        _gameProcess = null;
                    }
                }

                if (ReferenceEquals(_launchWorkflowCts, launchWorkflowCts))
                {
                    _launchWorkflowCts = null;
                }

                launchWorkflowCts.Dispose();
                NotifyLaunchStateChanged(launchState);
            }
        }

        private async Task HandleLaunchWorkflowClosingAsync(DisplayConfigurationRequest display)
        {
            _displayWindowLifetime.Cancel();
            _dialogManager.DismissDialog();
            await _launchWorkflowLifetime.StopAsync(CancelLaunchWorkflow, () => CompleteWindowLaunchCleanupAsync(display));
        }

        private async Task CompleteWindowLaunchCleanupAsync(DisplayConfigurationRequest display)
        {
            if (_displayTransactionCompletion != null) await _displayTransactionCompletion.Task;
            _logger.LogInformation("Launcher closing cleanup started.");
            CancelLaunchWorkflow();
            CancelGameProcessMonitoring(suppressExitHandling: true);
            ClearLaunchMessage(_launchUiState);

            KillTrackedGameProcessDuringClose();
            DisposeTrackedGameProcess();

            if (_gameProcessTracker.HasManagedAsphyxiaProcess())
            {
                _logger.LogInformation("Stopping managed Asphyxia Core process during window close.");
                _gameProcessTracker.TryStopManagedAsphyxiaProcess(out _);
            }

            RestoreAppliedDisplaySettings("窗口关闭", shouldRestore: display?.ExitRestore == true, logToLaunchOutput: false);
            _logger.LogInformation("Launcher closing cleanup completed.");
        }

        private void ReplaceLaunchWorkflowCancellationSource(CancellationTokenSource cancellationTokenSource)
        {
            CancelLaunchWorkflow();
            _launchWorkflowCts = cancellationTokenSource;
        }

        private void CancelLaunchWorkflow()
        {
            if (_launchWorkflowCts == null)
            {
                return;
            }

            try
            {
                if (!_launchWorkflowCts.IsCancellationRequested)
                {
                    _launchWorkflowCts.Cancel();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Launch workflow cancellation failed.");
            }
        }

        private void ResetLaunchRuntimeState()
        {
            if (_launchUiState == null)
            {
                return;
            }

            _launchUiState.IsLaunching = false;
            _launchUiState.IsGameRunning = false;
            NotifyLaunchStateChanged(_launchUiState);
        }

        private void DisposeTrackedGameProcess()
        {
            if (_gameProcess == null)
            {
                return;
            }

            try
            {
                _gameProcess.Dispose();
            }
            catch
            {
            }
            finally
            {
                _gameProcess = null;
            }
        }

        private void KillTrackedGameProcessDuringClose()
        {
            try
            {
                if (_gameProcess != null && !_gameProcess.HasExited)
                {
                    _gameProcess.Kill();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to terminate game process during window close.");
            }
        }

        private int RestoreAppliedDisplaySettings(string reason, bool shouldRestore, bool logToLaunchOutput)
        {
            if (!shouldRestore || _displayRestoreStates.Count == 0)
            {
                return 0;
            }

            if (logToLaunchOutput)
            {
                AppendLaunchOutput(_launchUiState, $"{reason}，正在恢复显示器设置...");
            }

            var restoreMessages = new List<string>();
            int restored = RestoreAppliedDisplaySettings(_displayRestoreStates, restoreMessages, out var pendingRestoreStates);
            _displayRestoreStates = pendingRestoreStates;
            _logger.LogInformation(
                "Display settings restored. Reason={Reason}, RestoredCount={RestoredCount}, MessageCount={MessageCount}",
                reason,
                restored,
                restoreMessages.Count);
            foreach (var message in restoreMessages)
                _logger.LogWarning("Display restore incomplete. Reason={Reason}, Error={Error}", reason, message);

            if (logToLaunchOutput)
            {
                AppendLaunchOutput(
                    _launchUiState,
                    pendingRestoreStates.Count == 0 ? $"已恢复 {restored} 个显示器设置。" : $"已恢复 {restored} 个显示器，仍有 {pendingRestoreStates.Count} 个未恢复。",
                    pendingRestoreStates.Count == 0 ? NotificationType.Information : NotificationType.Warning);
                foreach (var restoreMessage in restoreMessages)
                {
                    AppendLaunchOutput(_launchUiState, restoreMessage, NotificationType.Warning);
                }
            }

            return restored;
        }

        private void StartGameProcessMonitoring()
        {
            CancelGameProcessMonitoring(suppressExitHandling: true);
            _suppressGameProcessExitHandling = false;

            var monitorCts = new CancellationTokenSource();
            _gameProcessMonitorCts = monitorCts;
            _ = MonitorGameProcessLifecycleAsync(monitorCts);
        }

        private void CancelGameProcessMonitoring(bool suppressExitHandling)
        {
            if (suppressExitHandling)
            {
                _suppressGameProcessExitHandling = true;
            }

            if (_gameProcessMonitorCts == null)
            {
                return;
            }

            try
            {
                if (!_gameProcessMonitorCts.IsCancellationRequested)
                {
                    _gameProcessMonitorCts.Cancel();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Game process monitor cancellation failed.");
            }
            finally
            {
                _gameProcessMonitorCts.Dispose();
                _gameProcessMonitorCts = null;
            }
        }

        private async Task MonitorGameProcessLifecycleAsync(CancellationTokenSource monitorCts)
        {
            var cancellationToken = monitorCts.Token;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var currentProcess = _gameProcess;
                    if (currentProcess == null)
                    {
                        return;
                    }

                    try
                    {
                        await currentProcess.WaitForExitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested || _suppressGameProcessExitHandling)
                    {
                        return;
                    }

                    int exitCode = 0;
                    bool abnormalExit = false;
                    DateTime exitedAtUtc = DateTime.UtcNow;

                    try
                    {
                        exitCode = currentProcess.ExitCode;
                        abnormalExit = exitCode != 0;
                    }
                    catch
                    {
                    }

                    try
                    {
                        exitedAtUtc = currentProcess.ExitTime.ToUniversalTime();
                    }
                    catch
                    {
                    }

                    try
                    {
                        await Task.Delay(StartupRestartProbeDelay, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    if (cancellationToken.IsCancellationRequested || _suppressGameProcessExitHandling)
                    {
                        return;
                    }

                    try
                    {
                        var restartedProcess = _gameProcessTracker.TryFindRestartedSpiceProcess(exitedAtUtc);
                        if (restartedProcess != null)
                        {
                            restartedProcess.EnableRaisingEvents = true;
                            _gameProcess = restartedProcess;
                            _gameProcessTracker.RegisterTrackedSpiceProcess(restartedProcess);
                            _logger.LogInformation("Detected restarted spice64 process. ProcessId={ProcessId}", restartedProcess.Id);
                            AppendLaunchOutput(_launchUiState, "检测到 spice64 重新启动，继续监控中...");
                            currentProcess.Dispose();
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Failed to probe restarted spice64 process.");
                    }

                    await CompleteGameProcessLifecycleAsync(currentProcess, exitCode, abnormalExit, cancellationToken);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Game process lifecycle monitor failed.");
            }
            finally
            {
                if (ReferenceEquals(_gameProcessMonitorCts, monitorCts))
                {
                    _gameProcessMonitorCts.Dispose();
                    _gameProcessMonitorCts = null;
                }
                else
                {
                    monitorCts.Dispose();
                }
            }
        }

        private async Task CompleteGameProcessLifecycleAsync(Process exitedProcess, int exitCode, bool abnormalExit, CancellationToken cancellationToken)
        {
            try
            {
                SpiceCrashDiagnostic crashDiagnostic = null;
                if (abnormalExit)
                {
                    crashDiagnostic = await _spiceCrashLogAnalyzer.AnalyzeAsync();
                    _logger.LogInformation(
                        "spice64 abnormal exit diagnostic resolved. ExitCode={ExitCode}, CrashSignal={CrashSignal}, CrashReason={CrashReason}, MatchedRuleId={MatchedRuleId}, MatchedLine={MatchedLine}, LogPath={LogPath}, LogReadSucceeded={LogReadSucceeded}",
                        exitCode,
                        crashDiagnostic.Signal,
                        crashDiagnostic.ReasonText,
                        crashDiagnostic.MatchedRuleId,
                        crashDiagnostic.MatchedLine,
                        crashDiagnostic.LogPath,
                        crashDiagnostic.ReadSucceeded);
                }

                _logger.LogInformation("spice64 process exited. ExitCode={ExitCode}, AbnormalExit={AbnormalExit}", exitCode, abnormalExit);
                AppendLaunchOutput(
                    _launchUiState,
                    abnormalExit
                        ? $"游戏进程异常退出。错误信号：{crashDiagnostic?.Signal ?? SpiceCrashDiagnostic.UnknownSignal}，崩溃原因：{crashDiagnostic?.ReasonText ?? "未识别具体崩溃原因"}。"
                        : "游戏进程已正常退出。",
                    abnormalExit ? NotificationType.Warning : NotificationType.Information);

                if (abnormalExit && !cancellationToken.IsCancellationRequested && !_suppressGameProcessExitHandling)
                {
                    var diagnostic = crashDiagnostic ?? new SpiceCrashDiagnostic(
                        SpiceCrashDiagnostic.UnknownSignal,
                        "未识别具体崩溃原因",
                        string.Empty,
                        string.Empty,
                        Path.Combine(_paths.GetContentsDirectoryPath(), "log.txt"),
                        readSucceeded: false);
                    _openLogAfterLaunchMessageDismissal = true;

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_launchUiState != null)
                        {
                            ShowLaunchMessage(
                                _launchUiState,
                                NotificationType.Error,
                                "进程异常退出",
                                diagnostic.Signal,
                                BuildCrashOverlayBody(diagnostic));
                        }
                        else
                        {
                            _openLogAfterLaunchMessageDismissal = false;
                            _ = ShowLaunchLogDialogAsync();
                        }
                    });
                }

                if (cancellationToken.IsCancellationRequested || _suppressGameProcessExitHandling)
                {
                    return;
                }

                if (_gameProcessTracker.HasManagedAsphyxiaProcess())
                {
                    AppendLaunchOutput(_launchUiState, "正在关闭 Asphyxia Core...");
                    if (_gameProcessTracker.TryStopManagedAsphyxiaProcess(out var stopErrorMessage))
                    {
                        _logger.LogInformation("Managed Asphyxia Core process stopped after game exit.");
                        AppendLaunchOutput(_launchUiState, "Asphyxia Core 已关闭");
                    }
                    else
                    {
                        _logger.LogWarning("Failed to stop managed Asphyxia Core process after game exit.");
                        ShowAsphyxiaStopError(stopErrorMessage);
                    }
                }

                if (_activeLaunchDisplayConfiguration?.IsDisplayConfigurationEnabled == true
                    && _activeLaunchDisplayConfiguration.ExitRestore
                    && _displayRestoreStates.Count > 0)
                {
                    RestoreAppliedDisplaySettings("游戏退出", shouldRestore: true, logToLaunchOutput: true);
                }
                else if (_activeLaunchDisplayConfiguration?.ExitRestore == false)
                {
                    // A completed session intentionally kept its layout; these are not failed restores.
                    _displayRestoreStates = new Dictionary<string, DisplayState>(StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Launch cleanup after game exit failed.");
            }
            finally
            {
                if (!cancellationToken.IsCancellationRequested && !_suppressGameProcessExitHandling)
                {
                    _logger.LogInformation("Game process lifecycle cleanup completed.");
                    if (_launchUiState != null)
                    {
                        _launchUiState.IsLaunching = false;
                        _launchUiState.IsGameRunning = false;
                        if (!abnormalExit)
                        {
                            ClearLaunchMessage(_launchUiState);
                        }

                        NotifyLaunchStateChanged(_launchUiState);
                    }

                    RestoreLauncherWindow();
                }

                if (ReferenceEquals(_gameProcess, exitedProcess))
                {
                    _gameProcess.Dispose();
                    _gameProcess = null;
                }
                else
                {
                    exitedProcess?.Dispose();
                }
            }
        }

        private async Task MinimizeLauncherWindowDelayedAsync()
        {
            try
            {
                await Task.Delay(LauncherAutoMinimizeDelay);

                if (_launchUiState?.IsGameRunning == true)
                {
                    Dispatcher.UIThread.Post(() => WindowState = WindowState.Minimized);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to auto minimize launcher window.");
            }
        }

        private void FailLaunch(LaunchUiState launchState, string logMessage, string displayMessage = null)
        {
            StopLaunchWithMessage(
                launchState,
                NotificationType.Error,
                "启动失败",
                logMessage,
                displayMessage ?? logMessage);
        }

        private void WarnLaunchAndAbort(LaunchUiState launchState, string title, string bodyMessage)
        {
            StopLaunchWithMessage(
                launchState,
                NotificationType.Warning,
                title,
                bodyMessage,
                bodyMessage);
        }

        private void StopLaunchWithMessage(
            LaunchUiState launchState,
            NotificationType messageType,
            string title,
            string logMessage,
            string bodyMessage,
            string accentText = "")
        {
            AppendLaunchOutput(launchState, logMessage, messageType);
            launchState.IsLaunching = false;
            launchState.IsGameRunning = false;
            NotifyLaunchStateChanged(launchState);
            ShowLaunchMessage(launchState, messageType, title, accentText, bodyMessage);
        }

        private void ShowLaunchMessage(LaunchUiState launchState, NotificationType messageType, string title, string accentText, string bodyText)
        {
            if (launchState == null)
            {
                return;
            }

            launchState.MessageType = messageType;
            launchState.MessageTitle = title ?? string.Empty;
            launchState.MessageAccentText = accentText ?? string.Empty;
            launchState.MessageBodyText = bodyText ?? string.Empty;
            launchState.IsMessageVisible = true;
            NotifyLaunchMessageChanged(launchState);
        }

        private static string BuildCrashOverlayBody(SpiceCrashDiagnostic diagnostic)
        {
            string signalHint = diagnostic.Signal == SpiceCrashDiagnostic.UnknownSignal
                ? $"{SystemEnvironment.NewLine}未捕获到错误信号"
                : string.Empty;

            return
                $"检测到游戏进程异常退出{signalHint}{SystemEnvironment.NewLine}" +
                $"崩溃原因：{diagnostic.ReasonText}{SystemEnvironment.NewLine}" +
                "请阅读 log.txt";
        }

        private void ClearLaunchMessage(LaunchUiState launchState)
        {
            if (launchState == null)
            {
                return;
            }

            _openLogAfterLaunchMessageDismissal = false;
            launchState.IsMessageVisible = false;
            launchState.MessageType = NotificationType.Error;
            launchState.MessageTitle = string.Empty;
            launchState.MessageAccentText = string.Empty;
            launchState.MessageBodyText = string.Empty;
            NotifyLaunchMessageChanged(launchState);
        }

        private void ClearLaunchLog(LaunchUiState launchState)
        {
            _logLines.Clear();
            launchState.LaunchLogText = string.Empty;
            NotifyLaunchLogChanged(launchState);
        }

        private void AppendLaunchOutput(LaunchUiState launchState, string message, NotificationType type = NotificationType.Information)
        {
            if (launchState == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
            {
                _logLines.Enqueue(FormatLogEntry(line, type));
                while (_logLines.Count > MaxLogLines)
                {
                    _logLines.Dequeue();
                }
            }

            launchState.LaunchLogText = string.Join(SystemEnvironment.NewLine, _logLines);
            NotifyLaunchLogChanged(launchState);
        }

        private void NotifyLaunchStateChanged(LaunchUiState launchState)
        {
            if (launchState != null)
            {
                if (Dispatcher.UIThread.CheckAccess()) SynchronizeDisplayDetectionWithLaunchState();
                else Dispatcher.UIThread.Post(SynchronizeDisplayDetectionWithLaunchState);
                Dispatcher.UIThread.Post(ApplyLaunchStateToUi);
            }
        }

        private void NotifyLaunchLogVisibilityChanged(LaunchUiState launchState)
        {
            if (launchState != null)
            {
                Dispatcher.UIThread.Post(() => _ = ApplyLaunchLogVisibilityAsync());
            }
        }

        private void NotifyLaunchLogChanged(LaunchUiState launchState)
        {
            if (launchState != null)
            {
                Dispatcher.UIThread.Post(() => _ = ApplyLaunchLogTextAsync());
            }
        }

        private void NotifyLaunchMessageChanged(LaunchUiState launchState)
        {
            if (launchState != null)
            {
                Dispatcher.UIThread.Post(() => _ = ApplyLaunchMessageOverlayAsync());
            }
        }

        private void RestoreLauncherWindow()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isWindowCloseAnimationRunning || _allowImmediateWindowClose) return;
                if (WindowState == WindowState.Minimized)
                {
                    WindowState = WindowState.Normal;
                }

                Show();
                Activate();
            });
        }

        private static string FormatLogEntry(string line, NotificationType type)
        {
            string prefix = type switch
            {
                NotificationType.Error => "[错误] ",
                NotificationType.Warning => "[警告] ",
                _ => string.Empty
            };

            return $"[{DateTime.Now:HH:mm:ss}] {prefix}{line}";
        }

        private ProcessTerminationResult KillProcessesByName(string processName)
        {
            int count = 0;
            bool permissionDenied = false;
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
                _logger.LogInformation("Found processes for termination. ProcessName={ProcessName}, Count={Count}", processName, processes.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enumerate processes for termination. ProcessName={ProcessName}", processName);
                return new ProcessTerminationResult(processName, count, false);
            }

            foreach (var process in processes)
            {
                try
                {
                    if (process.HasExited) continue;
                    int pid = process.Id;
                    _logger.LogInformation("Killing process. ProcessName={ProcessName}, ProcessId={ProcessId}", processName, pid);
                    process.Kill();
                    if (!process.WaitForExit(3000))
                    {
                        _logger.LogWarning("Process did not exit after Kill. Falling back to taskkill. ProcessName={ProcessName}, ProcessId={ProcessId}", processName, pid);
                        using var taskKillProcess = Process.Start(new ProcessStartInfo
                        {
                            FileName = "taskkill",
                            Arguments = $"/F /PID {pid}",
                            CreateNoWindow = true,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        });
                        taskKillProcess?.WaitForExit(2000);
                    }
                    process.Refresh();
                    if (!process.HasExited)
                    {
                        _logger.LogWarning("Process is still running after termination attempt. ProcessName={ProcessName}, ProcessId={ProcessId}", processName, pid);
                        continue;
                    }
                    count++;
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogDebug(ex, "Process may have exited during termination. ProcessName={ProcessName}", processName);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    permissionDenied |= ex.NativeErrorCode == 5;
                    _logger.LogWarning(ex, "Failed to terminate process. ProcessName={ProcessName}", processName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to terminate process. ProcessName={ProcessName}", processName);
                }
                finally { process.Dispose(); }
            }

            // Confirm the final state, including processes that appeared during cleanup.
            try
            {
                var remaining = Process.GetProcessesByName(processName);
                try
                {
                    bool succeeded = remaining.All(process => process.HasExited);
                    _logger.LogInformation("Process termination verification completed. ProcessName={ProcessName}, Succeeded={Succeeded}", processName, succeeded);
                    return new ProcessTerminationResult(processName, count, succeeded, permissionDenied);
                }
                finally { foreach (var process in remaining) process.Dispose(); }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not verify process termination. ProcessName={ProcessName}", processName);
                return new ProcessTerminationResult(processName, count, false, permissionDenied);
            }
        }

        private bool IsAsphyxiaCoreRunning(out int processCount)
        {
            processCount = 0;

            try
            {
                var processes = Process.GetProcessesByName("asphyxia-core-x64");
                try
                {
                    processCount = processes.Length;
                    _logger.LogDebug("Asphyxia Core process inspection completed. Count={Count}", processCount);
                    return processCount > 0;
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect existing Asphyxia Core processes.");
                return false;
            }
        }

        private sealed record LaunchRequest(
            bool NoAsphyxia,
            bool UseSystemSpiceConfig,
            bool DisableSpiceFso,
            string ServerAddress,
            DisplayConfigurationRequest Display,
            bool AsphyxiaDevOnly);

        private sealed record LaunchLogDocument(string Path, string Content);

        private sealed class LaunchUiState
        {
            public string LaunchLogText { get; set; } = string.Empty;

            public bool IsLaunchLogVisible { get; set; }

            public string ToggleLaunchLogText { get; set; } = "显示启动日志";

            public bool IsLaunching { get; set; }

            public bool IsGameRunning { get; set; }

            public bool IsMessageVisible { get; set; }

            public NotificationType MessageType { get; set; } = NotificationType.Error;

            public string MessageTitle { get; set; } = string.Empty;

            public string MessageAccentText { get; set; } = string.Empty;

            public string MessageBodyText { get; set; } = string.Empty;

            public bool CanStartLaunch => !IsLaunching && !IsGameRunning;
        }

    }

    internal static class Spice64CommandLine
    {
        private const string LazyCfgRelative = "lazy/spicetools.xml";
        private const string LazyPatchRelative = "lazy/spicetools_patch_manager.json";

        public static string BuildGameLaunchArguments(bool useSystemConfig)
        {
            if (useSystemConfig)
            {
                return string.Empty;
            }

            return $"-cmdoverride -cfgpath {LazyCfgRelative} -patchcfgpath {LazyPatchRelative}";
        }

        public static string BuildConfigEditorArguments(bool useSystemConfig)
        {
            if (useSystemConfig)
            {
                return "-cfg -forcesoftware -y log_cfg.txt";
            }

            return $"-cfg -cmdoverride -forcesoftware -cfgpath {LazyCfgRelative} -patchcfgpath {LazyPatchRelative} -y log_cfg.txt";
        }
    }
}
