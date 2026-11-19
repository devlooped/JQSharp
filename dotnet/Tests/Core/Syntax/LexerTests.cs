using JQLite.Core.Syntax;

namespace JQLite.Tests.Core.Syntax;

public class LexerTests
{
    static List<Token> Lex(string source) => Lexer.Tokenize(source);

    // Kind, text and position of each token, without the final Eof.
    static string Describe(string source)
        => string.Join(" ", Lex(source).Where(t => t.Kind != TokenKind.Eof).Select(t => $"{t.Kind}:{t.Text}@{t.Line}:{t.Column}"));

    static string Kinds(string source)
        => string.Join(" ", Lex(source).Where(t => t.Kind != TokenKind.Eof).Select(t => t.Kind.ToString()));

    static string Texts(string source)
        => string.Join(" ", Lex(source).Where(t => t.Kind != TokenKind.Eof).Select(t => t.Text));

    static string Error(string source)
    {
        var last = Lex(source)[^1];
        Assert.Equal(TokenKind.Error, last.Kind);
        return $"{last.Text} @{last.Line}:{last.Column}";
    }

    [Fact]
    public void Every_punctuation_token_longest_first()
    {
        var source = ". .. [ ] { } ( ) | , : ; ? ?// = == != < <= > >= + - * / % += -= *= /= %= // //= |=";
        Assert.Equal(
            "Dot DotDot LeftBracket RightBracket LeftBrace RightBrace LeftParen RightParen Pipe Comma Colon Semicolon Question QuestionAlt "
            + "Assign Equal NotEqual Less LessEqual Greater GreaterEqual Plus Minus Star Slash Percent "
            + "PlusAssign MinusAssign StarAssign SlashAssign PercentAssign Alt AltAssign PipeAssign",
            Kinds(source));
        Assert.Equal(source, Texts(source));
    }

    [Fact]
    public void Adjacent_punctuation_takes_the_longest_match()
    {
        Assert.Equal("QuestionAlt Assign", Kinds("?//="));
        Assert.Equal("DotDot Dot", Kinds("..."));
        Assert.Equal("Alt Slash", Kinds("///"));
        Assert.Equal("PipeAssign Assign", Kinds("|=="));
    }

    [Fact]
    public void Every_keyword()
    {
        foreach (var (word, kind) in Token.Keywords)
        {
            var token = Assert.Single(Lex(word), t => t.Kind != TokenKind.Eof);
            Assert.Equal(kind, token.Kind);
            Assert.Equal(word, token.Text);
            Assert.True(Token.IsKeyword(token.Kind));
        }

        Assert.Equal(18, Token.Keywords.Length);
    }

    [Fact]
    public void Names()
    {
        Assert.Equal("Ident:foo@1:1 Ident:a::b@1:5 Ident:_x1@1:10 Ident:null@1:14 Ident:not@1:19", Describe("foo a::b _x1 null not"));
        Assert.Equal("Field:a@1:1 Field:end@1:3 Field:e0@1:7 Field:_@1:10", Describe(".a.end.e0._"));
        Assert.Equal("Variable:x@1:1 Variable:then@1:4 Variable:a::b@1:10 Variable:__loc__@1:16", Describe("$x $then $a::b $__loc__"));
        Assert.Equal("Format:base64@1:1 Format:base64d@1:9 Format:1x@1:18", Describe("@base64 @base64d @1x"));
    }

    [Fact]
    public void A_qualified_name_may_have_several_double_colons()
    {
        Assert.Equal("Ident", Kinds("a::b::c"));
        Assert.Equal("a::b::c", Texts("a::b::c"));
        Assert.Equal("Variable", Kinds("$a::b::c"));
        Assert.Equal("a : :", Texts("a::"));
        Assert.Equal("Field Colon Colon Ident", Kinds(".a::b"));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("42", 42)]
    [InlineData(".5", 0.5)]
    [InlineData("1.", 1)]
    [InlineData("1.5", 1.5)]
    [InlineData("1e3", 1000)]
    [InlineData("1E+3", 1000)]
    [InlineData("1.e3", 1000)]
    [InlineData(".5e1", 5)]
    [InlineData("25e-1", 2.5)]
    [InlineData("0.1", 0.1)]
    [InlineData("1e1000", double.PositiveInfinity)]
    [InlineData("1e-1000", 0)]
    [InlineData("100000000000000000000000000001", 1e29)]
    public void Numbers(string source, double value)
    {
        var token = Assert.Single(Lex(source), t => t.Kind != TokenKind.Eof);
        Assert.Equal(TokenKind.Number, token.Kind);
        Assert.Equal(value, token.Number);
        Assert.Equal(source, token.Text);
    }

    [Fact]
    public void Number_boundaries()
    {
        Assert.Equal("Field", Kinds(".e0"));
        Assert.Equal("Number Ident", Kinds("1e"));
        Assert.Equal("Number Ident Plus", Kinds("1e+"));
        Assert.Equal("1.5 .3", Texts("1.5.3"));
        Assert.Equal("DotDot Number", Kinds("..5"));
        Assert.Equal("Number Ident", Kinds("1x"));
    }

    [Fact]
    public void Positions_across_line_breaks_and_non_BMP_characters()
    {
        // "😀😀" is 4 code points; columns count code points, not UTF-16 units.
        Assert.Equal("String:😀😀@1:1 Plus:+@1:6 Number:1@2:3 Plus:+@3:1 Field:a@3:3", Describe("\"😀😀\" +\n  1\r\n+ .a"));
        Assert.Equal("String:a\nb@1:1 Ident:x@2:4", Describe("\"a\nb\" x"));
    }

