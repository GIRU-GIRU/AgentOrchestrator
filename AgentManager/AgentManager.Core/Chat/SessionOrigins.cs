using System.Text.RegularExpressions;
using AgentManager.Core.Claude;
using AgentManager.Core.Codex;
using AgentManager.Core.Diagnostics;
using AgentManager.Core.Orchestration;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Chat;

/// <summary>One thing an agent was asked. A note is the manager's own words about it, not the agent's prompt.</summary>
public sealed record OriginPrompt(DateTimeOffset? At, string Label, string Text, int CutChars = 0, bool IsNote = false);

/// <summary>What started a session. Empty means there is nothing to say, so no popup; a note is a plain line saying why nothing shows.</summary>
public sealed record SessionOrigin(IReadOnlyList<OriginPrompt> Prompts, string? What = null, string? Note = null)
{
    public static readonly SessionOrigin Nothing = new([]);

    public bool IsEmpty => Prompts.Count == 0 && Note is null;
}

/// <summary>Picks a provider's prompts out of its transcript, one line at a time. A line that is not a prompt, or fails to parse, gives null.</summary>
public interface IOriginLineReader
{
    OriginPrompt? Read(ReadOnlyMemory<byte> line);
}

/// <summary>
/// The opening prompts of the session behind a chat name. Read only when a name is hovered, never on a tick: a transcript can be
/// hundreds of megabytes and its agent is appending to it. Each is read once from the start up to the last prompt wanted and after
/// that only what was appended, so a second hover costs nothing. The hub's sources are not touched, since they belong to its thread;
/// the files are found again by name.
/// </summary>
public sealed partial class SessionOrigins(string claudeDir, string codexDir)
{
    public const int MAX_PROMPTS = 3;

    public const int MAX_BRIEFS = 2;

    private const int TEXT_CHARS = 900;

    /// <summary>How far back from the limit a cut looks for a space, so a long word or path is not what decides the length.</summary>
    private const int CUT_SEARCH_CHARS = 80;

    private const string NO_TRANSCRIPT = "its transcript is not on this machine any more";

    private const string NO_PROMPT = "no prompt on record";

    private const string NO_BRIEF = "no message to it in the orchestrator inbox";

    private const string NO_SESSION = "an orchestrator owner with no session on record";

    private const string SHARED_OWNER = "every Codex window shares this orchestrator owner, so it names no session";

    private const string UNREADABLE = "could not read its transcript";

    private const string OWNER_USER = "user";

    private const string OWNER_CODEX = "codex";

    private readonly string projectsDir = Path.Combine(claudeDir, "projects");

    private readonly string sessionsDir = Path.Combine(codexDir, "sessions");

    private readonly object gate = new();

    private readonly Dictionary<string, Scan> scans = [];

