using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Core.Abstractions;
using Phantom.Core.Metrics;
using Phantom.Core.Models;

namespace Phantom.Core.Processes;

/// <summary>
/// Assembles <see cref="ProcessSnapshot"/>s from raw OS samples: computes per-process CPU
/// from successive samples and caches slow-changing details per process identity so each
/// process is opened only once in its lifetime.
/// </summary>
public sealed class ProcessSnapshotBuilder
{
    private readonly IProcessDataSource _source;
    private readonly IClock _clock;
    private readonly int _logicalProcessors;
    private readonly ILogger _logger;

    private Dictionary<ProcessKey, TimeSpan> _previousCpu = [];
    private Dictionary<ProcessKey, ProcessDetails> _details = [];
    private DateTime _previousTime;

    public ProcessSnapshotBuilder(IProcessDataSource source, IClock clock, int logicalProcessors, ILogger? logger = null)
    {
        _source = source;
        _clock = clock;
        _logicalProcessors = Math.Max(1, logicalProcessors);
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Forgets CPU baselines (used after an interruption so stale deltas are not reported as spikes).</summary>
    public void ResetBaselines()
    {
        _previousCpu = [];
        _previousTime = default;
    }

    public ProcessSnapshot Build()
    {
        var raw = _source.EnumerateProcesses();
        var now = _clock.UtcNow;
        var elapsed = _previousTime == default ? TimeSpan.Zero : now - _previousTime;

        IReadOnlySet<int> windowed;
        try
        {
            windowed = _source.GetPidsWithVisibleWindows();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Window enumeration failed");
            windowed = new HashSet<int>();
        }

        var cpu = new Dictionary<ProcessKey, TimeSpan>(raw.Count);
        var details = new Dictionary<ProcessKey, ProcessDetails>(raw.Count);
        var result = new List<ProcessInfo>(raw.Count);

        foreach (var r in raw)
        {
            var key = r.Key;
            if (cpu.ContainsKey(key))
            {
                continue; // Defensive: a malformed enumeration must not produce duplicate identities.
            }

            double? cpuPercent = null;
            if (_previousCpu.TryGetValue(key, out var before))
            {
                cpuPercent = MetricMath.ProcessCpuPercent(r.TotalProcessorTime - before, elapsed, _logicalProcessors);
            }

            if (!_details.TryGetValue(key, out var d))
            {
                d = QueryDetailsSafe(r);
            }

            cpu[key] = r.TotalProcessorTime;
            details[key] = d;

            result.Add(new ProcessInfo
            {
                Key = key,
                Pid = r.Pid,
                ParentPid = r.ParentPid,
                Name = r.Name,
                StartTimeUtc = r.StartTimeUtc,
                CpuPercent = cpuPercent,
                TotalProcessorTime = r.TotalProcessorTime,
                WorkingSetBytes = r.WorkingSetBytes,
                PrivateBytes = r.PrivateBytes,
                ThreadCount = r.ThreadCount,
                HandleCount = r.HandleCount,
                SessionId = r.SessionId,
                HasVisibleWindow = windowed.Contains(r.Pid),
                Details = d,
            });
        }

        // Replacing the caches drops entries for processes that have exited.
        _previousCpu = cpu;
        _details = details;
        _previousTime = now;

        return new ProcessSnapshot(now, result);
    }

    private ProcessDetails QueryDetailsSafe(RawProcessSample r)
    {
        try
        {
            return _source.QueryDetails(r.Pid, r.SessionId);
        }
        catch (Exception ex)
        {
            // One uninspectable process must never take down the whole snapshot.
            _logger.LogDebug(ex, "Detail query failed for PID {Pid}", r.Pid);
            return ProcessDetails.Unavailable(Availability.Error);
        }
    }
}
