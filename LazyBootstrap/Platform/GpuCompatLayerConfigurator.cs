using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Serialization;

namespace LazyBootstrap.Platform
{

    internal readonly record struct GpuCompatLayerRuntimeState(
        bool IsFullyApplied,
        string DetectedRenderMode,
        bool HasInconsistentFiles);

    internal sealed class GpuCompatLayerConfigurator
    {
        private static readonly string[] BaseGpuCompatLayerFiles = { "nvcuda.dll", "nvcuvid.dll", "nvEncodeAPI64.dll" };
        private static readonly string[] ManagedGpuCompatLayerFiles = { "nvcuda.dll", "nvcuvid.dll", "nvEncodeAPI64.dll", "d3d9.dll" };

        private readonly AppConfigStore _appConfig;
        private readonly LauncherPaths _paths;
        private readonly SpiceXmlConfigEditor _spiceXmlConfigEditor;
        private readonly ILogger<GpuCompatLayerConfigurator> _logger;

        public GpuCompatLayerConfigurator(
            AppConfigStore appConfig,
            LauncherPaths paths,
            SpiceXmlConfigEditor spiceXmlConfigEditor,
            ILogger<GpuCompatLayerConfigurator> logger)
        {
            ArgumentNullException.ThrowIfNull(appConfig);
            ArgumentNullException.ThrowIfNull(paths);
            ArgumentNullException.ThrowIfNull(spiceXmlConfigEditor);
            ArgumentNullException.ThrowIfNull(logger);

            _appConfig = appConfig;
            _paths = paths;
            _spiceXmlConfigEditor = spiceXmlConfigEditor;
            _logger = logger;
        }

        private sealed record GpuCompatLayerSnapshots(
            FileStateSnapshot Config,
            List<FileStateSnapshot> Modules,
            FileStateSnapshot SpiceXml);

        public bool TryToggleGpuCompatLayer(bool enable, string renderMode, string spiceXmlPath, out string error)
        {
            error = string.Empty;
            renderMode = NormalizeRenderMode(renderMode);
            _logger.LogInformation("GPU compatibility layer toggle started. Enable={Enable}", enable);

            GpuCompatLayerSnapshots snapshots = null;

            try
            {
                snapshots = CaptureAllGpuCompatLayerSnapshots(spiceXmlPath);
                if (enable)
                {
                    if (!ApplyGpuCompatLayerFiles(renderMode, out error))
                    {
                        _logger.LogWarning("GPU compatibility layer file application failed. Rolling back.");
                        error = CombineErrors(error, RestoreSnapshots(snapshots));
                        return false;
                    }
                }
                else if (!RemoveGpuCompatLayerFiles(out error))
                {
                    _logger.LogWarning("GPU compatibility layer file removal failed. Rolling back.");
                    error = CombineErrors(error, RestoreSnapshots(snapshots));
                    return false;
                }

                _appConfig.WriteString(AppConfigDefaults.SettingSectionName, "compatlayer", enable ? "true" : "false");
                _appConfig.WriteString(AppConfigDefaults.SettingSectionName, "cl-rendermode", renderMode);

                if (!_spiceXmlConfigEditor.ApplySpiceOptions(spiceXmlPath, BuildDxModeUpdates(enable, renderMode), out var spiceError))
                {
                    _logger.LogWarning("GPU compatibility layer XML update failed. Rolling back.");
                    error = CombineErrors($"写入 spicetools.xml 失败: {spiceError}", RestoreSnapshots(snapshots));
                    return false;
                }

                _logger.LogInformation("GPU compatibility layer toggle completed.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GPU compatibility layer toggle failed. Rolling back.");
                error = CombineErrors(ex.Message, RestoreSnapshots(snapshots));
                return false;
            }
        }

