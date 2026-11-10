using AgentManager.Core;
using AgentManager.Core.Chat;
using AgentManager.Core.Model;
using AgentManager.Core.Orchestration;
using AgentManager.Core.State;

namespace AgentManager.Tests;

public sealed class StateClassifierTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static AgentSnapshot Agent(bool alive = true, bool working = true, bool prompted = true)
    {
        return new AgentSnapshot
        {
            Key = "claude:a",
            Kind = AgentKind.Claude,
            SessionId = "a",
            Name = "a",
            Surface = "CLI",
            IsAlive = alive,
            IsWorking = working,
            HasPrompt = prompted,
            LastEventAt = NOW,
            NeedsInputSince = NOW,
            TranscriptPath = "a.jsonl",
            ResumeCommand = "claude --resume a",
        };
    }

    private static LaneInfo Lane(string? queued = null, bool paused = false)
    {
        return new LaneInfo("claude:1:1", false, null, null, null, queued, 1, NOW, paused, 0, 0, 0, null, null);
    }

    [Fact]
    public void ClosedWindowIsClosed()
    {
        Assert.Equal(AgentState.Closed, StateClassifier.Classify(Agent(alive: false), null).State);
    }

    [Fact]
    public void TurnEndedWithoutAQuestionIsFinished()
    {
        var finished = Agent(working: false) with { LastTextAt = NOW };

        var verdict = StateClassifier.Classify(finished, null);
        Assert.Equal((AgentState.Finished, false), (verdict.State, verdict.IsRead));
        Assert.Equal(AgentState.Finished, StateClassifier.Classify(finished, NOW.AddSeconds(1)).State);
    }

    [Fact]
    public void TurnEndedOnAQuestionNeedsInputUntilRead()
    {
        var asking = Agent(working: false) with { LastTextAt = NOW, AsksQuestion = true, QuestionAt = NOW, QuestionText = "Ship it?" };

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(asking, null).State);
        var read = StateClassifier.Classify(asking, NOW.AddSeconds(1));
        Assert.Equal((AgentState.Idle, true), (read.State, read.IsRead));
        Assert.Equal("turn ended on a question: Ship it?, marked read", read.Reason);
        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(asking, NOW.AddSeconds(-1)).State);
    }

    [Fact]
    public void TaskBlockedOnTheUserNeedsInputUntilRead()
    {
        var waiting = Agent(working: false) with { LastTextAt = NOW, Lane = Lane() with { TaskTitle = "Review pages", TaskBlockedOnUser = true } };

        var verdict = StateClassifier.Classify(waiting, null);
        Assert.Equal(AgentState.NeedsInput, verdict.State);
        Assert.Equal("its task is blocked on you: Review pages", verdict.Reason);
        Assert.Equal(AgentState.Idle, StateClassifier.Classify(waiting, NOW.AddSeconds(1)).State);
    }

    [Fact]
    public void TaskBlockedOnTheUserIsNotRedWhileTheAgentQueuesForTheEditor()
    {
        var queued = Agent(working: false) with { RunningCommands = 1, LastTextAt = NOW, Lane = Lane() with { TaskTitle = "Molten head", TaskBlockedOnUser = true, QueuedKind = "Editor", QueuePosition = 1 } };

        Assert.Equal(AgentState.LaneBlocked, StateClassifier.Classify(queued, null).State);
    }

    [Fact]
    public void TaskBlockedOnTheUserIsNotRedWhileTheAgentWorks()
    {
        var working = Agent() with { Lane = Lane() with { TaskBlockedOnUser = true } };

        Assert.Equal(AgentState.Working, StateClassifier.Classify(working, null).State);
    }

    [Fact]
    public void WaitingOnTheUserIsRedEvenWithSomethingRunningInTheBackground()
    {
        var turnEnded = Agent(working: false) with { LastTextAt = NOW, RunningCommands = 1 };

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(turnEnded with { Lane = Lane() with { TaskBlockedOnUser = true } }, null).State);
        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(turnEnded with { AsksQuestion = true, QuestionAt = NOW, QuestionText = "Ship it?" }, null).State);
        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(turnEnded with { LastText = "Your call:" }, null).State);
    }

    [Fact]
    public void ARequestWithNoQuestionMarkWaitsOnTheUserUntilRead()
    {
        var asking = Agent(working: false) with { LastTextAt = NOW, LastPromptAt = NOW.AddMinutes(-1), LastText = "It's built.\n\nRefresh the page and pick one." };

        var verdict = StateClassifier.Classify(asking, null);
        Assert.Equal(AgentState.NeedsInput, verdict.State);
        Assert.Equal("waiting on you: Refresh the page and pick one.", verdict.Reason);
        Assert.Equal(AgentState.Idle, StateClassifier.Classify(asking, NOW.AddSeconds(1)).State);
    }

    [Fact]
    public void ARequestOnlyCountsOnceItsTurnHasEnded()
    {
        var working = Agent() with { LastTextAt = NOW, LastText = "Refresh the page." };

        Assert.Equal(AgentState.Working, StateClassifier.Classify(working, null).State);
    }

    [Fact]
    public void ARequestFromBeforeTheLastPromptHasBeenAnswered()
    {
        var answered = Agent(working: false) with { LastTextAt = NOW.AddMinutes(-5), LastPromptAt = NOW.AddMinutes(-1), LastText = "Your call:" };

        Assert.Equal(AgentState.Finished, StateClassifier.Classify(answered, null).State);
    }

    [Theory]
    [InlineData("Say \"cut over\" when you're ready.")]
    [InlineData("Nothing gets built until you say go.")]
    [InlineData("Still waiting on you:")]
    [InlineData("Pick one or a few bases per sound, then tell me.")]
    [InlineData("Refresh the page, then switch views with the dropdown at the top.")]
    [InlineData("I still need to know where preview (2).m4a came from.")]
    [InlineData("The brief is on a new channel, but nothing reaches Astra until you run this in its window:")]
    [InlineData("Your call:")]
    [InlineData("## Your call")]
    [InlineData("Save your picks when you're done and tell me \"use my picks\".")]
    [InlineData("I'm also waiting on your yes or no about the restarted agents.")]
    [InlineData("The editor is free for you to play it.")]
    [InlineData("The task is parked, blocked on you, with these steps recorded.")]
    [InlineData("Everything is still unsubmitted and waiting for you to play it.")]
    [InlineData("Ready to pose:")]
    [InlineData("**The preview is ready at http://127.0.0.1:8766.**")]
    [InlineData("Neither file has been touched, so it still needs you to run this in its window:")]
    [InlineData("Once you've chosen a base, I'll offer layers one at a time.")]
    [InlineData("This changes the design, so I want your yes before building.")]
    [InlineData("Want me to apply the same fix to the parser? It would bring parse time to about 950 ms and smooth out the startup.")]
    [InlineData("Or do you want to review more first? Lines 20-22 in the parser are still the main block, and only the lexer can change those.")]
    [InlineData("Should I do the retry next? I'd do it first, because it blocks every upload after a timeout.")]
    [InlineData("I can't rename this window from inside my turn, so please type these here:\n```\n/rename Master Agent\n```")]
    [InlineData("Which one, A or B? I'll start as soon as you've picked and you're done testing.")]
    [InlineData("Should I raise the full timeout too? One. Two. Three. Four. Five.")]
    [InlineData("Keep that, or do you want a different rule? One. Two. Three. Four. Five. Six. Seven. Eight. Nine.")]
    [InlineData("A (low limit), B (middle limit) or C (high limit)?")]
    [InlineData("Which do you want?\n\n- **A**: fast\n- **B**: slow\n- **C**: both")]
    [InlineData("If you haven't yet, run this in Astra's window:")]
    [InlineData("If you change anything, press Ctrl+S and tell me.")]
    [InlineData("Test it in PIE.")]
    [InlineData("Run the game and check the door.")]
    [InlineData("Waiting on you.")]
    [InlineData("You need to run the installer.")]
    [InlineData("Say go.")]
    [InlineData("Log in to Perforce, then I can push.")]
    [InlineData("the four new icons skipped your review, so they stay out unless you approve them.")]
    public void MessagesThatLeftTheNextStepToTheUser(string message)
    {
        Assert.NotNull(AgentManager.Core.Transcripts.Questions.FindRequest(message));
    }

    [Theory]
    [InlineData("You don't need to do anything.")]
    [InlineData("Refresh the page. You don't need to do anything else, and you don't need to do anything today.")]
    [InlineData("I'll tell you as each one finishes.")]
    [InlineData("If the panel clips the text with no warning, tell me and I'll widen it.")]
    [InlineData("Tell me if you'd rather leave them alone.")]
    [InlineData("Let me know if you want it changed.")]
    [InlineData("Still waiting on the C++/tools reviewer; its findings also tell me what changed inside the four edited data assets.")]
    [InlineData("A few skill history lines still say \"change 1377, unsubmitted\"; they'll be corrected.")]
    [InlineData("The rising one is for agents that need you.")]
    [InlineData("Windows only allows sound once you've clicked or pressed a key in the window.")]
    [InlineData("That button is supposed to stay hidden until you have the admin role.")]
    [InlineData("Click & Connect not working](https://umod.org/x)")]
    [InlineData("Your turn is now held back whenever the dragged body is off the middle of your view.")]
    [InlineData("When Astra posts a candidate I'll publish it to the same page for your yes.")]
    [InlineData("Any finished card whose task is blocked on you goes red.")]
    [InlineData("The skill docs that were waiting on your Perforce login are updated too.")]
    [InlineData("The build and re-measure are still running. I'll report back when the numbers are in.")]
    [InlineData("I'm waiting in the queue and will carry on automatically when the lane frees up.")]
    [InlineData("Pushed as change 1378.\n\n| Press | Open |\n|---|---|\n| a | b |")]
    [InlineData("It's in.\n\n```\nrun this\n```")]
    [InlineData("The page is http://127.0.0.1:8766/?view=docs and it is built.")]
    [InlineData("Why did it stall? The waiter matched a word. It's fixed. The test passes. The hold is gone. Nothing else changed.")]
    [InlineData("Did I break the normal build? No. It uses another config. It runs. It is built. It is tested.")]
    [InlineData("A soft pair plays, and the icon is a bobbing yellow ?")]
    [InlineData("I'm not holding the build slot; nothing is waiting on you, and nothing is submitted.")]
    [InlineData("I'm putting them straight in the game so you can test, rather than waiting for your approval first, and nothing is submitted.")]
    [InlineData("If you want it changed, tell me.")]
    [InlineData("It isn't finished. Next:\n- Run the load suite at 30 and 60 ms, and read the numbers.")]
    public void MessagesThatDidNot(string message)
    {
        Assert.Null(AgentManager.Core.Transcripts.Questions.FindRequest(message));
    }

    [Fact]
    public void QuestionWorkedPastEarlierInTheTurnStillFinishes()
    {
        var agent = Agent(working: false) with { LastTextAt = NOW, AsksQuestion = true, QuestionAt = NOW.AddMinutes(-3) };

        Assert.Equal(AgentState.Finished, StateClassifier.Classify(agent, null).State);
    }

    private static BridgeInfo Bridge(params BridgePeer[] peers)
    {
        return new BridgeInfo("release-notes", peers, 1, NOW, "a", false);
    }

    [Fact]
    public void BriefNobodyHasJoinedDoesNotWaitOnTheUser()
    {
        var posted = Agent(working: false) with { LastTextAt = NOW, Bridges = [Bridge()] };

        var verdict = StateClassifier.Classify(posted, null);
        Assert.Equal(AgentState.Finished, verdict.State);
        Assert.Equal("turn ended, nothing asked", verdict.Reason);
    }

    [Fact]
    public void BriefAnotherAgentJoinedIsNotWaitingOnTheUser()
    {
        var astra = new BridgePeer("Astra", true);
        var joined = Agent(working: false) with { LastTextAt = NOW, Bridges = [Bridge(astra)] };

        Assert.Equal(AgentState.Finished, StateClassifier.Classify(joined, null).State);
    }

    [Fact]
    public void WaitingOnAJoinedBridgeInTheBackgroundIsNotFinished()
    {
        var astra = new BridgePeer("Astra", true);
        var bridge = new BridgeInfo("release-notes", [astra], 2, NOW, "Astra", true);
        var waiting = Agent(working: false) with { LastTextAt = NOW, RunningCommands = 1, Bridges = [bridge] };

        var verdict = StateClassifier.Classify(waiting, null);
        Assert.Equal(AgentState.Working, verdict.State);
        Assert.Equal("waiting on release-notes", verdict.Reason);
    }

    [Fact]
    public void AnyBackgroundCommandIsNotFinished()
    {
        var running = Agent(working: false) with { LastTextAt = NOW, RunningCommands = 2 };

        var verdict = StateClassifier.Classify(running, null);
        Assert.Equal(AgentState.Working, verdict.State);
        Assert.Equal("2 background commands running", verdict.Reason);
    }

    [Fact]
    public void AServerLeftUpWaitsOnTheUserUntilRead()
    {
        var serving = Agent(working: false) with { LastTextAt = NOW, RunningServers = 1 };

        var verdict = StateClassifier.Classify(serving, null);
        Assert.Equal(AgentState.NeedsInput, verdict.State);
        Assert.Equal("it left a local server up for you to look at", verdict.Reason);
        Assert.Equal(AgentState.Idle, StateClassifier.Classify(serving, NOW.AddSeconds(1)).State);
    }

    [Fact]
    public void AServerDoesNotHideWorkStillRunning()
    {
        var inTurn = Agent() with { RunningServers = 1 };
        var withAJob = Agent(working: false) with { LastTextAt = NOW, RunningServers = 1, RunningCommands = 1 };

        Assert.Equal(AgentState.Working, StateClassifier.Classify(inTurn, null).State);
        Assert.Equal("1 background commands running", StateClassifier.Classify(withAJob, null).Reason);
    }

    [Fact]
    public void BriefNobodyJoinedWithAListenerRunningIsStillWork()
    {
        var waiting = Agent(working: false) with { LastTextAt = NOW, RunningCommands = 1, Bridges = [Bridge()] };

        Assert.Equal(AgentState.Working, StateClassifier.Classify(waiting, null).State);
    }

    [Fact]
    public void BlockingPromptAfterTheTurnIsNeverFinished()
    {
        var agent = Agent(working: false) with { LastTextAt = NOW, WaitingFor = "permission" };

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void NeverPromptedSessionIsIdle()
    {
        Assert.Equal(AgentState.Idle, StateClassifier.Classify(Agent(working: false, prompted: false), null).State);
    }

    [Fact]
    public void BlockingPromptBeatsTheLane()
    {
        var agent = Agent() with { WaitingFor = "permission", Lane = Lane(queued: "PIE") };

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void QueuedOrPausedOnTheLaneIsOrange()
    {
        Assert.Equal(AgentState.LaneBlocked, StateClassifier.Classify(Agent() with { Lane = Lane(queued: "Build") }, null).State);
        Assert.Equal(AgentState.LaneBlocked, StateClassifier.Classify(Agent() with { Lane = Lane(paused: true) }, null).State);
    }

    private static LaneInfo Holder(string kind = "PIE", bool paused = false)
    {
        return new LaneInfo("claude:1:1", true, kind, "door fix", NOW, null, 0, null, paused, 0, 0, 0, null, null);
    }

    private static ToolCall EditorCall()
    {
        return new ToolCall("t", "mcp__unreal-mcp__call_tool", "spawn a door", Activity.Tools, NOW, null, false, true);
    }

    [Theory]
    [InlineData("PIE")]
    [InlineData("Editor")]
    [InlineData("Build")]
    [InlineData("Sync")]
    public void HoldingTheEditorLaneIsPurple(string kind)
    {
        var verdict = StateClassifier.Classify(Agent() with { Lane = Holder(kind) }, null);

        Assert.Equal(AgentState.UsingEditor, verdict.State);
        Assert.Equal($"holds the editor lane ({kind}): door fix", verdict.Reason);
    }

    [Fact]
    public void AGrantOutsideTheEditorLaneStaysGreen()
    {
        var deploy = new LaneInfo("claude:1:1", false, "Deploy", "ship it", NOW, null, 0, null, false, 0, 0, 0, null, null);

        Assert.Equal(AgentState.Working, StateClassifier.Classify(Agent() with { Lane = deploy }, null).State);
    }

    [Fact]
    public void HoldingTheEditorLaneAfterTheTurnIsNotFinished()
    {
        var agent = Agent(working: false) with { LastTextAt = NOW, Lane = Holder() };

        Assert.Equal(AgentState.UsingEditor, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void BlockingPromptBeatsTheEditor()
    {
        var agent = Agent() with { WaitingFor = "permission", Lane = Holder() };

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void QuestionAtTheEndOfTheTurnIsRedThenPurpleOnceRead()
    {
        var agent = Agent(working: false) with { LastTextAt = NOW, AsksQuestion = true, QuestionAt = NOW, QuestionText = "Ship it?", Lane = Holder() };

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(agent, null).State);
        Assert.Equal(AgentState.UsingEditor, StateClassifier.Classify(agent, NOW.AddSeconds(1)).State);
    }

    [Fact]
    public void QuestionWhileHoldingTheEditorStaysPurple()
    {
        var agent = Agent() with { AsksQuestion = true, QuestionAt = NOW, Lane = Holder() };

        Assert.Equal(AgentState.UsingEditor, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void PausedByTheUsersHoldIsOrangeWhileHoldingTheLane()
    {
        var agent = Agent() with { Lane = Holder(paused: true) };

        Assert.Equal(AgentState.LaneBlocked, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void CallingTheEditorWithNoGrantIsPurple()
    {
        var verdict = StateClassifier.Classify(Agent() with { CurrentTool = EditorCall() }, null);

        Assert.Equal(AgentState.UsingEditor, verdict.State);
        Assert.Equal("calling the editor: spawn a door", verdict.Reason);
    }

    [Fact]
    public void QueuedForTheLaneStaysOrangeWhileItsCommandWaits()
    {
        var agent = Agent() with { CurrentTool = EditorCall(), Lane = Lane(queued: "Editor") };

        Assert.Equal(AgentState.LaneBlocked, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void OwnerWithASessionIdLinksByTheSession()
    {
        var bySession = new Dictionary<string, string> { ["s1"] = "claude:s1" };
        var byPid = new Dictionary<int, string> { [7] = "claude:other" };

        Assert.Equal("claude:s1", AgentHub.ClaudeAgentOf("s1", 7, true, bySession, byPid));
    }

    [Fact]
    public void LiveOwnerWithNoSessionIdLinksByItsPid()
    {
        var byPid = new Dictionary<int, string> { [7] = "claude:home" };

        Assert.Equal("claude:home", AgentHub.ClaudeAgentOf("", 7, true, new Dictionary<string, string>(), byPid));
        Assert.Null(AgentHub.ClaudeAgentOf("", 8, true, new Dictionary<string, string>(), byPid));
    }

    [Fact]
    public void DeadOwnerWithNoSessionIdNeverTakesAReusedPid()
    {
        var byPid = new Dictionary<int, string> { [7] = "claude:home" };

        Assert.Null(AgentHub.ClaudeAgentOf("", 7, false, new Dictionary<string, string>(), byPid));
    }

    [Fact]
    public void QuestionWhileWorkingIsYellowUntilRead()
    {
        var asking = Agent() with { AsksQuestion = true, QuestionAt = NOW };

        Assert.Equal(AgentState.Asked, StateClassifier.Classify(asking, null).State);
        Assert.Equal(AgentState.Working, StateClassifier.Classify(asking, NOW.AddSeconds(1)).State);
    }

    private static LaneInfo OpenTask(string? blockedOn = null, DateTimeOffset? updated = null, bool onUser = false, string? why = null, string? next = null)
    {
        return Lane() with { TaskTitle = "Build the banks", TaskBlockedOn = blockedOn, TaskWhy = why, TaskNext = next, TaskBlockedOnUser = onUser, TaskUpdatedAt = updated ?? NOW };
    }

    private static PromptLine[] PromptsAt(params int[] minutesAgo)
    {
        return minutesAgo.Select(m => new PromptLine(NOW.AddMinutes(-m), "go")).ToArray();
    }

    [Fact]
    public void ATurnThatEndedWithItsTaskBlockedOnAnotherAgentIsPausedAndSaysOnWhat()
    {
        var waiting = Agent(working: false) with { LastTextAt = NOW, Lane = OpenTask(blockedOn: "parser-hook", why: "its decision on the tap") };

        var verdict = StateClassifier.Classify(waiting, null);

        Assert.Equal(AgentState.Paused, verdict.State);
        Assert.Equal("waiting on parser-hook: its decision on the tap", verdict.Reason);
    }

    [Fact]
    public void ATurnThatEndedWithItsTaskStillInProgressIsPausedAndSaysWhatIsNext()
    {
        var open = Agent(working: false) with { LastTextAt = NOW, Lane = OpenTask(next: "build the banks") };

        var verdict = StateClassifier.Classify(open, null);

        Assert.Equal(AgentState.Paused, verdict.State);
        Assert.Equal("task still open, next: build the banks", verdict.Reason);
    }

    [Fact]
    public void ATaskBlockedOnTheUserStaysRedNotPaused()
    {
        var waiting = Agent(working: false) with { LastTextAt = NOW, Lane = OpenTask(blockedOn: "user", onUser: true) };

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(waiting, null).State);
    }

    [Fact]
    public void AnOpenTaskIsNotPausedWhileTheAgentWorks()
    {
        Assert.Equal(AgentState.Working, StateClassifier.Classify(Agent() with { Lane = OpenTask() }, null).State);
    }

    [Fact]
    public void ATurnThatEndedWithNoTaskIsStillFinished()
    {
        var done = Agent(working: false) with { LastTextAt = NOW, Lane = Lane() };

        Assert.Equal(AgentState.Finished, StateClassifier.Classify(done, null).State);
    }

    [Fact]
    public void ATaskLastTouchedInAMuchEarlierTurnDoesNotPauseACard()
    {
        var stale = Agent(working: false) with { LastTextAt = NOW, Prompts = PromptsAt(90, 60, 30, 10), Lane = OpenTask(updated: NOW.AddMinutes(-70)) };

        Assert.Equal(AgentState.Finished, StateClassifier.Classify(stale, null).State);
    }

    [Fact]
    public void ATaskTouchedInTheLastTurnOrTheOneBeforeItCounts()
    {
        var thisTurn = Agent(working: false) with { LastTextAt = NOW, Prompts = PromptsAt(90, 60, 30, 10), Lane = OpenTask(updated: NOW.AddMinutes(-5)) };
        var turnBefore = thisTurn with { Lane = OpenTask(updated: NOW.AddMinutes(-20)) };

        Assert.Equal(AgentState.Paused, StateClassifier.Classify(thisTurn, null).State);
        Assert.Equal(AgentState.Paused, StateClassifier.Classify(turnBefore, null).State);
    }

    [Fact]
    public void AnOldTaskOnASessionWithOnlyOneTurnStillCountsHoweverOld()
    {
        var waitingLong = Agent(working: false) with { LastTextAt = NOW, Prompts = PromptsAt(600), Lane = OpenTask(blockedOn: "x", updated: NOW.AddHours(-9)) };

        Assert.Equal(AgentState.Paused, StateClassifier.Classify(waitingLong, null).State);
    }

    [Fact]
    public void HoldingTheLaneBeatsPaused()
    {
        var holding = Agent(working: false) with { LastTextAt = NOW, Lane = Holder() with { TaskTitle = "Build the banks", TaskUpdatedAt = NOW } };

        Assert.Equal(AgentState.UsingEditor, StateClassifier.Classify(holding, null).State);
    }

    [Fact]
    public void RunningSubagentKeepsAFinishedTurnWorking()
    {
        var agent = Agent(working: false) with { Subagents = [new SubagentInfo("s", "Explore", "x", true, NOW, 0)] };

        Assert.Equal(AgentState.Working, StateClassifier.Classify(agent, null).State);
    }
}

public sealed class ChatLogTests
{
    private static readonly DateTimeOffset AT = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ReceiverRecordsOfOneBroadcastBecomeOneMessageToTheChannel()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage("1", AT, ChatChannel.Orchestrator, "A", "B", "k:a", "k:b", "search", null, "text"), false);
        log.Add(new ChatMessage("2", AT.AddSeconds(1), ChatChannel.Orchestrator, "A", "C", "k:a", "k:c", "search", null, "text"), false);

        var message = Assert.Single(log.Snapshot());
        Assert.Equal("#search", message.To);
    }

    [Fact]
    public void OneTextSentToSeveralAgentsNamesThemAll()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage("1", AT, ChatChannel.Peer, "M", "B", "k:m", null, null, null, "release the lane"), true);
        log.Add(new ChatMessage("2", AT.AddSeconds(1), ChatChannel.Peer, "M", "C", "k:m", null, null, null, "release the lane"), true);
        log.Add(new ChatMessage("3", AT.AddSeconds(2), ChatChannel.Peer, "M", "B", "k:m", "k:b", null, null, "release the lane"), false);

        var message = Assert.Single(log.Snapshot());
        Assert.Equal("B, C", message.To);
        Assert.Equal(2, message.Recipients);
    }

    [Fact]
    public void TwoAgentsSayingTheSameThingAreTwoMessages()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage("1", AT, ChatChannel.Peer, "A", "M", "k:a", "k:m", null, null, "Done."), true);
        log.Add(new ChatMessage("2", AT.AddSeconds(5), ChatChannel.Peer, "B", "M", "k:b", "k:m", null, null, "Done."), true);

        var messages = log.Snapshot();
        Assert.Equal(["A", "B"], messages.Select(m => m.From));
    }

    [Fact]
    public void AReceiverThatCannotTellWhoSentAMessageStillMergesWithTheSendersRecord()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage("1", AT, ChatChannel.Peer, "A", "B", "k:a", null, null, null, "text"), true);
        log.Add(new ChatMessage("2", AT.AddSeconds(1), ChatChannel.Peer, "unknown", "B", null, "k:b", null, null, "text"), false);

        var message = Assert.Single(log.Snapshot());
        Assert.Equal(("A", "k:b"), (message.From, message.ToKey));
    }

    [Fact]
    public void OneAgentUnderTwoNamesIsListedOnce()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage("1", AT, ChatChannel.Peer, "M", "uds:b", "k:m", null, null, null, "text"), true);
        log.Add(new ChatMessage("2", AT.AddSeconds(1), ChatChannel.Peer, "M", "B", "k:m", "k:b", null, null, "text"), false);
        log.Add(new ChatMessage("3", AT.AddSeconds(2), ChatChannel.Peer, "M", "uds:b", "k:m", null, null, null, "text"), true);

        var message = Assert.Single(log.Snapshot());
        Assert.Equal("B", message.To);
        Assert.Equal(1, message.Recipients);
    }

    [Fact]
    public void EveryoneCountsRecipientsNotCommasInAName()
    {
        var oneAgent = new ChatMessage("1", AT, ChatChannel.Peer, "M", "fix the door, the key, the lock, and the bar", "k:m", "k:b", null, null, "text");
        var log = new ChatLog();
        foreach (var (name, index) in new[] { "A", "B", "C", "D" }.Select((n, i) => (n, i)))
        {
            log.Add(new ChatMessage("m" + index, AT.AddSeconds(index), ChatChannel.Peer, "M", name, "k:m", null, null, null, "all of you"), true);
        }

        Assert.Equal(oneAgent.To, ChatView.Recipients(oneAgent));
        Assert.Equal("EVERYONE (4)", ChatView.Recipients(Assert.Single(log.Snapshot())));
    }

    [Theory]
    [InlineData(true, "claude:m", true)]
    [InlineData(false, "claude:m", false)]
    [InlineData(true, null, false)]
    public void TheMasterAgentFilterGoesWithHisChip(bool isChipOn, string? masterAgentKey, bool expected)
    {
        Assert.Equal(expected, ChatView.ShowsOnlyMasterAgent(isChipOn, masterAgentKey));
    }

    [Fact]
    public void SameTextHoursApartStaysTwoMessages()
    {
        var log = new ChatLog();
        log.Add(new ChatMessage("1", AT, ChatChannel.Peer, "A", "B", null, null, null, null, "ok"), true);
        log.Add(new ChatMessage("2", AT.AddHours(2), ChatChannel.Peer, "A", "B", null, null, null, null, "ok"), true);

        Assert.Equal(2, log.Snapshot().Count);
    }
}

