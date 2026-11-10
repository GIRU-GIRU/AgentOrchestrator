using System.Text.Json;
using System.Text.RegularExpressions;
using AgentManager.Core.Windows;

namespace AgentManager.Core.Codex;

/// <summary>
/// Which Codex process is a window. Since the app-server daemon, one background <c>codex.exe</c> (a child of the first
/// window, its pid in <c>~/.codex/app-server-daemon/daemon.pid</c>) runs every window's threads and holds every
/// rollout open, so the process holding a rollout no longer says which window shows it. Codex records that nowhere
/// (not in the rollout, the lock files or its logs), so a daemon-hosted thread is given the window started to resume it
/// (<see cref="WindowResuming"/>), else the window whose start it followed within <see cref="STARTUP_MATCH"/>, which is
/// true of a window's first thread; any other gets no window rather than a wrong one.
/// </summary>
public static class CodexWindows
{
    public static readonly TimeSpan STARTUP_MATCH = TimeSpan.FromSeconds(60);

    /// <summary>A window starts before the thread it opens, but the two clocks are read separately.</summary>
    private static readonly TimeSpan CLOCK_SLACK = TimeSpan.FromSeconds(2);

    private const string RESUME_COMMAND = "resume";

    private const string APP_SERVER_COMMAND = "app-server";

    /// <summary>Shorter than this and another window could show the same words by chance.</summary>
    public const int MIN_SIGHT_LETTERS = 12;

    private const int SIGHT_LETTERS = 48;

    private const char PROMPT_CUT = '…';

    private static readonly Regex COMMAND_TOKEN = new("\"([^\"]*)\"|\\S+", RegexOptions.Compiled);

    /// <summary>
    /// The daemon and its helpers are Codex processes started by another Codex process; a window is started from a
    /// shell. The pid file also names the daemon after the window that started it has closed, and the command line names
    /// what outlives even that: <c>app-server daemon pid-update-loop</c>, which reparents to nothing and was counted as a
    /// window for as long as the machine was up.
    /// </summary>
    public static HashSet<int> Daemons(IEnumerable<ProcessEntry> codex, Func<int, ProcessEntry?> processOf, int? daemonPid, Func<int, string?>? commandLineOf = null)
    {
        var daemons = new HashSet<int>();
        foreach (var process in codex)
        {
            var isChildOfCodex = processOf(process.ParentPid) is { } parent && string.Equals(parent.Exe, process.Exe, StringComparison.OrdinalIgnoreCase);
            if (isChildOfCodex || process.Pid == daemonPid || IsAppServer(commandLineOf?.Invoke(process.Pid)))
            {
                daemons.Add(process.Pid);
            }
        }

        return daemons;
    }

    public static bool IsAppServer(string? commandLine)
    {
        if (commandLine is null)
        {
            return false;
        }

        var tokens = COMMAND_TOKEN.Matches(commandLine).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value);
        return tokens.Skip(1).FirstOrDefault()?.Equals(APP_SERVER_COMMAND, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// What a window was started to resume: the arguments after <c>resume</c> on its command line, or null when it was
    /// not started to resume anything. A resumed thread was created long before its window, so start times cannot place
    /// it, and such a window opens no new thread for <see cref="WindowFor"/> to give it.
    /// </summary>
    public static IReadOnlyList<string>? ResumeArguments(string? commandLine)
    {
        if (commandLine is null)
        {
            return null;
        }

        var tokens = COMMAND_TOKEN.Matches(commandLine).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value);
        var fromResume = tokens.Skip(1).SkipWhile(t => !t.Equals(RESUME_COMMAND, StringComparison.OrdinalIgnoreCase)).ToList();
        return fromResume.Count == 0 ? null : fromResume.Skip(1).ToList();
    }

