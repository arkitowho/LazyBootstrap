using System;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using SukiUI.Dialogs;
using SukiUI.Toasts;
using LazyBootstrap.FileSystem;
using LazyBootstrap.Services;
using LazyBootstrap.Platform;
using LazyBootstrap.Serialization;
using LazyBootstrap.UI;

namespace LazyBootstrap.Application
{
    /// <summary>
    /// Explicitly constructs the application's fixed object graph without a DI container.
    /// </summary>
    internal sealed class ApplicationComposition : IDisposable
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly LauncherPaths _paths;
        private SukiToastManager _toastManager;
        private MainWindow _mainWindow;
        private bool _disposed;

        public ApplicationComposition(LauncherPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);

            _loggerFactory = new SerilogLoggerFactory(Log.Logger, dispose: false);
            _paths = paths;
            AppConfig = new AppConfigStore(
                paths.ConfigFilePath,
                CreateLogger<AppConfigStore>());
        }

        public AppConfigStore AppConfig { get; }

        public MainWindow CreateMainWindow()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_mainWindow != null)
            {
                return _mainWindow;
            }

            // SukiUI managers must be constructed only after App.Initialize loads SukiTheme.
            var dialogManager = new SukiDialogManager();
            var toastManager = new SukiToastManager();

            try
            {
                var spiceXmlConfigEditor = new SpiceXmlConfigEditor();
                var displayConfigurationService = new WindowsDisplayConfigurationService();
                var defenderExclusionService = new WindowsDefenderExclusionService(
                    CreateLogger<WindowsDefenderExclusionService>());
                var appCompatLayerService = new WindowsAppCompatLayerService();
                var startupService = new WindowsStartupService();
                var gameProcessTracker = new GameProcessTracker();
                var savedataTransferService = new SavedataTransferService(_paths);
                var displayTransactionCoordinator = new DisplaySettingsTransactionCoordinator(
                    displayConfigurationService);
                var gpuCompatLayerConfigurator = new GpuCompatLayerConfigurator(
                    AppConfig,
                    _paths,
                    spiceXmlConfigEditor,
                    CreateLogger<GpuCompatLayerConfigurator>());
                var spiceCrashLogAnalyzer = new SpiceCrashLogAnalyzer(
                    _paths,
                    CreateLogger<SpiceCrashLogAnalyzer>());


                _mainWindow = new MainWindow(
                    _paths,
                    spiceCrashLogAnalyzer,
                    gameProcessTracker,
                    defenderExclusionService,
                    appCompatLayerService,
                    spiceXmlConfigEditor,
                    gpuCompatLayerConfigurator,
                    startupService,
                    displayConfigurationService,
                    displayTransactionCoordinator,
                    new DisplayRefreshCoordinator(configurationEnabled: false),
                    new DisplayInitialization(),
                    new LaunchWorkflowLifetime(),
                    savedataTransferService,
                    dialogManager,
                    toastManager,
                    AppConfig,
                    CreateLogger<MainWindow>());
                _toastManager = toastManager;

                return _mainWindow;
            }
            catch
            {
                toastManager.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _toastManager?.Dispose();
            _loggerFactory.Dispose();
        }

        private ILogger<T> CreateLogger<T>()
        {
            return _loggerFactory.CreateLogger<T>();
        }
    }
}
