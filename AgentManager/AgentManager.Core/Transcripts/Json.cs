using System.Globalization;
using System.Text.Json;

namespace AgentManager.Core.Transcripts;

public static class Json
{
    public static string? TopLevelString(ReadOnlySpan<byte> json, ReadOnlySpan<byte> property)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isMatch = reader.ValueTextEquals(property);
                if (!reader.Read())
                {
                    return null;
                }

                if (isMatch)
                {
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    public static string? Str(this JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    public static long Num(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var whole) => whole,
            JsonValueKind.Number => (long)value.GetDouble(),
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
    }

    public static double Real(this JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return 0;
    }

    public static bool Flag(this JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    }

    public static JsonElement Obj(this JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value))
        {
            return value;
        }

        return default;
    }

    public static IEnumerable<JsonElement> Items(this JsonElement element, string property)
    {
        var array = element.Obj(property);
        return array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : [];
    }

    public static DateTimeOffset? Time(this JsonElement element, string property)
    {
        var text = element.Str(property);
        if (text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToLocalTime();
        }

        return null;
    }

    public static DateTimeOffset FromUnixSeconds(double seconds)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)).ToLocalTime();
    }

    public static DateTimeOffset FromUnixMilliseconds(long milliseconds)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).ToLocalTime();
    }

    public static string OneLine(string? text, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var span = text.AsSpan().Trim();
        var newline = span.IndexOfAny('\r', '\n');
        var first = newline >= 0 ? span[..newline].Trim() : span;
        var line = first.Length > max ? string.Concat(first[..(max - 1)], "…") : first.ToString();
        return line;
    }
}
