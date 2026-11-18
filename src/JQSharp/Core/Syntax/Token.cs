namespace Devlooped.Core.Syntax;

/// <summary>The kinds of tokens of jqlite (spec §2).</summary>
enum TokenKind
{
    /// <summary>The end of the source.</summary>
    Eof,
    /// <summary>A lexical error; <see cref="Token.Text"/> is the message. Always the last token.</summary>
    Error,

    /// <summary><c>name</c> or <c>module::name</c>; <see cref="Token.Text"/> is the name as written.</summary>
    Ident,
    /// <summary><c>.name</c>; <see cref="Token.Text"/> is the name without the dot.</summary>
    Field,
    /// <summary><c>$name</c> or <c>$module::name</c>; <see cref="Token.Text"/> is the name without <c>$</c>.</summary>
    Variable,
    /// <summary><c>@name</c>; <see cref="Token.Text"/> is the name without <c>@</c>.</summary>
    Format,
    /// <summary>A number literal; <see cref="Token.Number"/> is its value, <see cref="Token.Text"/> its source text.</summary>
    Number,

    // String literals. Text is the decoded literal text of the piece. A string without interpolation
    // is one String token; "a\(x)b\(y)c" is StringStart("a"), the tokens of x, StringMiddle("b"),
    // the tokens of y, StringEnd("c"). StringMiddle and StringEnd are at the ')' that closes the
    // interpolation.
    String,
    StringStart,
    StringMiddle,
    StringEnd,

    // Keywords (the list is Token.Keywords).
    Def,
    As,
    If,
    Then,
    Elif,
    Else,
    End,
    Reduce,
    Foreach,
    Try,
    Catch,
    Label,
    Break,
    Import,
    Include,
    Module,
    And,
    Or,

    // Punctuation.
    Dot,
    DotDot,
    LeftBracket,
    RightBracket,
    LeftBrace,
    RightBrace,
    LeftParen,
    RightParen,
    Pipe,
    Comma,
    Colon,
    Semicolon,
    Question,
    QuestionAlt,
    Assign,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,
    PercentAssign,
    Alt,
    AltAssign,
    PipeAssign,
}

/// <summary>
/// A token with the position of its first code point (1-based line and column; columns count
/// code points). A class, not a struct: the parser passes tokens around, and copies of a struct
/// this size would grow its stack frames.
/// </summary>
sealed record Token(TokenKind Kind, string Text, double Number, int Line, int Column)
{
    /// <summary>The keywords of jqlite: the only place they are listed.</summary>
    public static readonly (string Word, TokenKind Kind)[] Keywords =
    [
        ("def", TokenKind.Def),
        ("as", TokenKind.As),
        ("if", TokenKind.If),
        ("then", TokenKind.Then),
        ("elif", TokenKind.Elif),
        ("else", TokenKind.Else),
        ("end", TokenKind.End),
        ("reduce", TokenKind.Reduce),
        ("foreach", TokenKind.Foreach),
        ("try", TokenKind.Try),
        ("catch", TokenKind.Catch),
        ("label", TokenKind.Label),
        ("break", TokenKind.Break),
        ("import", TokenKind.Import),
        ("include", TokenKind.Include),
        ("module", TokenKind.Module),
        ("and", TokenKind.And),
        ("or", TokenKind.Or),
    ];

    /// <summary>The keyword kind of <paramref name="word"/>, or <see cref="TokenKind.Ident"/>.</summary>
    public static TokenKind KeywordKind(string word)
    {
        foreach (var (keyword, kind) in Keywords)
        {
            if (keyword == word)
                return kind;
        }

        return TokenKind.Ident;
    }

    public static bool IsKeyword(TokenKind kind) => kind is >= TokenKind.Def and <= TokenKind.Or;

    /// <summary>The token as the parser's messages show it: <c>'|='</c>, <c>end of program</c>, <c>'.name'</c>.</summary>
    public string Describe() => Kind switch
    {
        TokenKind.Eof => "end of program",
        TokenKind.Error => Text,
        TokenKind.Field => $"'.{Text}'",
        TokenKind.Variable => $"'${Text}'",
        TokenKind.Format => $"'@{Text}'",
        TokenKind.String or TokenKind.StringStart => "string",
        TokenKind.StringMiddle or TokenKind.StringEnd => "')'",
        _ => $"'{Text}'",
    };
}
