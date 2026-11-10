namespace AgentManager.Core.Model;

public sealed record TimeSplit(double Think, double Code, double Test, double Build, double Tools, double Lane)
{
    public static readonly TimeSplit Empty = new(0, 0, 0, 0, 0, 0);

    public double Total => Think + Code + Test + Build + Tools + Lane;

    public double this[Activity activity] => activity switch
    {
        Activity.Think => Think,
        Activity.Code => Code,
        Activity.Test => Test,
        Activity.Build => Build,
        Activity.Tools => Tools,
        _ => Lane,
    };

    public static TimeSplit From(IReadOnlyList<double> seconds)
    {
        return new TimeSplit(seconds[0], seconds[1], seconds[2], seconds[3], seconds[4], seconds[5]);
    }
}

public sealed record TokenTally(long Input, long Output, long CacheRead, long CacheWrite, long Context, long ContextWindow)
{
    public static readonly TokenTally Empty = new(0, 0, 0, 0, 0, 0);

    public long Total => Input + Output + CacheRead + CacheWrite;
}

public sealed record ToolCall(string Id, string Name, string Detail, Activity Activity, DateTimeOffset Start, DateTimeOffset? End, bool IsError, bool UsesEditor = false)
{
    public bool IsRunning => End is null;
}

public sealed record PromptLine(DateTimeOffset At, string Text);

public sealed record BridgePeer(string Name, bool IsAlive, string? Key = null);

/// <summary>An AI-bridge channel (orchestrator <c>msg --to &lt;work&gt;</c> / <c>inbox &lt;work&gt;</c>) an agent is on.</summary>
public sealed record BridgeInfo(
    string Channel,
    IReadOnlyList<BridgePeer> Peers,
    int Messages,
    DateTimeOffset? LastAt,
    string? LastFrom,
    bool IsListening,
    string? LastFromKey = null);

/// <summary>A trace on the grid between two cards on one bridge channel. <see cref="From"/> is the end that spoke last, so the pulse runs the way the words went.</summary>
public sealed record BridgeLink(string Channel, string From, string To, bool IsActive);

public sealed record SubagentInfo(string Id, string Type, string Description, bool IsRunning, DateTimeOffset? LastActivity, long Tokens);

public sealed record LaneInfo(
    string Owner,
    bool HoldsLane,
    string? HeldKind,
    string? HeldPurpose,
    DateTimeOffset? HeldSince,
    string? QueuedKind,
    int QueuePosition,
    DateTimeOffset? QueuedSince,
    bool Paused,
    double HeldSecondsToday,
    double WaitedSecondsToday,
    int GrantsToday,
    string? TaskTitle,
    string? TaskNext,
    bool TaskBlockedOnUser = false,
    string? QueuedBehindOwner = null,
    string? QueuedBehind = null,
    string? TaskBlockedOn = null,
    string? TaskWhy = null,
    DateTimeOffset? TaskUpdatedAt = null)
{
    public bool IsStuck => QueuedKind is not null || Paused;
}

public sealed record ChatMessage(
    string Id,
    DateTimeOffset At,
    ChatChannel Channel,
    string From,
    string To,
    string? FromKey,
    string? ToKey,
    string? Topic,
    string? Summary,
    string Text,
    int Recipients = 1);

public sealed record LaneEntry(string Owner, string Name, string Kind, string Purpose, DateTimeOffset Since, int Position);

public sealed record OrchestratorView(
    string StateDir,
    DateTimeOffset FeedAt,
    bool DaemonAlive,
    bool HoldOn,
    string HoldBy,
    string HoldReason,
    DateTimeOffset? HoldSince,
    LaneEntry? Lane,
    IReadOnlyList<LaneEntry> Queue,
    double BusySecondsToday,
    double ElapsedSecondsToday,
    int GrantsToday,
    int BuildsToday)
{
    /// <summary>The daemon rewrites the feed every 10 seconds, so a dead one leaves a feed that goes on saying it is alive.</summary>
    private static readonly TimeSpan DAEMON_SILENT_AFTER = TimeSpan.FromSeconds(60);

    public bool IsDaemonUp(DateTimeOffset now)
    {
        return DaemonAlive && now - FeedAt < DAEMON_SILENT_AFTER;
    }
}

public sealed record HubSnapshot(
    DateTimeOffset At,
    bool IsLoading,
    IReadOnlyList<AgentSnapshot> Agents,
    IReadOnlyList<ChatMessage> Chat,
    IReadOnlyList<OrchestratorView> Orchestrators,
    string? Error,
    StatsPeriod? Stats = null)
{
    public static readonly HubSnapshot Loading = new(DateTimeOffset.Now, true, [], [], [], null);
}
