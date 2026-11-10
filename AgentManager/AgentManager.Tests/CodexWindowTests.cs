using AgentManager.Core.Codex;
using AgentManager.Core.Windows;

namespace AgentManager.Tests;

/// <summary>The app-server daemon case: two Codex windows, one daemon holding both threads' rollouts.</summary>
public sealed class CodexWindowTests
{
    private static readonly DateTimeOffset DAY = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly ProcessEntry EXPLORER = new(5348, 2824, "explorer.exe");

    private static readonly ProcessEntry FIRST_WINDOW = new(40304, 5348, "codex.exe");

    private static readonly ProcessEntry DAEMON = new(41620, 40304, "codex.exe");

    private static readonly ProcessEntry DAEMON_HELPER = new(16188, 40304, "codex.exe");

    private static readonly ProcessEntry SECOND_WINDOW = new(39868, 5348, "codex.exe");

    private static readonly (int Pid, DateTimeOffset Start)[] WINDOWS =
    [
        (40304, DAY.AddHours(16).AddMinutes(8).AddSeconds(13)),
        (39868, DAY.AddHours(21).AddMinutes(19).AddSeconds(31)),
    ];

    private static ProcessEntry? Get(int pid)
    {
        return new[] { EXPLORER, FIRST_WINDOW, DAEMON, DAEMON_HELPER, SECOND_WINDOW }.FirstOrDefault(p => p.Pid == pid);
    }

    [Fact]
    public void CodexStartedByCodexIsTheDaemonNotAWindow()
    {
        var daemons = CodexWindows.Daemons([FIRST_WINDOW, DAEMON, DAEMON_HELPER, SECOND_WINDOW], Get, null);

        Assert.Equal([16188, 41620], daemons.Order());
    }

    private static readonly DateTimeOffset CHECKED = new(2026, 11, 9, 22, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("1,2", 0, 10, true)]
    [InlineData("1,2,3", 0, 10, false)]
    [InlineData("1,2", 5, 10, false)]
    [InlineData("1,2", 0, 40, false)]
    public void ARolloutNobodyHeldIsAskedAboutOnlyWhenSomethingCouldHaveChanged(string processes, int newEventSeconds, int secondsLater, bool isSkipped)
    {
        var lastEvent = CHECKED.AddSeconds(newEventSeconds);

        Assert.Equal(isSkipped, CodexWindows.IsKnownNotHeld("1,2", processes, CHECKED, lastEvent, CHECKED.AddSeconds(30), CHECKED.AddSeconds(secondsLater)));
    }

    [Fact]
    public void ARolloutSomeoneHeldIsNeverKnownNotHeld()
    {
        Assert.False(CodexWindows.IsKnownNotHeld(null, "1,2", CHECKED, CHECKED, CHECKED.AddSeconds(30), CHECKED));
    }

    [Theory]
    [InlineData("1,2", 10, 7)]
    [InlineData("1,2,3", 10, null)]
    [InlineData("1", 10, null)]
    [InlineData("1,2", 40, null)]
    public void AHeldRolloutIsAskedAboutAgainOnlyWhenTheCodexProcessesChangeOrTheRecheckComes(string processes, int secondsLater, int? holder)
    {
        Assert.Equal(holder, CodexWindows.KnownHolder((7, "1,2"), processes, CHECKED.AddSeconds(30), CHECKED.AddSeconds(secondsLater)));
    }

    [Fact]
    public void ARolloutNobodyHeldHasNoKnownHolder()
    {
        Assert.Null(CodexWindows.KnownHolder(null, "1,2", CHECKED.AddSeconds(30), CHECKED));
    }

    [Fact]
    public void ThePidUpdateLoopIsNotAWindowThoughNoCodexStartedIt()
    {
        var helper = DAEMON with { ParentPid = 1 };

        var daemons = CodexWindows.Daemons([helper, SECOND_WINDOW], Get, null, _ => "\"C:\\x\\codex.exe\" app-server daemon pid-update-loop");

        Assert.Contains(helper.Pid, daemons);
    }

