using System.Text;
using AgentManager.Core.Chat;
using AgentManager.Core.Claude;
using AgentManager.Core.Codex;
using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;
using AgentManager.Core;
using AgentManager.Core.Windows;

namespace AgentManager.Tests;

public sealed class JsonlTailTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), "am-tail-" + Guid.NewGuid().ToString("N") + ".jsonl");

    [Fact]
    public void HoldsBackAPartialLineUntilItsNewlineArrives()
    {
        File.WriteAllText(path, "{\"a\":1}\r\n{\"b\":");
        var tail = new JsonlTail(path);

        var first = tail.ReadNewLines().Select(l => Encoding.UTF8.GetString(l.Span)).ToList();
        File.AppendAllText(path, "2}\n");
        var second = tail.ReadNewLines().Select(l => Encoding.UTF8.GetString(l.Span)).ToList();

        Assert.Equal(["{\"a\":1}"], first);
        Assert.Equal(["{\"b\":2}"], second);
        Assert.Empty(tail.ReadNewLines());
    }

    public void Dispose()
    {
        File.Delete(path);
    }
}

/// <summary>Built like the real thing: a registry file for a live process, a transcript and a subagent, read by the real source.</summary>
public sealed class RegistryBusyTests : IDisposable
{
    private const string SESSION = "11111111-2222-3333-4444-555555555555";

    private readonly string dir = Path.Combine(Path.GetTempPath(), "am-registry-" + Guid.NewGuid().ToString("N"));

    private readonly string transcript;

    private readonly DateTimeOffset now = DateTimeOffset.Now;

    public RegistryBusyTests()
    {
        var project = Path.Combine(dir, "projects", "C--work");
        var subagents = Path.Combine(project, SESSION, "subagents");
        Directory.CreateDirectory(subagents);
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
        transcript = Path.Combine(project, SESSION + ".jsonl");
        File.WriteAllText(Path.Combine(subagents, "agent-abc.meta.json"), """{"agentType":"general","description":"sub","finished":false}""");
        File.WriteAllText(Path.Combine(subagents, "agent-abc.jsonl"), Line($$$"""{"type":"assistant","timestamp":"{{{Stamp(-5)}}}","isSidechain":true,"message":{"content":[{"type":"text","text":"working"}]}}"""));
        var pid = Environment.ProcessId;
        var start = ProcessTable.Capture().StartTime(pid);
        File.WriteAllText(Path.Combine(dir, "sessions", pid + ".json"), $$$"""{"pid":{{{pid}}},"procStart":{{{start}}},"sessionId":"{{{SESSION}}}","cwd":"C:/work","status":"busy","startedAt":{{{now.AddHours(-1).ToUnixTimeMilliseconds()}}},"statusUpdatedAt":{{{now.AddMinutes(-30).ToUnixTimeMilliseconds()}}}}""");
        Append($$$"""{"type":"user","timestamp":"{{{Stamp(-60)}}}","message":{"role":"user","content":"look at the footer"}}""");
    }

    private string Stamp(int secondsFromNow)
    {
        return now.AddSeconds(secondsFromNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Line(string json)
    {
        return json + "\n";
    }

    private void Append(string json)
    {
        File.AppendAllText(transcript, Line(json));
    }

    private void EndTurn(string text)
    {
        Append($$$"""{"type":"assistant","timestamp":"{{{Stamp(-30)}}}","message":{"id":"m1","stop_reason":"end_turn","content":[{"type":"text","text":{{{System.Text.Json.JsonSerializer.Serialize(text)}}}}]}}""");
    }

    private AgentSnapshot Read()
    {
        var source = new ClaudeSource(dir);
        var store = AgentManager.Core.State.ManagerStore.InMemory();
        source.Poll(ProcessTable.Capture(), new ChatLog(), store, now);
        return source.Snapshots(now).Single();
    }

    [Fact]
    public void ATurnThatEndedOnAQuestionIsRedWithASubagentRunningAndTheRegistryBusy()
    {
        EndTurn("It's built. Should a tap catch him too?");

        var agent = Read();

        Assert.False(agent.IsWorking);
        Assert.True(agent.IsProcessBusy);
        Assert.Equal(1, agent.RunningSubagents);
        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(agent, null).State);
    }

    [Fact]
    public void ATurnThatEndedOnARequestIsRedWithASubagentRunningAndTheRegistryBusy()
    {
        EndTurn("The page is ready. Your call:");

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(Read(), null).State);
    }

