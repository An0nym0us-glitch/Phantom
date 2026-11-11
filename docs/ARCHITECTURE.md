# Architecture

## Data flow

```
Windows APIs
   │
   ├─ WindowsProcessDataSource  (IProcessDataSource)      NtQuerySystemInformation, OpenProcess(LIMITED)
   └─ WindowsSystemMetricsSource (ISystemMetricsSource)   processor times, GlobalMemoryStatusEx, PDH, NIC counters
        │
        ▼
MonitoringEngine  ── one background loop ──────────────────────────────────────────────
   1. sample system metrics
   2. build ProcessSnapshot (CPU deltas, cached details) and ProcessHierarchy
   3. ActivityAnalyzer → new ActivityEvents
   4. persist events and 10-second metric averages to IHistoryStore (SQLite)
   5. publish an immutable MonitoringFrame
        │
        ▼
UiFrameHub  ── coalesces frames, hops to the UI thread ──▶  view models  ──▶  views
```

## Threading

- **Sampling thread.** Everything that touches Windows or SQLite on a schedule runs in
  `MonitoringEngine`'s loop on the thread pool. It never waits on the UI thread.
- **UI thread.** `UiFrameHub` posts at most one pending callback to the dispatcher. If the UI is
  busy while several frames arrive, only the newest is delivered, so work can never queue up.
- **On-demand work** (history queries, exports, graph layout, file version lookups) runs in
  `Task.Run` and returns to the UI thread with the result. Stale results are discarded by a
  version counter.
- Frames, snapshots, hierarchies and graphs are immutable, so they cross threads without locks.

Only the visible page does per-frame work. Hidden pages mark themselves dirty and catch up when
shown.

## Process discovery

One `NtQuerySystemInformation(SystemProcessInformation)` call returns the whole process table —
name, PID, parent PID, creation time, CPU times, working set, private bytes, threads, handles,
session — without opening any process and without privileges. This is why collection costs a few
milliseconds even with hundreds of processes.

Slow-changing details (executable path, architecture, owner) need a process handle. They are
queried once per process identity and cached. If Windows refuses, the reason is recorded as an
`Availability` value and shown in the UI.

**Identity.** Windows recycles PIDs, so a process is identified by `ProcessKey(Pid, CreateTime)`.

**Hierarchy.** Windows stores only the creator's PID. `ProcessHierarchy` accepts a parent link only
if the parent exists and was created no later than the child; otherwise the child becomes a root.
This prevents a recycled PID from being shown as the parent of an older process.

## System Map

`Phantom.SystemMap` has no UI dependency and is fully unit tested.

- `ProcessGraph.Build` applies the user/system filter and the collapsed set and produces the
  visible forest. Its `TopologySignature` changes only when nodes or edges change.
- `RadialClusterLayout.Compute` is deterministic and O(n). Each process tree becomes a radial
  cluster: the root in the centre, descendants on concentric rings, angular space shared in
  proportion to subtree size. Crowded rings split into up to three staggered sub-rings. Clusters
  are packed into rows, largest first.

`SystemMapCanvas` draws the whole scene in a single `OnRender` pass — no per-node WPF elements.

- A metrics-only refresh reuses the previous layout and costs one repaint.
- Layout is recomputed only when the topology signature changes, on a background thread.
- Nodes animate from old to new positions; the per-frame render loop is attached only while
  something is moving.
- Brushes and pens are frozen and shared, label layouts are cached, off-screen nodes are culled.

## Activity events and provenance

`ActivityAnalyzer` compares each snapshot with the previous one.

| Event | How it is detected | Provenance |
|---|---|---|
| Process started | New `ProcessKey`; timestamp is the kernel creation time | Observed |
| Process exited | `ProcessKey` disappeared; timestamp is the sample time | Inferred |
| Application launch | A process started during monitoring shows a visible top-level window | Inferred |
| Resource spike | Usage above a threshold for three consecutive samples, with hysteresis | Inferred |
| Monitoring interruption | A gap far longer than the interval (sleep), or a collector failure | Inferred / Observed |
| Monitoring | Session start/end, pause/resume | Observed |

The first snapshot after start, resume or an interruption is a baseline and produces no events.

## Storage

`SqliteHistoryStore` uses one WAL-mode connection guarded by a lock. Tables: `sessions`,
`metric_samples`, `activity_events`.

- Metric samples are averaged over 10 seconds before being written.
- Missing measurements are stored as `NULL` and stay `NULL` through queries and exports.
- Chart queries bucket in SQL (`GROUP BY` on the time slot), so a 30-day chart returns at most
  240 rows.
- If the file is corrupted at startup it is renamed to `history.db.corrupt-<timestamp>` and a new
  database is created. If the database cannot be opened at all, the store reports itself
  unavailable: writes are ignored, reads return nothing, and live monitoring carries on.

## Error handling

- A failed detail query degrades one process, not the snapshot.
- A failed sensor family degrades that family; the others keep reporting.
- A failed process enumeration keeps the last snapshot on screen, marks it stale and records one
  interruption event; a recovery event follows.
- Exceptions in a frame subscriber, in persistence, or on the UI thread are logged and contained.

## Testing

- **Unit tests** cover metric maths, models, hierarchy construction, snapshot building, graph and
  layout generation, activity analysis, the SQLite store, export, settings and the engine's
  failure handling, using fakes for the two Windows interfaces.
- **Windows integration tests** (`WindowsIntegrationTests`) call the real collectors and check the
  results against `System.Diagnostics` and sanity ranges.
- **UI smoke test** (`--smoke-test`) launches the real application, visits every page, exercises
  selection, collapse, filtering, the inspector and pause/resume, and saves screenshots.
- **Static XAML check** (`build/check-xaml.py`) verifies resource keys and binding paths, which
  the XAML compiler does not.

CI (`.github/workflows/ci.yml`) runs all of the above on `windows-latest`, then builds the
installer and verifies a silent install, launch and uninstall.
