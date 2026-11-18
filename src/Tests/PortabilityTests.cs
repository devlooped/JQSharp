using System.Text;
using System.Text.RegularExpressions;

namespace Devlooped.Tests;

/// <summary>
/// The portability rules of AGENTS.md as tests: the core (src/JQSharp/Core) must read the same in
/// every language, so a host feature in its code fails here, naming the file and the line. Comments
/// and string literals are not code and are not matched. Where a rule has exceptions, they are
/// listed by file with the reason: a new use anywhere else must be justified by adding it here.
/// </summary>
public class PortabilityTests
{
    static readonly string Root = FindRoot();

    /// <summary>Rule name, pattern on code, and the files (relative to Core) allowed to match, with the reason.</summary>
    static readonly (string Name, string Pattern, Dictionary<string, string> Allowed)[] CoreRules =
    [
        ("System.Text.Json", @"System\s*\.\s*Text\s*\.\s*Json", []),
        ("yield", @"\byield\s+(return|break)\b", []),
        ("System.Linq", @"System\s*\.\s*Linq", []),
        ("System.Text.RegularExpressions", @"System\s*\.\s*Text\s*\.\s*RegularExpressions|\bRegex(Options)?\b", new()
        {
            ["Builtins/RegexEngine.cs"] = "the regex engine: the one place the host regex runs",
        }),
        ("System.Reflection", @"System\s*\.\s*Reflection|\.\s*Assembly\b|\bGetManifestResource\w*|\bActivator\b|\b(Method|Property|Field|Type)Info\b", new()
        {
            ["Binding/Prelude.cs"] = "reads the embedded prelude: typeof(Prelude).Assembly.GetManifestResourceStream",
        }),
        ("DateTime, DateTimeOffset, TimeZoneInfo, TimeProvider", @"\b(DateTime|DateTimeOffset|TimeZoneInfo|TimeProvider)\b", []),
        ("culture", @"System\s*\.\s*Globalization|\bCultureInfo\b", new()
        {
            ["Values/ValueText.cs"] = "invariant-culture number text: shortest digits and decimal parsing",
        }),
        ("throw", @"\bthrow\b", new()
        {
            ["Binding/Prelude.cs"] = "the embedded prelude does not bind or is missing: a build defect",
            ["Binding/Resolver.cs"] = "a node kind the resolver does not know: unreachable",
            ["Builtins/NativeTable.cs"] = "start-up checks of the registrations against the catalogue",
            ["Values/JqArray.cs"] = "index guards of the host caller",
            ["Values/JqArrayBuilder.cs"] = "index guard, and use after Build(): programming errors",
            ["Values/JqObject.cs"] = "index guards of the host caller",
            ["Values/JqObjectBuilder.cs"] = "use after Build(): a programming error",
            ["Values/JqValue.cs"] = "the As* accessors on a value of another kind: a programming error",
        }),
        ("catch", @"\bcatch\b", new()
        {
            ["Builtins/RegexEngine.cs"] = "the host regex's exception becomes the dialect's error (one host boundary)",
            ["Runtime/StackGuard.cs"] = "carries a defect of a step from the fresh stack back to the caller's thread",
        }),
    ];

    /// <summary>
    /// Outside Core, the files that name System.Text.Json: the public API (JsonElement in signatures),
    /// the adapter, and the host glue that calls the adapter. Conversion logic is the adapter's alone.
    /// </summary>
    static readonly Dictionary<string, string> JsonFiles = new()
    {
        ["Jq.cs"] = "public API: JsonElement in signatures",
        ["JqException.cs"] = "public API: the error Value",
        ["JqExpression.cs"] = "public API: JsonElement in signatures",
        ["JqFileResolver.cs"] = "public API: JsonElement metadata of the resolver",
        ["JqOptions.cs"] = "public API: JsonElement values of the debug and stderr handlers",
        ["JqResolver.cs"] = "public API: JsonElement metadata of the resolver",
        ["JqResourceResolver.cs"] = "public API: JsonElement metadata of the resolver",
        ["Host/ResolverSource.cs"] = "hands the resolver's metadata to it as a JsonElement, converted by the adapter",
        ["Json/JsonElementAdapter.cs"] = "the adapter: the only conversion between JsonElement and JqValue",
    };

    public static TheoryData<string> CoreRuleNames
    {
        get
        {
            var names = new TheoryData<string>();
            foreach (var rule in CoreRules)
                names.Add(rule.Name);
            return names;
        }
    }

    [Theory]
    [MemberData(nameof(CoreRuleNames))]
    public void Core_does_not_use(string name)
    {
        var (_, pattern, allowed) = CoreRules.Single(rule => rule.Name == name);
        var violations = Violations(Path.Combine(Root, "src", "JQSharp", "Core"), pattern, allowed.Keys);
        Assert.True(violations.Count == 0, $"{name} is not allowed in the core outside {(allowed.Count == 0 ? "nowhere" : string.Join(", ", allowed.Keys))}:\n{string.Join('\n', violations)}");
    }

