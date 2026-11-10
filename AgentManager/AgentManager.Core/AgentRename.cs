using System.Globalization;
using System.Text.RegularExpressions;
using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;
using AgentManager.Core.Windows;

namespace AgentManager.Core;

public enum ComposerState
{
    Empty,
    Draft,
    NotShowing,
}

public sealed record ComposerReading(ComposerState State, string Text = "", bool HasStash = false);

/// <summary>
/// What is in an agent's input box, read off its console screen, because neither CLI records a half-typed prompt anywhere else. The
/// caret finds the box: a panel or dialog parks it elsewhere, which is the only reliable sign the box is gone. Text with the caret at
/// its very start reads as empty, since that is what a placeholder or a suggestion looks like; a real draft with the caret moved
/// there is caught by the read-back in <see cref="AgentRename.Run"/>.
/// </summary>
public static partial class Composer
{
    private const int PROMPT_COLUMNS = 2;

    private const int RULE_MIN = 8;

    private const char RULE = '─';

    private const string CONTINUATION = "  ";

    private const string CLAUDE_STASH_MARK = "› stashed";

    private const char CODEX_PROMPT = '›';

    private static readonly char[] CLAUDE_PROMPTS = ['❯', '>', '!'];

    private static readonly ComposerReading NOT_SHOWING = new(ComposerState.NotShowing);

    public static ComposerReading Read(AgentKind kind, ConsoleScreen? screen)
    {
        if (screen is null || screen.CursorRow < 0 || screen.CursorRow >= screen.Rows.Count)
        {
            return NOT_SHOWING;
        }

        return kind == AgentKind.Claude ? ReadClaude(screen) : ReadCodex(screen);
    }

    /// <summary>
    /// The box is the rows between two rules. The upper rule may carry the session's name, and the row above it, when anything is
    /// stashed, ends in the stash mark.
    /// </summary>
    private static ComposerReading ReadClaude(ConsoleScreen screen)
    {
        var rows = screen.Rows;
        if (IsRule(rows[screen.CursorRow]))
        {
            return NOT_SHOWING;
        }

        var top = screen.CursorRow - 1;
        while (top >= 0 && !IsRule(rows[top]))
        {
            top--;
        }

        var bottom = screen.CursorRow + 1;
        while (bottom < rows.Count && !IsRule(rows[bottom]))
        {
            bottom++;
        }

        if (top < 0 || bottom >= rows.Count || rows[top + 1].Length == 0 || !CLAUDE_PROMPTS.Contains(rows[top + 1][0]))
        {
            return NOT_SHOWING;
        }

        var hasStash = top > 0 && rows[top - 1].Contains(CLAUDE_STASH_MARK, StringComparison.Ordinal);
        return Reading(screen, top + 1, bottom, hasStash);
    }

    /// <summary>The box is the prompt row and the indented rows under it, down to a blank row.</summary>
    private static ComposerReading ReadCodex(ConsoleScreen screen)
    {
        var rows = screen.Rows;
        var top = screen.CursorRow;
        while (top >= 0 && (rows[top].Length == 0 || rows[top][0] != CODEX_PROMPT))
        {
            if (!rows[top].StartsWith(CONTINUATION, StringComparison.Ordinal))
            {
                return NOT_SHOWING;
            }

            top--;
        }

        if (top < 0)
        {
            return NOT_SHOWING;
        }

        var end = screen.CursorRow + 1;
        while (end < rows.Count && rows[end].Length > 0)
        {
            end++;
        }

        return Reading(screen, top, end, false);
    }

    private static ComposerReading Reading(ConsoleScreen screen, int first, int end, bool hasStash)
    {
        var lines = screen.Rows.Skip(first).Take(end - first).Select((row, i) => (i == 0 ? row[1..] : row).Trim()).ToList();
        var text = string.Join('\n', lines).Trim();
        if (NumberedChoice().IsMatch(text))
        {
            return NOT_SHOWING;
        }

        var isCaretAtStart = screen.CursorRow == first && screen.CursorColumn <= PROMPT_COLUMNS;
        return text.Length == 0 || isCaretAtStart
            ? new ComposerReading(ComposerState.Empty, "", hasStash)
            : new ComposerReading(ComposerState.Draft, text, hasStash);
    }

