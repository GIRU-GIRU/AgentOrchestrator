using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Diagnostics;

/// <summary>
/// Turns consecutive hub snapshots into log events (agents appearing, leaving and changing state, new messages, lane and
/// hold changes, slow ticks) and writes the current snapshot to <c>live.json</c>, so an agent can follow the manager
/// without seeing its window.
/// </summary>
public sealed class HubJournal
{
    private const int PREVIEW_CHARS = 160;

    private static readonly TimeSpan SLOW_TICK = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan SLOW_TICK_REPEAT = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions JSON = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string? livePath;

    private readonly Dictionary<string, (AgentState State, string Reason, string Name, int Number)> agents = [];

    private readonly HashSet<string> loggedMessages = [];

    private readonly Dictionary<string, string> orchestratorLines = [];

    private bool hasObserved;

    private DateTimeOffset lastSlowTick;

    private string? lastError;

    public HubJournal(string? livePath)
    {
        this.livePath = livePath;
    }

    public void Observe(HubSnapshot snapshot, TimeSpan duration, IReadOnlyList<(string Phase, TimeSpan Time)> phases)
    {
        if (!hasObserved)
        {
            hasObserved = true;
            AppLog.Info("hub", $"first scan in {duration.TotalMilliseconds:0} ms: {snapshot.Agents.Count} agents ({snapshot.Agents.Count(a => a.IsAlive)} live), "
                + $"{snapshot.Chat.Count} messages, {snapshot.Orchestrators.Count} orchestrators ({string.Join(", ", snapshot.Orchestrators.Select(o => o.StateDir))})");
            foreach (var message in snapshot.Chat)
            {
                loggedMessages.Add(message.Id);
            }
        }
        else if (duration > SLOW_TICK && snapshot.At - lastSlowTick > SLOW_TICK_REPEAT)
        {
            lastSlowTick = snapshot.At;
            AppLog.Warn("hub", $"slow tick {duration.TotalMilliseconds:0} ms: " + string.Join(", ", phases.Select(p => $"{p.Phase} {p.Time.TotalMilliseconds:0}")));
        }

        ObserveAgents(snapshot);
        ObserveChat(snapshot);
        ObserveOrchestrators(snapshot);
        ObserveError(snapshot.Error);
        WriteLive(snapshot);
    }

    private void ObserveAgents(HubSnapshot snapshot)
    {
        var seen = new HashSet<string>();
        foreach (var agent in snapshot.Agents)
        {
            seen.Add(agent.Key);
            var current = (agent.State, agent.StateReason, agent.Name, agent.Number);
            if (!agents.TryGetValue(agent.Key, out var previous))
            {
                AppLog.Info("agent", $"+ #{agent.Number:00} {agent.Name} ({agent.Kind} {agent.Surface}, pid {agent.Pid?.ToString() ?? "-"}) {agent.State}: {agent.StateReason} · {agent.Cwd} · {agent.Key}");
            }
            else if (previous.State != agent.State || previous.Reason != agent.StateReason)
            {
                AppLog.Info("agent", $"#{agent.Number:00} {agent.Name}: {previous.State} → {agent.State} ({agent.StateReason})");
            }

            agents[agent.Key] = current;
        }

        foreach (var key in agents.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            var gone = agents[key];
            AppLog.Info("agent", $"- #{gone.Number:00} {gone.Name} left the grid (closed for good, hidden, or older than the history window)");
            agents.Remove(key);
        }
    }

    private void ObserveChat(HubSnapshot snapshot)
    {
        foreach (var message in snapshot.Chat)
        {
            if (loggedMessages.Add(message.Id))
            {
                var topic = message.Topic is null ? "" : " #" + message.Topic;
                AppLog.Info("chat", $"{message.Channel}{topic} {message.From} → {message.To}: {Json.OneLine(message.Summary ?? message.Text, PREVIEW_CHARS)}");
            }
        }
    }

