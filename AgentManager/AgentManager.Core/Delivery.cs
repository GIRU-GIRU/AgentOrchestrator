using AgentManager.Core.Model;

namespace AgentManager.Core;

public enum DeliveryRoute
{
    Console,
    Inbox,
    Skip,
}

public sealed record DeliveryPlan(string Key, string Name, DeliveryRoute Route, string Why);

public sealed record DeliveryResult(string Key, string Name, bool IsSent, string How);

/// <summary>How a message from the user reaches an agent.</summary>
public static class Delivery
{
    public const string CONSOLE_SURFACE = "CLI";

    /// <summary>
    /// Typing into the agent's own console makes it the user's prompt. An open permission or question dialog would take
    /// the keys as its answer, so that agent is skipped. An agent with no console (desktop app, IDE) gets it through the
    /// orchestrator inbox, which a Claude reads at its next tool call; a Codex owner is shared by every Codex window (the
    /// app-server daemon), so the inbox would reach the wrong thread and Codex without a known window is skipped.
    /// </summary>
    public static DeliveryPlan Plan(AgentSnapshot agent, bool hasInboxOwner)
    {
        if (!agent.IsAlive)
        {
            return new(agent.Key, agent.Name, DeliveryRoute.Skip, "its window is closed");
        }

        if (agent.PendingQuestion || agent.WaitingFor is not null)
        {
            return new(agent.Key, agent.Name, DeliveryRoute.Skip, "a dialog is open in it, and typing would answer it");
        }

        if (agent.Pid is not null && agent.Surface == CONSOLE_SURFACE)
        {
            return new(agent.Key, agent.Name, DeliveryRoute.Console, "typed into its console as your prompt");
        }

        if (hasInboxOwner && agent.Kind == AgentKind.Claude)
        {
            return new(agent.Key, agent.Name, DeliveryRoute.Inbox, $"no console ({agent.Surface}): orchestrator inbox, read at its next tool call");
        }

        var why = agent.Pid is null ? "its window is not known" : $"no console ({agent.Surface}) and no orchestrator inbox";
        return new(agent.Key, agent.Name, DeliveryRoute.Skip, why);
    }
}