    [Fact]
    public void ATurnThatEndedAskingNothingStaysGreenWhileItsSubagentRuns()
    {
        EndTurn("A subagent is on it; I'll report when it hands back.");

        var verdict = StateClassifier.Classify(Read(), null);

        Assert.Equal(AgentState.Working, verdict.State);
        Assert.Equal("1 subagents running", verdict.Reason);
    }

    [Theory]
    [InlineData("""{"type":"api-request","querySource":"repl_main_thread"}""")]
    [InlineData("""{"type":"user","timestamp":"2026-11-02T22:00:00Z","message":{"role":"user","content":"<task-notification>\n<status>completed</status></task-notification>"}}""")]
    [InlineData("""{"type":"user","isMeta":true,"timestamp":"2026-11-02T22:00:00Z","message":{"role":"user","content":"Stop hook feedback: keep going"}}""")]
    [InlineData("""{"type":"assistant","timestamp":"2026-11-02T22:00:00Z","message":{"id":"m2","content":[{"type":"thinking","thinking":""}]}}""")]
    public void AnAgentWokenAfterItsTurnReadsAsWorkingAgain(string wake)
    {
        EndTurn("Should a tap catch him too?");
        Append(wake);

        var agent = Read();

        Assert.True(agent.IsWorking);
        Assert.NotEqual(AgentState.NeedsInput, StateClassifier.Classify(agent, null).State);
    }

    [Theory]
    [InlineData("""{"type":"user","isMeta":true,"timestamp":"2026-11-02T22:00:00Z","message":{"role":"user","content":"<system-reminder>a note</system-reminder>"}}""")]
    [InlineData("""{"type":"system","subtype":"local_command","content":"<command-name>/rename</command-name>"}""")]
    [InlineData("""{"type":"system","subtype":"turn_duration","durationMs":1}""")]
    [InlineData("""{"type":"api-request","querySource":"away_summary"}""")]
    public void LinesThatDoNotMeanTheAgentRunsAgainLeaveItAtItsPrompt(string line)
    {
        EndTurn("Should a tap catch him too?");
        Append(line);

        Assert.Equal(AgentState.NeedsInput, StateClassifier.Classify(Read(), null).State);
    }

    [Fact]
    public void ANewPromptWhileItsSubagentRunsReadsAsWorking()
    {
        EndTurn("Should a tap catch him too?");
        Append($$$"""{"type":"user","timestamp":"{{{Stamp(-1)}}}","message":{"role":"user","content":"yes"}}""");

        Assert.True(Read().IsWorking);
    }

    public void Dispose()
    {
        Directory.Delete(dir, true);
    }
}

public sealed class AstraTitleTests
{
    [Theory]
    [InlineData(AgentKind.Codex, "Astra: api-docs", false, true)]
    [InlineData(AgentKind.Codex, "Astra: api-docs", true, false)]
    [InlineData(AgentKind.Claude, "Astra: api-docs", false, false)]
    [InlineData(AgentKind.Codex, "Astra", false, false)]
    [InlineData(AgentKind.Codex, "Astra review of the door", false, false)]
    [InlineData(AgentKind.Codex, "Run orch inbox command", false, false)]
    [InlineData(AgentKind.Codex, null, false, false)]
    public void OnlyAClosedAstraWindowLeavesNoCard(AgentKind kind, string? name, bool isAlive, bool expected)
    {
        Assert.Equal(expected, BridgeRules.LeavesNoClosedCard(kind, name, isAlive));
    }

    [Fact]
    public void ThePromptAReopenedAstraIsGivenStillTitlesItsCard()
    {
        var card = BridgeRules.AstraTitle("Run .claude/scripts/orch.cmd --as astra-api-docs inbox api-docs and do what it says. Put --as astra-api-docs before the command word on every orch.cmd call in this job.");

        Assert.Equal("Astra: api-docs", card);
        Assert.True(BridgeRules.LeavesNoClosedCard(AgentKind.Codex, card, false));
    }

