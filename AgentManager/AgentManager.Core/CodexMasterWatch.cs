using System.Diagnostics;
using System.Text;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.Model;
using AgentManager.Core.Windows;

namespace AgentManager.Core;

/// <summary>
/// A Codex cannot be woken by a background shell ending, which is how a Claude Master Agent runs his watcher (the master-agent skill).
/// So when the Master Agent is a Codex the manager runs <see cref="WATCHER"/> for him, restarts it each time it exits, and types what it
/// printed into his window once he is at his prompt with an empty box. A Claude Master Agent is left to run his own.
/// </summary>
public sealed class CodexMasterWatch(string wakeFolder) : IDisposable
{
    public const string WATCHER = ".claude/skills/master-agent/watch.py";

    /// <summary>A typed wake arrives as a prompt, like the user's own words, so it says whose it is.</summary>
    public const string WAKE_PREFIX = "[watcher, not the user]";

    private const string LOG_AREA = "master";

    private const string WAKE_FILE_PREFIX = "wake-";

    private const int SUMMARY_CHARS = 200;

    private static readonly TimeSpan FAILED_RESTART = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan TYPE_RETRY = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan KEEP_WAKE_FILES = TimeSpan.FromDays(1);

    private readonly Lock gate = new();

    private readonly StringBuilder output = new();

    private readonly List<string> pending = [];

    private Process? watcher;

    private DateTimeOffset nextStart;

    private DateTimeOffset nextType;

    private string? lastProblem;

    public void Tick(AgentSnapshot? master, string? project, DateTimeOffset now)
    {
        if (master is not { Kind: AgentKind.Codex, IsAlive: true } || project is null)
        {
            Stop("the Master Agent is not a running Codex");
            pending.Clear();
            return;
        }

        TakeFinished(now);
        StartIfIdle(project, now);
        TypeWake(master, now);
    }

    public void Dispose()
    {
        Stop("the manager is closing");
    }

    /// <summary>
    /// One line, because a newline would send it early: what the newest run said first, how much more there is, and where all of it is.
    /// <c>@</c> and <c>$</c> are dropped because in Codex a word starting with one opens a picker that takes the Enter.
    /// </summary>
    public static string WakeLine(IReadOnlyList<(string Path, string Text)> wakes)
    {
        var lines = wakes.SelectMany(w => w.Text.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var newest = wakes.Count == 0 ? "" : wakes[^1].Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        var summary = Clean(newest);
        if (summary.Length > SUMMARY_CHARS)
        {
            summary = summary[..SUMMARY_CHARS] + "…";
        }

        var more = lines.Count > 1 ? $" (+{lines.Count - 1} more lines)" : "";
        var files = string.Join(", ", wakes.Select(w => Clean(w.Path)));
        return $"{WAKE_PREFIX} watch.py: {summary}{more}. Full output: {files}. Act on it as the master-agent skill says; the manager restarts the watcher itself.";
    }

    /// <summary>He is at his prompt: no turn running and nothing on screen asking for an answer.</summary>
    public static bool IsAtPrompt(AgentSnapshot master)
    {
        return master is { IsAlive: true, IsWorking: false, PendingQuestion: false, WaitingFor: null, Pid: not null };
    }

    private static string Clean(string text)
    {
        return text.Replace("@", "").Replace("$", "").Replace("\r", "").Replace("\t", " ");
    }

    private void TakeFinished(DateTimeOffset now)
    {
        if (watcher is not { HasExited: true } done)
        {
            return;
        }

        done.WaitForExit();
        var code = done.ExitCode;
        done.Dispose();
        watcher = null;
        string text;
        lock (gate)
        {
            text = output.ToString();
            output.Clear();
        }

        nextStart = code == 0 ? now : now + FAILED_RESTART;
        if (code != 0)
        {
            text = $"the watcher failed (exit {code}): " + text;
            AppLog.Warn(LOG_AREA, $"watch.py exited {code}; it starts again in {FAILED_RESTART.TotalSeconds:0} s");
        }

        if (text.Trim().Length == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(wakeFolder);
            var path = Path.Combine(wakeFolder, $"{WAKE_FILE_PREFIX}{now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text);
            pending.Add(path);
            AppLog.Info(LOG_AREA, $"watch.py woke him: {text.Split('\n')[0].Trim()} ({path})");
            PruneWakeFiles(now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Error(LOG_AREA, "could not keep the watcher's output", exception);
        }
    }

    private void PruneWakeFiles(DateTimeOffset now)
    {
        foreach (var file in Directory.EnumerateFiles(wakeFolder, WAKE_FILE_PREFIX + "*.txt"))
        {
            if (now - File.GetLastWriteTime(file) > KEEP_WAKE_FILES && !pending.Contains(file))
            {
                File.Delete(file);
            }
        }
    }

    private void StartIfIdle(string project, DateTimeOffset now)
    {
        if (watcher is not null || now < nextStart)
        {
            return;
        }

        var start = new ProcessStartInfo("python")
        {
            WorkingDirectory = project,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-X");
        start.ArgumentList.Add("utf8");
        start.ArgumentList.Add(WATCHER);
        foreach (var name in start.Environment.Keys.Where(Launcher.IsSessionVariable).ToList())
        {
            start.Environment.Remove(name);
        }

        try
        {
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, line) => Collect(line.Data);
            process.ErrorDataReceived += (_, line) => Collect(line.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            watcher = process;
            Problem(null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            nextStart = now + FAILED_RESTART;
            Problem("could not start watch.py: " + exception.Message);
        }
    }

    private void Collect(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (gate)
        {
            output.AppendLine(line);
        }
    }

    private void TypeWake(AgentSnapshot master, DateTimeOffset now)
    {
        if (pending.Count == 0 || now < nextType || !IsAtPrompt(master))
        {
            return;
        }

        var wakes = pending.Where(File.Exists).Select(path => (path, File.ReadAllText(path))).ToList();
        if (wakes.Count == 0)
        {
            pending.Clear();
            return;
        }

        var line = WakeLine(wakes);
        var (isSent, how) = ConsoleInput.Attached(master.Pid!.Value, console => PromptTyping.TypeIfEmpty(AgentKind.Codex, line, console, Thread.Sleep), why => (false, why));
        if (isSent)
        {
            AppLog.Info(LOG_AREA, $"typed the watcher's wake into his window ({wakes.Count} run(s))");
            pending.Clear();
            Problem(null);
            return;
        }

        nextType = now + TYPE_RETRY;
        Problem("could not type the watcher's wake: " + how);
    }

    private void Stop(string why)
    {
        if (watcher is null)
        {
            return;
        }

        try
        {
            if (!watcher.HasExited)
            {
                watcher.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Warn(LOG_AREA, "could not stop watch.py: " + exception.Message);
        }

        watcher.Dispose();
        watcher = null;
        lock (gate)
        {
            output.Clear();
        }

        AppLog.Info(LOG_AREA, "stopped watch.py: " + why);
    }

    /// <summary>Logs a problem once, not every tick it repeats.</summary>
    private void Problem(string? what)
    {
        if (what is not null && what != lastProblem)
        {
            AppLog.Warn(LOG_AREA, what);
        }

        lastProblem = what;
    }
}
