using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

public class JqFormatStringTests
{
    [Fact]
    public void Base64_encodes_string()
        => Assert.Equal([JsonSerializer.Serialize("VGhpcyBpcyBhIG1lc3NhZ2U=")], EvaluateToStrings("\"This is a message\" | @base64", "null"));

    [Fact]
    public void Base64_encodes_empty_string()
        => Assert.Equal([JsonSerializer.Serialize("")], EvaluateToStrings("\"\" | @base64", "null"));

    [Fact]
    public void Base64d_decodes_string()
        => Assert.Equal([JsonSerializer.Serialize("This is a message")], EvaluateToStrings("\"VGhpcyBpcyBhIG1lc3NhZ2U=\" | @base64d", "null"));

    [Fact]
    public void Base64d_decodes_empty_string()
        => Assert.Equal([JsonSerializer.Serialize("")], EvaluateToStrings("\"\" | @base64d", "null"));

    [Fact]
    public void Base64_roundtrip_preserves_original()
        => Assert.Equal([JsonSerializer.Serialize("hello")], EvaluateToStrings("\"hello\" | @base64 | @base64d", "null"));

    [Fact]
    public void Html_escapes_lt_character()
        => Assert.Equal([JsonSerializer.Serialize("This works if x &lt; y")], EvaluateToStrings("\"This works if x < y\" | @html", "null"));

    [Fact]
    public void Html_escapes_all_supported_symbols()
        => Assert.Equal([JsonSerializer.Serialize("&lt;&gt;&amp;&apos;&quot;")], EvaluateToStrings("\"<>&'\\\"\" | @html", "null"));

    [Fact]
    public void Html_leaves_safe_text_unchanged()
        => Assert.Equal([JsonSerializer.Serialize("safe text")], EvaluateToStrings("\"safe text\" | @html", "null"));

    [Fact]
    public void Uri_encodes_reserved_characters()
        => Assert.Equal([JsonSerializer.Serialize("what%20is%20jq%3F")], EvaluateToStrings("\"what is jq?\" | @uri", "null"));

    [Fact]
    public void Uri_keeps_unreserved_characters()
        => Assert.Equal([JsonSerializer.Serialize("abc-_.~")], EvaluateToStrings("\"abc-_.~\" | @uri", "null"));

    [Fact]
    public void Urid_decodes_percent_encoding()
        => Assert.Equal([JsonSerializer.Serialize("what is jq?")], EvaluateToStrings("\"what%20is%20jq%3F\" | @urid", "null"));

    [Fact]
    public void Uri_roundtrip_preserves_original()
        => Assert.Equal([JsonSerializer.Serialize("hello world")], EvaluateToStrings("\"hello world\" | @uri | @urid", "null"));

    [Fact]
    public void Csv_formats_mixed_array_values()
        => Assert.Equal([JsonSerializer.Serialize("1,\"two\",,true")], EvaluateToStrings("[1,\"two\",null,true] | @csv", "null"));

    [Fact]
    public void Csv_escapes_quotes_inside_string_fields()
        => Assert.Equal([JsonSerializer.Serialize("\"a\"\"b\"")], EvaluateToStrings("[\"a\\\"b\"] | @csv", "null"));

    [Fact]
    public void Tsv_escapes_tab_characters_inside_values()
        => Assert.Equal([JsonSerializer.Serialize("1\thello\\tworld")], EvaluateToStrings("[1,\"hello\\tworld\"] | @tsv", "null"));

    [Fact]
    public void Tsv_joins_array_values_with_tabs()
        => Assert.Equal([JsonSerializer.Serialize("a\tb\tc")], EvaluateToStrings("[\"a\",\"b\",\"c\"] | @tsv", "null"));

    [Fact]
    public void Sh_quotes_single_string()
        => Assert.Equal([JsonSerializer.Serialize("'test'")], EvaluateToStrings("\"test\" | @sh", "null"));

    [Fact]
    public void Sh_escapes_embedded_apostrophes()
        => Assert.Equal([JsonSerializer.Serialize("'O'\\''Hara'\\''s Ale'")], EvaluateToStrings("\"O'Hara's Ale\" | @sh", "null"));

    [Fact]
    public void Sh_formats_array_as_space_separated_quoted_values()
        => Assert.Equal([JsonSerializer.Serialize("'a' 'b c'")], EvaluateToStrings("[\"a\",\"b c\"] | @sh", "null"));

    [Fact]
    public void Json_formats_number()
        => Assert.Equal([JsonSerializer.Serialize("42")], EvaluateToStrings("42 | @json", "null"));

    [Fact]
    public void Json_formats_object_compactly()
        => Assert.Equal([JsonSerializer.Serialize("{\"a\":1}")], EvaluateToStrings("{\"a\":1} | @json", "null"));

    [Fact]
    public void Json_matches_tojson_output()
        => Assert.Equal(EvaluateToStrings("42 | tojson", "null"), EvaluateToStrings("42 | @json", "null"));

    [Fact]
    public void Text_preserves_string_value()
        => Assert.Equal([JsonSerializer.Serialize("hello")], EvaluateToStrings("\"hello\" | @text", "null"));

    [Fact]
    public void Text_converts_number_to_text()
        => Assert.Equal([JsonSerializer.Serialize("42")], EvaluateToStrings("42 | @text", "null"));

    [Fact]
    public void Html_format_with_interpolation_escapes_only_interpolation()
        => Assert.Equal([JsonSerializer.Serialize("<b>&lt;script&gt;</b>")], EvaluateToStrings("@html \"<b>\\(.)</b>\"", "\"<script>\""));

    [Fact]
    public void Uri_format_with_interpolation_escapes_only_interpolation()
        => Assert.Equal([JsonSerializer.Serialize("https://example.com/search?q=hello%20world")], EvaluateToStrings("@uri \"https://example.com/search?q=\\(.)\"", "\"hello world\""));

    [Fact]
    public void Sh_format_with_interpolation_quotes_interpolated_value()
        => Assert.Equal([JsonSerializer.Serialize("echo 'hello'")], EvaluateToStrings("@sh \"echo \\(.)\"", "\"hello\""));

    [Fact]
    public void Format_string_literals_are_not_escaped()
        => Assert.Equal([JsonSerializer.Serialize("a<b>x</b>")], EvaluateToStrings("@html \"a<b>\\(.)</b>\"", "\"x\""));

    [Fact]
    public void Html_throws_for_non_string_input()
        => Assert.Throws<JqException>(() => Evaluate("42 | @html", "null"));

    [Fact]
    public void Uri_throws_for_non_string_input()
        => Assert.Throws<JqException>(() => Evaluate("42 | @uri", "null"));

    [Fact]
    public void Csv_throws_for_non_array_input()
        => Assert.Throws<JqException>(() => Evaluate("\"not an array\" | @csv", "null"));

    [Fact]
    public void Tsv_throws_for_non_array_input()
        => Assert.Throws<JqException>(() => Evaluate("\"not an array\" | @tsv", "null"));

    static string[] EvaluateToStrings(string expression, string inputJson)
        => Evaluate(expression, inputJson)
            .Select(static element => JsonSerializer.Serialize(element))
            .ToArray();

    static JsonElement[] Evaluate(string expression, string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        return Jq.Evaluate(expression, document.RootElement)
            .ToArray();
    }
}
