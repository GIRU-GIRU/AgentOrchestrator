using System.Text.Json;
using AgentManager.Core.Chat;
using AgentManager.Core.Claude;
using AgentManager.Core.Codex;
using AgentManager.Core.Orchestration;
using Microsoft.Data.Sqlite;

namespace AgentManager.Tests;

public sealed class CodexEncryptedTests
{
    /// <summary>Real shape: a version byte 0x80, a timestamp and the payload, base64url with padding.</summary>
    internal const string TOKEN = "gAAAAABq6l-UjNOigXQXDesFXkOECM11tOKWf_rDe9ubcuGGR_p6NIVrMWYTXAQSDHaNzM-EpImU2qOygLzzCPxFb6xVaWp7629mgH6sAGTQUgho51pbFMgaSSJSEUBx-_krbBIzHSAnaWI_spHIIo6DRN4F-5GpFDUSd-UwAqm23H6rIISoCdG9rREMwCjfEahff=";

    [Fact]
    public void ATokenIsEncryptedAndPlainWordsAreNot()
    {
        Assert.True(CodexEncrypted.IsEncrypted(TOKEN));
        Assert.True(CodexEncrypted.IsEncrypted("  " + TOKEN + "\n"));
        Assert.False(CodexEncrypted.IsEncrypted(null));
        Assert.False(CodexEncrypted.IsEncrypted(""));
        Assert.False(CodexEncrypted.IsEncrypted("Independent review complete, no new revision defects found."));
        Assert.False(CodexEncrypted.IsEncrypted("gAAAAshort"));
        Assert.False(CodexEncrypted.IsEncrypted("gAAAA " + TOKEN));
        Assert.False(CodexEncrypted.IsEncrypted("x" + TOKEN));
    }

    [Fact]
    public void TheChatShowsANoteAndTwoTokensStayTwoRows()
    {
        var chat = new ChatLog();
        var thread = new CodexThread("rollout.jsonl") { Id = "parent" };
        CodexLines.Apply(SendMessage("call_1", TOKEN), thread, chat, _ => "name");
        CodexLines.Apply(SendMessage("call_2", TOKEN.Replace("gAAAAABq6l-U", "gAAAAABq6l_N")), thread, chat, _ => "name");

        var rows = chat.Snapshot();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(CodexEncrypted.NOTE, row.Text));
    }

    [Fact]
    public void APlainMessageIsLeftAlone()
    {
        var chat = new ChatLog();
        CodexLines.Apply(SendMessage("call_1", "look at the diagonal"), new CodexThread("rollout.jsonl") { Id = "parent" }, chat, _ => "name");

        Assert.Equal("look at the diagonal", chat.Snapshot().Single().Text);
    }

    private static byte[] SendMessage(string callId, string message)
    {
        var arguments = JsonSerializer.Serialize(new { target = "diagonal_review", message });
        var line = JsonSerializer.Serialize(new
        {
            timestamp = "2026-11-03T22:26:30.301Z",
            type = "response_item",
            payload = new { type = "function_call", name = "send_message", arguments, call_id = callId },
        });
        return System.Text.Encoding.UTF8.GetBytes(line);
    }
}

public sealed class ClaudeOriginReaderTests
{
    private const string STAMP = "\"timestamp\":\"2026-11-03T17:22:12.000Z\"";

    [Fact]
    public void MetaHookAndWrappedLinesAreNotPrompts()
    {
        var found = Read(
            false,
            """{"type":"user","isMeta":true,"message":{"content":"Base directory for this skill: x"}}""",
            """{"type":"user","message":{"content":"<system-reminder>be good</system-reminder>"}}""",
            """{"type":"user","message":{"content":"<command-name>/color</command-name>"}}""",
            """{"type":"user","message":{"content":"Caveat: local commands"}}""",
            """{"type":"user","message":{"content":"This session is being continued from an earlier one"}}""",
            """{"type":"system","subtype":"stop_hook_summary","content":"hook ran"}""",
            """{"type":"attachment","attachment":{"type":"hook_success","content":"ok"}}""",
            """{"type":"user","toolUseResult":{"a":1},"message":{"content":"tool text"}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":"output"}]}}""",
            """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"a task ended"}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"I will start"}]}}""",
            "{not json");

        Assert.Empty(found);
    }

