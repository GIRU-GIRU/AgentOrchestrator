using System.Text.Json;
using System.Text.RegularExpressions;
using AgentManager.Core.Chat;
using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Claude;

/// <summary>Who wrote a transcript, and how its message addresses map to agents.</summary>
public sealed record ClaudeChatContext(string WriterKey, string WriterName, Func<string, (string Name, string? Key)> Resolve, Action<string, string>? LearnAddress = null, bool IsSubagent = false);

/// <summary>Reads one line of a Claude Code transcript into a <see cref="SessionTracker"/> and the chat log.</summary>
public static class ClaudeLines
{
    private const string SEND_MESSAGE = "SendMessage";

    private static readonly string[] WRAPPER_PREFIXES =
    [
        "<",
        "Another Claude session sent a message",
        "[Request interrupted",
        "Caveat:",
        "This session is being continued",
    ];

    /// <summary>What wakes an agent after its turn ended. Other wrapped lines (a system reminder, a local command, <c>!</c> input) arrive with the agent still at its prompt.</summary>
    private static readonly string[] WAKE_PREFIXES =
    [
        "<task-notification",
        "<agent-message",
        "<cross-session-message",
        "Another Claude session sent a message",
        "Stop hook feedback",
        "This session is being continued",
    ];

    public static bool MayHoldChat(ReadOnlySpan<byte> line)
    {
        return line.IndexOf("SendMessage"u8) >= 0 || line.IndexOf("\"enqueue\""u8) >= 0;
    }