    [Theory]
    [InlineData("Run .claude/scripts/orch.cmd --as astra-api-docs inbox api-docs and do what it says. Put --as astra-api-docs before the command word on every orch.cmd call in this job.", "Astra: api-docs")]
    [InlineData("Run .claude/scripts/orch.cmd --as astra-cache-layer inbox cache-layer and do what it says.", "Astra: cache-layer")]
    [InlineData("Run the game and check the door", null)]
    [InlineData("Run .claude/scripts/orch.cmd inbox api-docs", null)]
    [InlineData(null, null)]
    public void AnAstraWindowIsTitledByItsChannel(string? firstPrompt, string? expected)
    {
        Assert.Equal(expected, BridgeRules.AstraTitle(firstPrompt));
    }

    [Fact]
    public void ALongChannelCutOffInTheStoredPromptStillGivesTheTitle()
    {
        var channel = new string('x', 90);
        var stored = Json.OneLine($"Run .claude/scripts/orch.cmd --as astra-{channel} inbox {channel} and do what it says.", 120);

        Assert.StartsWith("Astra: xxx", BridgeRules.AstraTitle(stored));
    }
}

public sealed class FailedBackgroundCommandTests
{
    [Fact]
    public void AServerThatFailedToStartIsNotLeftRunning()
    {
        var tracker = new SessionTracker();
        var at = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        tracker.ToolStart(at, "c", "Bash", "serve", Activity.Tools);
        tracker.BackgroundStart("c", at, isServer: true);

        tracker.ToolEnd(at.AddSeconds(1), "c", isError: true);

        Assert.Equal(0, tracker.RunningServersSince(at.AddHours(-1)));
    }
}

public sealed class ClaudeLinesTests
{
    private static readonly ClaudeChatContext CONTEXT = new("claude:me", "me", address => (address == "uds:peer" ? "peer-session" : address, address == "uds:peer" ? "claude:peer" : null));

    private static void Apply(string json, SessionTracker? tracker, ChatLog chat, string id = "1")
    {
        ClaudeLines.Apply(Encoding.UTF8.GetBytes(json), id, tracker, CONTEXT, chat);
    }

    [Fact]
    public void SendMessageBecomesAnOutgoingChatMessage()
    {
        var chat = new ChatLog();
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:00Z","message":{"id":"m1","content":[{"type":"tool_use","id":"tu1","name":"SendMessage","input":{"to":"uds:peer","summary":"hi","message":"hello there"}}]}}""", null, chat);

