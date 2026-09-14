using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Platform;
using LazyBootstrap.MediaUpdate;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private const string RequiredUpdateArchiveFileNamePrefix = "UPDATE_LAZY_KFC";
        private static readonly string[] UpdateArchiveFilePatterns = ["*.7z", "*.zip", "*.rar", "*.001"];
        private static readonly string[] UpdateArchiveExtensions = [".7z", ".zip", ".rar", ".001"];

        private enum MediaUpdaterStartResult
        {
            Started,
            CancelledByUser,
            Failed
        }

        private async void OnApplyUpdateClick(object sender, RoutedEventArgs e)
        {
            using var busy = BeginBusy(BusyPresentation.GlobalOverlay, "正在准备更新...");
            await ApplyUpdateFromUserSelectedArchiveAsync(busy.UpdateText);
        }

        private async Task ApplyUpdateFromUserSelectedArchiveAsync(Action<string> reportProgress)
        {
            _logger.LogInformation("KFC update workflow requested.");

            string sevenZipExecutablePath = _paths.ResolveSevenZipExecutablePath();
            string mediaUpdaterExecutablePath = Path.Combine(_paths.ApplicationDirectoryPath, MediaUpdateProtocol.MediaUpdaterExecutableFileName);
            string archivePath = await PickFileAsync("选择更新压缩包", UpdateArchiveFilePatterns).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
            {
                _logger.LogInformation("KFC update cancelled before archive selection.");
                return;
            }

            if (!IsSupportedUpdateArchive(archivePath))
            {
                _logger.LogWarning("KFC update rejected because the selected file is not a supported archive: {ArchivePath}", archivePath);
                ShowErrorToast("更新失败", "仅支持 7z、zip、rar 或 001 更新压缩包。");
                return;
            }

            if (!HasRequiredUpdateArchivePrefix(archivePath))
            {
                _logger.LogWarning("KFC update rejected because the selected archive file name has an invalid prefix: {ArchivePath}", archivePath);
                ShowErrorToast("更新失败", $"更新压缩包文件名必须以 {RequiredUpdateArchiveFileNamePrefix} 开头。");
                return;
            }

            _logger.LogInformation("KFC update archive selected: {ArchivePath}", archivePath);

            string stagingDirectoryPath = _paths.GetUpdateStagingDirectoryPath();
            reportProgress?.Invoke("正在准备更新...");
            bool mediaUpdaterStarted = false;

            try
            {
                reportProgress?.Invoke("正在清理更新临时目录...");
                ClearStagingDirectory(stagingDirectoryPath);
                reportProgress?.Invoke("正在解压更新压缩包...");
                if (!await RunSevenZipExtractAsync(sevenZipExecutablePath, archivePath, stagingDirectoryPath).ConfigureAwait(true)) return;
                string packageDirectory = await Task.Run(() => MediaUpdateChecksums.Verify(_paths.BaseDir, stagingDirectoryPath,
                    message => Avalonia.Threading.Dispatcher.UIThread.Post(() => reportProgress?.Invoke(message)),
                    log: message => _logger.LogInformation("{UpdateVerification}", message)));
                reportProgress?.Invoke("正在启动更新程序...");
                var updaterStartResult = TryStartMediaUpdater(
                    mediaUpdaterExecutablePath,
                    _paths.BaseDir,
                    packageDirectory,
                    _paths.ApplicationDirectoryPath,
                    out string updaterStartError);

                if (updaterStartResult != MediaUpdaterStartResult.Started)
                {
                    if (updaterStartResult == MediaUpdaterStartResult.CancelledByUser)
                    {
                        _logger.LogWarning("KFC update cancelled because MediaUpdater elevation was cancelled.");
                        ShowWarningToast("更新已取消", updaterStartError);
                    }
                    else
                    {
                        _logger.LogWarning("KFC update failed because MediaUpdater could not be started. Error={Error}", updaterStartError);
                        ShowErrorToast(
                            "更新失败",
                            string.IsNullOrWhiteSpace(updaterStartError) ? "无法启动 MediaUpdater。" : updaterStartError);
                    }

                    return;
                }

                mediaUpdaterStarted = true;
                _logger.LogInformation("MediaUpdater started successfully. Main application will exit.");
                ShowInfoToast(
                    "开始更新",
                    "本程序将立即退出。更新器预演通过后开始安装，成功后自动重新启动。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "KFC update workflow failed.");
                ShowErrorToast("更新失败", ex.Message);
            }
            if (mediaUpdaterStarted)
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                {
                    desktopLifetime.Shutdown(0);
                }
                else
                {
                    global::System.Environment.Exit(0);
                }
            }
        }

        private void ClearStagingDirectory(string stagingDirectoryPath)
        {
            if (Directory.Exists(stagingDirectoryPath)) Directory.Delete(stagingDirectoryPath, true);
        }

        private static bool IsSupportedUpdateArchive(string archivePath)
        {
            string extension = Path.GetExtension(archivePath);
            foreach (string supportedExtension in UpdateArchiveExtensions)
            {
                if (string.Equals(extension, supportedExtension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasRequiredUpdateArchivePrefix(string archivePath)
        {
            string fileName = Path.GetFileName(archivePath);
            return fileName.StartsWith(RequiredUpdateArchiveFileNamePrefix, StringComparison.Ordinal);
        }

        private async Task<bool> RunSevenZipExtractAsync(string sevenZipPath, string archivePath, string outputDir)
        {
            try
            {

                Directory.CreateDirectory(outputDir);
                _logger.LogInformation("Update extraction directory prepared: {OutputDirectory}", outputDir);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create update extraction directory.");
                ShowErrorToast("更新失败", $"无法创建临时目录: {ex.Message}");
                return false;
            }

            string workingDirectory = Path.GetDirectoryName(sevenZipPath) ?? _paths.ApplicationDirectoryPath;

            int exitCode;
            try
            {
                _logger.LogInformation(
                    "Starting update extraction. Elevated={IsElevated}",
                    ProcessExecutionHelper.IsCurrentProcessElevated());
                exitCode = await ProcessExecutionHelper.RunShellProcessAsync(
                    sevenZipPath,
                    workingDirectory,
                    true,
                    startInfo =>
                    {
                        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                        startInfo.ArgumentList.Add("x");
                        startInfo.ArgumentList.Add(archivePath);
                        startInfo.ArgumentList.Add("-o" + outputDir + Path.DirectorySeparatorChar);
                        startInfo.ArgumentList.Add("-y");
                    }).ConfigureAwait(true);

                if (exitCode == -1)
                {
                    _logger.LogWarning("Update extraction failed because 7za process start returned false.");
                    ShowErrorToast("更新失败", "无法启动 7za。");
                    return false;
                }
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _logger.LogWarning("Update extraction cancelled at UAC prompt.");
                ShowWarningToast("解压已取消", "用户取消了 7za 管理员授权。");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start update extraction process.");
                ShowErrorToast("更新失败", $"无法启动 7za: {ex.Message}");
                return false;
            }

            _logger.LogInformation("Update extraction process exited. ExitCode={ExitCode}", exitCode);
            if (exitCode != 0)
            {
                _logger.LogWarning("7za exited with code {Code}.", exitCode);
                ShowErrorToast("解压失败", $"7za 退出代码: {exitCode}");
                return false;
            }

            return true;
        }

        private static MediaUpdaterStartResult TryStartMediaUpdater(
            string mediaUpdaterPath,
            string gamePath,
            string packagePath,
            string applicationDirectoryPath,
            out string error)
        {
            error = null;

            try
            {
                if (string.IsNullOrEmpty(gamePath) || string.IsNullOrEmpty(packagePath)
                    || gamePath.Contains('"', StringComparison.Ordinal)
                    || packagePath.Contains('"', StringComparison.Ordinal))
                {
                    error = "更新路径无效。";
                    return MediaUpdaterStartResult.Failed;
                }

                string normalizedGamePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
                string normalizedPackagePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagePath));

                using var process = ProcessExecutionHelper.StartShellProcess(
                    mediaUpdaterPath,
                    Path.GetDirectoryName(mediaUpdaterPath) ?? applicationDirectoryPath,
                    true,
                    startInfo =>
                    {
                        startInfo.ArgumentList.Add("--game");
                        startInfo.ArgumentList.Add(normalizedGamePath);
                        startInfo.ArgumentList.Add("--package");
                        startInfo.ArgumentList.Add(normalizedPackagePath);
                        startInfo.ArgumentList.Add("--parent-pid");
                        startInfo.ArgumentList.Add(global::System.Environment.ProcessId.ToString(global::System.Globalization.CultureInfo.InvariantCulture));
                    });

                if (process == null)
                {
                    error = "未能创建 MediaUpdater 进程。";
                    return MediaUpdaterStartResult.Failed;
                }

                return MediaUpdaterStartResult.Started;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                error = "用户取消了 MediaUpdater 管理员授权。";
                return MediaUpdaterStartResult.CancelledByUser;
            }
            catch (Exception ex)
            {
                error = $"无法启动 MediaUpdater: {ex.Message}";
                return MediaUpdaterStartResult.Failed;
            }
        }
    }
}
