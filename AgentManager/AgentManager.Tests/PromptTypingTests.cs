using AgentManager.Core.Model;
using AgentManager.Core.Windows;

namespace AgentManager.Tests;

/// <summary>A prompt typed into an agent's console: only into an empty box, and Enter only once the box reads back as the prompt.</summary>
public sealed class PromptTypingTests
{
    private static ConsoleScreen EmptyClaudeBox()
    {
        return new ConsoleScreen(["──────────────────", ">", "──────────────────", "  footer"], 2, 1);
    }

    private static ConsoleScreen EmptyCodexBox()
    {
        return new ConsoleScreen(["› Ask Codex to do anything", "", "  footer"], 2, 0);
    }

    [Fact]
    public void AnEmptyClaudeBoxIsTypedIntoAndSent()
    {
        var console = new FakeConsole(EmptyClaudeBox());

        var (isSent, _) = PromptTyping.TypeIfEmpty(AgentKind.Claude, "carry on", console, _ => { });

        Assert.True(isSent);
        Assert.Equal("carry on", console.TypedText);
        Assert.True(console.EnterPressed);
    }

    [Fact]
    public void AnEmptyCodexBoxIsTypedIntoAndSent()
    {
        var console = new FakeConsole(EmptyCodexBox());

        var (isSent, _) = PromptTyping.TypeIfEmpty(AgentKind.Codex, "wake up", console, _ => { });

        Assert.True(isSent);
        Assert.True(console.EnterPressed);
    }

    [Fact]
    public void ABoxWithTextInItIsLeftAlone()
    {
        var console = new FakeConsole(new ConsoleScreen(["──────────────────", "> half a thought", "──────────────────", "  footer"], 16, 1));

        var (isSent, how) = PromptTyping.TypeIfEmpty(AgentKind.Claude, "carry on", console, _ => { });

        Assert.False(isSent);
        Assert.Contains("unsent text", how);
        Assert.Null(console.TypedText);
        Assert.False(console.EnterPressed);
    }

    [Fact]
    public void ABoxThatIsNotOnScreenIsNotTypedInto()
    {
        var console = new FakeConsole(new ConsoleScreen(["Do you want to proceed?", "1. Yes", "2. No"], 0, 2));

        var (isSent, _) = PromptTyping.TypeIfEmpty(AgentKind.Claude, "carry on", console, _ => { });

        Assert.False(isSent);
        Assert.Null(console.TypedText);
        Assert.False(console.EnterPressed);
    }

    [Fact]
    public void ABoxThatDoesNotReadBackAsThePromptIsErasedAndNotSent()
    {
        var console = new FakeConsole(EmptyClaudeBox()) { EchoesTyping = false };

        var (isSent, _) = PromptTyping.TypeIfEmpty(AgentKind.Claude, "carry on", console, _ => { });

        Assert.False(isSent);
        Assert.False(console.EnterPressed);
        Assert.Equal("carry on".Length, console.Erased);
    }

    private sealed class FakeConsole(ConsoleScreen start) : IAgentConsole
    {
        private ConsoleScreen screen = start;

        public string? TypedText { get; private set; }

        public bool EnterPressed { get; private set; }

        public int Erased { get; private set; }

        public bool EchoesTyping { get; init; } = true;

        public ConsoleScreen? ReadScreen()
        {
            return screen;
        }

        public bool Type(string text)
        {
            TypedText = text;
            if (EchoesTyping)
            {
                var promptRow = screen.CursorRow;
                var rows = screen.Rows.ToList();
                rows[promptRow] = rows[promptRow][..1] + " " + text;
                screen = screen with { Rows = rows, CursorColumn = 2 + text.Length };
            }

            return true;
        }

        public bool PressEnter()
        {
            EnterPressed = true;
            return true;
        }

        public bool PressStash()
        {
            return true;
        }

        public bool Erase(int characters)
        {
            Erased = characters;
            return true;
        }
    }
}
