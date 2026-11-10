using AgentManager.Core.Model;
using AgentManager.Core.State;

namespace AgentManager.Core;

/// <summary>The strip on a category box. Counts cover running agents, so a closed card sitting in the box is left out.</summary>
public sealed record CategoryStats(int Agents, int Active, int Waiting, int Claude, int Codex);

public enum CanvasDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>The canvas's rules, pure so each one has a test. The page owns panning, zooming and dragging; this decides where things land.</summary>
public static class CanvasLayout
{
    /// <summary>A card's width in world units, matched by <c>.canvas-world &gt; .card</c> in app.css.</summary>
    public const double CARD_WIDTH = 420;

    /// <summary>Cards are as tall as their content, so finding room uses a figure a little over the usual height.</summary>
    public const double CARD_HEIGHT = 400;

    /// <summary>How much of the view's left edge the docked card and its margins take, matched by <c>.canvas-dock</c> in app.css. In screen pixels: the dock does not zoom.</summary>
    public const double DOCK_WIDTH = 448;

    private const double CARD_GAP = 28;

    private const double NEW_BOX_WIDTH = 900;

    private const double NEW_BOX_HEIGHT = 520;

    private const int DEFAULT_COLUMNS = 4;

    /// <summary>How far in from the visible corner a card that nobody placed starts looking for room.</summary>
    private const double VIEW_MARGIN = 40;

    /// <summary>Off-axis distance counts this much more, so an arrow prefers the card straight ahead over a nearer one off to the side.</summary>
    private const double OFF_AXIS_WEIGHT = 2;

    private const string DEFAULT_CATEGORY_NAME = "Category";

    /// <summary>A card's middle, for whether it is in a box. Its real height changes with its content, and this is about half the usual one.</summary>
    private const double CARD_MIDDLE_Y = 170;

    /// <summary>A long-lived box would otherwise grow this list for ever in the state file.</summary>
    private const int MAX_TOKEN_STRETCHES = 40;

    /// <summary>
    /// The user's own category colours, chosen by them for each box, so they sit outside the card states' scheme.
    /// Kept muted because the box lies behind the cards and a card's own state colour has to read over it.
    /// </summary>
    public static readonly IReadOnlyList<string> COLOURS = ["#7c6cf0", "#2fa8a0", "#c7649b", "#b98a3a", "#5f8fd0", "#8fa14b", "#c06a56", "#7f8a9c"];

