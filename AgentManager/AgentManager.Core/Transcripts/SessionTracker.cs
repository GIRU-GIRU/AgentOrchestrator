using AgentManager.Core.Model;

namespace AgentManager.Core.Transcripts;

/// <summary>
/// What one agent transcript has said so far, accumulated line by line. Provider parsers feed it; it knows nothing about
/// any provider's format.
/// </summary>
public sealed class SessionTracker
{
    public const int SPARK_MINUTES = 40;

    private const int RECENT_TOOLS = 60;

    private const int RECENT_PROMPTS = 5;

    private static readonly TimeSpan MAX_GAP = TimeSpan.FromMinutes(10);

    private readonly double[] seconds = new double[6];

    private readonly Dictionary<long, long> tokensByMinute = [];

    private readonly Dictionary<string, ToolCall> openTools = [];

    private readonly List<ToolCall> recentTools = [];

    private readonly HashSet<string> countedUsage = [];

    private readonly List<PromptLine> prompts = [];

    private DateTimeOffset? cursor;

    public DateTimeOffset? FirstEventAt { get; private set; }

    public DateTimeOffset? LastEventAt { get; private set; }

    public DateTimeOffset? LastPromptAt { get; private set; }

    public DateTimeOffset? TurnEndedAt { get; private set; }

    public DateTimeOffset? LastTextAt { get; private set; }

    public DateTimeOffset? QuestionAt { get; private set; }

    public bool TurnOpen { get; private set; }

    public string? Model { get; set; }

    public string? Effort { get; set; }

    public string? Title { get; set; }

    public string? Name { get; set; }

    public string? Cwd { get; set; }

    public string? FirstPrompt { get; private set; }

    public string? LastPromptText { get; private set; }

    public string? LastText { get; private set; }

    public string? QuestionText { get; private set; }

    public bool PendingQuestion => PendingQuestionId is not null;

    /// <summary>The bridge channel a running command is waiting on (<see cref="BridgeRules"/>), while it runs.</summary>
    public string? ListeningOn { get; private set; }

    public bool IsWaitingOnBridge => listenCallId is not null && CurrentTool?.Id == listenCallId;

    private string? listenCallId;

    private readonly HashSet<string> bridgeChannels = [];

    public IReadOnlyCollection<string> BridgeChannels => bridgeChannels.ToArray();

    public void SawCommand(string? command)
    {
        foreach (var channel in BridgeRules.MentionedChannels(command))
        {
            bridgeChannels.Add(channel);
        }
    }

    public void Listen(string callId, string channel)
    {
        ListeningOn = channel;
        listenCallId = callId;
    }

    private readonly Dictionary<string, DateTimeOffset> backgroundCalls = [];

    private readonly Dictionary<string, string> backgroundTaskCalls = [];

    private readonly Dictionary<string, DateTimeOffset> backgroundServers = [];

    public void BackgroundStart(string callId, DateTimeOffset at, bool isServer = false)
    {
        (isServer ? backgroundServers : backgroundCalls)[callId] = at;
    }

    public void BackgroundTaskId(string callId, string taskId)
    {
        backgroundTaskCalls[taskId] = callId;
    }

    public void BackgroundEnd(string callId)
    {
        backgroundCalls.Remove(callId);
        backgroundServers.Remove(callId);
        if (listenCallId == callId)
        {
            ListeningOn = null;
            listenCallId = null;
        }
    }

    public void BackgroundTaskStopped(string taskId)
    {
        if (backgroundTaskCalls.TryGetValue(taskId, out var callId))
        {
            BackgroundEnd(callId);
        }
    }

    /// <summary>Background commands still running; one started before this process belongs to a process that has gone.</summary>
    public int RunningCommandsSince(DateTimeOffset processStart)
    {
        return backgroundCalls.Values.Count(started => started >= processStart);
    }

    public int RunningServersSince(DateTimeOffset processStart)
    {
        return backgroundServers.Values.Count(started => started >= processStart);
    }

