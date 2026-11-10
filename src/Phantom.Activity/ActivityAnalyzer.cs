using System.Globalization;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;

namespace Phantom.Activity;

public sealed record ActivityOptions
{
    /// <summary>A process using at least this share of total CPU counts as spiking.</summary>
    public double ProcessCpuSpikePercent { get; init; } = 50;
    public double SystemCpuSpikePercent { get; init; } = 90;
    public double MemorySpikePercent { get; init; } = 90;
    /// <summary>Consecutive samples above the threshold before a spike is reported. Filters out momentary bursts.</summary>
    public int SustainSamples { get; init; } = 3;
}

/// <summary>
/// Derives activity events by comparing successive process snapshots.
/// <para>
/// Provenance rules: a process start carries the kernel-recorded creation time and is marked
/// <see cref="EventProvenance.Observed"/>. Exits, application launches and spikes are deduced
/// from sampling and are marked <see cref="EventProvenance.Inferred"/>; their timestamps are
/// accurate only to the sampling interval. Processes that start and exit between two samples
/// are not seen at all.
/// </para>
/// </summary>
public sealed class ActivityAnalyzer : IActivityAnalyzer
{
    private sealed class Tracker
    {
        public int Above;
        public int Below;
        public bool Active;

        /// <summary>Returns true exactly once when the value has stayed above the threshold long enough.</summary>
        public bool Update(double? value, double threshold, int sustain)
        {
            if (value is not { } v)
            {
                return false;
            }

            if (v >= threshold)
            {
                Above++;
                Below = 0;
                if (!Active && Above >= sustain)
                {
                    Active = true;
                    return true;
                }
            }
            else
            {
                Above = 0;
                // Hysteresis: re-arm only after the value has clearly dropped for a while.
                if (Active && v < threshold * 0.6 && ++Below >= sustain)
                {
                    Active = false;
                    Below = 0;
                }
            }

            return false;
        }
    }

    private readonly object _gate = new();
    private Dictionary<ProcessKey, ProcessInfo>? _previous;
    private readonly HashSet<ProcessKey> _launchCandidates = [];
    private readonly Dictionary<ProcessKey, Tracker> _processCpu = [];
    private readonly Tracker _systemCpu = new();
    private readonly Tracker _memory = new();
    private ActivityOptions _options = new();

    public ActivityOptions Options
    {
        get => _options;
        set => _options = value ?? new ActivityOptions();
    }

    public void Reset()
    {
        lock (_gate)
        {
            _previous = null;
            _launchCandidates.Clear();
            _processCpu.Clear();
            _systemCpu.Above = _systemCpu.Below = 0;
            _memory.Above = _memory.Below = 0;
        }
    }

    public IReadOnlyList<ActivityEvent> Analyze(ProcessSnapshot snapshot, SystemMetricsSample metrics)
    {
        lock (_gate)
        {
            return AnalyzeCore(snapshot, metrics);
        }
    }