        var message = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatChannel.Peer, message.Channel);
        Assert.Equal("me", message.From);
        Assert.Equal("peer-session", message.To);
        Assert.Equal("claude:peer", message.ToKey);
        Assert.Equal("hello there", message.Text);
    }

    [Fact]
    public void QueuedEnvelopeIsAnIncomingMessageAndMergesWithTheSendersRecord()
    {
        var chat = new ChatLog();
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:00Z","message":{"id":"m1","content":[{"type":"tool_use","id":"tu1","name":"SendMessage","input":{"to":"uds:peer","message":"same text"}}]}}""", null, chat);
        Apply("""{"type":"queue-operation","operation":"enqueue","timestamp":"2026-11-01T10:00:01Z","content":"<cross-session-message from=\"uds:x\" from-name=\"other\">\nsame text\n</cross-session-message>"}""", null, chat, "2");

        var message = Assert.Single(chat.Snapshot());
        Assert.Equal("me", message.From);
    }

    [Fact]
    public void SubagentHandBackFrameIsStripped()
    {
        var body = EnvelopeParser.CleanBody("[Subagent hand-back] The text below is the report. The report follows:\n  Summary: done\n  - item");

        Assert.Equal("Summary: done\n- item", body);
    }

    [Fact]
    public void UsageIsCountedOncePerMessage()
    {
        var tracker = new SessionTracker();
        var chat = new ChatLog();
        var line = """{"type":"assistant","timestamp":"2026-11-01T10:00:00Z","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":10,"cache_read_input_tokens":100,"cache_creation_input_tokens":5,"output_tokens":7},"content":[{"type":"text","text":"a"}]}}""";
        Apply(line, tracker, chat);
        Apply(line, tracker, chat);

        Assert.Equal(122, tracker.TotalTokens);
        Assert.Equal(115, tracker.Context);
        Assert.Equal("Opus 5.5", ClaudeSource.PrettyModel(tracker.Model));
    }

    private static readonly DateTimeOffset PROCESS_START = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private static (SessionTracker Tracker, ChatLog Chat) StartBackgroundListener()
    {
        var tracker = new SessionTracker();
        var chat = new ChatLog();
        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:00Z","message":{"content":"get the pages up"}}""", tracker, chat);
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:05Z","message":{"id":"m1","content":[{"type":"tool_use","id":"bg1","name":"Bash","input":{"command":".claude/scripts/orch.cmd inbox --wait 300","run_in_background":true}}]}}""", tracker, chat);
        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:06Z","toolUseResult":{"backgroundTaskId":"task1"},"message":{"content":[{"type":"tool_result","tool_use_id":"bg1","content":"running in background"}]}}""", tracker, chat);
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:10Z","message":{"id":"m2","stop_reason":"end_turn","content":[{"type":"text","text":"Waiting on Astra."}]}}""", tracker, chat);
        return (tracker, chat);
    }

    [Fact]
    public void BackgroundCommandOutlivesItsToolCallAndTheTurn()
    {
        var (tracker, _) = StartBackgroundListener();

        Assert.Equal(1, tracker.RunningCommandsSince(PROCESS_START));
        Assert.Equal(BridgeRules.ANY_CHANNEL, tracker.ListeningSince(PROCESS_START));
    }

    [Fact]
    public void TaskNotificationEndsABackgroundCommand()
    {
        var (tracker, chat) = StartBackgroundListener();

        Apply("""{"type":"queue-operation","operation":"enqueue","timestamp":"2026-11-01T10:05:00Z","content":"<task-notification>\n<task-id>task1</task-id>\n<tool-use-id>bg1</tool-use-id>\n<status>completed</status>\n</task-notification>"}""", tracker, chat);

        Assert.Equal(0, tracker.RunningCommandsSince(PROCESS_START));
        Assert.Null(tracker.ListeningSince(PROCESS_START));
    }

    [Fact]
    public void TaskStopEndsABackgroundCommand()
    {
        var (tracker, chat) = StartBackgroundListener();

        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:05:00Z","message":{"id":"m3","content":[{"type":"tool_use","id":"s1","name":"TaskStop","input":{"task_id":"task1"}}]}}""", tracker, chat);

        Assert.Equal(0, tracker.RunningCommandsSince(PROCESS_START));
    }

    [Fact]
    public void BackgroundCommandFromAnEarlierProcessDoesNotCount()
    {
        var (tracker, _) = StartBackgroundListener();
        var resumedAt = new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero);

        Assert.Equal(0, tracker.RunningCommandsSince(resumedAt));
        Assert.Null(tracker.ListeningSince(resumedAt));
    }

    [Fact]
    public void ABackgroundServerIsNotCountedAsWorkAndEndsLikeAnyOther()
    {
        var tracker = new SessionTracker();
        var chat = new ChatLog();
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:05Z","message":{"id":"m1","content":[{"type":"tool_use","id":"bg1","name":"PowerShell","input":{"command":"python -I C:\\tools\\serve.py","run_in_background":true}}]}}""", tracker, chat);

        Assert.Equal(0, tracker.RunningCommandsSince(PROCESS_START));
        Assert.Equal(1, tracker.RunningServersSince(PROCESS_START));

        tracker.BackgroundEnd("bg1");
        Assert.Equal(0, tracker.RunningServersSince(PROCESS_START));
    }

    [Theory]
    [InlineData("python -I C:\\x\\picks\\serve.py", true)]
    [InlineData("python -m http.server 8080", true)]
    [InlineData("npm run dev", true)]
    [InlineData("UnrealEditor.exe MyGame.uproject -server -log", false)]
    [InlineData("MyGameServer.exe -log", false)]
    [InlineData("python build_server.py", false)]
    [InlineData(".claude/scripts/orch.cmd inbox --wait 300", false)]
    [InlineData("dotnet build AgentManager.sln", false)]
    public void WhatCountsAsAServer(string command, bool expected)
    {
        Assert.Equal(expected, ActivityRules.IsServer(command));
    }

    [Fact]
    public void BackgroundCommandThatFailedToStartDoesNotCount()
    {
        var tracker = new SessionTracker();
        var chat = new ChatLog();
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:05Z","message":{"id":"m1","content":[{"type":"tool_use","id":"bg1","name":"Bash","input":{"command":"sleep 100","run_in_background":true}}]}}""", tracker, chat);
        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:06Z","message":{"content":[{"type":"tool_result","tool_use_id":"bg1","is_error":true,"content":"denied"}]}}""", tracker, chat);

        Assert.Equal(0, tracker.RunningCommandsSince(PROCESS_START));
    }

    [Fact]
    public void AnsweredQuestionNoLongerCounts()
    {
        var tracker = new SessionTracker();
        var chat = new ChatLog();
        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:00Z","message":{"content":"build it"}}""", tracker, chat);
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:05Z","message":{"id":"m1","content":[{"type":"tool_use","id":"q1","name":"AskUserQuestion","input":{"questions":[{"question":"Which shell?"}]}}]}}""", tracker, chat);
        Assert.True(tracker.PendingQuestion);
        Assert.Equal("Which shell?", tracker.QuestionText);

        Apply("""{"type":"user","timestamp":"2026-11-01T10:01:00Z","message":{"content":[{"type":"tool_result","tool_use_id":"q1","content":"WPF"}]}}""", tracker, chat);

        Assert.False(tracker.PendingQuestion);
        Assert.Null(tracker.QuestionAt);
    }

    [Fact]
    public void WrappersAreNotHumanPrompts()
    {
        var tracker = new SessionTracker();
        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:00Z","message":{"content":"<task-notification>done</task-notification>"}}""", tracker, new ChatLog());

        Assert.Null(tracker.LastPromptAt);
    }

    [Fact]
    public void ToolTimeIsSplitByWhatTheToolDid()
    {
        var tracker = new SessionTracker();
        var chat = new ChatLog();
        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:00Z","message":{"content":"go"}}""", tracker, chat);
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:10Z","message":{"id":"m1","content":[{"type":"tool_use","id":"e1","name":"Edit","input":{"file_path":"C:/x/a.cs"}}]}}""", tracker, chat);
        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:11Z","message":{"content":[{"type":"tool_result","tool_use_id":"e1"}]}}""", tracker, chat);
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:15Z","message":{"id":"m2","content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"dotnet build App"}}]}}""", tracker, chat);
        Apply("""{"type":"user","timestamp":"2026-11-01T10:01:15Z","message":{"content":[{"type":"tool_result","tool_use_id":"b1"}]}}""", tracker, chat);

        var split = tracker.Split;
        Assert.Equal(11, split.Code);
        Assert.Equal(4, split.Think);
        Assert.Equal(60, split.Build);
    }

    [Fact]
    public void AnEditorCallIsMarkedOnTheToolWhileItRuns()
    {
        var tracker = new SessionTracker();
        var chat = new ChatLog();
        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:00Z","message":{"id":"m1","content":[{"type":"tool_use","id":"u1","name":"mcp__unreal-mcp__call_tool","input":{}}]}}""", tracker, chat);
        Assert.True(tracker.CurrentTool?.UsesEditor);

        Apply("""{"type":"user","timestamp":"2026-11-01T10:00:01Z","message":{"content":[{"type":"tool_result","tool_use_id":"u1"}]}}""", tracker, chat);
        Assert.Null(tracker.CurrentTool);

        Apply("""{"type":"assistant","timestamp":"2026-11-01T10:00:02Z","message":{"id":"m2","content":[{"type":"tool_use","id":"b1","name":"Bash","input":{"command":"git status"}}]}}""", tracker, chat);
        Assert.False(tracker.CurrentTool?.UsesEditor);
    }
}

