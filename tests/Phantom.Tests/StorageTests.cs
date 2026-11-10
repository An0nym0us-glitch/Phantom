using System.Text;
using Phantom.Core.Models;
using Phantom.Infrastructure;
using Phantom.Storage;

namespace Phantom.Tests;

public class StorageTests
{
    private static readonly DateTime T = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static ActivityEvent Event(int minutes, ActivityCategory category = ActivityCategory.ProcessStarted,
        string name = "notepad.exe", int? pid = 100, string? description = null) => new()
        {
            TimestampUtc = T.AddMinutes(minutes),
            Category = category,
            Provenance = category == ActivityCategory.ProcessStarted ? EventProvenance.Observed : EventProvenance.Inferred,
            Pid = pid,
            ProcessName = name,
            Description = description ?? $"{name} did something",
        };

    private static SystemMetricsSample Sample(int seconds, double? cpu = 10, double? gpu = null) => new()
    {
        TimestampUtc = T.AddSeconds(seconds),
        CpuPercent = cpu,
        MemoryTotalBytes = 16_000,
        MemoryAvailableBytes = 8_000,
        GpuPercent = gpu,
        DiskReadBytesPerSec = 100,
        NetworkDownloadBytesPerSec = 2048.5,
        ProcessCount = 200,
    };

    [Fact]
    public void Events_round_trip_and_are_returned_newest_first()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        Assert.True(store.IsAvailable);
        long session = store.BeginSession(T, "1.0");
        store.AppendEvents(session, [Event(0), Event(5, ActivityCategory.ProcessExited, pid: null), Event(2)]);

