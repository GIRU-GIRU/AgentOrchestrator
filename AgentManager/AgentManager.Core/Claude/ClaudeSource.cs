using System.Text.Json;
using System.Text.RegularExpressions;
using AgentManager.Core.Chat;
using AgentManager.Core.Model;
using AgentManager.Core.State;
using AgentManager.Core.Transcripts;
using AgentManager.Core.Windows;

namespace AgentManager.Core.Claude;

/// <summary>
/// Claude Code sessions on this machine: running ones from the session registry, recently closed ones from their
/// transcripts. Works the same for the CLI, the desktop app and the IDE extensions, which all write the same files.
/// </summary>
public sealed partial class ClaudeSource
{
    public const string KEY_PREFIX = "claude:";

    public const string SUBAGENT_PREFIX = "claude-sub:";

    private static readonly TimeSpan DISCOVERY_PERIOD = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan SUBAGENT_SCAN_PERIOD = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan STALE_SUBAGENT = TimeSpan.FromMinutes(30);

    private const int USAGE_POINTS = 90;

    private readonly string projectsDir;

    private readonly string sessionsDir;

    private readonly Dictionary<string, ClaudeSession> sessions = [];

    private readonly Dictionary<string, ChatOnlyFile> chatFiles = [];

    private readonly Dictionary<string, string> addressKeys = [];

    private readonly Dictionary<string, string> addressNames = [];

    private readonly Dictionary<string, string> nameKeys = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset nextDiscovery;

    private readonly Dictionary<(int Pid, long Start), string> processSessions = [];

    private readonly List<(string NewKey, string OldKey)> replacements = [];

    public ClaudeSource(string claudeDir)
    {
        projectsDir = Path.Combine(claudeDir, "projects");
        sessionsDir = Path.Combine(claudeDir, "sessions");
    }

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex SessionIdPattern();

    public void Poll(ProcessTable processes, ChatLog chat, ManagerStore store, DateTimeOffset now)
    {
        var live = ClaudeRegistryEntry.Read(sessionsDir)
            .Where(e => processes.IsAlive(e.Pid, e.ProcessStart))
            .GroupBy(e => e.SessionId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.StatusAt).First());

        foreach (var entry in live.Values)
        {
            var process = (entry.Pid, entry.ProcessStart);
            if (processSessions.TryGetValue(process, out var previous) && previous != entry.SessionId)
            {
                replacements.Add((KEY_PREFIX + entry.SessionId, KEY_PREFIX + previous));
            }

            processSessions[process] = entry.SessionId;
        }

        foreach (var process in processSessions.Keys.Where(p => !processes.IsAlive(p.Pid, p.Start)).ToList())
        {
            processSessions.Remove(process);
        }

        foreach (var entry in live.Values)
        {
            var key = KEY_PREFIX + entry.SessionId;
            if (entry.SocketPath is not null)
            {
                addressKeys["uds:" + entry.SocketPath] = key;
            }

            if (entry.Name is not null)
            {
                nameKeys[entry.Name] = key;
            }

            if (!sessions.ContainsKey(entry.SessionId))
            {
                Track(entry.SessionId, FindTranscript(entry.SessionId, entry.Cwd));
            }
        }

        if (now >= nextDiscovery)
        {
            nextDiscovery = now + DISCOVERY_PERIOD;
            Discover(store, now);
        }

        foreach (var session in sessions.Values)
        {
            session.Live = live.GetValueOrDefault(session.SessionId);
            if (session.Live is { } running)
            {
                session.Shell = Launcher.ShellOf(running.Pid, processes) ?? session.Shell;
            }
            ReadSession(session, chat);
            if (now >= session.NextSubagentScan)
            {
                var isCurrent = session.Live is not null || session.Tracker.LastEventAt > now - TimeSpan.FromMinutes(10);
                session.NextSubagentScan = now + (isCurrent ? SUBAGENT_SCAN_PERIOD : TimeSpan.FromMinutes(2));
                ScanSubagents(session, chat);
            }
        }

