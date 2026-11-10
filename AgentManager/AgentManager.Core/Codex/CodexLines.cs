using System.Text;
using System.Text.Json;
using AgentManager.Core.Chat;
using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Codex;

public sealed class CodexThread(string path)
{
    public JsonlTail Tail { get; } = new(path);

    public SessionTracker Tracker { get; } = new() { HasItemTimings = true };

    public string? Id { get; set; }

    public string? ParentId { get; set; }

    public string? Originator { get; set; }

    public string? Nickname { get; set; }

    public bool IsReview { get; set; }

    /// <summary>The window showing this thread (never the app-server daemon), or null when it cannot be told.</summary>
    public int? Pid { get; set; }

    public bool IsAlive { get; set; }

    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>The window this thread was last placed on, kept so its exit can close the thread before the daemon lets go.</summary>
    public (int Pid, DateTimeOffset Start)? LastWindow { get; set; }

    /// <summary>The window whose screen showed this thread: the one placement that /clear, /new or /resume inside a window cannot fool.</summary>
    public (int Pid, DateTimeOffset Start)? SeenWindow { get; set; }

    /// <summary>The window that moved on to another thread, and when. The thread counts as closed until it acts again and a window shows it.</summary>
    public (int Pid, DateTimeOffset At)? LeftWindow { get; set; }

    public DateTimeOffset NextSightAt { get; set; }

    /// <summary>The Codex processes running when the Restart Manager last found nobody holding this rollout.</summary>
    public string? NotHeldUnder { get; set; }

    /// <summary>The Codex that held this rollout when the Restart Manager was last asked, and the Codex processes running then.</summary>
    public (int Holder, string Under)? HeldUnder { get; set; }

    public DateTimeOffset? NotHeldAtEvent { get; set; }

    public DateTimeOffset NextHolderCheck { get; set; }

    /// <summary>What the last liveness scan decided, so the log records a change once instead of every scan.</summary>
    public string? Placement { get; set; }

    public (int Pid, long Start)? Shell { get; set; }

    public Dictionary<string, string> ChildNames { get; } = [];
}

/// <summary>Reads one line of a Codex rollout (<c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>).</summary>
public static class CodexLines
{
    private const string REVIEW_MODEL = "codex-auto-review";

