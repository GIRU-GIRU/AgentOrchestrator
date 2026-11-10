using System.Globalization;
using AgentManager.Core.Model;

namespace AgentManager.App.Components;

public static class Ascii
{
    public const int SPLIT_CELLS = 34;

    private static readonly char[] BLOCKS = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    /// <summary>Who sent a chat message. Muted hues only, so a name never reads as an agent's state.</summary>
    private static readonly string[] PALETTE = ["#5ee0c8", "#d9c39a", "#9db7d6", "#b5bd68", "#d8a0b0", "#c49a7a", "#c3cee0", "#8fc7c7", "#8fae7f", "#b0a3b8"];

    public static readonly Activity[] ACTIVITIES = [Activity.Think, Activity.Code, Activity.Test, Activity.Build, Activity.Tools, Activity.Lane];

    public static IReadOnlyList<(char Glyph, int Level)> Spark(IReadOnlyList<long> values)
    {
        var max = values.Count == 0 ? 0 : values.Max();
        var cells = new (char, int)[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] <= 0 || max <= 0)
            {
                cells[i] = ('▁', 0);
                continue;
            }

            var level = (int)Math.Clamp(Math.Ceiling(Math.Sqrt(values[i] / (double)max) * BLOCKS.Length), 1, BLOCKS.Length);
            cells[i] = (BLOCKS[level - 1], level);
        }

        return cells;
    }

    public static IReadOnlyList<(Activity Activity, int Cells)> Split(TimeSplit split, int width = SPLIT_CELLS)
    {
        var total = split.Total;
        if (total <= 0)
        {
            return [];
        }

        var exact = ACTIVITIES.Select(a => (Activity: a, Share: split[a] / total * width)).ToList();
        var cells = exact.Select(e => (e.Activity, Cells: (int)Math.Floor(e.Share))).ToList();
        var remaining = width - cells.Sum(c => c.Cells);
        foreach (var extra in exact.Select((e, i) => (Index: i, Remainder: e.Share - Math.Floor(e.Share))).OrderByDescending(e => e.Remainder).Take(remaining))
        {
            cells[extra.Index] = (cells[extra.Index].Activity, cells[extra.Index].Cells + 1);
        }

        return cells.Where(c => c.Cells > 0).ToList();
    }

    public static string Duration(double seconds)
    {
        if (seconds < 0.5)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalMinutes < 1)
        {
            return $"{span.Seconds}s";
        }

        if (span.TotalHours < 1)
        {
            return $"{span.Minutes}m {span.Seconds:00}s";
        }

        return span.TotalDays < 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m" : $"{(int)span.TotalDays}d {span.Hours}h";
    }

    public static string Duration(TimeSpan span)
    {
        return Duration(span.TotalSeconds);
    }

    public static string Short(double seconds)
    {
        if (seconds < 0.5)
        {
            return "0";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalMinutes < 1 ? $"{span.Seconds}s" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" : $"{span.TotalHours:0.#}h";
    }

    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null)
        {
            return "—";
        }

        var span = now - at.Value;
        return span.TotalSeconds < 5 ? "now" : Short(span.TotalSeconds) + " ago";
    }

    public static string Tokens(long tokens)
    {
        return tokens switch
        {
            <= 0 => "—",
            < 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
            < 1_000_000 => (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
            < 1_000_000_000 => (tokens / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M",
            _ => (tokens / 1_000_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        };
    }

    public static string Color(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "var(--dim)";
        }

        var hash = 0u;
        foreach (var c in key)
        {
            hash = hash * 31 + c;
        }

        return PALETTE[hash % PALETTE.Length];
    }

    public static string Label(Activity activity)
    {
        return activity switch
        {
            Activity.Think => "WORKING",
            Activity.Code => "CODING",
            Activity.Test => "TESTING",
            Activity.Build => "BUILDING",
            Activity.Tools => "TOOLS",
            _ => "LANE",
        };
    }

    public static string Icon(Activity activity)
    {
        return activity switch
        {
            Activity.Think => "✻",
            Activity.Code => "✎",
            Activity.Test => "",
            Activity.Build => "⚒",
            Activity.Tools => "⚙",
            _ => "▶",
        };
    }

    public static string Css(Activity activity)
    {
        return "a-" + activity.ToString().ToLowerInvariant();
    }

    public static string Css(AgentState state)
    {
        return state switch
        {
            AgentState.NeedsInput => "s-input",
            AgentState.Asked => "s-asked",
            AgentState.LaneBlocked => "s-lane",
            AgentState.UsingEditor => "s-editor",
            AgentState.Working => "s-working",
            AgentState.Paused => "s-paused",
            AgentState.Finished => "s-finished",
            AgentState.Closed => "s-closed",
            _ => "s-idle",
        };
    }

    public static string Badge(AgentSnapshot agent)
    {
        return agent.State switch
        {
            AgentState.NeedsInput => "",
            AgentState.Asked => "? ASKED",
            AgentState.LaneBlocked => agent.Lane?.Paused == true ? "⏸ PAUSED" : "⧗ QUEUED",
            AgentState.UsingEditor => "▶ " + (agent.Lane is { HoldsLane: true, HeldKind: { } kind } ? kind.ToUpperInvariant() : "EDITOR"),
            AgentState.Working => WorkLabel(agent),
            AgentState.Paused => "⏸ PAUSED",
            AgentState.Finished => "✓ FINISHED",
            AgentState.Closed => "✕ CLOSED",
            _ => agent.IsRead ? "○ READ" : "○ IDLE",
        };
    }

    public static string WorkLabel(AgentSnapshot agent)
    {
        if (agent.CurrentTool is null && agent.RunningSubagents > 0)
        {
            return "◆ SUBAGENTS";
        }

        var activity = WorkActivity(agent);
        return activity == Activity.Test ? "TESTING" : Icon(activity) + " " + WorkWord(activity);
    }

    public static Activity WorkActivity(AgentSnapshot agent)
    {
        return agent.CurrentTool?.Activity ?? Activity.Think;
    }

    public static bool IsBuilding(AgentSnapshot agent)
    {
        return agent.State == AgentState.Working && agent.CurrentTool?.Activity == Activity.Build;
    }

    public static bool ShowsBug(AgentSnapshot agent)
    {
        return agent.State == AgentState.Working && agent.CurrentTool?.Activity == Activity.Test;
    }

    private static string WorkWord(Activity activity)
    {
        return activity switch
        {
            Activity.Code => "CODING",
            Activity.Build => "BUILDING",
            Activity.Lane => "LANE",
            Activity.Tools => "TOOLS",
            _ => "WORKING",
        };
    }

    public static string Day(DateTimeOffset at)
    {
        return at.ToString("ddd d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>An agent's markdown as one readable run of text: no emphasis marks, code ticks or headings, lines joined.</summary>
    public static string? Plain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('#', '>', ' ').Replace("**", "").Replace("`", ""))
            .Where(l => l.Length > 0 && !l.StartsWith("|---", StringComparison.Ordinal) && !l.StartsWith("```", StringComparison.Ordinal));
        return string.Join(" ", lines);
    }

    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
