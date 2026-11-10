using System.Globalization;
using AgentManager.Core.Chat;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.Model;
using AgentManager.Core.State;
using AgentManager.Core.Windows;

namespace AgentManager.Core.Codex;

/// <summary>
/// Codex threads on this machine, from their rollouts. Codex keeps no registry of running sessions, so a thread is alive
/// while a <c>codex.exe</c> process holds its rollout open (asked through the Restart Manager); when none can be matched,
/// a thread in the middle of a turn, or written in the last few minutes, counts as alive while any Codex runs.
/// </summary>
public sealed class CodexSource
{
    public const string KEY_PREFIX = "codex:";

    private const string CODEX_EXE = "codex.exe";

    private const int USAGE_POINTS = 90;

    private static readonly TimeSpan DISCOVERY_PERIOD = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan TODAY_PERIOD = TimeSpan.FromSeconds(2);

    private DateTimeOffset nextToday;

    private static readonly TimeSpan HOLDER_PERIOD = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RECENT_WRITE = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan NOT_HELD_RECHECK = TimeSpan.FromSeconds(30);

    /// <summary>A thread no window's screen shows is looked for again this often: attaching to every window every scan buys nothing.</summary>
    private static readonly TimeSpan SIGHT_RETRY = TimeSpan.FromSeconds(10);

    private readonly string sessionsDir;

    private readonly Dictionary<string, CodexThread> threads = [];

    private readonly Dictionary<string, string> threadNames = [];

    private readonly string indexPath;

    private DateTime indexWrite;

    private DateTimeOffset nextDiscovery;

    private DateTimeOffset nextHolderScan;

    private readonly Dictionary<int, string> processThreads = [];

    private readonly string daemonPidPath;

    private DateTime daemonPidWrite;

    private (int Pid, long Start)? daemonPidFile;

    private string? loggedProcesses;

    private readonly List<(string NewKey, string OldKey)> replacements = [];

    /// <summary>
    /// Codex windows nothing has been typed into yet. Codex opens no thread until the first prompt, so a new window would have no card at
    /// all; these get one, which the thread takes over.
    /// </summary>
    private List<(int Pid, DateTimeOffset Start)> emptyWindows = [];

    private readonly HashSet<(int Pid, DateTimeOffset Start)> windowsWithAThread = [];

    /// <summary>Windows not started to resume a thread, from the last liveness scan.</summary>
    private List<(int Pid, DateTimeOffset Start)> lastOpeningWindows = [];

    /// <summary>A thread's creation time and a process's start come from two clocks.</summary>
    private static readonly TimeSpan WINDOW_CLOCK_SLACK = TimeSpan.FromSeconds(2);

    private const string EMPTY_WINDOW_KEY = "window-";

    private const string EMPTY_WINDOW_NAME = "New Codex";

    public CodexSource(string codexDir)
    {
        sessionsDir = Path.Combine(codexDir, "sessions");
        indexPath = Path.Combine(codexDir, "session_index.jsonl");
        daemonPidPath = Path.Combine(codexDir, "app-server-daemon", "daemon.pid");
    }

    public void Poll(ProcessTable processes, ChatLog chat, ManagerStore store, Func<string, string> nameOf, DateTimeOffset now)
    {
        if (now >= nextDiscovery)
        {
            nextDiscovery = now + DISCOVERY_PERIOD;
            Discover(store, now);
        }
        else if (now >= nextToday)
        {
            nextToday = now + TODAY_PERIOD;
            DiscoverDay(now, now - TimeSpan.FromHours(1));
        }

        ReadThreadNames();

        foreach (var thread in threads.Values)
        {
            foreach (var line in thread.Tail.ReadNewLines())
            {
                CodexLines.Apply(line, thread, chat, nameOf);
            }
        }

        if (now >= nextHolderScan)
        {
            nextHolderScan = now + HOLDER_PERIOD;
            UpdateLiveness(processes, now);
            FindEmptyWindows(processes);
        }
    }

