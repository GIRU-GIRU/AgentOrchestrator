using AgentManager.Core.Model;

namespace AgentManager.Core;

/// <summary>The X a double click puts on a finished card, which closes it.</summary>
public static class CardClose
{
    /// <summary>A paused, working or red card is not done with. A card with no known window can still be taken off the grid, so it gets the X too.</summary>
    public static bool CanOffer(AgentSnapshot agent)
    {
        return agent is { State: AgentState.Finished, IsAlive: true, IsClosing: false };
    }

    /// <summary>
    /// Ctrl+click takes a closed card off the grid and closes a finished agent's window. Never the Master Agent: closed means gone until New MASTER AGENT, which is too much for a click with no confirmation.
    /// </summary>
    public static bool ClosesOnCtrlClick(AgentSnapshot agent)
    {
        return !agent.IsAlive || (CanOffer(agent) && !agent.IsMasterAgent);
    }
}
