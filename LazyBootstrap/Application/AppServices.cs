using System;
using SystemEnvironment = System.Environment;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Serilog;
using LazyBootstrap.FileSystem;

namespace LazyBootstrap.Application
{
    /// <summary>
    /// Startup bootstrap helpers: Serilog configuration, runtime-context resolution and
    /// global exception logging. The application object graph is built explicitly by
    /// <see cref="ApplicationComposition"/>.
    /// </summary>
    internal static class AppServices
    {
        private static bool _serilogInitialized;
        private static bool _globalExceptionLoggingRegistered;
        private const string LogOutputTemplate = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{ProcessId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

        public static LauncherPaths Paths { get; private set; }

        public static void InitializeSerilog(string[] args)
        {
            if (_serilogInitialized) return;

            EnsurePaths(args);

            Directory.CreateDirectory(Paths.ApplicationDirectoryPath);
            string logFilePath = Path.Combine(Paths.ApplicationDirectoryPath, "LazyBootstrap.log");
            string applicationVersion = ResolveApplicationVersion();
            ResetLogFile(logFilePath);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "LazyBootstrap")
                .Enrich.WithProperty("ApplicationVersion", applicationVersion)
                .Enrich.WithProperty("ProcessId", SystemEnvironment.ProcessId)
                .Enrich.WithProperty("BaseDirectory", Paths.BaseDir)
                .Enrich.WithProperty("ApplicationDirectory", Paths.ApplicationDirectoryPath)
                .Enrich.WithProperty("ConfigPath", Paths.ConfigFilePath)
                .WriteTo.File(logFilePath, outputTemplate: LogOutputTemplate, shared: true)
                .CreateLogger();

            RegisterGlobalExceptionLogging();
            _serilogInitialized = true;

            Log.Information(
                "Serilog initialized. Version={Version}, ProcessId={ProcessId}, BaseDir={BaseDirectory}, ApplicationDir={ApplicationDirectory}, ConfigPath={ConfigPath}, LogPath={LogPath}",
                applicationVersion,
                SystemEnvironment.ProcessId,
                Paths.BaseDir,
                Paths.ApplicationDirectoryPath,
                Paths.ConfigFilePath,
                logFilePath);
        }

        private static void ResetLogFile(string logFilePath)
        {
            try
            {
                using var _ = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            }
            catch
            {
                // Keep startup tolerant if another process has a stricter lock on the log file.
            }
        }

        private static void EnsurePaths(string[] args)
        {
            if (Paths != null) return;

            Paths = LauncherPaths.Create(args);
        }

        public static void Dispose()
        {
            Log.Information("LazyBootstrap services are shutting down.");
            Log.CloseAndFlush();
            _serilogInitialized = false;
        }

        private static void RegisterGlobalExceptionLogging()
        {
            if (_globalExceptionLoggingRegistered)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    Log.Fatal(ex, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", e.IsTerminating);
                    return;
                }

                Log.Fatal("Unhandled AppDomain exception object: {ExceptionObject}. IsTerminating={IsTerminating}", e.ExceptionObject, e.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "Unobserved task exception.");
            };

            _globalExceptionLoggingRegistered = true;
        }

        private static string ResolveApplicationVersion()
        {
            try
            {
                return Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

    }
}
