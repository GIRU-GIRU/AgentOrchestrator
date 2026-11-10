using AgentManager.Core;
using AgentManager.Core.Model;

namespace AgentManager.Tests;

public sealed class SendAndRestoreTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static AgentSnapshot Agent(string surface = Delivery.CONSOLE_SURFACE, int? pid = 100, bool alive = true, AgentKind kind = AgentKind.Claude)
    {
        return new AgentSnapshot
        {
            Key = "claude:a",
            Kind = kind,
            SessionId = "a",
            Name = "a",
            Surface = surface,
            Pid = pid,
            IsAlive = alive,
            HasPrompt = true,
            LastEventAt = NOW,
            TranscriptPath = "a.jsonl",
            ResumeCommand = "claude --resume a",
        };
    }

    [Fact]
    public void ConsoleAgentIsTypedInto()
    {
        Assert.Equal(DeliveryRoute.Console, Delivery.Plan(Agent(), hasInboxOwner: true).Route);
    }

    [Fact]
    public void OpenDialogIsNeverTypedInto()
    {
        Assert.Equal(DeliveryRoute.Skip, Delivery.Plan(Agent() with { WaitingFor = "permission" }, true).Route);
        Assert.Equal(DeliveryRoute.Skip, Delivery.Plan(Agent() with { PendingQuestion = true }, true).Route);
    }

    [Fact]
    public void ClaudeWithoutAConsoleGoesThroughTheInbox()
    {
        Assert.Equal(DeliveryRoute.Inbox, Delivery.Plan(Agent(surface: "DESKTOP"), hasInboxOwner: true).Route);
        Assert.Equal(DeliveryRoute.Skip, Delivery.Plan(Agent(surface: "DESKTOP"), hasInboxOwner: false).Route);
    }

    [Fact]
    public void CodexWithoutAKnownWindowIsSkippedNotSentToTheSharedInbox()
    {
        var codex = Agent(pid: null, kind: AgentKind.Codex);

        Assert.Equal(DeliveryRoute.Skip, Delivery.Plan(codex, hasInboxOwner: true).Route);
    }

    [Fact]
    public void ClosedAgentIsSkipped()
    {
        Assert.Equal(DeliveryRoute.Skip, Delivery.Plan(Agent(alive: false), true).Route);
    }

    [Fact]
    public void OnlyAnEarlierLogonOffersARestore()
    {
        var logon = NOW.AddHours(-3);

        Assert.True(SessionRestore.IsEarlierLogon(logon.AddDays(-1), logon));
        Assert.False(SessionRestore.IsEarlierLogon(logon.AddSeconds(2), logon));
        Assert.False(SessionRestore.IsEarlierLogon(null, logon));
        Assert.False(SessionRestore.IsEarlierLogon(logon, null));
    }

    [Fact]
    public void OnlyRunningAgentsWithAConversationAreKeptForRestore()
    {
        Assert.True(SessionRestore.IsRestorable(Agent()));
        Assert.False(SessionRestore.IsRestorable(Agent(alive: false)));
        Assert.False(SessionRestore.IsRestorable(Agent() with { HasPrompt = false }));
    }

    [Fact]
    public void SessionsTheOrchestratorIsRelaunchingAreTheResumingTasks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "agentmanager-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(dir, "orch.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE tasks (id TEXT, session_id TEXT, state TEXT); INSERT INTO tasks VALUES ('1', 'sess-A', 'resuming'), ('2', 'sess-B', 'orphaned'), ('3', '', 'resuming');";
            command.ExecuteNonQuery();
        }

        var sessions = new AgentManager.Core.Orchestration.OrchestratorReader(dir).RelaunchingSessions();

        Assert.Equal(["sess-A"], sessions);
    }

    [Fact]
    public void RecordKeepsWhatARestoreNeeds()
    {
        var record = SessionRestore.Record(Agent() with { Cwd = "C:/work", Prompts = [new PromptLine(NOW, "fix the door")] });

        Assert.Equal(("claude:a", "claude --resume a", "C:/work", "fix the door"), (record.Key, record.ResumeCommand, record.Cwd, record.LastPrompt));
        Assert.True(SessionRestore.IsSameSet([record], [SessionRestore.Record(Agent() with { Cwd = "C:/work" })]));
        Assert.False(SessionRestore.IsSameSet([record], []));
    }
}
