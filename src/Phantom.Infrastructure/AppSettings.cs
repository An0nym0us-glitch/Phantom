using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Phantom.Infrastructure;

public sealed record AppSettings
{
    /// <summary>Sampling interval in milliseconds.</summary>
    public int SampleIntervalMs { get; init; } = 1000;
    public bool PersistHistory { get; init; } = true;
    public int RetentionDays { get; init; } = 14;
    public double ProcessCpuSpikePercent { get; init; } = 50;
    public double SystemCpuSpikePercent { get; init; } = 90;
    public double MemorySpikePercent { get; init; } = 90;

    public static readonly int[] AllowedIntervalsMs = [500, 1000, 2000, 5000];
    public static readonly int[] AllowedRetentionDays = [1, 7, 14, 30, 90];

    /// <summary>Returns a copy with every value forced into its supported range.</summary>
    public AppSettings Normalize() => this with
    {
        SampleIntervalMs = Math.Clamp(SampleIntervalMs, 250, 60_000),
        RetentionDays = Math.Clamp(RetentionDays, 1, 365),
        ProcessCpuSpikePercent = Clamp(ProcessCpuSpikePercent, 5, 100, 50),
        SystemCpuSpikePercent = Clamp(SystemCpuSpikePercent, 10, 100, 90),
        MemorySpikePercent = Clamp(MemorySpikePercent, 10, 100, 90),
    };

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>Loads and saves settings as JSON. A missing or damaged file yields defaults rather than an error.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly object _gate = new();

    public SettingsStore(AppPaths paths, ILogger<SettingsStore>? logger = null)
    {
        _path = paths.SettingsFile;
        _logger = logger;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options);
                if (loaded is not null)
                {
                    return loaded.Normalize();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.LogWarning("Settings file could not be read ({Error}); using defaults", ex.GetType().Name);
        }

        return new AppSettings();
    }

    /// <summary>Applies and persists new settings. Returns false if they could not be written to disk.</summary>
    public bool Save(AppSettings settings)
    {
        settings = settings.Normalize();
        bool written = true;
        lock (_gate)
        {
            Current = settings;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning("Settings could not be saved ({Error})", ex.GetType().Name);
                written = false;
            }
        }

        Changed?.Invoke(settings);
        return written;
    }
}