    public IEnumerable<AgentSnapshot> Snapshots(DateTimeOffset now)
    {
        foreach (var window in emptyWindows)
        {
            yield return EmptyWindowSnapshot(window);
        }

        var children = threads.Values.Where(t => t.ParentId is not null && !t.IsReview).ToLookup(t => t.ParentId!);
        foreach (var thread in threads.Values)
        {
            var hasActivity = thread.Tracker.LastPromptAt is not null || thread.Tracker.TotalTokens > 0;
            if (thread.Id is null || thread.ParentId is not null || thread.IsReview || !hasActivity)
            {
                continue;
            }

            var tracker = thread.Tracker;
            var isWorking = thread.IsAlive && tracker.TurnOpen;
            yield return new AgentSnapshot
            {
                Key = KEY_PREFIX + thread.Id,
                Kind = AgentKind.Codex,
                SessionId = thread.Id,
                Name = NameOf(thread),
                Title = tracker.LastPromptText ?? tracker.FirstPrompt,
                Cwd = tracker.Cwd,
                Model = tracker.Model,
                Effort = tracker.Effort,
                Surface = Surface(thread.Originator),
                Pid = thread.Pid,
                Shell = thread.Shell,
                IsAlive = thread.IsAlive,
                IsWorking = isWorking,
                HasPrompt = tracker.LastPromptAt is not null,
                WaitingFor = tracker.PendingQuestion ? "question" : null,
                NeedsInputSince = tracker.PendingQuestion ? tracker.QuestionAt : tracker.TurnEndedAt,
                StartedAt = tracker.FirstEventAt ?? now,
                FirstEventAt = tracker.FirstEventAt,
                LastEventAt = tracker.LastEventAt ?? now,
                LastPromptAt = tracker.LastPromptAt,
                Prompts = tracker.RecentPrompts,
                ListeningOn = thread.IsAlive ? tracker.ListeningOn : null,
                IsWaitingOnBridge = isWorking && tracker.IsWaitingOnBridge,
                BridgeChannels = tracker.BridgeChannels,
                Tokens = tracker.Tokens,
                SubagentTokens = children[thread.Id].Sum(c => c.Tracker.TotalTokens),
                Split = tracker.Split,
                Spark = tracker.Spark(now),
                UsageProfile = tracker.UsageProfile(thread.IsAlive ? now : tracker.LastEventAt ?? now, USAGE_POINTS),
                CurrentTool = isWorking ? tracker.CurrentTool : null,
                RecentTools = tracker.RecentTools,
                LastText = tracker.LastText,
                LastTextAt = tracker.LastTextAt,
                AsksQuestion = tracker.QuestionAt is not null && tracker.QuestionAt >= tracker.LastPromptAt,
                QuestionText = tracker.QuestionText,
                QuestionAt = tracker.QuestionAt,
                PendingQuestion = tracker.PendingQuestion,
                Subagents = children[thread.Id]
                    .Select(c => new SubagentInfo(c.Id ?? "?", "agent", thread.ChildNames.GetValueOrDefault(c.Id ?? "") ?? c.Tracker.FirstPrompt ?? "agent", thread.IsAlive && c.Tracker.TurnOpen, c.Tracker.LastEventAt, c.Tracker.TotalTokens))
                    .ToArray(),
                TranscriptPath = thread.Tail.Path,
                ResumeCommand = "codex resume " + thread.Id,
            };
        }
    }

    public void CollectNames(IDictionary<string, string> names)
    {
        foreach (var thread in threads.Values.Where(t => t.Id is not null))
        {
            names[KEY_PREFIX + thread.Id] = NameOf(thread);
            foreach (var (childId, nickname) in thread.ChildNames)
            {
                names[KEY_PREFIX + childId] = nickname;
            }
        }
    }

    public IReadOnlyList<(string NewKey, string OldKey)> TakeReplacements()
    {
        var taken = replacements.ToArray();
        replacements.Clear();
        return taken;
    }

    public IEnumerable<(string Key, int Pid)> LivePids()
    {
        return threads.Values.Where(t => t.IsAlive && t.Pid is not null && t.Id is not null).Select(t => (KEY_PREFIX + t.Id!, t.Pid!.Value));
    }

    private void Discover(ManagerStore store, DateTimeOffset now)
    {
        if (!Directory.Exists(sessionsDir))
        {
            return;
        }

        var days = Math.Max(store.Settings.ChatDays, (int)Math.Ceiling(store.Settings.HistoryHours / 24.0)) + 1;
        var since = now - TimeSpan.FromDays(days);
        for (var day = now.Date; day >= since.Date; day = day.AddDays(-1))
        {
            DiscoverDay(new DateTimeOffset(day, now.Offset), since);
        }
    }

