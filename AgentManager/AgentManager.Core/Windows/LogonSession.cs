using System.Runtime.InteropServices;

namespace AgentManager.Core.Windows;

/// <summary>
/// When the user's Windows session began. A restart, a shutdown with Fast Startup (which keeps the boot clock running)
/// and signing out all start a new one, so it tells "the agents died because the PC restarted" apart from "the manager
/// was reopened".
/// </summary>
public static class LogonSession
{
    private const int WTS_CURRENT_SESSION = -1;

    private const int WTS_SESSION_INFO = 24;

    /// <summary>WTSINFOW: 8 DWORD counters, the station, domain and user names (32, 17, 21 WCHARs), then 8-byte aligned times.</summary>
    private const int LOGON_TIME_OFFSET = 200;

    public static DateTimeOffset? LogonAt()
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, WTS_SESSION_INFO, out var buffer, out var bytes))
        {
            return null;
        }

        try
        {
            if (bytes < LOGON_TIME_OFFSET + sizeof(long))
            {
                return null;
            }

            var fileTime = Marshal.ReadInt64(buffer, LOGON_TIME_OFFSET);
            return fileTime > 0 ? DateTimeOffset.FromFileTime(fileTime) : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