    /// <summary>The first free card-sized spot reading left to right from (<paramref name="left"/>, <paramref name="top"/>). Boxes are not in the way: a card belongs on them.</summary>
    public static (double X, double Y) FreeSpot(IEnumerable<CanvasPoint> taken, double left, double top, int columns)
    {
        var cards = taken.ToList();
        var width = Math.Max(1, columns);
        for (var row = 0; ; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var x = left + column * (CARD_WIDTH + CARD_GAP);
                var y = top + row * (CARD_HEIGHT + CARD_GAP);
                if (cards.All(card => !Overlaps(card, x, y)))
                {
                    return (x, y);
                }
            }
        }
    }

    /// <summary>Where an agent nobody placed starts: from the part of the canvas in view and right of the dock if he is in it, so it is neither off-screen nor underneath him. Before the page has reported a view, the top-left corner.</summary>
    public static (double X, double Y) SpotForNewCard(CanvasState canvas, bool hasDock)
    {
        var hasView = canvas.HasView && canvas.ViewWidth > 0 && canvas.Zoom > 0;
        var dock = hasView && hasDock ? DOCK_WIDTH / canvas.Zoom : 0;
        var left = hasView ? -canvas.PanX / canvas.Zoom + dock + VIEW_MARGIN : 0;
        var top = hasView ? -canvas.PanY / canvas.Zoom + VIEW_MARGIN : 0;
        var columns = hasView ? (int)Math.Floor((canvas.ViewWidth / canvas.Zoom - dock) / (CARD_WIDTH + CARD_GAP)) : DEFAULT_COLUMNS;
        return FreeSpot(canvas.Cards.Values, left, top, columns);
    }

    /// <summary>
    /// The MASTER AGENT is docked in the view's top-left corner while he is pinned, so he is not on the canvas itself.
    /// Unpinned (right-click, Unpin) he is a card on the canvas like any other. With him closed there is no dock and the corner is free.
    /// </summary>
    public static bool IsDocked(AgentSnapshot agent)
    {
        return agent is { IsMasterAgent: true, Pinned: true };
    }

    /// <summary>Takes a docked card's spot off the canvas, so no box counts him and nothing is laid out around a card that is not there, and keeps it for when he is undocked. Returns whether one was removed.</summary>
    public static bool LiftDocked(CanvasState canvas, IEnumerable<AgentSnapshot> agents)
    {
        var isLifted = false;
        foreach (var agent in agents.Where(IsDocked))
        {
            if (canvas.Cards.Remove(agent.Key, out var spot))
            {
                canvas.Parked[agent.Key] = spot;
                isLifted = true;
            }
        }

        return isLifted;
    }

    /// <summary>Where an undocked card goes back to: the spot he had before he was docked, if he had one.</summary>
    public static CanvasPoint? TakeParked(CanvasState canvas, string key)
    {
        return canvas.Parked.Remove(key, out var spot) ? spot : null;
    }

    /// <summary>Where the docked card counts as being for the arrow keys: the corner of the view it is docked in.</summary>
    public static CanvasPoint DockedAt(CanvasBoard board)
    {
        return new CanvasPoint { X = -board.PanX / board.Zoom, Y = -board.PanY / board.Zoom };
    }

    /// <summary>The next colour no box is using, or the first once all of them are.</summary>
    public static string NextColour(IEnumerable<CanvasCategory> categories)
    {
        var used = categories.Select(category => category.Colour).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return COLOURS.FirstOrDefault(colour => !used.Contains(colour)) ?? COLOURS[0];
    }

    public static CanvasCategory NewCategory(IEnumerable<CanvasCategory> categories, double x, double y)
    {
        return new CanvasCategory { Id = Guid.NewGuid().ToString("N"), Name = DEFAULT_CATEGORY_NAME, Colour = NextColour(categories), X = x, Y = y, Width = NEW_BOX_WIDTH, Height = NEW_BOX_HEIGHT };
    }

    /// <summary>One line, trimmed; left empty it keeps the old name.</summary>
    public static string CleanCategoryName(string? typed, string current)
    {
        var name = string.Join(' ', (typed ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? current : name;
    }

    public static bool IsInside(CanvasCategory category, CanvasPoint card)
    {
        var x = card.X + CARD_WIDTH / 2;
        var y = card.Y + CARD_MIDDLE_Y;
        return x >= category.X && x <= category.X + category.Width && y >= category.Y && y <= category.Y + category.Height;
    }

    public static CategoryStats StatsOf(CanvasCategory category, IReadOnlyDictionary<string, CanvasPoint> cards, IEnumerable<AgentSnapshot> agents)
    {
        var inside = agents.Where(agent => agent.IsAlive && cards.TryGetValue(agent.Key, out var card) && IsInside(category, card)).ToList();
        return new CategoryStats(
            inside.Count,
            inside.Count(agent => agent.State is AgentState.Working or AgentState.UsingEditor or AgentState.LaneBlocked),
            inside.Count(agent => agent.State is AgentState.NeedsInput or AgentState.Asked),
            inside.Count(agent => agent.Kind == AgentKind.Claude),
            inside.Count(agent => agent.Kind == AgentKind.Codex));
    }

    /// <summary>
    /// Adds what each card in a box has used since the last read. A card dropped in starts from what it had, and one taken out stops counting,
    /// so the meter says what was spent in that box. Returns whether any meter moved.
    /// </summary>
    public static bool AccrueTokens(CanvasState canvas, IEnumerable<AgentSnapshot> agents)
    {
        var isUsed = false;
        var all = agents.ToList();
        foreach (var category in canvas.Categories)
        {
            var seen = new Dictionary<string, long>();
            foreach (var agent in all.Where(a => canvas.Cards.TryGetValue(a.Key, out var card) && IsInside(category, card)))
            {
                var total = agent.Tokens.Total + agent.SubagentTokens;
                seen[agent.Key] = total;
                if (category.TokensSeen.TryGetValue(agent.Key, out var last) && total > last)
                {
                    category.Tokens += total - last;
                    isUsed = true;
                }
            }

            category.TokensSeen = seen;
        }

        return isUsed;
    }

    /// <summary>Starts the meter again; what it read goes into the box's history, and the cards inside keep counting from where they are.</summary>
    public static void ResetTokens(CanvasCategory category, DateTimeOffset now)
    {
        category.TokenHistory.Add(new CategoryTokenStretch(category.TokensSince, now, category.Tokens));
        if (category.TokenHistory.Count > MAX_TOKEN_STRETCHES)
        {
            category.TokenHistory.RemoveAt(0);
        }

        category.Tokens = 0;
        category.TokensSince = now;
    }

    /// <summary>The card an arrow key goes to: the nearest strictly in that direction, or null at the edge.</summary>
    public static string? Nearest(string fromKey, CanvasDirection direction, IReadOnlyDictionary<string, CanvasPoint> cards)
    {
        if (!cards.TryGetValue(fromKey, out var from))
        {
            return null;
        }

        string? best = null;
        var bestCost = double.MaxValue;
        foreach (var (key, card) in cards)
        {
            if (key == fromKey)
            {
                continue;
            }

            var dx = card.X - from.X;
            var dy = card.Y - from.Y;
            var (ahead, across) = direction switch
            {
                CanvasDirection.Right => (dx, dy),
                CanvasDirection.Left => (-dx, dy),
                CanvasDirection.Down => (dy, dx),
                _ => (-dy, dx),
            };
            if (ahead <= 0)
            {
                continue;
            }

            var cost = ahead + Math.Abs(across) * OFF_AXIS_WEIGHT;
            if (cost < bestCost)
            {
                bestCost = cost;
                best = key;
            }
        }

        return best;
    }

    /// <summary>The card closest to the top-left, where the arrow keys start when nothing is selected.</summary>
    public static string? First(IReadOnlyDictionary<string, CanvasPoint> cards)
    {
        return cards.OrderBy(card => card.Value.Y + card.Value.X).Select(card => card.Key).FirstOrDefault();
    }

    private static bool Overlaps(CanvasPoint card, double x, double y)
    {
        return x < card.X + CARD_WIDTH && card.X < x + CARD_WIDTH && y < card.Y + CARD_HEIGHT && card.Y < y + CARD_HEIGHT;
    }
}
