using System.Text.RegularExpressions;

namespace AgentManager.Core.Transcripts;

public static partial class Questions
{
    private static readonly char[] DECORATION = ['*', '_', '`', ' ', '\t', ')', '"', '\''];

    [GeneratedRegex(@"(^|[,;:]\s+|\b(and|then|just|so|or)\s+)(tell me|let me know)\b|(^|\b(just|and|then|or|you)\s+)say\s+""|\bwaiting for you to\b|\b(i['’]?m|i am)( also)? waiting (on|for) your?\b|\bwaiting on you\b|\byou need to\b|\bas soon as you\b|\bblocked on you\b|\bneeds? you to\b|\bneeds? your (sign-off|hands|yes|approval|decision|input|answer|go-ahead)\b|\byour (call|decision|go-ahead|yes|answer|sign-off)\b|\b(until|unless) you (say|run|release|pick|approve|tell|confirm|log|answer|reply|give|choose)\b|\bonce you(['’]re| are) (happy|ready|done)\b|\bonce you(['’]ve| have)? (chosen|picked|decided|approved|tested|played|say|said|pick|choose|log|approve|confirm)\b|\bwhen you(['’]re| are) (ready|done|happy)\b|\bwhen you want\b|\bi (still )?need to know\b|\bup to you\b|\byou can (now|restart|test|play|try)\b|\b(free|ready) for you to\b|\brun this\b|^send me\b|\bis ready (at|for you)\b|^ready to (pose|test|play|review|try)\b|\bplease (type|run|press|open|click|try|test|play|restart|check|tell|send|close|log|confirm)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Ask();

    [GeneratedRegex(@"^(please|refresh|press|open|pick|choose|try|play|restart|save|click|approve|go ahead|just press|just play|say go|log in|test (it|this|that)|run the game)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Imperative();

    [GeneratedRegex(@"\b(?:rather than|instead of|without|not)\s+waiting\b|^if\b(?!\s+you\s+(haven['’]?t|have not|change)\b)|^in case\b|^when (?!you)|\bnothing (is )?waiting on you\b|\b(tell me|let me know) if\b|\bi['’]?ll (tell|let) you\b|\]\(|\b(whose|agents? that|no agent)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Soft();

    [GeneratedRegex(@"\b(don['’]?t|do not) need to do anything\b|\bnothing (for you|you need) to do\b|\bno action (is )?needed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoneNeeded();

    [GeneratedRegex(@"(?<=[.!:?])\s+")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\b(you|your|should|shall|want|i|me|we)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Addressed();

    [GeneratedRegex(@"\b(you|your|should|shall|want)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressedToTheUser();

    /// <summary>The way an agent asks itself something it then answers ("Why does it fail? Because the path is wrong."). "Which one, A or B?" has no such opening.</summary>
    [GeneratedRegex(@"^(why|what|how|where|when|who|whose|is|are|was|were|does|do|did|has|have|can|could|will|would)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SelfAnswering();

    /// <summary>
    /// How far from the end "I" or "we" still make a question one for the user. Further up, "Did I break the normal build?" is the agent
    /// going through its own work, and agents follow a real question with their reason or recommendation, so it is rarely the last thing they say.
    /// </summary>
    private const int QUESTION_SENTENCES = 4;

    /// <summary>A message asks the user something when one of its last two lines, outside code, holds a sentence ending in a question mark.</summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var lines = new List<string>();
        var inCode = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (!inCode && line.Length > 0)
            {
                lines.Add(line);
            }
        }

        for (var i = lines.Count - 1; i >= Math.Max(0, lines.Count - 2); i--)
        {
            if (lines[i].TrimEnd(DECORATION).EndsWith('?'))
            {
                return lines[i].Trim(DECORATION);
            }

            var asked = SentenceEnd().Split(lines[i]).LastOrDefault(IsQuestionToTheUser);
            if (asked is not null)
            {
                return asked.Trim(DECORATION);
            }
        }

        return null;
    }

    /// <summary>With more text after it, a question may be one the agent answers itself ("Why does it fail? Because the path is wrong."), so it must address the user.</summary>
    private static bool IsQuestionToTheUser(string sentence)
    {
        return sentence.TrimEnd(DECORATION).EndsWith('?') && Addressed().IsMatch(sentence);
    }

    /// <summary>
    /// The sentence in which a message leaves the next step to the user with no question mark: "Refresh the page", "Your call:",
    /// "nothing happens until you run this". Most turns that wait on the user end like this, so a miss here shows a waiting
    /// agent as done. The pattern comes from real turn endings; add a missed sentence to the tests in <c>StateTests</c> and widen it.
    /// </summary>
    public static string? FindRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var sentences = Sentences(text);
        if (sentences.Exists(s => NoneNeeded().IsMatch(s)))
        {
            return null;
        }

        for (var i = sentences.Count - 1; i >= 0; i--)
        {
            var sentence = sentences[i];
            if (IsRealQuestion(sentence, i >= sentences.Count - QUESTION_SENTENCES) || (!Soft().IsMatch(sentence) && (Ask().IsMatch(sentence) || Imperative().IsMatch(sentence))))
            {
                return sentence;
            }
        }

        return null;
    }

    /// <summary>
    /// A question anywhere in a last message is the agent waiting, even with a list of options under it or a long report after it.
    /// Only one the agent asks itself and answers does not count, and a lone "?" is a label, not a question.
    /// </summary>
    private static bool IsRealQuestion(string sentence, bool isNearTheEnd)
    {
        var plain = sentence.TrimEnd(DECORATION);
        if (!plain.EndsWith('?') || plain.EndsWith(" ?", StringComparison.Ordinal))
        {
            return false;
        }

        return AddressedToTheUser().IsMatch(sentence) || !SelfAnswering().IsMatch(sentence) || (isNearTheEnd && Addressed().IsMatch(sentence));
    }

    private static List<string> Sentences(string text)
    {
        var sentences = new List<string>();
        var inCode = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode || line.Length == 0 || line.StartsWith('|'))
            {
                continue;
            }

            var plain = line.TrimStart('-', '*', '>', '#', ' ').Replace("**", "").Replace("`", "");
            sentences.AddRange(SentenceEnd().Split(plain).Select(s => s.Trim()).Where(s => s.Length > 0));
        }

        return sentences;
    }
}
