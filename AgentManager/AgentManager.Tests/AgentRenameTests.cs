using AgentManager.Core;
using AgentManager.Core.Model;
using AgentManager.Core.Windows;

namespace AgentManager.Tests;

/// <summary>The screens are copied from a real Claude Code 2.1.318 and a real Codex 0.166 in a windowless console.</summary>
public sealed class AgentRenameTests
{
    private const char NBSP = '\u00A0';

    private static readonly string RULE = new('─', 120);

    private static readonly string CLAUDE_FOOTER = "  ⏵⏵ auto mode on (shift+tab to cycle)";

    private static AgentSnapshot Agent(AgentKind kind = AgentKind.Claude, string name = "a", string surface = Delivery.CONSOLE_SURFACE, int? pid = 100, bool alive = true)
    {
        return new AgentSnapshot
        {
            Key = "k",
            Kind = kind,
            SessionId = "a",
            Name = name,
            Surface = surface,
            Pid = pid,
            IsAlive = alive,
            HasPrompt = true,
            TranscriptPath = "a.jsonl",
            ResumeCommand = "claude --resume a",
        };
    }

    private static ConsoleScreen Screen(int column, int row, params string[] rows)
    {
        return new ConsoleScreen(rows, column, row);
    }

    [Fact]
    public void ClaudeEmptyBoxReadsEmpty()
    {
        var box = Composer.Read(AgentKind.Claude, Screen(2, 2, "✻ Cooked for 2m 17s", RULE, "❯", RULE, CLAUDE_FOOTER));

        Assert.Equal(ComposerState.Empty, box.State);
        Assert.False(box.HasStash);
    }

    [Fact]
    public void ClaudeDraftIsRead()
    {
        var box = Composer.Read(AgentKind.Claude, Screen(23, 1, RULE, $"❯{NBSP}half typed draft here", RULE, CLAUDE_FOOTER));

        Assert.Equal(ComposerState.Draft, box.State);
        Assert.Equal("half typed draft here", box.Text);
    }

    [Fact]
    public void ClaudeRuleMayCarryTheSessionName()
    {
        var named = new string('─', 100) + " lab name one ─";

        var box = Composer.Read(AgentKind.Claude, Screen(23, 1, named, $"❯{NBSP}half typed draft here", RULE, CLAUDE_FOOTER));

        Assert.Equal("half typed draft here", box.Text);
    }

    [Fact]
    public void ClaudeDraftOnTwoLinesIsReadWhole()
    {
        var box = Composer.Read(AgentKind.Claude, Screen(13, 2, RULE, $"❯{NBSP}first line", "  second line", RULE, CLAUDE_FOOTER));

        Assert.Equal(ComposerState.Draft, box.State);
        Assert.Equal("first line\nsecond line", box.Text);
    }

    [Fact]
    public void ClaudeStashMarkIsSeen()
    {
        const string STATUS = "                  Session renamed to: mid turn name · › stashed";

        var empty = Composer.Read(AgentKind.Claude, Screen(2, 2, STATUS, RULE, "❯", RULE, CLAUDE_FOOTER));
        var draft = Composer.Read(AgentKind.Claude, Screen(14, 2, STATUS, RULE, $"❯{NBSP}second draft", RULE, CLAUDE_FOOTER));

        Assert.True(empty.HasStash);
        Assert.Equal(ComposerState.Empty, empty.State);
        Assert.True(draft.HasStash);
        Assert.Equal(ComposerState.Draft, draft.State);
    }

    [Fact]
    public void ClaudeWorkingStillHasABox()
    {
        var box = Composer.Read(AgentKind.Claude, Screen(22, 3, "✽ Herding…", "  ⎿  Tip: Start with small features", RULE, $"❯{NBSP}draft while it works", RULE, "  ⏵⏵ auto mode on (shift+tab to cycle) · esc to interrupt"));

        Assert.Equal("draft while it works", box.Text);
    }