    private void DiscoverDay(DateTimeOffset day, DateTimeOffset since)
    {
        var dir = Path.Combine(sessionsDir, day.ToString("yyyy", CultureInfo.InvariantCulture), day.ToString("MM", CultureInfo.InvariantCulture), day.ToString("dd", CultureInfo.InvariantCulture));
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
        {
            if (!threads.ContainsKey(file) && File.GetLastWriteTime(file) >= since)
            {
                threads[file] = new CodexThread(file);
            }
        }
    }

    /// <summary>
    /// A thread is alive while a Codex process holds its rollout. Only a window holding it says which window shows it;
    /// the app-server daemon holds every window's rollouts, so a daemon-hosted thread is placed by
    /// <see cref="CodexWindows.WindowFor"/> and never counts as a window switching session: taking the daemon for a
    /// window handed one card between two windows' threads every scan, hid the other card, and gave the daemon's pid
    /// to Raise and Close.
    /// </summary>
    private void UpdateLiveness(ProcessTable processes, DateTimeOffset now)
    {
        var codex = processes.Named(CODEX_EXE).ToList();
        var codexPids = codex.Select(p => p.Pid).ToHashSet();
        var daemons = CodexWindows.Daemons(codex, processes.Get, DaemonPid(processes), processes.CommandLine);
        var windows = codex
            .Where(p => !daemons.Contains(p.Pid))
            .Select(p => (p.Pid, Start: processes.StartedAt(p.Pid)))
            .Where(w => w.Start is not null)
            .Select(w => (w.Pid, Start: w.Start!.Value))
            .ToList();
        LogDaemons(daemons, windows);
        var resumeWindows = windows
            .Select(w => (w.Pid, Arguments: CodexWindows.ResumeArguments(processes.CommandLine(w.Pid))))
            .Where(w => w.Arguments is not null)
            .Select(w => (w.Pid, Arguments: w.Arguments!))
            .ToList();
        var openingWindows = windows.Where(w => resumeWindows.All(r => r.Pid != w.Pid)).ToList();
        lastOpeningWindows = openingWindows;
        var promptedWindows = openingWindows.Select(w => (w.Pid, w.Start, CommandLine: processes.CommandLine(w.Pid))).ToList();
        var firstThreads = CodexWindows.FirstThreadOfEachWindow(threads.Values.Where(t => t.Id is not null && t.ParentId is null && !t.IsReview && t.CreatedAt is not null).Select(t => (t.Id!, t.CreatedAt!.Value)), openingWindows);

        var codexRunning = string.Join(',', codexPids.Order());
        var anyMatched = false;
        foreach (var (path, thread) in threads)
        {
            var isCandidate = codexPids.Count > 0 && thread.ParentId is null && (thread.Tracker.TurnOpen || now - LastWrite(path, thread) < TimeSpan.FromHours(24));
            if (!isCandidate)
            {
                thread.IsAlive = false;
                thread.Pid = null;
                thread.HeldUnder = null;
                continue;
            }

            if (CodexWindows.IsKnownNotHeld(thread.NotHeldUnder, codexRunning, thread.NotHeldAtEvent, thread.Tracker.LastEventAt, thread.NextHolderCheck, now))
            {
                thread.IsAlive = false;
                thread.Pid = null;
                continue;
            }

            if (CodexWindows.KnownHolder(thread.HeldUnder, codexRunning, thread.NextHolderCheck, now) is not { } holder)
            {
                var holders = FileHolders.Pids(path);
                if (holders is null)
                {
                    anyMatched = true;
                    continue;
                }

                holder = holders.FirstOrDefault(codexPids.Contains);
                thread.NotHeldUnder = holder != 0 ? null : codexRunning;
                thread.HeldUnder = holder != 0 ? (holder, codexRunning) : null;
                thread.NotHeldAtEvent = thread.Tracker.LastEventAt;
                thread.NextHolderCheck = now + NOT_HELD_RECHECK;
            }

            var isHeld = holder != 0;
            var isDaemonHosted = isHeld && daemons.Contains(holder);
            var threadName = thread.Id is null ? null : threadNames.GetValueOrDefault(thread.Id);
            var resumedBy = isDaemonHosted ? CodexWindows.WindowResuming(thread.Id, threadName, resumeWindows) : null;
            var seen = thread.SeenWindow is { } sighted && windows.Contains(sighted) ? sighted.Pid : (int?)null;
            var guessed = CodexWindows.WindowPrompted(thread.Tracker.FirstPrompt, thread.CreatedAt, promptedWindows) ?? (firstThreads.TryGetValue(thread.Id ?? "", out var opened) ? opened : null);
            var hasLeftItsWindow = isDaemonHosted && thread.LeftWindow is not null && resumedBy is null && seen is null;
            var window = !isHeld ? null : isDaemonHosted ? resumedBy ?? seen ?? (hasLeftItsWindow ? null : guessed) : holder;
            var hasNoWindowToBeIn = isDaemonHosted && window is null && windows.Count == 0;
            var isWindowClosed = isDaemonHosted && window is null && (hasLeftItsWindow || hasNoWindowToBeIn || CodexWindows.IsGone(thread.LastWindow, windows));
            if (window is { } placed && processes.StartedAt(placed) is { } placedStart)
            {
                thread.LastWindow = (placed, placedStart);
            }

            thread.Pid = window;
            anyMatched |= isHeld;
            thread.IsAlive = isHeld && !isWindowClosed;
            if (window is { } windowPid)
            {
                RememberShell(thread, windowPid, processes);
            }

            LogPlacement(thread, isWindowClosed ? 0 : holder, isDaemonHosted, window, resumedBy is not null, isWindowClosed, seen is not null, hasLeftItsWindow, hasNoWindowToBeIn);
            if (isHeld && !isDaemonHosted && thread.Id is not null)
            {
                if (processThreads.TryGetValue(holder, out var previous) && previous != thread.Id)
                {
                    replacements.Add((KEY_PREFIX + thread.Id, KEY_PREFIX + previous));
                }

                processThreads[holder] = thread.Id;
            }
        }

        PlaceBySight(windows, processes, now);
        if (anyMatched || codexPids.Count == 0)
        {
            return;
        }

        foreach (var (path, thread) in threads)
        {
            var isRecent = thread.ParentId is null && (thread.Tracker.TurnOpen || now - LastWrite(path, thread) < RECENT_WRITE);
            thread.IsAlive = isRecent;
        }
    }

