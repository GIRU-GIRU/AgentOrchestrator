using AgentManager.Core.Claude;
using AgentManager.Core.Codex;
using AgentManager.Core.Model;
using AgentManager.Core.State;

namespace AgentManager.Core;

/// <summary>What the manager does about the Master Agent on this tick.</summary>
public enum MasterAgentStep
{
    None,
    StartFresh,
    Resume,
    Gone,
    NeverStarted,
}

/// <summary>
/// Where the prompt that makes an agent the Master Agent is, whether this copy of the manager may start him without being asked,
/// and the folder his Codex start scripts are written to (a prompt with spaces does not survive Windows Terminal and cmd on one command line).
/// </summary>
public sealed record MasterAgentSetup(string PromptPath, bool MayStartItself, string? ScriptFolder = null);

/// <summary>
/// The Master Agent is the one agent the manager keeps for the user to talk to about the orchestrator and the agents. There is
/// only ever one, a Claude or a Codex as the user picks.
/// Closing him gets rid of him until the user asks for a new one; a PC restart is not the user closing him, so he comes back with his conversation.
/// </summary>
public static class MasterAgent
{
    public const string NAME = "MASTER AGENT";

    public const string PROMPT_FOLDER = "MasterAgent";

    public const string PROMPT_FILE = "master-agent-prompt.md";

    /// <summary>The name Claude starts under. No space: it passes through Windows Terminal and cmd on the way to -n, and other sessions address him by it.</summary>
    public const string SESSION_NAME = "MASTER_AGENT";

    /// <summary>
    /// How a Codex Master Agent's first prompt starts. Codex takes no session id and opens no thread until its first prompt, so the manager
    /// starts him with this on the command line and knows his thread by it.
    /// </summary>
    public const string CODEX_START_MARK = "MASTER AGENT start:";

    /// <summary>Claude Code has no flag for the colour, and -n cannot carry a space, so the manager types these into his window, one per tick.</summary>
    public static readonly string[] STARTUP_COMMANDS = ["/color yellow", "/rename " + NAME];

    /// <summary>Codex has no <c>/color</c>.</summary>
    public static readonly string[] CODEX_STARTUP_COMMANDS = ["/rename " + NAME];

    private const string CODEX_SCRIPT_FILE = "start-codex.cmd";

    /// <summary>A window that refuses a command (no console to attach to) would otherwise be tried every tick.</summary>
    private static readonly TimeSpan STARTUP_RETRY = TimeSpan.FromMinutes(1);

    /// <summary>A thread's first event and the moment the manager asked for it come from two clocks a little apart.</summary>
    private static readonly TimeSpan START_SLACK = TimeSpan.FromSeconds(5);

    /// <summary>Long enough for a terminal and the agent to start and for the session to reach the registry or the thread to be written.</summary>
    public static readonly TimeSpan STARTING_WINDOW = TimeSpan.FromSeconds(60);

    /// <summary>A registry file read mid-write drops a session for one scan, and that must not read as the user closing him.</summary>
    public static readonly TimeSpan GONE_CONFIRM = TimeSpan.FromSeconds(5);