        var events = store.QueryEvents(new ActivityQuery());
        Assert.Equal(3, events.Count);
        Assert.Equal([5, 2, 0], events.Select(e => (int)(e.TimestampUtc - T).TotalMinutes));
        Assert.All(events, e => Assert.True(e.Id > 0));
        Assert.Equal(DateTimeKind.Utc, events[0].TimestampUtc.Kind);
        Assert.Null(events[0].Pid);
        Assert.Equal(EventProvenance.Inferred, events[0].Provenance);
        Assert.Equal(EventProvenance.Observed, events[1].Provenance);
    }

    [Fact]
    public void Events_can_be_filtered_by_date_category_and_text()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long s = store.BeginSession(T, "1.0");
        store.AppendEvents(s,
        [
            Event(0, name: "chrome.exe", pid: 11),
            Event(10, ActivityCategory.ProcessExited, "chrome.exe", 11),
            Event(20, ActivityCategory.ResourceSpike, "100%_done.exe", 12, "weird 100%_done name"),
            Event(30, name: "notepad.exe", pid: 13),
        ]);

        Assert.Equal(2, store.QueryEvents(new ActivityQuery { FromUtc = T.AddMinutes(5), ToUtc = T.AddMinutes(25) }).Count);
        Assert.Single(store.QueryEvents(new ActivityQuery { Categories = [ActivityCategory.ResourceSpike] }));
        Assert.Empty(store.QueryEvents(new ActivityQuery { Categories = [] }));
        Assert.Equal(2, store.QueryEvents(new ActivityQuery { SearchText = "CHROME" }).Count);
        Assert.Single(store.QueryEvents(new ActivityQuery { SearchText = "13" }));
        // LIKE wildcards in user input are treated literally.
        Assert.Single(store.QueryEvents(new ActivityQuery { SearchText = "100%_done" }));
        Assert.Empty(store.QueryEvents(new ActivityQuery { SearchText = "%" + "zzz" }));
        Assert.Equal(2, store.CountEvents(new ActivityQuery { SearchText = "chrome" }));
        // Injection attempts are just text.
        Assert.Empty(store.QueryEvents(new ActivityQuery { SearchText = "'; DROP TABLE activity_events; --" }));
        Assert.Equal(4, store.CountEvents(new ActivityQuery()));
    }

    [Fact]
    public void Event_paging_uses_limit_and_offset()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long s = store.BeginSession(T, "1.0");
        store.AppendEvents(s, Enumerable.Range(0, 25).Select(i => Event(i)).ToList());

        var page = store.QueryEvents(new ActivityQuery { Limit = 10, Offset = 20 });
        Assert.Equal(5, page.Count);
        Assert.Equal(T.AddMinutes(4), page[0].TimestampUtc);
    }

    [Fact]
    public void Metrics_round_trip_preserving_missing_values_as_null()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long s = store.BeginSession(T, "1.0");
        store.AppendMetrics(s, [Sample(0, cpu: 42.5, gpu: null), Sample(10, cpu: null, gpu: 7)]);

        var rows = store.QueryMetrics(T.AddMinutes(-1), T.AddMinutes(1), 10_000);
        Assert.Equal(2, rows.Count);
        Assert.Equal(42.5, rows[0].CpuPercent);
        Assert.Null(rows[0].GpuPercent);
        Assert.Null(rows[1].CpuPercent);
        Assert.Equal(7, rows[1].GpuPercent);
        Assert.Null(rows[0].DiskWriteBytesPerSec);
        Assert.Equal(8_000, rows[0].MemoryUsedBytes);
        Assert.Equal(2048.5, rows[0].NetworkDownloadBytesPerSec);
        Assert.Equal(200, rows[0].ProcessCount);
    }

    [Fact]
    public void Metrics_are_downsampled_into_averaged_buckets()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long s = store.BeginSession(T, "1.0");
        store.AppendMetrics(s, Enumerable.Range(0, 100).Select(i => Sample(i, cpu: i < 50 ? 0 : 100)).ToList());

        var rows = store.QueryMetrics(T, T.AddSeconds(100), 2);
        Assert.Equal(2, rows.Count);
        Assert.Equal(0, rows[0].CpuPercent!.Value, 3);
        Assert.Equal(100, rows[1].CpuPercent!.Value, 3);
        Assert.Empty(store.QueryMetrics(T.AddSeconds(100), T, 10));
    }

    [Fact]
    public void Retention_removes_only_old_records()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long old = store.BeginSession(T.AddDays(-30), "1.0");
        store.AppendEvents(old, [Event(-30 * 24 * 60)]);
        store.AppendMetrics(old, [Sample(-30 * 24 * 3600)]);
        store.EndSession(old, T.AddDays(-30).AddHours(1));
        long current = store.BeginSession(T, "1.0");
        store.AppendEvents(current, [Event(0)]);
        store.AppendMetrics(current, [Sample(0)]);

        long removed = store.ApplyRetention(T.AddDays(-7));

        Assert.Equal(3, removed);
        var stats = store.GetStats();
        Assert.Equal(1, stats.EventCount);
        Assert.Equal(1, stats.MetricSampleCount);
        Assert.Equal(1, stats.SessionCount);
        Assert.Equal(T, stats.OldestRecordUtc);
        Assert.True(stats.DatabaseSizeBytes > 0);
    }

    [Fact]
    public void Clearing_history_removes_data_but_keeps_the_live_session()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long ended = store.BeginSession(T.AddHours(-2), "1.0");
        store.EndSession(ended, T.AddHours(-1));
        long live = store.BeginSession(T, "1.0");
        store.AppendEvents(live, [Event(0), Event(1)]);
        store.AppendMetrics(live, [Sample(0)]);

        store.ClearAll();

        var stats = store.GetStats();
        Assert.Equal(0, stats.EventCount);
        Assert.Equal(0, stats.MetricSampleCount);
        Assert.Equal(live, Assert.Single(store.GetSessions(10)).Id);
        store.Compact();
        store.AppendEvents(live, [Event(2)]);
        Assert.Equal(1, store.GetStats().EventCount);
    }

    [Fact]
    public void Sessions_are_recorded_and_crashed_sessions_are_closed_on_next_start()
    {
        using var dir = new TempDirectory();
        string path = dir.File("h.db");
        using (var store = new SqliteHistoryStore(path))
        {
            long crashed = store.BeginSession(T, "1.0");
            store.AppendMetrics(crashed, [Sample(120)]);
        }

        using var reopened = new SqliteHistoryStore(path);
        long next = reopened.BeginSession(T.AddHours(1), "1.1");
        reopened.EndSession(next, T.AddHours(2));

        var sessions = reopened.GetSessions(10);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("1.1", sessions[0].AppVersion);
        Assert.Equal(T.AddHours(2), sessions[0].EndedUtc);
        Assert.Equal(T.AddSeconds(120), sessions[1].EndedUtc); // closed at its last sample
    }

    [Fact]
    public void Data_persists_across_reopen()
    {
        using var dir = new TempDirectory();
        string path = dir.File("h.db");
        using (var store = new SqliteHistoryStore(path))
        {
            store.AppendEvents(store.BeginSession(T, "1.0"), [Event(0)]);
        }

        using var again = new SqliteHistoryStore(path);
        Assert.Single(again.QueryEvents(new ActivityQuery()));
    }

    [Fact]
    public void A_corrupted_database_is_quarantined_and_replaced()
    {
        using var dir = new TempDirectory();
        string path = dir.File("h.db");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(new string('x', 8192)));

        using var store = new SqliteHistoryStore(path);

        Assert.True(store.IsAvailable);
        Assert.Contains("corrupted", store.StatusMessage);
        Assert.Single(Directory.GetFiles(dir.Path, "h.db.corrupt-*"));
        store.AppendEvents(store.BeginSession(T, "1.0"), [Event(0)]);
        Assert.Single(store.QueryEvents(new ActivityQuery()));
    }

    [Fact]
    public void An_unopenable_database_leaves_the_store_unavailable_but_harmless()
    {
        using var dir = new TempDirectory();
        // A directory where the file should be cannot be opened as a database.
        string path = dir.File("blocked.db");
        Directory.CreateDirectory(path);

        using var store = new SqliteHistoryStore(path);

        Assert.False(store.IsAvailable);
        Assert.NotNull(store.StatusMessage);
        Assert.Equal(0, store.BeginSession(T, "1.0"));
        store.AppendEvents(0, [Event(0)]);
        store.AppendMetrics(0, [Sample(0)]);
        store.EndSession(0, T);
        Assert.Empty(store.QueryEvents(new ActivityQuery()));
        Assert.Empty(store.QueryMetrics(T.AddDays(-1), T.AddDays(1), 100));
        Assert.Empty(store.GetSessions(5));
        Assert.Equal(0, store.CountEvents(new ActivityQuery()));
        Assert.Equal(0, store.ApplyRetention(T));
        Assert.Equal(0, store.GetStats().EventCount);
        store.ClearAll();
        store.Compact();
    }

    [Fact]
    public void Events_export_to_csv_with_escaping_and_formula_neutralisation()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long s = store.BeginSession(T, "1.0");
        store.AppendEvents(s,
        [
            Event(0, name: "=cmd|calc.exe", description: "said \"hi\", twice"),
            Event(1, name: "plain.exe", pid: null, description: "simple"),
        ]);

        using var stream = new MemoryStream();
        long count = HistoryExporter.ExportEvents(store, new ActivityQuery(), stream, ExportFormat.Csv);
        string[] lines = Encoding.UTF8.GetString(stream.ToArray()).TrimStart('﻿').Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, count);
        Assert.Equal("TimestampUtc,Category,Provenance,Pid,ProcessName,Description", lines[0]);
        Assert.Equal("2026-03-01T10:00:00.0000000Z,ProcessStarted,Observed,100,'=cmd|calc.exe,\"said \"\"hi\"\", twice\"", lines[1]);
        Assert.Equal("2026-03-01T10:01:00.0000000Z,ProcessStarted,Observed,,plain.exe,simple", lines[2]);
    }

    [Fact]
    public void Metrics_export_leaves_missing_values_empty_and_json_is_valid()
    {
        using var dir = new TempDirectory();
        using var store = new SqliteHistoryStore(dir.File("h.db"));
        long s = store.BeginSession(T, "1.0");
        store.AppendMetrics(s, [Sample(0, cpu: 12.5, gpu: null)]);
        store.AppendEvents(s, [Event(0)]);

        using var csv = new MemoryStream();
        Assert.Equal(1, HistoryExporter.ExportMetrics(store, T.AddDays(-1), T.AddDays(1), csv, ExportFormat.Csv));
        string row = Encoding.UTF8.GetString(csv.ToArray()).Split(Environment.NewLine)[1];
        Assert.StartsWith("2026-03-01T10:00:00.0000000Z,12.5,16000,8000,,,,100,,,,2048.5,200", row);

        using var json = new MemoryStream();
        HistoryExporter.ExportMetrics(store, T.AddDays(-1), T.AddDays(1), json, ExportFormat.Json);
        using var doc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(json.ToArray()).TrimStart('﻿'));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement[0].GetProperty("gpuPercent").ValueKind);
        Assert.Equal(12.5, doc.RootElement[0].GetProperty("cpuPercent").GetDouble());

        using var eventsJson = new MemoryStream();
        HistoryExporter.ExportEvents(store, new ActivityQuery(), eventsJson, ExportFormat.Json);
        using var eventsDoc = System.Text.Json.JsonDocument.Parse(Encoding.UTF8.GetString(eventsJson.ToArray()).TrimStart('﻿'));
        Assert.Equal("ProcessStarted", eventsDoc.RootElement[0].GetProperty("category").GetString());
    }

    [Fact]
    public void Settings_round_trip_and_fall_back_to_defaults_when_damaged()
    {
        using var dir = new TempDirectory();
        var paths = new AppPaths(dir.Path);
        var store = new SettingsStore(paths);
        Assert.Equal(1000, store.Current.SampleIntervalMs);

        AppSettings? notified = null;
        store.Changed += s => notified = s;
        Assert.True(store.Save(store.Current with { SampleIntervalMs = 2000, RetentionDays = 9999, PersistHistory = false }));
        Assert.Equal(365, notified!.RetentionDays);

        var reloaded = new SettingsStore(paths).Current;
        Assert.Equal(2000, reloaded.SampleIntervalMs);
        Assert.Equal(365, reloaded.RetentionDays);
        Assert.False(reloaded.PersistHistory);

        File.WriteAllText(paths.SettingsFile, "{ not json");
        Assert.Equal(new AppSettings(), new SettingsStore(paths).Current);
    }
}