    private static void RememberShell(CodexThread thread, int windowPid, ProcessTable processes)
    {
        thread.Shell = new[] { windowPid }.Concat(processes.Ancestors(windowPid).Select(p => p.Pid)).Select(pid => Launcher.ShellOf(pid, processes)).FirstOrDefault(s => s is not null) ?? thread.Shell;
    }

    /// <summary>
    /// A window is empty while no thread has ever been placed on it and no thread opened since it started is still unplaced (that one may be
    /// its own). The first thread placed on an empty window that opened after it started takes the window's card, so the card stays where it was spawned.
    /// </summary>
    private void FindEmptyWindows(ProcessTable processes)
    {
        var topLevel = threads.Values.Where(t => t.Id is not null && t.ParentId is null && !t.IsReview).ToList();
        var placedOn = topLevel.Where(t => t.IsAlive && t.Pid is not null).ToLookup(t => t.Pid!.Value);
        var next = new List<(int Pid, DateTimeOffset Start)>();
        foreach (var window in lastOpeningWindows)
        {
            if (placedOn.Contains(window.Pid))
            {
                windowsWithAThread.Add(window);
                var opened = placedOn[window.Pid].FirstOrDefault(t => t.CreatedAt >= window.Start - WINDOW_CLOCK_SLACK);
                if (emptyWindows.Contains(window) && opened is not null)
                {
                    replacements.Add((KEY_PREFIX + opened.Id, EmptyWindowKey(window)));
                }

                continue;
            }

            var mayHaveOpenedOne = topLevel.Any(t => t.Pid is null && t.CreatedAt >= window.Start - WINDOW_CLOCK_SLACK);
            if (windowsWithAThread.Contains(window) || mayHaveOpenedOne || !CodexWindows.IsInteractive(processes.CommandLine(window.Pid)))
            {
                continue;
            }

            next.Add(window);
        }

        windowsWithAThread.RemoveWhere(w => !lastOpeningWindows.Contains(w));
        emptyWindows = next;
    }

    public static string EmptyWindowKey((int Pid, DateTimeOffset Start) window)
    {
        return $"{KEY_PREFIX}{EMPTY_WINDOW_KEY}{window.Pid}-{window.Start.UtcTicks}";
    }