public sealed class CodexLinesTests
{
    private static void Apply(CodexThread thread, string json, ChatLog? chat = null)
    {
        CodexLines.Apply(Encoding.UTF8.GetBytes(json), thread, chat ?? new ChatLog(), key => key);
    }

    [Fact]
    public void ItemsAreTimedByTheirOwnClock()
    {
        var thread = new CodexThread("x");
        Apply(thread, """{"timestamp":"2026-11-01T10:00:00Z","type":"event_msg","payload":{"type":"task_started"}}""");
        Apply(thread, """{"timestamp":"2026-11-01T10:00:30Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"FileChange"},"started_at_ms":1793527200000,"completed_at_ms":1793527205000}}""");
        Apply(thread, """{"timestamp":"2026-11-01T10:00:40Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","parsed_cmd":[{"cmd":"dotnet test App.Tests"}]},"started_at_ms":1793527210000,"completed_at_ms":1793527240000}}""");

        Assert.Equal(5, thread.Tracker.Split.Code);
        Assert.Equal(30, thread.Tracker.Split.Test);
        Assert.True(thread.Tracker.TurnOpen);
    }

    [Fact]
    public void TokenCountReadsTheRunningTotals()
    {
        var thread = new CodexThread("x");
        Apply(thread, """{"timestamp":"2026-11-01T10:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000,"cached_input_tokens":600,"output_tokens":50},"last_token_usage":{"input_tokens":900},"model_context_window":258400}}}""");

        Assert.Equal(400, thread.Tracker.Input);
        Assert.Equal(600, thread.Tracker.CacheRead);
        Assert.Equal(1050, thread.Tracker.TotalTokens);
        Assert.Equal(900, thread.Tracker.Context);
        Assert.Equal(258400, thread.Tracker.ContextWindow);
    }