public sealed class OrchestratorTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InboxRowsAreParsedLikeOrchPyWritesThem()
    {
        var channel = InboxParser.Parse("Message on login-form from LOGIN WITH SSO [claude:45236:1]:\nhello\nworld\n(reply with: orch.cmd msg --to login-form \"...\")");
        var direct = InboxParser.Parse("Message from  [codex:5:1]:\nthanks\n(reply with: orch.cmd msg --to codex:5:1 \"...\")");
        var system = InboxParser.Parse("The user took the editor.");

        Assert.Equal((InboxKind.Channel, "LOGIN WITH SSO", "claude:45236:1", "login-form", "hello\nworld"), (channel.Kind, channel.SenderName, channel.SenderOwner, channel.Work, channel.Body));
        Assert.Equal((InboxKind.Direct, (string?)null, "codex:5:1", "thanks"), (direct.Kind, direct.SenderName, direct.SenderOwner, direct.Body));
        Assert.Equal(InboxKind.System, system.Kind);
    }

    [Fact]
    public void TheViewLooksOwnersUpUnderTheKeyTheirNamesAreStoredWith()
    {
        var dir = Path.Combine(Path.GetTempPath(), "am-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "orch-status.json"), """{"lane":{"owner":"claude:1:2","kind":"PIE"},"queue":[{"owner":"claude:3:4","kind":"Build","position":1}]}""");
            var reader = new OrchestratorReader(dir);
            reader.Poll(new ChatLog(), 1, key => key, NOW);
            var names = new Dictionary<string, string> { ["orch:claude:1:2"] = "spec", ["orch:claude:3:4"] = "searchFIX" };

            var view = reader.View(NOW, key => names.GetValueOrDefault(key, "unnamed"));

            Assert.Equal("spec", view?.Lane?.Name);
            Assert.Equal("searchFIX", Assert.Single(view!.Queue).Name);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static OrchestratorReader ReaderOf(string dir, string feed)
    {
        File.WriteAllText(Path.Combine(dir, "orch-status.json"), feed);
        var reader = new OrchestratorReader(dir);
        reader.Poll(new ChatLog(), 1, key => key, NOW);
        return reader;
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "am-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void AQueueEntryIsBehindWhatItWantsNotWhateverIsAheadOfIt()
    {
        var dir = TempDir();
        try
        {
            var reader = ReaderOf(dir, """
                {"lane":{"id":"h","owner":"holder","kind":"PIE","lane":"X"},
                 "grants":[{"id":"h","owner":"holder","kind":"PIE","state":"active","lane":"X"}],
                 "queue":[{"id":"d","owner":"releaser","kind":"Deploy","lane":"","position":1},
                          {"id":"a","owner":"first","kind":"Build","lane":"X","position":1},
                          {"id":"b","owner":"second","kind":"PIE","lane":"X","position":2}]}
                """);

            Assert.Null(reader.LaneFor("releaser", NOW).QueuedBehindOwner);
            Assert.Equal("holder", reader.LaneFor("first", NOW).QueuedBehindOwner);
            Assert.Equal("first", reader.LaneFor("second", NOW).QueuedBehindOwner);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TwoSharedGrantsDoNotQueueBehindEachOther()
    {
        var dir = TempDir();
        try
        {
            var reader = ReaderOf(dir, """
                {"grants":[{"id":"t","owner":"tester","kind":"Test","state":"active","lane":"S"}],
                 "queue":[{"id":"u","owner":"queued","kind":"Test","lane":"S","position":1},
                          {"id":"p","owner":"pie","kind":"PIE","lane":"X","position":1}]}
                """);

            Assert.Null(reader.LaneFor("queued", NOW).QueuedBehindOwner);
            Assert.Equal("queued", reader.LaneFor("pie", NOW).QueuedBehindOwner);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void AnOlderTaskThatFellOutOfTheFeedIsStillRead()
    {
        var dir = TempDir();
        try
        {
            var created = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(dir, "orch.db"), Pooling = false }.ToString();
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection(created))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = $"""
                    CREATE TABLE tasks (id TEXT, owner TEXT, title TEXT, state TEXT, next_step TEXT, blocked_on TEXT, updated REAL, why TEXT);
                    CREATE TABLE owners (owner TEXT, kind TEXT, pid INTEGER, session_id TEXT, title TEXT, cwd TEXT);
                    CREATE TABLE inbox (id INTEGER, owner TEXT, created REAL, text TEXT);
                    CREATE TABLE grants (id TEXT, owner TEXT, kind TEXT, resources TEXT, purpose TEXT, implicit INTEGER, state TEXT, queued_at REAL, acquired_at REAL, released_at REAL);
                    CREATE TABLE meta (key TEXT, value TEXT);
                    INSERT INTO tasks VALUES ('old', 'claude:1:1', 'Review pages', 'blocked', '', 'user', {NOW.AddDays(-1).ToUnixTimeSeconds()}, 'which page?');
                    INSERT INTO tasks VALUES ('done', 'claude:2:2', 'Closed', 'done', '', '', {NOW.ToUnixTimeSeconds()}, '');
                    """;
                command.ExecuteNonQuery();
            }

            var reader = ReaderOf(dir, """{"tasks":[{"id":"other","owner":"claude:9:9","title":"Not mine","state":"in_progress","updated":1}]}""");

            var lane = reader.LaneFor("claude:1:1", NOW);
            Assert.Equal("Review pages", lane.TaskTitle);
            Assert.True(lane.TaskBlockedOnUser);
            Assert.Equal("which page?", lane.TaskWhy);
            Assert.Null(reader.LaneFor("claude:2:2", NOW).TaskTitle);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(true, 5, true)]
    [InlineData(false, 5, false)]
    [InlineData(true, 120, false)]
    public void TheDaemonIsUpOnlyWhileItsFeedIsFreshAndSaysSo(bool saysAlive, int feedAgeSeconds, bool expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "am-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var generatedAt = NOW.AddSeconds(-feedAgeSeconds).ToUnixTimeSeconds();
            var alive = saysAlive ? "true" : "false";
            File.WriteAllText(Path.Combine(dir, "orch-status.json"), $$$"""{"generatedAt":{{{generatedAt}}},"daemon":{"alive":{{{alive}}}}}""");
            var reader = new OrchestratorReader(dir);
            reader.Poll(new ChatLog(), 1, key => key, NOW);

            var view = reader.View(NOW, key => key);

            Assert.Equal(expected, view!.IsDaemonUp(NOW));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LaneDayCountsHeldAndWaitedTime()
    {
        var grants = new[]
        {
            new GrantRow("1", "a", "Build", true, "", false, "done", NOW.AddMinutes(-40), NOW.AddMinutes(-30), NOW.AddMinutes(-10)),
            new GrantRow("2", "b", "PIE", true, "", false, "queued", NOW.AddMinutes(-5), null, null),
            new GrantRow("3", "a", "P4", false, "", true, "done", NOW.AddMinutes(-3), NOW.AddMinutes(-3), NOW.AddMinutes(-3)),
        };

        var day = LaneMetrics.Today(grants, NOW);

        Assert.Equal(20 * 60, day.BusySeconds);
        Assert.Equal(1, day.Builds);
        Assert.Equal(10 * 60, day.Owners["a"].WaitedSeconds);
        Assert.Equal(5 * 60, day.Owners["b"].WaitedSeconds);
    }
}

public sealed class PageZoomTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100)]
    [InlineData(double.NaN)]
    public void ADamagedZoomFallsBackToTheDefault(double stored)
    {
        Assert.Equal(PageZoom.DEFAULT, PageZoom.Usable(stored));
    }

    [Fact]
    public void AChosenZoomIsKept()
    {
        Assert.Equal(0.67, PageZoom.Usable(0.67));
    }

    [Fact]
    public void AnOlderStateFileGetsTheDefaultAndAChosenZoomSurvivesARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "am-zoom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "state.json");
            File.WriteAllText(path, """{"Settings":{"HistoryHours":12}}""");
            var store = ManagerStore.Load(path);
            Assert.Equal(PageZoom.DEFAULT, store.Settings.Zoom);

            store.Change(data => data.Settings.Zoom = 0.67);
            store.SaveIfDirty();

            Assert.Equal(0.67, ManagerStore.Load(path).Settings.Zoom);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class TokenRateTests
{
    [Fact]
    public void RateRisesWithTokensAndDecaysWithout()
    {
        var rates = new TokenRates();
        var start = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        rates.Sample("a", 1_000, start);

        var (busy, history) = rates.Sample("a", 41_000, start.AddSeconds(1));
        var (quiet, _) = rates.Sample("a", 41_000, start.AddSeconds(5));

        Assert.True(busy > 0);
        Assert.True(quiet < busy);
        Assert.Equal(TokenRates.HISTORY, history.Count);
        Assert.Equal(busy, history[^1]);
    }
}

public sealed class UsageProfileTests
{
    [Fact]
    public void ProfileCoversTheWholeSession()
    {
        var tracker = new AgentManager.Core.Transcripts.SessionTracker();
        var start = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        tracker.Usage(start, "m1", 100, 0, 0, 0);
        tracker.Usage(start.AddHours(3), "m2", 900, 0, 0, 0);

        var profile = tracker.UsageProfile(start.AddHours(3), 90);

        Assert.Equal(90, profile.Count);
        Assert.Equal(100, profile[0]);
        Assert.Equal(900, profile[^1]);
        Assert.Equal(1000, profile.Sum());
    }
}

public sealed class LauncherTests
{
    [Theory]
    [InlineData("CLAUDE_CODE_CHILD_SESSION", true)]
    [InlineData("CLAUDECODE", true)]
    [InlineData("CLAUDE_CODE_MESSAGING_SOCKET", true)]
    [InlineData("CODEX_THREAD_ID", true)]
    [InlineData("CLAUDE_PID", true)]
    [InlineData("PATH", false)]
    public void OnlySessionVariablesAreDropped(string name, bool isSession)
    {
        Assert.Equal(isSession, AgentManager.Core.Windows.Launcher.IsSessionVariable(name));
    }
}
