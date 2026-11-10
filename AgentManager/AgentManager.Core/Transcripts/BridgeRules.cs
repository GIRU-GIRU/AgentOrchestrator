using System.Text.RegularExpressions;
using AgentManager.Core.Model;

namespace AgentManager.Core.Transcripts;

public static partial class BridgeRules
{
    public const string ANY_CHANNEL = "*";

    public static readonly TimeSpan QUIET_AFTER = TimeSpan.FromHours(1);

    public static readonly TimeSpan MOVING_AFTER = TimeSpan.FromMinutes(2);

    public const string NOBODY_YET = "nobody yet";

    private const string ASTRA = "Astra";

    private const string TITLE_SEPARATOR = ": ";

    private const string CODEX = "Codex";

    private const string NAMED_OWNER = "agent:";

    private const string ASTRA_OWNER = NAMED_OWNER + "astra-";

    private const string CODEX_OWNER = "codex";

    private const string ASTRA_MODEL_SUFFIX = "-astra";

    /// <summary>
    /// A channel shows on a card only with another agent still on it, and only while it is in use: a message within
    /// <see cref="QUIET_AFTER"/>, or the agent waiting on it by name. A bare <c>inbox --wait</c> names no channel and is often for
    /// a direct message, so it keeps none alive.
    /// </summary>
    public static bool IsLive(BridgeInfo bridge, string? listeningOn, DateTimeOffset now)
    {
        if (!bridge.Peers.Any(p => p.IsAlive))
        {
            return false;
        }

        var isRecent = bridge.LastAt is { } last && now - last < QUIET_AFTER;
        return isRecent || listeningOn == bridge.Channel;
    }

    /// <summary>The live channels, newest first: a card shows the first and counts the rest.</summary>
    public static IReadOnlyList<BridgeInfo> OnCard(IEnumerable<BridgeInfo> bridges, string? listeningOn, DateTimeOffset now)
    {
        return bridges.Where(b => IsLive(b, listeningOn, now)).OrderByDescending(b => b.LastAt).ToList();
    }

    /// <summary>
    /// The traces the grid draws, from the cards it shows. Cards pair by channel, never by name: an Astra's card and its peer's row call
    /// each other different things. A row's own peers count too, because a Codex thread on the daemon's shared owner has no row of its
    /// own. A partner that is closed or not on the grid (filtered, hidden) makes no trace, so a line can never lead nowhere; the card's
    /// bridge line still says who it is with. Three cards on one channel are a chain.
    /// </summary>
    public static IReadOnlyList<BridgeLink> Links(IReadOnlyList<AgentSnapshot> shown, DateTimeOffset now)
    {
        var cards = shown.Where(a => a.IsAlive).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        var channels = new SortedDictionary<string, (List<string> Ends, List<BridgeInfo> Rows)>(StringComparer.Ordinal);
        foreach (var card in cards.Values)
        {
            foreach (var bridge in card.Bridges)
            {
                if (!channels.TryGetValue(bridge.Channel, out var channel))
                {
                    channel = ([], []);
                    channels[bridge.Channel] = channel;
                }

                channel.Rows.Add(bridge);
                channel.Ends.Add(card.Key);
                channel.Ends.AddRange(bridge.Peers.Where(p => p.IsAlive && p.Key is not null && cards.ContainsKey(p.Key)).Select(p => p.Key!));
            }
        }

        var order = shown.Select((a, i) => (a.Key, i)).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().i);
        var links = new List<BridgeLink>();
        foreach (var (name, channel) in channels)
        {
            var ends = channel.Ends.Distinct().OrderBy(key => order[key]).ToList();
            var sender = channel.Rows.OrderByDescending(r => r.LastAt).First().LastFromKey;
            var isActive = channel.Rows.Any(r => r.LastAt is { } last && now - last < MOVING_AFTER) || ends.Any(key => IsBusy(cards[key]));
            for (var i = 0; i + 1 < ends.Count; i++)
            {
                var isSecondSender = sender == ends[i + 1];
                links.Add(new BridgeLink(name, isSecondSender ? ends[i + 1] : ends[i], isSecondSender ? ends[i] : ends[i + 1], isActive));
            }
        }