        public bool TryPersistGpuCompatLayerRenderMode(string renderMode, bool gpuCompatLayerEnabled, string spiceXmlPath, out string error)
        {
            error = string.Empty;
            renderMode = NormalizeRenderMode(renderMode);
            _logger.LogInformation("GPU compatibility layer render mode persistence started. LayerEnabled={LayerEnabled}", gpuCompatLayerEnabled);
            GpuCompatLayerSnapshots snapshots = null;

            try
            {
                snapshots = CaptureAllGpuCompatLayerSnapshots(spiceXmlPath);
                _appConfig.WriteString(AppConfigDefaults.SettingSectionName, "cl-rendermode", renderMode);

                if (!gpuCompatLayerEnabled)
                {
                    _logger.LogInformation("GPU compatibility layer render mode persisted while layer is disabled.");
                    return true;
                }

                if (!ApplyGpuCompatLayerFiles(renderMode, out error))
                {
                    _logger.LogWarning("GPU compatibility layer file refresh failed. Rolling back.");
                    error = CombineErrors(error, RestoreSnapshots(snapshots));
                    return false;
                }

                if (!_spiceXmlConfigEditor.ApplySpiceOptions(spiceXmlPath, BuildDxModeUpdates(true, renderMode), out var spiceError))
                {
                    _logger.LogWarning("GPU compatibility layer XML refresh failed. Rolling back.");
                    error = CombineErrors($"写入 spicetools.xml 失败: {spiceError}", RestoreSnapshots(snapshots));
                    return false;
                }

                _logger.LogInformation("GPU compatibility layer render mode persistence completed.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GPU compatibility layer render mode persistence failed. Rolling back.");
                error = CombineErrors(ex.Message, RestoreSnapshots(snapshots));
                return false;
            }
        }

        public static string NormalizeRenderMode(string renderMode)
        {
            if (string.Equals(renderMode, "dxvk", StringComparison.OrdinalIgnoreCase))
            {
                return "dxvk";
            }

            if (string.Equals(renderMode, "dx9on12_external", StringComparison.OrdinalIgnoreCase))
            {
                return "dx9on12_external";
            }

            return "dx9on12";
        }

        public static string ResolveDxModeValue(bool gpuCompatLayerEnabled, string renderMode)
        {
            if (!gpuCompatLayerEnabled)
            {
                return string.Empty;
            }

            return string.Equals(NormalizeRenderMode(renderMode), "dx9on12", StringComparison.Ordinal)
                ? "1"
                : "0";
        }

        internal static GpuCompatLayerRuntimeState DetectRuntimeState(string contentsDirectoryPath, string bundledLibsDirectoryPath)
        {
            if (string.IsNullOrWhiteSpace(contentsDirectoryPath))
            {
                return new GpuCompatLayerRuntimeState(false, string.Empty, false);
            }

            string modulesDirectoryPath = Path.Combine(contentsDirectoryPath, "modules");
            if (!Directory.Exists(modulesDirectoryPath))
            {
                return new GpuCompatLayerRuntimeState(false, string.Empty, false);
            }

            int presentBaseFileCount = BaseGpuCompatLayerFiles.Count(fileName =>
                File.Exists(Path.Combine(modulesDirectoryPath, fileName)));
            bool hasAllBaseFiles = presentBaseFileCount == BaseGpuCompatLayerFiles.Length;
            bool hasPartialBaseFiles = presentBaseFileCount > 0 && !hasAllBaseFiles;

            string detectedRenderMode = string.Empty;
            bool hasUnknownD3d9State = false;
            string d3d9Path = Path.Combine(modulesDirectoryPath, "d3d9.dll");

            if (File.Exists(d3d9Path))
            {
                string dxvkStubPath = Path.Combine(bundledLibsDirectoryPath ?? string.Empty, "d3d9.dll.dxvk");
                string externalStubPath = Path.Combine(bundledLibsDirectoryPath ?? string.Empty, "d3d9.dll.dx9on12");

                if (FilesMatch(d3d9Path, dxvkStubPath))
                {
                    detectedRenderMode = "dxvk";
                }
                else if (FilesMatch(d3d9Path, externalStubPath))
                {
                    detectedRenderMode = "dx9on12_external";
                }
                else
                {
                    hasUnknownD3d9State = true;
                }
            }
            else if (hasAllBaseFiles)
            {
                detectedRenderMode = "dx9on12";
            }

            bool isFullyApplied = hasAllBaseFiles
                && (!File.Exists(d3d9Path) || !string.IsNullOrWhiteSpace(detectedRenderMode));
            bool hasInconsistentFiles = hasPartialBaseFiles
                || (File.Exists(d3d9Path) && string.IsNullOrWhiteSpace(detectedRenderMode))
                || (File.Exists(d3d9Path) && !hasAllBaseFiles)
                || hasUnknownD3d9State;

            return new GpuCompatLayerRuntimeState(
                isFullyApplied,
                detectedRenderMode,
                hasInconsistentFiles);
        }