    public string? ListeningSince(DateTimeOffset processStart)
    {
        var isFromAGoneProcess = listenCallId is not null && backgroundCalls.TryGetValue(listenCallId, out var started) && started < processStart;
        return isFromAGoneProcess ? null : ListeningOn;
    }

    public string? PendingQuestionId { get; private set; }

    public long Input { get; private set; }

    public long Output { get; private set; }

    public long CacheRead { get; private set; }

    public long CacheWrite { get; private set; }

    public long Context { get; private set; }

    public long ContextWindow { get; set; }

    public void Touch(DateTimeOffset at)
    {
        FirstEventAt ??= at;
        if (LastEventAt is null || at > LastEventAt)
        {
            LastEventAt = at;
        }
    }

    public void Prompt(DateTimeOffset at, string text)
    {
        Touch(at);
        LastPromptAt = at;
        TurnOpen = true;
        TurnEndedAt = null;
        QuestionText = null;
        QuestionAt = null;
        cursor = at;
        if (!string.IsNullOrWhiteSpace(text))
        {
            FirstPrompt ??= Json.OneLine(text, 120);
            LastPromptText = Json.OneLine(text, 160);
            prompts.Add(new PromptLine(at, Json.OneLine(text, 400)));
            if (prompts.Count > RECENT_PROMPTS)
            {
                prompts.RemoveAt(0);
            }
        }
    }

    /// <summary>Set when the transcript times each item itself (Codex), so gaps between lines are not counted again.</summary>
    public bool HasItemTimings { get; set; }

    public void Elapse(DateTimeOffset at, Activity activity)
    {
        Touch(at);
        if (!HasItemTimings && cursor is { } from && at > from && at - from < MAX_GAP)
        {
            seconds[(int)activity] += (at - from).TotalSeconds;
        }

        if (cursor is null || at > cursor)
        {
            cursor = at;
        }
    }

    public void Add(Activity activity, DateTimeOffset start, DateTimeOffset end)
    {
        Touch(end);
        var span = end - start;
        if (span > TimeSpan.Zero && span < TimeSpan.FromHours(2))
        {
            seconds[(int)activity] += span.TotalSeconds;
        }
    }

    public void Text(DateTimeOffset at, string text)
    {
        Touch(at);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        LastText = text.Trim();
        LastTextAt = at;
        var question = Questions.Find(text);
        if (question is not null)
        {
            QuestionText = question;
            QuestionAt = at;
        }
    }

    public void AskBlocking(DateTimeOffset at, string id, string question)
    {
        PendingQuestionId = id;
        QuestionText = question;
        QuestionAt = at;
    }

    public void AskAsync(DateTimeOffset at, string question)
    {
        QuestionText = question;
        QuestionAt = at;
    }

    public void ToolStart(DateTimeOffset at, string id, string name, string detail, Activity activity, bool usesEditor = false)
    {
        Elapse(at, activity == Activity.Code ? Activity.Code : Activity.Think);
        var call = new ToolCall(id, name, detail, activity, at, null, false, usesEditor);
        openTools[id] = call;
        recentTools.Add(call);
        if (recentTools.Count > RECENT_TOOLS)
        {
            recentTools.RemoveAt(0);
        }
    }

    public void ToolEnd(DateTimeOffset at, string id, bool isError)
    {
        if (isError)
        {
            backgroundCalls.Remove(id);
            backgroundServers.Remove(id);
        }

        if (listenCallId == id && !backgroundCalls.ContainsKey(id))
        {
            ListeningOn = null;
            listenCallId = null;
        }

        if (PendingQuestionId == id)
        {
            PendingQuestionId = null;
            QuestionText = null;
            QuestionAt = null;
        }

        if (!openTools.Remove(id, out var call))
        {
            Elapse(at, Activity.Tools);
            return;
        }

        Elapse(at, call.Activity);
        var closed = call with { End = at, IsError = isError };
        var index = recentTools.FindLastIndex(t => t.Id == id);
        if (index >= 0)
        {
            recentTools[index] = closed;
        }
    }