    [Fact]
    public void ClaudeCommandPopupIsNotTheBox()
    {
        var box = Composer.Read(AgentKind.Claude, Screen(9, 3, "  ❯ /status                     Show Claude Code status", "    /statusline                 Set up", RULE, $"❯{NBSP}/status", RULE, CLAUDE_FOOTER));

        Assert.Equal("/status", box.Text);
    }

    [Fact]
    public void ClaudePanelHasNoBox()
    {
        var box = Composer.Read(AgentKind.Claude, Screen(13, 1, "   Model:                      Default", "   Setting sources:", "", "   Esc to cancel"));

        Assert.Equal(ComposerState.NotShowing, box.State);
    }

    [Fact]
    public void ClaudeChoiceBetweenRulesIsADialogNotADraft()
    {
        var box = Composer.Read(AgentKind.Claude, Screen(8, 1, RULE, $"❯{NBSP}1. Yes", "  2. No", RULE));

        Assert.Equal(ComposerState.NotShowing, box.State);
    }

    [Fact]
    public void TextBehindACaretAtTheStartReadsEmptyLikeAPlaceholder()
    {
        var claude = Composer.Read(AgentKind.Claude, Screen(2, 1, RULE, $"❯{NBSP}Press up to edit queued messages", RULE));
        var codex = Composer.Read(AgentKind.Codex, Screen(2, 0, "› Ask Codex to do anything", "", "  gpt-5.1 default"));

        Assert.Equal(ComposerState.Empty, claude.State);
        Assert.Equal(ComposerState.Empty, codex.State);
    }

    [Fact]
    public void CodexDraftIsRead()
    {
        var box = Composer.Read(AgentKind.Codex, Screen(18, 1, "• Working (2s • esc to interrupt)", "› half typed draft", "", "  gpt-5.1 default · C:\\cxlab\\work", "  tab to queue message"));

        Assert.Equal(ComposerState.Draft, box.State);
        Assert.Equal("half typed draft", box.Text);
    }

    [Fact]
    public void CodexDraftOnTwoLinesIsReadWhole()
    {
        var box = Composer.Read(AgentKind.Codex, Screen(5, 1, "› one", "  two", "", "  gpt-5.1 default"));

        Assert.Equal("one\ntwo", box.Text);
    }

    [Fact]
    public void CodexPanelHasNoBox()
    {
        var box = Composer.Read(AgentKind.Codex, Screen(119, 5, "  Select Model and Effort", "", "› 1. GPT-6.1-Sol    Latest workhorse model", "  2. GPT-6-Astra    Frontier intelligence", "↓", "  enter select · esc back"));

        Assert.Equal(ComposerState.NotShowing, box.State);
    }

    [Fact]
    public void CodexCommandPopupIsNotTheBox()
    {
        var box = Composer.Read(AgentKind.Codex, Screen(8, 2, "› /model  choose what model and reasoning effort to use", "", "› /model", "", "  gpt-5.1 default"));

        Assert.Equal("/model", box.Text);
    }

    [Fact]
    public void ACaretOffTheScreenIsNoBox()
    {
        Assert.Equal(ComposerState.NotShowing, Composer.Read(AgentKind.Claude, Screen(0, 9, RULE)).State);
        Assert.Equal(ComposerState.NotShowing, Composer.Read(AgentKind.Claude, null).State);
    }

    [Fact]
    public void ACliAgentIsTypedInto()
    {
        Assert.True(AgentRename.Plan(Agent()).CanType);
        Assert.True(AgentRename.Plan(Agent(AgentKind.Codex)).CanType);
    }

    [Fact]
    public void TheMasterAgentKeepsHisName()
    {
        var plan = AgentRename.Plan(Agent() with { IsMasterAgent = true });

        Assert.False(plan.CanType);
        Assert.Contains("manager", plan.Why);
    }

