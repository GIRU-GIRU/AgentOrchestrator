using AgentManager.Core;
using AgentManager.Core.Model;
using AgentManager.Core.State;

namespace AgentManager.Tests;

public sealed class CanvasLayoutTests
{
    private static CanvasPoint At(double x, double y)
    {
        return new CanvasPoint { X = x, Y = y };
    }

    [Fact]
    public void AnEmptyCanvasPutsTheFirstCardAtTheStart()
    {
        Assert.Equal((10.0, 20.0), CanvasLayout.FreeSpot([], 10, 20, 4));
    }

    [Fact]
    public void ACardTakesTheNextColumnWhenTheFirstIsFull()
    {
        var (x, y) = CanvasLayout.FreeSpot([At(0, 0)], 0, 0, 4);

        Assert.True(x >= CanvasLayout.CARD_WIDTH);
        Assert.Equal(0, y);
    }

    [Fact]
    public void AFullRowMovesToTheNext()
    {
        var (x, y) = CanvasLayout.FreeSpot([At(0, 0)], 0, 0, 1);

        Assert.Equal(0, x);
        Assert.True(y >= CanvasLayout.CARD_HEIGHT);
    }

    [Fact]
    public void ACardDraggedAwayFreesItsSpot()
    {
        Assert.Equal((0.0, 0.0), CanvasLayout.FreeSpot([At(5000, 5000)], 0, 0, 4));
    }

    [Fact]
    public void ANewCardStartsInsideTheView()
    {
        var canvas = new CanvasState { HasView = true, PanX = -2000, PanY = -1000, Zoom = 0.5, ViewWidth = 1600, ViewHeight = 900 };

        var (x, y) = CanvasLayout.SpotForNewCard(canvas, hasDock: true);

        Assert.InRange(x, 4000, 4000 + 1600 / 0.5);
        Assert.InRange(y, 2000, 2000 + 900 / 0.5);
    }

    [Fact]
    public void BeforeAnyViewCardsLayOutFromTheOrigin()
    {
        Assert.Equal((0.0, 0.0), CanvasLayout.SpotForNewCard(new CanvasState(), hasDock: true));
    }

    [Fact]
    public void EachBoxTakesAnUnusedColourUntilTheyRunOut()
    {
        var categories = new List<CanvasCategory>();
        foreach (var _ in CanvasLayout.COLOURS)
        {
            categories.Add(CanvasLayout.NewCategory(categories, 0, 0));
        }

        Assert.Equal(CanvasLayout.COLOURS.Count, categories.Select(c => c.Colour).Distinct().Count());
        Assert.Equal(CanvasLayout.COLOURS[0], CanvasLayout.NextColour(categories));
    }

    [Theory]
    [InlineData("  my   box \n", "old", "my box")]
    [InlineData("", "old", "old")]
    [InlineData("   ", "old", "old")]
    [InlineData(null, "old", "old")]
    public void ACategoryNameIsOneTrimmedLineAndEmptyKeepsTheOldOne(string? typed, string current, string expected)
    {
        Assert.Equal(expected, CanvasLayout.CleanCategoryName(typed, current));
    }

    private static readonly Dictionary<string, CanvasPoint> PLUS = new()
    {
        ["middle"] = At(1000, 1000),
        ["right"] = At(1500, 1000),
        ["left"] = At(500, 1000),
        ["up"] = At(1000, 500),
        ["down"] = At(1000, 1500),
        ["far-right-off-axis"] = At(1300, 1900),
    };

    [Theory]
    [InlineData(CanvasDirection.Right, "right")]
    [InlineData(CanvasDirection.Left, "left")]
    [InlineData(CanvasDirection.Up, "up")]
    [InlineData(CanvasDirection.Down, "down")]
    public void AnArrowGoesToTheCardStraightAhead(CanvasDirection direction, string expected)
    {
        Assert.Equal(expected, CanvasLayout.Nearest("middle", direction, PLUS));
    }

    [Fact]
    public void AnArrowAtTheEdgeStaysPut()
    {
        Assert.Null(CanvasLayout.Nearest("up", CanvasDirection.Up, PLUS));
    }

