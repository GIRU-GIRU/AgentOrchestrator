using System.Collections;
using System.Diagnostics;

namespace AgentManager.Core.Windows;

public static class Launcher
{
    private static readonly string[] SESSION_PREFIXES = ["CLAUDE", "CODEX"];

    public static string WindowsTerminal => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");

    /// <summary>
    /// Removes the variables an agent session sets for its own children. When the manager is started from inside an
    /// agent, they leak into every terminal it opens, and a resumed Claude then believes it is a child session and stops
    /// saving its transcript.
    /// </summary>
    public static IReadOnlyList<string> ForgetAgentSession()
    {
        var removed = new List<string>();
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            var name = (string)variable.Key;
            if (IsSessionVariable(name))
            {
                Environment.SetEnvironmentVariable(name, null);
                removed.Add(name);
            }
        }

        return removed;
    }

    /// <summary>
    /// A Claude or Codex variable that only this process has: one a parent agent set for its children. The same name set
    /// in the user's or the machine's environment is the user's own configuration and is kept.
    /// </summary>
    public static bool IsSessionVariable(string name)
    {
        var isAgentName = SESSION_PREFIXES.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var isConfigured = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) is not null
            || Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) is not null;
        return isAgentName && !isConfigured;
    }

    /// <summary>The command that starts an agent: its name when it is on PATH, else the known install location.</summary>
    public static string AgentCommand(Model.AgentKind kind)
    {
        var name = kind == Model.AgentKind.Claude ? "claude" : "codex";
        var onPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir.Trim(), name + ".exe")) || File.Exists(Path.Combine(dir.Trim(), name + ".cmd")));
        if (onPath)
        {
            return name;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var known = kind == Model.AgentKind.Claude
            ? Path.Combine(home, ".local", "bin", "claude.exe")
            : Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        return File.Exists(known) ? "\"" + known + "\"" : name;
    }

    private static readonly HashSet<string> SHELLS = new(StringComparer.OrdinalIgnoreCase) { "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe" };

    /// <summary>The shell an agent runs in, when it was started from one: it outlives the agent and keeps its window open.</summary>
    public static (int Pid, long Start)? ShellOf(int pid, ProcessTable processes)
    {
        var parent = processes.Get(pid) is { } entry ? processes.Get(entry.ParentPid) : null;
        if (parent is null || !SHELLS.Contains(parent.Exe) || processes.StartTime(parent.Pid) > processes.StartTime(pid))
        {
            return null;
        }

        return (parent.Pid, processes.StartTime(parent.Pid));
    }

    /// <summary>
    /// Runs a command line in a new terminal window (Windows Terminal, else a console), in <paramref name="cwd"/>. The
    /// shell ends with the command (<c>/c</c>), so the window closes when the agent exits; Windows Terminal keeps it open
    /// when the command fails, so an error can still be read.
    /// </summary>
    public static void RunInTerminal(string commandLine, string? cwd, string title)
    {
        AllowSetForegroundWindow(ASFW_ANY);
        var directory = cwd is not null && Directory.Exists(cwd) ? cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var hasTerminal = File.Exists(WindowsTerminal);
        var start = new ProcessStartInfo(hasTerminal ? WindowsTerminal : "cmd.exe") { UseShellExecute = false, WorkingDirectory = directory };
        var arguments = hasTerminal
            ? new[] { "-w", "new", "new-tab", "--title", title, "-d", directory, "cmd", "/c", commandLine }
            : ["/c", commandLine];
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var name in start.Environment.Keys.Where(IsSessionVariable).ToList())
        {
            start.Environment.Remove(name);
        }

        Process.Start(start);
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
        }
    }

    public static void RevealFile(string path)
    {
        if (File.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(path)) { UseShellExecute = true });
        }
    }

    private const int ASFW_ANY = -1;

    /// <summary>
    /// The manager is the foreground app when the user clicks; this lets the terminal it starts take the foreground, so
    /// the new window comes up in front instead of flashing behind.
    /// </summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static string Quote(string path)
    {
        return "\"" + path + "\"";
    }
}
