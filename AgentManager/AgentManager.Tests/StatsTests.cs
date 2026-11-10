using AgentManager.Core;
using AgentManager.Core.Model;

namespace AgentManager.Tests;

public sealed class StatsTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 2, 16, 0, 0, TimeSpan.Zero);

    private static AgentSnapshot Agent(string key, double lane, long tokens = 0, DateTimeOffset? started = null)
    {
        return new AgentSnapshot
        {
            Key = key,
            Kind = AgentKind.Claude,
            SessionId = key,
            Name = key,
            Surface = "CLI",
            Split = new TimeSplit(0, 0, 0, 0, 0, lane),
            Tokens = new TokenTally(tokens, 0, 0, 0, 0, 0),
            StartedAt = started ?? NOW.AddHours(-3),
            TranscriptPath = "a.jsonl",
            ResumeCommand = "claude --resume " + key,
        };
    }

    [Fact]
    public void BeforeAnyResetEverySessionCountsWhole()
    {
        var book = new StatsBook();
        Stats.Observe(book, Agent("a", 100, 5));
        Stats.Observe(book, Agent("b", 50, 7));

        var period = Stats.Current(book, NOW);

        Assert.Equal(150, period.Split.Lane);
        Assert.Equal(12, period.Tokens);
    }

    [Fact]
    public void AResetClosesTheStretchIntoTheHistoryAndCountsFromZero()
    {
        var book = new StatsBook();
        Stats.Observe(book, Agent("a", 100, 5));

        Stats.Reset(book, NOW);

        Assert.Equal(100, Assert.Single(book.History).Split.Lane);
        Assert.Equal(0, Stats.Current(book, NOW).Split.Total);
        Stats.Observe(book, Agent("a", 130, 9));
        var period = Stats.Current(book, NOW.AddMinutes(1));
        Assert.Equal(30, period.Split.Lane);
        Assert.Equal(4, period.Tokens);
    }

    [Fact]
    public void AnAgentThatLeavesTheGridKeepsItsTimeInThePeriod()
    {
        var book = new StatsBook();
        Stats.Observe(book, Agent("a", 100));

        Assert.Equal(100, Stats.Current(book, NOW).Split.Lane);
    }

    [Fact]
    public void AnOlderSessionFirstSeenAfterAResetCountsOnlyFromThen()
    {
        var book = new StatsBook();
        Stats.Reset(book, NOW);

        Stats.Observe(book, Agent("old", 500, started: NOW.AddHours(-2)));
        Assert.Equal(0, Stats.Current(book, NOW).Split.Lane);
        Stats.Observe(book, Agent("old", 520, started: NOW.AddHours(-2)));
        Assert.Equal(20, Stats.Current(book, NOW).Split.Lane);
    }

    [Fact]
    public void ASessionStartedAfterTheResetCountsWhole()
    {
        var book = new StatsBook();
        Stats.Reset(book, NOW);

        Stats.Observe(book, Agent("new", 40, started: NOW.AddMinutes(1)));

        Assert.Equal(40, Stats.Current(book, NOW.AddMinutes(2)).Split.Lane);
    }

    [Fact]
    public void AFigureThatShrinksNeverCountsBelowZero()
    {
        var book = new StatsBook();
        Stats.Observe(book, Agent("a", 100));
        Stats.Reset(book, NOW);

        Stats.Observe(book, Agent("a", 80));

        Assert.Equal(0, Stats.Current(book, NOW).Split.Lane);
    }

    [Fact]
    public void AResumedOldSessionFirstSeenAfterAResetCountsOnlyFromThen()
    {
        var book = new StatsBook();
        Stats.Reset(book, NOW);
        var resumed = Agent("old", 5000, 900, NOW.AddMinutes(1)) with { FirstEventAt = NOW.AddDays(-3) };

        Stats.Observe(book, resumed);

        var period = Stats.Current(book, NOW.AddMinutes(2));
        Assert.Equal(0, period.Split.Total);
        Assert.Equal(0, period.Tokens);
    }

    [Fact]
    public void ASecondResetStraightAfterTheFirstIsIgnored()
    {
        var book = new StatsBook();
        Stats.Observe(book, Agent("a", 100));

        Assert.True(Stats.Reset(book, NOW));
        Assert.False(Stats.Reset(book, NOW.AddMilliseconds(300)));
        Assert.Single(book.History);

        Assert.True(Stats.Reset(book, NOW.AddMinutes(1)));
        Assert.Equal(2, book.History.Count);
    }

    [Fact]
    public void PruningForgetsAgentsThatAddNothingAndKeepsTheRest()
    {
        var book = new StatsBook();
        Stats.Observe(book, Agent("known", 10));
        Stats.Observe(book, Agent("gone", 10));
        Stats.Reset(book, NOW);
        Stats.Observe(book, Agent("gone-but-counting", 30, started: NOW.AddMinutes(1)));

        Stats.Prune(book, key => key == "known");

        Assert.Equal(["gone-but-counting", "known"], book.Latest.Keys.Order());
        Assert.False(book.Baseline.ContainsKey("gone"));
        Assert.Equal(30, Stats.Current(book, NOW).Split.Total);
    }

    [Fact]
    public void AnAgentForgottenByPruningCountsOnlyFromThenIfItTurnsUpAgain()
    {
        var book = new StatsBook();
        Stats.Observe(book, Agent("gone", 10));
        Stats.Reset(book, NOW);
        Stats.Prune(book, _ => false);

        Stats.Observe(book, Agent("gone", 10));

        Assert.Equal(0, Stats.Current(book, NOW).Split.Total);
    }

    [Fact]
    public void TheStoreForgetsStatsOfAgentsItNoLongerKnows()
    {
        var store = AgentManager.Core.State.ManagerStore.InMemory();
        store.Change(data =>
        {
            Stats.Observe(data.Stats, Agent("gone", 10));
            Stats.Reset(data.Stats, NOW);
        });

        store.Prune(NOW);

        Assert.Empty(store.Read(data => data.Stats.Latest));
    }

    [Fact]
    public void TheHistorySurvivesARoundTripThroughTheStateFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "am-stats-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{}");
            var store = AgentManager.Core.State.ManagerStore.Load(path);
            store.Change(data =>
            {
                Stats.Observe(data.Stats, Agent("a", 100, 5));
                Stats.Reset(data.Stats, NOW);
                Stats.Observe(data.Stats, Agent("a", 130, 9));
                Stats.Reset(data.Stats, NOW.AddHours(1));
            });
            store.SaveIfDirty();

            var history = AgentManager.Core.State.ManagerStore.Load(path).Read(data => data.Stats.History);

            Assert.Equal(2, history.Count);
            Assert.Equal(100, history[0].Split.Lane);
            Assert.Equal(5, history[0].Tokens);
            Assert.Equal(NOW, history[1].From);
            Assert.Equal(30, history[1].Split.Lane);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
