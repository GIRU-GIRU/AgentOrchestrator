using AgentManager.Core.Model;

namespace AgentManager.Core.Windows;

/// <summary>Types a prompt into an agent's console and sends it, but only where nothing the user wrote can be sent along with it.</summary>
public static class PromptTyping
{
    private const int READ_TRIES = 60;

    private static readonly TimeSpan READ_GAP = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan ENTER_DELAY = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Types a prompt only into an empty box, and the box must read back exactly that before Enter goes, so nothing half-typed can ever be
    /// sent along with it. Whatever was typed is erased again if it does not.
    /// </summary>
    public static (bool IsSent, string How) TypeIfEmpty(AgentKind kind, string text, IAgentConsole console, Action<TimeSpan> wait)
    {
        var box = Composer.Read(kind, console.ReadScreen());
        if (box.State == ComposerState.NotShowing)
        {
            return (false, "its input box is not on screen, so a dialog or panel may be open");
        }

        if (box.State == ComposerState.Draft)
        {
            return (false, "it has unsent text in its box");
        }

        if (!console.Type(text))
        {
            return (false, "the keys did not reach its console");
        }

        for (var i = 0; i < READ_TRIES; i++)
        {
            if (AgentRename.ReadsAs(Composer.Read(kind, console.ReadScreen()).Text, text))
            {
                wait(ENTER_DELAY);
                return console.PressEnter() ? (true, "typed into its console") : (false, "Enter did not reach its console");
            }

            wait(READ_GAP);
        }

        if (Composer.Read(kind, console.ReadScreen()).State != ComposerState.NotShowing)
        {
            console.Erase(text.Length);
        }

        return (false, "its box did not read back as the prompt, so it was erased and nothing was sent");
    }
}
