using System.Runtime.InteropServices;
using System.Text;

namespace AgentManager.Core.Windows;

public sealed record RaiseResult(bool IsRaised, string How);

/// <summary>
/// Brings an agent's own window to the front: its console window, or else the first visible top-level window of the agent
/// process or one of its ancestors (Windows Terminal, VS Code, the Claude or Codex app). A console handed to Windows Terminal
/// (the default terminal, so anything started from Explorer) is a <c>PseudoConsoleWindow</c> owned by that agent's Windows
/// Terminal window: its root owner is the window to raise. Windows lets the foreground
/// process hand the foreground on, and AgentManager is that process while the user is clicking in it.
/// </summary>
public static class WindowRaiser
{
    private const int SW_RESTORE = 9;

    private const int SW_SHOW = 5;

    private const int GWL_EXSTYLE = -20;

    private const uint GA_ROOTOWNER = 3;

    private const uint WM_CLOSE = 0x0010;

    private const long WS_EX_TOOLWINDOW = 0x80;

    /// <summary>Where the walk up the process tree stops: the desktop shell and system hosts own windows that are never the agent's.</summary>
    private static readonly HashSet<string> SHELL = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "sihost.exe", "svchost.exe", "services.exe", "wininit.exe", "winlogon.exe", "csrss.exe", "userinit.exe", "RuntimeBroker.exe",
    };

    public static RaiseResult Raise(int pid, ProcessTable processes)
    {
        var (window, owner, tried) = Find(pid, processes);
        if (window == IntPtr.Zero)
        {
            return new RaiseResult(false, "no visible window on " + tried);
        }

        ShowWindow(window, IsIconic(window) ? SW_RESTORE : SW_SHOW);
        var isForeground = SetForegroundWindow(window);
        return new RaiseResult(isForeground, $"\"{TitleOf(window)}\" of {owner}{(isForeground ? "" : ", but Windows refused the foreground")}");
    }

    /// <summary>
    /// Closes the agent's own window as its close button would (Windows Terminal or the console then ends the agent); with
    /// no window found, the agent process itself is ended.
    /// </summary>
    public static RaiseResult Close(int pid, ProcessTable processes)
    {
        var (window, owner, tried) = Find(pid, processes);
        if (window != IntPtr.Zero)
        {
            var title = TitleOf(window);
            var isPosted = PostMessage(window, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return new RaiseResult(isPosted, $"asked \"{title}\" of {owner} to close");
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            process.Kill();
            return new RaiseResult(true, $"no window on {tried}; ended process {pid}");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new RaiseResult(false, $"could not end process {pid}: {exception.Message}");
        }
    }

    /// <summary>The window <see cref="Raise"/> would bring up, without touching it.</summary>
    public static (IntPtr Window, string? Owner, string Tried) Find(int pid, ProcessTable processes)
    {
        var window = IntPtr.Zero;
        string? owner = null;
        var tried = new List<string>();
        foreach (var candidate in new[] { pid }.Concat(processes.Ancestors(pid).Select(p => p.Pid)))
        {
            var exe = processes.Get(candidate)?.Exe ?? "?";
            if (SHELL.Contains(exe))
            {
                break;
            }

            tried.Add($"{exe} ({candidate})");
            window = ConsoleWindowOf(candidate);
            if (window == IntPtr.Zero)
            {
                window = TopLevelWindowOf(candidate);
            }

            if (window != IntPtr.Zero)
            {
                owner = $"{exe} ({candidate})";
                break;
            }
        }

        return (window, owner, string.Join(" → ", tried));
    }

    private static IntPtr ConsoleWindowOf(int pid)
    {
        lock (ConsoleInput.GATE)
        {
            if (!AttachConsole((uint)pid))
            {
                return IntPtr.Zero;
            }

            try
            {
                var window = GetConsoleWindow();
                if (window == IntPtr.Zero)
                {
                    return IntPtr.Zero;
                }

                var terminal = GetAncestor(window, GA_ROOTOWNER);
                if (terminal != IntPtr.Zero && terminal != window && IsWindowVisible(terminal))
                {
                    return terminal;
                }

                return IsWindowVisible(window) ? window : IntPtr.Zero;
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private static IntPtr TopLevelWindowOf(int pid)
    {
        var found = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var owner);
            var isCandidate = owner == pid && IsWindowVisible(window) && GetWindow(window, 4) == IntPtr.Zero
                && (GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) == 0 && GetWindowTextLength(window) > 0;
            if (!isCandidate)
            {
                return true;
            }

            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static string TitleOf(IntPtr window)
    {
        var builder = new StringBuilder(GetWindowTextLength(window) + 1);
        GetWindowText(window, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