    [Fact]
    public void ThePromptsAreInOrderAsStringsAndAsBlocks()
    {
        var found = Read(
            false,
            "{\"type\":\"user\"," + STAMP + ",\"message\":{\"content\":\"make the button blue\"}}",
            """{"type":"user","message":{"content":[{"type":"image"},{"type":"text","text":"like this one"}]}}""");

        Assert.Equal(["make the button blue", "like this one"], found.Select(p => p.Text));
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 17, 22, 12, TimeSpan.Zero), found[0].At!.Value.ToUniversalTime());
    }

    [Fact]
    public void AMainTranscriptSkipsSidechainLines()
    {
        var found = Read(false, """{"type":"user","isSidechain":true,"message":{"content":"a subagent's prompt"}}""");

        Assert.Empty(found);
    }

    [Fact]
    public void ATypedPromptQueuedMidTurnCountsButEnvelopesAndNoticesDoNot()
    {
        var found = Read(
            false,
            """{"type":"queue-operation","operation":"enqueue","content":"also fix the footer"}""",
            """{"type":"queue-operation","operation":"dequeue","content":"not this"}""",
            """{"type":"queue-operation","operation":"enqueue","content":"<task-notification><status>completed</status></task-notification>"}""",
            """{"type":"queue-operation","operation":"enqueue","content":"[Cross-session idle notice] \"uds:pipe\" went idle"}""",
            """{"type":"queue-operation","operation":"enqueue","content":"<cross-session-message from=\"uds:x\">hi</cross-session-message>"}""");

        Assert.Equal(["also fix the footer"], found.Select(p => p.Text));
    }

    [Fact]
    public void ASubagentsFirstLineIsItsParentsPromptWhateverItStartsWith()
    {
        var found = Read(
            true,
            """{"type":"attachment","attachment":{"type":"deferred_tools_delta"}}""",
            """{"type":"user","isSidechain":true,"message":{"content":"<task>Port the old parser tests</task>"}}""",
            """{"type":"user","isSidechain":true,"isMeta":true,"message":{"content":"<system-reminder>hand back</system-reminder>"}}""",
            """{"type":"user","isSidechain":true,"message":{"content":"<agent-message from=\"a\">more</agent-message>"}}""",
            """{"type":"user","isSidechain":true,"message":{"content":[{"type":"tool_result","tool_use_id":"t","content":"output"}]}}""");

        var prompt = Assert.Single(found);
        Assert.Equal("<task>Port the old parser tests</task>", prompt.Text);
        Assert.Equal("asked by its parent", prompt.Label);
    }

    [Fact]
    public void AForkGetsItsDirectiveWithoutTheBoilerplateEveryForkShares()
    {
        const string OPENING = "<fork-boilerplate>\\nYou are a worker fork.\\n</fork-boilerplate>\\n\\nYour directive: Build the supervisor agent";
        var found = Read(
            true,
            "{\"type\":\"user\",\"isSidechain\":true,\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t\",\"content\":\"Fork started\"},{\"type\":\"text\",\"text\":\"" + OPENING + "\"}]}}",
            """{"type":"user","isSidechain":true,"message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":"Launching skill"}]}}""");

        Assert.Equal("Build the supervisor agent", Assert.Single(found).Text);
    }

    private static List<OriginPrompt> Read(bool isSubagent, params string[] lines)
    {
        var reader = new ClaudeOriginReader(isSubagent);
        return lines.Select(line => reader.Read(System.Text.Encoding.UTF8.GetBytes(line))).OfType<OriginPrompt>().ToList();
    }
}

public sealed class CodexOriginReaderTests
{
    private const string CHILD_META = """{"timestamp":"2026-11-03T22:25:11.970Z","type":"session_meta","payload":{"id":"child","session_id":"root","parent_thread_id":"root","agent_path":"/root/diagonal_review","agent_nickname":"Mencius"}}""";

    private const string PARENT_META = """{"timestamp":"2026-11-03T22:25:11.970Z","type":"session_meta","payload":{"id":"root","session_id":"root","thread_source":"user"}}""";

    private const string INHERITED_USER = """{"timestamp":"2026-11-03T22:25:11.990Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Run .claude/scripts/orch.cmd inbox x"}]}}""";

