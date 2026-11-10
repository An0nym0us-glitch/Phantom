using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Phantom.SystemMonitoring;

/// <summary>
/// Thin wrapper over the Performance Data Helper API. One query collects every registered
/// counter in a single call, including wildcard paths whose instances change over time,
/// which is far cheaper than one managed PerformanceCounter per instance.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PdhQuery : IDisposable
{
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const int ItemSize = 24; // PDH_FMT_COUNTERVALUE_ITEM_W on 64-bit
    private const uint CStatusValidData = 0;
    private const uint CStatusNewData = 1;

    private IntPtr _query;
    private IntPtr _buffer;
    private int _bufferSize;

    public PdhQuery()
    {
        uint status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != 0)
        {
            _query = IntPtr.Zero;
            throw new InvalidOperationException($"PdhOpenQuery failed (0x{status:X8}).");
        }
    }

    /// <summary>Adds a counter using its locale-independent English path. Returns null if it does not exist here.</summary>
    public PdhCounter? TryAdd(string englishPath, out string? failure)
    {
        uint status = PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out IntPtr counter);
        if (status != 0)
        {
            failure = status switch
            {
                0xC0000BB8 => "the performance object does not exist on this system",
                0xC0000BB9 => "the performance counter does not exist on this system",
                0x800007D5 or 0xC0000BBA => "no instances of the performance object are present",
                _ => $"the counter could not be opened (PDH 0x{status:X8})",
            };
            return null;
        }

        failure = null;
        return new PdhCounter(counter);
    }

    /// <summary>Takes a new sample of every counter. Returns false if collection failed.</summary>
    public bool Collect() => _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0;

    /// <summary>
    /// Reads all instances of a counter after <see cref="Collect"/>. Returns null when no valid data
    /// is available yet (rate counters need two collections) or the counter has no instances.
    /// </summary>
    public List<(string Instance, double Value)>? Read(PdhCounter counter)
    {
        int size = 0;
        uint status = PdhGetFormattedCounterArrayW(counter.Handle, PdhFmtDouble | PdhFmtNoCap100, ref size, out int count, IntPtr.Zero);
        if (status != PdhMoreData || size <= 0)
        {
            return null;
        }

        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
            }

            _bufferSize = size + 4096;
            _buffer = Marshal.AllocHGlobal(_bufferSize);
        }

        size = _bufferSize;
        status = PdhGetFormattedCounterArrayW(counter.Handle, PdhFmtDouble | PdhFmtNoCap100, ref size, out count, _buffer);
        if (status != 0)
        {
            return null;
        }

        var result = new List<(string, double)>(count);
        for (int i = 0; i < count; i++)
        {
            IntPtr item = _buffer + (i * ItemSize);
            uint cstatus = (uint)Marshal.ReadInt32(item, 8);
            if (cstatus is not (CStatusValidData or CStatusNewData))
            {
                continue;
            }

            double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
            if (!double.IsFinite(value))
            {
                continue;
            }

            string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
            result.Add((name, value));
        }

        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }

        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

internal sealed record PdhCounter(IntPtr Handle);
