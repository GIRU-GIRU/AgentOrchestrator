using System.Text.Json;
using AgentManager.Core.Transcripts;

namespace AgentManager.Core.Claude;

/// <summary>
/// One running Claude Code session, as it registers itself in <c>~/.claude/sessions/&lt;pid&gt;.json</c>. <c>Status</c> is
/// <c>busy</c>, <c>idle</c> or <c>waiting</c> (a permission prompt or a question, named by <c>WaitingFor</c>).
/// </summary>
public sealed record ClaudeRegistryEntry(
    int Pid,
    long ProcessStart,
    string SessionId,
    string? Cwd,
    string? Name,
    string? Status,
    string? WaitingFor,
    DateTimeOffset? StartedAt,
    DateTimeOffset? StatusAt,
    string? Entrypoint,
    string? Kind,
    string? SocketPath,
    string? NameSource)
{
    /// <summary>The registry name, when the user gave it; Claude derives one (<c>myproject-8b</c>) for every other session.</summary>
    public string? UserName => NameSource == "user" ? Name : null;

    public string Surface
    {
        get
        {
            var entry = (Entrypoint ?? "").ToLowerInvariant();
            var surface = entry switch
            {
                _ when entry.Contains("desktop") => "DESKTOP",
                _ when entry.Contains("vscode") => "VS CODE",
                _ when entry.Contains("jetbrains") => "JETBRAINS",
                _ when entry.StartsWith("sdk", StringComparison.Ordinal) => "SDK",
                _ when entry.Contains("web") => "WEB",
                _ => "CLI",
            };
            var isBackground = Kind is not null && Kind != "interactive";
            return isBackground ? "BG · " + surface : surface;
        }
    }

    public static IReadOnlyList<ClaudeRegistryEntry> Read(string sessionsDir)
    {
        var entries = new List<ClaudeRegistryEntry>();
        if (!Directory.Exists(sessionsDir))
        {
            return entries;
        }

        foreach (var file in Directory.EnumerateFiles(sessionsDir, "*.json"))
        {
            try
            {
                using var stream = JsonlTail.OpenShared(file);
                using var document = JsonDocument.Parse(stream);
                var entry = Parse(document.RootElement);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return entries;
    }

    internal static ClaudeRegistryEntry? Parse(JsonElement root)
    {
        var sessionId = root.Str("sessionId");
        var pid = (int)root.Num("pid");
        if (sessionId is null || pid <= 0)
        {
            return null;
        }

        var started = root.Num("startedAt");
        var statusAt = root.Num("statusUpdatedAt");
        return new ClaudeRegistryEntry(
            pid,
            root.Num("procStart"),
            sessionId,
            root.Str("cwd"),
            root.Str("name"),
            root.Str("status"),
            root.Str("waitingFor"),
            started > 0 ? Json.FromUnixMilliseconds(started) : null,
            statusAt > 0 ? Json.FromUnixMilliseconds(statusAt) : null,
            root.Str("entrypoint"),
            root.Str("kind"),
            root.Str("messagingSocketPath"),
            root.Str("nameSource"));
    }
}