        return links;
    }

    /// <summary>Doing something of its own. A card waiting on a bridge reads as working, but two agents waiting on each other are a quiet bridge.</summary>
    private static bool IsBusy(AgentSnapshot card)
    {
        if (card.State is not (AgentState.Working or AgentState.UsingEditor or AgentState.Asked))
        {
            return false;
        }

        var isWaitingInBackground = !card.IsWorking && card.ListeningOn is not null && card.RunningCommands <= 1;
        return card.RunningSubagents > 0 || (!card.IsWaitingOnBridge && !isWaitingInBackground);
    }

    /// <summary>
    /// What is behind a channel member. An <c>agent:</c> owner named itself and could be anything, except that astra.py's windows are
    /// Codex; null means no card can be matched to it.
    /// </summary>
    public static AgentKind? OwnerKind(string owner)
    {
        if (owner.StartsWith(CODEX_OWNER, StringComparison.Ordinal) || owner.StartsWith(ASTRA_OWNER, StringComparison.Ordinal))
        {
            return AgentKind.Codex;
        }

        return owner.StartsWith(NAMED_OWNER, StringComparison.Ordinal) ? null : AgentKind.Claude;
    }

    /// <summary>
    /// Who the user is talking about. Every Astra is "Astra", whichever window and however it joined; a Codex thread's title is its
    /// first prompt ("Run orch inbox command"), so an Astra thread never goes by it.
    /// </summary>
    public static string PeerName(string owner, string? model, string? cardName, string ownerName)
    {
        var isAstra = owner.StartsWith(ASTRA_OWNER, StringComparison.Ordinal) || model?.EndsWith(ASTRA_MODEL_SUFFIX, StringComparison.Ordinal) == true;
        if (isAstra)
        {
            return ASTRA;
        }

        if (owner.StartsWith(NAMED_OWNER, StringComparison.Ordinal))
        {
            return owner[NAMED_OWNER.Length..];
        }

        return cardName ?? (owner.StartsWith(CODEX_OWNER, StringComparison.Ordinal) ? CODEX : ownerName);
    }

    /// <summary>Who a post on a channel reaches: everyone on it but the sender, each name once. The channel's own name says nothing about who is listening.</summary>
    public static string Audience(IReadOnlyDictionary<string, string> members, string? sender)
    {
        var others = AudienceNames(members, sender);
        return others.Count == 0 ? NOBODY_YET : string.Join(Chat.ChatLog.RECIPIENT_SEPARATOR, others);
    }

    public static IReadOnlyList<string> AudienceNames(IReadOnlyDictionary<string, string> members, string? sender)
    {
        return members.Where(m => m.Key != sender).Select(m => m.Value).Distinct().ToList();
    }

    /// <summary>What an astra.py window is called. Codex titles a thread from its first prompt in words of its own ("Run orch inbox command"), which tell the user nothing.</summary>
    public static string? AstraTitle(string? firstPrompt)
    {
        var match = AstraJoinLine().Match(firstPrompt ?? "");
        return match.Success ? ASTRA + TITLE_SEPARATOR + match.Groups["channel"].Value : null;
    }

    /// <summary>
    /// A closed Astra leaves no card: the watcher closes one whose job looks done and reopens it when work arrives, so there is nothing for
    /// the user to resume or clear. It is not marked dismissed, so the same thread running again has its card back.
    /// </summary>
    public static bool LeavesNoClosedCard(AgentKind kind, string? name, bool isAlive)
    {
        return !isAlive && kind == AgentKind.Codex && IsAstraTitle(name);
    }

    public static bool IsAstraTitle(string? name)
    {
        return name?.StartsWith(ASTRA + TITLE_SEPARATOR, StringComparison.Ordinal) == true;
    }

    [GeneratedRegex(@"^Run \S*orch\S* --as astra-(?<channel>[^\s…]+)")]
    private static partial Regex AstraJoinLine();

    [GeneratedRegex(@"orch(?:\.cmd|\.py)\b[^|;&\n]*?\binbox\b(?:\s+(?!-)(?<work>[\w.-]+))?[^|;&\n]*?--wait", RegexOptions.IgnoreCase)]
    private static partial Regex ListenPattern();

    [GeneratedRegex(@"orch(?:\.cmd|\.py)\b[^|;&\n]*?\b(?:inbox\s+(?!-)(?<work>[a-z0-9][\w.:-]*)|msg\s+--to\s+(?<work>[a-z0-9][\w.:-]*))", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelPattern();

    /// <summary>Every bridge channel a command reads or posts on (<c>inbox &lt;work&gt;</c>, <c>msg --to &lt;work&gt;</c>).</summary>
    public static IEnumerable<string> MentionedChannels(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            yield break;
        }

        foreach (Match match in ChannelPattern().Matches(command))
        {
            var work = match.Groups["work"].Value.ToLowerInvariant();
            var isOwner = work.StartsWith("claude:", StringComparison.Ordinal) || work.StartsWith("codex:", StringComparison.Ordinal);
            if (!isOwner)
            {
                yield return work;
            }
        }
    }

    /// <summary>
    /// The bridge channel a command waits on (<c>orch.cmd inbox &lt;work&gt; --wait</c>), <see cref="ANY_CHANNEL"/> for the
    /// agent's own inbox (<c>inbox --wait</c>), or null when the command is not waiting for a bridge message.
    /// </summary>
    public static string? ListenChannel(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        var match = ListenPattern().Match(command);
        if (!match.Success)
        {
            return null;
        }

        return match.Groups["work"].Success ? match.Groups["work"].Value.ToLowerInvariant() : ANY_CHANNEL;
    }
}
