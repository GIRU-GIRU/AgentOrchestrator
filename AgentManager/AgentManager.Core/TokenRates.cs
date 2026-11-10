namespace AgentManager.Core;

/// <summary>
/// Live token rate per agent: the tokens its transcripts gained since the last tick, smoothed, kept as a short history
/// for the card's graph. Transcripts land in bursts (a whole response at once), so the rate is an exponential average.
/// </summary>
public sealed class TokenRates
{
    public const int HISTORY = 90;

    private static readonly TimeSpan SMOOTHING = TimeSpan.FromSeconds(4);

    private readonly Dictionary<string, Track> tracks = [];

    public (double Rate, IReadOnlyList<double> History) Sample(string key, long totalTokens, DateTimeOffset now)
    {
        if (!tracks.TryGetValue(key, out var track))
        {
            track = new Track(totalTokens, now);
            tracks[key] = track;
            return (0, track.History.ToArray());
        }

        var elapsed = (now - track.At).TotalSeconds;
        if (elapsed < 0.2)
        {
            return (track.Rate, track.History.ToArray());
        }

        var added = Math.Max(0, totalTokens - track.Total);
        var instant = added / elapsed;
        var blend = 1 - Math.Exp(-elapsed / SMOOTHING.TotalSeconds);
        track.Rate += (instant - track.Rate) * blend;
        track.Total = totalTokens;
        track.At = now;
        track.History.Enqueue(track.Rate);
        while (track.History.Count > HISTORY)
        {
            track.History.Dequeue();
        }

        return (track.Rate, track.History.ToArray());
    }

    public void Forget(IEnumerable<string> keep)
    {
        var live = keep.ToHashSet();
        foreach (var key in tracks.Keys.Where(k => !live.Contains(k)).ToList())
        {
            tracks.Remove(key);
        }
    }

    private sealed class Track(long total, DateTimeOffset at)
    {
        public long Total { get; set; } = total;

        public DateTimeOffset At { get; set; } = at;

        public double Rate { get; set; }

        public Queue<double> History { get; } = new(Enumerable.Repeat(0.0, HISTORY));
    }
}