    private static bool IsRule(string row)
    {
        return row.Length >= RULE_MIN && row[..RULE_MIN].All(c => c == RULE) && row[^1] == RULE;
    }

    [GeneratedRegex(@"^\d+\.\s")]
    private static partial Regex NumberedChoice();
}

public sealed record RenamePlan(bool CanType, string Why);

/// <summary><see cref="Name"/> and <see cref="Problem"/> both null means the user left it empty, which cancels.</summary>
public sealed record RenameName(string? Name, string? Problem);

public sealed record RenameResult(bool IsSent, string How);

/// <summary>
/// F2 on a card: <c>/rename</c> typed into the agent's own console, so the session records its own name and the card's title follows from
/// that record. Both CLIs run the command at once even mid-turn (Claude Code marks it immediate, and Codex does the same), so a working
/// agent is typed into as well.
/// </summary>
public static class AgentRename
{
    public const int MAX_NAME = 100;

    public const string HINT = "enter renames · esc cancels";

    private const string COMMAND = "/rename ";

    private const string BACKGROUND_SURFACE = "BG";

    private const int NOTE_TEXT = 40;

    /// <summary>How often the box is read back, and how many times, before the CLI is taken not to have done what it was asked.</summary>
    private const int READ_TRIES = 60;

    private static readonly TimeSpan READ_GAP = TimeSpan.FromMilliseconds(50);

    /// <summary>Enter goes separately, after the text has been read, so a fast batch is not taken as a paste ending in a newline.</summary>
    private static readonly TimeSpan ENTER_DELAY = TimeSpan.FromMilliseconds(300);

    public static RenamePlan Plan(AgentSnapshot agent)
    {
        if (agent.IsMasterAgent)
        {
            return new(false, "the manager fixes this name");
        }

        if (!agent.IsAlive)
        {
            return new(false, "its window is closed: resume it to rename it");
        }

        if (BridgeRules.IsAstraTitle(agent.Name))
        {
            return new(false, "an Astra's card is named after its channel, so a new name would never show");
        }

        var delivery = Delivery.Plan(agent, hasInboxOwner: false);
        if (delivery.Route == DeliveryRoute.Console)
        {
            return new(true, "");
        }

        if (agent.PendingQuestion || agent.WaitingFor is not null)
        {
            return new(false, delivery.Why);
        }

        if (agent.Pid is null)
        {
            return new(false, "its window is not known, so nothing can reach it: run /rename in that window");
        }

        return new(false, agent.Surface.StartsWith(BACKGROUND_SURFACE, StringComparison.Ordinal)
            ? "a background session has no console to type into: claude attach it, then /rename"
            : $"no console to type /rename into ({agent.Surface})");
    }

    /// <summary>
    /// Codex's composer opens a picker on a word starting with @ or $, and Enter then takes the picker instead of the command. It also
    /// loses a character outside the 16-bit range typed as two key events (Claude Code keeps both). A name that would make the
    /// card pass for the Master Agent or an Astra is refused for what the manager would then do with that card.
    /// </summary>
    public static RenameName CleanName(AgentKind kind, string? typed)
    {
        var words = new string((typed ?? "").Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray())
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join(' ', words);
        if (name.Length == 0)
        {
            return new(null, null);
        }

        if (name.Length > MAX_NAME)
        {
            return new(null, $"a name is at most {MAX_NAME} characters");
        }

        if (MasterAgent.IsReservedName(name) || BridgeRules.IsAstraTitle(name))
        {
            return new(null, $"\"{name}\" belongs to the manager: a card named that would be taken for his");
        }

        if (kind == AgentKind.Codex && words.Any(word => word[0] is '@' or '$'))
        {
            return new(null, "in Codex a word starting with @ or $ opens a picker that takes the Enter");
        }

        if (kind == AgentKind.Codex && name.Any(char.IsSurrogate))
        {
            return new(null, "Codex loses an emoji typed through its console");
        }

        return new(name, null);
    }

