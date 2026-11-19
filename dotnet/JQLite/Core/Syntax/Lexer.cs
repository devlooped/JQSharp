using System.Text;
using JQLite.Core.Values;

namespace JQLite.Core.Syntax;

/// <summary>
/// Turns jqlite source text into tokens (spec §2), longest match first. Never throws: a lexical
/// error ends the list with a token of kind <see cref="TokenKind.Error"/> whose text is the message;
/// otherwise the list ends with <see cref="TokenKind.Eof"/>.
/// </summary>
/// <remarks>
/// <para>
/// String interpolation is lexed with a stack, without recursion: <c>\(</c> ends the current
/// string piece (<see cref="TokenKind.StringStart"/> or <see cref="TokenKind.StringMiddle"/>) and
/// pushes a frame; the lexer then produces the ordinary tokens of the interpolated expression,
/// counting the parentheses opened inside it; the <c>)</c> that closes the frame resumes the string
/// (<see cref="TokenKind.StringMiddle"/> or <see cref="TokenKind.StringEnd"/>). Nesting
/// (<c>"a\("b\("c")")"</c>) is any number of frames; the parser limits it (spec §11).
/// </para>
/// <para>
/// Positions: lines are separated by LF (a CR before it is white space, a lone CR is white space
/// too, as in jq); columns count code points.
/// </para>
/// </remarks>
sealed class Lexer
{
    // Punctuation, longest first where one is a prefix of another.
    static readonly (string Text, TokenKind Kind)[] Punctuation =
    [
        ("?//", TokenKind.QuestionAlt),
        ("//=", TokenKind.AltAssign),
        ("..", TokenKind.DotDot),
        ("|=", TokenKind.PipeAssign),
        ("==", TokenKind.Equal),
        ("!=", TokenKind.NotEqual),
        ("<=", TokenKind.LessEqual),
        (">=", TokenKind.GreaterEqual),
        ("+=", TokenKind.PlusAssign),
        ("-=", TokenKind.MinusAssign),
        ("*=", TokenKind.StarAssign),
        ("/=", TokenKind.SlashAssign),
        ("%=", TokenKind.PercentAssign),
        ("//", TokenKind.Alt),
        (".", TokenKind.Dot),
        ("[", TokenKind.LeftBracket),
        ("]", TokenKind.RightBracket),
        ("{", TokenKind.LeftBrace),
        ("}", TokenKind.RightBrace),
        ("(", TokenKind.LeftParen),
        (")", TokenKind.RightParen),
        ("|", TokenKind.Pipe),
        (",", TokenKind.Comma),
        (":", TokenKind.Colon),
        (";", TokenKind.Semicolon),
        ("?", TokenKind.Question),
        ("=", TokenKind.Assign),
        ("<", TokenKind.Less),
        (">", TokenKind.Greater),
        ("+", TokenKind.Plus),
        ("-", TokenKind.Minus),
        ("*", TokenKind.Star),
        ("/", TokenKind.Slash),
        ("%", TokenKind.Percent),
    ];

    readonly string source;
    readonly List<Token> tokens = [];
    // One frame per open interpolation: the parentheses opened inside it, and the position of the
    // opening quote of its string (for "Unterminated string").
    readonly List<(int Parens, int Line, int Column)> interpolations = [];
    int index;
    int line = 1;
    int column = 1;

    Lexer(string source) => this.source = source;

    /// <summary>The tokens of <paramref name="source"/>, ending with an Eof or Error token.</summary>
    public static List<Token> Tokenize(string source)
    {
        var lexer = new Lexer(source);
        while (lexer.Next())
        {
        }

        return lexer.tokens;
    }

