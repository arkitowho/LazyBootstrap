using System;
using System.IO;
using System.Threading;
using Serilog;

namespace LazyBootstrap.Platform
{
    internal static class MediaUpdaterPendingUpdateService
    {
        private const string MediaUpdaterFileName = "MediaUpdater.exe";
        private const string PendingExtension = ".pending";

        private const int MaxAttempts = 20;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

        public static bool ApplyPendingUpdate(string applicationDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(applicationDirectoryPath))
            {
                return false;
            }

            string applicationDirectory;
            try
            {
                applicationDirectory = Path.GetFullPath(applicationDirectoryPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MediaUpdater pending update skipped because the application directory is invalid.");
                return false;
            }

            string targetPath = Path.Combine(applicationDirectory, MediaUpdaterFileName);
            string pendingPath = targetPath + PendingExtension;

            if (!File.Exists(pendingPath))
            {
                return true;
            }

            Log.Information("MediaUpdater pending update found: {PendingPath}", pendingPath);

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    File.Move(pendingPath, targetPath, true);
                    Log.Information("MediaUpdater pending update applied successfully.");
                    return true;
                }
                catch (Exception ex) when (IsRetriableFileAccessError(ex) && attempt < MaxAttempts)
                {
                    Log.Debug(
                        ex,
                        "MediaUpdater pending update attempt {Attempt}/{MaxAttempts} failed because the file is not ready.",
                        attempt,
                        MaxAttempts);
                    Thread.Sleep(RetryDelay);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "MediaUpdater pending update failed. Pending file will be retried on next startup.");
                    return false;
                }
            }
            return false;
        }

        private static bool IsRetriableFileAccessError(Exception ex)
        {
            return ex is IOException or UnauthorizedAccessException;
        }
    }
}
