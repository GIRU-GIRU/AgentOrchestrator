using AgentManager.Core.Model;

namespace AgentManager.Core;

public sealed class StatsEntry
{
    public TimeSplit Split { get; set; } = TimeSplit.Empty;

    public long Tokens { get; set; }
}

/// <summary>The agents' added-up time and tokens over one stretch: the running one, or one closed by a reset.</summary>
public sealed record StatsPeriod(DateTimeOffset? From, DateTimeOffset To, TimeSplit Split, long Tokens);

/// <summary>What the header's numbers count from. A session's own figures only ever grow, so a reset is a baseline, never a change to any session.</summary>
public sealed class StatsBook
{
    public DateTimeOffset? PeriodStart { get; set; }

    public Dictionary<string, StatsEntry> Baseline { get; set; } = [];

    public Dictionary<string, StatsEntry> Latest { get; set; } = [];

    public List<StatsPeriod> History { get; set; } = [];
}

/// <summary>
/// Latest keeps an agent's last figures after it leaves the grid, so closing a card does not take its time out of the
/// period, and a session first seen after a reset that began before it counts only from then.
/// </summary>
public static class Stats
{
    private const int HISTORY_KEPT = 500;

    private static readonly TimeSpan RESET_GUARD = TimeSpan.FromSeconds(5);

    public static void Observe(StatsBook book, AgentSnapshot agent)
    {
        var entry = new StatsEntry { Split = agent.Split, Tokens = agent.Tokens.Total + agent.SubagentTokens };
        var isNew = !book.Latest.ContainsKey(agent.Key);
        if (isNew && book.PeriodStart is { } start && (agent.FirstEventAt ?? agent.StartedAt) < start)
        {
            book.Baseline[agent.Key] = entry;
        }

        book.Latest[agent.Key] = entry;
    }

    public static StatsPeriod Current(StatsBook book, DateTimeOffset now)
    {
        var seconds = new double[Enum.GetValues<Activity>().Length];
        long tokens = 0;
        foreach (var (key, latest) in book.Latest)
        {
            var baseline = book.Baseline.GetValueOrDefault(key);
            foreach (var activity in Enum.GetValues<Activity>())
            {
                seconds[(int)activity] += Math.Max(0, latest.Split[activity] - (baseline?.Split[activity] ?? 0));
            }

            tokens += Math.Max(0, latest.Tokens - (baseline?.Tokens ?? 0));
        }

        return new StatsPeriod(book.PeriodStart, now, TimeSplit.From(seconds), tokens);
    }

    /// <summary>
    /// Closes the running period into the history and starts counting from zero. A second reset straight after the first (a double
    /// click) has nothing to close, and its row would share the first one's key in the panel.
    /// </summary>
    public static bool Reset(StatsBook book, DateTimeOffset now)
    {
        if (book.PeriodStart is { } last && now - last < RESET_GUARD)
        {
            return false;
        }

        book.History.Add(Current(book, now));
        if (book.History.Count > HISTORY_KEPT)
        {
            book.History.RemoveAt(0);
        }

        book.Baseline = new Dictionary<string, StatsEntry>(book.Latest);
        book.PeriodStart = now;
        return true;
    }

    /// <summary>
    /// Forgets agents the store no longer knows, or the ledger grows for ever in a file rewritten every minute. An agent that still
    /// adds to the running period stays until the next reset, or its time would leave the period. If it turns up again it is new,
    /// and counts from then.
    /// </summary>
    public static void Prune(StatsBook book, Func<string, bool> isKnown)
    {
        foreach (var (key, latest) in book.Latest.ToList())
        {
            var baseline = book.Baseline.GetValueOrDefault(key);
            var addsToThePeriod = latest.Tokens > (baseline?.Tokens ?? 0) || latest.Split.Total > (baseline?.Split.Total ?? 0);
            if (!isKnown(key) && !addsToThePeriod)
            {
                book.Latest.Remove(key);
                book.Baseline.Remove(key);
            }
        }

        foreach (var key in book.Baseline.Keys.Where(k => !book.Latest.ContainsKey(k)).ToList())
        {
            book.Baseline.Remove(key);
        }
    }
}