    public static void Apply(ReadOnlyMemory<byte> line, string lineId, SessionTracker? tracker, ClaudeChatContext context, ChatLog chat)
    {
        var type = Json.TopLevelString(line.Span, "type"u8);
        var isTracked = tracker is not null;
        if (type == "api-request")
        {
            if (line.Span.IndexOf("\"repl_main_thread\""u8) >= 0)
            {
                tracker?.TurnResumed();
            }

            return;
        }

        switch (type)
        {
            case "assistant":
            case "user" when isTracked:
            case "queue-operation":
            case "ai-title" when isTracked:
            case "custom-title" when isTracked:
            case "agent-name" when isTracked:
                break;
            default:
                return;
        }

        if (!isTracked && !MayHoldChat(line.Span))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            switch (type)
            {
                case "assistant":
                    Assistant(root, tracker, context, chat);
                    break;
                case "user":
                    User(root, tracker!, context.IsSubagent);
                    break;
                case "queue-operation":
                    Queued(root, lineId, tracker, context, chat);
                    break;
                case "ai-title":
                    tracker!.Title = root.Str("aiTitle") ?? tracker.Title;
                    break;
                case "custom-title":
                    tracker!.Name = root.Str("customTitle") ?? tracker.Name;
                    break;
                case "agent-name":
                    tracker!.Name = root.Str("agentName") ?? tracker.Name;
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void Assistant(JsonElement root, SessionTracker? tracker, ClaudeChatContext context, ChatLog chat)
    {
        if (root.Flag("isSidechain") && tracker is not null && !context.IsSubagent)
        {
            return;
        }

        var at = root.Time("timestamp") ?? DateTimeOffset.Now;
        var message = root.Obj("message");
        if (tracker is not null)
        {
            var model = message.Str("model");
            if (model is not null && !model.StartsWith('<'))
            {
                tracker.Model = model;
            }

            tracker.Effort = root.Str("effort") ?? tracker.Effort;
            tracker.Cwd ??= root.Str("cwd");
            var usage = message.Obj("usage");
            if (usage.ValueKind == JsonValueKind.Object)
            {
                tracker.Usage(at, message.Str("id"), usage.Num("input_tokens"), usage.Num("cache_read_input_tokens"), usage.Num("cache_creation_input_tokens"), usage.Num("output_tokens"));
            }
        }

        foreach (var block in message.Items("content"))
        {
            switch (block.Str("type"))
            {
                case "thinking" or "redacted_thinking":
                    tracker?.Elapse(at, Activity.Think);
                    break;
                case "text":
                    tracker?.Elapse(at, Activity.Think);
                    tracker?.Text(at, block.Str("text") ?? "");
                    break;
                case "tool_use":
                    ToolUse(block, at, tracker, context, chat);
                    break;
            }
        }

        if (message.Str("stop_reason") == "end_turn")
        {
            tracker?.TurnEnd(at);
        }
        else
        {
            tracker?.TurnResumed();
        }
    }

    private static void ToolUse(JsonElement block, DateTimeOffset at, SessionTracker? tracker, ClaudeChatContext context, ChatLog chat)
    {
        var name = block.Str("name") ?? "?";
        var id = block.Str("id") ?? Guid.NewGuid().ToString("N");
        var input = block.Obj("input");
        if (name == SEND_MESSAGE)
        {
            var to = input.Str("to") ?? input.Str("recipient") ?? "?";
            var text = input.Str("message") ?? input.Str("content") ?? "";
            var (toName, toKey) = context.Resolve(to);
            var channel = toKey?.StartsWith("claude-sub:", StringComparison.Ordinal) == true ? ChatChannel.Subagent : ChatChannel.Peer;
            chat.Add(new ChatMessage("tu:" + id, at, channel, context.WriterName, toName, context.WriterKey, toKey, null, input.Str("summary"), text), true);
        }

        if (tracker is null)
        {
            return;
        }

        var activity = ActivityRules.ForTool(name, input.Str("command"));
        tracker.ToolStart(at, id, name, Describe(name, input), activity, ActivityRules.UsesEditor(name, input.Str("command")));
        tracker.SawCommand(input.Str("command"));
        if (BridgeRules.ListenChannel(input.Str("command")) is { } listenChannel)
        {
            tracker.Listen(id, listenChannel);
        }

        if (input.Flag("run_in_background") && name is "Bash" or "PowerShell")
        {
            tracker.BackgroundStart(id, at, ActivityRules.IsServer(input.Str("command")));
        }

        if (name is "TaskStop" or "KillShell" && (input.Str("task_id") ?? input.Str("shell_id")) is { } stoppedTask)
        {
            tracker.BackgroundTaskStopped(stoppedTask);
        }

        if (name == "AskUserQuestion")
        {
            var question = input.Items("questions").Select(q => q.Str("question")).FirstOrDefault(q => q is not null) ?? "question";
            tracker.AskBlocking(at, id, question);
        }
    }

    private static void User(JsonElement root, SessionTracker tracker, bool isSubagent)
    {
        if (root.Flag("isSidechain") && !isSubagent)
        {
            return;
        }

        var at = root.Time("timestamp") ?? DateTimeOffset.Now;
        var content = root.Obj("message").Obj("content");
        if (content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString() ?? "";
            if (IsHumanPrompt(root, text))
            {
                tracker.Prompt(at, text);
            }
            else
            {
                tracker.Touch(at);
                if (WAKE_PREFIXES.Any(p => text.StartsWith(p, StringComparison.Ordinal)))
                {
                    tracker.TurnResumed();
                }
            }

            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var hasToolResult = false;
        string? promptText = null;
        foreach (var block in content.EnumerateArray())
        {
            switch (block.Str("type"))
            {
                case "tool_result":
                    hasToolResult = true;
                    tracker.ToolEnd(at, block.Str("tool_use_id") ?? "", block.Flag("is_error"));
                    if (root.Obj("toolUseResult").Str("backgroundTaskId") is { } taskId)
                    {
                        tracker.BackgroundTaskId(block.Str("tool_use_id") ?? "", taskId);
                    }

                    break;
                case "text":
                    promptText ??= block.Str("text");
                    break;
            }
        }

        if (!hasToolResult && promptText is not null && IsHumanPrompt(root, promptText))
        {
            tracker.Prompt(at, promptText);
        }
    }

    private static string? EndedBackgroundCall(string content)
    {
        if (!content.StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            return null;
        }

        var match = Regex.Match(content, @"<tool-use-id>(?<id>[^<]+)</tool-use-id>.*?<status>(?<status>[^<]+)</status>", RegexOptions.Singleline);
        return match.Success && match.Groups["status"].Value != "running" ? match.Groups["id"].Value : null;
    }

    private static void Queued(JsonElement root, string lineId, SessionTracker? tracker, ClaudeChatContext context, ChatLog chat)
    {
        if (root.Str("operation") != "enqueue")
        {
            return;
        }

        var at = root.Time("timestamp") ?? DateTimeOffset.Now;
        var content = root.Str("content") ?? "";
        if (tracker is not null && EndedBackgroundCall(content) is { } endedCall)
        {
            tracker.BackgroundEnd(endedCall);
        }

        var index = 0;
        foreach (var envelope in EnvelopeParser.Parse(content))
        {
            var (fromName, fromKey) = context.Resolve(envelope.From);
            if (envelope.FromName is not null)
            {
                context.LearnAddress?.Invoke(envelope.From, envelope.FromName);
                fromName = fromKey is null ? envelope.FromName : fromName;
            }

            var channel = envelope.Tag == "agent-message" ? ChatChannel.Subagent : ChatChannel.Peer;
            chat.Add(new ChatMessage($"q:{lineId}:{index++}", at, channel, fromName, context.WriterName, fromKey, context.WriterKey, null, null, envelope.Body), false);
        }

        var isTypedPrompt = index == 0 && tracker is not null && content.Length > 0 && !WRAPPER_PREFIXES.Any(p => content.StartsWith(p, StringComparison.Ordinal));
        if (isTypedPrompt)
        {
            tracker!.Prompt(at, content);
        }
    }

    internal static bool IsWrapped(string text)
    {
        return WRAPPER_PREFIXES.Any(p => text.StartsWith(p, StringComparison.Ordinal));
    }

    internal static bool IsHumanPrompt(JsonElement root, string text)
    {
        if (root.Flag("isMeta") || root.Obj("toolUseResult").ValueKind != JsonValueKind.Undefined)
        {
            return false;
        }

        var origin = root.Obj("origin");
        var isFromElsewhere = origin.ValueKind == JsonValueKind.Object && origin.Str("kind") is { } kind && kind != "user" && kind != "human";
        var trimmed = text.TrimStart();
        return !isFromElsewhere && trimmed.Length > 0 && !WRAPPER_PREFIXES.Any(p => trimmed.StartsWith(p, StringComparison.Ordinal));
    }

    internal static string Describe(string name, JsonElement input)
    {
        string? detail = name switch
        {
            "Bash" or "PowerShell" => input.Str("description") ?? input.Str("command"),
            "Read" or "Edit" or "Write" or "MultiEdit" or "NotebookEdit" => FileName(input.Str("file_path") ?? input.Str("notebook_path")),
            "Grep" or "Glob" => input.Str("pattern"),
            "Agent" or "Task" => input.Str("description"),
            "WebFetch" => input.Str("url"),
            "WebSearch" => input.Str("query"),
            "Skill" => input.Str("skill"),
            "SendMessage" => "→ " + (input.Str("to") ?? "?"),
            "AskUserQuestion" => "asking you",
            _ => null,
        };
        return Json.OneLine(detail, 90);
    }

    private static string? FileName(string? path)
    {
        return path is null ? null : Path.GetFileName(path.TrimEnd('/', '\\'));
    }
}