    private static AgentSnapshot EmptyWindowSnapshot((int Pid, DateTimeOffset Start) window)
    {
        var key = EmptyWindowKey(window);
        return new AgentSnapshot
        {
            Key = key,
            Kind = AgentKind.Codex,
            SessionId = key[KEY_PREFIX.Length..],
            Name = EMPTY_WINDOW_NAME,
            Surface = "CLI",
            Pid = window.Pid,
            IsAlive = true,
            StartedAt = window.Start,
            LastEventAt = window.Start,
            TranscriptPath = "",
            ResumeCommand = "",
        };
    }

    /// <summary>
    /// A thread started by <c>/clear</c>, <c>/new</c> or a picked <c>/resume</c> inside a window has nothing that says which window: it is
    /// found by reading the windows' screens for its words, and then carries on the card of whatever that window showed before.
    /// </summary>
    private void PlaceBySight(List<(int Pid, DateTimeOffset Start)> windows, ProcessTable processes, DateTimeOffset now)
    {
        if (windows.Count == 0)
        {
            return;
        }

        Dictionary<int, string>? screens = null;
        foreach (var thread in threads.Values.Where(t => NeedsSight(t, now)).ToList())
        {
            thread.NextSightAt = now + SIGHT_RETRY;
            screens ??= windows.ToDictionary(w => w.Pid, w => ScreenOf(w.Pid));
            if (CodexWindows.WindowShowing([thread.Tracker.LastPromptText, thread.Tracker.FirstPrompt, thread.Tracker.LastText], screens) is { } pid)
            {
                PlaceOnWindow(thread, windows.First(w => w.Pid == pid), processes, now);
            }
        }
    }

    private static bool NeedsSight(CodexThread thread, DateTimeOffset now)
    {
        var hasCard = thread.Id is not null && thread.ParentId is null && !thread.IsReview && thread.Tracker.LastPromptAt is not null;
        var hasActedSinceLeaving = thread.LeftWindow is { } left && thread.Tracker.LastEventAt > left.At;
        return hasCard && thread.Pid is null && now >= thread.NextSightAt && (thread.LeftWindow is null ? thread.IsAlive : hasActedSinceLeaving);
    }

    private static string ScreenOf(int pid)
    {
        return ConsoleInput.Attached(pid, console => console.ReadScreen() is { } screen ? CodexWindows.ScreenLetters(screen.Rows) : "", _ => "");
    }

    private void PlaceOnWindow(CodexThread thread, (int Pid, DateTimeOffset Start) window, ProcessTable processes, DateTimeOffset now)
    {
        var movedOnFrom = threads.Values.Where(t => t != thread && t.Id is not null && t.LastWindow == window).OrderByDescending(t => t.CreatedAt).ToList();
        foreach (var old in movedOnFrom)
        {
            old.LastWindow = null;
            old.SeenWindow = null;
            old.Pid = null;
            old.IsAlive = false;
            old.LeftWindow = (window.Pid, now);
        }

        thread.SeenWindow = window;
        thread.LastWindow = window;
        thread.LeftWindow = null;
        thread.Pid = window.Pid;
        thread.IsAlive = true;
        RememberShell(thread, window.Pid, processes);
        AppLog.Info("agent", $"codex {NameOf(thread)} ({thread.Id}): window {window.Pid} is showing it, seen on its screen" + (movedOnFrom.Count > 0 ? $"; that window moved on from {NameOf(movedOnFrom[0])}" : ""));
        if (movedOnFrom.Count > 0)
        {
            replacements.Add((KEY_PREFIX + thread.Id, KEY_PREFIX + movedOnFrom[0].Id));
        }
    }

    /// <summary>
    /// A rollout the daemon keeps open keeps its modified time from when it was opened, hours before the events it
    /// holds, so the newest event read from it counts too.
    /// </summary>
    private static DateTimeOffset LastWrite(string path, CodexThread thread)
    {
        var written = new DateTimeOffset(File.GetLastWriteTime(path));
        return thread.Tracker.LastEventAt is { } last && last > written ? last : written;
    }