    /// <summary>
    /// Whether a Codex was started as a chat window: no subcommand, or <c>resume</c> / <c>fork</c>. A sign-in, <c>exec</c>, <c>queue</c> and
    /// the rest are Codex processes that never show a conversation. An unreadable command line is not one, so nothing is guessed onto it.
    /// </summary>
    public static bool IsInteractive(string? commandLine)
    {
        if (commandLine is null)
        {
            return false;
        }

        var tokens = COMMAND_TOKEN.Matches(commandLine).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Value).Skip(1).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!tokens[i].StartsWith('-'))
            {
                return !NON_CHAT_COMMANDS.Contains(tokens[i]);
            }

            if (VALUE_OPTIONS.Contains(tokens[i]))
            {
                i++;
            }
        }

        return true;
    }

    private static readonly HashSet<string> NON_CHAT_COMMANDS = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "e", "review", "login", "logout", "mcp", "mcp-server", "plugin", "app-server", "remote-control", "app", "completion", "update", "doctor",
        "sandbox", "debug", "apply", "a", "queue", "archive", "delete", "migrate-rollouts", "migrate-nfs-db", "unarchive", "cloud", "exec-server", "features", "help", "agents",
    };

    /// <summary>The options of <c>codex --help</c> that take a value, so the value is not read as a subcommand.</summary>
    private static readonly HashSet<string> VALUE_OPTIONS = new(StringComparer.Ordinal)
    {
        "-c", "--config", "--enable", "--disable", "--remote", "--remote-auth-token-env", "-i", "--image", "-m", "--model", "--local-provider",
        "-p", "--profile", "-s", "--sandbox", "-C", "--cd", "--add-dir", "-a", "--ask-for-approval",
    };

    /// <summary>The window whose <c>codex resume</c> names this thread, by its id or its name.</summary>
    public static int? WindowResuming(string? threadId, string? threadName, IEnumerable<(int Pid, IReadOnlyList<string> Arguments)> resumeWindows)
    {
        bool NamesThread(string argument)
        {
            return string.Equals(argument, threadId, StringComparison.OrdinalIgnoreCase) || string.Equals(argument, threadName, StringComparison.OrdinalIgnoreCase);
        }

        return resumeWindows.Where(w => w.Arguments.Any(NamesThread)).Select(w => (int?)w.Pid).FirstOrDefault();
    }

    public static int? WindowFor(DateTimeOffset? threadCreatedAt, IEnumerable<(int Pid, DateTimeOffset Start)> windows)
    {
        if (threadCreatedAt is not { } created)
        {
            return null;
        }

        var match = windows
            .Where(w => OpenedThread(w.Start, created))
            .OrderByDescending(w => w.Start)
            .Select(w => (int?)w.Pid)
            .FirstOrDefault();
        return match;
    }

    /// <summary>
    /// Start times only say which thread a window opened first. The next one is a <c>/clear</c>, <c>/new</c> or picked <c>/resume</c>
    /// inside some window, and giving it to any window started in the last minute would put a /clear in one Astra on another's card.
    /// </summary>
    public static Dictionary<string, int> FirstThreadOfEachWindow(IEnumerable<(string Id, DateTimeOffset Created)> threads, IReadOnlyCollection<(int Pid, DateTimeOffset Start)> windows)
    {
        var placed = new Dictionary<string, int>();
        var taken = new HashSet<int>();
        foreach (var (id, created) in threads.OrderBy(t => t.Created))
        {
            if (WindowFor(created, windows.Where(w => !taken.Contains(w.Pid))) is { } pid)
            {
                placed[id] = pid;
                taken.Add(pid);
            }
        }

        return placed;
    }

    /// <summary>Letters and digits only: a terminal UI wraps and indents lines and draws markdown without its marks.</summary>
    public static string ScreenLetters(IEnumerable<string> rows)
    {
        return string.Concat(rows.SelectMany(row => row).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));
    }

    /// <summary>
    /// Codex records the link between a thread and its window nowhere, so what the window draws is the only witness. The window showing
    /// the most of this thread's words, when one window does; several tying (a shared first prompt) is no answer.
    /// </summary>
    public static int? WindowShowing(IEnumerable<string?> threadTexts, IReadOnlyDictionary<int, string> screens)
    {
        var probes = threadTexts.SelectMany(SightProbes).Distinct().ToList();
        var scores = screens.Select(screen => (screen.Key, Score: probes.Count(probe => screen.Value.Contains(probe, StringComparison.Ordinal)))).Where(s => s.Score > 0).OrderByDescending(s => s.Score).ToList();
        var isClearWinner = scores.Count == 1 || (scores.Count > 1 && scores[0].Score > scores[1].Score);
        return isClearWinner ? scores[0].Key : null;
    }

    private static IEnumerable<string> SightProbes(string? text)
    {
        var letters = ScreenLetters([text ?? ""]);
        if (letters.Length >= MIN_SIGHT_LETTERS)
        {
            yield return letters[..Math.Min(letters.Length, SIGHT_LETTERS)];
            yield return letters[^Math.Min(letters.Length, SIGHT_LETTERS)..];
        }
    }

    /// <summary>
    /// The window that has this thread's first prompt on its command line. Windows opened in the same minute all pass <see cref="WindowFor"/>
    /// (astra.py can open several in one second, and all their cards would get one window's pid, so Raise, Send and Close would hit
    /// the wrong Astra), so this comes first where a prompt says which. A stored prompt may be cut off with an ellipsis.
    /// </summary>
    public static int? WindowPrompted(string? firstPrompt, DateTimeOffset? threadCreatedAt, IEnumerable<(int Pid, DateTimeOffset Start, string? CommandLine)> windows)
    {
        var prompt = firstPrompt?.TrimEnd(PROMPT_CUT);
        if (string.IsNullOrEmpty(prompt) || threadCreatedAt is not { } created)
        {
            return null;
        }

        return windows
            .Where(w => OpenedThread(w.Start, created) && w.CommandLine?.Contains(prompt, StringComparison.Ordinal) == true)
            .OrderByDescending(w => w.Start)
            .Select(w => (int?)w.Pid)
            .FirstOrDefault();
    }

    private static bool OpenedThread(DateTimeOffset windowStart, DateTimeOffset threadCreated)
    {
        return windowStart <= threadCreated + CLOCK_SLACK && threadCreated - windowStart <= STARTUP_MATCH;
    }

    /// <summary>
    /// A Restart Manager query walks every handle on the machine, which is slow to do every scan for rollouts nobody has open. Nothing can have
    /// opened this one while the same Codex processes run and no new line has reached it; the timer only catches a /resume that has written nothing yet.
    /// </summary>
    public static bool IsKnownNotHeld(string? notHeldUnder, string processes, DateTimeOffset? notHeldAtEvent, DateTimeOffset? lastEvent, DateTimeOffset nextCheck, DateTimeOffset now)
    {
        return notHeldUnder == processes && notHeldAtEvent == lastEvent && now < nextCheck;
    }

    /// <summary>
    /// A held rollout stays with its holder until the Codex processes change or the recheck comes: a Restart Manager query
    /// walks every process and handle on the machine (0.3 to 0.6 s each here), and asking for every live thread every scan
    /// kept the manager busy. Its writes are the holder's own, so they change nothing.
    /// </summary>
    public static int? KnownHolder((int Holder, string Under)? heldUnder, string processes, DateTimeOffset nextCheck, DateTimeOffset now)
    {
        return heldUnder is { } held && held.Under == processes && now < nextCheck ? held.Holder : null;
    }

    /// <summary>
    /// The daemon keeps a thread for about a minute after its window closes, so a thread whose window has exited is
    /// closed without waiting for it. The start time
    /// guards against a reused pid.
    /// </summary>
    public static bool IsGone((int Pid, DateTimeOffset Start)? lastWindow, IEnumerable<(int Pid, DateTimeOffset Start)> windows)
    {
        return lastWindow is { } known && !windows.Any(w => w.Pid == known.Pid && Math.Abs((w.Start - known.Start).TotalSeconds) < 1);
    }

    /// <summary>The daemon's pid file: <c>{"pid":41620,"processStartTime":"134380228947684568",...}</c> (a FILETIME).</summary>
    public static (int Pid, long Start)? ParseDaemonPid(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out var value))
            {
                return null;
            }

            var start = root.TryGetProperty("processStartTime", out var time) && time.ValueKind == JsonValueKind.String && long.TryParse(time.GetString(), out var ticks) ? ticks : 0;
            return (value, start);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