        private List<FileStateSnapshot> CaptureGpuCompatLayerModuleSnapshots()
        {
            var modulesDirectoryPath = GetGpuCompatLayerModulesDirectoryPath();
            var snapshots = new List<FileStateSnapshot>(ManagedGpuCompatLayerFiles.Length);
            foreach (var fileName in ManagedGpuCompatLayerFiles)
            {
                snapshots.Add(FileStateSnapshot.Capture(Path.Combine(modulesDirectoryPath, fileName)));
            }

            return snapshots;
        }

        private bool ApplyGpuCompatLayerFiles(string renderMode, out string error)
        {
            error = string.Empty;
            string stubsDir = _paths.GetBundledLibsDirectoryPath();
            string modulesDir = GetGpuCompatLayerModulesDirectoryPath();
            _logger.LogDebug("Applying GPU compatibility layer files.");
            if (!Directory.Exists(stubsDir))
            {
                error = $"未找到兼容层资源目录: {stubsDir}";
                _logger.LogWarning("GPU compatibility layer resource directory is missing: {Directory}", stubsDir);
                return false;
            }

            if (!Directory.Exists(modulesDir))
            {
                error = $"未找到兼容层目标目录: {modulesDir}";
                _logger.LogWarning("GPU compatibility layer modules directory is missing: {Directory}", modulesDir);
                return false;
            }

            try
            {
                foreach (var fileName in BaseGpuCompatLayerFiles)
                {
                    string sourcePath = Path.Combine(stubsDir, fileName);
                    if (!File.Exists(sourcePath))
                    {
                        error = $"缺少文件: {fileName}";
                        _logger.LogWarning("GPU compatibility layer resource file is missing: {FileName}", fileName);
                        return false;
                    }

                    File.Copy(sourcePath, Path.Combine(modulesDir, fileName), true);
                }

                string d3d9Path = Path.Combine(modulesDir, "d3d9.dll");
                if (File.Exists(d3d9Path))
                {
                    File.Delete(d3d9Path);
                }

                string stubName = ResolveD3d9StubName(renderMode);
                if (string.IsNullOrEmpty(stubName))
                {
                    return true;
                }

                string stubPath = Path.Combine(stubsDir, stubName);
                if (!File.Exists(stubPath))
                {
                    error = $"缺少文件: {stubName}";
                    _logger.LogWarning("GPU compatibility layer d3d9 resource file is missing.");
                    return false;
                }

                File.Copy(stubPath, d3d9Path, true);
                _logger.LogDebug("GPU compatibility layer files applied.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply GPU compatibility layer files.");
                error = ex.Message;
                return false;
            }
        }

        private bool RemoveGpuCompatLayerFiles(out string error)
        {
            error = string.Empty;
            string modulesDir = GetGpuCompatLayerModulesDirectoryPath();
            if (!Directory.Exists(modulesDir))
            {
                _logger.LogDebug("GPU compatibility layer modules directory does not exist. Removal skipped.");
                return true;
            }

            try
            {
                int deleted = 0;
                foreach (var fileName in ManagedGpuCompatLayerFiles)
                {
                    string path = Path.Combine(modulesDir, fileName);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted++;
                    }
                }

                _logger.LogInformation("GPU compatibility layer managed files removed. DeletedCount={DeletedCount}", deleted);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove GPU compatibility layer files.");
                error = ex.Message;
                return false;
            }
        }

