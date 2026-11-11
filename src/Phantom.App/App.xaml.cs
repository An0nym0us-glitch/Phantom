using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Phantom.Activity;
using Phantom.App.Services;
using Phantom.App.ViewModels;
using Phantom.Core.Abstractions;
using Phantom.Core.Monitoring;
using Phantom.Infrastructure;
using Phantom.ProcessMonitoring;
using Phantom.Storage;
using Phantom.SystemMonitoring;

namespace Phantom.App;

/// <summary>Composition root: builds the object graph, starts monitoring and owns shutdown.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private ILogger<App>? _logger;
    private DispatcherTimer? _retentionTimer;
    private SmokeTest? _smokeTest;

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string? dataDirectory = ArgumentValue(e.Args, "--data-dir");
        string? smokeDirectory = ArgumentValue(e.Args, "--smoke-test");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        try
        {
            _services = BuildServices(new AppPaths(dataDirectory));
            _logger = _services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("Phantom {Version} starting", Version);

            var settings = _services.GetRequiredService<SettingsStore>();
            var engine = _services.GetRequiredService<MonitoringEngine>();
            var analyzer = _services.GetRequiredService<ActivityAnalyzer>();
            ApplySettings(settings.Current, engine, analyzer);
            settings.Changed += s => ApplySettings(s, engine, analyzer);

            var window = new MainWindow { DataContext = _services.GetRequiredService<ShellViewModel>() };
            MainWindow = window;
            window.Show();

            engine.Start();
            ScheduleRetention(settings);

            if (smokeDirectory is not null)
            {
                _smokeTest = new SmokeTest(this, window, smokeDirectory, engine);
                _smokeTest.Start();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Startup failed");
            if (smokeDirectory is not null)
            {
                SmokeTest.WriteFailure(smokeDirectory, ex);
            }
            else
            {
                MessageBox.Show($"Phantom could not start.\n\n{ex.GetType().Name}: {ex.Message}", "Phantom", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            Shutdown(1);
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private ServiceProvider BuildServices(AppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.LogDirectory));
        });
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<IProcessDataSource, WindowsProcessDataSource>();
        services.AddSingleton<ISystemMetricsSource, WindowsSystemMetricsSource>();
        services.AddSingleton<ActivityAnalyzer>();
        services.AddSingleton<IActivityAnalyzer>(sp => sp.GetRequiredService<ActivityAnalyzer>());
        services.AddSingleton<IHistoryStore>(sp =>
            new SqliteHistoryStore(paths.DatabaseFile, sp.GetRequiredService<ILogger<SqliteHistoryStore>>()));
        services.AddSingleton(sp => new MonitoringEngine(
            sp.GetRequiredService<IProcessDataSource>(),
            sp.GetRequiredService<ISystemMetricsSource>(),
            sp.GetRequiredService<IActivityAnalyzer>(),
            sp.GetRequiredService<IHistoryStore>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<MonitoringEngine>>(),
            Version));
        services.AddSingleton(sp => new UiFrameHub(sp.GetRequiredService<MonitoringEngine>(), Dispatcher));
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ShellViewModel>();
        return services.BuildServiceProvider();
    }

    private static void ApplySettings(AppSettings settings, MonitoringEngine engine, ActivityAnalyzer analyzer)
    {
        engine.Options = new MonitoringOptions
        {
            SampleInterval = TimeSpan.FromMilliseconds(settings.SampleIntervalMs),
            PersistInterval = TimeSpan.FromSeconds(10),
            PersistHistory = settings.PersistHistory,
        };
        analyzer.Options = new ActivityOptions
        {
            ProcessCpuSpikePercent = settings.ProcessCpuSpikePercent,
            SystemCpuSpikePercent = settings.SystemCpuSpikePercent,
            MemorySpikePercent = settings.MemorySpikePercent,
        };
    }

    /// <summary>Enforces the retention period at startup and every six hours, off the UI thread.</summary>
    private void ScheduleRetention(SettingsStore settings)
    {
        var history = _services!.GetRequiredService<IHistoryStore>();
        void Run() => Task.Run(() =>
        {
            long removed = history.ApplyRetention(DateTime.UtcNow.AddDays(-settings.Current.RetentionDays));
            if (removed > 0)
            {
                _logger?.LogInformation("Retention removed {Count} records", removed);
            }
        });

        Run();
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _retentionTimer.Tick += (_, _) => Run();
        _retentionTimer.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        if (_smokeTest is not null)
        {
            _smokeTest.Fail(e.Exception);
            return;
        }

        MessageBox.Show(
            $"Something went wrong in the interface, but monitoring is still running.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\nDetails were written to the log folder.",
            "Phantom", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _retentionTimer?.Stop();
        if (_services is not null)
        {
            try
            {
                // The sampling loop runs on the thread pool and never waits on the UI thread,
                // so blocking here briefly to flush the session is safe.
                _services.GetRequiredService<MonitoringEngine>().StopAsync().Wait(TimeSpan.FromSeconds(5));
                _logger?.LogInformation("Phantom exiting");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Shutdown was not clean");
            }

            _services.Dispose();
        }

        base.OnExit(e);
    }
}
