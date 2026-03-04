using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Devlooped;

public sealed class JqParser
{
    private readonly string text;
    private readonly HashSet<string> _definedVariables = new(StringComparer.Ordinal);
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
        if (TryConsumeKeyword("as"))
        {
            var pattern = ParsePattern();
            var declared = pattern.VariableNames.Distinct(StringComparer.Ordinal).ToArray();
            var newlyDeclared = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in declared)
            {
                if (_definedVariables.Add(name))
                    newlyDeclared.Add(name);
            }

            SkipWhitespace();
            Expect('|');

            JqFilter body;
            try
            {
                body = ParsePipe();
            }
            finally
            {
                foreach (var name in newlyDeclared)
                    _definedVariables.Remove(name);
            }

            return new BindingFilter(left, pattern, body);
        }

        if (!TryConsume('|'))
            return left;

        var right = ParsePipe();
        return new PipeFilter(left, right);
    }

    private JqFilter ParseComma()
    {
        var left = ParseOr();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsume(','))
                break;

            var right = ParseOr();
            left = new CommaFilter(left, right);
        }

        return left;
    }

    private JqFilter ParseOr()
    {
        var left = ParseAnd();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsumeKeyword("or"))
                break;

            var right = ParseAnd();
            left = new BinaryOpFilter(left, BinaryOp.Or, right);
        }

        return left;
    }

    private JqFilter ParseAnd()
    {
        var left = ParseComparison();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsumeKeyword("and"))
                break;

            var right = ParseComparison();
            left = new BinaryOpFilter(left, BinaryOp.And, right);
        }

        return left;
    }

    private JqFilter ParseComparison()
    {
        var left = ParseAlternative();
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

        var right = ParseAlternative();
        return new BinaryOpFilter(left, op, right);
    }

    private JqFilter ParseAlternative()
    {
        var left = ParseAdditive();
        while (true)
        {
            SkipWhitespace();
            if (!TryConsumeSequence("//"))
                break;

            var right = ParseAdditive();
            left = new AlternativeFilter(left, right);
        }

        return left;
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
                filter = new TryCatchFilter(filter, new BuiltinFilter("empty"));
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
                    filter = suffix is DynamicIndexFilter dynamicIndex
                        ? new DynamicIndexFilter(filter, dynamicIndex.IndexExpression)
                        : new PipeFilter(filter, suffix);
                    continue;
                }

                throw Error("Expected field access after '.'.");
            }

            if (Peek() == '[')
            {
                var suffix = ParseBracketOperation();
                filter = suffix is DynamicIndexFilter dynamicIndex
                    ? new DynamicIndexFilter(filter, dynamicIndex.IndexExpression)
                    : new PipeFilter(filter, suffix);
                continue;
            }

            break;
        }

        return filter;
    }

    private JqFilter ParsePrimary()
    {
        SkipWhitespace();

        if (TryConsume('$'))
        {
            var name = ParseIdentifier();
            if (string.Equals(name, "ENV", StringComparison.Ordinal) ||
                string.Equals(name, "__loc__", StringComparison.Ordinal) ||
                _definedVariables.Contains(name))
            {
                return new VariableFilter(name);
            }

            throw new JqException($"${name} is not defined");
        }

        if (TryConsumeKeyword("try"))
            return ParseTryExpression();

        if (TryConsumeKeyword("if"))
            return ParseIfExpression();

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

        if (TryConsumeKeyword("not"))
            return new NotFilter();

        if (IsIdentifierStart(Peek()))
        {
            var saved = position;
            var name = ParseIdentifier();
            SkipWhitespace();

            if (TryConsume('('))
            {
                var args = new List<JqFilter>();
                SkipWhitespace();
                if (!TryConsume(')'))
                {
                    while (true)
                    {
                        args.Add(ParsePipe());
                        SkipWhitespace();
                        if (TryConsume(')'))
                            break;

                        Expect(';');
                        SkipWhitespace();
                    }
                }

                return new ParameterizedFilter(name, args.ToArray());
            }

            if (BuiltinFilter.IsBuiltin(name))
                return new BuiltinFilter(name);

            position = saved;
        }

        throw Error($"Unexpected character '{Current}'.");
    }

    private JqFilter ParseIfExpression()
    {
        var condition = ParsePipe();
        SkipWhitespace();
        ExpectKeyword("then");

        var thenBranch = ParsePipe();
        var elseBranch = ParseIfElseBranch();

        SkipWhitespace();
        ExpectKeyword("end");
        return new ConditionalFilter(condition, thenBranch, elseBranch);
    }

    private JqFilter ParseTryExpression()
    {
        var body = ParsePostfix();
        SkipWhitespace();
        if (TryConsumeKeyword("catch"))
        {
            var catchBody = ParsePostfix();
            return new TryCatchFilter(body, catchBody);
        }

        return new TryCatchFilter(body, new BuiltinFilter("empty"));
    }

    private JqFilter ParseIfElseBranch()
    {
        SkipWhitespace();
        if (TryConsumeKeyword("elif"))
        {
            var condition = ParsePipe();
            SkipWhitespace();
            ExpectKeyword("then");
            var thenBranch = ParsePipe();
            var elseBranch = ParseIfElseBranch();
            return new ConditionalFilter(condition, thenBranch, elseBranch);
        }

        if (TryConsumeKeyword("else"))
            return ParsePipe();

        return new IdentityFilter();
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

        var expression = ParsePipe();
        SkipWhitespace();
        Expect(']');
        return new DynamicIndexFilter(expression);
    }

    private JqPattern ParsePattern()
    {
        SkipWhitespace();
        if (TryConsume('$'))
        {
            var name = ParseIdentifier();
            return new VariablePattern(name);
        }

        if (Peek() == '[')
            return ParseArrayPattern();

        if (Peek() == '{')
            return ParseObjectPattern();

        throw Error("Expected pattern (variable, array, or object).");
    }

    private JqPattern ParseArrayPattern()
    {
        Expect('[');
        SkipWhitespace();
        if (TryConsume(']'))
            throw Error("Array pattern must contain at least one element.");

        var items = new List<JqPattern>();
        while (true)
        {
            items.Add(ParsePattern());
            SkipWhitespace();
            if (TryConsume(']'))
                break;

            Expect(',');
            SkipWhitespace();
        }

        return new ArrayPattern(items.ToArray());
    }

    private JqPattern ParseObjectPattern()
    {
        Expect('{');
        SkipWhitespace();
        if (TryConsume('}'))
            throw Error("Object pattern must contain at least one entry.");

        var entries = new List<(JqFilter KeyExpr, JqPattern ValuePattern)>();
        while (true)
        {
            SkipWhitespace();
            if (TryConsume('$'))
            {
                var shorthandName = ParseIdentifier();
                entries.Add((new LiteralFilter(CreateStringLiteral(shorthandName)), new VariablePattern(shorthandName)));
            }
            else
            {
                JqFilter keyExpression;

                if (TryConsume('('))
                {
                    keyExpression = ParsePipe();
                    SkipWhitespace();
                    Expect(')');
                }
                else if (Peek() == '"')
                {
                    Consume();
                    keyExpression = new LiteralFilter(CreateStringLiteral(ParseStringContent()));
                }
                else if (IsIdentifierStart(Peek()))
                {
                    keyExpression = new LiteralFilter(CreateStringLiteral(ParseIdentifier()));
                }
                else
                {
                    throw Error("Invalid object pattern key.");
                }

                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                var valuePattern = ParsePattern();
                entries.Add((keyExpression, valuePattern));
            }

            SkipWhitespace();
            if (TryConsume('}'))
                break;

            Expect(',');
            SkipWhitespace();
        }

        return new ObjectPattern(entries);
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
            return ParseString();
        }

        if (IsIdentifierStart(Peek()))
        {
            var identifier = ParseIdentifier();
            return new LiteralFilter(CreateStringLiteral(identifier));
        }

        var keyExpression = ReadUntilTopLevel(':');
        if (string.IsNullOrWhiteSpace(keyExpression))
            throw Error("Object key expression cannot be empty.");

        return ParseSubExpression(keyExpression);
    }

    private JqFilter ParseObjectValue()
    {
        var valueExpression = ReadUntilTopLevel(',', '}');
        if (string.IsNullOrWhiteSpace(valueExpression))
            throw Error("Object value expression cannot be empty.");

        return ParseSubExpression(valueExpression);
    }

    private JqFilter ParseSubExpression(string expression)
    {
        var parser = new JqParser(expression);
        foreach (var variable in _definedVariables)
            parser._definedVariables.Add(variable);

        return parser.Parse();
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
                    if (ch == '(')
                    {
                        // \( starts string interpolation — exit string mode and track the paren
                        inString = false;
                        parenDepth++;
                    }
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

            if (ch == '#')
            {
                SkipComment();
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
            literal = ParseString();
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

    private JqFilter ParseString()
    {
        var builder = new StringBuilder();
        var parts = new List<(string? Literal, JqFilter? Expression)>();
        var hasInterpolation = false;

        while (!IsAtEnd)
        {
            var ch = Consume();
            if (ch == '"')
            {
                if (!hasInterpolation)
                    return new LiteralFilter(CreateStringLiteral(builder.ToString()));

                if (builder.Length > 0)
                    parts.Add((builder.ToString(), null));

                return new StringInterpolationFilter(parts.ToArray());
            }

            if (ch != '\\')
            {
                builder.Append(ch);
                continue;
            }

            if (IsAtEnd)
                throw Error("Invalid string escape.");

            if (Peek() == '(')
            {
                Consume(); // consume '('
                hasInterpolation = true;

                if (builder.Length > 0)
                {
                    parts.Add((builder.ToString(), null));
                    builder.Clear();
                }

                var expr = ParsePipe();
                SkipWhitespace();
                Expect(')');

                parts.Add((null, expr));
                continue;
            }

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
        while (!IsAtEnd)
        {
            if (char.IsWhiteSpace(Current))
            {
                position++;
                continue;
            }

            if (Current == '#')
            {
                SkipComment();
                continue;
            }

            break;
        }
    }

    private void SkipComment()
    {
        if (IsAtEnd || Current != '#')
            return;

        while (!IsAtEnd)
        {
            var lineStart = position;
            while (!IsAtEnd && Current != '\r' && Current != '\n')
                position++;

            var trailingSlashCount = 0;
            for (var i = position - 1; i >= lineStart && text[i] == '\\'; i--)
                trailingSlashCount++;

            if (IsAtEnd)
                break;

            if (Current == '\r')
            {
                position++;
                if (!IsAtEnd && Current == '\n')
                    position++;
            }
            else if (Current == '\n')
            {
                position++;
            }

            if (trailingSlashCount % 2 == 0)
                break;
        }
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

    private void ExpectKeyword(string keyword)
    {
        if (!TryConsumeKeyword(keyword))
            throw Error($"Expected keyword '{keyword}'.");
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
