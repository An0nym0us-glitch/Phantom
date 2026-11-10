using System.Diagnostics;
using System.Globalization;
using Phantom.App.Mvvm;
using Phantom.Core.Metrics;
using Phantom.Core.Models;
using Phantom.Core.Processes;

namespace Phantom.App.ViewModels;

public sealed record RelatedProcess(ProcessKey Key, string Name, int Pid)
{
    public string Display => $"{Name} ({Pid})";
}

/// <summary>
/// The inspection panel for one process. Every field is display text so that "unavailable"
/// can be stated explicitly instead of being confused with zero or empty.
/// </summary>
public sealed class ProcessDetailViewModel : ObservableObject
{
    private ProcessKey? _key;
    private string? _versionPath;
    private bool _hasProcess;
    private bool _hasExited;
    private string _name = "", _pid = "", _parent = "", _path = "", _start = "", _cpu = "", _memory = "", _privateBytes = "";
    private string _threads = "", _handles = "", _architecture = "", _owner = "", _session = "", _cpuTime = "", _lineage = "";
    private string _description = "", _company = "", _fileVersion = "", _window = "";
    private IReadOnlyList<RelatedProcess> _children = [];

    public bool HasProcess { get => _hasProcess; private set => Set(ref _hasProcess, value); }
    public bool HasExited { get => _hasExited; private set => Set(ref _hasExited, value); }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Pid { get => _pid; private set => Set(ref _pid, value); }
    public string Parent { get => _parent; private set => Set(ref _parent, value); }
    public string Path { get => _path; private set => Set(ref _path, value); }
    public string Start { get => _start; private set => Set(ref _start, value); }
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public string Memory { get => _memory; private set => Set(ref _memory, value); }
    public string PrivateBytes { get => _privateBytes; private set => Set(ref _privateBytes, value); }
    public string Threads { get => _threads; private set => Set(ref _threads, value); }
    public string Handles { get => _handles; private set => Set(ref _handles, value); }
    public string Architecture { get => _architecture; private set => Set(ref _architecture, value); }
    public string Owner { get => _owner; private set => Set(ref _owner, value); }
    public string Session { get => _session; private set => Set(ref _session, value); }
    public string CpuTime { get => _cpuTime; private set => Set(ref _cpuTime, value); }
    public string Lineage { get => _lineage; private set => Set(ref _lineage, value); }
    public string Window { get => _window; private set => Set(ref _window, value); }
    public string Description { get => _description; private set => Set(ref _description, value); }
    public string Company { get => _company; private set => Set(ref _company, value); }
    public string FileVersion { get => _fileVersion; private set => Set(ref _fileVersion, value); }
    public IReadOnlyList<RelatedProcess> Children { get => _children; private set => Set(ref _children, value); }

    public ProcessKey? Key => _key;

    public void Clear()
    {
        _key = null;
        HasProcess = false;
        HasExited = false;
    }

    /// <summary>Shows the process with the given identity, or marks the panel as exited if it is gone.</summary>
    public void Update(ProcessKey? key, ProcessSnapshot snapshot, ProcessHierarchy hierarchy)
    {
        if (key is not { } k)
        {
            Clear();
            return;
        }

        var p = snapshot.FindByPid(k.Pid);
        if (p is null || p.Key != k)
        {
            if (_key == k && HasProcess)
            {
                // Keep the last known values on screen, clearly marked as historical.
                HasExited = true;
                Cpu = Format.Unavailable;
            }
            else
            {
                Clear();
            }

            return;
        }

        _key = k;
        HasProcess = true;
        HasExited = false;
        Name = p.Name;
        Pid = p.Pid.ToString(CultureInfo.InvariantCulture);

        var parent = hierarchy.GetParent(p.Pid);
        Parent = parent is not null
            ? $"{parent.Name} ({parent.Pid})"
            : p.Pid == p.ParentPid || (p.Pid == 0)
                ? "None"
                : $"PID {p.ParentPid} (no longer running)";

        Path = p.ExecutablePath ?? Format.Describe(p.Details.PathAvailability);
        Start = p.StartTimeUtc is { } s
            ? s.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : "Not reported by Windows";
        Cpu = p.CpuPercent is null ? "Measuring…" : Format.Percent(p.CpuPercent, 1);
        Memory = Format.Bytes(p.WorkingSetBytes);
        PrivateBytes = Format.Bytes(p.PrivateBytes);
        Threads = Format.Count(p.ThreadCount);
        Handles = p.HandleCount is null ? "Not applicable" : Format.Count(p.HandleCount);
        Architecture = p.Architecture != ProcessArchitecture.Unknown
            ? p.Architecture switch
            {
                ProcessArchitecture.X86 => "x86 (32-bit)",
                ProcessArchitecture.X64 => "x64",
                ProcessArchitecture.Arm => "ARM (32-bit)",
                _ => "ARM64",
            }
            : Format.Describe(p.Details.ArchitectureAvailability == Availability.Available
                ? Availability.Error
                : p.Details.ArchitectureAvailability);
        Owner = p.Owner switch
        {
            ProcessOwnerKind.CurrentUser => "Current user",
            ProcessOwnerKind.System => "System, service or another account",
            _ => "Unknown",
        };
        Session = p.SessionId is { } sid ? sid.ToString(CultureInfo.InvariantCulture) : Format.Unavailable;
        CpuTime = p.TotalProcessorTime.TotalHours >= 1
            ? $"{(int)p.TotalProcessorTime.TotalHours}h {p.TotalProcessorTime.Minutes}m {p.TotalProcessorTime.Seconds}s"
            : $"{p.TotalProcessorTime.Minutes}m {p.TotalProcessorTime.Seconds}.{p.TotalProcessorTime.Milliseconds / 100}s";
        Window = p.HasVisibleWindow ? "Yes" : "No";

        var ancestors = hierarchy.GetAncestors(p.Pid);
        Lineage = ancestors.Count == 0
            ? "Top-level process"
            : string.Join("  ›  ", ancestors.Reverse().Select(a => a.Name).Append(p.Name));

        var children = hierarchy.GetChildren(p.Pid);
        if (children.Count != Children.Count || !children.Select(c => c.Key).SequenceEqual(Children.Select(c => c.Key)))
        {
            Children = children.Select(c => new RelatedProcess(c.Key, c.Name, c.Pid)).ToList();
        }

        LoadVersionInfo(k, p.ExecutablePath);
    }

    private void LoadVersionInfo(ProcessKey key, string? path)
    {
        if (path == _versionPath)
        {
            return;
        }

        _versionPath = path;
        Description = Company = FileVersion = path is null ? Format.Unavailable : "…";
        if (path is null)
        {
            return;
        }

        // Reading version resources touches the disk, so it happens off the UI thread.
        _ = Task.Run(() =>
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return (info.FileDescription, info.CompanyName, info.FileVersion);
            }
            catch (Exception)
            {
                return (null, null, null);
            }
        }).ContinueWith(t =>
        {
            if (_key == key && _versionPath == path)
            {
                var (description, company, version) = t.Result;
                Description = string.IsNullOrWhiteSpace(description) ? Format.Unavailable : description;
                Company = string.IsNullOrWhiteSpace(company) ? Format.Unavailable : company;
                FileVersion = string.IsNullOrWhiteSpace(version) ? Format.Unavailable : version;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
