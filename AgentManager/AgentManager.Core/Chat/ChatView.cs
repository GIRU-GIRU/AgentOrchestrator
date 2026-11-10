using AgentManager.Core.Model;

namespace AgentManager.Core.Chat;

/// <summary>What the chat panel shows, apart from how it looks.</summary>
public static class ChatView
{
    private const int EVERYONE_FROM = 4;

    /// <summary>Counted when the message merged, never from the commas in its name: a session titled by its first prompt can hold any number of them.</summary>
    public static string Recipients(ChatMessage message)
    {
        return message.Recipients >= EVERYONE_FROM ? $"EVERYONE ({message.Recipients})" : message.To;
    }

    /// <summary>His chip leaves with him, so a filter left on would blank the chat with no way to turn it off.</summary>
    public static bool ShowsOnlyMasterAgent(bool isChipOn, string? masterAgentKey)
    {
        return isChipOn && masterAgentKey is not null;
    }
}