    public void Usage(DateTimeOffset at, string? messageId, long input, long cacheRead, long cacheWrite, long output)
    {
        if (messageId is not null && !countedUsage.Add(messageId))
        {
            return;
        }

        Touch(at);
        Input += input;
        CacheRead += cacheRead;
        CacheWrite += cacheWrite;
        Output += output;
        Context = input + cacheRead + cacheWrite;
        Bucket(at, input + cacheRead + cacheWrite + output);
    }

    public void Totals(DateTimeOffset at, long input, long cached, long output, long context)
    {
        var added = input + output - (Input + CacheRead + Output);
        Input = Math.Max(0, input - cached);
        CacheRead = cached;
        Output = output;
        if (context > 0)
        {
            Context = context;
        }

        if (added > 0)
        {
            Bucket(at, added);
        }
    }

    /// <summary>The agent runs again after its turn ended with no prompt to say so: a notification woke it, or it is calling the model.</summary>
    public void TurnResumed()
    {
        TurnOpen = true;
    }

    /// <summary>Nothing has run since the transcript's last end of turn, so the agent is at its prompt whatever a process-wide status says.</summary>
    public bool HasTurnEndedForNow => !TurnOpen && TurnEndedAt is not null;

    public void TurnEnd(DateTimeOffset at)
    {
        ClearOpenTools(at);
        if (listenCallId is null || !backgroundCalls.ContainsKey(listenCallId))
        {
            ListeningOn = null;
            listenCallId = null;
        }

        Touch(at);
        TurnOpen = false;
        TurnEndedAt = at;
        cursor = null;
        PendingQuestionId = null;
    }

    public void ClearOpenTools(DateTimeOffset at)
    {
        foreach (var id in openTools.Keys.ToList())
        {
            ToolEnd(at, id, false);
        }
    }

    public TimeSplit Split => TimeSplit.From(seconds);

    public TokenTally Tokens => new(Input, Output, CacheRead, CacheWrite, Context, ContextWindow);

    public long TotalTokens => Input + Output + CacheRead + CacheWrite;

    public IReadOnlyList<ToolCall> RecentTools => recentTools.ToArray();

    public IReadOnlyList<PromptLine> RecentPrompts => prompts.ToArray();

    public ToolCall? CurrentTool => openTools.Count == 0 ? null : openTools.Values.MaxBy(t => t.Start);

    public IReadOnlyList<long> Spark(DateTimeOffset now)
    {
        var minute = MinuteOf(now);
        var values = new long[SPARK_MINUTES];
        for (var i = 0; i < SPARK_MINUTES; i++)
        {
            tokensByMinute.TryGetValue(minute - (SPARK_MINUTES - 1 - i), out values[i]);
        }

        return values;
    }

    /// <summary>Tokens used across the whole session, from its first event to <paramref name="end"/>, in up to <paramref name="points"/> equal slices.</summary>
    public IReadOnlyList<double> UsageProfile(DateTimeOffset end, int points)
    {
        if (FirstEventAt is not { } first || tokensByMinute.Count == 0)
        {
            return [];
        }

        var startMinute = MinuteOf(first);
        var minutes = Math.Max(1, MinuteOf(end) - startMinute + 1);
        var count = (int)Math.Min(points, Math.Max(2, minutes));
        var values = new double[count];
        foreach (var (minute, tokens) in tokensByMinute)
        {
            var offset = minute - startMinute;
            if (offset < 0 || offset >= minutes)
            {
                continue;
            }

            values[(int)(offset * count / minutes)] += tokens;
        }

        return values;
    }

    private void Bucket(DateTimeOffset at, long tokens)
    {
        var minute = MinuteOf(at);
        tokensByMinute[minute] = tokensByMinute.GetValueOrDefault(minute) + tokens;
    }

    private static long MinuteOf(DateTimeOffset at)
    {
        return at.ToUnixTimeSeconds() / 60;
    }
}