    [Fact]
    public void BlockingQuestionWaitsAndAsyncQuestionDoesNot()
    {
        var thread = new CodexThread("x");
        Apply(thread, """{"timestamp":"2026-11-01T10:00:00Z","type":"response_item","payload":{"type":"function_call","name":"request_user_input","call_id":"c1","arguments":"{\"questions\":[{\"title\":\"Red or blue?\"}]}"}}""");
        Assert.True(thread.Tracker.PendingQuestion);

        Apply(thread, """{"timestamp":"2026-11-01T10:00:09Z","type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":"red"}}""");
        Apply(thread, """{"timestamp":"2026-11-01T10:00:10Z","type":"response_item","payload":{"type":"function_call","name":"request_user_input_async","call_id":"c2","arguments":"{\"questions\":[{\"title\":\"Keep going?\"}]}"}}""");

        Assert.False(thread.Tracker.PendingQuestion);
        Assert.Equal("Keep going?", thread.Tracker.QuestionText);
    }
}

public sealed class RulesTests
{
    [Theory]
    [InlineData("Build.bat MyGameEditor Win64 Development", Activity.Build)]
    [InlineData("dotnet test App.Tests", Activity.Test)]
    [InlineData("python .claude/scripts/tools/ue_python.py check.py", Activity.Test)]
    [InlineData(".claude/scripts/orch.cmd acquire --kind PIE --wait 540", Activity.Lane)]
    [InlineData("git status", Activity.Tools)]
    public void CommandsAreClassified(string command, Activity expected)
    {
        Assert.Equal(expected, ActivityRules.ForCommand(command));
    }

    [Theory]
    [InlineData("mcp__unreal-mcp__call_tool", null, true)]
    [InlineData("mcp__unreal-mcp__list_toolsets", null, true)]
    [InlineData("Bash", "python .claude/scripts/tools/ue_python.py check.py", true)]
    [InlineData("PowerShell", "curl.exe -s http://127.0.0.1:8000/mcp", true)]
    [InlineData("exec_command", "python .claude/scripts/tools/ue_python.py check.py", true)]
    [InlineData("Bash", "Build.bat MyGameEditor Win64 Development", false)]
    [InlineData("Bash", "git status", false)]
    [InlineData("Read", "ue_python.py", false)]
    public void EditorCallsAreRecognised(string name, string? command, bool expected)
    {
        Assert.Equal(expected, ActivityRules.UsesEditor(name, command));
    }

    [Theory]
    [InlineData("Done. Want me to build it?", true)]
    [InlineData("**Should I push?**", true)]
    [InlineData("Why does it fail? Because the path is wrong.\nFixed it.", false)]
    [InlineData("```\nis this code?\n```\nAll good.", false)]
    public void QuestionsAreFoundAtTheEnd(string text, bool asks)
    {
        Assert.Equal(asks, Questions.Find(text) is not null);
    }
}