    [Theory]
    [InlineData("\"C:\\x\\codex.exe\" app-server --listen unix://", true)]
    [InlineData("codex.exe app-server daemon pid-update-loop", true)]
    [InlineData("codex.exe \"tell me about the app-server\"", false)]
    [InlineData("codex.exe resume app-server", false)]
    [InlineData("codex.exe", false)]
    public void OnlyTheFirstArgumentMakesAnAppServer(string commandLine, bool isAppServer)
    {
        Assert.Equal(isAppServer, CodexWindows.IsAppServer(commandLine));
    }

    private static readonly DateTimeOffset BASE = new(2026, 11, 9, 18, 23, 0, TimeSpan.Zero);

    [Fact]
    public void AThreadOpenedByClearGetsNoWindowFromItsStartTime()
    {
        (int, DateTimeOffset)[] windows = [(1, BASE.AddSeconds(18)), (2, BASE.AddSeconds(31))];
        (string, DateTimeOffset)[] threads = [("first-of-1", BASE.AddSeconds(20)), ("first-of-2", BASE.AddSeconds(31)), ("after-clear-in-1", BASE.AddSeconds(51))];

        var placed = CodexWindows.FirstThreadOfEachWindow(threads, windows);

        Assert.Equal(1, placed["first-of-1"]);
        Assert.Equal(2, placed["first-of-2"]);
        Assert.False(placed.ContainsKey("after-clear-in-1"));
    }

    [Fact]
    public void WrappedStyledScreenTextStillMatchesItsPrompt()
    {
        var screen = CodexWindows.ScreenLetters(["› prompt typed after clear in", "  window A **papa** quebec romeo", "", "• done"]);

        var shown = CodexWindows.WindowShowing(["prompt typed after clear in window A papa quebec romeo"], new Dictionary<int, string> { [7] = screen, [8] = CodexWindows.ScreenLetters(["some other window"]) });

        Assert.Equal(7, shown);
    }

    [Fact]
    public void WordsTooShortToTellWindowsApartPlaceNothing()
    {
        Assert.Null(CodexWindows.WindowShowing(["chop chop"], new Dictionary<int, string> { [7] = "chopchop" }));
    }

    [Fact]
    public void TwoWindowsShowingTheSameWordsPlaceNothing()
    {
        var both = CodexWindows.ScreenLetters(["prompt typed after clear in window A"]);

        Assert.Null(CodexWindows.WindowShowing(["prompt typed after clear in window A"], new Dictionary<int, string> { [7] = both, [8] = both }));
    }

    [Fact]
    public void TheWindowShowingMoreOfTheThreadWinsOverOneThatSharesItsFirstPrompt()
    {
        var shared = CodexWindows.ScreenLetters(["run the inbox for this bridge channel"]);
        var mine = shared + CodexWindows.ScreenLetters(["the reply only this thread ever wrote"]);

        var shown = CodexWindows.WindowShowing(["run the inbox for this bridge channel", "the reply only this thread ever wrote"], new Dictionary<int, string> { [7] = shared, [8] = mine });

        Assert.Equal(8, shown);
    }

    [Fact]
    public void PidFileNamesTheDaemonAfterItsWindowClosed()
    {
        var orphan = DAEMON with { ParentPid = 1 };

        Assert.Contains(41620, CodexWindows.Daemons([orphan, SECOND_WINDOW], Get, 41620));
        Assert.DoesNotContain(41620, CodexWindows.Daemons([orphan, SECOND_WINDOW], Get, null));
    }

    [Fact]
    public void EachWindowGetsTheThreadItOpened()
    {
        var astra = DAY.AddHours(16).AddMinutes(8).AddSeconds(17.78);
        var inbox = DAY.AddHours(21).AddMinutes(19).AddSeconds(32.21);

        Assert.Equal(40304, CodexWindows.WindowFor(astra, WINDOWS));
        Assert.Equal(39868, CodexWindows.WindowFor(inbox, WINDOWS));
    }

