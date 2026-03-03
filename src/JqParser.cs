using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Devlooped;

public sealed class JqParser
{
    private readonly string text;
    private int position;

    public JqParser(string text)
    {
        this.text = text ?? throw new ArgumentNullException(nameof(text));
    }

    public static JqFilter Parse(string expression)
    {
        return new JqParser(expression).Parse();
    }

    public JqFilter Parse()
    {
        SkipWhitespace();
        if (IsAtEnd)
            throw Error("Filter expression cannot be empty.");

        var filter = ParsePipe();
        SkipWhitespace();
        if (!IsAtEnd)
            throw Error($"Unexpected character '{Current}'.");

        return filter;
    }

    private JqFilter ParsePipe()
    {
        var left = ParseComma();
        SkipWhitespace();
        if (!TryConsume('|'))
            return left;

        var right = ParsePipe();
        return new PipeFilter(left, right);
    }

    private JqFilter ParseComma()
    {
        var left = ParseComparison();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsume(','))
                break;

            var right = ParseComparison();
            left = new CommaFilter(left, right);
        }

        return left;
    }

    private JqFilter ParseComparison()
    {
        var left = ParseAdditive();
        SkipWhitespace();
        BinaryOp op;
        if (TryConsumeSequence("=="))
            op = BinaryOp.Equal;
        else if (TryConsumeSequence("!="))
            op = BinaryOp.NotEqual;
        else if (TryConsumeSequence("<="))
            op = BinaryOp.LessOrEqual;
        else if (TryConsumeSequence(">="))
            op = BinaryOp.GreaterOrEqual;
        else if (TryConsume('<'))
            op = BinaryOp.LessThan;
        else if (TryConsume('>'))
            op = BinaryOp.GreaterThan;
        else
            return left;

        var right = ParseAdditive();
        return new BinaryOpFilter(left, op, right);
    }

    private JqFilter ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            SkipWhitespace();
            BinaryOp? op = null;
            if (TryConsume('+'))
                op = BinaryOp.Add;
            else if (TryConsume('-'))
                op = BinaryOp.Subtract;

            if (!op.HasValue)
                break;

            var right = ParseMultiplicative();
            left = new BinaryOpFilter(left, op.Value, right);
        }

        return left;
    }

    private JqFilter ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            SkipWhitespace();
            BinaryOp? op = null;
            if (TryConsume('*'))
                op = BinaryOp.Multiply;
            else if (Peek() == '/' && Peek(1) != '/')
            {
                Consume();
                op = BinaryOp.Divide;
            }
            else if (TryConsume('%'))
                op = BinaryOp.Modulo;

            if (!op.HasValue)
                break;

            var right = ParseUnary();
            left = new BinaryOpFilter(left, op.Value, right);
        }

        return left;
    }

    private JqFilter ParseUnary()
    {
        SkipWhitespace();
        if (TryConsume('-'))
            return new NegateFilter(ParseUnary());

        return ParsePostfix();
    }

    private JqFilter ParsePostfix()
    {
        SkipWhitespace();
        JqFilter filter;
        if (Peek() == '.' && Peek(1) != '.' && (IsIdentifierStart(Peek(1)) || Peek(1) == '['))
            filter = new IdentityFilter();
        else
            filter = ParsePrimary();

        while (true)
        {
            SkipWhitespace();
            if (TryConsume('?'))
            {
                filter = new OptionalFilter(filter);
                continue;
            }

            if (TryConsume('.'))
            {
                if (IsIdentifierStart(Peek()))
                {
                    var name = ParseIdentifier();
                    filter = new PipeFilter(filter, new FieldFilter(name));
                    continue;
                }

                if (Peek() == '[')
                {
                    var suffix = ParseBracketOperation();
                    filter = new PipeFilter(filter, suffix);
                    continue;
                }

                throw Error("Expected field access after '.'.");
            }

            if (Peek() == '[')
            {
                var suffix = ParseBracketOperation();
                filter = new PipeFilter(filter, suffix);
                continue;
            }

            break;
        }

        return filter;
    }

    private JqFilter ParsePrimary()
    {
        SkipWhitespace();

        if (TryConsumeSequence(".."))
            return new RecurseFilter();

        if (TryConsume('.'))
            return new IdentityFilter();

        if (TryConsume('('))
        {
            var inner = ParsePipe();
            SkipWhitespace();
            Expect(')');
            return inner;
        }

        if (Peek() == '[')
            return ParseArrayConstructor();

        if (Peek() == '{')
            return ParseObjectConstructor();

        if (TryParseLiteral(out var literal))
            return literal;

        throw Error($"Unexpected character '{Current}'.");
    }

    private JqFilter ParseBracketOperation()
    {
        Expect('[');
        SkipWhitespace();

        if (TryConsume(']'))
            return new IterateFilter();

        var saved = position;
        if (TryParseSlice(out var sliceFilter))
            return sliceFilter;

        position = saved;
        SkipWhitespace();

        if (Peek() == '"')
        {
            Consume();
            var field = ParseStringContent();
            SkipWhitespace();
            Expect(']');
            return new FieldFilter(field);
        }

        if (TryParseInteger(out var index))
        {
            SkipWhitespace();
            Expect(']');
            return new IndexFilter(index);
        }

        throw Error("Index expression must be a string or integer.");
    }

    private bool TryParseSlice(out JqFilter filter)
    {
        filter = null!;
        var startPos = position;
        SkipWhitespace();

        int? start = null;
        if (Peek() != ':')
        {
            if (!TryParseInteger(out var parsedStart))
            {
                position = startPos;
                return false;
            }

            start = parsedStart;
            SkipWhitespace();
        }

        if (!TryConsume(':'))
        {
            position = startPos;
            return false;
        }

        SkipWhitespace();
        int? end = null;
        if (Peek() != ']')
        {
            if (!TryParseInteger(out var parsedEnd))
                throw Error("Slice bounds must be integers.");

            end = parsedEnd;
            SkipWhitespace();
        }

        Expect(']');
        filter = new SliceFilter(start, end);
        return true;
    }

    private JqFilter ParseArrayConstructor()
    {
        Expect('[');
        SkipWhitespace();
        if (TryConsume(']'))
            return LiteralFilter.FromRawJson("[]");

        var inner = ParsePipe();
        SkipWhitespace();
        Expect(']');
        return new ArrayFilter(inner);
    }

    private JqFilter ParseObjectConstructor()
    {
        Expect('{');
        SkipWhitespace();

        var pairs = new List<(JqFilter Key, JqFilter Value)>();
        if (TryConsume('}'))
            return new ObjectFilter(pairs.ToArray());

        while (true)
        {
            SkipWhitespace();
            var keyFilter = ParseObjectKey();

            SkipWhitespace();
            if (!TryConsume(':'))
                throw Error("Expected ':' after object key.");

            var valueFilter = ParseObjectValue();
            pairs.Add((keyFilter, valueFilter));

            SkipWhitespace();
            if (TryConsume('}'))
                break;

            Expect(',');
        }

        return new ObjectFilter(pairs.ToArray());
    }

    private JqFilter ParseObjectKey()
    {
        SkipWhitespace();

        if (Peek() == '"')
        {
            Consume();
            var text = ParseStringContent();
            return new LiteralFilter(CreateStringLiteral(text));
        }

        if (IsIdentifierStart(Peek()))
        {
            var identifier = ParseIdentifier();
            return new LiteralFilter(CreateStringLiteral(identifier));
        }

        var keyExpression = ReadUntilTopLevel(':');
        if (string.IsNullOrWhiteSpace(keyExpression))
            throw Error("Object key expression cannot be empty.");

        return Parse(keyExpression);
    }

    private JqFilter ParseObjectValue()
    {
        var valueExpression = ReadUntilTopLevel(',', '}');
        if (string.IsNullOrWhiteSpace(valueExpression))
            throw Error("Object value expression cannot be empty.");

        return Parse(valueExpression);
    }

    private string ReadUntilTopLevel(params char[] terminators)
    {
        var start = position;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var inString = false;
        var escaped = false;

        while (!IsAtEnd)
        {
            var ch = Current;

            if (inString)
            {
                position++;
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                position++;
                continue;
            }

            if (ch == '(')
            {
                parenDepth++;
                position++;
                continue;
            }

            if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
                position++;
                continue;
            }

            if (ch == '[')
            {
                bracketDepth++;
                position++;
                continue;
            }

            if (ch == ']')
            {
                if (bracketDepth > 0)
                    bracketDepth--;
                position++;
                continue;
            }

            if (ch == '{')
            {
                braceDepth++;
                position++;
                continue;
            }

            if (ch == '}')
            {
                if (braceDepth == 0 && terminators.Contains('}'))
                    break;

                if (braceDepth > 0)
                    braceDepth--;

                position++;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && terminators.Contains(ch))
                break;

            position++;
        }

        return text[start..position].Trim();
    }

    private bool TryParseLiteral(out JqFilter literal)
    {
        literal = null!;

        if (Peek() == '"')
        {
            Consume();
            literal = new LiteralFilter(CreateStringLiteral(ParseStringContent()));
            return true;
        }

        if (TryConsumeKeyword("null"))
        {
            literal = LiteralFilter.FromRawJson("null");
            return true;
        }

        if (TryConsumeKeyword("true"))
        {
            literal = LiteralFilter.FromRawJson("true");
            return true;
        }

        if (TryConsumeKeyword("false"))
        {
            literal = LiteralFilter.FromRawJson("false");
            return true;
        }

        if (TryParseNumberToken(out var numberToken))
        {
            literal = LiteralFilter.FromRawJson(numberToken);
            return true;
        }

        return false;
    }

    private static JsonElement CreateStringLiteral(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private bool TryParseInteger(out int value)
    {
        value = default;
        var start = position;

        if (Peek() == '-')
            position++;

        if (!char.IsDigit(Peek()))
        {
            position = start;
            return false;
        }

        if (Peek() == '0')
        {
            position++;
        }
        else
        {
            while (char.IsDigit(Peek()))
                position++;
        }

        var token = text[start..position];
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            throw Error($"Invalid integer value '{token}'.");

        return true;
    }

    private bool TryParseNumberToken(out string token)
    {
        token = string.Empty;
        var start = position;

        if (!char.IsDigit(Peek()))
        {
            position = start;
            return false;
        }

        if (Peek() == '0')
        {
            position++;
        }
        else
        {
            while (char.IsDigit(Peek()))
                position++;
        }

        if (Peek() == '.')
        {
            position++;
            if (!char.IsDigit(Peek()))
                throw Error("Invalid number literal.");

            while (char.IsDigit(Peek()))
                position++;
        }

        if (Peek() == 'e' || Peek() == 'E')
        {
            position++;
            if (Peek() == '+' || Peek() == '-')
                position++;

            if (!char.IsDigit(Peek()))
                throw Error("Invalid number literal.");

            while (char.IsDigit(Peek()))
                position++;
        }

        token = text[start..position];
        return true;
    }

    private string ParseIdentifier()
    {
        if (!IsIdentifierStart(Peek()))
            throw Error("Expected identifier.");

        var start = position;
        position++;
        while (IsIdentifierPart(Peek()))
            position++;

        return text[start..position];
    }

    private string ParseStringContent()
    {
        var builder = new StringBuilder();
        while (!IsAtEnd)
        {
            var ch = Consume();
            if (ch == '"')
                return builder.ToString();

            if (ch != '\\')
            {
                builder.Append(ch);
                continue;
            }

            if (IsAtEnd)
                throw Error("Invalid string escape.");

            var escaped = Consume();
            builder.Append(escaped switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' => ParseUnicodeEscape(),
                _ => throw Error($"Unsupported escape sequence '\\{escaped}'."),
            });
        }

        throw Error("Unterminated string literal.");
    }

    private char ParseUnicodeEscape()
    {
        if (position + 4 > text.Length)
            throw Error("Invalid unicode escape sequence.");

        var hex = text.Substring(position, 4);
        for (var i = 0; i < hex.Length; i++)
        {
            if (!Uri.IsHexDigit(hex[i]))
                throw Error("Invalid unicode escape sequence.");
        }

        position += 4;
        return (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    private void SkipWhitespace()
    {
        while (!IsAtEnd && char.IsWhiteSpace(Current))
            position++;
    }

    private bool TryConsume(char ch)
    {
        if (Peek() != ch)
            return false;

        position++;
        return true;
    }

    private bool TryConsumeSequence(string value)
    {
        if (position + value.Length > text.Length)
            return false;

        if (!string.Equals(text.Substring(position, value.Length), value, StringComparison.Ordinal))
            return false;

        position += value.Length;
        return true;
    }

    private bool TryConsumeKeyword(string keyword)
    {
        if (position + keyword.Length > text.Length)
            return false;

        if (!string.Equals(text.Substring(position, keyword.Length), keyword, StringComparison.Ordinal))
            return false;

        var end = position + keyword.Length;
        if (end < text.Length && IsIdentifierPart(text[end]))
            return false;

        position = end;
        return true;
    }

    private void Expect(char ch)
    {
        if (!TryConsume(ch))
            throw Error($"Expected '{ch}'.");
    }

    private char Consume()
    {
        if (IsAtEnd)
            throw Error("Unexpected end of filter.");

        return text[position++];
    }

    private char Peek() => IsAtEnd ? '\0' : text[position];

    private char Peek(int offset)
    {
        var index = position + offset;
        if (index < 0 || index >= text.Length)
            return '\0';

        return text[index];
    }

    private char Current => IsAtEnd ? '\0' : text[position];

    private bool IsAtEnd => position >= text.Length;

    private static bool IsIdentifierStart(char ch) => ch == '_' || char.IsLetter(ch);

    private static bool IsIdentifierPart(char ch) => IsIdentifierStart(ch) || char.IsDigit(ch);

    private JqException Error(string message)
    {
        return new JqException($"Parse error at position {position}: {message}");
    }
}
