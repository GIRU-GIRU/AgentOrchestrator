using System.Text;
using AgentManager.Core.Model;

namespace AgentManager.Core.Chat;

/// <summary>
/// Every agent message, once. A message is usually written twice, by its sender and by each receiver, so records with the
/// same text a few minutes apart merge: the sender's record names the sender, a receiver's names the receiver.
/// </summary>
public sealed class ChatLog
{
    private const int CAPACITY = 6000;

    private const int PRINT_CHARS = 400;

    /// <summary>What joins the names when one text went to several agents.</summary>
    public const string RECIPIENT_SEPARATOR = ", ";

    private static readonly TimeSpan MERGE_WINDOW = TimeSpan.FromMinutes(3);

    private readonly object gate = new();

    private readonly List<Entry> entries = [];

    private readonly Dictionary<string, List<Entry>> byPrint = [];

    private readonly HashSet<string> ids = [];

    private bool isSorted = true;

    private ChatMessage[] snapshot = [];

    private bool isDirty;

    public int Version { get; private set; }

    /// <param name="matchText">What the record's twins are found by, when the message shows something other than what was sent: two encrypted messages read the same on screen and are still two.</param>
    public void Add(ChatMessage message, bool isSenderRecord, string? matchText = null)
    {
        lock (gate)
        {
            if (!ids.Add(message.Id))
            {
                return;
            }

            var print = Fingerprint(matchText ?? message.Text);
            if (byPrint.TryGetValue(print, out var similar))
            {
                var twin = similar.FirstOrDefault(e => (e.Message.At - message.At).Duration() < MERGE_WINDOW && IsSameSender(e.Message, message));
                if (twin is not null)
                {
                    Merge(twin, message, isSenderRecord);
                    MarkDirty();
                    return;
                }
            }
            else
            {
                similar = [];
                byPrint[print] = similar;
            }

            var entry = new Entry(message, isSenderRecord, print);
            similar.Add(entry);
            if (entries.Count > 0 && entries[^1].Message.At > message.At)
            {
                isSorted = false;
            }

            entries.Add(entry);
            Trim();
            MarkDirty();
        }
    }

    public IReadOnlyList<ChatMessage> Snapshot()
    {
        lock (gate)
        {
            if (!isDirty)
            {
                return snapshot;
            }

            if (!isSorted)
            {
                entries.Sort((a, b) => a.Message.At.CompareTo(b.Message.At));
                isSorted = true;
            }

            snapshot = entries.Select(e => e.Message).ToArray();
            isDirty = false;
            return snapshot;
        }
    }

    /// <summary>Two agents saying the same "Done." are two messages. A receiver often cannot tell who sent one, and that record still merges.</summary>
    private static bool IsSameSender(ChatMessage a, ChatMessage b)
    {
        return a.FromKey is null || b.FromKey is null || a.FromKey == b.FromKey;
    }

    private static void Merge(Entry existing, ChatMessage incoming, bool isSenderRecord)
    {
        var current = existing.Message;
        var isBroadcastSend = isSenderRecord && existing.FromSender;
        if (isBroadcastSend)
        {
            if (existing.AddRecipient(incoming))
            {
                existing.Message = current with { To = current.To + RECIPIENT_SEPARATOR + incoming.To, ToKey = null, Recipients = current.Recipients + 1 };
            }

            return;
        }

        if (isSenderRecord && !existing.FromSender)
        {
            existing.AddRecipient(incoming);
            existing.Message = current with
            {
                From = incoming.From,
                FromKey = incoming.FromKey,
                Summary = incoming.Summary ?? current.Summary,
                At = incoming.At < current.At ? incoming.At : current.At,
                To = current.ToKey is not null ? current.To : incoming.To,
                ToKey = current.ToKey ?? incoming.ToKey,
            };
            existing.FromSender = true;
            return;
        }

        if (!isSenderRecord && existing.FromSender)
        {
            existing.AddRecipient(incoming);
            var hasBetterRecipient = current.ToKey is null && incoming.ToKey is not null && current.Recipients == 1;
            if (hasBetterRecipient)
            {
                existing.Message = current with { To = incoming.To, ToKey = incoming.ToKey };
            }

            return;
        }

        var isBroadcast = !isSenderRecord && incoming.ToKey != current.ToKey && incoming.To != current.To;
        if (isBroadcast && existing.AddRecipient(incoming))
        {
            var isOnAChannel = current.Topic is not null;
            var to = isOnAChannel ? "#" + current.Topic : current.To + RECIPIENT_SEPARATOR + incoming.To;
            existing.Message = current with { To = to, ToKey = null, Recipients = isOnAChannel ? 1 : current.Recipients + 1 };
        }
    }

    private void Trim()
    {
        if (entries.Count <= CAPACITY)
        {
            return;
        }

        if (!isSorted)
        {
            entries.Sort((a, b) => a.Message.At.CompareTo(b.Message.At));
            isSorted = true;
        }

        var removed = entries[0];
        entries.RemoveAt(0);
        if (byPrint.TryGetValue(removed.Print, out var list))
        {
            list.Remove(removed);
            if (list.Count == 0)
            {
                byPrint.Remove(removed.Print);
            }
        }
    }

    private void MarkDirty()
    {
        isDirty = true;
        Version++;
    }

    internal static string Fingerprint(string text)
    {
        var builder = new StringBuilder(PRINT_CHARS);
        var lastWasSpace = true;
        foreach (var c in text)
        {
            if (builder.Length >= PRINT_CHARS)
            {
                break;
            }

            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private sealed class Entry
    {
        /// <summary>Each recipient's name and key: a sender's <c>to</c> address and a receiver's display name are one agent under two names.</summary>
        private readonly HashSet<string> recipients = [];

        public Entry(ChatMessage message, bool fromSender, string print)
        {
            Message = message;
            FromSender = fromSender;
            Print = print;
            AddRecipient(message);
        }

        public ChatMessage Message { get; set; }

        public bool FromSender { get; set; }

        public string Print { get; }

        public bool AddRecipient(ChatMessage message)
        {
            var isKnown = recipients.Contains(message.To) || (message.ToKey is { } known && recipients.Contains(known));
            recipients.Add(message.To);
            if (message.ToKey is { } key)
            {
                recipients.Add(key);
            }

            return !isKnown;
        }
    }
}