    public static void Apply(ReadOnlyMemory<byte> line, CodexThread thread, ChatLog chat, Func<string, string> nameOf)
    {
        var type = Json.TopLevelString(line.Span, "type"u8);
        if (type is not ("session_meta" or "turn_context" or "event_msg" or "response_item"))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var at = root.Time("timestamp") ?? DateTimeOffset.Now;
            var payload = root.Obj("payload");
            var tracker = thread.Tracker;
            switch (type)
            {
                case "session_meta":
                    thread.Id ??= payload.Str("id");
                    thread.ParentId ??= payload.Str("parent_thread_id");
                    thread.Originator ??= payload.Str("originator");
                    thread.Nickname ??= payload.Str("agent_nickname");
                    tracker.Cwd ??= payload.Str("cwd");
                    thread.CreatedAt ??= payload.Time("timestamp") ?? at;
                    tracker.Touch(payload.Time("timestamp") ?? at);
                    break;
                case "turn_context":
                    TurnContext(payload, thread);
                    break;
                case "event_msg":
                    Event(payload, at, thread, chat, nameOf);
                    break;
                case "response_item":
                    ResponseItem(payload, at, thread, chat, nameOf);
                    break;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void TurnContext(JsonElement payload, CodexThread thread)
    {
        var model = payload.Str("model");
        if (model is not null)
        {
            thread.Tracker.Model = model;
            thread.IsReview |= model == REVIEW_MODEL;
        }

        thread.Tracker.Cwd ??= payload.Str("cwd");
        var settings = payload.Obj("collaboration_mode").Obj("settings");
        thread.Tracker.Effort = settings.Str("reasoning_effort") ?? payload.Str("effort") ?? payload.Str("reasoning_effort") ?? thread.Tracker.Effort;
    }

    private static void Event(JsonElement payload, DateTimeOffset at, CodexThread thread, ChatLog chat, Func<string, string> nameOf)
    {
        var tracker = thread.Tracker;
        switch (payload.Str("type"))
        {
            case "task_started":
                var trigger = payload.Obj("turn_attribution").Str("turn_trigger");
                thread.IsReview |= trigger?.Contains("guardian", StringComparison.Ordinal) == true;
                tracker.ContextWindow = Math.Max(tracker.ContextWindow, payload.Num("model_context_window"));
                tracker.Prompt(at, "");
                break;
            case "task_complete":
            case "turn_aborted":
                var last = payload.Str("last_agent_message");
                if (last is not null)
                {
                    tracker.Text(at, last);
                }

                tracker.TurnEnd(at);
                break;
            case "token_count":
                var info = payload.Obj("info");
                var total = info.Obj("total_token_usage");
                if (total.ValueKind == JsonValueKind.Object)
                {
                    tracker.Totals(at, total.Num("input_tokens"), total.Num("cached_input_tokens"), total.Num("output_tokens"), info.Obj("last_token_usage").Num("input_tokens"));
                }

                tracker.ContextWindow = Math.Max(tracker.ContextWindow, info.Num("model_context_window"));
                break;
            case "user_message":
                tracker.Prompt(at, payload.Str("message") ?? "");
                break;
            case "agent_message":
                tracker.Text(at, payload.Str("message") ?? "");
                break;
            case "exec_approval_request":
            case "apply_patch_approval_request":
                tracker.AskBlocking(at, payload.Str("call_id") ?? "approval", "approve " + (payload.Str("type") == "exec_approval_request" ? "a command" : "a patch"));
                break;
            case "item_completed":
                Item(payload, at, thread, chat, nameOf);
                break;
        }
    }

    private static void Item(JsonElement payload, DateTimeOffset at, CodexThread thread, ChatLog chat, Func<string, string> nameOf)
    {
        var tracker = thread.Tracker;
        var item = payload.Obj("item");
        var startMs = payload.Num("started_at_ms");
        var endMs = payload.Num("completed_at_ms");
        var start = startMs > 0 ? Json.FromUnixMilliseconds(startMs) : at;
        var end = endMs > 0 ? Json.FromUnixMilliseconds(endMs) : at;
        switch (item.Str("type"))
        {
            case "Reasoning":
            case "ContextCompaction":
                tracker.Add(Activity.Think, start, end);
                break;
            case "AgentMessage":
                tracker.Add(Activity.Think, start, end);
                tracker.Text(end, JoinText(item.Items("content")));
                break;
            case "UserMessage":
                tracker.Prompt(start, JoinText(item.Items("content")));
                break;
            case "FileChange":
                tracker.Add(Activity.Code, start, end);
                break;
            case "CommandExecution":
                var command = item.Items("parsed_cmd").Select(p => p.Str("cmd")).FirstOrDefault(c => c is not null)
                    ?? string.Join(' ', item.Items("command").Select(c => c.GetString()));
                tracker.Add(ActivityRules.ForCommand(command), start, end);
                break;
            case "CollabAgentToolCall":
                tracker.Add(Activity.Tools, start, end);
                Collab(item, end, thread, chat, nameOf);
                break;
            case "Extension":
                break;
            default:
                tracker.Add(Activity.Tools, start, end);
                break;
        }
    }

    private static void Collab(JsonElement item, DateTimeOffset at, CodexThread thread, ChatLog chat, Func<string, string> nameOf)
    {
        foreach (var receiver in item.Items("receiver_agents"))
        {
            var childId = receiver.Str("thread_id");
            var nickname = receiver.Str("agent_nickname");
            if (childId is not null && nickname is not null)
            {
                thread.ChildNames[childId] = nickname;
            }
        }

        var prompt = item.Str("prompt") ?? item.Str("message");
        if (string.IsNullOrWhiteSpace(prompt) || thread.Id is null)
        {
            return;
        }

        var receivers = item.Items("receiver_thread_ids").Select(r => r.GetString()).Where(r => r is not null).Cast<string>().ToList();
        var to = receivers.Count == 0 ? "agents" : string.Join(", ", receivers.Select(r => thread.ChildNames.GetValueOrDefault(r) ?? nameOf(CodexSource.KEY_PREFIX + r)));
        var toKey = receivers.Count == 1 ? CodexSource.KEY_PREFIX + receivers[0] : null;
        var tool = item.Str("tool") ?? "message";
        AddSent(chat, new ChatMessage("cx:" + (item.Str("id") ?? Guid.NewGuid().ToString("N")), at, ChatChannel.Subagent, nameOf(CodexSource.KEY_PREFIX + thread.Id), to,
            CodexSource.KEY_PREFIX + thread.Id, toKey, null, tool.Replace('_', ' '), prompt));
    }

    private static void AddSent(ChatLog chat, ChatMessage message)
    {
        if (CodexEncrypted.IsEncrypted(message.Text))
        {
            chat.Add(message with { Text = CodexEncrypted.NOTE }, true, message.Text);
            return;
        }

        chat.Add(message, true);
    }

    private static void ResponseItem(JsonElement payload, DateTimeOffset at, CodexThread thread, ChatLog chat, Func<string, string> nameOf)
    {
        var tracker = thread.Tracker;
        switch (payload.Str("type"))
        {
            case "function_call":
            case "custom_tool_call":
            case "local_shell_call":
                ToolCall(payload, at, thread, chat, nameOf);
                break;
            case "function_call_output":
            case "custom_tool_call_output":
            case "local_shell_call_output":
                tracker.ToolEnd(at, payload.Str("call_id") ?? "", false);
                break;
            case "message" when payload.Str("role") == "assistant":
                tracker.Text(at, JoinText(payload.Items("content")));
                break;
        }
    }

    private static void ToolCall(JsonElement payload, DateTimeOffset at, CodexThread thread, ChatLog chat, Func<string, string> nameOf)
    {
        var tracker = thread.Tracker;
        var name = payload.Str("name") ?? payload.Str("type") ?? "tool";
        var callId = payload.Str("call_id") ?? payload.Str("id") ?? Guid.NewGuid().ToString("N");
        var raw = payload.Str("arguments") ?? payload.Str("input") ?? "";
        using var arguments = TryParse(raw);
        var args = arguments?.RootElement ?? default;
        var command = args.ValueKind == JsonValueKind.Object ? CommandOf(args) : null;
        var detail = Json.OneLine(command ?? (name == "apply_patch" ? PatchTarget(raw) : raw), 90);
        tracker.ToolStart(at, callId, name, detail, ActivityRules.ForTool(name, command), ActivityRules.UsesEditor(name, command));
        tracker.SawCommand(command ?? raw);
        if (BridgeRules.ListenChannel(command ?? raw) is { } channel)
        {
            tracker.Listen(callId, channel);
        }
        switch (name)
        {
            case "request_user_input":
                tracker.AskBlocking(at, callId, FirstQuestion(args) ?? "question");
                break;
            case "request_user_input_async":
                tracker.AskAsync(at, FirstQuestion(args) ?? "question");
                break;
            case "send_message_to_thread":
            case "send_message":
                SentMessage(args, at, thread, chat, nameOf, callId);
                break;
        }
    }

    private static void SentMessage(JsonElement args, DateTimeOffset at, CodexThread thread, ChatLog chat, Func<string, string> nameOf, string callId)
    {
        var text = args.Str("message") ?? args.Str("text") ?? args.Str("content");
        if (text is null || thread.Id is null)
        {
            return;
        }

        var target = args.Str("thread_id") ?? args.Str("to") ?? args.Str("target");
        var toKey = target is null ? null : CodexSource.KEY_PREFIX + target;
        var to = target is null ? "?" : thread.ChildNames.GetValueOrDefault(target) ?? nameOf(toKey!);
        AddSent(chat, new ChatMessage("cx:" + callId, at, ChatChannel.Peer, nameOf(CodexSource.KEY_PREFIX + thread.Id), to, CodexSource.KEY_PREFIX + thread.Id, toKey, null, null, text));
    }

    private static string? CommandOf(JsonElement args)
    {
        var command = args.Obj("command");
        return command.ValueKind switch
        {
            JsonValueKind.String => command.GetString(),
            JsonValueKind.Array => string.Join(' ', command.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString())),
            _ => args.Str("cmd"),
        };
    }

    private static string? FirstQuestion(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var question = args.Items("questions").FirstOrDefault();
        return question.Str("title") ?? question.Str("question") ?? args.Str("question") ?? args.Str("prompt");
    }

    private static string PatchTarget(string patch)
    {
        foreach (var line in patch.Split('\n'))
        {
            var trimmed = line.Trim();
            var isFileLine = trimmed.StartsWith("*** Update File:", StringComparison.Ordinal) || trimmed.StartsWith("*** Add File:", StringComparison.Ordinal);
            if (isFileLine)
            {
                return Path.GetFileName(trimmed[(trimmed.IndexOf(':') + 1)..].Trim());
            }
        }

        return "patch";
    }

    internal static string JoinText(IEnumerable<JsonElement> content)
    {
        var builder = new StringBuilder();
        foreach (var part in content)
        {
            var text = part.Str("text");
            if (text is not null)
            {
                builder.Append(text).Append('\n');
            }
        }

        return builder.ToString().Trim();
    }

    private static JsonDocument? TryParse(string text)
    {
        if (text.Length == 0 || text[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
