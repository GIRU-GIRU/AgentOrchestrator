using AgentManager.Core;
using AgentManager.Core.Model;
using AgentManager.Core.State;
using AgentManager.Core.Windows;

namespace AgentManager.Tests;

/// <summary>Close agent leaves no closed card behind, and a close the window refused brings the card back.</summary>
public sealed class CloseAgentTests
{
    private static readonly DateTimeOffset CLICKED = new(2026, 11, 2, 1, 41, 0, TimeSpan.Zero);

    private static AgentMark Closing()
    {
        return new AgentMark { Number = 2, ClosingAt = CLICKED };
    }

    [Fact]
    public void CardStaysOnTheGridWhileItsProcessIsBeingClosed()
    {
        Assert.False(AgentHub.IsHidden(Closing(), isAlive: true, CLICKED.AddSeconds(1)));
    }

    [Fact]
    public void ACardClosedWithNoWindowStaysAwayHoweverMuchItsThreadWrites()
    {
        var mark = new AgentMark { Number = 3, DismissedAt = CLICKED, ClosedWithNoWindow = true };

        Assert.True(AgentHub.IsHidden(mark, isAlive: true, CLICKED.AddMinutes(5), hasWindow: false));
    }

    [Fact]
    public void ACardClosedWithNoWindowComesBackWhenAWindowShowsItsThread()
    {
        var mark = new AgentMark { Number = 3, DismissedAt = CLICKED, ClosedWithNoWindow = true };

        Assert.False(AgentHub.IsHidden(mark, isAlive: true, CLICKED.AddMinutes(5), hasWindow: true));
    }

    private static AgentSnapshot Agent(AgentState state, bool isAlive, int? pid)
    {
        return new AgentSnapshot { Key = "k", Kind = AgentKind.Claude, SessionId = "s", Name = "n", Surface = "CLI", State = state, IsAlive = isAlive, Pid = pid, TranscriptPath = "t", ResumeCommand = "r" };
    }

    [Theory]
    [InlineData(AgentState.Finished, true, 42, true)]
    [InlineData(AgentState.Finished, true, null, true)]
    [InlineData(AgentState.Closed, false, null, true)]
    [InlineData(AgentState.Working, true, 42, false)]
    [InlineData(AgentState.NeedsInput, true, 42, false)]
    [InlineData(AgentState.Paused, true, 42, false)]
    public void CtrlClickClosesFinishedAndClosedCardsOnly(AgentState state, bool isAlive, int? pid, bool closes)
    {
        Assert.Equal(closes, CardClose.ClosesOnCtrlClick(Agent(state, isAlive, pid)));
    }

    [Fact]
    public void CtrlClickNeverClosesTheMasterAgent()
    {
        Assert.False(CardClose.ClosesOnCtrlClick(Agent(AgentState.Finished, true, 42) with { IsMasterAgent = true }));
    }

    [Fact]
    public void ACardAlreadyClosingIsNotOfferedTheXAgain()
    {
        Assert.False(CardClose.CanOffer(Agent(AgentState.Finished, true, 42) with { IsClosing = true }));
    }

    [Fact]
    public void CardStaysHiddenWhileTheProcessIsOnItsWay()
    {
        var mark = Closing();

        Assert.False(AgentHub.SettleClose(mark, isAlive: true, CLICKED.AddSeconds(5)));
        Assert.Equal(CLICKED, mark.ClosingAt);
    }

    [Fact]
    public void ProcessGoneClosesTheCardForGood()
    {
        var mark = Closing();
        var exited = CLICKED.AddSeconds(2);
        var lastEvent = CLICKED.AddSeconds(1);

        Assert.True(AgentHub.SettleClose(mark, isAlive: false, exited));

        Assert.Null(mark.ClosingAt);
        Assert.Equal(exited, mark.DismissedAt);
        Assert.True(AgentHub.IsHidden(mark, isAlive: false, lastEvent));
    }

    [Fact]
    public void ResumingAClosedAgentLaterShowsItsCardAgain()
    {
        var mark = Closing();
        var exited = CLICKED.AddSeconds(2);
        AgentHub.SettleClose(mark, isAlive: false, exited);

        Assert.False(AgentHub.IsHidden(mark, isAlive: true, exited.AddHours(1)));
    }

    [Fact]
    public void WindowThatRefusedToCloseGetsItsCardBack()
    {
        var mark = Closing();
        var refused = CLICKED.AddMinutes(1);

        Assert.True(AgentHub.SettleClose(mark, isAlive: true, refused));

        Assert.Null(mark.ClosingAt);
        Assert.Null(mark.DismissedAt);
        Assert.False(AgentHub.IsHidden(mark, isAlive: true, CLICKED));
    }

    [Fact]
    public void MarkNotClosingIsLeftAlone()
    {
        var mark = new AgentMark { Number = 2 };

        Assert.False(AgentHub.SettleClose(mark, isAlive: false, CLICKED));
        Assert.Null(mark.DismissedAt);
    }

    [Fact]
    public void CommandLineOfARunningProcessIsRead()
    {
        var commandLine = ProcessTable.Capture().CommandLine(Environment.ProcessId);

        Assert.False(string.IsNullOrEmpty(commandLine));
        Assert.Contains(Path.GetFileNameWithoutExtension(Environment.ProcessPath!), commandLine, StringComparison.OrdinalIgnoreCase);
    }
}
