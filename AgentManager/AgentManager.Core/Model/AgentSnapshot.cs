namespace AgentManager.Core.Model;

public sealed record AgentSnapshot
{
    public required string Key { get; init; }

    public required AgentKind Kind { get; init; }

    public required string SessionId { get; init; }

    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? Cwd { get; init; }

    public string? Model { get; init; }

    public string? Effort { get; init; }

    public required string Surface { get; init; }

    public int? Pid { get; init; }

    /// <summary>The shell the agent ran in (pid, start time), remembered after the agent exits so its window can be closed.</summary>
    public (int Pid, long Start)? Shell { get; init; }

    public bool IsAlive { get; init; }

    /// <summary>The user chose Close window and the process has not gone yet; the card animates until it has, or until it refuses.</summary>
    public bool IsClosing { get; init; }

    public DateTimeOffset? ClosingSince { get; init; }

    public bool IsWorking { get; init; }

    /// <summary>
    /// The process's own status. Claude's registry stays busy after its turn ended while a background subagent runs, so this can be
    /// true with <see cref="IsWorking"/> false; its window then still counts as busy for anything typed into it.
    /// </summary>
    public bool IsProcessBusy { get; init; }

    public bool HasPrompt { get; init; }

    public string? WaitingFor { get; init; }

    public DateTimeOffset? NeedsInputSince { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    /// <summary>The transcript's first event. <see cref="StartedAt"/> is the process, which is much later for a resumed session.</summary>
    public DateTimeOffset? FirstEventAt { get; init; }

    public DateTimeOffset LastEventAt { get; init; }

    public DateTimeOffset? LastPromptAt { get; init; }

    public IReadOnlyList<PromptLine> Prompts { get; init; } = [];

    public TokenTally Tokens { get; init; } = TokenTally.Empty;

    public long SubagentTokens { get; init; }

    public TimeSplit Split { get; init; } = TimeSplit.Empty;

    public IReadOnlyList<long> Spark { get; init; } = [];

    public double TokenRate { get; init; }

    public IReadOnlyList<double> UsageProfile { get; init; } = [];

    public ToolCall? CurrentTool { get; init; }

    public IReadOnlyList<ToolCall> RecentTools { get; init; } = [];

    public string? LastText { get; init; }

    public DateTimeOffset? LastTextAt { get; init; }

    public bool AsksQuestion { get; init; }

    public string? QuestionText { get; init; }

    public DateTimeOffset? QuestionAt { get; init; }

    public bool PendingQuestion { get; init; }

    public IReadOnlyList<SubagentInfo> Subagents { get; init; } = [];

    public required string TranscriptPath { get; init; }

    public required string ResumeCommand { get; init; }

    public LaneInfo? Lane { get; init; }

    public string? ListeningOn { get; init; }

    /// <summary>Its current tool call is the wait on a bridge channel, so its turn is running but it is doing nothing.</summary>
    public bool IsWaitingOnBridge { get; init; }

    /// <summary>Background shell commands still running after their tool call returned (<c>run_in_background</c>).</summary>
    public int RunningCommands { get; init; }

    /// <summary>Background commands that serve a page until stopped. They are not work: an agent that ended its turn with one up has left it for the user to look at.</summary>
    public int RunningServers { get; init; }

    public IReadOnlyCollection<string> BridgeChannels { get; init; } = [];

    public IReadOnlyList<BridgeInfo> Bridges { get; init; } = [];

    public AgentState State { get; init; } = AgentState.Idle;

    public string StateReason { get; init; } = "";

    public int Number { get; init; }

    public bool Pinned { get; init; }

    public bool IsRead { get; init; }

    public bool IsMasterAgent { get; init; }

    public int RunningSubagents => Subagents.Count(s => s.IsRunning);

    public TimeSpan Age(DateTimeOffset now)
    {
        var end = IsAlive ? now : LastEventAt;
        return end > StartedAt ? end - StartedAt : TimeSpan.Zero;
    }
}