    [Fact]
    public void AnAstraCardIsNamedAfterItsChannel()
    {
        Assert.False(AgentRename.Plan(Agent(AgentKind.Codex, "Astra: sheathe-draw")).CanType);
    }

    [Fact]
    public void ADialogIsNeverTypedInto()
    {
        Assert.Contains("dialog", AgentRename.Plan(Agent() with { WaitingFor = "permission" }).Why);
        Assert.Contains("dialog", AgentRename.Plan(Agent() with { PendingQuestion = true }).Why);
    }

    [Theory]
    [InlineData("BG · CLI", "claude attach")]
    [InlineData("DESKTOP", "no console")]
    [InlineData("SDK", "no console")]
    public void NoConsoleIsSaidPlainly(string surface, string why)
    {
        var plan = AgentRename.Plan(Agent(surface: surface));

        Assert.False(plan.CanType);
        Assert.Contains(why, plan.Why);
    }

    [Fact]
    public void ACodexThreadWithNoKnownWindowCannotBeReached()
    {
        var plan = AgentRename.Plan(Agent(AgentKind.Codex, pid: null));

        Assert.False(plan.CanType);
        Assert.Contains("window is not known", plan.Why);
    }

    [Fact]
    public void AClosedAgentIsNotTypedInto()
    {
        Assert.False(AgentRename.Plan(Agent(alive: false)).CanType);
    }

