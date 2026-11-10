using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Core.Abstractions;
using Phantom.Core.Models;

namespace Phantom.Storage;

/// <summary>
/// SQLite-backed history. The store degrades instead of failing: if the database cannot be
/// opened it is quarantined and recreated, and if that is impossible too the store reports
/// itself unavailable, ignores writes and returns empty results so the application keeps working.
/// </summary>
public sealed class SqliteHistoryStore : IHistoryStore
{
    private const int SchemaVersion = 1;
    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private SqliteConnection? _connection;

    public SqliteHistoryStore(string databasePath, ILogger<SqliteHistoryStore>? logger = null)
    {
        DatabasePath = databasePath;
        _logger = logger ?? NullLogger<SqliteHistoryStore>.Instance;
        Initialize();
    }

    public bool IsAvailable => _connection is not null;
    public string? StatusMessage { get; private set; }
    public string? DatabasePath { get; }

    private void Initialize()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath!))!);
            _connection = Open();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            _logger.LogWarning("History database is corrupted (SQLite error {Code}); quarantining it", ex.SqliteErrorCode);
            TryRecoverFromCorruption();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "History database could not be opened");
            StatusMessage = $"History is unavailable: the database could not be opened ({ex.GetType().Name}). Live monitoring is unaffected.";
        }
    }

    private void TryRecoverFromCorruption()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            string quarantine = $"{DatabasePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(DatabasePath!, quarantine, overwrite: true);
            foreach (string suffix in new[] { "-wal", "-shm" })
            {
                if (File.Exists(DatabasePath + suffix))
                {
                    File.Delete(DatabasePath + suffix);
                }
            }

            _connection = Open();
            StatusMessage = $"The previous history database was corrupted and has been set aside as '{Path.GetFileName(quarantine)}'. A new database was created.";
        }
        catch (Exception ex)
        {
            _connection = null;
            _logger.LogError(ex, "History database recovery failed");
            StatusMessage = "History is unavailable: the database is corrupted and could not be replaced. Live monitoring is unaffected.";
        }
    }

    private SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
            // Touch the schema so a damaged file fails here rather than later.
            long version = (long)(Scalar(connection, "PRAGMA user_version;") ?? 0L);
            Scalar(connection, "SELECT COUNT(*) FROM sqlite_master;");
            if (version < SchemaVersion)
            {
                Execute(connection, """
                    CREATE TABLE IF NOT EXISTS sessions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        started_utc INTEGER NOT NULL,
                        ended_utc INTEGER NULL,
                        app_version TEXT NOT NULL DEFAULT '');
                    CREATE TABLE IF NOT EXISTS metric_samples (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id INTEGER NOT NULL,
                        ts INTEGER NOT NULL,
                        cpu REAL NULL,
                        mem_total INTEGER NULL,
                        mem_available INTEGER NULL,
                        gpu REAL NULL,
                        gpu_dedicated INTEGER NULL,
                        gpu_shared INTEGER NULL,
                        disk_read REAL NULL,
                        disk_write REAL NULL,
                        disk_active REAL NULL,
                        net_up REAL NULL,
                        net_down REAL NULL,
                        process_count INTEGER NULL);
                    CREATE INDEX IF NOT EXISTS ix_metric_samples_ts ON metric_samples(ts);
                    CREATE TABLE IF NOT EXISTS activity_events (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        session_id INTEGER NOT NULL,
                        ts INTEGER NOT NULL,
                        category INTEGER NOT NULL,
                        provenance INTEGER NOT NULL,
                        pid INTEGER NULL,
                        process_name TEXT NULL,
                        description TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS ix_activity_events_ts ON activity_events(ts);
                    PRAGMA user_version = 1;
                    """);
            }

            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>Runs a database operation under the lock, converting failures into a degraded result.</summary>
    private T Run<T>(Func<SqliteConnection, T> action, T fallback, string operation)
    {
        lock (_gate)
        {
            if (_connection is null)
            {
                return fallback;
            }

            try
            {
                return action(_connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
            {
                _logger.LogError("History database became corrupted during {Operation}; disabling history", operation);
                _connection.Dispose();
                _connection = null;
                StatusMessage = "History is unavailable: the database became corrupted. Restart Phantom to create a fresh one. Live monitoring is unaffected.";
                return fallback;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "History operation {Operation} failed", operation);
                return fallback;
            }
        }
    }

    public long BeginSession(DateTime startedUtc, string appVersion) => Run(c =>
    {
        // Sessions left open by a crash are closed at their last recorded sample.
        using (var fix = c.CreateCommand())
        {
            fix.CommandText = """
                UPDATE sessions SET ended_utc = COALESCE(
                    (SELECT MAX(ts) FROM metric_samples WHERE session_id = sessions.id), started_utc)
                WHERE ended_utc IS NULL;
                """;
            fix.ExecuteNonQuery();
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions(started_utc, app_version) VALUES ($s, $v); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$s", startedUtc.Ticks);
        cmd.Parameters.AddWithValue("$v", appVersion);
        return (long)cmd.ExecuteScalar()!;
    }, 0L, nameof(BeginSession));

    public void EndSession(long sessionId, DateTime endedUtc) => Run(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET ended_utc = $e WHERE id = $id;";
        cmd.Parameters.AddWithValue("$e", endedUtc.Ticks);
        cmd.Parameters.AddWithValue("$id", sessionId);
        return cmd.ExecuteNonQuery();
    }, 0, nameof(EndSession));

    private static object Db(double? v) => v is { } x && double.IsFinite(x) ? x : DBNull.Value;
    private static object Db(long? v) => v is { } x ? x : DBNull.Value;
    private static object Db(int? v) => v is { } x ? x : DBNull.Value;
    private static object Db(string? v) => v is not null ? v : DBNull.Value;

    public void AppendMetrics(long sessionId, IReadOnlyList<SystemMetricsSample> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        Run(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO metric_samples(session_id, ts, cpu, mem_total, mem_available, gpu, gpu_dedicated, gpu_shared,
                    disk_read, disk_write, disk_active, net_up, net_down, process_count)
                VALUES ($sid, $ts, $cpu, $mt, $ma, $gpu, $gd, $gs, $dr, $dw, $da, $nu, $nd, $pc);
                """;
            foreach (var s in samples)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$sid", sessionId);
                cmd.Parameters.AddWithValue("$ts", s.TimestampUtc.Ticks);
                cmd.Parameters.AddWithValue("$cpu", Db(s.CpuPercent));
                cmd.Parameters.AddWithValue("$mt", Db(s.MemoryTotalBytes));
                cmd.Parameters.AddWithValue("$ma", Db(s.MemoryAvailableBytes));
                cmd.Parameters.AddWithValue("$gpu", Db(s.GpuPercent));
                cmd.Parameters.AddWithValue("$gd", Db(s.GpuDedicatedUsedBytes));
                cmd.Parameters.AddWithValue("$gs", Db(s.GpuSharedUsedBytes));
                cmd.Parameters.AddWithValue("$dr", Db(s.DiskReadBytesPerSec));
                cmd.Parameters.AddWithValue("$dw", Db(s.DiskWriteBytesPerSec));
                cmd.Parameters.AddWithValue("$da", Db(s.DiskActivePercent));
                cmd.Parameters.AddWithValue("$nu", Db(s.NetworkUploadBytesPerSec));
                cmd.Parameters.AddWithValue("$nd", Db(s.NetworkDownloadBytesPerSec));
                cmd.Parameters.AddWithValue("$pc", Db(s.ProcessCount));
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return 0;
        }, 0, nameof(AppendMetrics));
    }

    public void AppendEvents(long sessionId, IReadOnlyList<ActivityEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        Run(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO activity_events(session_id, ts, category, provenance, pid, process_name, description)
                VALUES ($sid, $ts, $cat, $prov, $pid, $name, $desc);
                """;
            foreach (var e in events)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$sid", sessionId);
                cmd.Parameters.AddWithValue("$ts", e.TimestampUtc.Ticks);
                cmd.Parameters.AddWithValue("$cat", (int)e.Category);
                cmd.Parameters.AddWithValue("$prov", (int)e.Provenance);
                cmd.Parameters.AddWithValue("$pid", Db(e.Pid));
                cmd.Parameters.AddWithValue("$name", Db(e.ProcessName));
                cmd.Parameters.AddWithValue("$desc", e.Description);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return 0;
        }, 0, nameof(AppendEvents));
    }

    private static void ApplyFilter(SqliteCommand cmd, StringBuilder sql, ActivityQuery q)
    {
        sql.Append(" WHERE 1=1");
        if (q.FromUtc is { } from)
        {
            sql.Append(" AND ts >= $from");
            cmd.Parameters.AddWithValue("$from", from.Ticks);
        }

        if (q.ToUtc is { } to)
        {
            sql.Append(" AND ts <= $to");
            cmd.Parameters.AddWithValue("$to", to.Ticks);
        }

        if (q.Categories is { } cats)
        {
            if (cats.Count == 0)
            {
                sql.Append(" AND 0");
            }
            else
            {
                // Enum values are integers, so inlining them cannot inject SQL.
                sql.Append(" AND category IN (").AppendJoin(',', cats.Select(x => (int)x)).Append(')');
            }
        }

        if (!string.IsNullOrWhiteSpace(q.SearchText))
        {
            string escaped = q.SearchText.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            sql.Append(" AND (description LIKE $text ESCAPE '\\' OR process_name LIKE $text ESCAPE '\\' OR CAST(pid AS TEXT) = $exact)");
            cmd.Parameters.AddWithValue("$text", $"%{escaped}%");
            cmd.Parameters.AddWithValue("$exact", q.SearchText.Trim());
        }
    }

    public IReadOnlyList<ActivityEvent> QueryEvents(ActivityQuery query) => Run<IReadOnlyList<ActivityEvent>>(c =>
    {
        using var cmd = c.CreateCommand();
        var sql = new StringBuilder("SELECT id, ts, category, provenance, pid, process_name, description FROM activity_events");
        ApplyFilter(cmd, sql, query);
        sql.Append(" ORDER BY ts DESC, id DESC LIMIT $limit OFFSET $offset;");
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100_000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        cmd.CommandText = sql.ToString();

        var result = new List<ActivityEvent>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ActivityEvent
            {
                Id = reader.GetInt64(0),
                TimestampUtc = new DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                Category = (ActivityCategory)reader.GetInt32(2),
                Provenance = (EventProvenance)reader.GetInt32(3),
                Pid = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                ProcessName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Description = reader.GetString(6),
            });
        }

        return result;
    }, [], nameof(QueryEvents));

    public long CountEvents(ActivityQuery query) => Run(c =>
    {
        using var cmd = c.CreateCommand();
        var sql = new StringBuilder("SELECT COUNT(*) FROM activity_events");
        ApplyFilter(cmd, sql, query);
        cmd.CommandText = sql.ToString();
        return (long)cmd.ExecuteScalar()!;
    }, 0L, nameof(CountEvents));

    public IReadOnlyList<SystemMetricsSample> QueryMetrics(DateTime fromUtc, DateTime toUtc, int maxPoints)
        => Run<IReadOnlyList<SystemMetricsSample>>(c =>
        {
            if (toUtc <= fromUtc)
            {
                return [];
            }

            long bucket = Math.Max(1, (toUtc.Ticks - fromUtc.Ticks) / Math.Max(1, maxPoints));
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT MAX(ts), AVG(cpu), MAX(mem_total), AVG(mem_available), AVG(gpu), AVG(gpu_dedicated), AVG(gpu_shared),
                       AVG(disk_read), AVG(disk_write), AVG(disk_active), AVG(net_up), AVG(net_down), AVG(process_count)
                FROM metric_samples
                WHERE ts >= $from AND ts <= $to
                GROUP BY (ts - $from) / $bucket
                ORDER BY 1;
                """;
            cmd.Parameters.AddWithValue("$from", fromUtc.Ticks);
            cmd.Parameters.AddWithValue("$to", toUtc.Ticks);
            cmd.Parameters.AddWithValue("$bucket", bucket);

            var result = new List<SystemMetricsSample>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                double? D(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                long? L(int i) => r.IsDBNull(i) ? null : (long)Math.Round(r.GetDouble(i));
                result.Add(new SystemMetricsSample
                {
                    TimestampUtc = new DateTime(r.GetInt64(0), DateTimeKind.Utc),
                    CpuPercent = D(1),
                    MemoryTotalBytes = L(2),
                    MemoryAvailableBytes = L(3),
                    GpuPercent = D(4),
                    GpuDedicatedUsedBytes = L(5),
                    GpuSharedUsedBytes = L(6),
                    DiskReadBytesPerSec = D(7),
                    DiskWriteBytesPerSec = D(8),
                    DiskActivePercent = D(9),
                    NetworkUploadBytesPerSec = D(10),
                    NetworkDownloadBytesPerSec = D(11),
                    ProcessCount = L(12) is { } pc ? (int)pc : null,
                });
            }

            return result;
        }, [], nameof(QueryMetrics));

    public IReadOnlyList<MonitoringSession> GetSessions(int limit) => Run<IReadOnlyList<MonitoringSession>>(c =>
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, started_utc, ended_utc, app_version FROM sessions ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        var result = new List<MonitoringSession>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new MonitoringSession
            {
                Id = r.GetInt64(0),
                StartedUtc = new DateTime(r.GetInt64(1), DateTimeKind.Utc),
                EndedUtc = r.IsDBNull(2) ? null : new DateTime(r.GetInt64(2), DateTimeKind.Utc),
                AppVersion = r.GetString(3),
            });
        }

        return result;
    }, [], nameof(GetSessions));

    public HistoryStats GetStats() => Run(c =>
    {
        long Count(string table) => (long)Scalar(c, $"SELECT COUNT(*) FROM {table};")!;
        object? oldest = Scalar(c, """
            SELECT MIN(t) FROM (SELECT MIN(ts) AS t FROM metric_samples
                                UNION ALL SELECT MIN(ts) FROM activity_events);
            """);
        long size = 0;
        try
        {
            foreach (string suffix in new[] { "", "-wal" })
            {
                var file = new FileInfo(DatabasePath + suffix);
                if (file.Exists)
                {
                    size += file.Length;
                }
            }
        }
        catch (IOException)
        {
        }

        return new HistoryStats
        {
            MetricSampleCount = Count("metric_samples"),
            EventCount = Count("activity_events"),
            SessionCount = Count("sessions"),
            DatabaseSizeBytes = size,
            OldestRecordUtc = oldest is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : null,
        };
    }, new HistoryStats(), nameof(GetStats));

    public long ApplyRetention(DateTime cutoffUtc) => Run(c =>
    {
        using var tx = c.BeginTransaction();
        long removed = 0;
        foreach (string sql in new[]
        {
            "DELETE FROM metric_samples WHERE ts < $cutoff;",
            "DELETE FROM activity_events WHERE ts < $cutoff;",
            // Only ended sessions with no remaining data are removed; the live session is always kept.
            """
            DELETE FROM sessions WHERE ended_utc IS NOT NULL AND ended_utc < $cutoff
                AND NOT EXISTS (SELECT 1 FROM metric_samples m WHERE m.session_id = sessions.id)
                AND NOT EXISTS (SELECT 1 FROM activity_events e WHERE e.session_id = sessions.id);
            """,
        })
        {
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$cutoff", cutoffUtc.Ticks);
            removed += cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return removed;
    }, 0L, nameof(ApplyRetention));

    public void ClearAll() => Run(c =>
    {
        Execute(c, """
            BEGIN;
            DELETE FROM metric_samples;
            DELETE FROM activity_events;
            DELETE FROM sessions WHERE ended_utc IS NOT NULL;
            COMMIT;
            """);
        Execute(c, "VACUUM;");
        return 0;
    }, 0, nameof(ClearAll));

    public void Compact() => Run(c =>
    {
        Execute(c, "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
        return 0;
    }, 0, nameof(Compact));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_connection is not null)
            {
                try
                {
                    Execute(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                }
                catch (SqliteException)
                {
                }

                _connection.Dispose();
                _connection = null;
            }
        }
    }
}