    /// <summary>Why the box cannot be typed into as it stands, or null when it can. The stash is Claude Code's only: Codex has no way to set text aside.</summary>
    public static string? Blocker(AgentKind kind, ComposerReading box)
    {
        if (box.State == ComposerState.NotShowing)
        {
            return "its input box is not on screen: a panel or dialog is open in it";
        }

        if (box.State != ComposerState.Draft)
        {
            return null;
        }

        if (kind != AgentKind.Claude)
        {
            return $"it has unsent text (\"{Short(box.Text)}\") and Codex cannot set it aside: clear it in its window first";
        }

        return box.HasStash
            ? $"it has unsent text (\"{Short(box.Text)}\") and a stash, and setting the text aside would replace the stash: clear one in its window first"
            : null;
    }

    public static string Note(AgentKind kind, ComposerReading box)
    {
        return Blocker(kind, box) ?? (box.State == ComposerState.Draft ? $"unsent text (\"{Short(box.Text)}\") is set aside and comes back · {HINT}" : HINT);
    }

    /// <summary>
    /// The box must read exactly the command before Enter goes, so a draft nobody detected can never be sent along with it; whatever was
    /// typed is erased again if it does not. Ctrl+S sets a draft aside and Claude Code puts it back by itself once the command has run.
    /// </summary>
    public static RenameResult Run(AgentKind kind, string name, IAgentConsole console, Action<TimeSpan> wait)
    {
        ComposerReading Box() => Composer.Read(kind, console.ReadScreen());

        bool WaitFor(Func<ComposerReading, bool> isDone)
        {
            for (var i = 0; i < READ_TRIES; i++)
            {
                if (isDone(Box()))
                {
                    return true;
                }

                wait(READ_GAP);
            }

            return false;
        }

        var before = Box();
        if (Blocker(kind, before) is { } blocker)
        {
            return new(false, blocker);
        }

        var isSetAside = false;
        if (before.State == ComposerState.Draft)
        {
            console.PressStash();
            if (!WaitFor(box => box.State == ComposerState.Empty))
            {
                return new(false, "its unsent text would not move aside, so nothing was typed");
            }

            isSetAside = true;
        }

        void Undo(string typed)
        {
            if (Box().State != ComposerState.NotShowing)
            {
                console.Erase(new StringInfo(typed).LengthInTextElements);
            }

            if (isSetAside && WaitFor(box => box.State == ComposerState.Empty))
            {
                console.PressStash();
            }
        }

        var command = COMMAND + name;
        if (!console.Type(command))
        {
            if (isSetAside)
            {
                console.PressStash();
            }

            return new(false, "the keys did not reach its console");
        }

        if (!WaitFor(box => ReadsAs(box.Text, command)))
        {
            Undo(command);
            return new(false, "its box did not read back as the command (text the caret was in front of?), so it was erased and nothing was sent");
        }

        wait(ENTER_DELAY);
        console.PressEnter();
        if (!WaitFor(box => box.State != ComposerState.NotShowing && !ReadsAs(box.Text, command)))
        {
            Undo(command);
            return new(false, "the CLI did not take the command, so it was erased");
        }

        if (!isSetAside)
        {
            return new(true, "/rename typed into its console");
        }

        bool IsDraftBack(ComposerReading box)
        {
            return ReadsAs(box.Text, before.Text);
        }

        if (WaitFor(IsDraftBack))
        {
            return new(true, "/rename typed into its console, and its unsent text is back in its box");
        }

        if (Box().State == ComposerState.Empty)
        {
            console.PressStash();
            if (WaitFor(IsDraftBack))
            {
                return new(true, "/rename typed into its console, and its unsent text is back in its box");
            }
        }

        return new(true, "/rename typed into its console, but its unsent text is still stashed: ctrl+s in its window brings it back");
    }

    /// <summary>
    /// Spacing is no evidence, since the console wraps long lines and pads with non-breaking spaces. A character outside the 16-bit range
    /// reads back as U+FFFD whatever it was, so it is left out of the comparison too.
    /// </summary>
    public static bool ReadsAs(string shown, string expected)
    {
        return Comparable(shown) == Comparable(expected);
    }

    private static string Comparable(string text)
    {
        return new string(text.Where(c => !char.IsWhiteSpace(c) && !char.IsSurrogate(c) && c != '�').ToArray());
    }

    private static string Short(string text)
    {
        return Json.OneLine(text, NOTE_TEXT);
    }
}