        foreach (var file in chatFiles.Values)
        {
            foreach (var line in file.Tail.ReadNewLines())
            {
                ClaudeLines.Apply(line, file.Prefix + file.Tail.Offset, null, file.Context, chat);
            }
        }
    }

    public IEnumerable<AgentSnapshot> Snapshots(DateTimeOffset now)
    {
        foreach (var session in sessions.Values)
        {
            var tracker = session.Tracker;
            var live = session.Live;
            var key = KEY_PREFIX + session.SessionId;
            if (live is null && tracker.FirstEventAt is null)
            {
                continue;
            }

            var isWaiting = live?.Status == "waiting";
            var isProcessBusy = live?.Status == "busy";
            var isBusy = live is null ? tracker.TurnOpen : isProcessBusy && !tracker.HasTurnEndedForNow;
            var needsSince = isWaiting || live?.Status == "idle" ? live!.StatusAt : tracker.TurnEndedAt;
            yield return new AgentSnapshot
            {
                Key = key,
                Kind = AgentKind.Claude,
                SessionId = session.SessionId,
                Name = SessionName(session),
                Title = tracker.LastPromptText ?? tracker.FirstPrompt,
                Cwd = live?.Cwd ?? tracker.Cwd,
                Model = PrettyModel(tracker.Model),
                Effort = tracker.Effort,
                Surface = live?.Surface ?? "CLI",
                Pid = live?.Pid,
                Shell = session.Shell,
                IsAlive = live is not null,
                IsWorking = isBusy,
                IsProcessBusy = isProcessBusy,
                HasPrompt = tracker.LastPromptAt is not null,
                WaitingFor = isWaiting ? live!.WaitingFor ?? "input" : null,
                NeedsInputSince = needsSince,
                StartedAt = live?.StartedAt ?? tracker.FirstEventAt ?? now,
                FirstEventAt = tracker.FirstEventAt,
                LastEventAt = Max(tracker.LastEventAt, live?.StatusAt) ?? now,
                LastPromptAt = tracker.LastPromptAt,
                Prompts = tracker.RecentPrompts,
                ListeningOn = live is null ? null : tracker.ListeningSince(live.StartedAt ?? DateTimeOffset.MinValue),
                IsWaitingOnBridge = isBusy && tracker.IsWaitingOnBridge,
                RunningCommands = live is null ? 0 : tracker.RunningCommandsSince(live.StartedAt ?? DateTimeOffset.MinValue),
                RunningServers = live is null ? 0 : tracker.RunningServersSince(live.StartedAt ?? DateTimeOffset.MinValue),
                BridgeChannels = tracker.BridgeChannels,
                Tokens = tracker.Tokens,
                SubagentTokens = session.Subagents.Values.Sum(s => s.Tracker.TotalTokens),
                Split = tracker.Split,
                Spark = tracker.Spark(now),
                UsageProfile = tracker.UsageProfile(live is null ? tracker.LastEventAt ?? now : now, USAGE_POINTS),
                CurrentTool = isBusy ? tracker.CurrentTool : null,
                RecentTools = tracker.RecentTools,
                LastText = tracker.LastText,
                LastTextAt = tracker.LastTextAt,
                AsksQuestion = tracker.QuestionAt is not null && tracker.QuestionAt >= tracker.LastPromptAt,
                QuestionText = tracker.QuestionText,
                QuestionAt = tracker.QuestionAt,
                PendingQuestion = isWaiting || tracker.PendingQuestion,
                Subagents = session.Subagents.Values
                    .Select(s => s.Info with { IsRunning = s.Info.IsRunning && live is not null && s.Info.LastActivity > now - STALE_SUBAGENT })
                    .OrderByDescending(s => s.LastActivity)
                    .ToArray(),
                TranscriptPath = session.Tail.Path,
                ResumeCommand = "claude --resume " + session.SessionId,
            };
        }
    }

    public void CollectNames(IDictionary<string, string> names)
    {
        foreach (var session in sessions.Values)
        {
            names[KEY_PREFIX + session.SessionId] = SessionName(session);

            foreach (var subagent in session.Subagents.Values)
            {
                names[SUBAGENT_PREFIX + subagent.Id] = subagent.DisplayName;
            }
        }
    }

    /// <summary>
    /// Sessions a running window switched away from since the last call (<c>/clear</c> starts a new session in the same
    /// process, <c>/resume</c> loads another): the card follows the window, so the old session hands its card over.
    /// </summary>
    public IReadOnlyList<(string NewKey, string OldKey)> TakeReplacements()
    {
        var taken = replacements.ToArray();
        replacements.Clear();
        return taken;
    }

    public void Forget(string sessionId)
    {
        sessions.Remove(sessionId);
    }

    private void Track(string sessionId, string transcript)
    {
        var session = new ClaudeSession(sessionId, new JsonlTail(transcript));
        sessions[sessionId] = session;
        chatFiles.Remove(transcript);
    }

    private void Discover(ManagerStore store, DateTimeOffset now)
    {
        if (!Directory.Exists(projectsDir))
        {
            return;
        }

        var settings = store.Settings;
        var trackSince = now - TimeSpan.FromHours(settings.HistoryHours);
        var chatSince = now - TimeSpan.FromDays(settings.ChatDays);
        foreach (var project in Directory.EnumerateDirectories(projectsDir))
        {
            foreach (var file in Directory.EnumerateFiles(project, "*.jsonl"))
            {
                var sessionId = Path.GetFileNameWithoutExtension(file);
                if (!SessionIdPattern().IsMatch(sessionId) || sessions.ContainsKey(sessionId))
                {
                    continue;
                }

                var written = File.GetLastWriteTime(file);
                var key = KEY_PREFIX + sessionId;
                var isKnown = store.IsKnown(key);
                if (store.IsDismissed(key, written))
                {
                    AddChatFile(file, key, sessionId, chatSince, written);
                    continue;
                }

                if (written >= trackSince || isKnown)
                {
                    Track(sessionId, file);
                    continue;
                }

                AddChatFile(file, key, sessionId, chatSince, written);
            }

            foreach (var dir in Directory.EnumerateDirectories(project))
            {
                var subagentsDir = Path.Combine(dir, "subagents");
                var parentId = Path.GetFileName(dir);
                if (!Directory.Exists(subagentsDir) || sessions.ContainsKey(parentId) || Directory.GetLastWriteTime(dir) < chatSince)
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(subagentsDir, "agent-*.jsonl"))
                {
                    var agentId = Path.GetFileNameWithoutExtension(file)["agent-".Length..];
                    AddChatFile(file, SUBAGENT_PREFIX + agentId, "subagent " + Short(agentId), chatSince, File.GetLastWriteTime(file));
                }
            }
        }
    }

    private void AddChatFile(string file, string writerKey, string writerName, DateTimeOffset chatSince, DateTime written)
    {
        if (written < chatSince || chatFiles.ContainsKey(file))
        {
            return;
        }

        var context = new ClaudeChatContext(writerKey, writerName, Resolve, Learn);
        chatFiles[file] = new ChatOnlyFile(new JsonlTail(file), context, "f" + (uint)file.GetHashCode() + ":");
    }

    private void ReadSession(ClaudeSession session, ChatLog chat)
    {
        if (!session.Tail.HasNewData())
        {
            return;
        }

        var name = SessionName(session);
        var context = new ClaudeChatContext(KEY_PREFIX + session.SessionId, name, address => ResolveFor(session, address), Learn);
        foreach (var line in session.Tail.ReadNewLines())
        {
            ClaudeLines.Apply(line, session.SessionId + ":" + session.Tail.Offset, session.Tracker, context, chat);
        }

        if (session.Tracker.Name is { } learned)
        {
            nameKeys[learned] = KEY_PREFIX + session.SessionId;
        }
    }

    private void ScanSubagents(ClaudeSession session, ChatLog chat)
    {
        var dir = Path.Combine(Path.GetDirectoryName(session.Tail.Path) ?? "", session.SessionId, "subagents");
        if (!Directory.Exists(dir))
        {
            return;
        }

        foreach (var meta in Directory.EnumerateFiles(dir, "agent-*.meta.json"))
        {
            var agentId = Path.GetFileName(meta)["agent-".Length..^".meta.json".Length];
            if (!session.Subagents.TryGetValue(agentId, out var subagent))
            {
                subagent = new ClaudeSubagent(agentId, new JsonlTail(Path.Combine(dir, "agent-" + agentId + ".jsonl")));
                session.Subagents[agentId] = subagent;
            }

            subagent.ReadMeta(meta);
            var context = new ClaudeChatContext(SUBAGENT_PREFIX + agentId, subagent.DisplayName, address => ResolveFor(session, address), Learn, true);
            foreach (var line in subagent.Tail.ReadNewLines())
            {
                ClaudeLines.Apply(line, agentId + ":" + subagent.Tail.Offset, subagent.Tracker, context, chat);
            }
        }
    }

    private (string Name, string? Key) ResolveFor(ClaudeSession session, string address)
    {
        if (session.Subagents.TryGetValue(address, out var subagent))
        {
            return (subagent.DisplayName, SUBAGENT_PREFIX + address);
        }

        return Resolve(address);
    }

    private (string Name, string? Key) Resolve(string address)
    {
        if (addressKeys.TryGetValue(address, out var key) || nameKeys.TryGetValue(address, out key))
        {
            return (addressNames.GetValueOrDefault(address) ?? address, key);
        }

        if (addressNames.TryGetValue(address, out var name))
        {
            return (name, null);
        }

        if (address.StartsWith("uds:", StringComparison.Ordinal))
        {
            return ("session " + address[^6..], null);
        }

        var isAgentId = address.Length is >= 16 and <= 24 && address.All(char.IsAsciiHexDigit);
        return isAgentId ? ("subagent " + Short(address), SUBAGENT_PREFIX + address) : (address, null);
    }

    private void Learn(string address, string name)
    {
        addressNames[address] = name;
        if (!addressKeys.ContainsKey(address) && nameKeys.TryGetValue(name, out var key))
        {
            addressKeys[address] = key;
        }
    }

    /// <summary>A session the user never spoke to has no transcript, and <c>claude --resume</c> refuses it.</summary>
    public bool HasTranscript(string sessionId, string? cwd)
    {
        return File.Exists(FindTranscript(sessionId, cwd));
    }

    private string FindTranscript(string sessionId, string? cwd)
    {
        if (cwd is not null)
        {
            var guess = Path.Combine(projectsDir, ProjectFolder(cwd), sessionId + ".jsonl");
            if (File.Exists(guess) || !Directory.Exists(projectsDir))
            {
                return guess;
            }
        }

        if (Directory.Exists(projectsDir))
        {
            foreach (var project in Directory.EnumerateDirectories(projectsDir))
            {
                var candidate = Path.Combine(project, sessionId + ".jsonl");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return Path.Combine(projectsDir, ProjectFolder(cwd ?? "unknown"), sessionId + ".jsonl");
    }

    internal static string ProjectFolder(string cwd)
    {
        return new string(cwd.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
    }

    internal static string PrettyModel(string? model)
    {
        if (string.IsNullOrEmpty(model))
        {
            return "";
        }

        var name = model.StartsWith("claude-", StringComparison.Ordinal) ? model["claude-".Length..] : model;
        var parts = name.Split('-');
        if (parts.Length >= 3 && int.TryParse(parts[1], out _) && int.TryParse(parts[2], out _))
        {
            return char.ToUpperInvariant(parts[0][0]) + parts[0][1..] + " " + parts[1] + "." + parts[2];
        }

        return name;
    }

    /// <summary>
    /// The name the user sees for the session in /resume and the desktop app: their own name (/rename, or a name given in
    /// the registry), else Claude's generated title, else the first prompt.
    /// </summary>
    private static string SessionName(ClaudeSession session)
    {
        var tracker = session.Tracker;
        var name = session.Live?.UserName ?? tracker.Name ?? tracker.Title ?? Json.OneLine(tracker.FirstPrompt, 60);
        return string.IsNullOrWhiteSpace(name) ? DerivedName(session) : name;
    }

    private static string DerivedName(ClaudeSession session)
    {
        var cwd = session.Live?.Cwd ?? session.Tracker.Cwd;
        var folder = cwd is null ? "claude" : Path.GetFileName(cwd.TrimEnd('\\', '/')).ToLowerInvariant();
        return folder + "-" + session.SessionId[..4];
    }

    private static string Short(string id)
    {
        return id.Length > 8 ? id[..8] : id;
    }

    private static DateTimeOffset? Max(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null)
        {
            return b;
        }

        return b is null || a > b ? a : b;
    }

    private sealed class ClaudeSession(string sessionId, JsonlTail tail)
    {
        public string SessionId { get; } = sessionId;

        public JsonlTail Tail { get; } = tail;

        public SessionTracker Tracker { get; } = new();

        public ClaudeRegistryEntry? Live { get; set; }

        public Dictionary<string, ClaudeSubagent> Subagents { get; } = [];

        public DateTimeOffset NextSubagentScan { get; set; }

        public (int Pid, long Start)? Shell { get; set; }
    }

    private sealed class ClaudeSubagent(string id, JsonlTail tail)
    {
        public string Id { get; } = id;

        public JsonlTail Tail { get; } = tail;

        public SessionTracker Tracker { get; } = new();

        public string Type { get; private set; } = "agent";

        public string Description { get; private set; } = "";

        public bool IsFinished { get; private set; }

        public string DisplayName => Tracker.Name ?? (Description.Length > 0 ? Description : Type + " " + Short(Id));

        public SubagentInfo Info => new(Id, Type, Description, !IsFinished, Tracker.LastEventAt, Tracker.TotalTokens);

        public void ReadMeta(string path)
        {
            try
            {
                using var stream = JsonlTail.OpenShared(path);
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                Type = root.Str("agentType") ?? Type;
                Description = root.Str("description") ?? Description;
                IsFinished = root.Flag("finished");
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record ChatOnlyFile(JsonlTail Tail, ClaudeChatContext Context, string Prefix);
}
