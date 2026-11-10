using System.Text.RegularExpressions;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.Transcripts;
using Microsoft.Data.Sqlite;

namespace AgentManager.Core.Orchestration;

/// <summary>A prompt that sends an agent to read an inbox: the channel, and the owner it reads as when it says (<c>--as name</c>).</summary>
public sealed record InboxPointer(string? Work, string? Reader);

public sealed record InboxBrief(DateTimeOffset? At, string Label, string Text);

public sealed record InboxRow(string Owner, DateTimeOffset? At, string Text);

/// <summary>
/// The brief an agent read from the orchestrator inbox. A window <c>astra.py</c> opens is told only "Run orch.cmd inbox ... and do what it
/// says", so its job is in <c>orch.db</c>. Rows stay there after they are read, and the database is opened read-only, as everywhere.
/// </summary>
public static partial class OrchestratorBriefs
{
    private const string BACKLOG_PREFIX = "channel:";

    private const string NAMED_OWNER = "agent:";

    private const string ASTRA_OWNER = NAMED_OWNER + "astra-";

    /// <summary>A longer prompt that mentions an inbox is a job of its own, not a pointer at one.</summary>
    private const int POINTER_CHARS = 400;

    /// <summary>Enough to get past an agent's own replies, which are never addressed to it, and duplicates.</summary>
    private const int ROWS_READ = 200;

    [GeneratedRegex(@"--as\s+(?<name>[\w.-]+)")]
    private static partial Regex ReadsAs();

    public static InboxPointer? PointerIn(string prompt)
    {
        if (prompt.Length > POINTER_CHARS || BridgeRules.MentionedChannels(prompt).FirstOrDefault() is not { } work)
        {
            return null;
        }

        var reader = ReadsAs().Match(prompt);
        return new InboxPointer(work, reader.Success ? NAMED_OWNER + reader.Groups["name"].Value : null);
    }

    /// <summary>An owner that named itself has no transcript behind it; what was sent to it is all there is. Only an Astra says its channel.</summary>
    public static InboxPointer? PointerOfOwner(string owner)
    {
        if (owner.StartsWith(ASTRA_OWNER, StringComparison.Ordinal))
        {
            return new InboxPointer(owner[ASTRA_OWNER.Length..], owner);
        }

        return owner.StartsWith(NAMED_OWNER, StringComparison.Ordinal) ? new InboxPointer(null, owner) : null;
    }

    public static IReadOnlyList<InboxBrief> Read(string stateDir, InboxPointer pointer, int max)
    {
        var database = Path.Combine(stateDir, "orch.db");
        if (!File.Exists(database))
        {
            return [];
        }

        try
        {
            using var connection = Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT owner, created, text FROM inbox WHERE owner IN ($backlog, $reader) AND text LIKE 'Message %' ORDER BY id LIMIT $limit";
            command.Parameters.AddWithValue("$backlog", BACKLOG_PREFIX + pointer.Work);
            command.Parameters.AddWithValue("$reader", pointer.Reader ?? "");
            command.Parameters.AddWithValue("$limit", ROWS_READ);
            using var reader = command.ExecuteReader();
            var rows = new List<InboxRow>();
            while (reader.Read())
            {
                var created = reader.IsDBNull(1) ? 0 : reader.GetDouble(1);
                rows.Add(new InboxRow(reader.GetString(0), created > 0 ? Json.FromUnixSeconds(created) : null, reader.GetString(2)));
            }

            return Pick(rows, pointer, max);
        }
        catch (SqliteException exception)
        {
            AppLog.Warn("orch", $"{stateDir}: could not read a brief from orch.db: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// What was waiting on the channel when the agent joined is the brief, since a post to a channel nobody else has joined stays in its
    /// backlog. Failing that, the first message sent to it: everything after that is the conversation.
    /// </summary>
    internal static IReadOnlyList<InboxBrief> Pick(IEnumerable<InboxRow> rows, InboxPointer pointer, int max)
    {
        var messages = rows
            .Select(row => (row.Owner, row.At, Message: InboxParser.Parse(row.Text)))
            .Where(row => row.Message.Kind != InboxKind.System && row.Message.Body.Length > 0)
            .Where(row => row.Message.SenderOwner != pointer.Reader && (pointer.Work is null || row.Message.Work == pointer.Work))
            .ToList();
        var backlog = messages.Where(row => row.Owner == BACKLOG_PREFIX + pointer.Work).ToList();
        var chosen = backlog.Count > 0 ? backlog : messages.Take(1);
        return chosen
            .DistinctBy(row => row.Message.Body)
            .Take(max)
            .Select(row => new InboxBrief(row.At, Label(row.Message), row.Message.Body))
            .ToList();
    }

    private static string Label(InboxMessage message)
    {
        var sender = message.SenderName ?? message.SenderOwner ?? "?";
        return message.Work is null ? "brief from " + sender : $"brief from {sender} on #{message.Work}";
    }

    public static string? SessionOfOwner(string stateDir, string owner)
    {
        var database = Path.Combine(stateDir, "orch.db");
        if (!File.Exists(database))
        {
            return null;
        }

        try
        {
            using var connection = Open(database);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT session_id FROM owners WHERE owner = $owner";
            command.Parameters.AddWithValue("$owner", owner);
            return command.ExecuteScalar() as string;
        }
        catch (SqliteException exception)
        {
            AppLog.Warn("orch", $"{stateDir}: could not read an owner from orch.db: {exception.Message}");
            return null;
        }
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
