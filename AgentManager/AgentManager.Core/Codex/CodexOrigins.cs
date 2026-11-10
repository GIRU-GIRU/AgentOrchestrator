using System.Text;
using System.Text.Json;
using AgentManager.Core.Chat;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Codex;

/// <summary>
/// Codex keeps the messages between its own agents encrypted, as a Fernet-style token: base64url of a version byte 0x80, a timestamp and
/// the payload. Only Codex holds the key, so the manager can only say what it is.
/// </summary>
public static class CodexEncrypted
{
    public const string NOTE = "encrypted message (only Codex can read it)";

    /// <summary>The version byte 0x80 and the zero high bytes of the timestamp, in base64.</summary>
    private const string TOKEN_START = "gAAAA";

    /// <summary>The base64 length of the shortest token: version, timestamp, one IV, one cipher block and the HMAC.</summary>
    private const int MIN_CHARS = 100;

    public static bool IsEncrypted(string? text)
    {
        var token = text.AsSpan().Trim();
        if (token.Length < MIN_CHARS || !token.StartsWith(TOKEN_START, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '='))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>A rollout's own <c>session_meta</c>. <c>SessionId</c> is the root thread of the agent tree, the thread's own id on a root.</summary>
public sealed record CodexThreadMeta(string? Id, string? SessionId, string? ParentId, string? AgentPath, string? Nickname);

/// <summary>Finds Codex threads by id or by the address one agent used for another.</summary>
public static class CodexOrigins
{
    private const string ROOT_ADDRESS = "/root";

    private const string WINDOW = "Codex thread";

    private const string SUBAGENT = "Codex subagent";

    private const int FIRST_LINE_BUFFER = 1 << 16;

    public static bool IsThreadId(string text)
    {
        return Guid.TryParse(text, out _);
    }

    public static string? FindRollout(string sessionsDir, string threadId)
    {
        if (!IsThreadId(threadId) || !Directory.Exists(sessionsDir))
        {
            return null;
        }

        return Directory.EnumerateFiles(sessionsDir, $"rollout-*{threadId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>
    /// The thread an agent means by <c>/root</c> or a task name. A target that is not a thread id can only be read against the sender, and
    /// the task names come from the agents, so two trees can use the same one.
    /// </summary>
    public static string? ThreadOfAddress(string sessionsDir, string address, string? senderId)
    {
        var senderPath = senderId is null ? null : FindRollout(sessionsDir, senderId);
        var sender = senderPath is null ? null : ReadMeta(senderPath);
        if (sender is null)
        {
            return null;
        }

        var tree = sender.SessionId ?? sender.ParentId ?? sender.Id;
        if (address == ROOT_ADDRESS)
        {
            return tree;
        }

        var earliest = Path.GetFileName(senderPath!);
        return Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
            .Where(file => string.CompareOrdinal(Path.GetFileName(file), earliest) >= 0)
            .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
            .Select(ReadMeta)
            .FirstOrDefault(meta => meta is not null && IsAddressedBy(meta, address, tree))?.Id;
    }

    internal static bool IsAddressedBy(CodexThreadMeta meta, string address, string? tree)
    {
        var isInTree = meta.SessionId == tree;
        var isNamed = meta.AgentPath == address || meta.AgentPath?.EndsWith("/" + address, StringComparison.Ordinal) == true;
        return isInTree && isNamed;
    }

    public static string Describe(CodexThreadMeta? meta)
    {
        if (meta?.ParentId is null)
        {
            return WINDOW;
        }

        var names = new[] { meta.Nickname, meta.AgentPath }.Where(name => !string.IsNullOrEmpty(name)).ToArray();
        return names.Length == 0 ? SUBAGENT : SUBAGENT + " " + string.Join(", ", names);
    }

    /// <summary>A rollout's first line is its own meta. A fork's second line is its parent's, so only the first says who this is.</summary>
    internal static CodexThreadMeta? ReadMeta(string path)
    {
        try
        {
            using var reader = new StreamReader(JsonlTail.OpenShared(path), Encoding.UTF8, true, FIRST_LINE_BUFFER);
            return reader.ReadLine() is { } line ? ParseMeta(line) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CodexThreadMeta? ParseMeta(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Str("type") == "session_meta" ? MetaOf(document.RootElement.Obj("payload")) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static CodexThreadMeta MetaOf(JsonElement payload)
    {
        return new CodexThreadMeta(payload.Str("id"), payload.Str("session_id"), payload.Str("parent_thread_id"), payload.Str("agent_path"), payload.Str("agent_nickname"));
    }
}

/// <summary>
/// What a Codex thread was asked. A user's message comes as a completed <c>UserMessage</c> item. A subagent's task comes as an
/// <c>agent_message</c> addressed to its path, usually encrypted. The <c>message</c> items with the user role are never read: they hold the
/// harness's AGENTS.md and context, and a fork's copy of its parent's history, whose prompts are not its own.
/// </summary>
public sealed class CodexOriginReader : IOriginLineReader
{
    private const string PROMPT_LABEL = "prompt";

    private const string TASK_LABEL = "task";

    private const string NEW_TASK = "Message Type: NEW_TASK";

    private const string ANY_MESSAGE = "Message Type:";

    private const string PAYLOAD = "Payload:\n";

    private static readonly string[] HARNESS_PREFIXES = ["<environment_context", "<turn_aborted", "<user_instructions", "# AGENTS.md instructions"];

    public CodexThreadMeta? Meta { get; private set; }

    public OriginPrompt? Read(ReadOnlyMemory<byte> line)
    {
        var type = Json.TopLevelString(line.Span, "type"u8);
        var isWanted = type switch
        {
            "session_meta" => Meta is null,
            "event_msg" => line.Span.IndexOf("\"UserMessage\""u8) >= 0 || line.Span.IndexOf("\"user_message\""u8) >= 0,
            "response_item" => line.Span.IndexOf("\"agent_message\""u8) >= 0,
            _ => false,
        };
        if (!isWanted)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var payload = root.Obj("payload");
            var at = root.Time("timestamp");
            switch (type)
            {
                case "session_meta":
                    Meta = CodexOrigins.MetaOf(payload);
                    return null;
                case "event_msg":
                    return FromEvent(payload, at);
                default:
                    return FromAgentMessage(payload, at);
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static OriginPrompt? FromEvent(JsonElement payload, DateTimeOffset? at)
    {
        var text = payload.Str("type") switch
        {
            "user_message" => payload.Str("message"),
            "item_completed" when payload.Obj("item").Str("type") == "UserMessage" => CodexLines.JoinText(payload.Obj("item").Items("content")),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text) || HARNESS_PREFIXES.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return null;
        }

        return CodexEncrypted.IsEncrypted(text) ? new OriginPrompt(at, PROMPT_LABEL, CodexEncrypted.NOTE, 0, true) : new OriginPrompt(at, PROMPT_LABEL, text);
    }

    private OriginPrompt? FromAgentMessage(JsonElement payload, DateTimeOffset? at)
    {
        var isToThisThread = payload.Str("type") == "agent_message" && Meta?.AgentPath is { } path && payload.Str("recipient") == path;
        if (!isToThisThread)
        {
            return null;
        }

        var plain = CodexLines.JoinText(payload.Items("content"));
        var isTask = plain.Contains(NEW_TASK, StringComparison.Ordinal) || !plain.Contains(ANY_MESSAGE, StringComparison.Ordinal);
        if (!isTask)
        {
            return null;
        }

        var label = payload.Str("author") is { Length: > 0 } author ? TASK_LABEL + " from " + author : TASK_LABEL;
        var marker = plain.IndexOf(PAYLOAD, StringComparison.Ordinal);
        var body = marker >= 0 ? plain[(marker + PAYLOAD.Length)..].Trim() : plain;
        var isEncrypted = payload.Items("content").Any(part => part.Str("type") == "encrypted_content") || CodexEncrypted.IsEncrypted(body);
        if (isEncrypted)
        {
            return new OriginPrompt(at, label, CodexEncrypted.NOTE, 0, true);
        }

        return body.Length == 0 ? null : new OriginPrompt(at, label, body);
    }
}
