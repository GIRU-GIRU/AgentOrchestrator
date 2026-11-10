using AgentManager.Core.Model;
using AgentManager.Core.Transcripts;

namespace AgentManager.Tests;

public sealed class BridgeLinkTests
{
    private static readonly DateTimeOffset NOW = new(2026, 11, 3, 17, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan QUIET = TimeSpan.FromMinutes(30);

    private static AgentSnapshot Card(string key, AgentState state = AgentState.Finished, bool alive = true, params BridgeInfo[] bridges)
    {
        return new AgentSnapshot
        {
            Key = key,
            Kind = AgentKind.Claude,
            SessionId = key,
            Name = key,
            Surface = "CLI",
            IsAlive = alive,
            State = state,
            Bridges = bridges,
            TranscriptPath = key + ".jsonl",
            ResumeCommand = "claude --resume " + key,
        };
    }

    private static BridgeInfo Row(string channel, TimeSpan? lastAgo = null, string? from = null, params string[] peers)
    {
        var peerList = peers.Select(p => new BridgePeer(p, true, p)).ToArray();
        return new BridgeInfo(channel, peerList, 3, NOW - (lastAgo ?? QUIET), from, false, from);
    }

    /// <summary>The real shape: Astra's card is named after the channel and its partner's row calls it "Astra", so the names never match.</summary>
    [Fact]
    public void CardsPairByChannelNotByName()
    {
        var claude = Card("claude:search", bridges: new BridgeInfo("search-index", [new BridgePeer("Astra", true)], 37, NOW - QUIET, "Astra", false)) with { Name = "Search index rebuild" };
        var astra = Card("codex:thread", bridges: new BridgeInfo("search-index", [new BridgePeer("Search index rebuild", true)], 37, NOW - QUIET, "Astra", false)) with { Name = "Astra: search-index" };

        var link = Assert.Single(BridgeRules.Links([claude, astra], NOW));

        Assert.Equal("search-index", link.Channel);
        Assert.Equal(["claude:search", "codex:thread"], new[] { link.From, link.To }.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void APeerWithNoRowOfItsOwnStillGetsTheTrace()
    {
        var claude = Card("claude:a", bridges: Row("test-suite", peers: "codex:b"));
        var codex = Card("codex:b");

        var link = Assert.Single(BridgeRules.Links([claude, codex], NOW));

        Assert.Equal("test-suite", link.Channel);
    }

    [Fact]
    public void APartnerNotOnTheGridMakesNoTrace()
    {
        var claude = Card("claude:a", bridges: Row("test-suite", peers: "codex:b"));

        Assert.Empty(BridgeRules.Links([claude], NOW));
    }

    [Fact]
    public void AClosedCardMakesNoTrace()
    {
        var claude = Card("claude:a", bridges: Row("test-suite", peers: "codex:b"));
        var closed = Card("codex:b", AgentState.Closed, alive: false, Row("test-suite", peers: "claude:a"));
        var gonePeer = Card("claude:a", bridges: new BridgeInfo("test-suite", [new BridgePeer("b", false, "codex:b")], 3, NOW, null, false));

        Assert.Empty(BridgeRules.Links([claude, closed], NOW));
        Assert.Empty(BridgeRules.Links([gonePeer, Card("codex:b")], NOW));
    }

    [Fact]
    public void ACardWithNoBridgesMakesNoTrace()
    {
        Assert.Empty(BridgeRules.Links([Card("claude:a"), Card("claude:b")], NOW));
    }

    [Fact]
    public void OneAgentOnTwoChannelsHasTwoTraces()
    {
        var hub = Card("claude:a", bridges: [Row("api-docs", peers: "codex:b"), Row("test-suite", peers: "codex:c")]);
        var links = BridgeRules.Links([hub, Card("codex:b"), Card("codex:c")], NOW);

        Assert.Equal(["api-docs", "test-suite"], links.Select(l => l.Channel).ToArray());
        Assert.All(links, link => Assert.Contains("claude:a", new[] { link.From, link.To }));
    }

    [Fact]
    public void TwoPairsOnOneChannelNameTheirOwnEnds()
    {
        var a = Card("claude:a", bridges: Row("test-suite", peers: "codex:b"));
        var b = Card("codex:b", bridges: Row("test-suite", peers: "claude:a"));
        var c = Card("claude:c", bridges: Row("api-docs", peers: "codex:d"));
        var d = Card("codex:d", bridges: Row("api-docs", peers: "claude:c"));

        var links = BridgeRules.Links([a, c, b, d], NOW);

        Assert.Equal(2, links.Count);
        Assert.Equal(["claude:c", "codex:d"], new[] { links[0].From, links[0].To });
        Assert.Equal(["claude:a", "codex:b"], new[] { links[1].From, links[1].To });
    }

    [Fact]
    public void ThreeCardsOnAChannelAreAChainInGridOrder()
    {
        var first = Card("claude:a", bridges: Row("test-suite", peers: ["claude:b", "codex:c"]));
        var links = BridgeRules.Links([first, Card("claude:b"), Card("codex:c")], NOW);

        Assert.Equal(2, links.Count);
        Assert.Equal(("claude:a", "claude:b"), (links[0].From, links[0].To));
        Assert.Equal(("claude:b", "codex:c"), (links[1].From, links[1].To));
    }

    [Fact]
    public void ThePulseRunsFromWhoeverSpokeLast()
    {
        var claude = Card("claude:a", bridges: Row("test-suite", from: "codex:b", peers: "codex:b"));
        var codex = Card("codex:b", bridges: Row("test-suite", from: "codex:b", peers: "claude:a"));

        var link = Assert.Single(BridgeRules.Links([claude, codex], NOW));

        Assert.Equal("codex:b", link.From);
        Assert.Equal("claude:a", link.To);
    }

    [Fact]
    public void AMessageInTheLastCoupleOfMinutesIsActiveAndThenItGoesQuiet()
    {
        var justMoved = Card("claude:a", bridges: Row("test-suite", BridgeRules.MOVING_AFTER - TimeSpan.FromSeconds(1), peers: "codex:b"));
        var wentQuiet = Card("claude:a", bridges: Row("test-suite", BridgeRules.MOVING_AFTER, peers: "codex:b"));

        Assert.True(BridgeRules.Links([justMoved, Card("codex:b")], NOW).Single().IsActive);
        Assert.False(BridgeRules.Links([wentQuiet, Card("codex:b")], NOW).Single().IsActive);
    }

    [Theory]
    [InlineData(AgentState.Working, true)]
    [InlineData(AgentState.UsingEditor, true)]
    [InlineData(AgentState.Asked, true)]
    [InlineData(AgentState.LaneBlocked, false)]
    [InlineData(AgentState.NeedsInput, false)]
    [InlineData(AgentState.Finished, false)]
    [InlineData(AgentState.Paused, false)]
    public void EitherSideWorkingKeepsItActive(AgentState partnerState, bool isActive)
    {
        var claude = Card("claude:a", bridges: Row("test-suite", peers: "codex:b"));
        var partner = Card("codex:b", partnerState) with { IsWorking = true };

        Assert.Equal(isActive, BridgeRules.Links([claude, partner], NOW).Single().IsActive);
    }

    [Fact]
    public void TwoAgentsOnlyWaitingOnEachOtherAreAQuietBridge()
    {
        var foreground = Card("claude:a", AgentState.Working, true, Row("test-suite", peers: "codex:b")) with { IsWorking = true, IsWaitingOnBridge = true, ListeningOn = "test-suite" };
        var background = Card("codex:b", AgentState.Working, true, Row("test-suite", peers: "claude:a")) with { RunningCommands = 1, ListeningOn = "test-suite" };

        Assert.False(BridgeRules.Links([foreground, background], NOW).Single().IsActive);
    }

    [Fact]
    public void AnAgentDoingOtherWorkBesideItsWaitIsActive()
    {
        var waiting = Card("claude:a", AgentState.Working, true, Row("test-suite", peers: "codex:b")) with { IsWorking = true, ListeningOn = "test-suite" };
        var withABuild = Card("claude:a", AgentState.Working, true, Row("test-suite", peers: "codex:b")) with { RunningCommands = 2, ListeningOn = "test-suite" };

        Assert.True(BridgeRules.Links([waiting, Card("codex:b")], NOW).Single().IsActive);
        Assert.True(BridgeRules.Links([withABuild, Card("codex:b")], NOW).Single().IsActive);
    }

    /// <summary>A real case: holding PIE, its own tool call a wait on the inbox, and a subagent doing the actual work.</summary>
    [Fact]
    public void ASubagentWorkingBehindAWaitIsActive()
    {
        var subagent = new SubagentInfo("s", "general-purpose", "film the take", true, NOW, 0);
        var waiting = Card("claude:a", AgentState.UsingEditor, true, Row("test-suite", peers: "codex:b")) with { IsWorking = true, IsWaitingOnBridge = true, ListeningOn = BridgeRules.ANY_CHANNEL, Subagents = [subagent] };

        Assert.True(BridgeRules.Links([waiting, Card("codex:b")], NOW).Single().IsActive);
    }

    [Fact]
    public void TheForegroundWaitIsTheCurrentToolCallOnly()
    {
        var tracker = new SessionTracker();
        var at = NOW;
        tracker.ToolStart(at, "wait", "Bash", "orch.cmd inbox test-suite --wait 540", Activity.Tools);
        tracker.Listen("wait", "test-suite");

        Assert.True(tracker.IsWaitingOnBridge);

        tracker.ToolStart(at.AddSeconds(1), "read", "Read", "a.cs", Activity.Tools);

        Assert.False(tracker.IsWaitingOnBridge);

        tracker.ToolEnd(at.AddSeconds(2), "read", false);
        tracker.ToolEnd(at.AddSeconds(3), "wait", false);

        Assert.False(tracker.IsWaitingOnBridge);
    }
}
