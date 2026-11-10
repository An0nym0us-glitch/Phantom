using System.Globalization;
using System.Text;
using System.Text.Json;
using Phantom.Core.Abstractions;
using Phantom.Core.Models;

namespace Phantom.Storage;

public enum ExportFormat
{
    Csv,
    Json,
}

/// <summary>Writes history to user-chosen files. Nothing is ever sent anywhere else.</summary>
public static class HistoryExporter
{
    private const int PageSize = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Exports activity events matching the query. Returns the number of events written.</summary>
    public static long ExportEvents(IHistoryStore store, ActivityQuery query, Stream destination, ExportFormat format)
    {
        var events = ReadAllEvents(store, query);
        using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        if (format == ExportFormat.Json)
        {
            writer.Write(JsonSerializer.Serialize(events.Select(e => new
            {
                timestampUtc = e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                category = e.Category.ToString(),
                provenance = e.Provenance.ToString(),
                pid = e.Pid,
                processName = e.ProcessName,
                description = e.Description,
            }), JsonOptions));
        }
        else
        {
            writer.WriteLine("TimestampUtc,Category,Provenance,Pid,ProcessName,Description");
            foreach (var e in events)
            {
                writer.WriteLine(string.Join(',',
                    e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                    e.Category,
                    e.Provenance,
                    e.Pid?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Csv(e.ProcessName),
                    Csv(e.Description)));
            }
        }

        writer.Flush();
        return events.Count;
    }

    /// <summary>Exports metric samples in the range. Returns the number of samples written.</summary>
    public static long ExportMetrics(IHistoryStore store, DateTime fromUtc, DateTime toUtc, Stream destination, ExportFormat format)
    {
        var samples = store.QueryMetrics(fromUtc, toUtc, int.MaxValue);
        using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        if (format == ExportFormat.Json)
        {
            writer.Write(JsonSerializer.Serialize(samples.Select(s => new
            {
                timestampUtc = s.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                cpuPercent = s.CpuPercent,
                memoryTotalBytes = s.MemoryTotalBytes,
                memoryAvailableBytes = s.MemoryAvailableBytes,
                gpuPercent = s.GpuPercent,
                gpuDedicatedUsedBytes = s.GpuDedicatedUsedBytes,
                gpuSharedUsedBytes = s.GpuSharedUsedBytes,
                diskReadBytesPerSec = s.DiskReadBytesPerSec,
                diskWriteBytesPerSec = s.DiskWriteBytesPerSec,
                diskActivePercent = s.DiskActivePercent,
                networkUploadBytesPerSec = s.NetworkUploadBytesPerSec,
                networkDownloadBytesPerSec = s.NetworkDownloadBytesPerSec,
                processCount = s.ProcessCount,
            }), JsonOptions));
        }
        else
        {
            writer.WriteLine("TimestampUtc,CpuPercent,MemoryTotalBytes,MemoryAvailableBytes,GpuPercent,GpuDedicatedUsedBytes,GpuSharedUsedBytes,DiskReadBytesPerSec,DiskWriteBytesPerSec,DiskActivePercent,NetworkUploadBytesPerSec,NetworkDownloadBytesPerSec,ProcessCount");
            foreach (var s in samples)
            {
                writer.WriteLine(string.Join(',',
                    s.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                    Num(s.CpuPercent), Num(s.MemoryTotalBytes), Num(s.MemoryAvailableBytes),
                    Num(s.GpuPercent), Num(s.GpuDedicatedUsedBytes), Num(s.GpuSharedUsedBytes),
                    Num(s.DiskReadBytesPerSec), Num(s.DiskWriteBytesPerSec), Num(s.DiskActivePercent),
                    Num(s.NetworkUploadBytesPerSec), Num(s.NetworkDownloadBytesPerSec), Num(s.ProcessCount)));
            }
        }

        writer.Flush();
        return samples.Count;
    }

    private static List<ActivityEvent> ReadAllEvents(IHistoryStore store, ActivityQuery query)
    {
        var all = new List<ActivityEvent>();
        for (int offset = 0; ; offset += PageSize)
        {
            var page = store.QueryEvents(query with { Limit = PageSize, Offset = offset });
            all.AddRange(page);
            if (page.Count < PageSize)
            {
                break;
            }
        }

        all.Reverse(); // Oldest first reads naturally in a spreadsheet.
        return all;
    }

    // An absent measurement is exported as an empty cell, never as 0.
    private static string Num(double? v) => v?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
    private static string Num(long? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Num(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    public static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        // Process names are attacker-influenced; neutralise spreadsheet formula prefixes.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
