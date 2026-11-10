using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Phantom.Core.Abstractions;
using Phantom.Core.Models;

namespace Phantom.ProcessMonitoring;

/// <summary>
/// Read-only process discovery for Windows.
/// <para>
/// The whole process table (names, parent PIDs, CPU times, memory, threads, handles) comes from
/// a single <c>NtQuerySystemInformation</c> call, which needs no special privileges and does not
/// open any process. Per-process details use <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, the weakest
/// access right Windows offers; when even that is refused the result says so instead of guessing.
/// Nothing here writes to, suspends, or injects into another process.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessDataSource : IProcessDataSource, IDisposable
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;
    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;

    private readonly object _gate = new();
    private readonly SecurityIdentifier? _currentUser;
    private readonly ProcessArchitecture _nativeArchitecture;
    private IntPtr _buffer;
    private int _bufferSize = 512 * 1024;
    private HashSet<int>? _windowPids;
    private readonly EnumWindowsProc _enumCallback;

    public WindowsProcessDataSource()
    {
        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException("Phantom requires a 64-bit process to read the Windows process table.");
        }

        _enumCallback = OnEnumWindow;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            _currentUser = identity.User;
        }
        catch (Exception)
        {
            _currentUser = null;
        }

        _nativeArchitecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => ProcessArchitecture.X64,
            Architecture.Arm64 => ProcessArchitecture.Arm64,
            Architecture.X86 => ProcessArchitecture.X86,
            Architecture.Arm => ProcessArchitecture.Arm,
            _ => ProcessArchitecture.Unknown,
        };
    }

    public IReadOnlyList<RawProcessSample> EnumerateProcesses()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_bufferSize < 0, this);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                if (_buffer == IntPtr.Zero)
                {
                    _buffer = Marshal.AllocHGlobal(_bufferSize);
                }

                uint status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out int needed);
                if (status == 0)
                {
                    return Parse(_buffer, _bufferSize);
                }

                if (status != StatusInfoLengthMismatch)
                {
                    throw new Win32Exception($"NtQuerySystemInformation failed with status 0x{status:X8}.");
                }

                // The table can grow between calls; leave headroom.
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = Math.Max(needed, _bufferSize) + (128 * 1024);
            }

            throw new InvalidOperationException("The process table kept growing while it was being read.");
        }
    }

    // Field offsets of SYSTEM_PROCESS_INFORMATION on 64-bit Windows.
    private static List<RawProcessSample> Parse(IntPtr buffer, int size)
    {
        var result = new List<RawProcessSample>(512);
        int offset = 0;
        while (true)
        {
            if (offset < 0 || offset + 0xD0 > size)
            {
                break; // Truncated or malformed entry; keep what was read so far.
            }

            IntPtr entry = buffer + offset;
            int next = Marshal.ReadInt32(entry, 0x00);
            int threads = Marshal.ReadInt32(entry, 0x04);
            long createTime = Marshal.ReadInt64(entry, 0x20);
            long userTime = Marshal.ReadInt64(entry, 0x28);
            long kernelTime = Marshal.ReadInt64(entry, 0x30);
            ushort nameLength = (ushort)Marshal.ReadInt16(entry, 0x38);
            IntPtr namePtr = Marshal.ReadIntPtr(entry, 0x40);
            int pid = (int)Marshal.ReadIntPtr(entry, 0x50);
            int parentPid = (int)Marshal.ReadIntPtr(entry, 0x58);
            int handles = Marshal.ReadInt32(entry, 0x60);
            int session = Marshal.ReadInt32(entry, 0x64);
            long workingSet = Marshal.ReadInt64(entry, 0x90);
            long privateBytes = Marshal.ReadInt64(entry, 0xC8);

            string name = namePtr != IntPtr.Zero && nameLength > 0
                ? Marshal.PtrToStringUni(namePtr, nameLength / 2)
                : pid == 0 ? "System Idle Process" : pid == 4 ? "System" : $"PID {pid}";

            DateTime? start = null;
            if (createTime > 0)
            {
                try
                {
                    start = DateTime.FromFileTimeUtc(createTime);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }

            result.Add(new RawProcessSample
            {
                Pid = pid,
                ParentPid = parentPid,
                Name = name,
                StartTimeUtc = start,
                TotalProcessorTime = new TimeSpan(Math.Max(0, userTime) + Math.Max(0, kernelTime)),
                WorkingSetBytes = workingSet,
                PrivateBytes = privateBytes,
                ThreadCount = threads,
                HandleCount = pid == 0 ? null : handles,
                SessionId = session,
            });

            if (next == 0)
            {
                break;
            }

            offset += next;
        }

        return result;
    }

    public ProcessDetails QueryDetails(int pid, int? sessionId)
    {
        if (pid is 0 or 4)
        {
            // Kernel pseudo-processes have no image file on disk.
            return new ProcessDetails
            {
                PathAvailability = Availability.NotApplicable,
                Architecture = _nativeArchitecture,
                Owner = ProcessOwnerKind.System,
            };
        }

        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            int error = Marshal.GetLastPInvokeError();
            var reason = error switch
            {
                ErrorAccessDenied => Availability.AccessDenied,
                ErrorInvalidParameter => Availability.ProcessExited,
                _ => Availability.Error,
            };
            return new ProcessDetails
            {
                PathAvailability = reason,
                ArchitectureAvailability = reason,
                // A process the current user cannot even query is, by definition, not theirs.
                Owner = reason == Availability.AccessDenied ? ProcessOwnerKind.System : ProcessOwnerKind.Unknown,
            };
        }

        try
        {
            string? path = null;
            var pathAvailability = Availability.Available;
            var sb = new StringBuilder(1024);
            int length = sb.Capacity;
            if (QueryFullProcessImageNameW(handle, 0, sb, ref length))
            {
                path = sb.ToString(0, length);
            }
            else
            {
                pathAvailability = Marshal.GetLastPInvokeError() == ErrorAccessDenied
                    ? Availability.AccessDenied
                    : Availability.Error;
            }

            var architecture = ProcessArchitecture.Unknown;
            var architectureAvailability = Availability.Available;
            try
            {
                if (IsWow64Process2(handle, out ushort processMachine, out ushort nativeMachine))
                {
                    // IMAGE_FILE_MACHINE_UNKNOWN means "not running under WOW64", i.e. native.
                    architecture = MapMachine(processMachine == 0 ? nativeMachine : processMachine);
                }
                else
                {
                    architectureAvailability = Availability.Error;
                }
            }
            catch (EntryPointNotFoundException)
            {
                architectureAvailability = Availability.Unsupported;
            }

            return new ProcessDetails
            {
                ExecutablePath = path,
                PathAvailability = pathAvailability,
                Architecture = architecture,
                ArchitectureAvailability = architecture == ProcessArchitecture.Unknown && architectureAvailability == Availability.Available
                    ? Availability.Error
                    : architectureAvailability,
                Owner = QueryOwner(handle),
            };
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static ProcessArchitecture MapMachine(ushort machine) => machine switch
    {
        0x014c => ProcessArchitecture.X86,
        0x8664 => ProcessArchitecture.X64,
        0xAA64 => ProcessArchitecture.Arm64,
        0x01c0 or 0x01c4 => ProcessArchitecture.Arm,
        _ => ProcessArchitecture.Unknown,
    };

    private ProcessOwnerKind QueryOwner(IntPtr process)
    {
        if (_currentUser is null)
        {
            return ProcessOwnerKind.Unknown;
        }

        if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? ProcessOwnerKind.System : ProcessOwnerKind.Unknown;
        }

        IntPtr info = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int size);
            if (size <= 0)
            {
                return ProcessOwnerKind.Unknown;
            }

            info = Marshal.AllocHGlobal(size);
            if (!GetTokenInformation(token, TokenUser, info, size, out _))
            {
                return ProcessOwnerKind.Unknown;
            }

            // TOKEN_USER starts with a pointer to the SID.
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(info));
            return sid.Equals(_currentUser) ? ProcessOwnerKind.CurrentUser : ProcessOwnerKind.System;
        }
        catch (ArgumentException)
        {
            return ProcessOwnerKind.Unknown;
        }
        finally
        {
            if (info != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(info);
            }

            CloseHandle(token);
        }
    }

    public IReadOnlySet<int> GetPidsWithVisibleWindows()
    {
        lock (_gate)
        {
            _windowPids = [];
            EnumWindows(_enumCallback, IntPtr.Zero);
            var result = _windowPids;
            _windowPids = null;
            return result;
        }
    }

    private bool OnEnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) == 0 || GetWindow(hwnd, GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            // Suspended store apps keep "visible" windows that are cloaked by the compositor.
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            {
                return true;
            }

            if (GetWindowThreadProcessId(hwnd, out uint pid) != 0)
            {
                _windowPids?.Add((int)pid);
            }
        }
        catch (Exception)
        {
            // Never let an exception cross the native callback boundary.
        }

        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }

            _bufferSize = -1;
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
