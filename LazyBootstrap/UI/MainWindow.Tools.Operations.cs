using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls.Notifications;
using Microsoft.Extensions.Logging;
using LazyBootstrap.Platform;
using LazyBootstrap.Services;

namespace LazyBootstrap.UI
{
    public partial class MainWindow
    {
        private SavedataTransferService _savedataTransferService = null!;

        private void InitializeToolsServices(SavedataTransferService savedataTransferService)
        {
            _savedataTransferService = savedataTransferService ?? throw new ArgumentNullException(nameof(savedataTransferService));
        }

        private Task ClearCacheAsync()
        {
            string cachePath = Path.Combine(_paths.GetContentsDirectoryPath(), "data_mods", "_cache");
            _logger.LogInformation("Cache cleanup requested. CachePath={CachePath}", cachePath);
            try
            {
                if (Directory.Exists(cachePath))
                {
                    Directory.Delete(cachePath, true);
                    _logger.LogInformation("Cache cleanup completed.");
                    ShowInfoToast("清理缓存", "缓存已成功清理。");
                }
                else
                {
                    _logger.LogInformation("Cache cleanup skipped because the cache directory does not exist.");
                    ShowInfoToast("清理缓存", "无需清理，当前没有缓存。");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup failed.");
                ShowErrorToast("清理缓存失败", "请关闭游戏并检查缓存目录写入权限后重试。详情请查看日志。");
            }

            return Task.CompletedTask;
        }

        private async Task AddFirewallRuleAsync()
        {
            const string ruleName = "SpiceTools";
            string spicePath = _paths.GetSpicePath();
            _logger.LogInformation("Firewall rule creation requested.");

            bool confirmed = await ShowDialogAsync(
                "添加防火墙规则",
                "确认要执行吗？\n如果之前已经添加过规则，将会重复添加。",
                "确认",
                "取消",
                NotificationType.Warning);
            if (!confirmed)
            {
                _logger.LogInformation("Firewall rule creation cancelled by user.");
                return;
            }

            if (!File.Exists(spicePath))
            {
                _logger.LogWarning("Firewall rule creation failed because spice64.exe was not found: {SpicePath}", spicePath);
                ShowErrorToast("添加防火墙规则失败", $"未找到目标程序：{spicePath}");
                return;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow program=\"{spicePath}\" enable=yes profile=public,private",
                    UseShellExecute = true,
                    Verb = "runas",
                    CreateNoWindow = true
                });

                if (process == null)
                {
                    _logger.LogWarning("Firewall rule creation failed because netsh process creation returned null.");
                    ShowFirewallRuleError();
                    return;
                }

                await process.WaitForExitAsync();
                _logger.LogInformation("Firewall rule netsh process exited. ExitCode={ExitCode}", process.ExitCode);
                if (process.ExitCode != 0)
                {
                    ShowFirewallRuleError();
                    return;
                }

                ShowInfoToast("防火墙规则", "防火墙规则添加完成。");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _logger.LogInformation("Firewall rule creation cancelled at UAC prompt.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Firewall rule creation failed.");
                ShowFirewallRuleError();
            }
        }

        private Task OpenAudioPanelAsync()
        {
            try
            {
                _logger.LogInformation("Opening audio control panel.");
                ProcessExecutionHelper.OpenControlPanel("mmsys.cpl");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open audio control panel.");
                ShowErrorToast("打开音频面板失败", "无法打开系统声音设置，请在 Windows 设置中检查音频设备。详情请查看日志。");
            }

            return Task.CompletedTask;
        }

