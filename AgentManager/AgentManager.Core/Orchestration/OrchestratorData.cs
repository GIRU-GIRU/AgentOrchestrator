using System.Text.Json;
using System.Text.RegularExpressions;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Orchestration;

public sealed record GrantRow(
    string Id,
    string Owner,
    string Kind,
    bool IsLane,
    string Purpose,
    bool IsImplicit,
    string State,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? AcquiredAt,
    DateTimeOffset? ReleasedAt);

public sealed record OwnerRow(string Owner, string Kind, int Pid, string SessionId, string Title, string Cwd);

public sealed record StatusGrant(string Id, string Owner, string Title, string SessionId, string Kind, string State, string Purpose, string Lane, DateTimeOffset? QueuedAt, DateTimeOffset? AcquiredAt, int Position);

public sealed record StatusAgent(string Owner, string Kind, int Pid, string SessionId, string Title, string Cwd, string State, bool WaitingOnUser, string OpenTool, bool HoldsLane, bool Paused);

public sealed record StatusTask(string Id, string Owner, string Title, string State, string NextStep, string BlockedOn, DateTimeOffset? Updated, string Why = "");

/// <summary>
/// The orchestrator's live feed, <c>Saved/Orchestration/orch-status.json</c>. Its field names are a contract with
/// <c>write_status</c> in orch.py (and the editor panel's parser): a renamed field reads as its default here.
/// </summary>
public sealed record StatusFeed(
    DateTimeOffset GeneratedAt,
    bool DaemonAlive,
    bool HoldOn,
    string HoldBy,
    string HoldReason,
    DateTimeOffset? HoldSince,
    IReadOnlyList<string> Paused,
    StatusGrant? Lane,
    IReadOnlyList<StatusGrant> Grants,
    IReadOnlyList<StatusGrant> Queue,
    IReadOnlyList<StatusAgent> Agents,
    IReadOnlyList<StatusTask> Tasks)
{
    public static StatusFeed Parse(JsonElement root)
    {
        var hold = root.Obj("hold");
        var lane = root.Obj("lane");
        return new StatusFeed(
            Seconds(root, "generatedAt") ?? DateTimeOffset.MinValue,
            root.Obj("daemon").Flag("alive"),
            hold.Flag("on"),
            hold.Str("by") ?? "",
            hold.Str("reason") ?? "",
            Seconds(hold, "since"),
            hold.Items("paused").Select(p => p.GetString() ?? "").ToArray(),
            lane.ValueKind == JsonValueKind.Object ? Grant(lane) : null,
            root.Items("grants").Select(Grant).ToArray(),
            root.Items("queue").Select(Grant).ToArray(),
            root.Items("agents").Select(Agent).ToArray(),
            root.Items("tasks").Select(Task).ToArray());
    }

    private static StatusGrant Grant(JsonElement g)
    {
        return new StatusGrant(g.Str("id") ?? "", g.Str("owner") ?? "", g.Str("title") ?? "", g.Str("sessionId") ?? "", g.Str("kind") ?? "", g.Str("state") ?? "",
            g.Str("purpose") ?? "", g.Str("lane") ?? "", Seconds(g, "queuedAt"), Seconds(g, "acquiredAt"), (int)g.Num("position"));
    }

    private static StatusAgent Agent(JsonElement a)
    {
        return new StatusAgent(a.Str("owner") ?? "", a.Str("kind") ?? "", (int)a.Num("pid"), a.Str("sessionId") ?? "", a.Str("title") ?? "", a.Str("cwd") ?? "",
            a.Str("state") ?? "", a.Flag("waitingOnUser"), a.Str("openTool") ?? "", a.Flag("holdsLane"), a.Flag("paused"));
    }

    private static StatusTask Task(JsonElement t)
    {
        return new StatusTask(t.Str("id") ?? "", t.Str("owner") ?? "", t.Str("title") ?? "", t.Str("state") ?? "", t.Str("nextStep") ?? "", t.Str("blockedOn") ?? "", Seconds(t, "updated"), t.Str("why") ?? "");
    }

    internal static DateTimeOffset? Seconds(JsonElement element, string property)
    {
        var value = element.Real(property);
        return value > 0 ? Json.FromUnixSeconds(value) : null;
    }
}

public enum InboxKind
{
    Direct,
    Channel,
    System,
}

public sealed record InboxMessage(InboxKind Kind, string? SenderName, string? SenderOwner, string? Work, string Body);

/// <summary>Reads the text <c>cmd_msg</c> and <c>notify</c> in orch.py put into an inbox row.</summary>
public static partial class InboxParser
{
    [GeneratedRegex(@"^Message on (?<work>\S+) from (?<name>.*?) ?\[(?<owner>[^\]]+)\]:\r?\n(?<body>.*)$", RegexOptions.Singleline)]
    private static partial Regex ChannelMessage();

    [GeneratedRegex(@"^Message from (?<name>.*?) ?\[(?<owner>[^\]]+)\]:\r?\n(?<body>.*)$", RegexOptions.Singleline)]
    private static partial Regex DirectMessage();

    public static InboxMessage Parse(string text)
    {
        var channel = ChannelMessage().Match(text);
        if (channel.Success)
        {
            return new InboxMessage(InboxKind.Channel, Name(channel), channel.Groups["owner"].Value, channel.Groups["work"].Value, StripReplyHint(channel.Groups["body"].Value));
        }

        var direct = DirectMessage().Match(text);
        if (direct.Success)
        {
            return new InboxMessage(InboxKind.Direct, Name(direct), direct.Groups["owner"].Value, null, StripReplyHint(direct.Groups["body"].Value));
        }

        return new InboxMessage(InboxKind.System, null, null, null, text.Trim());
    }

    private static string? Name(Match match)
    {
        var name = match.Groups["name"].Value.Trim();
        return name.Length == 0 ? null : name;
    }

    private static string StripReplyHint(string body)
    {
        var text = body.TrimEnd();
        var hint = text.LastIndexOf("\n(reply with:", StringComparison.Ordinal);
        return (hint >= 0 ? text[..hint] : text).Trim();
    }
}