    private void ObserveOrchestrators(HubSnapshot snapshot)
    {
        foreach (var view in snapshot.Orchestrators)
        {
            var lane = view.Lane is { } holder ? $"{holder.Kind} held by {holder.Name} ({holder.Purpose})" : "free";
            var queue = view.Queue.Count == 0 ? "empty" : string.Join(", ", view.Queue.Select(q => $"#{q.Position} {q.Name} {q.Kind}"));
            var hold = view.HoldOn ? $"on by {view.HoldBy}" : "off";
            var line = $"lane {lane} · queue {queue} · hold {hold} · daemon {(view.IsDaemonUp(snapshot.At) ? "alive" : "dead")}";
            if (orchestratorLines.GetValueOrDefault(view.StateDir) != line)
            {
                orchestratorLines[view.StateDir] = line;
                AppLog.Info("orch", $"{Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(view.StateDir.TrimEnd('\\', '/'))))}: {line}");
            }
        }
    }

    private void ObserveError(string? error)
    {
        if (error == lastError)
        {
            return;
        }

        lastError = error;
        if (error is null)
        {
            AppLog.Info("hub", "error cleared");
        }
        else
        {
            AppLog.Warn("hub", error);
        }
    }

    private void WriteLive(HubSnapshot snapshot)
    {
        if (livePath is null)
        {
            return;
        }

        var live = new
        {
            at = snapshot.At,
            log = AppLog.CurrentFile,
            error = snapshot.Error,
            agents = snapshot.Agents.Select(a => new
            {
                number = a.Number,
                name = a.Name,
                state = a.State,
                reason = a.StateReason,
                kind = a.Kind,
                surface = a.Surface,
                pid = a.Pid,
                isAlive = a.IsAlive,
                model = a.Model,
                effort = a.Effort,
                title = a.Title,
                cwd = a.Cwd,
                tool = a.CurrentTool is { } tool ? $"{tool.Name}: {tool.Detail}" : null,
                lastSaid = Json.OneLine(a.LastText, PREVIEW_CHARS),
                question = a.AsksQuestion || a.PendingQuestion ? a.QuestionText : null,
                tokens = a.Tokens.Total,
                context = a.Tokens.Context,
                subagentsRunning = a.RunningSubagents,
                startedAt = a.StartedAt,
                lastEventAt = a.LastEventAt,
                lane = a.Lane is { } lane ? new { lane.Owner, lane.HoldsLane, lane.HeldKind, lane.QueuedKind, lane.QueuePosition, lane.Paused, lane.TaskTitle } : null,
                listeningOn = a.ListeningOn,
                bridges = a.Bridges.Count == 0 ? null : a.Bridges.Select(b => new { b.Channel, peers = b.Peers.Select(p => p.Name), b.Messages, b.LastAt, b.LastFrom, b.IsListening }),
                key = a.Key,
                transcript = a.TranscriptPath,
                resume = a.ResumeCommand,
            }),
            bridgeLinks = BridgeRules.Links(snapshot.Agents, snapshot.At),
            orchestrators = snapshot.Orchestrators.Select(o => new
            {
                o.StateDir,
                lane = o.Lane,
                queue = o.Queue,
                hold = o.HoldOn ? o.HoldBy : null,
                o.DaemonAlive,
                daemonUp = o.IsDaemonUp(snapshot.At),
                o.GrantsToday,
                o.BuildsToday,
                busyPercent = o.ElapsedSecondsToday <= 0 ? 0 : Math.Round(o.BusySecondsToday * 100 / o.ElapsedSecondsToday),
            }),
            recentMessages = snapshot.Chat.TakeLast(15).Select(m => new { m.At, m.Channel, m.From, m.To, text = Json.OneLine(m.Text, PREVIEW_CHARS) }),
        };

        try
        {
            var temp = livePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(live, JSON));
            File.Move(temp, livePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
