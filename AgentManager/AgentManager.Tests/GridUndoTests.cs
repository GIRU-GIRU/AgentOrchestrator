using AgentManager.Core;
using AgentManager.Core.Model;
using AgentManager.Core.State;

namespace AgentManager.Tests;

public sealed class GridUndoTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private static AgentSnapshot Agent(string key, int number = 1, bool pinned = false, AgentKind kind = AgentKind.Claude, bool alive = true, bool isMasterAgent = false)
    {
        return new AgentSnapshot
        {
            Key = key,
            Kind = kind,
            SessionId = key,
            Name = "agent " + key,
            Surface = "CLI",
            IsAlive = alive,
            Number = number,
            Pinned = pinned,
            IsMasterAgent = isMasterAgent,
            TranscriptPath = "a.jsonl",
            ResumeCommand = "claude --resume " + key,
        };
    }

    private static RemovedCard Card(string key, int number = 1, string? previous = null, int index = 0, bool pinned = false, bool isMasterAgent = false)
    {
        return new RemovedCard(key, "agent " + key, number, pinned, previous, index, isMasterAgent);
    }

    [Fact]
    public void EachPressGoesFurtherBackThenThereIsNothingLeft()
    {
        var undo = new GridUndo();
        undo.Remember(Card("a"));
        undo.Remember(Card("b"));
        undo.Remember(Card("c"));

        Assert.Equal("c", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
        Assert.Equal("b", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
        Assert.Equal("a", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
        Assert.Null(undo.TakeLast(_ => true));
    }

    [Fact]
    public void ACardRemovedTwiceIsOneEntryAtItsNewestPlace()
    {
        var undo = new GridUndo();
        undo.Remember(Card("a"));
        undo.Remember(Card("b"));
        undo.Remember(Card("a"));

        Assert.Equal("a", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
        Assert.Equal("b", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
        Assert.Null(undo.TakeLast(_ => true));
    }

    [Fact]
    public void AMasterAgentWhoHasGoneIsPassedOverAndKept()
    {
        var undo = new GridUndo();
        undo.Remember(Card("a"));
        undo.Remember(Card("master", isMasterAgent: true));

        bool MayComeBack(RemovedCard card) => !card.IsMasterAgent;

        Assert.Equal("a", (undo.TakeLast(MayComeBack) as RemovedCard)?.Key);
        Assert.Null(undo.TakeLast(MayComeBack));
        Assert.Equal("master", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
    }

    [Fact]
    public void ACardThatCameBackByItselfHasNothingLeftToUndo()
    {
        var undo = new GridUndo();
        undo.Remember(Card("a"));
        undo.Remember(Card("acted"));

        undo.Forget(["acted"]);

        Assert.Equal("a", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
    }

    [Fact]
    public void BringingACardBackClearsWhatHidItAndRestoresItsNumberAndPin()
    {
        var mark = new AgentMark { DismissedAt = NOW, ClosingAt = NOW, Number = 0, Pinned = false, ReadAt = NOW };

        GridUndo.BringBack(Card("a", number: 7, pinned: true), mark, []);

        Assert.Null(mark.DismissedAt);
        Assert.Null(mark.ClosingAt);
        Assert.Equal(7, mark.Number);
        Assert.True(mark.Pinned);
        Assert.Equal(NOW, mark.ReadAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACardBroughtBackShowsAndAClosedAgentReadsAsClosedNotRunning(bool isAlive)
    {
        var mark = new AgentMark { DismissedAt = NOW, Number = 3 };
        Assert.True(AgentHub.IsHidden(mark, isAlive, NOW.AddMinutes(-1)));

        GridUndo.BringBack(Card("a", number: 3), mark, []);

        Assert.False(AgentHub.IsHidden(mark, isAlive, NOW.AddMinutes(-1)));
        var state = StateClassifier.Classify(Agent("a", 3, alive: isAlive), null).State;
        Assert.Equal(isAlive, state != AgentState.Closed);
    }

    [Fact]
    public void AnAgentClosedWithCloseAgentComesBackWithNoCloseStillPending()
    {
        var mark = new AgentMark { ClosingAt = NOW };

        GridUndo.BringBack(Card("a"), mark, []);

        Assert.False(AgentHub.SettleClose(mark, false, NOW.AddMinutes(1)));
        Assert.Null(mark.DismissedAt);
    }

    [Fact]
    public void ItGoesBackRightAfterTheCardBeforeIt()
    {
        var order = new List<string> { "a", "c", "b" };

        GridUndo.BringBack(Card("b", previous: "a", index: 1), new AgentMark(), order);

        Assert.Equal(["a", "b", "c"], order);
    }

    [Fact]
    public void AFirstCardGoesBackToTheFront()
    {
        var order = new List<string> { "b", "c", "a" };

        GridUndo.BringBack(Card("a", previous: null, index: 0), new AgentMark(), order);

        Assert.Equal(["a", "b", "c"], order);
    }

    [Fact]
    public void WithTheCardBeforeItGoneItKeepsItsOldIndex()
    {
        var order = new List<string> { "a", "b", "c" };

        GridUndo.BringBack(Card("x", previous: "gone", index: 1), new AgentMark(), order);

        Assert.Equal(["a", "x", "b", "c"], order);
    }

    [Fact]
    public void AnOldIndexPastTheEndGoesToTheEnd()
    {
        var order = new List<string> { "a" };

        GridUndo.BringBack(Card("x", previous: "gone", index: 9), new AgentMark(), order);

        Assert.Equal(["a", "x"], order);
    }

    [Fact]
    public void ACardsPlaceIsTheCardBeforeItInItsOwnSection()
    {
        var removed = Agent("c");
        var grid = new[] { Agent("pinned", pinned: true), Agent("a"), Agent("codex", kind: AgentKind.Codex), removed, Agent("d") };

        var card = GridUndo.Describe(removed, grid, 3);

        Assert.Equal("a", card.PreviousKey);
        Assert.Equal(3, card.OrderIndex);
    }

    [Fact]
    public void TheFirstCardOfItsSectionHasNoneBeforeIt()
    {
        var removed = Agent("a");
        var grid = new[] { Agent("pinned", pinned: true), removed, Agent("b") };

        Assert.Null(GridUndo.Describe(removed, grid, 1).PreviousKey);
    }

    [Fact]
    public void AnUnsavedPlaceCountsAsTheFront()
    {
        Assert.Equal(0, GridUndo.Describe(Agent("a"), [], -1).OrderIndex);
    }

    [Fact]
    public void ARemovedCardsNumberStaysReservedUntilItIsBack()
    {
        var undo = new GridUndo();
        undo.Remember(Card("a", number: 4));
        undo.Remember(Card("handed-on", number: 0));

        Assert.Equal([4], undo.ReservedNumbers());

        undo.TakeLast(card => card.Key == "a");

        Assert.Empty(undo.ReservedNumbers());
    }

    [Fact]
    public void TheMasterAgentIsMarkedAsSuch()
    {
        Assert.True(GridUndo.Describe(Agent("m", isMasterAgent: true), [], 0).IsMasterAgent);
    }
}

public sealed class CardCloseTests
{
    private static AgentSnapshot Agent(AgentState state, bool alive = true, int? pid = 42)
    {
        return new AgentSnapshot
        {
            Key = "claude:a",
            Kind = AgentKind.Claude,
            SessionId = "a",
            Name = "a",
            Surface = "CLI",
            IsAlive = alive,
            Pid = pid,
            State = state,
            TranscriptPath = "a.jsonl",
            ResumeCommand = "claude --resume a",
        };
    }

    [Fact]
    public void OnlyAFinishedCardOffersTheX()
    {
        foreach (var state in Enum.GetValues<AgentState>())
        {
            Assert.Equal(state == AgentState.Finished, CardClose.CanOffer(Agent(state)));
        }
    }

    [Fact]
    public void ACardWithNoKnownWindowCanStillBeTakenOffTheGrid()
    {
        Assert.True(CardClose.CanOffer(Agent(AgentState.Finished, pid: null)));
    }

    [Fact]
    public void AClosedAgentHasNothingToClose()
    {
        Assert.False(CardClose.CanOffer(Agent(AgentState.Finished, alive: false)));
    }
}

/// <summary>A deleted category box comes back with Ctrl+Z, in the same history as the cards.</summary>
public sealed class CategoryUndoTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 2, 15, 0, 0, TimeSpan.Zero);

    private static RemovedCard Card(string key, int number = 1, bool isMasterAgent = false)
    {
        return new RemovedCard(key, "agent " + key, number, false, null, 0, isMasterAgent);
    }

    private static CanvasCategory Box(string id, string name = "box")
    {
        return new CanvasCategory { Id = id, Name = name, Colour = "#7c6cf0", X = 10, Y = 20, Width = 900, Height = 520, Tokens = 1234 };
    }

    [Fact]
    public void ADeletedCategoryComesBackWholeAtItsPlace()
    {
        var a = Box("a");
        var b = Box("b", "Default");
        var c = Box("c");
        var categories = new List<CanvasCategory> { a, c };

        GridUndo.BringBack(new DeletedCategory(b, 1), categories);

        Assert.Equal(["a", "b", "c"], categories.Select(category => category.Id));
        Assert.Same(b, categories[1]);
        Assert.Equal(1234, categories[1].Tokens);
    }

    [Fact]
    public void ACategoryWhosePlaceIsGoneGoesToTheEndAndIsNeverDoubled()
    {
        var categories = new List<CanvasCategory> { Box("a") };

        GridUndo.BringBack(new DeletedCategory(Box("b"), 5), categories);
        GridUndo.BringBack(new DeletedCategory(Box("b"), 0), categories);

        Assert.Equal(["b", "a"], categories.Select(category => category.Id));
    }

    [Fact]
    public void CardsAndCategoriesComeBackNewestFirst()
    {
        var undo = new GridUndo();
        undo.Remember(Card("a"));
        undo.Remember(new DeletedCategory(Box("box"), 0));
        undo.Remember(Card("b"));

        Assert.Equal("b", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
        Assert.Equal("box", (undo.TakeLast(_ => true) as DeletedCategory)?.Category.Id);
        Assert.Equal("a", (undo.TakeLast(_ => true) as RemovedCard)?.Key);
        Assert.Null(undo.TakeLast(_ => true));
    }

    [Fact]
    public void ACardThatMayNotComeBackDoesNotHideADeletedCategory()
    {
        var undo = new GridUndo();
        undo.Remember(new DeletedCategory(Box("box"), 0));
        undo.Remember(Card("master", isMasterAgent: true));

        Assert.Equal("box", (undo.TakeLast(card => !card.IsMasterAgent) as DeletedCategory)?.Category.Id);
    }

    [Fact]
    public void ForgettingCardsKeepsADeletedCategoryAndReservesNoNumberForIt()
    {
        var undo = new GridUndo();
        undo.Remember(new DeletedCategory(Box("box"), 0));
        undo.Remember(Card("a", number: 7));

        undo.Forget(["a"]);

        Assert.Empty(undo.ReservedNumbers());
        Assert.IsType<DeletedCategory>(undo.TakeLast(_ => true));
    }

    [Fact]
    public void DeletingACategoryThroughTheHubThenCtrlZPutsItBack()
    {
        var path = Path.Combine(Path.GetTempPath(), "am-undo-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = ManagerStore.Load(path);
            using var hub = new AgentHub(store, path + ".claude", path + ".codex");
            var first = hub.AddCategory(0, 0);
            var second = hub.AddCategory(2000, 0);
            hub.RenameCategory(second.Id, "Default");

            hub.DeleteCategory(second.Id);
            Assert.DoesNotContain(hub.Board().Categories, category => category.Id == second.Id);

            var step = hub.BringBackLastRemoved(NOW);

            Assert.Equal(second.Id, (step as DeletedCategory)?.Category.Id);
            Assert.Equal([first.Id, second.Id], hub.Board().Categories.Select(category => category.Id));
            Assert.Equal("Default", hub.Board().Categories[1].Name);
            Assert.Null(hub.BringBackLastRemoved(NOW));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
