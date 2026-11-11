# Phantom

Phantom is a Windows system intelligence application. It shows what a computer is doing: every
running process and how processes relate to each other, live hardware utilisation, a timeline of
system activity, and stored history — in one dark, native desktop interface.

Its signature feature is the **System Map**, an interactive graph of the whole process ecosystem.

Phantom is **read-only**, **local-only** and **never asks for administrator rights**.

## Features

| Page | What it does |
|---|---|
| **Overview** | Live cards for CPU, memory, GPU, disk, network and process count with mini-graphs; a CPU/memory utilisation chart; top consumers; hardware summary; recent activity. |
| **System Map** | Every process is a node, sized by memory and coloured by CPU, joined to its parent. Pan, zoom, select, drag nodes, search, highlight ancestors and descendants, collapse or expand groups, filter user/system processes, focus a process, reset the layout. |
| **Processes** | Sortable, searchable, filterable table (name, PID, CPU, memory, private bytes, threads, handles, account, architecture, start time) with a details panel: path, parent, lineage, publisher, file version and children. |
| **Activity Timeline** | Chronological events — process started/exited, application launches, resource spikes, monitoring interruptions — with search, category and date filters, paging through history, and an explicit *Observed* / *Inferred* label on every event. |
| **Performance** | CPU (overall and per logical processor), memory, GPU (utilisation, dedicated/shared memory, adapters), disk (read/write throughput, active time) and network (upload/download, adapters). |
| **History** | Charts of stored metrics over 1 hour to 30 days, monitoring sessions, database statistics, CSV/JSON export, retention, compaction and "clear all history". |
| **Settings** | Update speed, history recording, retention period, spike thresholds, a list of which telemetry sources work on this machine, and data locations. |

Values that cannot be measured are shown as `—` or with a stated reason (for example
*Unavailable (access denied)*). They are never shown as zero, and Phantom never fabricates telemetry.

## Requirements

- Windows 10 version 1809 or later, or Windows 11 (x64).
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- To build the installer: [Inno Setup 6](https://jrsoftware.org/isinfo.php).

The published application is self-contained; end users do not need to install .NET.

## Install

Run `Phantom-<version>-win-x64-setup.exe`. The installer defaults to a per-user install and does
not require administrator rights. Uninstalling asks whether to also delete your settings and
recorded history.

## Build, test and run

```powershell
# Build everything
dotnet build Phantom.slnx -c Release

# Run the tests (unit tests + integration tests against the real Windows APIs)
dotnet test Phantom.slnx -c Release

# Run the application
dotnet run --project src/Phantom.App -c Release
```

### Create the installer

```powershell
pwsh build\publish.ps1
```

This publishes a self-contained `win-x64` build to `artifacts\publish\win-x64` and compiles
`installer\Phantom.iss` into `artifacts\installer\Phantom-<version>-win-x64-setup.exe`.
Use `-SkipInstaller` to publish without Inno Setup.

### Command-line options

| Option | Purpose |
|---|---|
| `--data-dir <folder>` | Store settings, history and logs in this folder instead of `%LOCALAPPDATA%\Phantom`. |
| `--smoke-test <folder>` | Drive the app through every page, save screenshots and a result file to the folder, then exit (0 = success). Used by CI. |

### Building on Linux or macOS

The libraries and tests target plain `net10.0`, so `dotnet build` and `dotnet test` work on any
platform (`EnableWindowsTargeting` lets the WPF project compile too). Tests that need Windows are
reported as skipped. The application itself only runs on Windows.

## Architecture

```
src/
  Phantom.Core               Models, service interfaces, metric maths, process hierarchy, monitoring engine
  Phantom.ProcessMonitoring  Windows process table and per-process details
  Phantom.SystemMonitoring   Windows CPU / memory / GPU / disk / network telemetry
  Phantom.SystemMap          Process graph, filtering, collapsing and layout (no UI dependency)
  Phantom.Activity           Turns successive samples into activity events
  Phantom.Storage            SQLite history store and CSV/JSON export
  Phantom.Infrastructure     Settings, file logging, data paths
  Phantom.App                WPF application (MVVM): views, view models, custom-rendered controls
tests/
  Phantom.Tests              xUnit tests
```

All Windows API calls sit behind two interfaces, `IProcessDataSource` and `ISystemMetricsSource`,
so everything above them is tested with fakes. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
for the data flow, threading model and design decisions.

## Privacy and safety

- **Read-only.** Phantom never terminates, suspends or modifies a process, never writes to another
  process's memory, never injects code and never changes system configuration.
- **No elevation.** The manifest requests `asInvoker`. Per-process details are read with
  `PROCESS_QUERY_LIMITED_INFORMATION`, the weakest access right Windows offers.
- **Local only.** There is no telemetry and no network code. Data stays in `%LOCALAPPDATA%\Phantom`:
  `settings.json`, `history.db` (SQLite) and `logs\`.
- **Minimal collection.** Process names, IDs, executable paths and resource counters. No window
  titles, command lines, keystrokes, file contents or network payloads.
- **You control retention.** History recording can be turned off; stored data is deleted after the
  retention period (14 days by default) and can be cleared at any time.

## Known limitations

- **Process events are sampled, not traced.** Real-time kernel process tracing (ETW) requires
  administrator rights, which Phantom deliberately does not ask for. Starts and exits are detected
  by comparing samples: a start carries the exact kernel creation time (*Observed*), an exit is
  timed to the sampling interval (*Inferred*), and a process that starts and exits between two
  samples is not seen.
- **Protected and other-account processes** expose only what the process table provides (name, PID,
  parent, CPU, memory, threads, handles). Their path, architecture and owner are shown as
  unavailable unless Phantom happens to be started elevated.
- **CPU percentages** are time-based (share of time not idle). Task Manager shows frequency-scaled
  "processor utility", so the two can differ slightly.
- **GPU telemetry** needs Windows 10 1709+ and a WDDM 2.x driver. Temperatures, fan speeds and
  per-vendor sensors are out of scope: they need vendor SDKs or kernel drivers.
- **Machines with more than 64 logical processors** report per-processor figures for the current
  processor group only.
- **x64 only.** ARM64 Windows runs the x64 build under emulation; a native ARM64 build has not
  been produced or tested.
- **x64 processes on ARM64** may be reported as native ARM64, a limitation of `IsWow64Process2`.

## Licence

No licence has been chosen yet. Until one is added, all rights are reserved by the authors.