public sealed class BridgeRulesTests
{
    [Fact]
    public void APostOnAChannelReachesEveryoneButItsSender()
    {
        var members = new Dictionary<string, string> { ["claude:1"] = "docs agent", ["agent:astra-docs"] = "Astra" };

        Assert.Equal("Astra", BridgeRules.Audience(members, "claude:1"));
        Assert.Equal("docs agent", BridgeRules.Audience(members, "agent:astra-docs"));
    }

    [Fact]
    public void AChannelNobodyElseJoinedSaysSoInsteadOfNamingItself()
    {
        var members = new Dictionary<string, string> { ["claude:1"] = "docs agent" };

        Assert.Equal(BridgeRules.NOBODY_YET, BridgeRules.Audience(members, "claude:1"));
    }

    [Fact]
    public void SeveralWindowsOfOneAstraCountOnceInAnAudience()
    {
        var members = new Dictionary<string, string> { ["claude:1"] = "docs agent", ["agent:astra-a"] = "Astra", ["codex:2"] = "Astra", ["claude:3"] = "theme agent" };

        Assert.Equal("Astra, theme agent", BridgeRules.Audience(members, "claude:1"));
    }

    [Theory]
    [InlineData(".claude/scripts/orch.cmd inbox login-form --wait 540", "login-form")]
    [InlineData("until out=$(.claude/scripts/orch.cmd inbox --wait 300); [ \"$out\" != \"inbox empty\" ]; do :; done", "*")]
    [InlineData("python .claude/scripts/orch/orch.py inbox test-suite --wait 60", "test-suite")]
    [InlineData(".claude/scripts/orch.cmd inbox login-form", null)]
    [InlineData(".claude/scripts/orch.cmd msg --to login-form \"hi\"", null)]
    public void ListeningCommandsNameTheirChannel(string command, string? channel)
    {
        Assert.Equal(channel, BridgeRules.ListenChannel(command));
    }

    private static readonly DateTimeOffset NOW = new(2026, 11, 2, 22, 0, 0, TimeSpan.Zero);

    private static readonly BridgePeer ASTRA = new("Astra", true);

    private static readonly BridgePeer ASTRA_GONE = ASTRA with { IsAlive = false };

    private static BridgeInfo Channel(string name, int messages, TimeSpan lastAgo, params BridgePeer[] peers)
    {
        return new BridgeInfo(name, peers, messages, NOW - lastAgo, "a", false);
    }

    [Fact]
    public void AChannelNobodyJoinedNeverShows()
    {
        var apiDocs = Channel("api-docs", 1, TimeSpan.FromMinutes(24));
        var dbMigration = Channel("db-migration", 4, TimeSpan.FromHours(1.8));

        Assert.False(BridgeRules.IsLive(apiDocs, null, NOW));
        Assert.False(BridgeRules.IsLive(apiDocs, "api-docs", NOW));
        Assert.False(BridgeRules.IsLive(dbMigration, null, NOW));
    }

    [Fact]
    public void AJoinedChannelShowsWhileItIsInUseAndThenClearsItself()
    {
        var justUsed = Channel("release-notes", 38, TimeSpan.FromMinutes(3), ASTRA);
        var wentQuiet = Channel("release-notes", 38, TimeSpan.FromHours(1.2), ASTRA);
        var atTheLimit = Channel("release-notes", 38, BridgeRules.QUIET_AFTER, ASTRA);

        Assert.True(BridgeRules.IsLive(justUsed, null, NOW));
        Assert.False(BridgeRules.IsLive(wentQuiet, null, NOW));
        Assert.False(BridgeRules.IsLive(atTheLimit, null, NOW));
    }

    [Fact]
    public void AChannelWhosePeerHasGoneClearsAtOnceEvenWhileWaitedOn()
    {
        var uiTheme = Channel("ui-theme", 5, TimeSpan.FromMinutes(2), ASTRA_GONE);

        Assert.False(BridgeRules.IsLive(uiTheme, null, NOW));
        Assert.False(BridgeRules.IsLive(uiTheme, "ui-theme", NOW));
    }

    [Fact]
    public void OneLivePeerIsEnoughOnAChannelOfSeveral()
    {
        var bridge = Channel("test-suite", 9, TimeSpan.FromMinutes(5), ASTRA_GONE, new BridgePeer("Claude", true));

        Assert.True(BridgeRules.IsLive(bridge, null, NOW));
    }