    [Fact]
    public void A_lone_CR_is_white_space_not_a_line_break()
        => Assert.Equal("Number:1@1:1 Plus:+@1:3 Number:2@1:5", Describe("1\r+\r2"));

    [Fact]
    public void Comments()
    {
        Assert.Equal("Number:1@1:1 Plus:+@2:1 Number:2@2:3", Describe("1 # a comment\n+ 2 # another"));
        // An odd number of backslashes at the end of the line continues the comment.
        Assert.Equal("1 + 10", Texts("1 # c \\\n+ 1\n+ 10"));
        Assert.Equal("1 + 1 + 10", Texts("1 # c \\\\\n+ 1\n+ 10"));
        Assert.Equal("1 + 10", Texts("1 # c \\\\\\\n+ 1\n+ 10"));
        Assert.Equal("1 + 10", Texts("1 # c \\\r\n+ 1\r\n+ 10"));
        Assert.Equal("1 + 1 + 10", Texts("1 # c \\ x\n+ 1\n+ 10"));
        // As in jq, a lone CR does not end a comment.
        Assert.Equal("1", Texts("1 # c\r+ 1"));
        Assert.Equal("1 + 10", Texts("1 # c \\\r+ 1\n+ 10"));
    }

    [Theory]
    [InlineData("\"abc\"", "abc")]
    [InlineData("\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"", "\"\\/\b\f\n\r\t")]
    [InlineData("\"\\u00e9\\u00C9\"", "éÉ")]
    [InlineData("\"\\uD83D\\uDE00\"", "😀")]
    [InlineData("\"\\ud83d x\"", "\uFFFD x")]
    [InlineData("\"\\ude00\"", "\uFFFD")]
    [InlineData("\"\\uDE00\\uD83D\"", "\uFFFD\uFFFD")]
    [InlineData("\"\\uD83D\\u0041\"", "\uFFFDA")]
    [InlineData("\"\\uD83D\\uD83D\\uDE00\"", "\uFFFD😀")]
    [InlineData("\"\\u12345\"", "\u12345")]
    [InlineData("\"é\t\"", "é\t")]
    public void String_escapes(string source, string text)
    {
        var token = Assert.Single(Lex(source), t => t.Kind != TokenKind.Eof);
        Assert.Equal(TokenKind.String, token.Kind);
        Assert.Equal(text, token.Text);
    }

    // Lone surrogates in the source text itself (InlineData cannot carry them: xUnit re-encodes them).
    [Fact]
    public void A_lone_surrogate_in_the_source_is_U_FFFD()
    {
        var token = Assert.Single(Lex("\"a\uD800b\uDC00\""), t => t.Kind != TokenKind.Eof);
        Assert.Equal("a\uFFFDb\uFFFD", token.Text);
        Assert.Equal("Invalid character '\uFFFD' @1:2", Error("a\uD800"));
        // A lone surrogate is one column.
        Assert.Equal("String:\uFFFD@1:1 Plus:+@1:5", Describe("\"\uD800\" +"));
    }

    [Fact]
    public void Interpolation_is_split_into_pieces()
    {
        Assert.Equal(
            "StringStart:a@1:1 Number:1@1:5 Plus:+@1:7 LeftParen:(@1:9 Number:2@1:10 RightParen:)@1:11 StringMiddle:b@1:12 String:c@1:16 StringEnd:d@1:19",
            Describe("\"a\\(1 + (2))b\\(\"c\")d\""));
    }

    [Fact]
    public void Interpolation_nests()
    {
        Assert.Equal("StringStart StringStart String StringEnd StringEnd", Kinds("\"a\\(\"b\\(\"c\")\")\""));
        Assert.Equal("a b c", Texts("\"a\\(\"b\\(\"c\")\")\"").Trim());
    }

    [Fact]
    public void A_parenthesis_outside_interpolation_is_punctuation()
        => Assert.Equal("RightParen", Kinds(")"));

    [Theory]
    [InlineData("\"abc", "Unterminated string @1:1")]
    [InlineData("1 + \"a\\(1", "Unterminated string @1:5")]
    [InlineData("\"a\\(\"b\" + (1", "Unterminated string @1:1")]
    [InlineData("\"a\\", "Unterminated string @1:1")]
    [InlineData("\"a\\qb\"", "Invalid escape @1:3")]
    [InlineData("\"x\"\n \"\\u12\"", "Invalid escape @2:3")]
    [InlineData("\"\\u12G4\"", "Invalid escape @1:2")]
    [InlineData("\"\\u\"", "Invalid escape @1:2")]
    [InlineData("1 $ x", "Expected a variable name after '$' @1:3")]
    [InlineData("@", "Expected a format name after '@' @1:1")]
    [InlineData("{aé: 1}", "Invalid character 'é' @1:3")]
    [InlineData("😀", "Invalid character '😀' @1:1")]
    [InlineData("1\f+2", "Invalid character U+000C @1:2")]
    [InlineData("1 ! 2", "Invalid character '!' @1:3")]
    public void Errors(string source, string expected) => Assert.Equal(expected, Error(source));

    [Fact]
    public void An_error_is_the_last_token()
    {
        var tokens = Lex("1 + \"a\\q\" + 2");
        Assert.Equal("Number Plus Error", string.Join(" ", tokens.Select(t => t.Kind)));
    }

    [Fact]
    public void Empty_source_is_eof()
    {
        var token = Assert.Single(Lex("  # nothing\n"));
        Assert.Equal(TokenKind.Eof, token.Kind);
        Assert.Equal((2, 1), (token.Line, token.Column));
    }
}