    [Fact]
    public void WindowsOpenedTogetherEachGetTheThreadOfTheirOwnPrompt()
    {
        const string DOCS = "Run .claude/scripts/orch.cmd --as astra-api-docs inbox api-docs and do what it says.";
        const string FX = "Run .claude/scripts/orch.cmd --as astra-cache-layer inbox cache-layer and do what it says.";
        var opened = DAY.AddHours(1);
        (int, DateTimeOffset, string?)[] windows =
        [
            (34860, opened, "\"C:\\codex.exe\"  \"Run .claude/scripts/orch.cmd --as astra-parser inbox parser and do what it says.\""),
            (35916, opened, $"\"C:\\codex.exe\"  \"{FX}\""),
            (37896, opened, $"\"C:\\codex.exe\"  \"{DOCS}\""),
        ];
        var created = opened.AddSeconds(1);

        Assert.Equal(37896, CodexWindows.WindowPrompted(DOCS, created, windows));
        Assert.Equal(35916, CodexWindows.WindowPrompted(FX, created, windows));
        Assert.Equal(37896, CodexWindows.WindowPrompted(DOCS[..60] + "…", created, windows));
    }

    [Fact]
    public void WithNoPromptToGoByTheStartTimeDecides()
    {
        (int, DateTimeOffset, string?)[] windows = [(40304, DAY, "codex"), (39868, DAY, null)];

        Assert.Null(CodexWindows.WindowPrompted("Fix the door", DAY.AddSeconds(1), windows));
        Assert.Null(CodexWindows.WindowPrompted(null, DAY.AddSeconds(1), windows));
        Assert.Null(CodexWindows.WindowPrompted("…", DAY.AddSeconds(1), windows));
    }

    [Fact]
    public void AnOlderThreadWithTheSamePromptIsNotTheNewWindows()
    {
        (int, DateTimeOffset, string?)[] windows = [(40304, DAY.AddHours(2), "codex \"Fix the door\"")];

        Assert.Null(CodexWindows.WindowPrompted("Fix the door", DAY, windows));
        Assert.Null(CodexWindows.WindowPrompted("Fix the door", null, windows));
    }

    [Fact]
    public void AWindowReopenedWithAPromptIsStillFoundByTheThreadItResumes()
    {
        var arguments = CodexWindows.ResumeArguments("\"C:\\codex.exe\" resume 01a19efc-4d32-77b3-9a68-b02dd433f8cb \"Run .claude/scripts/orch.cmd --as astra-api-docs inbox api-docs\"");

        Assert.Equal(37896, CodexWindows.WindowResuming("01a19efc-4d32-77b3-9a68-b02dd433f8cb", null, [(37896, arguments!)]));
    }

    [Fact]
    public void ThreadStartedLaterInAWindowGetsNoWindowRatherThanAWrongOne()
    {
        Assert.Null(CodexWindows.WindowFor(DAY.AddHours(22), WINDOWS));
        Assert.Null(CodexWindows.WindowFor(null, WINDOWS));
    }

