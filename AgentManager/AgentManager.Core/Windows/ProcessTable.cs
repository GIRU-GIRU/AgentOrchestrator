using System.Runtime.InteropServices;

namespace AgentManager.Core.Windows;

public sealed record ProcessEntry(int Pid, int ParentPid, string Exe);

/// <summary>A snapshot of the machine's processes, taken once per poll.</summary>
public sealed class ProcessTable
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private const int PROCESS_BASIC_INFORMATION_CLASS = 0;

    private const int PROCESS_COMMAND_LINE_INFORMATION = 60;

    private const int SYSTEM_PROCESS_ID_INFORMATION_CLASS = 88;

    private const int FIRST_PID_CAPACITY = 1024;

    private const int IMAGE_NAME_CHARS = 1024;

    private const int COMMAND_LINE_BUFFER = 32768;

    private readonly Dictionary<int, ProcessEntry> byPid;

    private readonly Dictionary<int, long> startTimes;

    private ProcessTable(Dictionary<int, ProcessEntry> byPid, Dictionary<int, long> startTimes)
    {
        this.byPid = byPid;
        this.startTimes = startTimes;
    }

    public static ProcessTable Empty { get; } = new([], []);

    public IEnumerable<ProcessEntry> All => byPid.Values;

    /// <summary>
    /// Read one process at a time: the kernel's list of every process (<c>CreateToolhelp32Snapshot</c>) walks every thread too,
    /// and took 0.3 to 0.6 s of CPU a poll on a machine running 11,000 threads. A process this user cannot open is still
    /// listed, named through its pid, with no parent.
    /// </summary>
    public static ProcessTable Capture()
    {
        var pids = RunningPids();
        var map = new Dictionary<int, ProcessEntry>(pids.Length);
        var starts = new Dictionary<int, long>(pids.Length);
        var name = new char[IMAGE_NAME_CHARS];
        var nameById = Marshal.AllocHGlobal(IMAGE_NAME_CHARS * sizeof(char));
        try
        {
            foreach (var pid in pids)
            {
                string? exe = null;
                var parent = 0;
                long start = 0;
                var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (handle != IntPtr.Zero)
                {
                    var length = name.Length;
                    if (QueryFullProcessImageNameW(handle, 0, name, ref length))
                    {
                        exe = Path.GetFileName(new string(name, 0, length));
                    }

                    if (NtQueryInformationProcess(handle, PROCESS_BASIC_INFORMATION_CLASS, out var basic, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0)
                    {
                        parent = (int)basic.InheritedFromUniqueProcessId;
                    }

                    if (GetProcessTimes(handle, out var created, out _, out _, out _))
                    {
                        start = created;
                    }

                    CloseHandle(handle);
                }

                map[(int)pid] = new ProcessEntry((int)pid, parent, exe ?? ImageNameOf(pid, nameById) ?? "");
                starts[(int)pid] = start;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(nameById);
        }

        return new ProcessTable(map, starts);
    }

    private static uint[] RunningPids()
    {
        var pids = new uint[FIRST_PID_CAPACITY];
        while (true)
        {
            if (!EnumProcesses(pids, pids.Length * sizeof(uint), out var bytes))
            {
                return [];
            }

            if (bytes < pids.Length * sizeof(uint))
            {
                return pids[..(bytes / sizeof(uint))];
            }

            pids = new uint[pids.Length * 2];
        }
    }

    private static string? ImageNameOf(uint pid, IntPtr buffer)
    {
        var query = new SYSTEM_PROCESS_ID_INFORMATION
        {
            ProcessId = (IntPtr)pid,
            ImageName = new UNICODE_STRING { Length = 0, MaximumLength = IMAGE_NAME_CHARS * sizeof(char), Buffer = buffer },
        };
        if (NtQuerySystemInformation(SYSTEM_PROCESS_ID_INFORMATION_CLASS, ref query, Marshal.SizeOf<SYSTEM_PROCESS_ID_INFORMATION>(), out _) != 0 || query.ImageName.Length == 0)
        {
            return null;
        }

        return Path.GetFileName(Marshal.PtrToStringUni(buffer, query.ImageName.Length / sizeof(char)));
    }

    public bool Exists(int pid)
    {
        return byPid.ContainsKey(pid);
    }

    public ProcessEntry? Get(int pid)
    {
        return byPid.GetValueOrDefault(pid);
    }

    public IEnumerable<ProcessEntry> Named(string exe)
    {
        return byPid.Values.Where(p => string.Equals(p.Exe, exe, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<ProcessEntry> Ancestors(int pid, int maxDepth = 10)
    {
        var current = Get(pid);
        var seen = new HashSet<int>();
        for (var depth = 0; current is not null && depth < maxDepth && seen.Add(current.Pid); depth++)
        {
            var parent = Get(current.ParentPid);
            if (parent is null || StartTime(parent.Pid) > StartTime(current.Pid))
            {
                yield break;
            }

            yield return parent;
            current = parent;
        }
    }

    /// <summary>Creation time as a FILETIME (100 ns ticks since 1601), or 0 when the process cannot be opened.</summary>
    public long StartTime(int pid)
    {
        if (startTimes.TryGetValue(pid, out var cached))
        {
            return cached;
        }

        long time = 0;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle != IntPtr.Zero)
        {
            if (GetProcessTimes(handle, out var created, out _, out _, out _))
            {
                time = created;
            }

            CloseHandle(handle);
        }

        startTimes[pid] = time;
        return time;
    }

    public bool IsAlive(int pid, long expectedStart)
    {
        if (!Exists(pid))
        {
            return false;
        }

        if (expectedStart <= 0)
        {
            return true;
        }

        var start = StartTime(pid);
        return start == 0 || Math.Abs(start - expectedStart) < TimeSpan.TicksPerSecond;
    }

    public DateTimeOffset? StartedAt(int pid)
    {
        var start = StartTime(pid);
        return start > 0 ? DateTimeOffset.FromFileTime(start) : null;
    }

    /// <summary>The process's command line, or null when it cannot be read.</summary>
    public string? CommandLine(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(COMMAND_LINE_BUFFER);
        try
        {
            if (NtQueryInformationProcess(handle, PROCESS_COMMAND_LINE_INFORMATION, buffer, COMMAND_LINE_BUFFER, out _) != 0)
            {
                return null;
            }

            var text = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
            return text.Buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(text.Buffer, text.Length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESS_ID_INFORMATION
    {
        public IntPtr ProcessId;
        public UNICODE_STRING ImageName;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcesses([Out] uint[] processIds, int size, out int bytesReturned);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, out PROCESS_BASIC_INFORMATION information, int length, out int returned);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, ref SYSTEM_PROCESS_ID_INFORMATION information, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