    [Fact]
    public void AUserMessageItemIsAPromptAndTheHarnesssAreNot()
    {
        var found = Read(
            PARENT_META,
            """{"timestamp":"2026-11-03T13:37:03Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"# AGENTS.md instructions for C:\\x"}]}}""",
            """{"timestamp":"2026-11-03T13:37:04Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"text","text":"# AGENTS.md instructions for C:\\x"}]}}}""",
            """{"timestamp":"2026-11-03T13:37:05Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"text","text":"make the logo bigger"}]}}}""",
            """{"timestamp":"2026-11-03T13:37:06Z","type":"event_msg","payload":{"type":"user_message","message":"and slower"}}""",
            """{"timestamp":"2026-11-03T13:37:07Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution","aggregated_output":"\"UserMessage\""}}}""");

        Assert.Equal(["make the logo bigger", "and slower"], found.Select(p => p.Text));
    }

    [Fact]
    public void ASubagentsEncryptedTaskSaysSoAndInheritedPromptsAreNotItsOwn()
    {
        var found = Read(
            CHILD_META,
            PARENT_META,
            INHERITED_USER,
            AgentMessage("/root", "/root/diagonal_review", "Message Type: NEW_TASK\nTask name: /root/diagonal_review\nSender: /root\nPayload:\n", CodexEncryptedTests.TOKEN),
            AgentMessage("/root", "/root/diagonal_review", "Message Type: MESSAGE\nTask name: /root/diagonal_review\nSender: /root\nPayload:\n", CodexEncryptedTests.TOKEN),
            AgentMessage("/root/diagonal_review", "/root", "Message Type: MESSAGE\nPayload:\n", CodexEncryptedTests.TOKEN));

        var task = Assert.Single(found);
        Assert.True(task.IsNote);
        Assert.Equal(CodexEncrypted.NOTE, task.Text);
        Assert.Equal("task from /root", task.Label);
    }

    [Fact]
    public void ASubagentsPlainTaskIsShownWithoutItsHeader()
    {
        var found = Read(
            CHILD_META,
            AgentMessage("/root", "/root/diagonal_review", "Message Type: NEW_TASK\nTask name: /root/diagonal_review\nSender: /root\nPayload:\nReview the diagonal", null));

        Assert.Equal("Review the diagonal", Assert.Single(found).Text);
    }

    [Fact]
    public void AnOlderSubagentGotItsTaskAsAUserMessage()
    {
        var found = Read(
            """{"timestamp":"2026-10-18T04:07:46Z","type":"session_meta","payload":{"id":"child","parent_thread_id":"root","agent_nickname":"Leibniz"}}""",
            PARENT_META,
            INHERITED_USER,
            """{"timestamp":"2026-10-18T04:07:47Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","content":[{"type":"text","text":"Own caching improvements"}]}}}""");

        Assert.Equal("Own caching improvements", Assert.Single(found).Text);
    }

    [Fact]
    public void OnlyTheFirstMetaSaysWhoTheThreadIs()
    {
        var reader = new CodexOriginReader();
        reader.Read(System.Text.Encoding.UTF8.GetBytes(CHILD_META));
        reader.Read(System.Text.Encoding.UTF8.GetBytes(PARENT_META));

        Assert.Equal("child", reader.Meta?.Id);
        Assert.Equal("Codex subagent Mencius, /root/diagonal_review", CodexOrigins.Describe(reader.Meta));
    }

    private static string AgentMessage(string author, string recipient, string header, string? token)
    {
        object[] content = token is null
            ? [new { type = "input_text", text = header }]
            : [new { type = "input_text", text = header }, new { type = "encrypted_content", encrypted_content = token }];
        return JsonSerializer.Serialize(new
        {
            timestamp = "2026-11-03T22:25:13.494Z",
            type = "response_item",
            payload = new { type = "agent_message", author, recipient, content },
        });
    }

    private static List<OriginPrompt> Read(params string[] lines)
    {
        var reader = new CodexOriginReader();
        return lines.Select(line => reader.Read(System.Text.Encoding.UTF8.GetBytes(line))).OfType<OriginPrompt>().ToList();
    }
}

public sealed class OrchestratorBriefTests
{
    private const string POINTER = "Run .claude/scripts/orch.cmd --as astra-search-index inbox search-index and do what it says. Put --as astra-search-index before the command word on every orch.cmd call in this job.";