    [Theory]
    [InlineData("codex  resume 01a197de-6870-7a23-a567-96cd567a923e", "01a197de-6870-7a23-a567-96cd567a923e")]
    [InlineData("\"C:\\Program Files\\codex.exe\" resume --model gpt-6 01a197de-6870-7a23-a567-96cd567a923e", "01a197de-6870-7a23-a567-96cd567a923e")]
    [InlineData("codex resume \"Search Index - Astra\"", "Search Index - Astra")]
    public void ResumeArgumentsNameTheThread(string commandLine, string expected)
    {
        Assert.Contains(expected, CodexWindows.ResumeArguments(commandLine)!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("codex")]
    [InlineData("codex \"resume the work\"")]
    [InlineData("C:\\tools\\resume")]
    public void WindowNotStartedToResumeHasNoArguments(string? commandLine)
    {
        Assert.Null(CodexWindows.ResumeArguments(commandLine));
    }

    [Fact]
    public void ResumePickerIsAResumeWindowNamingNoThread()
    {
        var arguments = CodexWindows.ResumeArguments("codex resume");

        Assert.NotNull(arguments);
        Assert.Null(CodexWindows.WindowResuming("01a197de", "Astra", [(5992, arguments)]));
    }

    [Fact]
    public void ResumedWindowIsFoundByTheThreadsIdOrName()
    {
        (int, IReadOnlyList<string>)[] windows = [(5992, ["01a197de"]), (6000, ["Check orch inbox"])];

        Assert.Equal(5992, CodexWindows.WindowResuming("01a197de", "Astra", windows));
        Assert.Equal(6000, CodexWindows.WindowResuming("01a198fb", "Check orch inbox", windows));
        Assert.Null(CodexWindows.WindowResuming("01a199aa", null, windows));
        Assert.Null(CodexWindows.WindowResuming(null, null, windows));
    }

    [Fact]
    public void CardHandedOnByMistakeComesBackWhileItsSessionRuns()
    {
        var handedOn = new AgentManager.Core.State.AgentMark { Number = 0, DismissedAt = DAY.AddHours(23) };
        var closedByUser = new AgentManager.Core.State.AgentMark { Number = 3, DismissedAt = DAY.AddHours(23) };
        var olderEvent = DAY.AddHours(22);

        Assert.False(AgentManager.Core.AgentHub.IsHidden(handedOn, isAlive: true, olderEvent));
        Assert.True(AgentManager.Core.AgentHub.IsHidden(handedOn, isAlive: false, olderEvent));
        Assert.True(AgentManager.Core.AgentHub.IsHidden(closedByUser, isAlive: true, olderEvent));
        Assert.False(AgentManager.Core.AgentHub.IsHidden(closedByUser, isAlive: true, DAY.AddHours(23).AddMinutes(1)));
    }

    [Fact]
    public void ThreadWhoseWindowExitedIsGoneEvenWhileTheDaemonHoldsIt()
    {
        var inboxWindow = WINDOWS[1];
        var afterClose = WINDOWS.Take(1).ToArray();
        var reusedPid = new[] { WINDOWS[0], (inboxWindow.Pid, inboxWindow.Start.AddMinutes(30)) };

        Assert.False(CodexWindows.IsGone(inboxWindow, WINDOWS));
        Assert.True(CodexWindows.IsGone(inboxWindow, afterClose));
        Assert.True(CodexWindows.IsGone(inboxWindow, reusedPid));
        Assert.False(CodexWindows.IsGone(null, afterClose));
    }

    [Fact]
    public void DaemonPidFileIsParsed()
    {
        var parsed = CodexWindows.ParseDaemonPid("""{"pid":41620,"processStartTime":"134380228947684568","executableIdentity":{"digest":[1,2]}}""");

        Assert.Equal((41620, 134380228947684568L), parsed);
        Assert.Null(CodexWindows.ParseDaemonPid("not json"));
    }

    [Theory]
    [InlineData("\"C:\\Codex\\bin\\codex.exe\"", true)]
    [InlineData("codex resume 01a1c2a9-3a76-7d62-b7f1-a337b4ba40da", true)]
    [InlineData("codex -c \"developer_instructions=You are the MASTER AGENT.\" \"MASTER AGENT start: read x and follow it.\"", true)]
    [InlineData("codex -m gpt-6 exec \"do it\"", false)]
    [InlineData("codex login", false)]
    [InlineData("codex --profile login", true)]
    [InlineData("codex queue --thread t --message hi", false)]
    [InlineData("codex app-server daemon pid-update-loop", false)]
    [InlineData(null, false)]
    public void OnlyAChatWindowGetsACardBeforeItsFirstPrompt(string? commandLine, bool isChat)
    {
        Assert.Equal(isChat, CodexWindows.IsInteractive(commandLine));
    }

    [Fact]
    public void AnEmptyWindowHasACardKeyOfItsOwn()
    {
        var key = CodexSource.EmptyWindowKey((1131040, DAY));

        Assert.StartsWith(CodexSource.KEY_PREFIX, key);
        Assert.NotEqual(key, CodexSource.EmptyWindowKey((1131040, DAY.AddMinutes(1))));
    }
}