        private string GetGpuCompatLayerModulesDirectoryPath()
        {
            return Path.Combine(_paths.GetContentsDirectoryPath(), "modules");
        }

        private static string ResolveD3d9StubName(string renderMode)
        {
            return NormalizeRenderMode(renderMode) switch
            {
                "dxvk" => "d3d9.dll.dxvk",
                "dx9on12_external" => "d3d9.dll.dx9on12",
                _ => string.Empty
            };
        }

        private static bool FilesMatch(string leftPath, string rightPath)
        {
            if (string.IsNullOrWhiteSpace(leftPath)
                || string.IsNullOrWhiteSpace(rightPath)
                || !File.Exists(leftPath)
                || !File.Exists(rightPath))
            {
                return false;
            }

            var leftInfo = new FileInfo(leftPath);
            var rightInfo = new FileInfo(rightPath);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using var leftStream = File.OpenRead(leftPath);
            using var rightStream = File.OpenRead(rightPath);

            int leftByte;
            while ((leftByte = leftStream.ReadByte()) != -1)
            {
                if (leftByte != rightStream.ReadByte())
                {
                    return false;
                }
            }

            return rightStream.ReadByte() == -1;
        }

        private GpuCompatLayerSnapshots CaptureAllGpuCompatLayerSnapshots(string spiceXmlPath)
        {
            return new GpuCompatLayerSnapshots(
                FileStateSnapshot.Capture(_paths.ConfigFilePath),
                CaptureGpuCompatLayerModuleSnapshots(),
                FileStateSnapshot.Capture(spiceXmlPath));
        }

        private static SpiceOptionUpdate[] BuildDxModeUpdates(bool gpuCompatLayerEnabled, string renderMode)
        {
            string dxModeValue = ResolveDxModeValue(gpuCompatLayerEnabled, renderMode);
            return
            [
                new SpiceOptionUpdate("sp2x-dx9on12", dxModeValue, string.IsNullOrEmpty(dxModeValue))
            ];
        }

        private static string RestoreSnapshots(GpuCompatLayerSnapshots snapshots)
        {
            return snapshots == null ? string.Empty : RestoreSnapshots(snapshots.Config, snapshots.Modules, snapshots.SpiceXml);
        }

        private static string RestoreSnapshots(FileStateSnapshot configSnapshot, IEnumerable<FileStateSnapshot> moduleSnapshots, FileStateSnapshot spiceSnapshot)
        {
            var errors = new List<string>();

            var configRestoreError = RestoreSnapshot(configSnapshot, existingConfigOnly: true);
            if (!string.IsNullOrWhiteSpace(configRestoreError))
            {
                errors.Add($"启动器配置回滚失败: {configRestoreError}");
            }

            foreach (var moduleSnapshot in moduleSnapshots)
            {
                var moduleRestoreError = RestoreSnapshot(moduleSnapshot);
                if (!string.IsNullOrWhiteSpace(moduleRestoreError))
                {
                    errors.Add($"兼容层文件回滚失败: {moduleRestoreError}");
                }
            }

            var spiceRestoreError = RestoreSnapshot(spiceSnapshot);
            if (!string.IsNullOrWhiteSpace(spiceRestoreError))
            {
                errors.Add($"spicetools.xml 回滚失败: {spiceRestoreError}");
            }

            return errors.Count == 0 ? string.Empty : string.Join(" ", errors);
        }

        private static string RestoreSnapshot(FileStateSnapshot snapshot, bool existingConfigOnly = false)
        {
            try
            {
                snapshot.Restore(existingConfigOnly);
                return string.Empty;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string CombineErrors(string primaryError, string rollbackError)
        {
            if (string.IsNullOrWhiteSpace(rollbackError))
            {
                return primaryError ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(primaryError))
            {
                return rollbackError;
            }

            return $"{primaryError} {rollbackError}";
        }
    }

}
