using System.Text.Json;
using Devlooped;

namespace Devlooped.Tests;

/// <summary>The .NET boundary: options, exceptions, streaming, variables and value conversion.</summary>
public class JqBoundaryTests
{
    static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    static string[] Texts(IEnumerable<JsonElement> results) => [.. results.Select(static e => e.GetRawText())];

    [Fact]
    public void Options_environment_is_ENV_and_env()
    {
        var options = new JqOptions { Environment = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" } };
        Assert.Equal(["""{"B":"2","A":"1"}""", "\"1\""], Texts(Jq.Parse("$ENV, env.A", options).Evaluate(Json("null"))));
    }

    [Fact]
    public void Without_options_ENV_is_the_process_environment()
    {
        var name = System.Environment.GetEnvironmentVariables().Keys.Cast<string>().First(static key => key.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'));
        var value = System.Environment.GetEnvironmentVariable(name);
        Assert.Equal([JsonSerializer.Serialize(value)], Texts(Jq.Parse($"$ENV.{name}").Evaluate(Json("null"))));
    }

    [Fact]
    public void Options_time_provider_gives_now_and_the_local_zone()
    {
        var options = new JqOptions { TimeProvider = new FixedTime(new DateTimeOffset(2015, 3, 5, 23, 51, 47, TimeSpan.Zero), TimeSpan.FromHours(-3)) };
        Assert.Equal(["1425599507", "\"2015-03-05T20:51:47 -0300\""], Texts(Jq.Parse("now, (now | strflocaltime(\"%Y-%m-%dT%H:%M:%S %z\"))", options).Evaluate(Json("null"))));
    }

    [Fact]
    public void Options_time_provider_follows_daylight_saving_time()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("dst", TimeSpan.FromHours(1), "dst", "std", "dst",
            [TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(DateTime.MinValue.Date, DateTime.MaxValue.Date, TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 1),
                TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 1))]);
        var options = new JqOptions { TimeProvider = new FixedTime(DateTimeOffset.UnixEpoch, zone) };
        // January: +01:00; July: +02:00.
        Assert.Equal(["\"+0100\"", "\"+0200\""], Texts(Jq.Parse("(1420070400, 1435708800) | strflocaltime(\"%z\")", options).Evaluate(Json("null"))));
    }

    [Fact]
    public void Options_debug_and_stderr_receive_the_values()
    {
        var debugged = new List<string>();
        var written = new List<string>();
        var options = new JqOptions { Debug = value => debugged.Add(value.GetRawText()), Stderr = value => written.Add(value.GetRawText()) };
        Assert.Equal(["1", "1", "1"], Texts(Jq.Parse("debug, debug(\"m\", {a: .}), stderr", options).Evaluate(Json("1"))));
        Assert.Equal(["1", "\"m\"", """{"a":1}"""], debugged);
        Assert.Equal(["1"], written);
    }

    [Fact]
    public void Without_handlers_debug_and_stderr_only_output_their_input()
        => Assert.Equal(["1", "1"], Texts(Jq.Evaluate("debug, stderr", Json("1"))));

    [Fact]
    public void Halt_error_without_a_code_exits_with_5()
    {
        var error = Assert.Throws<JqHaltException>(() => Jq.Evaluate("halt_error", Json("\"bye\"")));
        Assert.Equal(("bye", 5), (error.Message, error.ExitCode));
    }

    [Fact]
    public void Options_max_depth_limits_recursion()
    {
        var expression = Jq.Parse("def f: if . == 0 then 0 else (. - 1 | f) + 1 end; f", new JqOptions { MaxDepth = 10 });
        Assert.Equal(["9"], Texts(expression.Evaluate(Json("9"))));
        var error = Assert.Throws<JqException>(() => expression.Evaluate(Json("10")));
        Assert.Equal("Maximum call depth exceeded", error.Message);
    }

    [Theory]
    [InlineData("1 +", "Unexpected end of program at line 1, column 4", 1, 4)]
    [InlineData(".a |\n  foo", "foo/0 is not defined at line 2, column 3", 2, 3)]
    [InlineData(". as $x | break $x", "$*label-x is not defined at line 1, column 11", 1, 11)]
    public void Parse_errors_have_a_position(string expression, string message, int line, int column)
    {
        var error = Assert.Throws<JqParseException>(() => Jq.Parse(expression));
        Assert.Equal((message, line, column), (error.Message, error.Line, error.Column));
        Assert.Null(error.Value);
    }

    [Fact]
    public void A_string_error_is_its_message_and_value()
    {
        var error = Assert.Throws<JqException>(() => Jq.Evaluate("error(\"boom\")", Json("null")));
        Assert.Equal("boom", error.Message);
        Assert.Equal("\"boom\"", error.Value!.Value.GetRawText());
    }

    [Fact]
    public void Another_error_value_is_its_text_not_a_string()
    {
        var error = Assert.Throws<JqException>(() => Jq.Evaluate("error({a: [1, nan]})", Json("null")));
        Assert.Equal("""{"a":[1,null]} (not a string)""", error.Message);
        Assert.Equal("""{"a":[1,null]}""", error.Value!.Value.GetRawText());
    }

    [Fact]
    public void A_builtin_error_is_its_message()
    {
        var error = Assert.Throws<JqException>(() => Jq.Evaluate(". + 1", Json("\"a\"")));
        Assert.Equal("string (\"a\") and number (1) cannot be added", error.Message);
    }

    [Fact]
    public void Streaming_gives_results_as_they_come_and_stops_when_asked()
    {
        var expression = Jq.Parse("range(1000000000)");
        var seen = new List<int>();
        expression.Evaluate(Json("null"), result =>
        {
            seen.Add(result.GetInt32());
            return seen.Count < 3;
        });
        Assert.Equal([0, 1, 2], seen);
    }

    [Fact]
    public void Streaming_delivers_results_before_an_error()
    {
        var seen = new List<string>();
        var error = Assert.Throws<JqException>(() => Jq.Parse("1, 2, error(\"late\"), 3").Evaluate(Json("null"), result =>
        {
            seen.Add(result.GetRawText());
            return true;
        }));
        Assert.Equal(["1", "2"], seen);
        Assert.Equal("late", error.Message);
    }

    [Fact]
    public void An_exception_of_the_callback_propagates()
        => Assert.Throws<FormatException>(() => Jq.Parse("1, 2").Evaluate(Json("null"), _ => throw new FormatException()));

    [Fact]
    public void Halt_ends_normally()
        => Assert.Equal(["1"], Texts(Jq.Evaluate("1, halt, 2", Json("null"))));

    [Fact]
    public void Halt_error_carries_the_value_and_the_code()
    {
        var seen = new List<string>();
        var error = Assert.Throws<JqHaltException>(() => Jq.Parse("1, ({a: 1} | halt_error(5)), 2").Evaluate(Json("null"), result =>
        {
            seen.Add(result.GetRawText());
            return true;
        }));
        Assert.Equal(["1"], seen);
        Assert.Equal(5, error.ExitCode);
        Assert.Equal("""{"a":1}""", error.Value!.Value.GetRawText());
        Assert.Equal("""{"a":1} (not a string)""", error.Message);
        Assert.Equal("bye", Assert.Throws<JqHaltException>(() => Jq.Evaluate("\"bye\" | halt_error(0)", Json("null"))).Message);
    }

    [Theory]
    [InlineData("$root")]
    [InlineData("1x")]
    [InlineData("a-b")]
    [InlineData("")]
    [InlineData("ENV")]
    public void Invalid_variable_names_are_rejected_before_evaluation(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => Jq.Evaluate("error(\"evaluated\")", Json("null"), new Dictionary<string, JsonElement> { [name] = Json("1") }));
        Assert.Equal("variables", error.ParamName);
    }

    [Fact]
    public void Variables_are_converted_per_evaluation()
    {
        var expression = Jq.Parse("$x + 1");
        Assert.Equal(["2"], Texts(expression.Evaluate(Json("null"), new Dictionary<string, JsonElement> { ["x"] = Json("1") })));
        Assert.Equal(["11"], Texts(expression.Evaluate(Json("null"), new Dictionary<string, JsonElement> { ["x"] = Json("10") })));
        Assert.Equal("$x is not defined", Assert.Throws<JqException>(() => expression.Evaluate(Json("null"))).Message);
    }

    [Theory]
    // Non-finite numbers come out as jq prints them; large numbers stay doubles.
    [InlineData("[nan, infinite, -infinite]", "null", "[null,1.7976931348623157e+308,-1.7976931348623157e+308]")]
    [InlineData(".", "100000000000000000000000000001", "1e+29")]
    [InlineData(".", "1E400", "1.7976931348623157e+308")]
    [InlineData(". + 1", "9007199254740993", "9007199254740992")]
    [InlineData(".", "-0", "-0")]
    // A lone surrogate becomes U+FFFD on the way in (spec §4.1).
    [InlineData(". | explode", "\"a\\ud800b\"", "[97,65533,98]")]
    [InlineData("keys", "{\"\\udc00\":1}", "[\"\uFFFD\"]")]
    [InlineData("[(.[0] | keys), .[1]]", "[{\"\\udc00x\":1,\"\\udc00x\":2}, 3]", "[[\"\uFFFDx\"],3]")]
    [InlineData("tojson", "{\"b\":1,\"a\":2,\"b\":3}", "\"{\\\"b\\\":3,\\\"a\\\":2}\"")]
    public void Values_cross_the_boundary(string expression, string input, string expected)
    {
        var result = Assert.Single(Jq.Evaluate(expression, Json(input)));
        Assert.Equal(expected, result.GetRawText());
    }

    [Fact]
    public void Results_outlive_the_input_document()
    {
        IReadOnlyList<JsonElement> results;
        using (var document = JsonDocument.Parse("""{"a":{"b":[1,2]}}"""))
            results = Jq.Evaluate(".a, .a.b[]", document.RootElement);
        Assert.Equal(["""{"b":[1,2]}""", "1", "2"], Texts(results));
    }

    [Fact]
    public void Deep_input_and_output_convert_without_recursion()
    {
        var text = new string('[', 5000) + new string(']', 5000);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 6000 });
        string? result = null;
        var thread = new Thread(() => result = Assert.Single(Jq.Evaluate(".", document.RootElement)).GetRawText(), 256 * 1024);
        thread.Start();
        thread.Join();
        Assert.Equal(text, result);
    }

    [Fact]
    public void One_expression_evaluates_concurrently()
    {
        var expression = Jq.Parse("[.[] | . * $k] | add");
        var input = Json("[1,2,3]");
        var results = new string[64];
        Parallel.For(0, results.Length, i => results[i] = expression.Evaluate(input, new Dictionary<string, JsonElement> { ["k"] = Json(i.ToString()) })[0].GetRawText());
        for (var i = 0; i < results.Length; i++)
            Assert.Equal((6 * i).ToString(), results[i]);
    }

    sealed class FixedTime(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public FixedTime(DateTimeOffset now, TimeSpan offset)
            : this(now, TimeZoneInfo.CreateCustomTimeZone("fixed", offset, "fixed", "fixed"))
        {
        }

        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => zone;
    }
}