    [Fact]
    public void Core_allowances_are_still_used()
    {
        // An allowance nothing uses any more is removed, so the lists stay the truth.
        var core = Path.Combine(Root, "src", "JQSharp", "Core");
        foreach (var (name, pattern, allowed) in CoreRules)
        {
            foreach (var file in allowed.Keys)
                Assert.True(Matches(Path.Combine(core, file), pattern).Any(), $"{file} no longer uses {name}: remove it from the allowed files.");
        }
    }

    [Fact]
    public void Only_the_adapter_converts_on_System_Text_Json()
    {
        var directory = Path.Combine(Root, "src", "JQSharp");
        var project = Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Where(file => !file.StartsWith("Core/"))
            .ToList();

        var named = project.Where(file => Matches(Path.Combine(directory, file), @"System\s*\.\s*Text\s*\.\s*Json").Any()).Order().ToList();
        Assert.Equal(JsonFiles.Keys.Order(), named);

        // Parsing and writing JSON, and walking its tokens, are conversion logic.
        var conversion = @"\b(JsonSerializer|JsonDocument|JsonNode|JsonValueKind|JsonProperty|JsonReaderOptions|Utf8JsonReader|Utf8JsonWriter)\b";
        var violations = project.Where(file => file != "Json/JsonElementAdapter.cs")
            .SelectMany(file => Matches(Path.Combine(directory, file), conversion).Select(line => $"{file}:{line}"))
            .ToList();
        Assert.True(violations.Count == 0, $"Only Json/JsonElementAdapter.cs converts JSON:\n{string.Join('\n', violations)}");
    }

    [Fact]
    public void Code_is_what_remains_without_comments_and_strings()
    {
        const string source = """"
            var a = "throw"; // throw
            /* throw
               yield return */
            var b = @"he said ""catch""";
            var c = $"x {catch}"; var d = '"'; var e = "\"throw\"";
            var f = """
                throw
                """;
            throw new Exception(); /// throw
            """";
        var lines = Code(source).Split('\n');
        Assert.Equal(source.Split('\n').Length, lines.Length);
        Assert.Single(Regex.Matches(string.Join('\n', lines), @"\bthrow\b"));
        Assert.DoesNotContain("yield", string.Join('\n', lines));
        Assert.DoesNotContain("catch", string.Join('\n', lines));
        Assert.Contains("throw new Exception();", lines[^1]);
    }

    static List<string> Violations(string directory, string pattern, IEnumerable<string> allowed)
    {
        var skip = allowed.ToHashSet();
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            if (skip.Contains(relative))
                continue;
            foreach (var line in Matches(file, pattern))
                violations.Add($"  Core/{relative}:{line}");
        }
        return violations;
    }

    /// <summary>The lines of the code of a file (comments and strings aside) where the pattern matches.</summary>
    static IEnumerable<int> Matches(string file, string pattern)
    {
        var lines = Code(File.ReadAllText(file)).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], pattern))
                yield return i + 1;
        }
    }

    /// <summary>
    /// The source with comments, string literals (regular, verbatim, interpolated and raw) and
    /// character literals blanked out, line breaks kept so that line numbers stay. The holes of an
    /// interpolated string are blanked with it.
    /// </summary>
    static string Code(string source)
    {
        var text = new StringBuilder(source.Length);
        var i = 0;

        void Blank(int count)
        {
            for (var n = 0; n < count && i < source.Length; n++, i++)
                text.Append(source[i] is '\n' or '\r' ? source[i] : ' ');
        }

        while (i < source.Length)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                    Blank(1);
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                Blank(end < 0 ? source.Length - i : end + 2 - i);
            }
            else if (c == '"')
            {
                var quotes = 1;
                while (i + quotes < source.Length && source[i + quotes] == '"')
                    quotes++;
                var verbatim = text.Length > 0 && text[^1] == '@' || text.Length > 1 && text[^1] == '$' && text[^2] == '@';
                if (quotes >= 3)
                {
                    // Raw string: ends at the same number of quotes.
                    var end = source.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                    Blank(end < 0 ? source.Length - i : end + quotes - i);
                }
                else if (quotes == 2 && !verbatim)
                {
                    Blank(2);
                }
                else
                {
                    Blank(1);
                    while (i < source.Length)
                    {
                        if (source[i] == '\\' && !verbatim)
                            Blank(2);
                        else if (source[i] == '"' && verbatim && i + 1 < source.Length && source[i + 1] == '"')
                            Blank(2);
                        else if (source[i] == '"')
                        {
                            Blank(1);
                            break;
                        }
                        else
                            Blank(1);
                    }
                }
            }
            else if (c == '\'' && i + 2 < source.Length && (source[i + 1] != '\\' && source[i + 2] == '\'' || source[i + 1] == '\\'))
            {
                // Character literal: '"', '\'' or 'A'.
                Blank(1);
                while (i < source.Length && source[i] != '\'')
                    Blank(source[i] == '\\' ? 2 : 1);
                Blank(1);
            }
            else
            {
                text.Append(c);
                i++;
            }
        }

        return text.ToString();
    }

    static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "JQSharp", "Core")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("The repository root (the folder with src/JQSharp/Core) is not above the test output.");
    }
}