    [Fact]
    public void ACardMissingFromTheCanvasHasNoNeighbours()
    {
        Assert.Null(CanvasLayout.Nearest("gone", CanvasDirection.Right, PLUS));
    }

    [Fact]
    public void ArrowsStartFromTheTopLeftCard()
    {
        Assert.Equal("up", CanvasLayout.First(new Dictionary<string, CanvasPoint> { ["down"] = At(100, 900), ["up"] = At(100, 10) }));
    }

    [Fact]
    public void ACardWhoseWindowMovedOnKeepsItsSpotThroughTheStore()
    {
        var canvas = new CanvasState();
        canvas.Cards["old"] = At(70, 80);

        AgentHub.CarryCanvasPosition(canvas, "new", "old");

        Assert.False(canvas.Cards.ContainsKey("old"));
        Assert.Equal(70, canvas.Cards["new"].X);
        Assert.Equal(80, canvas.Cards["new"].Y);
    }

    private static readonly DateTimeOffset NOW = new(2026, 11, 9, 21, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    public void ACardNobodyPlacedStartsRightOfTheDockAtAnyZoom(double zoom)
    {
        var canvas = new CanvasState { HasView = true, Zoom = zoom, ViewWidth = 1600, ViewHeight = 900 };

        var (x, _) = CanvasLayout.SpotForNewCard(canvas, hasDock: true);

        Assert.True(x * zoom >= CanvasLayout.DOCK_WIDTH);
    }

    [Fact]
    public void WithTheMasterAgentClosedTheCornerIsFree()
    {
        var canvas = new CanvasState { HasView = true, Zoom = 1, ViewWidth = 1600, ViewHeight = 900 };

        Assert.Equal((40.0, 40.0), CanvasLayout.SpotForNewCard(canvas, hasDock: false));
    }

    [Fact]
    public void WhenOnlyTheDockFitsANewCardStillFindsASpot()
    {
        var canvas = new CanvasState { HasView = true, Zoom = 1, ViewWidth = 300, ViewHeight = 900 };

        var (x, y) = CanvasLayout.SpotForNewCard(canvas, hasDock: true);

        Assert.True(x >= CanvasLayout.DOCK_WIDTH);
        Assert.Equal(40.0, y);
    }

    [Fact]
    public void TheMasterAgentLeavesTheCanvasSoNoBoxCountsHim()
    {
        var canvas = Boxed("master", "other");
        var master = Agent("master", AgentKind.Claude, AgentState.Working, 10) with { IsMasterAgent = true, Pinned = true };
        var other = Agent("other", AgentKind.Claude, AgentState.Working, 10);

        Assert.True(CanvasLayout.LiftDocked(canvas, [master, other]));

        Assert.False(canvas.Cards.ContainsKey("master"));
        Assert.Equal(1, CanvasLayout.StatsOf(canvas.Categories[0], canvas.Cards, [master, other]).Agents);
        Assert.False(CanvasLayout.LiftDocked(canvas, [master, other]));
    }

    [Fact]
    public void UnpinnedTheMasterAgentIsACardOnTheCanvas()
    {
        var canvas = Boxed("master");
        var master = Agent("master", AgentKind.Codex, AgentState.Working, 10) with { IsMasterAgent = true, Pinned = false };

        Assert.False(CanvasLayout.IsDocked(master));
        Assert.False(CanvasLayout.LiftDocked(canvas, [master]));
        Assert.Equal(1, CanvasLayout.StatsOf(canvas.Categories[0], canvas.Cards, [master]).Agents);
        Assert.False(CanvasLayout.IsDocked(Agent("pinned", AgentKind.Claude, AgentState.Working, 10) with { Pinned = true }));
    }

    [Fact]
    public void DockingHimKeepsHisSpotForWhenHeIsUndocked()
    {
        var canvas = Boxed("master");
        var at = canvas.Cards["master"];
        var docked = Agent("master", AgentKind.Claude, AgentState.Working, 10) with { IsMasterAgent = true, Pinned = true };

        Assert.True(CanvasLayout.LiftDocked(canvas, [docked]));

        var back = CanvasLayout.TakeParked(canvas, "master");
        Assert.NotNull(back);
        Assert.Equal(at.X, back.X);
        Assert.Equal(at.Y, back.Y);
        Assert.Null(CanvasLayout.TakeParked(canvas, "master"));
    }

    [Fact]
    public void TheDockIsAtTheCornerOfTheViewForTheArrowKeys()
    {
        var at = CanvasLayout.DockedAt(new CanvasBoard(new Dictionary<string, CanvasPoint>(), [], true, -200, -100, 0.5));

        Assert.Equal(400, at.X);
        Assert.Equal(200, at.Y);
    }

    private static AgentSnapshot Agent(string key, AgentKind kind, AgentState state, long tokens, bool isAlive = true)
    {
        return new AgentSnapshot { Key = key, Kind = kind, SessionId = key, Name = key, Surface = "CLI", State = state, IsAlive = isAlive, Tokens = TokenTally.Empty with { Input = tokens }, TranscriptPath = "t", ResumeCommand = "r" };
    }

    private static CanvasState Boxed(params string[] inside)
    {
        var canvas = new CanvasState();
        canvas.Categories.Add(new CanvasCategory { Id = "c", Name = "box", X = 0, Y = 0, Width = 2000, Height = 2000 });
        foreach (var key in inside)
        {
            canvas.Cards[key] = At(100, 100);
        }

        canvas.Cards["outside"] = At(9000, 9000);
        return canvas;
    }

    [Fact]
    public void TheStripCountsTheRunningAgentsInTheBoxOnly()
    {
        var canvas = Boxed("working", "editor", "asked", "input", "finished", "closed");
        var agents = new[]
        {
            Agent("working", AgentKind.Claude, AgentState.Working, 1),
            Agent("editor", AgentKind.Codex, AgentState.UsingEditor, 1),
            Agent("asked", AgentKind.Claude, AgentState.Asked, 1),
            Agent("input", AgentKind.Codex, AgentState.NeedsInput, 1),
            Agent("finished", AgentKind.Claude, AgentState.Finished, 1),
            Agent("closed", AgentKind.Claude, AgentState.Closed, 1, isAlive: false),
            Agent("outside", AgentKind.Codex, AgentState.Working, 1),
        };

        Assert.Equal(new CategoryStats(5, 2, 2, 3, 2), CanvasLayout.StatsOf(canvas.Categories[0], canvas.Cards, agents));
    }

    [Fact]
    public void ACardDroppedInCountsOnlyWhatItUsesFromThen()
    {
        var canvas = Boxed("a");

        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 1000)]);
        Assert.Equal(0, canvas.Categories[0].Tokens);

        Assert.True(CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 1250)]));
        Assert.Equal(250, canvas.Categories[0].Tokens);
    }

    [Fact]
    public void ACardTakenOutStopsCountingAndStartsFreshIfItComesBack()
    {
        var canvas = Boxed("a");
        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 100)]);
        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 150)]);

        canvas.Cards["a"] = At(9000, 9000);
        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 900)]);
        canvas.Cards["a"] = At(100, 100);
        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 1000)]);

        Assert.Equal(50, canvas.Categories[0].Tokens);
    }

    [Fact]
    public void AResetStoresTheStretchAndCardsKeepCounting()
    {
        var canvas = Boxed("a");
        var box = canvas.Categories[0];
        box.TokensSince = NOW.AddHours(-1);
        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 100)]);
        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 300)]);

        CanvasLayout.ResetTokens(box, NOW);
        CanvasLayout.AccrueTokens(canvas, [Agent("a", AgentKind.Claude, AgentState.Working, 350)]);

        var stretch = Assert.Single(box.TokenHistory);
        Assert.Equal(200, stretch.Tokens);
        Assert.Equal(NOW.AddHours(-1), stretch.From);
        Assert.Equal(NOW, stretch.To);
        Assert.Equal(NOW, box.TokensSince);
        Assert.Equal(50, box.Tokens);
    }

    [Fact]
    public void CanvasPositionsOfForgottenCardsAreDropped()
    {
        var canvas = new CanvasState();
        canvas.Cards["kept"] = At(1, 1);
        canvas.Cards["forgotten"] = At(2, 2);

        Assert.Equal(1, ManagerStore.PruneCanvas(canvas, key => key == "kept"));

        Assert.Equal(["kept"], canvas.Cards.Keys);
    }
}