    // Adds the next token; false after the last one (Eof or Error).
    bool Next()
    {
        SkipTrivia();
        var startLine = line;
        var startColumn = column;
        if (index >= source.Length)
        {
            if (interpolations.Count > 0)
                return Fail(SyntaxMessages.UnterminatedString, interpolations[^1].Line, interpolations[^1].Column);

            Add(TokenKind.Eof, "", startLine, startColumn);
            return false;
        }

        var c = source[index];
        if (c == '"')
        {
            Advance();
            return LexStringPiece(startLine, startColumn, startLine, startColumn, first: true);
        }

        if (IsDigit(c) || (c == '.' && IsDigit(At(index + 1))))
            return LexNumber(startLine, startColumn);

        if (IsIdentStart(c))
        {
            var name = ReadIdentifier(qualified: true);
            var kind = name.Contains(':') ? TokenKind.Ident : Token.KeywordKind(name);
            Add(kind, name, startLine, startColumn);
            return true;
        }

        if (c == '.' && IsIdentStart(At(index + 1)))
        {
            Advance();
            Add(TokenKind.Field, ReadIdentifier(qualified: false), startLine, startColumn);
            return true;
        }

        if (c == '$')
        {
            Advance();
            if (index >= source.Length || !IsIdentStart(source[index]))
                return Fail(SyntaxMessages.VariableNameExpected, startLine, startColumn);
            Add(TokenKind.Variable, ReadIdentifier(qualified: true), startLine, startColumn);
            return true;
        }

        if (c == '@')
        {
            Advance();
            var start = index;
            while (IsIdentPart(At(index)))
                Advance();
            if (index == start)
                return Fail(SyntaxMessages.FormatNameExpected, startLine, startColumn);
            Add(TokenKind.Format, source[start..index], startLine, startColumn);
            return true;
        }

        if (c == '(' && interpolations.Count > 0)
            interpolations[^1] = interpolations[^1] with { Parens = interpolations[^1].Parens + 1 };

        if (c == ')' && interpolations.Count > 0)
        {
            var frame = interpolations[^1];
            if (frame.Parens == 0)
            {
                // The ')' that closes "\(": the string resumes.
                interpolations.RemoveAt(interpolations.Count - 1);
                Advance();
                return LexStringPiece(startLine, startColumn, frame.Line, frame.Column, first: false);
            }

            interpolations[^1] = frame with { Parens = frame.Parens - 1 };
        }

        foreach (var (text, kind) in Punctuation)
        {
            if (string.CompareOrdinal(source, index, text, 0, text.Length) == 0)
            {
                for (var i = 0; i < text.Length; i++)
                    Advance();
                Add(kind, text, startLine, startColumn);
                return true;
            }
        }

        return Fail(SyntaxMessages.InvalidCharacter(CodePointAt(index)), startLine, startColumn);
    }