    public static SessionOrigins ForThisUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new SessionOrigins(
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"),
            Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(home, ".codex"));
    }

    /// <summary>
    /// <paramref name="otherKey"/> is the other end of the chat row: a Codex agent addresses another by a path relative to itself
    /// (<c>/root</c>, <c>diagonal_review</c>), so only the sender says which thread that is. Slow the first time, so call it off the UI thread.
    /// </summary>
    public SessionOrigin Find(string key, string? otherKey, IReadOnlyList<string> orchestratorDirs)
    {
        lock (gate)
        {
            try
            {
                if (key.StartsWith(ClaudeSource.SUBAGENT_PREFIX, StringComparison.Ordinal))
                {
                    return OfClaudeSubagent(key, orchestratorDirs);
                }

                if (key.StartsWith(ClaudeSource.KEY_PREFIX, StringComparison.Ordinal))
                {
                    return OfClaudeSession(key[ClaudeSource.KEY_PREFIX.Length..], orchestratorDirs);
                }

                if (key.StartsWith(CodexSource.KEY_PREFIX, StringComparison.Ordinal))
                {
                    return OfCodex(key[CodexSource.KEY_PREFIX.Length..], otherKey, orchestratorDirs);
                }

                return key.StartsWith(OrchestratorReader.OWNER_PREFIX, StringComparison.Ordinal) ? OfOwner(key[OrchestratorReader.OWNER_PREFIX.Length..], orchestratorDirs) : SessionOrigin.Nothing;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AppLog.Warn("chat", $"opening prompts of {key}: {exception.Message}");
                return new SessionOrigin([], null, UNREADABLE);
            }
        }
    }

    private SessionOrigin OfClaudeSession(string sessionId, IReadOnlyList<string> orchestratorDirs)
    {
        const string WHAT = "Claude session";
        var key = ClaudeSource.KEY_PREFIX + sessionId;
        if (!scans.TryGetValue(key, out var scan))
        {
            if (ClaudeOrigins.FindSession(projectsDir, sessionId) is not { } path)
            {
                return new SessionOrigin([], WHAT, NO_TRANSCRIPT);
            }

            scan = NewScan(key, path, new ClaudeOriginReader(false));
        }

        return Compose(scan, WHAT, orchestratorDirs);
    }

    private SessionOrigin OfClaudeSubagent(string key, IReadOnlyList<string> orchestratorDirs)
    {
        const string WHAT = "Claude subagent";
        if (!scans.TryGetValue(key, out var scan))
        {
            if (ClaudeOrigins.FindSubagent(projectsDir, key[ClaudeSource.SUBAGENT_PREFIX.Length..]) is not { } path)
            {
                return new SessionOrigin([], WHAT, NO_TRANSCRIPT);
            }

            scan = NewScan(key, path, new ClaudeOriginReader(true));
        }

        return Compose(scan, WHAT, orchestratorDirs);
    }

    private SessionOrigin OfCodex(string target, string? otherKey, IReadOnlyList<string> orchestratorDirs)
    {
        var sender = otherKey?.StartsWith(CodexSource.KEY_PREFIX, StringComparison.Ordinal) == true ? otherKey[CodexSource.KEY_PREFIX.Length..] : null;
        var threadId = CodexOrigins.IsThreadId(target) ? target : CodexOrigins.ThreadOfAddress(sessionsDir, target, sender);
        if (threadId is null)
        {
            return new SessionOrigin([], "Codex agent", $"no Codex thread answers to {target}");
        }

        var key = CodexSource.KEY_PREFIX + threadId;
        if (!scans.TryGetValue(key, out var scan))
        {
            if (CodexOrigins.FindRollout(sessionsDir, threadId) is not { } path)
            {
                return new SessionOrigin([], "Codex agent", NO_TRANSCRIPT);
            }

            scan = NewScan(key, path, new CodexOriginReader());
        }

        var prompts = scan.Advance();
        return Compose(prompts, CodexOrigins.Describe(((CodexOriginReader)scan.Reader).Meta), orchestratorDirs);
    }

    private SessionOrigin OfOwner(string owner, IReadOnlyList<string> orchestratorDirs)
    {
        if (owner == OWNER_USER)
        {
            return SessionOrigin.Nothing;
        }

        if (OrchestratorBriefs.PointerOfOwner(owner) is { } pointer)
        {
            var briefs = BriefsFor(pointer, orchestratorDirs);
            return briefs.Count == 0 ? new SessionOrigin([], "orchestrator owner", NO_BRIEF) : new SessionOrigin(briefs, "from the orchestrator inbox");
        }

        foreach (var dir in orchestratorDirs)
        {
            if (OrchestratorBriefs.SessionOfOwner(dir, owner) is { Length: > 0 } sessionId)
            {
                return OfClaudeSession(sessionId, orchestratorDirs);
            }
        }

        return new SessionOrigin([], "orchestrator owner", owner.StartsWith(OWNER_CODEX, StringComparison.Ordinal) ? SHARED_OWNER : NO_SESSION);
    }

    private Scan NewScan(string key, string path, IOriginLineReader reader)
    {
        AppLog.Info("chat", $"opening prompts of {key}: reading {path}");
        var scan = new Scan(path, reader);
        scans[key] = scan;
        return scan;
    }

    private SessionOrigin Compose(Scan scan, string what, IReadOnlyList<string> orchestratorDirs)
    {
        return Compose(scan.Advance(), what, orchestratorDirs);
    }

    /// <summary>
    /// A prompt that only points at an inbox says nothing of the job, so the brief it pointed at goes straight after it. The watcher wakes
    /// an Astra with that line again each time, sometimes with half-typed text in front, so one is enough.
    /// </summary>
    private SessionOrigin Compose(IReadOnlyList<OriginPrompt> prompts, string what, IReadOnlyList<string> orchestratorDirs)
    {
        if (prompts.Count == 0)
        {
            return new SessionOrigin([], what, NO_PROMPT);
        }

        var shown = new List<OriginPrompt>();
        var briefedOn = new HashSet<string?>();
        foreach (var prompt in prompts)
        {
            var pointer = prompt.IsNote ? null : OrchestratorBriefs.PointerIn(prompt.Text);
            if (pointer is not null && !briefedOn.Add(pointer.Work))
            {
                continue;
            }

            shown.Add(Trimmed(prompt));
            if (pointer is not null)
            {
                shown.AddRange(BriefsFor(pointer, orchestratorDirs));
            }
        }

        return new SessionOrigin(shown, what);
    }

    private static List<OriginPrompt> BriefsFor(InboxPointer pointer, IReadOnlyList<string> orchestratorDirs)
    {
        return orchestratorDirs
            .SelectMany(dir => OrchestratorBriefs.Read(dir, pointer, MAX_BRIEFS))
            .Take(MAX_BRIEFS)
            .Select(brief => Trimmed(new OriginPrompt(brief.At, brief.Label, brief.Text)))
            .ToList();
    }

    private static OriginPrompt Trimmed(OriginPrompt prompt)
    {
        var (text, cut) = Trim(prompt.Text, TEXT_CHARS);
        return prompt with { Text = text, CutChars = cut };
    }

    /// <summary>The text up to about <paramref name="max"/> characters, cut at a space where one is near, and how much was left off.</summary>
    internal static (string Text, int CutChars) Trim(string text, int max)
    {
        var clean = BlankLines().Replace(text.Replace("\r\n", "\n").Trim(), "\n\n");
        if (clean.Length <= max)
        {
            return (clean, 0);
        }

        var space = clean.LastIndexOfAny([' ', '\n'], max - 1, Math.Min(CUT_SEARCH_CHARS, max));
        var cut = space > 0 ? space : max;
        if (char.IsHighSurrogate(clean[cut - 1]))
        {
            cut--;
        }

        return (clean[..cut].TrimEnd(), clean.Length - cut);
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    /// <summary>The prompts found so far in one transcript and how far it has been read. Stops reading once it has all it will show.</summary>
    private sealed class Scan(string path, IOriginLineReader reader)
    {
        private readonly JsonlTail tail = new(path);

        private readonly List<OriginPrompt> found = [];

        private readonly HashSet<string> seen = [];

        public IOriginLineReader Reader { get; } = reader;

        public IReadOnlyList<OriginPrompt> Advance()
        {
            if (found.Count >= MAX_PROMPTS)
            {
                return found;
            }

            foreach (var line in tail.ReadNewLines())
            {
                if (Reader.Read(line) is { } prompt && seen.Add(prompt.Text))
                {
                    found.Add(prompt);
                    if (found.Count >= MAX_PROMPTS)
                    {
                        break;
                    }
                }
            }

            return found;
        }
    }
}
