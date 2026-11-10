using System.Runtime.InteropServices;

namespace AgentManager.Core.Windows;

/// <summary>
/// Which processes hold a file open, through the Restart Manager. It only asks; it never touches the file, so an agent
/// appending to its transcript is never disturbed.
/// </summary>
public static class FileHolders
{
    private const int ERROR_MORE_DATA = 234;

    private const int CCH_RM_SESSION_KEY = 32;

    private const uint SPARE_HOLDERS = 16;

    private const int LIST_ATTEMPTS = 3;

    /// <summary>
    /// The processes holding the file, or null when the Restart Manager could not say: never an empty list, which means
    /// nobody holds it. The list has room to spare because any process opening the file between a sizing call and the
    /// real one fails the real one with <see cref="ERROR_MORE_DATA"/>, and something opening the file is routine: this
    /// manager reads each rollout every second.
    /// </summary>
    public static IReadOnlyList<int>? Pids(string path)
    {
        var key = new char[CCH_RM_SESSION_KEY + 1];
        if (RmStartSession(out var session, 0, key) != 0)
        {
            return null;
        }

        try
        {
            if (RmRegisterResources(session, 1, [path], 0, null, 0, null) != 0)
            {
                return null;
            }

            var capacity = SPARE_HOLDERS;
            for (var attempt = 0; attempt < LIST_ATTEMPTS; attempt++)
            {
                var processes = new RM_PROCESS_INFO[capacity];
                var count = capacity;
                var result = RmGetList(session, out var needed, ref count, processes, out _);
                if (result == 0)
                {
                    return processes.Take((int)count).Select(p => p.Process.dwProcessId).ToArray();
                }

                if (result != ERROR_MORE_DATA)
                {
                    return null;
                }

                capacity = needed + SPARE_HOLDERS;
            }

            return null;
        }
        finally
        {
            RmEndSession(session);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, char[] key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] fileNames, uint applications, RM_UNIQUE_PROCESS[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? processes, out uint rebootReasons);
}
