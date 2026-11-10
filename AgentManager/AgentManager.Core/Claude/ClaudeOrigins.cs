using System.Text.Json;
using AgentManager.Core.Chat;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Claude;

/// <summary>Where a Claude session's or subagent's transcript is, found from its id alone.</summary>
public static class ClaudeOrigins
{
    public static string? FindSession(string projectsDir, string sessionId)
    {
        if (!IsFileNameSafe(sessionId) || !Directory.Exists(projectsDir))
        {
            return null;
        }

        return Directory.EnumerateDirectories(projectsDir)
            .Select(project => Path.Combine(project, sessionId + ".jsonl"))
            .FirstOrDefault(File.Exists);
    }

    public static string? FindSubagent(string projectsDir, string agentId)
    {
        if (!IsFileNameSafe(agentId) || !Directory.Exists(projectsDir))
        {
            return null;
        }

        return Directory.EnumerateDirectories(projectsDir)
            .SelectMany(project => Directory.EnumerateDirectories(project))
            .Select(session => Path.Combine(session, "subagents", "agent-" + agentId + ".jsonl"))
            .FirstOrDefault(File.Exists);
    }

    private static bool IsFileNameSafe(string id)
    {
        return id.Length > 0 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}

/// <summary>
/// The lines of a Claude transcript that are someone asking the agent something. The first thing in a subagent's file is its parent's
/// prompt whatever it starts with. A fork inherits its parent's history and gets its job in a line that also carries a tool result, after
/// boilerplate every fork shares.
/// </summary>
public sealed class ClaudeOriginReader(bool isSubagent) : IOriginLineReader
{
    private const string PROMPT_LABEL = "prompt";

    private const string PARENT_LABEL = "asked by its parent";

    private const string FORK_END = "</fork-boilerplate>";

    private const string FORK_DIRECTIVE = "Your directive:";

    /// <summary>The harness's own notes that arrive like something typed: <see cref="ClaudeLines"/> has the wrappers the state rules care about.</summary>
    private static readonly string[] NOTICE_PREFIXES = ["[Cross-session"];

    private bool hasPrompt;

    public OriginPrompt? Read(ReadOnlyMemory<byte> line)
    {
        var type = Json.TopLevelString(line.Span, "type"u8);
        var isUser = type == "user";
        if (!isUser && type != "queue-operation")
        {
            return null;
        }

        var isForkOpening = isSubagent && line.Span.IndexOf("<fork-boilerplate>"u8) >= 0;
        var isOnlyToolOutput = isUser && !isForkOpening && line.Span.IndexOf("\"tool_result\""u8) >= 0;
        if (isOnlyToolOutput)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var text = isUser ? FromUserLine(root, isForkOpening) : TypedWhileBusy(root);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            hasPrompt = true;
            return new OriginPrompt(root.Time("timestamp"), isSubagent ? PARENT_LABEL : PROMPT_LABEL, text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string? FromUserLine(JsonElement root, bool isForkOpening)
    {
        if (root.Flag("isMeta") || (root.Flag("isSidechain") && !isSubagent))
        {
            return null;
        }

        var text = FirstText(root.Obj("message").Obj("content"));
        if (text is null)
        {
            return null;
        }

        if (isForkOpening)
        {
            return ForkDirective(text);
        }

        var isParentsPrompt = isSubagent && !hasPrompt;
        return isParentsPrompt || (ClaudeLines.IsHumanPrompt(root, text) && !IsNotice(text)) ? text : null;
    }

    /// <summary>A prompt typed during a turn is recorded here first, and may never become a user line.</summary>
    private static string? TypedWhileBusy(JsonElement root)
    {
        if (root.Str("operation") != "enqueue" || root.Str("content") is not { Length: > 0 } content)
        {
            return null;
        }

        var isTyped = !ClaudeLines.IsWrapped(content) && !IsNotice(content) && !EnvelopeParser.Parse(content).Any();
        return isTyped ? content : null;
    }

    private static string? FirstText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return content.EnumerateArray().Where(block => block.Str("type") == "text").Select(block => block.Str("text")).FirstOrDefault(text => text is not null);
    }

    internal static string ForkDirective(string text)
    {
        var end = text.IndexOf(FORK_END, StringComparison.Ordinal);
        var rest = (end >= 0 ? text[(end + FORK_END.Length)..] : text).TrimStart();
        return rest.StartsWith(FORK_DIRECTIVE, StringComparison.Ordinal) ? rest[FORK_DIRECTIVE.Length..].TrimStart() : rest;
    }

    private static bool IsNotice(string text)
    {
        var trimmed = text.TrimStart();
        return NOTICE_PREFIXES.Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal));
    }
}
