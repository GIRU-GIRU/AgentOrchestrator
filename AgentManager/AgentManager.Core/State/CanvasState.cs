namespace AgentManager.Core.State;

/// <summary>Where one card sits on the canvas, in world units. <see cref="Z"/> is raised when it is dropped, so the last card moved is on top.</summary>
public sealed class CanvasPoint
{
    public double X { get; set; }

    public double Y { get; set; }

    public int Z { get; set; }
}

/// <summary>A coloured box the user drew to group agents. It holds nothing itself: what is inside is worked out from positions when it is dragged.</summary>
public sealed class CanvasCategory
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Colour { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    /// <summary>What the agents in this box have used since <see cref="TokensSince"/>, counted while they are in it, like a trip meter.</summary>
    public long Tokens { get; set; }

    public DateTimeOffset? TokensSince { get; set; }

    /// <summary>Each card's running total when last read, so only what it uses while inside counts.</summary>
    public Dictionary<string, long> TokensSeen { get; set; } = [];

    /// <summary>The stretches a reset closed, newest last.</summary>
    public List<CategoryTokenStretch> TokenHistory { get; set; } = [];
}

/// <summary>A category's token meter between two resets.</summary>
public sealed record CategoryTokenStretch(DateTimeOffset? From, DateTimeOffset To, long Tokens);

/// <summary>The canvas the cards sit on. A card never moves by itself: only a drag or a spawn writes a position.</summary>
public sealed class CanvasState
{
    public Dictionary<string, CanvasPoint> Cards { get; set; } = [];

    /// <summary>Where a docked card sat on the canvas, so undocking him puts him back there.</summary>
    public Dictionary<string, CanvasPoint> Parked { get; set; } = [];

    public List<CanvasCategory> Categories { get; set; } = [];

    /// <summary>False until the page has reported a view, which is how the first opening knows to fit everything.</summary>
    public bool HasView { get; set; }

    public double PanX { get; set; }

    public double PanY { get; set; }

    public double Zoom { get; set; } = 1;

    public double ViewWidth { get; set; }

    public double ViewHeight { get; set; }
}

/// <summary>What the page draws, copied out of the store so a render never holds its lock.</summary>
public sealed record CanvasBoard(IReadOnlyDictionary<string, CanvasPoint> Cards, IReadOnlyList<CanvasCategory> Categories, bool HasView, double PanX, double PanY, double Zoom);

/// <summary>Where a card landed after a drag.</summary>
public sealed record CanvasCardMove(string Key, double X, double Y);

/// <summary>Where a card should appear once the window just asked for has started.</summary>
public sealed record CanvasCardSpot(double X, double Y);