        private async Task InstallRuntimeAsync(Action<string, double> reportProgress)
        {
            string runtimePath = _paths.GetRuntimeDirectoryPath();
            var installers = new[]
            {
                (Name: "DirectX", Path: Path.Combine(runtimePath, "directx", "DXSETUP.exe"), Arguments: "/silent"),
                (Name: "Visual C++ Redistributable", Path: Path.Combine(runtimePath, "vcredist", "VisualCppRedist_AIO_x86_x64.exe"), Arguments: "/y")
            };
            var result = new RuntimeInstallResult();
            var available = installers.Where(installer => File.Exists(installer.Path)).ToArray();
            result.Missing.AddRange(installers.Where(installer => !File.Exists(installer.Path)).Select(installer => installer.Name));
            _logger.LogInformation("Runtime installation requested. AvailableCount={AvailableCount}, Missing={Missing}", available.Length, string.Join(", ", result.Missing));
            double lastProgress = 5d;
            int completed = 0;
            void Report(string text, double value)
            {
                lastProgress = value;
                reportProgress?.Invoke(text, value);
            }

            foreach (var installer in available)
            {
                Report($"正在安装 {installer.Name}...", CalculateRuntimeInstallProgress(available.Length, completed, true));
                try
                {
                    int exitCode = await ProcessExecutionHelper.RunElevatedInstallerAsync(
                        installer.Path, installer.Arguments, Path.GetDirectoryName(installer.Path));
                    _logger.LogInformation("Runtime installer exited. Installer={Installer}, ExitCode={ExitCode}", installer.Name, exitCode);
                    result.Record(installer.Name, exitCode);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Runtime installer failed. Installer={Installer}", installer.Name);
                    result.Record(installer.Name, -1);
                }
                if (result.Cancelled)
                {
                    _logger.LogInformation("Runtime installation cancelled by user. Installer={Installer}", installer.Name);
                    Report("运行库安装已取消", lastProgress);
                    break;
                }
                completed++;
                Report($"{installer.Name} 安装流程已结束", CalculateRuntimeInstallProgress(available.Length, completed, false));
            }

            if (!result.ShouldNotify) return;
            Report(result.Succeeded ? "运行库安装完成" : "运行库安装未完成", 100d);
            _logger.LogInformation("Runtime installation finished. Succeeded={Succeeded}, Cancelled={Cancelled}, Failed={Failed}", result.Succeeded, result.Cancelled, string.Join(", ", result.Failed));
            if (result.Succeeded) ShowInfoToast("运行库安装完成", result.Message);
            else ShowWarningToast("运行库安装未完成", result.Message);
        }

        private void ShowFirewallRuleError() =>
            ShowSystemSettingsError("添加防火墙规则失败");

        private void ShowSavedataTransferError(string title) =>
            ShowErrorToast(title, "请检查源存档是否可读取、目标目录是否被占用及写入权限后重试。详情请查看日志。");

        private bool TryResolveSevenZip(string failureTitle, out string executablePath)
        {
            executablePath = _paths.ResolveSevenZipExecutablePath();
            if (File.Exists(executablePath)) return true;
            _logger.LogWarning("Archive operation failed because 7za.exe was not found: {SevenZipPath}", executablePath);
            ShowErrorToast(failureTitle, "缺少压缩工具，请补齐程序文件后重试。");
            return false;
        }

        private static double CalculateRuntimeInstallProgress(int totalInstallers, int completedInstallers, bool installerRunning)
        {
            if (totalInstallers <= 0)
            {
                return 100d;
            }

            const double startProgress = 5d;
            const double maxProgressBeforeCompletion = 95d;
            double units = completedInstallers + (installerRunning ? 0.5d : 0d);
            return startProgress + ((maxProgressBeforeCompletion - startProgress) * (units / totalInstallers));
        }