    [Fact]
    public void APointerNamesTheChannelAndWhoReadsIt()
    {
        var pointer = OrchestratorBriefs.PointerIn(POINTER);

        Assert.Equal("search-index", pointer?.Work);
        Assert.Equal("agent:astra-search-index", pointer?.Reader);
        Assert.Equal("api-docs", OrchestratorBriefs.PointerIn("Run `.claude/scripts/orch.cmd inbox api-docs` and do what it says")?.Work);
        Assert.Null(OrchestratorBriefs.PointerIn("Run `.claude/scripts/orch.cmd inbox api-docs` and do what it says")?.Reader);
    }

    [Fact]
    public void APromptThatIsAJobOfItsOwnIsNotAPointer()
    {
        Assert.Null(OrchestratorBriefs.PointerIn("make the button bluer"));
        Assert.Null(OrchestratorBriefs.PointerIn("Check orch.cmd inbox api-docs, then " + new string('x', 500)));
    }

    [Fact]
    public void TheBriefIsWhatWasWaitingOnTheChannel()
    {
        var rows = new[]
        {
            Row("channel:search", "Message on search from the claude [claude:40:1]:\n# Search brief"),
            Row("agent:astra-search", "Message on search from the claude [claude:40:1]:\nTimings, read from the assets"),
            Row("claude:40:1", "Message on search from  [agent:astra-search]:\nBrief received."),
        };

        var brief = Assert.Single(OrchestratorBriefs.Pick(rows, new InboxPointer("search", "agent:astra-search"), 2));

        Assert.Equal("# Search brief", brief.Text);
        Assert.Equal("brief from the claude on #search", brief.Label);
    }

    [Fact]
    public void WithNothingWaitingItIsTheFirstMessageSentToIt()
    {
        var rows = new[]
        {
            Row("agent:astra-search", "Message on search from  [agent:astra-search]:\nmy own words"),
            Row("agent:astra-search", "Message on other from the claude [claude:40:1]:\nanother job"),
            Row("agent:astra-search", "Message on search from the claude [claude:40:1]:\nthe first ask"),
            Row("agent:astra-search", "Message on search from the claude [claude:40:1]:\nthe conversation"),
        };

        var brief = Assert.Single(OrchestratorBriefs.Pick(rows, new InboxPointer("search", "agent:astra-search"), 2));

        Assert.Equal("the first ask", brief.Text);
    }

    [Fact]
    public void ANamedOwnerIsAPointerButOnlyAnAstraSaysItsChannel()
    {
        Assert.Equal(new InboxPointer("search", "agent:astra-search"), OrchestratorBriefs.PointerOfOwner("agent:astra-search"));
        Assert.Equal(new InboxPointer(null, "agent:helper"), OrchestratorBriefs.PointerOfOwner("agent:helper"));
        Assert.Null(OrchestratorBriefs.PointerOfOwner("claude:40:1"));
    }

    private static InboxRow Row(string owner, string text)
    {
        return new InboxRow(owner, null, text);
    }
}

/// <summary>Built like the real thing: transcripts, rollouts and an orch.db in a scratch folder, read by the real class.</summary>
public sealed class SessionOriginsTests : IDisposable
{
    private const string SESSION = "11111111-2222-3333-4444-555555555555";

    private const string ROOT = "01a1a1a0-ab8b-7772-9eab-d5df36ed21e8";

    private const string CHILD = "01a1a384-3113-7af3-88ed-2a6dc7c3955a";

    private const string POINTER = "Run .claude/scripts/orch.cmd --as astra-search inbox search and do what it says.";

    private readonly string dir = Path.Combine(Path.GetTempPath(), "am-origins-" + Guid.NewGuid().ToString("N"));

    private readonly string project;

    private readonly string day;

    private readonly SessionOrigins origins;

    public SessionOriginsTests()
    {
        project = Path.Combine(dir, "claude", "projects", "C--work");
        day = Path.Combine(dir, "codex", "sessions", "2026", "11", "03");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(day);
        Directory.CreateDirectory(Path.Combine(dir, "state"));
        origins = new SessionOrigins(Path.Combine(dir, "claude"), Path.Combine(dir, "codex"));
    }

