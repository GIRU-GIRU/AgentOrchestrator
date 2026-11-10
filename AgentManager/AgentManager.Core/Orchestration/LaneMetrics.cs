namespace AgentManager.Core.Orchestration;

public sealed record OwnerLaneDay(double HeldSeconds, double WaitedSeconds, int Grants);

public sealed record LaneDay(
    double BusySeconds,
    double ElapsedSeconds,
    int Grants,
    int Builds,
    IReadOnlyDictionary<string, OwnerLaneDay> Owners);

/// <summary>How the shared lane was spent, from the orchestrator's grant rows.</summary>
public static class LaneMetrics
{
    private const double MIN_WAIT_SECONDS = 2;

    public static LaneDay Today(IEnumerable<GrantRow> grants, DateTimeOffset now)
    {
        var midnight = new DateTimeOffset(now.Date, now.Offset);
        var owners = new Dictionary<string, (double Held, double Waited, int Grants)>();
        double busy = 0;
        int count = 0, builds = 0;
        foreach (var grant in grants.Where(g => g.IsLane))
        {
            var entry = owners.GetValueOrDefault(grant.Owner);
            if (grant.AcquiredAt is { } acquired && (grant.ReleasedAt ?? now) > midnight)
            {
                var held = (Min(grant.ReleasedAt ?? now, now) - Max(acquired, midnight)).TotalSeconds;
                if (held > 0)
                {
                    busy += held;
                    entry.Held += held;
                }

                if (acquired >= midnight)
                {
                    count++;
                    entry.Grants++;
                    builds += grant.Kind == "Build" ? 1 : 0;
                }
            }

            var waited = WaitSeconds(grant, now, midnight);
            if (waited >= MIN_WAIT_SECONDS)
            {
                entry.Waited += waited;
            }

            owners[grant.Owner] = entry;
        }

        return new LaneDay(busy, (now - midnight).TotalSeconds, count, builds,
            owners.ToDictionary(o => o.Key, o => new OwnerLaneDay(o.Value.Held, o.Value.Waited, o.Value.Grants)));
    }

    private static double WaitSeconds(GrantRow grant, DateTimeOffset now, DateTimeOffset since)
    {
        if (grant.IsImplicit || grant.QueuedAt is not { } queued)
        {
            return 0;
        }

        var end = grant.AcquiredAt ?? (grant.State == "queued" ? now : grant.ReleasedAt);
        if (end is null || end <= since)
        {
            return 0;
        }

        return (end.Value - Max(queued, since)).TotalSeconds;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b)
    {
        return a < b ? a : b;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b)
    {
        return a > b ? a : b;
    }
}
