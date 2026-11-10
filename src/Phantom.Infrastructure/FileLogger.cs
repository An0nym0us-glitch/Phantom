using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Phantom.Infrastructure;

/// <summary>
/// Minimal structured file logger. Privacy-conscious by default: it records only what
/// Phantom itself does (levels at Information and above), writes to the local profile,
/// keeps a few small files, and never transmits anything.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxFiles = 5;

    private readonly string _directory;
    private readonly LogLevel _minimum;
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 2048);
    private readonly Thread _writer;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string directory, LogLevel minimum = LogLevel.Information)
    {
        _directory = directory;
        _minimum = minimum;
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Phantom.Log" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    private void Enqueue(string line)
    {
        // Logging must never block or fail the caller; drop the line if the queue is full.
        if (!_queue.IsAddingCompleted)
        {
            try
            {
                _queue.TryAdd(line);
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private void WriteLoop()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            Prune();
        }
        catch (Exception)
        {
            // If the log directory is unusable, drain the queue silently.
        }

        foreach (string line in _queue.GetConsumingEnumerable())
        {
            try
            {
                string path = Path.Combine(_directory, $"phantom-{DateTime.UtcNow:yyyyMMdd}.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxFileBytes)
                {
                    continue; // Daily cap reached; stop writing rather than grow without bound.
                }

                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception)
            {
                // Swallow: a logging failure must not affect monitoring.
            }
        }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(_directory).GetFiles("phantom-*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(MaxFiles);
        foreach (var file in files)
        {
            file.Delete();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _category = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimum && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder(160);
            sb.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
              .Append(" [").Append(Level(logLevel)).Append("] ")
              .Append(_category).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }

            provider.Enqueue(sb.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}
