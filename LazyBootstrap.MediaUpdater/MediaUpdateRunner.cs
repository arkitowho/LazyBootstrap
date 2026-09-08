using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LazyBootstrap.MediaUpdate
{
    internal static class MediaUpdateRunner
    {
        public const int ExitSecurityBlocked = 4;

        public static async Task<int> RunAsync(
            string gamePath,
            string stagingPath,
            Action<string> log,
            CancellationToken cancellationToken = default,
            Action onUpdateComplete = null,
            Action<string> onSecurityBlockUi = null)
        {
            if (log == null)
            {
                throw new ArgumentNullException(nameof(log));
            }

            try
            {
                gamePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath));
                stagingPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingPath));

                if (!MediaUpdateProtocol.IsValidGameRoot(gamePath))
                {
                    log("错误: 游戏目录中未找到 contents 或 asphyxia。");
                    return 0;
                }

                MediaUpdateSynchronizer synchronizer;
                try
                {
                    synchronizer = new MediaUpdateSynchronizer(gamePath, stagingPath);
                }
                catch (IOException ex)
                {
                    string msg = MediaUpdateSecurity.BlockedNonGamePathMessage + Environment.NewLine + ex.Message;
                    if (onSecurityBlockUi != null)
                    {
                        onSecurityBlockUi(msg);
                    }
                    else
                    {
                        log(msg);
                    }

                    return ExitSecurityBlocked;
                }

                log("正在结束启动器…");
                TryKillLauncher();
                await Task.Delay(2000, cancellationToken).ConfigureAwait(true);

                log("开始同步资源…");
                synchronizer.Apply(log, cancellationToken);

                log($"正在清理 {MediaUpdateProtocol.UpdateStagingFolderName}…");
                try
                {
                    MediaUpdateSecurity.ValidateStagingDirectory(stagingPath, gamePath);
                    if (Directory.Exists(stagingPath))
                    {
                        Directory.Delete(stagingPath, true);
                    }
                }
                catch (Exception ex)
                {
                    log("清理临时目录时出现问题: " + ex.Message);
                }

                onUpdateComplete?.Invoke();

                await Task.Delay(5000, cancellationToken).ConfigureAwait(true);

                string gameLauncherPath = Path.Combine(gamePath, MediaUpdateProtocol.GameLauncherExeName);
                string outerShellPath = Path.Combine(gamePath, MediaUpdateProtocol.LauncherProcessImageFileName);
                if (!TryStartShellExe(gameLauncherPath, gamePath, log, MediaUpdateProtocol.GameLauncherExeName))
                {
                    if (!TryStartShellExe(outerShellPath, gamePath, log, MediaUpdateProtocol.LauncherProcessImageFileName))
                    {
                        log(
                            $"未找到 {MediaUpdateProtocol.GameLauncherExeName} 与 {MediaUpdateProtocol.LauncherProcessImageFileName}，请从游戏根目录手动运行启动器。");
                    }
                }

                return 0;
            }
            catch (OperationCanceledException)
            {
                log("已取消。");
                return 1;
            }
            catch (Exception ex)
            {
                log("错误: " + ex);
                return 1;
            }
        }

        // Outer shell path is game root LazyBootstrap.exe, not launcher/LazyBootstrap.exe.
        private static bool TryStartShellExe(string exePath, string workingDirectory, Action<string> log, string displayName)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true
                };
                Process.Start(psi);
                return true;
            }
            catch (Exception ex)
            {
                log("无法启动 " + displayName + ": " + ex.Message);
                return false;
            }
        }

        private static void TryKillLauncher()
        {
            try
            {
                string name = Path.GetFileNameWithoutExtension(MediaUpdateProtocol.LauncherProcessImageFileName);
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        p.Kill();
                    }
                    catch
                    {
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch
            {
            }
        }

    }
}