    /// <summary>A session under any of these is adopted as him (<see cref="RunningSession"/>), so a rename must not hand one of them to another agent.</summary>
    public static bool IsReservedName(string name)
    {
        return new[] { NAME, SESSION_NAME }.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static string KeyPrefix(AgentKind kind)
    {
        return kind == AgentKind.Codex ? CodexSource.KEY_PREFIX : ClaudeSource.KEY_PREFIX;
    }

    public static string? KeyOf(MasterAgentState state)
    {
        return state.SessionId is null ? null : KeyPrefix(state.Kind) + state.SessionId;
    }

    public static IReadOnlyList<string> StartupCommandsFor(AgentKind kind)
    {
        return kind == AgentKind.Codex ? CODEX_STARTUP_COMMANDS : STARTUP_COMMANDS;
    }

    /// <summary>A screenshot or a run on a scratch data folder must not open a real Master Agent window.</summary>
    public static bool MayStartItself(bool isScreenshot, bool hasScratchData, bool isTestAsking)
    {
        return !isScreenshot && (!hasScratchData || isTestAsking);
    }

    /// <summary>
    /// His own session if it is running, else the Codex thread the manager just started for him, else a live session named for him:
    /// state lost or another copy of the manager started one, and starting a second would break the one rule he has. Only his own kind counts.
    /// </summary>
    public static string? RunningSession(MasterAgentState state, IEnumerable<AgentSnapshot> agents)
    {
        var live = agents.Where(a => a.Kind == state.Kind && a.IsAlive).ToList();
        var own = state.SessionId is null ? null : live.FirstOrDefault(a => a.SessionId == state.SessionId);
        var started = state is { Kind: AgentKind.Codex, StartingAt: { } at } ? live.FirstOrDefault(a => IsCodexStart(a, at)) : null;
        var named = live.FirstOrDefault(a => a.Name is NAME or SESSION_NAME);
        return (own ?? started ?? named)?.SessionId;
    }

    /// <summary>A thread opened since the manager asked, whose first prompt is the one it started him with.</summary>
    public static bool IsCodexStart(AgentSnapshot agent, DateTimeOffset askedAt)
    {
        var opensWithTheMark = agent.Prompts.Count > 0 && agent.Prompts[0].Text.StartsWith(CODEX_START_MARK, StringComparison.Ordinal);
        return agent.Kind == AgentKind.Codex && opensWithTheMark && (agent.FirstEventAt ?? agent.StartedAt) >= askedAt - START_SLACK;
    }

    /// <summary>
    /// The next command his window has not had, however old the session, but only with him at his prompt: the keys would answer a dialog,
    /// or queue as a message while a turn or a background subagent runs. A session is named once, so each is typed once per session.
    /// </summary>
    public static string? NextStartupCommand(MasterAgentState state, AgentSnapshot agent, DateTimeOffset now)
    {
        var isAtItsPrompt = agent is { IsAlive: true, IsWorking: false, IsProcessBusy: false, PendingQuestion: false, WaitingFor: null };
        if (!isAtItsPrompt || now < state.NextStartupTryAt)
        {
            return null;
        }

        IReadOnlyList<string> typed = state.StartupSessionId == agent.SessionId ? state.TypedStartupCommands : [];
        return StartupCommandsFor(agent.Kind).FirstOrDefault(command => !typed.Contains(command));
    }

    public static void StartupCommandTyped(MasterAgentState state, string? sessionId, string command)
    {
        if (state.StartupSessionId != sessionId)
        {
            state.StartupSessionId = sessionId;
            state.TypedStartupCommands = [];
        }

        state.TypedStartupCommands.Add(command);
        state.LastStartupFailure = null;
    }

    /// <summary>Holds the next try back, and says whether this failure is news: a window with no console fails the same way every time.</summary>
    public static bool StartupCommandFailed(MasterAgentState state, string how, DateTimeOffset now)
    {
        var isNews = state.LastStartupFailure != how;
        state.LastStartupFailure = how;
        state.NextStartupTryAt = now + STARTUP_RETRY;
        return isNews;
    }

    /// <summary>
    /// A start in progress is checked before a missing session: a Codex start has no session until his thread appears, and asking for
    /// another then would open a second window.
    /// </summary>
    public static MasterAgentStep Next(MasterAgentState state, string? runningSession, DateTimeOffset? logonAt, DateTimeOffset now)
    {
        if (runningSession is not null)
        {
            state.SessionId = runningSession;
            state.IsWanted = true;
            state.StartingAt = null;
            state.IsResuming = false;
            state.MissingSince = null;
            state.LogonAt = logonAt;
            return MasterAgentStep.None;
        }

        if (!state.IsWanted)
        {
            return MasterAgentStep.None;
        }

        if (state.StartingAt is { } starting)
        {
            if (now - starting < STARTING_WINDOW)
            {
                return MasterAgentStep.None;
            }

            state.StartingAt = null;
            if (state.IsResuming)
            {
                state.IsResuming = false;
                return MasterAgentStep.StartFresh;
            }

            state.IsWanted = false;
            return MasterAgentStep.NeverStarted;
        }

        if (state.SessionId is null)
        {
            return MasterAgentStep.StartFresh;
        }

        state.MissingSince ??= now;
        if (now - state.MissingSince < GONE_CONFIRM)
        {
            return MasterAgentStep.None;
        }

        state.MissingSince = null;
        if (SessionRestore.IsEarlierLogon(state.LogonAt, logonAt))
        {
            return MasterAgentStep.Resume;
        }

        state.IsWanted = false;
        return MasterAgentStep.Gone;
    }

    /// <summary>
    /// Whether the menu may start a new one. He is gone only once <see cref="Next"/> has said so: a wanted Master Agent with a
    /// session, or one on his way, is running, starting, coming back after a restart or missing for one scan, and a start then would be a second.
    /// </summary>
    public static bool CanStartNew(MasterAgentState state, bool isRunning)
    {
        var isGone = !state.IsWanted || state is { SessionId: null, StartingAt: null };
        return !isRunning && isGone;
    }

    public static bool IsStarting(MasterAgentState state, DateTimeOffset now)
    {
        return state.StartingAt is { } starting && now - starting < STARTING_WINDOW;
    }

    /// <summary>A Claude's session id is chosen here and passed to it; a Codex's is not known until his thread appears, so it is null until then.</summary>
    public static void BeginFresh(MasterAgentState state, AgentKind kind, string? sessionId, string cwd, DateTimeOffset? logonAt, DateTimeOffset now)
    {
        state.Kind = kind;
        state.SessionId = sessionId;
        state.Cwd = cwd;
        state.IsWanted = true;
        state.IsResuming = false;
        state.StartingAt = now;
        state.MissingSince = null;
        state.LogonAt = logonAt;
    }

    public static void BeginResume(MasterAgentState state, DateTimeOffset? logonAt, DateTimeOffset now)
    {
        state.IsResuming = true;
        state.StartingAt = now;
        state.MissingSince = null;
        state.LogonAt = logonAt;
    }

    /// <summary>/clear or /resume in his window starts another session in the same process, and he stays the Master Agent.</summary>
    public static void HandOn(MasterAgentState state, string oldKey, string newKey)
    {
        var prefix = KeyPrefix(state.Kind);
        if (KeyOf(state) == oldKey && newKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            state.SessionId = newKey[prefix.Length..];
        }
    }

    public static string FreshCommand(string agentCommand, string sessionId, string promptPath)
    {
        return $"{agentCommand} --session-id {sessionId} -n {SESSION_NAME} --append-system-prompt-file {Argument(promptPath)}";
    }

    public static string ResumeCommand(string agentCommand, string sessionId, string promptPath)
    {
        return $"{agentCommand} --resume {sessionId} --append-system-prompt-file {Argument(promptPath)}";
    }

    /// <summary>
    /// A Codex has no system-prompt flag, so his role reaches him twice: as developer instructions, which last for the window and survive
    /// <c>/new</c>, and as his first prompt, which opens his thread at once and marks it his (<see cref="CODEX_START_MARK"/>). The value of
    /// <c>-c</c> is not TOML, so Codex takes it as a plain string.
    /// </summary>
    public static string CodexFreshScript(string agentCommand, string promptPath)
    {
        return $"@echo off\r\n{agentCommand} -c \"developer_instructions={CodexInstructions(promptPath)}\" \"{CODEX_START_MARK} read {promptPath} and follow it.\"\r\n";
    }

    public static string CodexResumeScript(string agentCommand, string threadId, string promptPath)
    {
        return $"@echo off\r\n{agentCommand} resume -c \"developer_instructions={CodexInstructions(promptPath)}\" {threadId}\r\n";
    }

    public static string CodexScriptPath(string folder)
    {
        return Path.Combine(folder, CODEX_SCRIPT_FILE);
    }

    private static string CodexInstructions(string promptPath)
    {
        return $"You are the {NAME}. Your role is written in {promptPath}: read it before anything else and follow it.";
    }

    private static string Argument(string value)
    {
        return value.Contains(' ') ? "\"" + value + "\"" : value;
    }
}