    [Fact]
    public void ASessionShowsItsFirstPromptsNotItsLatest()
    {
        var transcript = Path.Combine(project, SESSION + ".jsonl");
        File.WriteAllLines(transcript, new[] { "first", "second", "third", "fourth" }.Select(Typed));

        var shown = origins.Find("claude:" + SESSION, null, []);

        Assert.Equal(["first", "second", "third"], shown.Prompts.Select(p => p.Text));
        Assert.Equal("Claude session", shown.What);
    }

    [Fact]
    public void AShortSessionPicksUpWhatItsAgentAppendsLater()
    {
        var transcript = Path.Combine(project, SESSION + ".jsonl");
        File.WriteAllLines(transcript, [Typed("first")]);
        Assert.Single(origins.Find("claude:" + SESSION, null, []).Prompts);

        File.AppendAllLines(transcript, [Typed("first"), Typed("second")]);

        Assert.Equal(["first", "second"], origins.Find("claude:" + SESSION, null, []).Prompts.Select(p => p.Text));
    }

    [Fact]
    public void ALineAnAgentIsStillWritingWaitsForItsNewline()
    {
        var transcript = Path.Combine(project, SESSION + ".jsonl");
        File.WriteAllText(transcript, Typed("first") + "\n" + Typed("second")[..10]);

        Assert.Single(origins.Find("claude:" + SESSION, null, []).Prompts);
    }

    [Fact]
    public void ASubagentIsFoundUnderWhicheverSessionHasIt()
    {
        var subagents = Path.Combine(project, SESSION, "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllLines(Path.Combine(subagents, "agent-a400163e62349b83a.jsonl"), [Typed("Port the old parser tests")]);

        var shown = origins.Find("claude-sub:a400163e62349b83a", null, []);

        Assert.Equal("Port the old parser tests", Assert.Single(shown.Prompts).Text);
        Assert.Equal("asked by its parent", shown.Prompts[0].Label);
    }

    [Fact]
    public void AMissingTranscriptIsAPlainLine()
    {
        var shown = origins.Find("claude:" + SESSION, null, []);

        Assert.Empty(shown.Prompts);
        Assert.False(shown.IsEmpty);
        Assert.False(string.IsNullOrEmpty(shown.Note));
    }

    [Fact]
    public void AnAstraShowsThePointerOnceAndTheBriefItRead()
    {
        WriteRoot(POINTER, POINTER, "what" + POINTER, "cont");
        WriteInbox(("channel:search", "Message on search from the claude [claude:40:1]:\n# Search brief"));

        var shown = origins.Find("codex:" + ROOT, null, [Path.Combine(dir, "state")]);

        Assert.Equal([POINTER, "# Search brief", "cont"], shown.Prompts.Select(p => p.Text));
        Assert.Equal("Codex thread", shown.What);
    }

    [Fact]
    public void AnAstraWithNoOrchestratorStillShowsItsPrompt()
    {
        WriteRoot(POINTER);

        Assert.Equal([POINTER], origins.Find("codex:" + ROOT, null, []).Prompts.Select(p => p.Text));
    }

    [Fact]
    public void ACodexAddressIsReadAgainstItsSender()
    {
        WriteRoot("the root's job");
        WriteChild();

        var toChild = origins.Find("codex:diagonal_review", "codex:" + ROOT, []);
        Assert.Equal(CodexEncrypted.NOTE, Assert.Single(toChild.Prompts).Text);
        Assert.Equal("Codex subagent Mencius, /root/diagonal_review", toChild.What);

        var toRoot = origins.Find("codex:/root", "codex:" + CHILD, []);
        Assert.Equal("the root's job", Assert.Single(toRoot.Prompts).Text);

        var unknown = origins.Find("codex:/root/other", "codex:" + ROOT, []);
        Assert.Empty(unknown.Prompts);
        Assert.Contains("/root/other", unknown.Note);
    }

    [Fact]
    public void TheUserAndAnUnknownKeyHaveNoPopup()
    {
        Assert.True(origins.Find("orch:user", null, []).IsEmpty);
        Assert.True(origins.Find("somewhere:else", null, []).IsEmpty);
    }

    [Fact]
    public void AnOwnerWithNoSessionIsAPlainLine()
    {
        Assert.False(string.IsNullOrEmpty(origins.Find("orch:claude:40:1", null, []).Note));
        Assert.Contains("Codex", origins.Find("orch:codex:13284:1", null, []).Note);
    }

    [Fact]
    public void AClaudeOwnerLeadsToItsSession()
    {
        File.WriteAllLines(Path.Combine(project, SESSION + ".jsonl"), [Typed("first")]);
        WriteInbox();
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "state", "orch.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"INSERT INTO owners VALUES ('claude:40:1', '{SESSION}')";
            command.ExecuteNonQuery();
        }

        var shown = origins.Find("orch:claude:40:1", null, [Path.Combine(dir, "state")]);

        Assert.Equal("first", Assert.Single(shown.Prompts).Text);
    }