    [Fact]
    public void WaitingOnAChannelByNameKeepsItWhileThePeerIsThereHoweverOld()
    {
        var bridge = Channel("login-form", 9, TimeSpan.FromHours(30), ASTRA);

        Assert.True(BridgeRules.IsLive(bridge, "login-form", NOW));
    }

    [Fact]
    public void WaitingOnAnotherChannelOrTheBareInboxKeepsNothingAlive()
    {
        var quiet = Channel("login-form", 9, TimeSpan.FromHours(3), ASTRA);

        Assert.False(BridgeRules.IsLive(quiet, BridgeRules.ANY_CHANNEL, NOW));
        Assert.False(BridgeRules.IsLive(quiet, "test-suite", NOW));
    }

    [Fact]
    public void AJoinedChannelWithNoMessagesShowsOnlyWhileWaitedOn()
    {
        var bridge = new BridgeInfo("test-suite", [ASTRA], 0, null, null, false);

        Assert.False(BridgeRules.IsLive(bridge, null, NOW));
        Assert.True(BridgeRules.IsLive(bridge, "test-suite", NOW));
    }

    [Fact]
    public void TwoChannelsWithOneAstraShowTheNewestFirstSoTheCardCountsTheOther()
    {
        var apiDocs = Channel("api-docs", 3, TimeSpan.FromMinutes(2), ASTRA);
        var dbMigration = Channel("db-migration", 4, TimeSpan.FromMinutes(20), ASTRA);
        var waitedOn = Channel("test-suite", 9, TimeSpan.FromMinutes(40), ASTRA) with { IsListening = true };
        var nobodyJoined = Channel("cache-layer", 1, TimeSpan.FromMinutes(1));
        var peerGone = Channel("ui-theme", 5, TimeSpan.FromMinutes(1), ASTRA_GONE);

        var shown = BridgeRules.OnCard([dbMigration, nobodyJoined, waitedOn, peerGone, apiDocs], null, NOW);

        Assert.Equal(["api-docs", "db-migration", "test-suite"], shown.Select(b => b.Channel).ToArray());
    }

    [Theory]
    [InlineData("agent:astra-api-docs", null, null, "agent astra-api-docs", "Astra")]
    [InlineData("agent:astra-db-migration", "gpt-6-astra", "Run astra-db-migration task", "x", "Astra")]
    [InlineData("codex:13284:639291769646349240", "gpt-6-astra", "Run orch inbox command", "codex 13284", "Astra")]
    [InlineData("codex:13284:639291769646349240", "gpt-5-codex", "Fix the build", "codex 13284", "Fix the build")]
    [InlineData("codex:13284:639291769646349240", null, null, "codex 13284", "Codex")]
    [InlineData("agent:reviewer", null, null, "agent reviewer", "reviewer")]
    [InlineData("claude:55916:639292264656896670", "Opus 5.5", "Cache eviction rewrite", "x", "Cache eviction rewrite")]
    [InlineData("claude:55916:639292264656896670", null, null, "the owner's title", "the owner's title")]
    public void PeersGoByTheNameTheUserKnows(string owner, string? model, string? cardName, string ownerName, string expected)
    {
        Assert.Equal(expected, BridgeRules.PeerName(owner, model, cardName, ownerName));
    }

    [Theory]
    [InlineData("agent:astra-api-docs", AgentKind.Codex)]
    [InlineData("codex:13284:639291769646349240", AgentKind.Codex)]
    [InlineData("claude:55916:639292264656896670", AgentKind.Claude)]
    [InlineData("agent:reviewer", null)]
    public void OwnersMatchOnlyTheCardsTheyCanBe(string owner, AgentKind? kind)
    {
        Assert.Equal(kind, BridgeRules.OwnerKind(owner));
    }
}

public sealed class BridgeChannelTests
{
    [Fact]
    public void ChannelsAreFoundInInboxAndMsgCommands()
    {
        var command = "text(await tools.exec_command({cmd:'.claude/scripts/orch.cmd inbox login-form --wait 540'})); .claude/scripts/orch.cmd msg --to test-suite \"x\"; orch.cmd msg --to claude:1:2 \"y\"";

        Assert.Equal(["login-form", "test-suite"], BridgeRules.MentionedChannels(command).ToArray());
    }
}