    private int? DaemonPid(ProcessTable processes)
    {
        try
        {
            var write = File.GetLastWriteTimeUtc(daemonPidPath);
            if (write != daemonPidWrite)
            {
                daemonPidWrite = write;
                using var reader = new StreamReader(Transcripts.JsonlTail.OpenShared(daemonPidPath));
                daemonPidFile = CodexWindows.ParseDaemonPid(reader.ReadToEnd());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            daemonPidFile = null;
        }

        return daemonPidFile is { } daemon && processes.IsAlive(daemon.Pid, daemon.Start) ? daemon.Pid : null;
    }

    private void LogDaemons(HashSet<int> daemons, List<(int Pid, DateTimeOffset Start)> windows)
    {
        var text = $"daemon/helpers {(daemons.Count == 0 ? "none" : string.Join(", ", daemons.Order()))}; windows {(windows.Count == 0 ? "none" : string.Join(", ", windows.OrderBy(w => w.Start).Select(w => $"{w.Pid} (started {w.Start.ToLocalTime():HH:mm:ss})")))}";
        if (text != loggedProcesses)
        {
            loggedProcesses = text;
            AppLog.Info("agent", "codex processes: " + text);
        }
    }

    private void LogPlacement(CodexThread thread, int holder, bool isDaemonHosted, int? window, bool isResumed, bool isWindowClosed, bool isSeen, bool hasLeftItsWindow, bool hasNoWindowToBeIn)
    {
        var created = thread.CreatedAt is { } at ? at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "?";
        var placement = hasLeftItsWindow
            ? "its window moved on to another thread: closed (the app-server daemon may hold it a little longer)"
            : hasNoWindowToBeIn
            ? "no Codex window is open, so nothing can be showing it: closed"
            : isWindowClosed
            ? $"its window {thread.LastWindow?.Pid} has exited: closed (the app-server daemon still holds the thread and lets go of it about a minute later)"
            : holder == 0
            ? "no Codex process holds its rollout: closed"
            : isDaemonHosted
                ? window is { } pid
                    ? isSeen
                        ? $"held by the app-server daemon {holder}; window {pid}, whose screen shows this thread"
                        : isResumed
                        ? $"held by the app-server daemon {holder}; window {pid}, whose command line resumes this thread"
                        : $"held by the app-server daemon {holder}; window {pid}, which started within {CodexWindows.STARTUP_MATCH.TotalSeconds:0} s before the thread (created {created})"
                    : $"held by the app-server daemon {holder}; no window started within {CodexWindows.STARTUP_MATCH.TotalSeconds:0} s before the thread (created {created}) or resumes it by name (so /new or /resume inside a window, or the resume picker), so it cannot be raised or closed from here"
                : $"held by its window {holder}";
        if (placement != thread.Placement)
        {
            thread.Placement = placement;
            AppLog.Info("agent", $"codex {NameOf(thread)} ({thread.Id ?? "?"}): {placement}");
        }
    }

    private void ReadThreadNames()
    {
        try
        {
            var write = File.GetLastWriteTimeUtc(indexPath);
            if (write == indexWrite || !File.Exists(indexPath))
            {
                return;
            }

            indexWrite = write;
            using var reader = new StreamReader(Transcripts.JsonlTail.OpenShared(indexPath));
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = System.Text.Json.JsonDocument.Parse(line);
                    var id = Transcripts.Json.Str(document.RootElement, "id");
                    var name = Transcripts.Json.Str(document.RootElement, "thread_name");
                    if (id is not null && !string.IsNullOrWhiteSpace(name))
                    {
                        threadNames[id] = name;
                    }
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string NameOf(CodexThread thread)
    {
        if (Transcripts.BridgeRules.AstraTitle(thread.Tracker.FirstPrompt) is { } astra)
        {
            return astra;
        }

        if (thread.Id is not null && threadNames.TryGetValue(thread.Id, out var threadName))
        {
            return threadName;
        }

        if (thread.Nickname is not null)
        {
            return thread.Nickname;
        }

        if (thread.Tracker.FirstPrompt is { Length: > 0 } prompt)
        {
            return Transcripts.Json.OneLine(prompt, 60);
        }

        var folder = thread.Tracker.Cwd is null ? "codex" : Path.GetFileName(thread.Tracker.Cwd.TrimEnd('\\', '/')).ToLowerInvariant();
        var id = thread.Id ?? "????";
        return folder + "-" + id[^4..];
    }

    private static string Surface(string? originator)
    {
        var value = (originator ?? "").ToLowerInvariant();
        return value switch
        {
            _ when value.Contains("vscode") => "VS CODE",
            _ when value.Contains("desktop") || value.Contains("app") => "DESKTOP",
            _ when value.Contains("exec") => "EXEC",
            _ => "CLI",
        };
    }
}
