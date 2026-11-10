using System.Text;
using System.Text.RegularExpressions;

namespace AgentManager.Core.Chat;

public sealed record Envelope(string Tag, string From, string? FromName, string Body);

public static partial class EnvelopeParser
{
    private const string HANDBACK_MARKER = "The report follows:";

    [GeneratedRegex(@"<(?<tag>cross-session-message|agent-message|teammate-message)\b(?<attrs>[^>]*)>(?<body>.*?)</\k<tag>>", RegexOptions.Singleline)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"(?<k>[\w-]+)=""(?<v>[^""]*)""")]
    private static partial Regex Attribute();

    public static IEnumerable<Envelope> Parse(string text)
    {
        foreach (Match match in Pattern().Matches(text))
        {
            var attributes = Attribute().Matches(match.Groups["attrs"].Value)
                .GroupBy(m => m.Groups["k"].Value)
                .ToDictionary(g => g.Key, g => g.First().Groups["v"].Value);
            var from = attributes.GetValueOrDefault("from") ?? attributes.GetValueOrDefault("teammate_id") ?? "?";
            var name = attributes.GetValueOrDefault("from-name");
            yield return new Envelope(match.Groups["tag"].Value, from, string.IsNullOrWhiteSpace(name) ? null : name, CleanBody(match.Groups["body"].Value));
        }
    }

    internal static string CleanBody(string body)
    {
        var text = body.Trim('\r', '\n');
        var marker = text.IndexOf(HANDBACK_MARKER, StringComparison.Ordinal);
        if (text.StartsWith("[Subagent hand-back]", StringComparison.Ordinal) && marker >= 0)
        {
            text = Dedent(text[(marker + HANDBACK_MARKER.Length)..]);
        }

        return text.Trim();
    }

    private static string Dedent(string text)
    {
        var lines = text.Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        var builder = new StringBuilder(text.Length);
        foreach (var line in lines)
        {
            builder.Append(line.Length >= indent ? line[indent..] : line.TrimStart()).Append('\n');
        }

        return builder.ToString();
    }
}
