namespace AgentManager.Core.Model;

public enum AgentKind
{
    Claude,
    Codex,
}

/// <summary>Grid colour of an agent. The order is the attention order: the grid shows the first values first.</summary>
public enum AgentState
{
    NeedsInput,
    Asked,
    LaneBlocked,
    UsingEditor,
    Working,
    Paused,
    Finished,
    Idle,
    Closed,
}

public enum Activity
{
    Think,
    Code,
    Test,
    Build,
    Tools,
    Lane,
}

public enum ChatChannel
{
    Peer,
    Subagent,
    Orchestrator,
    System,
}
