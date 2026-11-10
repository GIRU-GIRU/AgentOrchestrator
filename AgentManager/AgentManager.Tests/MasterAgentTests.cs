using AgentManager.Core;
using AgentManager.Core.Model;
using AgentManager.Core.State;

namespace AgentManager.Tests;

/// <summary>
/// The one agent the manager keeps for the user: closing him gets rid of him until New MASTER AGENT, a PC restart
/// brings him back with his conversation, and there is never a second.
/// </summary>
public sealed class MasterAgentTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset LOGON = NOW.AddHours(-3);

    private static readonly DateTimeOffset EARLIER_LOGON = NOW.AddDays(-1);

    private const string SESSION = "11111111-1111-1111-1111-111111111111";

    private static MasterAgentState Running(DateTimeOffset? logon = null)
    {
        return new MasterAgentState { SessionId = SESSION, Cwd = "C:/MyProject", LogonAt = logon ?? LOGON };
    }

    private static AgentSnapshot Agent(string sessionId, string name = "other", bool alive = true, AgentKind kind = AgentKind.Claude, string? cwd = "C:/Home")
    {
        return new AgentSnapshot
        {
            Key = "claude:" + sessionId,
            Kind = kind,
            SessionId = sessionId,
            Name = name,
            Surface = "CLI",
            Pid = alive ? 100 : null,
            IsAlive = alive,
            HasPrompt = true,
            Cwd = cwd,
            TranscriptPath = "a.jsonl",
            ResumeCommand = "claude --resume " + sessionId,
        };
    }

    /// <summary>Runs the rule past the confirmation, as the hub does each tick.</summary>
    private static MasterAgentStep AfterConfirm(MasterAgentState state, DateTimeOffset? logon)
    {
        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, logon, NOW));
        return MasterAgent.Next(state, null, logon, NOW + MasterAgent.GONE_CONFIRM);
    }

    [Fact]
    public void AFreshInstallWantsHimAndStartsHim()
    {
        var state = new MasterAgentState();

        Assert.True(state.IsWanted);
        Assert.Equal(MasterAgentStep.StartFresh, MasterAgent.Next(state, null, LOGON, NOW));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    public void AScreenshotOrAScratchDataFolderNeverOpensARealWindow(bool isScreenshot, bool hasScratchData, bool isTestAsking, bool expected)
    {
        Assert.Equal(expected, MasterAgent.MayStartItself(isScreenshot, hasScratchData, isTestAsking));
    }

    [Fact]
    public void RunningNeedsNothingAndRemembersTheLogon()
    {
        var state = Running(EARLIER_LOGON);
        state.StartingAt = NOW;

        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, SESSION, LOGON, NOW));
        Assert.Equal(LOGON, state.LogonAt);
        Assert.Null(state.StartingAt);
    }

    [Fact]
    public void GoneInTheSameLogonMeansTheUserClosedHim()
    {
        var state = Running();

        Assert.Equal(MasterAgentStep.Gone, AfterConfirm(state, LOGON));
        Assert.False(state.IsWanted);
    }

    [Fact]
    public void ClosedStaysClosedAcrossManagerRestartsAndPcRestarts()
    {
        var state = Running();
        state.IsWanted = false;

        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, LOGON, NOW));
        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, NOW, NOW.AddDays(2)));
    }

    [Fact]
    public void GoneAfterAnEarlierLogonMeansThePcRestartedSoHeComesBack()
    {
        var state = Running(EARLIER_LOGON);

        Assert.Equal(MasterAgentStep.Resume, AfterConfirm(state, LOGON));
        Assert.True(state.IsWanted);
    }

    [Fact]
    public void OneScanWithoutHimIsNotTheUserClosingHim()
    {
        var state = Running();

        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, LOGON, NOW));
        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, SESSION, LOGON, NOW.AddSeconds(1)));
        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, LOGON, NOW.AddSeconds(2)));
        Assert.True(state.IsWanted);
    }

    [Fact]
    public void AStartThatNeverAppearsGivesUpInsteadOfOpeningWindowsForever()
    {
        var state = Running();
        state.StartingAt = NOW;

        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, LOGON, NOW.AddSeconds(30)));
        Assert.Equal(MasterAgentStep.NeverStarted, MasterAgent.Next(state, null, LOGON, NOW + MasterAgent.STARTING_WINDOW));
        Assert.False(state.IsWanted);
    }

    [Fact]
    public void AResumeThatNeverAppearsFallsBackToAFreshOne()
    {
        var state = Running(EARLIER_LOGON);
        MasterAgent.BeginResume(state, LOGON, NOW);

        Assert.Equal(MasterAgentStep.StartFresh, MasterAgent.Next(state, null, LOGON, NOW + MasterAgent.STARTING_WINDOW));
        Assert.True(state.IsWanted);
    }

    [Fact]
    public void TheMenuCannotStartOneWhileHeIsRunning()
    {
        Assert.False(MasterAgent.CanStartNew(Running(), isRunning: true));
    }

    [Fact]
    public void ASessionAdoptedAfterHeWasClosedCountsAsRunningBeforeTheNextTick()
    {
        var state = Running();
        state.IsWanted = false;

        Assert.False(MasterAgent.CanStartNew(state, isRunning: true));
    }

    [Fact]
    public void ASecondClickWhileHeIsStartingStartsNothing()
    {
        var state = new MasterAgentState { IsWanted = false };
        Assert.True(MasterAgent.CanStartNew(state, isRunning: false));

        MasterAgent.BeginFresh(state, AgentKind.Claude, SESSION, "C:/MyProject", LOGON, NOW);

        Assert.False(MasterAgent.CanStartNew(state, isRunning: false));
        Assert.True(MasterAgent.IsStarting(state, NOW.AddSeconds(1)));
        Assert.False(MasterAgent.IsStarting(state, NOW + MasterAgent.STARTING_WINDOW));
    }

    [Fact]
    public void OneScanWithoutHimCannotStartASecond()
    {
        var state = Running();

        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, LOGON, NOW));

        Assert.False(MasterAgent.CanStartNew(state, isRunning: false));
    }

    [Fact]
    public void NothingCanStartWhileHeIsComingBackAfterAPcRestart()
    {
        var state = Running(EARLIER_LOGON);
        Assert.False(MasterAgent.CanStartNew(state, isRunning: false));

        Assert.Equal(MasterAgentStep.Resume, AfterConfirm(state, LOGON));
        Assert.False(MasterAgent.CanStartNew(state, isRunning: false));

        MasterAgent.BeginResume(state, LOGON, NOW);
        Assert.False(MasterAgent.CanStartNew(state, isRunning: false));
    }

    [Fact]
    public void TheMenuCanStartOneOnceHeIsGone()
    {
        var state = Running();

        Assert.Equal(MasterAgentStep.Gone, AfterConfirm(state, LOGON));

        Assert.True(MasterAgent.CanStartNew(state, isRunning: false));
    }

    [Fact]
    public void TheMenuCanStartOneWhenTheLastStartNeverAppeared()
    {
        var state = Running();
        state.StartingAt = NOW;
        Assert.False(MasterAgent.CanStartNew(state, isRunning: false));

        Assert.Equal(MasterAgentStep.NeverStarted, MasterAgent.Next(state, null, LOGON, NOW + MasterAgent.STARTING_WINDOW));

        Assert.True(MasterAgent.CanStartNew(state, isRunning: false));
    }

    [Fact]
    public void TheMenuCanStartTheFirstOneOnAFreshInstall()
    {
        Assert.True(MasterAgent.CanStartNew(new MasterAgentState(), isRunning: false));
    }

    [Fact]
    public void BeforeTheFirstScanTheMenuCannotStartOneEvenIfHeWasClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), "am-master-agent-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"MasterAgent":{"IsWanted":false}}""");
        try
        {
            var store = ManagerStore.Load(path);
            using var hub = new AgentHub(store, path + ".claude", path + ".codex");

            Assert.True(store.Read(data => MasterAgent.CanStartNew(data.MasterAgent, isRunning: false)));
            Assert.False(hub.CanStartMasterAgent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NewMasterAgentBringsHimBackInAFreshSession()
    {
        var state = Running();
        state.IsWanted = false;
        const string FRESH = "22222222-2222-2222-2222-222222222222";

        MasterAgent.BeginFresh(state, AgentKind.Claude, FRESH, "C:/MyProject", LOGON, NOW);

        Assert.True(state.IsWanted);
        Assert.Equal(FRESH, state.SessionId);
        Assert.Equal(NOW, state.StartingAt);
    }

    [Fact]
    public void ClearOrResumeInHisWindowKeepsHimTheMasterAgent()
    {
        var state = Running();
        const string NEXT = "22222222-2222-2222-2222-222222222222";

        MasterAgent.HandOn(state, "claude:" + SESSION, "claude:" + NEXT);

        Assert.Equal(NEXT, state.SessionId);
    }

    [Fact]
    public void AnotherWindowMovingOnLeavesHimAlone()
    {
        var state = Running();

        MasterAgent.HandOn(state, "claude:someone-else", "claude:someone-new");

        Assert.Equal(SESSION, state.SessionId);
    }

    [Fact]
    public void HisOwnSessionIsFoundBeforeOneThatOnlySharesHisName()
    {
        var agents = new[] { Agent("named", MasterAgent.SESSION_NAME), Agent(SESSION, "renamed by the user") };

        Assert.Equal(SESSION, MasterAgent.RunningSession(Running(), agents));
    }

    [Fact]
    public void ALiveSessionNamedForHimIsAdoptedInsteadOfStartingASecond()
    {
        var state = new MasterAgentState();
        var agents = new[] { Agent("named", MasterAgent.SESSION_NAME) };

        var running = MasterAgent.RunningSession(state, agents);
        var step = MasterAgent.Next(state, running, LOGON, NOW);

        Assert.Equal(MasterAgentStep.None, step);
        Assert.Equal("named", state.SessionId);
    }

    private static AgentSnapshot AtHisPrompt(DateTimeOffset? started = null)
    {
        return Agent(SESSION) with { StartedAt = started ?? NOW.AddDays(-2) };
    }

    [Fact]
    public void HisNameIsExactlyTheOneOnHisCard()
    {
        Assert.Equal(["/color yellow", "/rename MASTER AGENT"], MasterAgent.STARTUP_COMMANDS);
    }

    [Fact]
    public void AnOldSessionGetsEachCommandOnceAtItsPrompt()
    {
        var state = new MasterAgentState();

        foreach (var command in MasterAgent.STARTUP_COMMANDS)
        {
            Assert.Equal(command, MasterAgent.NextStartupCommand(state, AtHisPrompt(), NOW));
            MasterAgent.StartupCommandTyped(state, SESSION, command);
        }

        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt(), NOW));
        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt(), NOW.AddDays(30)));
    }

    [Fact]
    public void ANewSessionInHisWindowGetsThemAgain()
    {
        var state = new MasterAgentState();
        foreach (var command in MasterAgent.STARTUP_COMMANDS)
        {
            MasterAgent.StartupCommandTyped(state, SESSION, command);
        }

        var cleared = AtHisPrompt() with { SessionId = "cleared" };
        Assert.Equal(MasterAgent.STARTUP_COMMANDS[0], MasterAgent.NextStartupCommand(state, cleared, NOW));

        MasterAgent.StartupCommandTyped(state, "cleared", MasterAgent.STARTUP_COMMANDS[0]);
        Assert.Equal(MasterAgent.STARTUP_COMMANDS[1], MasterAgent.NextStartupCommand(state, cleared, NOW));
    }

    [Fact]
    public void ACommandAlreadyTypedUnderAnOldNameDoesNotCountForTheNewOne()
    {
        var state = new MasterAgentState { StartupSessionId = SESSION, TypedStartupCommands = ["/color yellow", "/rename Master Agent"] };

        Assert.Equal("/rename MASTER AGENT", MasterAgent.NextStartupCommand(state, AtHisPrompt(), NOW));
    }

    [Fact]
    public void NoCommandIsTypedMidTurnWithADialogOpenOrWhileASubagentRuns()
    {
        var state = new MasterAgentState();

        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt() with { IsWorking = true }, NOW));
        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt() with { IsProcessBusy = true }, NOW));
        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt() with { PendingQuestion = true }, NOW));
        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt() with { WaitingFor = "permission" }, NOW));
        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt() with { IsAlive = false }, NOW));
    }

    [Fact]
    public void AWindowThatRefusesACommandIsTriedAgainLaterAndReportedOnce()
    {
        var state = new MasterAgentState();

        Assert.True(MasterAgent.StartupCommandFailed(state, "no console", NOW));
        Assert.Null(MasterAgent.NextStartupCommand(state, AtHisPrompt(), NOW.AddSeconds(1)));
        Assert.Equal(MasterAgent.STARTUP_COMMANDS[0], MasterAgent.NextStartupCommand(state, AtHisPrompt(), NOW.AddMinutes(2)));

        Assert.False(MasterAgent.StartupCommandFailed(state, "no console", NOW.AddMinutes(2)));
        Assert.True(MasterAgent.StartupCommandFailed(state, "something else", NOW.AddMinutes(4)));
    }

    [Fact]
    public void ASessionNamedForHimUnderAnyOfHisNamesIsAdopted()
    {
        foreach (var name in new[] { "MASTER AGENT", "MASTER_AGENT" })
        {
            Assert.Equal("named", MasterAgent.RunningSession(new MasterAgentState(), [Agent("named", name)]));
        }
    }

    [Fact]
    public void AClosedSessionOrACodexNamedForHimIsNotHim()
    {
        var agents = new[] { Agent(SESSION, alive: false), Agent("closed", MasterAgent.SESSION_NAME, alive: false), Agent("codex", MasterAgent.SESSION_NAME, kind: AgentKind.Codex) };

        Assert.Null(MasterAgent.RunningSession(Running(), agents));
    }

    [Fact]
    public void TheRestorePanelNeverAlsoOffersHim()
    {
        Assert.True(SessionRestore.IsRestorable(Agent(SESSION)));
        Assert.False(SessionRestore.IsRestorable(Agent(SESSION) with { IsMasterAgent = true }));
    }

    [Fact]
    public void AFreshOneCarriesTheRolePromptAndAResumeKeepsHisName()
    {
        var fresh = MasterAgent.FreshCommand("claude", SESSION, "C:/Publish/MasterAgent/master-agent-prompt.md");
        var resume = MasterAgent.ResumeCommand("claude", SESSION, "C:/Publish/MasterAgent/master-agent-prompt.md");

        Assert.Equal($"claude --session-id {SESSION} -n MASTER_AGENT --append-system-prompt-file C:/Publish/MasterAgent/master-agent-prompt.md", fresh);
        Assert.Equal($"claude --resume {SESSION} --append-system-prompt-file C:/Publish/MasterAgent/master-agent-prompt.md", resume);
    }

    [Fact]
    public void APromptPathWithASpaceIsQuoted()
    {
        Assert.EndsWith("--append-system-prompt-file \"C:/Program Files/master-agent-prompt.md\"", MasterAgent.FreshCommand("claude", SESSION, "C:/Program Files/master-agent-prompt.md"));
    }

    [Fact]
    public void StateWithNoMasterAgentInItFromAnOlderVersionStillWantsHim()
    {
        var path = Path.Combine(Path.GetTempPath(), "am-master-agent-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"Marks":{}}""");
        try
        {
            var store = ManagerStore.Load(path);

            Assert.True(store.Read(data => data.MasterAgent.IsWanted));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ANewAgentStartsInTheOrchestratedProjectEvenWhenMostAgentsAreElsewhere()
    {
        var agents = new[] { Agent("a"), Agent("b"), Agent("c", cwd: "C:/MyProject") };

        Assert.Equal("C:/MyProject", AgentHub.ChooseFolder("C:/MyProject", agents));
    }

    [Fact]
    public void WithNoOrchestratorANewAgentStartsWhereMostAgentsAre()
    {
        var agents = new[] { Agent("a"), Agent("b"), Agent("c", cwd: "C:/Other") };

        Assert.Equal("C:/Home", AgentHub.ChooseFolder(null, agents));
    }

    [Fact]
    public void TheProjectIsTwoFoldersAboveTheOrchestratorState()
    {
        var expected = Path.GetFullPath("C:/Projects/MyGame");

        Assert.Equal(expected, AgentHub.ProjectOf("C:/Projects/MyGame/Saved/Orchestration"));
    }

    private static AgentSnapshot CodexThread(string threadId, string firstPrompt, DateTimeOffset firstEvent, bool alive = true)
    {
        return new AgentSnapshot
        {
            Key = "codex:" + threadId,
            Kind = AgentKind.Codex,
            SessionId = threadId,
            Name = "Run orch inbox command",
            Surface = "CLI",
            Pid = alive ? 200 : null,
            IsAlive = alive,
            HasPrompt = true,
            FirstEventAt = firstEvent,
            StartedAt = firstEvent,
            Prompts = [new PromptLine(firstEvent, firstPrompt)],
            TranscriptPath = "r.jsonl",
            ResumeCommand = "codex resume " + threadId,
        };
    }

    [Fact]
    public void ACodexStartWaitsForHisThreadInsteadOfStartingAnother()
    {
        var state = new MasterAgentState { IsWanted = false };

        MasterAgent.BeginFresh(state, AgentKind.Codex, null, "C:/MyProject", LOGON, NOW);

        Assert.Equal(AgentKind.Codex, state.Kind);
        Assert.Null(state.SessionId);
        Assert.False(MasterAgent.CanStartNew(state, isRunning: false));
        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, null, LOGON, NOW.AddSeconds(30)));
        Assert.Equal(MasterAgentStep.NeverStarted, MasterAgent.Next(state, null, LOGON, NOW + MasterAgent.STARTING_WINDOW));
        Assert.True(MasterAgent.CanStartNew(state, isRunning: false));
    }

    [Fact]
    public void ACodexMasterAgentIsTheThreadThatOpensWithTheStartPrompt()
    {
        var state = new MasterAgentState();
        MasterAgent.BeginFresh(state, AgentKind.Codex, null, "C:/MyProject", LOGON, NOW);
        var before = CodexThread("old", MasterAgent.CODEX_START_MARK + " read it", NOW.AddMinutes(-5));
        var other = CodexThread("other", "fix the login", NOW.AddSeconds(3));
        var claudeNamed = Agent(SESSION, MasterAgent.SESSION_NAME);
        var his = CodexThread("his", MasterAgent.CODEX_START_MARK + " read it", NOW.AddSeconds(4));

        Assert.Null(MasterAgent.RunningSession(state, [before, other, claudeNamed]));
        var running = MasterAgent.RunningSession(state, [before, other, claudeNamed, his]);

        Assert.Equal("his", running);
        Assert.Equal(MasterAgentStep.None, MasterAgent.Next(state, running, LOGON, NOW.AddSeconds(5)));
        Assert.Equal("codex:his", MasterAgent.KeyOf(state));
        Assert.Null(state.StartingAt);
    }

    [Fact]
    public void AClaudeMasterAgentIsNeverACodexOfTheSameName()
    {
        var state = Running();
        var codexNamed = CodexThread("t", "anything", NOW) with { Name = MasterAgent.NAME };

        Assert.Null(MasterAgent.RunningSession(state, [codexNamed]));
    }

    [Fact]
    public void ACodexWindowIsOnlyRenamedItHasNoColourCommand()
    {
        var state = new MasterAgentState { Kind = AgentKind.Codex, SessionId = "t" };
        var his = CodexThread("t", MasterAgent.CODEX_START_MARK, NOW) with { IsWorking = false };

        Assert.Equal("/rename " + MasterAgent.NAME, MasterAgent.NextStartupCommand(state, his, NOW));
        MasterAgent.StartupCommandTyped(state, "t", "/rename " + MasterAgent.NAME);
        Assert.Null(MasterAgent.NextStartupCommand(state, his, NOW));
    }

    [Fact]
    public void ACodexWindowCarriesHisRoleAndHisMark()
    {
        var script = MasterAgent.CodexFreshScript("codex", "C:/Tools/master-agent-prompt.md");
        var resume = MasterAgent.CodexResumeScript("codex", "t1", "C:/Tools/master-agent-prompt.md");

        Assert.Contains("-c \"developer_instructions=", script);
        Assert.Contains("\"" + MasterAgent.CODEX_START_MARK + " read C:/Tools/master-agent-prompt.md", script);
        Assert.Contains("codex resume -c \"developer_instructions=", resume);
        Assert.EndsWith(" t1\r\n", resume);
    }

    [Fact]
    public void ANewThreadInACodexMasterAgentsWindowKeepsHimTheMasterAgent()
    {
        var state = new MasterAgentState { Kind = AgentKind.Codex, SessionId = "a" };

        MasterAgent.HandOn(state, "codex:a", "claude:b");
        Assert.Equal("a", state.SessionId);

        MasterAgent.HandOn(state, "codex:a", "codex:b");
        Assert.Equal("b", state.SessionId);
    }

    [Fact]
    public void AStateFileFromBeforeTheChoiceIsAClaude()
    {
        var path = Path.Combine(Path.GetTempPath(), "am-master-kind-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"MasterAgent":{"IsWanted":true,"SessionId":"11111111-1111-1111-1111-111111111111"}}""");
        try
        {
            var state = ManagerStore.Load(path).Read(data => data.MasterAgent);

            Assert.Equal(AgentKind.Claude, state.Kind);
            Assert.Equal("claude:11111111-1111-1111-1111-111111111111", MasterAgent.KeyOf(state));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