    private List<ActivityEvent> AnalyzeCore(ProcessSnapshot snapshot, SystemMetricsSample metrics)
    {
        var options = _options;
        int sustain = Math.Max(1, options.SustainSamples);
        var events = new List<ActivityEvent>();
        var current = new Dictionary<ProcessKey, ProcessInfo>(snapshot.Count);
        foreach (var p in snapshot.Processes)
        {
            current[p.Key] = p;
        }

        if (_previous is { } previous)
        {
            foreach (var p in snapshot.Processes)
            {
                if (previous.ContainsKey(p.Key))
                {
                    continue;
                }

                string parent = snapshot.FindByPid(p.ParentPid) is { } pp && pp.Pid != p.Pid
                    ? $"{pp.Name} ({pp.Pid})"
                    : $"PID {p.ParentPid} (no longer running)";
                events.Add(new ActivityEvent
                {
                    TimestampUtc = p.StartTimeUtc ?? snapshot.TimestampUtc,
                    Category = ActivityCategory.ProcessStarted,
                    Provenance = p.StartTimeUtc is null ? EventProvenance.Inferred : EventProvenance.Observed,
                    Pid = p.Pid,
                    ProcessName = p.Name,
                    Description = $"{p.Name} started. Parent: {parent}.",
                });
                _launchCandidates.Add(p.Key);
            }

            foreach (var (key, p) in previous)
            {
                if (current.ContainsKey(key))
                {
                    continue;
                }

                string lifetime = p.StartTimeUtc is { } s && snapshot.TimestampUtc > s
                    ? $" after running for {Format.Duration(snapshot.TimestampUtc - s)}"
                    : "";
                events.Add(new ActivityEvent
                {
                    TimestampUtc = snapshot.TimestampUtc,
                    Category = ActivityCategory.ProcessExited,
                    Provenance = EventProvenance.Inferred,
                    Pid = p.Pid,
                    ProcessName = p.Name,
                    Description = $"{p.Name} exited{lifetime}.",
                });
                _launchCandidates.Remove(key);
                _processCpu.Remove(key);
            }
        }

        foreach (var p in snapshot.Processes)
        {
            if (p.HasVisibleWindow && _launchCandidates.Remove(p.Key))
            {
                events.Add(new ActivityEvent
                {
                    TimestampUtc = snapshot.TimestampUtc,
                    Category = ActivityCategory.ApplicationLaunched,
                    Provenance = EventProvenance.Inferred,
                    Pid = p.Pid,
                    ProcessName = p.Name,
                    Description = $"{p.Name} opened a window.",
                });
            }

            // The Idle pseudo-process accumulates idle time and is not a consumer.
            if (p.Pid == 0 || p.CpuPercent is null)
            {
                continue;
            }

            if (!_processCpu.TryGetValue(p.Key, out var tracker))
            {
                if (p.CpuPercent < options.ProcessCpuSpikePercent)
                {
                    continue; // Only allocate trackers for processes that get near the threshold.
                }

                _processCpu[p.Key] = tracker = new Tracker();
            }

            if (tracker.Update(p.CpuPercent, options.ProcessCpuSpikePercent, sustain))
            {
                events.Add(Spike(snapshot.TimestampUtc, p,
                    $"{p.Name} sustained high CPU usage ({Pct(p.CpuPercent)} of total capacity)."));
            }
            else if (tracker is { Active: false, Above: 0 })
            {
                _processCpu.Remove(p.Key);
            }
        }

        if (_systemCpu.Update(metrics.CpuPercent, options.SystemCpuSpikePercent, sustain))
        {
            var top = snapshot.Processes.Where(p => p.Pid != 0).MaxBy(p => p.CpuPercent ?? -1);
            string culprit = top?.CpuPercent is > 0 ? $" Top consumer: {top.Name} ({Pct(top.CpuPercent)})." : "";
            events.Add(Spike(snapshot.TimestampUtc, null,
                $"System CPU usage stayed above {options.SystemCpuSpikePercent:0}% ({Pct(metrics.CpuPercent)}).{culprit}"));
        }

        if (_memory.Update(metrics.MemoryPercent, options.MemorySpikePercent, sustain))
        {
            var top = snapshot.Processes.MaxBy(p => p.WorkingSetBytes);
            string culprit = top is not null ? $" Largest working set: {top.Name} ({Format.Bytes(top.WorkingSetBytes)})." : "";
            events.Add(Spike(snapshot.TimestampUtc, null,
                $"Memory usage stayed above {options.MemorySpikePercent:0}% ({Pct(metrics.MemoryPercent)}).{culprit}"));
        }

        _previous = current;
        return events;
    }

    private static string Pct(double? v) => (v ?? 0).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static ActivityEvent Spike(DateTime at, ProcessInfo? p, string description) => new()
    {
        TimestampUtc = at,
        Category = ActivityCategory.ResourceSpike,
        Provenance = EventProvenance.Inferred,
        Pid = p?.Pid,
        ProcessName = p?.Name,
        Description = description,
    };
}
