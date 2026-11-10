using System.Globalization;
using System.Text.Json;
using AgentManager.Core.Chat;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;
using Microsoft.Data.Sqlite;

namespace AgentManager.Core.Orchestration;

/// <summary>
/// One orchestrator, read-only: its status feed for the live lane, queue, hold and agents, and its database for the
/// inbox (the agents' messages), the grant history and the owners. Nothing here writes to either.
/// </summary>
public sealed class OrchestratorReader
{
    public const string OWNER_PREFIX = "orch:";

    private const string CHANNEL_BACKLOG_PREFIX = "channel:";

    private const string EXCLUSIVE = "X";

    private static readonly TimeSpan INBOX_PERIOD = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan GRANTS_PERIOD = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan OWNERS_PERIOD = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan GRANT_WINDOW = TimeSpan.FromHours(26);

    private readonly string statusPath;

    private readonly string connectionString;

    private DateTime statusWrite;

    private long lastInboxId = -1;

    private DateTimeOffset nextInbox;

    private DateTimeOffset nextGrants;

    private DateTimeOffset nextOwners;

    private IReadOnlyList<GrantRow> grants = [];

    private IReadOnlyList<StatusTask> openTasks = [];

    public OrchestratorReader(string stateDir)
    {
        StateDir = stateDir;
        statusPath = Path.Combine(stateDir, "orch-status.json");
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(stateDir, "orch.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
    }

    public string StateDir { get; }

    public StatusFeed? Feed { get; private set; }

    public Dictionary<string, OwnerRow> Owners { get; } = [];

    /// <summary>Bridge channels and who is on each (meta <c>channel_members:&lt;work&gt;</c>, written by <c>join_channel</c>).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Channels { get; private set; } = new Dictionary<string, IReadOnlyList<string>>();

    public string? Error { get; private set; }

    public void Poll(ChatLog chat, int chatDays, Func<string, string> nameOf, DateTimeOffset now)
    {
        ReadFeed();
        if (!File.Exists(Path.Combine(StateDir, "orch.db")))
        {
            return;
        }

        try
        {
            if (now >= nextOwners)
            {
                nextOwners = now + OWNERS_PERIOD;
                ReadOwners();
            }

            if (now >= nextInbox)
            {
                nextInbox = now + INBOX_PERIOD;
                ReadInbox(chat, chatDays, nameOf, now);
                openTasks = ReadOpenTasks();
            }

            if (now >= nextGrants)
            {
                nextGrants = now + GRANTS_PERIOD;
                grants = ReadGrants(now);
                Channels = ReadChannels();
            }

            Error = null;
        }
        catch (SqliteException exception)
        {
            if (Error != exception.Message)
            {
                AppLog.Warn("orch", $"{StateDir}: orch.db read failed: {exception.Message}");
            }

            Error = exception.Message;
        }
    }

    public string OwnerName(string owner)
    {
        var agent = Feed?.Agents.FirstOrDefault(a => a.Owner == owner);
        var title = agent?.Title ?? Owners.GetValueOrDefault(owner)?.Title;
        if (!string.IsNullOrWhiteSpace(title))
        {
            return Json.OneLine(title, 28);
        }

        var parts = owner.Split(':');
        return parts.Length >= 2 ? parts[0] + " " + parts[1] : owner;
    }

    public OrchestratorView? View(DateTimeOffset now, Func<string, string> nameOf)
    {
        var feed = Feed;
        if (feed is null)
        {
            return null;
        }

        string OwnerNameOf(string owner) => nameOf(OWNER_PREFIX + owner);

        var day = LaneMetrics.Today(grants, now);
        var lane = feed.Lane is { } holder ? Entry(holder, OwnerNameOf, holder.AcquiredAt ?? now) : null;
        var queue = feed.Queue.Select(q => Entry(q, OwnerNameOf, q.QueuedAt ?? now)).OrderBy(q => q.Position).ToArray();
        return new OrchestratorView(
            StateDir,
            feed.GeneratedAt,
            feed.DaemonAlive,
            feed.HoldOn,
            feed.HoldBy,
            feed.HoldReason,
            feed.HoldSince,
            lane,
            queue,
            day.BusySeconds,
            day.ElapsedSeconds,
            day.Grants,
            day.Builds);
    }

    public LaneInfo LaneFor(string owner, DateTimeOffset now)
    {
        var feed = Feed;
        var day = LaneMetrics.Today(grants, now).Owners.GetValueOrDefault(owner);
        var lane = feed?.Lane is { } l && l.Owner == owner ? l : null;
        var held = lane ?? feed?.Grants.FirstOrDefault(g => g.Owner == owner && g.State == "active");
        var queued = feed?.Queue.Where(q => q.Owner == owner).OrderBy(q => q.Position).FirstOrDefault();
        var agent = feed?.Agents.FirstOrDefault(a => a.Owner == owner);
        var isPaused = feed is not null && feed.HoldOn && (agent?.Paused == true || feed.Paused.Contains(owner));
        var task = OpenTasksOf(owner).MaxBy(t => t.Updated);
        return new LaneInfo(
            owner,
            lane is not null,
            held?.Kind,
            held?.Purpose,
            held?.AcquiredAt,
            queued?.Kind,
            queued?.Position ?? 0,
            queued?.QueuedAt,
            isPaused,
            day?.HeldSeconds ?? 0,
            day?.WaitedSeconds ?? 0,
            day?.Grants ?? 0,
            task?.Title,
            string.IsNullOrWhiteSpace(task?.NextStep) ? null : task.NextStep,
            task is { State: "blocked", BlockedOn: "user" },
            queued is null || feed is null ? null : QueuedBehind(feed, queued),
            null,
            task?.State == "blocked" && task.BlockedOn.Length > 0 ? task.BlockedOn : null,
            string.IsNullOrWhiteSpace(task?.Why) ? null : task.Why,
            task?.Updated);
    }

    /// <summary>The feed carries only the newest tasks of any owner, so an older task of a long-lived agent is only in the database, which is read a moment behind the feed.</summary>
    private IEnumerable<StatusTask> OpenTasksOf(string owner)
    {
        var fromFeed = Feed?.Tasks ?? [];
        return fromFeed.Concat(openTasks.Where(t => fromFeed.All(f => f.Id != t.Id))).Where(t => t.Owner == owner && t.State is "in_progress" or "blocked");
    }

    /// <summary>Who a queue entry waits for: the entry ahead of it that wants what it wants, or at the head whoever holds that. A Deploy entry and a lane entry never wait on each other.</summary>
    private static string? QueuedBehind(StatusFeed feed, StatusGrant queued)
    {
        var ahead = feed.Queue.TakeWhile(q => q.Id != queued.Id).LastOrDefault(q => Conflicts(q, queued));
        if (ahead is not null)
        {
            return ahead.Owner;
        }

        var holders = feed.Lane is null ? feed.Grants : feed.Grants.Prepend(feed.Lane);
        return holders.FirstOrDefault(g => Conflicts(g, queued))?.Owner;
    }

    /// <summary><c>conflicts</c> in orch.py, from what the feed says: <c>lane</c> is X or S for a grant on the lane and empty for any other resource, which is one per kind.</summary>
    private static bool Conflicts(StatusGrant a, StatusGrant b)
    {
        var isOnTheLane = a.Lane.Length > 0 && b.Lane.Length > 0;
        return isOnTheLane ? a.Lane == EXCLUSIVE || b.Lane == EXCLUSIVE : a.Lane == b.Lane && a.Kind == b.Kind;
    }

    private static LaneEntry Entry(StatusGrant grant, Func<string, string> nameOf, DateTimeOffset since)
    {
        return new LaneEntry(grant.Owner, nameOf(grant.Owner), grant.Kind, grant.Purpose, since, grant.Position);
    }

    private void ReadFeed()
    {
        try
        {
            var write = File.GetLastWriteTimeUtc(statusPath);
            if (write == statusWrite || !File.Exists(statusPath))
            {
                return;
            }

            using var stream = JsonlTail.OpenShared(statusPath);
            using var document = JsonDocument.Parse(stream);
            Feed = StatusFeed.Parse(document.RootElement);
            statusWrite = write;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout=2000";
        timeout.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Claude sessions the orchestrator is relaunching headless right now (a task in <c>resuming</c>). Restoring one
    /// as well would run the same agent twice, so the restore panel leaves them out.
    /// </summary>
    public IReadOnlySet<string> RelaunchingSessions()
    {
        var sessions = new HashSet<string>();
        if (!File.Exists(Path.Combine(StateDir, "orch.db")))
        {
            return sessions;
        }

        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id FROM tasks WHERE state = 'resuming' AND session_id != ''";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                sessions.Add(Text(reader, 0));
            }
        }
        catch (SqliteException exception)
        {
            AppLog.Warn("orch", $"{StateDir}: could not read relaunching sessions: {exception.Message}");
        }

        return sessions;
    }

    private void ReadOwners()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT owner, kind, pid, session_id, title, cwd FROM owners";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new OwnerRow(Text(reader, 0), Text(reader, 1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2), Text(reader, 3), Text(reader, 4), Text(reader, 5));
            Owners[row.Owner] = row;
        }
    }

    private void ReadInbox(ChatLog chat, int chatDays, Func<string, string> nameOf, DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, owner, created, text FROM inbox WHERE id > $last AND created >= $since ORDER BY id LIMIT 5000";
        command.Parameters.AddWithValue("$last", lastInboxId);
        command.Parameters.AddWithValue("$since", (now - TimeSpan.FromDays(chatDays)).ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            lastInboxId = Math.Max(lastInboxId, id);
            var recipient = Text(reader, 1);
            var at = Json.FromUnixSeconds(reader.GetDouble(2));
            var message = InboxParser.Parse(Text(reader, 3));
            var isBacklog = recipient.StartsWith(CHANNEL_BACKLOG_PREFIX, StringComparison.Ordinal);
            var to = isBacklog ? "#" + recipient[CHANNEL_BACKLOG_PREFIX.Length..] : nameOf(OWNER_PREFIX + recipient);
            var toKey = isBacklog ? null : OWNER_PREFIX + recipient;
            var rowId = "o:" + StateDir.GetHashCode().ToString("x", CultureInfo.InvariantCulture) + ":" + id;
            if (message.Kind == InboxKind.System)
            {
                chat.Add(new ChatMessage(rowId, at, ChatChannel.System, "orchestrator", to, null, toKey, null, null, message.Body), false);
                continue;
            }

            var fromKey = OWNER_PREFIX + message.SenderOwner;
            var from = message.SenderName ?? nameOf(fromKey);
            chat.Add(new ChatMessage(rowId, at, ChatChannel.Orchestrator, from, to, fromKey, toKey, message.Work, null, message.Body), false);
        }

        if (lastInboxId < 0)
        {
            lastInboxId = 0;
        }
    }

    private List<StatusTask> ReadOpenTasks()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, owner, title, state, next_step, blocked_on, updated, why FROM tasks WHERE state IN ('in_progress', 'blocked')";
        using var reader = command.ExecuteReader();
        var tasks = new List<StatusTask>();
        while (reader.Read())
        {
            tasks.Add(new StatusTask(Text(reader, 0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Time(reader, 6), Text(reader, 7)));
        }

        return tasks;
    }

    private Dictionary<string, IReadOnlyList<string>> ReadChannels()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta WHERE key LIKE 'channel_members:%'";
        using var reader = command.ExecuteReader();
        var channels = new Dictionary<string, IReadOnlyList<string>>();
        while (reader.Read())
        {
            try
            {
                var members = JsonSerializer.Deserialize<List<string>>(Text(reader, 1)) ?? [];
                channels[Text(reader, 0)["channel_members:".Length..]] = members;
            }
            catch (JsonException)
            {
            }
        }

        return channels;
    }

    private IReadOnlyList<GrantRow> ReadGrants(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner, kind, resources, purpose, implicit, state, queued_at, acquired_at, released_at
            FROM grants
            WHERE queued_at >= $since OR acquired_at >= $since OR released_at >= $since OR released_at = 0
            """;
        command.Parameters.AddWithValue("$since", (now - GRANT_WINDOW).ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var rows = new List<GrantRow>();
        while (reader.Read())
        {
            rows.Add(new GrantRow(
                Text(reader, 0),
                Text(reader, 1),
                Text(reader, 2),
                Text(reader, 3).Contains("\"lane\"", StringComparison.Ordinal),
                Text(reader, 4),
                !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                Text(reader, 6),
                Time(reader, 7),
                Time(reader, 8),
                Time(reader, 9)));
        }

        return rows;
    }

    private static string Text(SqliteDataReader reader, int column)
    {
        return reader.IsDBNull(column) ? "" : reader.GetValue(column).ToString() ?? "";
    }

    private static DateTimeOffset? Time(SqliteDataReader reader, int column)
    {
        if (reader.IsDBNull(column))
        {
            return null;
        }

        var seconds = reader.GetDouble(column);
        return seconds > 0 ? Json.FromUnixSeconds(seconds) : null;
    }
}