    // White space (space, tab, CR, LF) and comments. A comment runs to the end of the line; when an
    // odd number of '\' ends the line (before LF or CR LF), the comment goes on with the next line.
    void SkipTrivia()
    {
        while (index < source.Length)
        {
            var c = source[index];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                Advance();
                continue;
            }

            if (c != '#')
                return;

            var start = index;
            while (index < source.Length)
            {
                if (source[index] == '\n')
                {
                    var j = index - 1;
                    if (j > start && source[j] == '\r')
                        j--;
                    var backslashes = 0;
                    for (; j > start && source[j] == '\\'; j--)
                        backslashes++;
                    if (backslashes % 2 == 0)
                        break;
                }

                Advance();
            }
        }
    }

    // A string piece: after the opening quote, or after the ')' that closes an interpolation. Ends
    // at the closing quote (String or StringEnd) or at "\(" (StringStart or StringMiddle).
    bool LexStringPiece(int tokenLine, int tokenColumn, int quoteLine, int quoteColumn, bool first)
    {
        var text = new StringBuilder();
        while (true)
        {
            if (index >= source.Length)
                return Fail(SyntaxMessages.UnterminatedString, quoteLine, quoteColumn);

            var c = source[index];
            if (c == '"')
            {
                Advance();
                Add(first ? TokenKind.String : TokenKind.StringEnd, text.ToString(), tokenLine, tokenColumn);
                return true;
            }

            if (c != '\\')
            {
                CodePoints.Append(text, CodePointAt(index));
                Advance();
                continue;
            }

            var escapeLine = line;
            var escapeColumn = column;
            Advance();
            var e = At(index);
            switch (e)
            {
                case '"' or '\\' or '/':
                    text.Append(e);
                    break;
                case 'b':
                    text.Append('\b');
                    break;
                case 'f':
                    text.Append('\f');
                    break;
                case 'n':
                    text.Append('\n');
                    break;
                case 'r':
                    text.Append('\r');
                    break;
                case 't':
                    text.Append('\t');
                    break;
                case 'u':
                    var unit = HexAt(index + 1);
                    if (unit < 0)
                        return Fail(SyntaxMessages.InvalidEscape, escapeLine, escapeColumn);
                    for (var i = 0; i < 5; i++)
                        Advance();
                    var low = At(index) == '\\' && At(index + 1) == 'u' ? HexAt(index + 2) : -1;
                    if (unit is >= 0xD800 and <= 0xDBFF && low is >= 0xDC00 and <= 0xDFFF)
                    {
                        // A high surrogate escape followed by a low one: one code point.
                        CodePoints.Append(text, 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00));
                        for (var i = 0; i < 6; i++)
                            Advance();
                    }
                    else
                    {
                        // Any other surrogate escape is U+FFFD.
                        CodePoints.Append(text, unit is >= 0xD800 and <= 0xDFFF ? CodePoints.Replacement : unit);
                    }

                    continue;
                case '(':
                    Advance();
                    Add(first ? TokenKind.StringStart : TokenKind.StringMiddle, text.ToString(), tokenLine, tokenColumn);
                    interpolations.Add((0, quoteLine, quoteColumn));
                    return true;
                default:
                    return index >= source.Length
                        ? Fail(SyntaxMessages.UnterminatedString, quoteLine, quoteColumn)
                        : Fail(SyntaxMessages.InvalidEscape, escapeLine, escapeColumn);
            }

            Advance();
        }
    }

    // ([0-9]+ ('.' [0-9]*)? | '.' [0-9]+) ([eE] [+-]? [0-9]+)?
    bool LexNumber(int startLine, int startColumn)
    {
        var start = index;
        while (IsDigit(At(index)))
            Advance();
        if (At(index) == '.')
        {
            Advance();
            while (IsDigit(At(index)))
                Advance();
        }

        if (At(index) is 'e' or 'E')
        {
            var digit = At(index + 1) is '+' or '-' ? index + 2 : index + 1;
            if (IsDigit(At(digit)))
            {
                while (index < digit)
                    Advance();
                while (IsDigit(At(index)))
                    Advance();
            }
        }

        var text = source[start..index];
        // The grammar above is a subset of the numeric text of ValueText, so the conversion succeeds.
        ValueText.TryParseNumber(text, out var number);
        tokens.Add(new Token(TokenKind.Number, text, number, startLine, startColumn));
        return true;
    }

    // [A-Za-z_][A-Za-z0-9_]*, and when qualified, any number of "::" each followed by such a name
    // (jq 1.8.2 accepts a::b::c as one name: an import alias may itself be qualified).
    string ReadIdentifier(bool qualified)
    {
        var start = index;
        while (IsIdentPart(At(index)))
            Advance();
        while (qualified && At(index) == ':' && At(index + 1) == ':' && IsIdentStart(At(index + 2)))
        {
            Advance();
            Advance();
            while (IsIdentPart(At(index)))
                Advance();
        }

        return source[start..index];
    }

    // The four hex digits at offset, as a number, or -1.
    int HexAt(int offset)
    {
        var value = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = At(offset + i);
            var digit = c is >= '0' and <= '9' ? c - '0'
                : c is >= 'a' and <= 'f' ? c - 'a' + 10
                : c is >= 'A' and <= 'F' ? c - 'A' + 10
                : -1;
            if (digit < 0)
                return -1;
            value = value * 16 + digit;
        }

        return value;
    }

    // The code point at offset: a surrogate pair is one code point, a lone surrogate is U+FFFD.
    int CodePointAt(int offset)
    {
        var c = source[offset];
        if (char.IsHighSurrogate(c) && offset + 1 < source.Length && char.IsLowSurrogate(source[offset + 1]))
            return char.ConvertToUtf32(c, source[offset + 1]);
        return char.IsSurrogate(c) ? CodePoints.Replacement : c;
    }

    // Moves past one code point, keeping line and column.
    void Advance()
    {
        var c = source[index];
        index += char.IsHighSurrogate(c) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]) ? 2 : 1;
        if (c == '\n')
        {
            line++;
            column = 1;
        }
        else
        {
            column++;
        }
    }

    char At(int offset) => offset < source.Length ? source[offset] : '\0';

    void Add(TokenKind kind, string text, int tokenLine, int tokenColumn)
        => tokens.Add(new Token(kind, text, 0, tokenLine, tokenColumn));

    bool Fail(string message, int errorLine, int errorColumn)
    {
        Add(TokenKind.Error, message, errorLine, errorColumn);
        return false;
    }

    static bool IsDigit(char c) => c is >= '0' and <= '9';

    static bool IsIdentStart(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    static bool IsIdentPart(char c) => IsIdentStart(c) || IsDigit(c);
}
