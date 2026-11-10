using AgentManager.Core.Model;
using AgentManager.Core.State;

namespace AgentManager.Core;

/// <summary>
/// Which agent windows to offer back after a restart: the ones open when the manager last ran in an earlier Windows
/// session. The user wraps the agents up, closes the manager and restarts; the next time they open the manager it lists
/// those windows to restore or not.
/// </summary>
public static class SessionRestore
{
    /// <summary>The logon time is read from the same clock each time; anything closer than this is the same session.</summary>
    public static readonly TimeSpan SAME_LOGON = TimeSpan.FromMinutes(1);

    /// <summary>LastActiveAt is refreshed this often even when no window opens or closes.</summary>
    public static readonly TimeSpan REFRESH = TimeSpan.FromMinutes(1);

    public static bool IsEarlierLogon(DateTimeOffset? saved, DateTimeOffset? current)
    {
        return saved is { } before && current is { } now && (now - before).Duration() > SAME_LOGON;
    }

    /// <summary>
    /// A window worth bringing back: running, and with a conversation to resume (a never-prompted one has none). The Master Agent
    /// comes back by himself, and offering him here too would run him twice.
    /// </summary>
    public static bool IsRestorable(AgentSnapshot agent)
    {
        return agent.IsAlive && agent.HasPrompt && agent.ResumeCommand.Length > 0 && !agent.IsMasterAgent;
    }

    public static OpenWindowRecord Record(AgentSnapshot agent)
    {
        return new OpenWindowRecord
        {
            Key = agent.Key,
            Kind = agent.Kind,
            Name = agent.Name,
            Cwd = agent.Cwd,
            ResumeCommand = agent.ResumeCommand,
            LastPrompt = agent.Prompts.Count > 0 ? agent.Prompts[^1].Text : agent.Title,
            LastActiveAt = agent.LastEventAt,
            Surface = agent.Surface,
        };
    }

    public static bool IsSameSet(IReadOnlyList<OpenWindowRecord> saved, IReadOnlyList<OpenWindowRecord> live)
    {
        return saved.Count == live.Count && saved.Zip(live).All(p => p.First.Key == p.Second.Key && p.First.Name == p.Second.Name && p.First.Cwd == p.Second.Cwd);
    }
}
