using System.Text;
using System.Text.Json;

namespace Devlooped;

public sealed class FormatFilter : JqFilter
{
    public static readonly HashSet<string> FormatNames = new(StringComparer.Ordinal)
    {
        "text", "json", "html", "uri", "urid", "csv", "tsv", "sh", "base64", "base64d"
    };

    private readonly string formatName;

    public FormatFilter(string formatName)
    {
        this.formatName = formatName;
    }

    public static bool IsFormat(string name) => FormatNames.Contains(name);

    public override IEnumerable<JsonElement> Evaluate(JsonElement input, JqEnvironment env)
    {
        yield return CreateStringElement(FormatValue(formatName, input));
    }

    // Converts a JsonElement to a string by applying the named format.
    public static string FormatValue(string formatName, JsonElement input)
    {
        return formatName switch
        {
            "text" => FormatText(input),
            "json" => JsonSerializer.Serialize(input),
            "html" => FormatHtml(input),
            "uri" => FormatUri(input),
            "urid" => FormatUrid(input),
            "csv" => FormatCsv(input),
            "tsv" => FormatTsv(input),
            "sh" => FormatSh(input),
            "base64" => FormatBase64(input),
            "base64d" => FormatBase64d(input),
            _ => throw new JqException($"Unknown format '@{formatName}'.")
        };
    }

    private static string FormatText(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return input.GetString() ?? string.Empty;
        return JsonSerializer.Serialize(input);
    }

    private static string FormatHtml(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"string ({GetValueText(input)}) and {GetTypeName(input)} ({GetValueText(input)}) cannot be iterated over");
        var str = input.GetString() ?? string.Empty;
        // Order matters: & must be escaped first
        return str
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("'", "&apos;")
            .Replace("\"", "&quot;");
    }

    private static string FormatUri(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not valid in a URI");
        var str = input.GetString() ?? string.Empty;
        // Percent-encode everything except RFC 3986 unreserved characters: A-Z a-z 0-9 - _ . ~
        var bytes = Encoding.UTF8.GetBytes(str);
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') ||
                b == '-' || b == '_' || b == '.' || b == '~')
                sb.Append((char)b);
            else
                sb.Append($"%{b:X2}");
        }
        return sb.ToString();
    }

    private static string FormatUrid(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) is not a valid URI-encoded string");
        var str = input.GetString() ?? string.Empty;
        return Uri.UnescapeDataString(str);
    }

    private static string FormatCsv(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be csv-formatted, only arrays");
        var sb = new StringBuilder();
        var first = true;
        foreach (var element in input.EnumerateArray())
        {
            if (!first) sb.Append(',');
            first = false;

            if (element.ValueKind == JsonValueKind.String)
            {
                var s = element.GetString() ?? string.Empty;
                sb.Append('"');
                sb.Append(s.Replace("\"", "\"\""));
                sb.Append('"');
            }
            else if (element.ValueKind == JsonValueKind.Null)
            {
                // null → empty field
            }
            else if (element.ValueKind == JsonValueKind.Number)
            {
                sb.Append(FormatText(element));
            }
            else if (element.ValueKind == JsonValueKind.True)
            {
                sb.Append("true");
            }
            else if (element.ValueKind == JsonValueKind.False)
            {
                sb.Append("false");
            }
            else
            {
                throw new JqException($"CSV does not support {GetTypeName(element)} values");
            }
        }
        return sb.ToString();
    }

    private static string FormatTsv(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be tsv-formatted, only arrays");
        var sb = new StringBuilder();
        var first = true;
        foreach (var element in input.EnumerateArray())
        {
            if (!first) sb.Append('\t');
            first = false;

            string str;
            if (element.ValueKind == JsonValueKind.String)
                str = element.GetString() ?? string.Empty;
            else if (element.ValueKind == JsonValueKind.Null)
                str = string.Empty;
            else
                str = FormatText(element);

            // Backslash must be escaped first to avoid double-escaping
            sb.Append(str
                .Replace("\\", "\\\\")
                .Replace("\t", "\\t")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r"));
        }
        return sb.ToString();
    }

    private static string FormatSh(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var element in input.EnumerateArray())
                parts.Add(ShQuote(FormatText(element)));
            return string.Join(" ", parts);
        }

        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be sh-quoted");
        return ShQuote(input.GetString() ?? string.Empty);
    }

    private static string ShQuote(string s)
    {
        // Wrap in single quotes, escape any single quotes as '\''
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    private static string FormatBase64(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be base64-encoded");
        var str = input.GetString() ?? string.Empty;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(str));
    }

    private static string FormatBase64d(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.String)
            throw new JqException($"{GetTypeName(input)} ({GetValueText(input)}) cannot be base64-decoded");
        var str = input.GetString() ?? string.Empty;
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(str));
        }
        catch (FormatException ex)
        {
            throw new JqException($"Invalid base64 string: {ex.Message}");
        }
    }
}