    [Theory]
    [InlineData("  lucerne   arts ", "lucerne arts")]
    [InlineData("one\r\ntwo\tthree", "one two three")]
    [InlineData("\u0007bell", "bell")]
    public void ANameGoesOnOneLineTrimmed(string typed, string name)
    {
        Assert.Equal(name, AgentRename.CleanName(AgentKind.Claude, typed).Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n ")]
    [InlineData(null)]
    public void AnEmptyNameCancels(string? typed)
    {
        var name = AgentRename.CleanName(AgentKind.Claude, typed);

        Assert.Null(name.Name);
        Assert.Null(name.Problem);
    }

    [Fact]
    public void ANameCannotBeTooLong()
    {
        Assert.NotNull(AgentRename.CleanName(AgentKind.Claude, new string('x', AgentRename.MAX_NAME + 1)).Problem);
        Assert.NotNull(AgentRename.CleanName(AgentKind.Claude, new string('x', AgentRename.MAX_NAME)).Name);
    }

    [Theory]
    [InlineData("MASTER AGENT")]
    [InlineData("master_agent")]
    [InlineData("Astra: my channel")]
    public void ANameTheManagerWouldActOnIsRefused(string typed)
    {
        Assert.NotNull(AgentRename.CleanName(AgentKind.Claude, typed).Problem);
        Assert.NotNull(AgentRename.CleanName(AgentKind.Codex, typed).Problem);
    }

    [Theory]
    [InlineData("foo @bar")]
    [InlineData("$skill foo")]
    [InlineData("party 🎉")]
    public void CodexRefusesWhatItsComposerMisreads(string typed)
    {
        Assert.NotNull(AgentRename.CleanName(AgentKind.Codex, typed).Problem);
        Assert.Equal(typed, AgentRename.CleanName(AgentKind.Claude, typed).Name);
    }

    [Fact]
    public void CodexAcceptsThoseCharactersInsideAWord()
    {
        Assert.Equal("me@home costs 5$", AgentRename.CleanName(AgentKind.Codex, "me@home costs 5$").Name);
    }

    [Fact]
    public void ReadBackIgnoresSpacingAndWhatTheConsoleCannotShow()
    {
        Assert.True(AgentRename.ReadsAs($"/rename{NBSP}日本語 café \uFFFD x", "/rename 日本語 café 🎉 x"));
        Assert.True(AgentRename.ReadsAs("/rename a very\nlong name", "/rename a very long name"));
        Assert.False(AgentRename.ReadsAs("/rename xdraft", "/rename x"));
    }

    [Fact]
    public void TheNoteSaysWhatHappensToADraft()
    {
        var draft = new ComposerReading(ComposerState.Draft, "half typed");
        var both = new ComposerReading(ComposerState.Draft, "half typed", HasStash: true);

        Assert.Null(AgentRename.Blocker(AgentKind.Claude, draft));
        Assert.Contains("comes back", AgentRename.Note(AgentKind.Claude, draft));
        Assert.Contains("replace the stash", AgentRename.Blocker(AgentKind.Claude, both));
        Assert.Contains("Codex cannot set it aside", AgentRename.Blocker(AgentKind.Codex, draft));
        Assert.Equal(AgentRename.HINT, AgentRename.Note(AgentKind.Codex, new ComposerReading(ComposerState.Empty)));
        Assert.NotNull(AgentRename.Blocker(AgentKind.Claude, new ComposerReading(ComposerState.NotShowing)));
    }

    [Fact]
    public void AnEmptyBoxGetsTheCommandAlone()
    {
        var console = new FakeCli(AgentKind.Claude);

        var result = Run(console);

        Assert.True(result.IsSent);
        Assert.Equal(["type:/rename new name", "enter"], console.Calls);
        Assert.Equal(["/rename new name"], console.Submitted);
    }

    [Fact]
    public void ACodexEmptyBoxGetsTheCommandToo()
    {
        var console = new FakeCli(AgentKind.Codex);

        Assert.True(Run(console).IsSent);
        Assert.Equal(["/rename new name"], console.Submitted);
    }

    [Fact]
    public void AClaudeDraftIsSetAsideAndComesBack()
    {
        var console = new FakeCli(AgentKind.Claude) { Box = "half typed", Caret = 10 };

        var result = Run(console);

        Assert.True(result.IsSent);
        Assert.Contains("back in its box", result.How);
        Assert.Equal(["stash", "type:/rename new name", "enter"], console.Calls);
        Assert.Equal(["/rename new name"], console.Submitted);
        Assert.Equal("half typed", console.Box);
        Assert.Null(console.Stash);
    }

    [Fact]
    public void ACliThatDoesNotPutTheDraftBackGetsTheStashKeyPressed()
    {
        var console = new FakeCli(AgentKind.Claude) { Box = "half typed", Caret = 10, RestoresStash = false };

        var result = Run(console);

        Assert.True(result.IsSent);
        Assert.Equal("half typed", console.Box);
        Assert.Equal(["stash", "type:/rename new name", "enter", "stash"], console.Calls);
    }

    [Fact]
    public void ACodexDraftIsLeftAloneAndNothingTyped()
    {
        var console = new FakeCli(AgentKind.Codex) { Box = "half typed", Caret = 10 };

        var result = Run(console);

        Assert.False(result.IsSent);
        Assert.Empty(console.Calls);
        Assert.Equal("half typed", console.Box);
    }

    [Fact]
    public void ADraftWithAnOlderStashIsLeftAlone()
    {
        var console = new FakeCli(AgentKind.Claude) { Box = "half typed", Caret = 10, Stash = "older" };

        var result = Run(console);

        Assert.False(result.IsSent);
        Assert.Empty(console.Calls);
        Assert.Equal("older", console.Stash);
    }

    [Fact]
    public void APanelOpenMeansNoKeys()
    {
        var console = new FakeCli(AgentKind.Claude) { IsPanelOpen = true };

        var result = Run(console);

        Assert.False(result.IsSent);
        Assert.Empty(console.Calls);
    }

    [Fact]
    public void ADraftBehindTheCaretIsErasedBackNeverSent()
    {
        var console = new FakeCli(AgentKind.Claude) { Box = "draft words", Caret = 0 };

        var result = Run(console);

        Assert.False(result.IsSent);
        Assert.DoesNotContain("enter", console.Calls);
        Assert.Empty(console.Submitted);
        Assert.Equal("draft words", console.Box);
        Assert.Equal(0, console.Caret);
    }

    [Fact]
    public void AnEmojiIsErasedAsOneCharacter()
    {
        var console = new FakeCli(AgentKind.Claude) { Box = "draft words", Caret = 0 };

        AgentRename.Run(AgentKind.Claude, "a🎉", console, _ => { });

        Assert.Contains("erase:10", console.Calls);
    }

    [Fact]
    public void AStashKeyThatDoesNothingMeansNothingTyped()
    {
        var console = new FakeCli(AgentKind.Claude) { Box = "half typed", Caret = 10, HasStashKey = false };

        var result = Run(console);

        Assert.False(result.IsSent);
        Assert.Equal(["stash"], console.Calls);
        Assert.Equal("half typed", console.Box);
    }

    [Fact]
    public void ACommandTheCliIgnoresIsErasedAndTheDraftPutBack()
    {
        var console = new FakeCli(AgentKind.Claude) { Box = "half typed", Caret = 10, TakesEnter = false };

        var result = Run(console);

        Assert.False(result.IsSent);
        Assert.Empty(console.Submitted);
        Assert.Equal("half typed", console.Box);
        Assert.Null(console.Stash);
    }

    private static RenameResult Run(FakeCli console)
    {
        return AgentRename.Run(console.Kind, "new name", console, _ => { });
    }

    /// <summary>The input box of either CLI, drawn the way the real one draws it.</summary>
    private sealed class FakeCli(AgentKind kind) : IAgentConsole
    {
        public AgentKind Kind { get; } = kind;

        public string Box { get; set; } = "";

        public int Caret { get; set; }

        public string? Stash { get; set; }

        public bool IsPanelOpen { get; init; }

        public bool HasStashKey { get; init; } = true;

        public bool TakesEnter { get; init; } = true;

        public bool RestoresStash { get; init; } = true;

        public List<string> Calls { get; } = [];

        public List<string> Submitted { get; } = [];

        public ConsoleScreen? ReadScreen()
        {
            if (IsPanelOpen)
            {
                return new ConsoleScreen(["   Model:   Default", "", "   Esc to cancel"], 13, 0);
            }

            if (Kind == AgentKind.Codex)
            {
                return new ConsoleScreen([Box.Length == 0 ? "› Ask Codex to do anything" : "› " + Box, "", "  gpt-5.1 default"], 2 + Caret, 0);
            }

            var status = Stash is null ? "   ◐ medium · /effort" : "   ◐ medium · /effort · › stashed";
            return new ConsoleScreen([status, RULE, Box.Length == 0 ? "❯" : $"❯{NBSP}{Box}", RULE, "  footer"], 2 + Caret, 2);
        }

        public bool Type(string text)
        {
            Calls.Add("type:" + text);
            Box = Box.Insert(Caret, text);
            Caret += text.Length;
            return true;
        }

        public bool PressEnter()
        {
            Calls.Add("enter");
            if (!TakesEnter)
            {
                return true;
            }

            Submitted.Add(Box);
            Box = "";
            Caret = 0;
            if (RestoresStash)
            {
                PopStash();
            }

            return true;
        }

        public bool PressStash()
        {
            Calls.Add("stash");
            if (!HasStashKey)
            {
                return true;
            }

            if (Box.Length == 0)
            {
                PopStash();
                return true;
            }

            Stash = Box;
            Box = "";
            Caret = 0;
            return true;
        }

        public bool Erase(int characters)
        {
            Calls.Add("erase:" + characters);
            var count = Math.Min(characters, Caret);
            Box = Box.Remove(Caret - count, count);
            Caret -= count;
            return true;
        }

        private void PopStash()
        {
            if (Stash is { } stash)
            {
                Box = stash;
                Caret = stash.Length;
                Stash = null;
            }
        }
    }
}