        private async Task BackupSavedataAsync()
        {
            _logger.LogInformation("Savedata backup requested.");
            if (!TryResolveSevenZip("存档备份失败", out string sevenZipPath)) return;

            var entries = _savedataTransferService.GetCurrentSavedataEntries();
            _logger.LogInformation("Savedata backup entries resolved. EntryCount={EntryCount}", entries.Count);
            if (entries.Count == 0)
            {
                _logger.LogWarning("Savedata backup skipped because no entries were found.");
                ShowWarningToast("存档备份", "未找到可备份的数据");
                return;
            }

            string stagingDirectory = null;
            try
            {
                string backupDirectory = _paths.GetSavedataBackupDirectoryPath();
                Directory.CreateDirectory(backupDirectory);
                string backupFilePath = Path.Combine(backupDirectory, $"savedata_{DateTime.Now:yyyyMMdd_HHmmss}.7z");
                stagingDirectory = _savedataTransferService.CreateTemporaryWorkingDirectory("backup");
                _savedataTransferService.StageEntries(entries, stagingDirectory);
                var stagedTopLevelEntries = Directory.GetFileSystemEntries(stagingDirectory)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                if (stagedTopLevelEntries.Count == 0)
                {
                    _logger.LogWarning("Savedata backup staging generated no compressible entries.");
                    ShowWarningToast("存档备份", "未生成可压缩的备份内容。");
                    return;
                }

                string arguments = $"a -t7z \"{backupFilePath}\" {string.Join(" ", stagedTopLevelEntries.Select(name => $"\"{name}\""))} -mx=9";
                var result = await ProcessExecutionHelper.RunProcessCaptureAsync(sevenZipPath, arguments, stagingDirectory);
                _logger.LogInformation("Savedata backup 7za process exited. ExitCode={ExitCode}", result.ExitCode);
                if (result.ExitCode != 0)
                {
                    _logger.LogWarning("Savedata backup failed during archive creation. ExitCode={ExitCode}, StdOut={StdOut}, StdErr={StdErr}", result.ExitCode, result.StdOut, result.StdErr);
                    ShowErrorToast("存档备份失败", "无法创建备份压缩包，请检查存档文件是否可读取及备份目录写入权限。详情请查看日志。");
                    return;
                }

                _logger.LogInformation("Savedata backup completed. BackupPath={BackupPath}", backupFilePath);
                ShowInfoToast("存档备份完成", $"已备份到：{backupFilePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Savedata backup failed.");
                ShowSavedataTransferError("存档备份失败");
            }
            finally
            {
                if (stagingDirectory != null) _savedataTransferService.DeleteDirectoryIfExists(stagingDirectory);
            }
        }

        private async Task ImportSavedataAsync()
        {
            _logger.LogInformation("Savedata import requested.");
            if (!TryResolveSevenZip("存档导入失败", out string sevenZipPath)) return;

            string archivePath = await PickFileAsync("选择存档备份文件", new[] { "*.7z" });
            if (string.IsNullOrWhiteSpace(archivePath))
            {
                _logger.LogInformation("Savedata import cancelled before archive selection.");
                return;
            }
            _logger.LogInformation("Savedata import archive selected: {ArchivePath}", archivePath);

            var targetEntries = _savedataTransferService.GetCurrentSavedataTargets();
            if (SavedataTransferService.HasExistingTargets(targetEntries))
            {
                bool confirmed = await ShowDialogAsync(
                    "存档导入覆盖提示",
                    "检测到当前游戏目录或氧无目录中已有存档文件，是否覆盖？",
                    "覆盖",
                    "取消",
                    NotificationType.Warning);
                if (!confirmed)
                {
                    _logger.LogInformation("Savedata import cancelled at overwrite confirmation.");
                    return;
                }
            }

            string extractionDirectory = null;
            try
            {
                extractionDirectory = _savedataTransferService.CreateTemporaryWorkingDirectory("import");
                string arguments = $"x \"{archivePath}\" -o\"{extractionDirectory}\" -y";
                var extractionResult = await ProcessExecutionHelper.RunProcessCaptureAsync(sevenZipPath, arguments, extractionDirectory);
                _logger.LogInformation("Savedata import extraction exited. ExitCode={ExitCode}", extractionResult.ExitCode);
                if (extractionResult.ExitCode != 0)
                {
                    _logger.LogWarning("Savedata import failed during extraction. ExitCode={ExitCode}, StdOut={StdOut}, StdErr={StdErr}", extractionResult.ExitCode, extractionResult.StdOut, extractionResult.StdErr);
                    ShowErrorToast("存档导入失败", "无法解压备份文件，请检查压缩包是否完整后重试。详情请查看日志。");
                    return;
                }

                var extractedEntries = _savedataTransferService.BuildArchiveEntriesFromDirectory(extractionDirectory);
                _logger.LogInformation("Savedata import extracted entries resolved. EntryCount={EntryCount}", extractedEntries.Count);
                if (extractedEntries.Count == 0)
                {
                    _logger.LogWarning("Savedata import skipped because the archive contained no importable entries.");
                    ShowWarningToast("存档导入", "备份文件中未找到可导入的数据");
                    return;
                }

                await Task.Run(() => _savedataTransferService.CopyEntries(extractedEntries));
                _logger.LogInformation("Savedata import completed.");
                ShowInfoToast("存档导入完成", "已导入到当前设置的游戏目录和氧无目录。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Savedata import failed.");
                ShowSavedataTransferError("存档导入失败");
            }
            finally
            {
                _logger.LogDebug("Deleting savedata import extraction directory.");
                if (extractionDirectory != null) _savedataTransferService.DeleteDirectoryIfExists(extractionDirectory);
            }
        }

        private async Task MigrateSavedataAsync(IReadOnlyList<SavedataTransferEntry> selectedEntries)
        {
            ArgumentNullException.ThrowIfNull(selectedEntries);
            try
            {
                await Task.Run(() => _savedataTransferService.CopyEntries(selectedEntries));
                _logger.LogInformation("Savedata migration completed.");
                ShowInfoToast("存档迁移完成", "已迁移到当前设置的游戏目录和氧无目录。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Savedata migration failed.");
                ShowSavedataTransferError("存档迁移失败");
            }
        }

        // Path normalization delegated to PathHelper.NormalizePath
    }
}