    [Fact]
    public void AnAstraOwnerWithNoThreadStillHasItsBrief()
    {
        WriteInbox(("channel:search", "Message on search from the claude [claude:40:1]:\n# Search brief"));

        var shown = origins.Find("orch:agent:astra-search", null, [Path.Combine(dir, "state")]);

        Assert.Equal("# Search brief", Assert.Single(shown.Prompts).Text);
    }

    [Fact]
    public void ALongPromptIsTrimmedAtASpaceAndSaysHowMuchWasLeft()
    {
        var (text, cut) = SessionOrigins.Trim(string.Join(' ', Enumerable.Repeat("lorem", 200)), 100);

        Assert.True(text.Length <= 100);
        Assert.EndsWith("lorem", text);
        Assert.True(cut > 0);
        Assert.Equal(("short\n\nlines", 0), SessionOrigins.Trim("  short\r\n\r\n\r\n\r\nlines\n", 100));
    }

    [Fact]
    public void ATrimNeverSplitsASurrogatePair()
    {
        var (text, _) = SessionOrigins.Trim(new string('x', 99) + "\U0001F600" + new string('y', 50), 100);

        Assert.Equal(new string('x', 99), text);
    }

    private static string Typed(string text)
    {
        return JsonSerializer.Serialize(new { type = "user", timestamp = "2026-11-03T17:22:12.000Z", message = new { content = text } });
    }

    private void WriteRoot(params string[] prompts)
    {
        var meta = JsonSerializer.Serialize(new { timestamp = "2026-11-03T13:37:03Z", type = "session_meta", payload = new { id = ROOT, session_id = ROOT, thread_source = "user" } });
        var lines = prompts.Select(prompt => JsonSerializer.Serialize(new
        {
            timestamp = "2026-11-03T13:37:04Z",
            type = "event_msg",
            payload = new { type = "item_completed", item = new { type = "UserMessage", content = new[] { new { type = "text", text = prompt } } } },
        }));
        File.WriteAllLines(Path.Combine(day, $"rollout-2026-11-03T13-37-03-{ROOT}.jsonl"), lines.Prepend(meta));
    }

    private void WriteChild()
    {
        var meta = JsonSerializer.Serialize(new { timestamp = "2026-11-03T22:25:11Z", type = "session_meta", payload = new { id = CHILD, session_id = ROOT, parent_thread_id = ROOT, agent_path = "/root/diagonal_review", agent_nickname = "Mencius" } });
        var task = JsonSerializer.Serialize(new
        {
            timestamp = "2026-11-03T22:25:13Z",
            type = "response_item",
            payload = new
            {
                type = "agent_message",
                author = "/root",
                recipient = "/root/diagonal_review",
                content = new object[] { new { type = "input_text", text = "Message Type: NEW_TASK\nPayload:\n" }, new { type = "encrypted_content", encrypted_content = CodexEncryptedTests.TOKEN } },
            },
        });
        File.WriteAllLines(Path.Combine(day, $"rollout-2026-11-03T22-25-11-{CHILD}.jsonl"), [meta, task]);
    }

    private void WriteInbox(params (string Owner, string Text)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(dir, "state", "orch.db")};Pooling=False");
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE inbox (id INTEGER PRIMARY KEY AUTOINCREMENT, owner TEXT, created REAL, text TEXT, delivered INTEGER DEFAULT 0); CREATE TABLE owners (owner TEXT PRIMARY KEY, session_id TEXT DEFAULT '')";
        create.ExecuteNonQuery();
        foreach (var (owner, text) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO inbox (owner, created, text) VALUES ($owner, 1793712959.5, $text)";
            insert.Parameters.AddWithValue("$owner", owner);
            insert.Parameters.AddWithValue("$text", text);
            insert.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(dir, true);
    }
}
