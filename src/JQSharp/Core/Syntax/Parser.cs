using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Devlooped.Core.Values;

namespace Devlooped.Core.Syntax;

/// <summary>
/// Recursive descent parser of jqlite (spec §3) over the tokens of <see cref="Lexer"/>. It never
/// throws for bad input: it returns the <see cref="Program"/> or one <see cref="SyntaxError"/>.
/// </summary>
/// <remarks>
/// <para>Rewritten by the parser (spec §3.3), so the AST has one form for each:</para>
/// <list type="bullet">
/// <item><c>..</c> is the call <c>recurse</c> (resolved like any call: a user <c>recurse/0</c> shadows it, as in jq).</item>
/// <item><c>E?</c> and <c>try E</c> are <see cref="Try"/> without a handler (which discards the error).</item>
/// <item><c>.name</c>, <c>."s"</c>, <c>T.name</c>, <c>T."s"</c> are <see cref="Index"/> with a literal or template key.</item>
/// <item><c>elif</c> nests <see cref="If"/>; a missing <c>else</c> is <c>.</c>; a <c>foreach</c> without extract has <c>.</c>.</item>
/// <item><c>{$x}</c> is <c>{"x": $x}</c>. The string shorthands (<c>{a}</c>, <c>{"s"}</c>, <c>{@f "s"}</c>) keep a null value, see <see cref="ObjectEntry"/>.</item>
/// <item><c>L = R</c> is <c>_assign(L; R)</c>, <c>L |= R</c> is <c>_modify(L; R)</c>, <c>L op= R</c> is
/// <c>R as $v | _modify(L; . op $v)</c> (<c>//=</c> likewise with <c>//</c>), where <c>$v</c> is
/// <see cref="UpdateVariable"/>, a name no program can write.</item>
/// <item>A string or <c>@f "s"</c> without interpolation is a <see cref="Literal"/> (a format applies to interpolations only); parentheses leave no node.</item>
/// </list>
/// <para>
/// Where jq 1.8.2 parses differently from the EBNF of spec §3.2, this parser follows jq:
/// <c>def</c> and <c>label</c> start a pipe or comma item only, never an operand
/// (<c>1 + def f: 1; f</c> fails); object values are <c>Alt ('|' Alt)*</c> without <c>as</c>;
/// object constructions accept a trailing comma; <c>.[:]</c> is an error; a definition name may
/// be qualified (<c>def a::b:</c>); module metadata constants have no negative numbers.
/// </para>
/// <para>
/// Nesting (brackets, interpolations, <c>if</c>, <c>reduce</c>, <c>foreach</c>, <c>try</c>,
/// <c>def</c>) is limited to <see cref="MaxNesting"/>; within the limit the host stack is not
/// exhausted. Sequences do not recurse per element: pipes, commas, bindings and leading definitions
/// are folded from a list (<see cref="ParsePipe"/>), binary operators are parsed with an operator
/// stack (<see cref="ParseExpression"/>), and runs of unary minus and of <c>elif</c> are loops.
/// </para>
/// </remarks>
sealed class Parser
{
    /// <summary>The nesting limit of spec §11.</summary>
    public const int MaxNesting = 1000;

    /// <summary>The variable that the rewriting of <c>L op= R</c> binds; '*' cannot appear in a source name.</summary>
    public const string UpdateVariable = "*update";

    /// <summary><c>$__loc__</c>, the location object of §7.5: the resolver replaces it; no pattern, parameter or import binds it.</summary>
    public const string LocationVariable = "__loc__";

    enum Assoc
    {
        Left,
        Right,
        None,
    }

    /// <summary>
    /// The binary operators (spec §3.1, levels 4 to 10) with their level, higher binding tighter,
    /// and associativity; level 0 for any other token. Levels 1 to 3 (<c>|</c>, <c>,</c>, <c>as</c>)
    /// are parsed by <see cref="ParsePipe"/>, levels 11 to 13 (unary minus, suffixes, terms) by
    /// <see cref="ParseExpression"/>.
    /// </summary>
    static (int Level, Assoc Assoc) BinaryLevel(TokenKind kind) => kind switch
    {
        TokenKind.Alt => (4, Assoc.Right),
        TokenKind.Assign or TokenKind.PipeAssign or TokenKind.PlusAssign or TokenKind.MinusAssign or TokenKind.StarAssign
            or TokenKind.SlashAssign or TokenKind.PercentAssign or TokenKind.AltAssign => (5, Assoc.None),
        TokenKind.Or => (6, Assoc.Left),
        TokenKind.And => (7, Assoc.Left),
        TokenKind.Equal or TokenKind.NotEqual or TokenKind.Less or TokenKind.LessEqual
            or TokenKind.Greater or TokenKind.GreaterEqual => (8, Assoc.None),
        TokenKind.Plus or TokenKind.Minus => (9, Assoc.Left),
        TokenKind.Star or TokenKind.Slash or TokenKind.Percent => (10, Assoc.Left),
        _ => (0, Assoc.Left),
    };

    readonly List<Token> tokens;
    // The token at next, kept in a field: reading it is the commonest operation of the parser.
    Token current;
    int next;
    int depth;
    SyntaxError? error;

    Parser(string source)
    {
        tokens = Lexer.Tokenize(source);
        current = tokens[0];
    }

    /// <summary>Parses a program (spec §3.2, rule <c>Program</c>).</summary>
    public static bool TryParse(string source, [NotNullWhen(true)] out Program? program, [NotNullWhen(false)] out SyntaxError? error)
        => new Parser(source).Run(module: false, out program, out error);

    /// <summary>Parses a module file (spec §3.2, rule <c>ModuleBody</c>): directives and definitions only.</summary>
    public static bool TryParseModule(string source, [NotNullWhen(true)] out Program? program, [NotNullWhen(false)] out SyntaxError? error)
        => new Parser(source).Run(module: true, out program, out error);

    bool Run(bool module, out Program? program, out SyntaxError? syntaxError)
    {
        program = ParseProgram(module);
        syntaxError = error;
        return program is not null;
    }

    Token Peek => tokens[Math.Min(next + 1, tokens.Count - 1)];

    void Advance()
    {
        if (next < tokens.Count - 1)
            current = tokens[++next];
    }

    static Position Pos(Token token) => new(token.Line, token.Column);

    static bool IsLocation(Token token) => token.Kind == TokenKind.Variable && token.Text == LocationVariable;

    // Program = ModuleDecl? Import* FuncDef* Pipe?      ModuleBody = ModuleDecl? Import* FuncDef*
    Program? ParseProgram(bool module)
    {
        JqValue? metadata = null;
        if (current.Kind == TokenKind.Module)
        {
            Advance();
            var value = ParseMetadata();
            if (value is null || !Expect(TokenKind.Semicolon, "';'"))
                return null;
            metadata = value;
        }

        var imports = new List<Import>();
        while (current.Kind is TokenKind.Import or TokenKind.Include)
        {
            var import = ParseImport();
            if (import is null)
                return null;
            imports.Add(import);
        }

        var definitions = new List<Definition>();
        while (current.Kind == TokenKind.Def)
        {
            var definition = ParseDefinition();
            if (definition is null)
                return null;
            definitions.Add(definition);
        }

        Node? body = null;
        if (module)
        {
            if (current.Kind != TokenKind.Eof)
                return Fail<Program>(SyntaxMessages.ModuleHasBody, current);
        }
        else
        {
            body = current.Kind == TokenKind.Eof ? new Identity(Pos(current)) : ParsePipe();
            if (body is null)
                return null;
            if (current.Kind != TokenKind.Eof)
                return Fail<Program>(SyntaxMessages.Unexpected(current), current);
        }

        return new Program(metadata, [.. imports], [.. definitions], body);
    }

    // Import = 'import' STRING 'as' (IDENT | VAR) ConstObject? ';' | 'include' STRING ConstObject? ';'
    Import? ParseImport()
    {
        var keyword = current;
        Advance();
        if (current.Kind == TokenKind.StringStart)
            return Fail<Import>(SyntaxMessages.ImportPathNotConstant, current);
        if (current.Kind != TokenKind.String)
            return Fail<Import>(SyntaxMessages.Expected("a module path string", current), current);
        var path = current.Text;
        Advance();

        string? alias = null;
        var isData = false;
        if (keyword.Kind == TokenKind.Import)
        {
            if (!Expect(TokenKind.As, "'as'"))
                return null;
            if (current.Kind is not (TokenKind.Ident or TokenKind.Variable) || IsLocation(current))
                return Fail<Import>(SyntaxMessages.Expected("a module name or a $name", current), current);
            alias = current.Text;
            isData = current.Kind == TokenKind.Variable;
            Advance();
        }

        JqValue? metadata = null;
        if (current.Kind == TokenKind.LeftBrace)
        {
            metadata = ParseMetadata();
            if (metadata is null)
                return null;
        }

        return Expect(TokenKind.Semicolon, "';'") ? new Import(path, alias, isData, metadata, Pos(keyword)) : null;
    }

    // The metadata of 'module' and of imports: a constant object.
    JqValue? ParseMetadata()
    {
        var start = current;
        var value = ParseConstant(start);
        if (value is null)
            return null;
        return value.Value.Kind == JqKind.Object ? value : FailValue(SyntaxMessages.MetadataNotObject, start);
    }

    // Const = NUMBER | STRING | null | true | false | '[' (Const (',' Const)*)? ']' | '{' (CEntry (',' CEntry)* ','?)? '}'
    // CEntry = (IDENT | Keyword | STRING) ':' Const. Any other token: "Module metadata must be constant" at the metadata.
    JqValue? ParseConstant(Token start) => current.Kind switch
    {
        TokenKind.LeftBracket => ParseConstantArray(start),
        TokenKind.LeftBrace => ParseConstantObject(start),
        _ => ParseConstantScalar(start),
    };

    JqValue? ParseConstantScalar(Token start)
    {
        var token = current;
        Advance();
        return token.Kind switch
        {
            TokenKind.Number => JqValue.FromNumber(token.Number),
            TokenKind.String => JqValue.FromString(token.Text),
            TokenKind.Ident when token.Text == "null" => JqValue.Null,
            TokenKind.Ident when token.Text is "true" or "false" => JqValue.FromBool(token.Text == "true"),
            _ => FailValue(SyntaxMessages.MetadataNotConstant, start),
        };
    }

    JqValue? ParseConstantArray(Token start)
    {
        if (!Enter(current))
            return null;
        Advance();
        var items = new List<JqValue>();
        while (current.Kind != TokenKind.RightBracket)
        {
            if (items.Count > 0 && !ExpectConstant(TokenKind.Comma, start))
                return null;
            var item = ParseConstant(start);
            if (item is null)
                return null;
            items.Add(item.Value);
        }

        Advance();
        depth--;
        return JqValue.FromArray(JqArray.FromOwned([.. items]));
    }

    JqValue? ParseConstantObject(Token start)
    {
        if (!Enter(current))
            return null;
        Advance();
        var builder = new JqObjectBuilder();
        while (current.Kind != TokenKind.RightBrace)
        {
            var key = current;
            if (key.Kind is not (TokenKind.Ident or TokenKind.String) && !Token.IsKeyword(key.Kind))
                return FailValue(SyntaxMessages.MetadataNotConstant, start);
            Advance();
            if (!ExpectConstant(TokenKind.Colon, start))
                return null;
            var value = ParseConstant(start);
            if (value is null)
                return null;
            builder.Set(key.Text, value.Value);
            if (current.Kind == TokenKind.Comma)
                Advance();
            else if (current.Kind != TokenKind.RightBrace)
                return FailValue(SyntaxMessages.MetadataNotConstant, start);
        }

        Advance();
        depth--;
        return JqValue.FromObject(builder.ToObject());
    }

    bool ExpectConstant(TokenKind kind, Token start)
    {
        if (current.Kind != kind)
        {
            Fail(SyntaxMessages.MetadataNotConstant, start);
            return false;
        }

        Advance();
        return true;
    }

    // FuncDef = 'def' IDENT ('(' Param (';' Param)* ')')? ':' Pipe ';'
    Definition? ParseDefinition()
    {
        var keyword = current;
        if (!Enter(keyword))
            return null;
        Advance();
        if (current.Kind != TokenKind.Ident)
            return Fail<Definition>(SyntaxMessages.Expected("a function name", current), current);
        var name = current.Text;
        Advance();
        var parameters = ParseParams();
        if (parameters is null || !Expect(TokenKind.Colon, "':'"))
            return null;
        var body = ParsePipe();
        if (body is null || !Expect(TokenKind.Semicolon, "';'"))
            return null;
        depth--;
        return new Definition(name, [.. parameters], body, Pos(keyword));
    }

    // ('(' Param (';' Param)* ')')?      Param = IDENT | VAR
    List<Param>? ParseParams()
    {
        var parameters = new List<Param>();
        if (current.Kind != TokenKind.LeftParen)
            return parameters;
        do
        {
            Advance();
            if (current.Kind is not (TokenKind.Ident or TokenKind.Variable) || IsLocation(current))
                return Fail<List<Param>>(SyntaxMessages.Expected("a parameter name", current), current);
            parameters.Add(new Param(current.Text, current.Kind == TokenKind.Variable, Pos(current)));
            Advance();
        }
        while (current.Kind == TokenKind.Semicolon);

        return Expect(TokenKind.RightParen, "')'") ? parameters : null;
    }

    // Pipe = Item (',' Item)* ('|' Pipe)?, where an Item is
    //   'def' … ';' Pipe  |  'label' VAR '|' Pipe  |  Alt 'as' Patterns '|' Pipe  |  Alt
    // The first three, and '|', take the rest of the pipe. To avoid recursing per element, each of
    // them is kept as a partial node whose last child is Hole, and the partial nodes are completed
    // from the last to the first when the pipe ends.
    // ParsePipe and ParseExpression are on the stack once per nesting level of the program, with
    // the frame of the construct (ParseArray, ParseIf, …): these keep few locals and leave the
    // building of nodes to helpers, so a level costs under 1 KB of stack even without JIT
    // optimizations (ParserLimitTests parses 1000 levels on a 1 MB stack).
    Node? ParsePipe()
    {
        var pending = new List<Node>();
        Node? items = null;
        var comma = current;
        while (true)
        {
            Node? partial;
            if (current.Kind == TokenKind.Def)
            {
                partial = ParseFuncDef();
            }
            else if (current.Kind == TokenKind.Label)
            {
                partial = ParseLabel();
            }
            else
            {
                var item = ParseExpression(operandOnly: false);
                if (item is null)
                    return null;
                if (current.Kind == TokenKind.As)
                {
                    partial = ParseBinding(item);
                }
                else
                {
                    items = AddItem(items, item, comma);
                    if (current.Kind == TokenKind.Comma)
                    {
                        comma = current;
                        Advance();
                        continue;
                    }

                    if (current.Kind != TokenKind.Pipe)
                        break;
                    pending.Add(PartialPipe(items, current));
                    Advance();
                    items = null;
                    continue;
                }
            }

            if (partial is null)
                return null;

            // A binding takes the rest of the pipe; the items before it are the left side of a comma.
            pending.Add(items is null ? partial : AddItem(items, partial, comma));
            items = null;
        }

        return Complete(pending, items);
    }

    static Node AddItem(Node? items, Node item, Token comma) => items is null ? item : new Comma(items, item, Pos(comma));

    static Node PartialPipe(Node left, Token bar) => new Pipe(left, Hole, Pos(bar));

    // The pending partial nodes of ParsePipe completed from the last to the first.
    static Node Complete(List<Node> pending, Node last)
    {
        for (var i = pending.Count - 1; i >= 0; i--)
            last = Complete(pending[i], last);
        return last;
    }

    // The placeholder for "the rest of the pipe" in a partial node of ParsePipe.
    static readonly Node Hole = new Identity(new Position(0, 0));

    // A partial node of ParsePipe with its hole filled.
    static Node Complete(Node partial, Node rest) => partial switch
    {
        Pipe pipe => pipe with { Right = rest },
        Comma comma => comma with { Right = Complete(comma.Right, rest) },
        FuncDef definition => definition with { Rest = rest },
        Label label => label with { Body = rest },
        Bind bind => bind with { Body = rest },
        If branch => branch with { Else = rest },
        _ => rest,
    };

    // 'def' … ';', as a partial node.
    Node? ParseFuncDef()
    {
        var definition = ParseDefinition();
        return definition is null ? null : new FuncDef(definition, Hole, definition.Position);
    }

    // 'label' VAR '|', as a partial node.
    Node? ParseLabel()
    {
        var start = current;
        Advance();
        if (current.Kind != TokenKind.Variable)
            return Fail(SyntaxMessages.Expected("a label name ($name)", current), current);
        var name = current.Text;
        Advance();
        return Expect(TokenKind.Pipe, "'|'") ? new Label(name, Hole, Pos(start)) : null;
    }

    // Source 'as' Patterns '|', as a partial node.
    Node? ParseBinding(Node source)
    {
        var keyword = current;
        Advance();
        var patterns = ParsePatterns();
        if (patterns is null || !Expect(TokenKind.Pipe, "'|'"))
            return null;
        return new Bind(source, patterns.Value, Hole, Pos(keyword));
    }

    // ObjVal = Alt ('|' Alt)*: an object value, which has no top-level ',' and no 'as', 'def' or 'label' (as jq).
    Node? ParseObjectValue()
    {
        var items = new List<Node>();
        var bars = new List<Token>();
        while (true)
        {
            var item = ParseExpression(operandOnly: false);
            if (item is null)
                return null;
            items.Add(item);
            if (current.Kind != TokenKind.Pipe)
                break;
            bars.Add(current);
            Advance();
        }

        var result = items[^1];
        for (var i = items.Count - 2; i >= 0; i--)
            result = new Pipe(items[i], result, Pos(bars[i]));
        return result;
    }

    // Levels 4 to 13 of the precedence table: binary operators with an operand stack and an
    // operator stack (shunting-yard), so a chain of any length does not recurse; each operand is
    // Unary = '-'* Postfix, Postfix = Term Suffix*. With operandOnly, one operand (the Unary of
    // 'try' and 'catch'). The term dispatch is here rather than in a function of its own, so that
    // a nesting level costs three frames: ParsePipe, ParseExpression and the construct.
    Node? ParseExpression(bool operandOnly)
    {
        var operands = new List<Node>();
        var operators = new List<Token>();
        while (true)
        {
            var firstMinus = next;
            while (current.Kind == TokenKind.Minus)
                Advance();
            var termStart = next;

            Node? operand;
            switch (current.Kind)
            {
                case TokenKind.LeftParen:
                    operand = ParseParenthesized();
                    break;
                case TokenKind.LeftBracket:
                    operand = ParseArray();
                    break;
                case TokenKind.LeftBrace:
                    operand = ParseObject();
                    break;
                case TokenKind.String or TokenKind.StringStart:
                    operand = ParseString(null);
                    break;
                case TokenKind.Reduce or TokenKind.Foreach:
                    operand = ParseReduceOrForeach();
                    break;
                case TokenKind.If:
                    operand = ParseIf();
                    break;
                case TokenKind.Try:
                    operand = ParseTry();
                    break;
                case TokenKind.Ident when !IsLiteralName(current.Text):
                    operand = ParseCall();
                    break;
                default:
                    operand = ParseAtom();
                    break;
            }

            while (operand is not null && IsSuffix())
                operand = IsBracketSuffix() ? ParseBracketSuffix(operand) : ParseSuffix(operand);
            if (operand is null)
                return null;
            operand = Negate(operand, firstMinus, termStart);
            if (operandOnly)
                return operand;

            operands.Add(operand);
            if (BinaryLevel(current.Kind).Level == 0)
                return ReduceAll(operands, operators);
            if (!PushOperator(operands, operators))
                return null;
        }
    }

    static Node ReduceAll(List<Node> operands, List<Token> operators)
    {
        while (operators.Count > 0)
            Reduce(operands, operators);
        return operands[0];
    }

    static bool IsLiteralName(string name) => name is "null" or "true" or "false";

    // Reduces the operators that bind at least as tightly as the current one, then pushes it.
    bool PushOperator(List<Node> operands, List<Token> operators)
    {
        var op = current;
        var (level, assoc) = BinaryLevel(op.Kind);
        while (operators.Count > 0)
        {
            var top = BinaryLevel(operators[^1].Kind).Level;
            if (top < level || (top == level && assoc == Assoc.Right))
                break;
            if (top == level && assoc == Assoc.None)
            {
                Fail(SyntaxMessages.NotAssociative(op), op);
                return false;
            }

            Reduce(operands, operators);
        }

        operators.Add(op);
        Advance();
        return true;
    }

    // Replaces the last two operands with the last operator applied to them.
    static void Reduce(List<Node> operands, List<Token> operators)
    {
        var op = operators[^1];
        operators.RemoveAt(operators.Count - 1);
        var right = operands[^1];
        var left = operands[^2];
        operands.RemoveRange(operands.Count - 2, 2);
        operands.Add(MakeBinary(op, left, right));
    }

    static Node MakeBinary(Token op, Node left, Node right)
    {
        var at = Pos(op);
        return op.Kind switch
        {
            TokenKind.Plus => new Binary(BinaryOp.Add, left, right, at),
            TokenKind.Minus => new Binary(BinaryOp.Subtract, left, right, at),
            TokenKind.Star => new Binary(BinaryOp.Multiply, left, right, at),
            TokenKind.Slash => new Binary(BinaryOp.Divide, left, right, at),
            TokenKind.Percent => new Binary(BinaryOp.Modulo, left, right, at),
            TokenKind.Equal => new Binary(BinaryOp.Equal, left, right, at),
            TokenKind.NotEqual => new Binary(BinaryOp.NotEqual, left, right, at),
            TokenKind.Less => new Binary(BinaryOp.Less, left, right, at),
            TokenKind.LessEqual => new Binary(BinaryOp.LessEqual, left, right, at),
            TokenKind.Greater => new Binary(BinaryOp.Greater, left, right, at),
            TokenKind.GreaterEqual => new Binary(BinaryOp.GreaterEqual, left, right, at),
            TokenKind.And => new And(left, right, at),
            TokenKind.Or => new Or(left, right, at),
            TokenKind.Alt => new Alternative(left, right, at),
            TokenKind.Assign => new Call("_assign", [left, right], at),
            TokenKind.PipeAssign => new Call("_modify", [left, right], at),
            _ => MakeUpdate(op.Kind, left, right, at),
        };
    }

    // L op= R  =>  R as $v | _modify(L; . op $v)
    static Node MakeUpdate(TokenKind kind, Node left, Node right, Position at)
    {
        Node dot = new Identity(at);
        Node value = new Variable(UpdateVariable, at);
        var update = kind switch
        {
            TokenKind.PlusAssign => new Binary(BinaryOp.Add, dot, value, at),
            TokenKind.MinusAssign => new Binary(BinaryOp.Subtract, dot, value, at),
            TokenKind.StarAssign => new Binary(BinaryOp.Multiply, dot, value, at),
            TokenKind.SlashAssign => new Binary(BinaryOp.Divide, dot, value, at),
            TokenKind.PercentAssign => new Binary(BinaryOp.Modulo, dot, value, at),
            _ => (Node)new Alternative(dot, value, at),
        };
        return new Bind(right, [new VariablePattern(UpdateVariable, at)], new Call("_modify", [left, update], at), at);
    }

    // The minus tokens from first (included) to end (excluded) applied to node, the last one innermost.
    Node Negate(Node node, int first, int end)
    {
        for (var i = end - 1; i >= first; i--)
            node = new Negate(node, Pos(tokens[i]));
        return node;
    }

    bool IsBracketSuffix() => current.Kind == TokenKind.LeftBracket || (current.Kind == TokenKind.Dot && Peek.Kind == TokenKind.LeftBracket);

    bool IsSuffix() => current.Kind is TokenKind.Question or TokenKind.Field or TokenKind.LeftBracket or TokenKind.QuestionAlt
        || (current.Kind == TokenKind.Dot && Peek.Kind is TokenKind.LeftBracket or TokenKind.String or TokenKind.StringStart);

    // Suffix = '?' | FIELD | '.' String; the bracket suffixes are ParseBracketSuffix. '?//' is an
    // error here: it separates patterns only.
    Node? ParseSuffix(Node target)
    {
        var token = current;
        switch (token.Kind)
        {
            case TokenKind.Question:
                Advance();
                return new Try(target, null, Pos(token));
            case TokenKind.Field:
                Advance();
                return new Index(target, new Literal(JqValue.FromString(token.Text), Pos(token)), Pos(token));
            case TokenKind.QuestionAlt:
                return Fail(SyntaxMessages.QuestionAlt(token), token);
            default:
                Advance();
                var key = ParseString(null);
                return key is null ? null : new Index(target, key, Pos(token));
        }
    }

    // '.'? '[' ']' | '[' Pipe ']' | '[' Pipe ':' Pipe? ']' | '[' ':' Pipe ']'
    Node? ParseBracketSuffix(Node target)
    {
        if (current.Kind == TokenKind.Dot)
            Advance();
        var open = current;
        if (!Enter(open))
            return null;
        Advance();
        Node? result;
        if (current.Kind == TokenKind.RightBracket)
        {
            result = new Iterate(target, Pos(open));
        }
        else
        {
            Node? from = null;
            if (current.Kind != TokenKind.Colon)
            {
                from = ParsePipe();
                if (from is null)
                    return null;
            }

            if (current.Kind == TokenKind.Colon)
            {
                Advance();
                Node? to = null;
                if (from is null || current.Kind != TokenKind.RightBracket)
                {
                    to = ParsePipe();
                    if (to is null)
                        return null;
                }

                result = new Slice(target, from, to, Pos(open));
            }
            else
            {
                result = new Index(target, from!, Pos(open));
            }
        }

        return Close(TokenKind.RightBracket, "']'", result);
    }

    // '(' Pipe ')'
    Node? ParseParenthesized()
    {
        if (!Enter(current))
            return null;
        Advance();
        var inner = ParsePipe();
        return inner is null ? null : Close(TokenKind.RightParen, "')'", inner);
    }

    // '[' Pipe? ']'
    Node? ParseArray()
    {
        var open = current;
        if (!Enter(open))
            return null;
        Advance();
        if (current.Kind == TokenKind.RightBracket)
            return Close(TokenKind.RightBracket, "']'", new ArrayExpr(null, Pos(open)));
        var body = ParsePipe();
        return body is null ? null : Close(TokenKind.RightBracket, "']'", new ArrayExpr(body, Pos(open)));
    }

    // '.', '..', '.' String, NUMBER, FORMAT String?, FIELD, VAR, null, true, false, 'break' VAR.
    Node? ParseAtom()
    {
        var token = current;
        var at = Pos(token);
        switch (token.Kind)
        {
            case TokenKind.Dot:
                Advance();
                if (current.Kind is TokenKind.String or TokenKind.StringStart)
                {
                    var key = ParseString(null);
                    return key is null ? null : new Index(new Identity(at), key, at);
                }

                return new Identity(at);
            case TokenKind.DotDot:
                Advance();
                return new Call("recurse", [], at);
            case TokenKind.Number:
                Advance();
                return new Literal(JqValue.FromNumber(token.Number), at);
            case TokenKind.Format:
                // Any name parses: an unknown format is a runtime error (spec §9.9).
                Advance();
                return current.Kind is TokenKind.String or TokenKind.StringStart ? ParseString(token.Text) : new Format(token.Text, at);
            case TokenKind.Field:
                Advance();
                return new Index(new Identity(at), new Literal(JqValue.FromString(token.Text), at), at);
            case TokenKind.Variable:
                Advance();
                return new Variable(token.Text, at);
            case TokenKind.Ident:
                Advance();
                return new Literal(token.Text == "null" ? JqValue.Null : JqValue.FromBool(token.Text == "true"), at);
            case TokenKind.Break:
                Advance();
                if (current.Kind != TokenKind.Variable)
                    return Fail(SyntaxMessages.Expected("a label name ($name)", current), current);
                var label = current.Text;
                Advance();
                return new Break(label, at);
            default:
                return Fail(SyntaxMessages.Unexpected(token), token);
        }
    }

    // IDENT ('(' Pipe (';' Pipe)* ')')?
    Node? ParseCall()
    {
        var name = current;
        Advance();
        var arguments = new List<Node>();
        if (current.Kind == TokenKind.LeftParen)
        {
            if (!Enter(current))
                return null;
            do
            {
                Advance();
                var argument = ParsePipe();
                if (argument is null)
                    return null;
                arguments.Add(argument);
            }
            while (current.Kind == TokenKind.Semicolon);

            if (!Expect(TokenKind.RightParen, "')'"))
                return null;
            depth--;
        }

        return new Call(name.Text, [.. arguments], Pos(name));
    }

    // A STRING, or StringStart Pipe (StringMiddle Pipe)* StringEnd.
    Node? ParseString(string? format)
    {
        var start = current;
        if (start.Kind == TokenKind.String)
            return ParseLiteralString();
        if (!Enter(start))
            return null;
        var texts = new List<string>();
        var parts = new List<Node>();
        do
        {
            texts.Add(current.Text);
            Advance();
            var part = ParsePipe();
            if (part is null)
                return null;
            parts.Add(part);
            if (current.Kind is not (TokenKind.StringMiddle or TokenKind.StringEnd))
                return Fail(SyntaxMessages.Expected("')'", current), current);
        }
        while (current.Kind == TokenKind.StringMiddle);

        texts.Add(current.Text);
        Advance();
        depth--;
        return MakeTemplate(format, texts, parts, start);
    }

    Node ParseLiteralString()
    {
        var token = current;
        Advance();
        return new Literal(JqValue.FromString(token.Text), Pos(token));
    }

    static Node MakeTemplate(string? format, List<string> texts, List<Node> parts, Token start)
        => new Template(format, [.. texts], [.. parts], Pos(start));

    // '{' (Entry (',' Entry)* ','?)? '}'
    Node? ParseObject()
    {
        var open = current;
        if (!Enter(open))
            return null;
        Advance();
        var entries = new List<ObjectEntry>();
        while (current.Kind != TokenKind.RightBrace)
        {
            var entry = ParseObjectKey();
            if (entry is not null && entry.Value is null && current.Kind == TokenKind.Colon)
            {
                Advance();
                var value = ParseObjectValue();
                entry = value is null ? null : entry with { Value = value };
            }

            if (entry is null)
                return null;
            entries.Add(entry);
            if (current.Kind == TokenKind.Comma)
                Advance();
            else if (current.Kind != TokenKind.RightBrace)
                return Fail(SyntaxMessages.Expected("',' or '}'", current), current);
        }

        Advance();
        depth--;
        return new ObjectExpr([.. entries], Pos(open));
    }

    // Entry = (IDENT | Keyword | VAR | String | '(' Pipe ')') ':' ObjVal  |  IDENT | Keyword | VAR | String
    // The key of an entry, with a null value when a ':' and the value follow. A shorthand is
    // complete: {$x} has its value, {name} and {"s"} have none and no ':' after them.
    ObjectEntry? ParseObjectKey()
    {
        var token = current;
        var at = Pos(token);
        if (token.Kind == TokenKind.Variable)
        {
            Advance();
            return current.Kind == TokenKind.Colon
                ? new ObjectEntry(new Variable(token.Text, at), null, at)
                : new ObjectEntry(new Literal(JqValue.FromString(token.Text), at), new Variable(token.Text, at), at);
        }

        var key = token.Kind == TokenKind.LeftParen ? ParseParenthesizedKey() : ParseNameKey();
        if (key is null)
            return null;
        if (token.Kind == TokenKind.LeftParen && current.Kind != TokenKind.Colon)
            return Fail<ObjectEntry>(SyntaxMessages.Expected("':'", current), current);
        return new ObjectEntry(key, null, at);
    }

    // A key written as a name or a string (objects and object patterns): IDENT | Keyword | FORMAT? String.
    Node? ParseNameKey()
    {
        var token = current;
        if (token.Kind == TokenKind.Ident || Token.IsKeyword(token.Kind))
        {
            Advance();
            return new Literal(JqValue.FromString(token.Text), Pos(token));
        }

        if (token.Kind is TokenKind.String or TokenKind.StringStart)
            return ParseString(null);

        if (token.Kind == TokenKind.Format && Peek.Kind is TokenKind.String or TokenKind.StringStart)
        {
            Advance();
            return ParseString(token.Text);
        }

        return token.Kind is TokenKind.Comma or TokenKind.RightBrace or TokenKind.RightBracket or TokenKind.RightParen
            or TokenKind.Semicolon or TokenKind.Colon or TokenKind.Pipe or TokenKind.Eof or TokenKind.Error
            ? Fail(SyntaxMessages.Unexpected(token), token)
            : Fail(SyntaxMessages.MayNeedParentheses, token);
    }

    // '(' Pipe ')' as a key; a number, boolean or null literal there is a static error (spec §3.2).
    Node? ParseParenthesizedKey()
    {
        var open = current;
        if (!Enter(open))
            return null;
        Advance();
        var key = ParsePipe();
        if (key is null || !Expect(TokenKind.RightParen, "')'"))
            return null;
        depth--;
        if (key is Literal literal && literal.Value.Kind != JqKind.String)
            return Fail(SyntaxMessages.InvalidObjectKey(literal.Value), literal.Position);
        return key;
    }

    // 'reduce' Alt 'as' Patterns '(' Pipe ';' Pipe ')'   |   'foreach' Alt 'as' Patterns '(' Pipe ';' Pipe (';' Pipe)? ')'
    Node? ParseReduceOrForeach()
    {
        if (!Enter(current))
            return null;
        var head = ParseReduceOrForeachHead();
        if (head is null)
            return null;
        var bodies = new List<Node>();
        do
        {
            if (bodies.Count > 0)
                Advance();
            var body = ParsePipe();
            if (body is null)
                return null;
            bodies.Add(body);
        }
        while (current.Kind == TokenKind.Semicolon && bodies.Count < (head.Kind == NodeKind.Reduce ? 2 : 3));

        return bodies.Count < 2
            ? Fail(SyntaxMessages.Expected("';'", current), current)
            : Close(TokenKind.RightParen, "')'", CompleteReduceOrForeach(head, bodies));
    }

    // ('reduce' | 'foreach') Alt 'as' Patterns '(', as a node whose bodies are Hole.
    Node? ParseReduceOrForeachHead()
    {
        var keyword = current;
        Advance();
        var source = ParseExpression(operandOnly: false);
        if (source is null || !Expect(TokenKind.As, "'as'"))
            return null;
        var patterns = ParsePatterns();
        if (patterns is null || !Expect(TokenKind.LeftParen, "'('"))
            return null;
        return keyword.Kind == TokenKind.Reduce
            ? new Reduce(source, patterns.Value, Hole, Hole, Pos(keyword))
            : new Foreach(source, patterns.Value, Hole, Hole, Hole, Pos(keyword));
    }

    static Node CompleteReduceOrForeach(Node head, List<Node> bodies) => head switch
    {
        Reduce reduce => reduce with { Init = bodies[0], Update = bodies[1] },
        Foreach each => each with { Init = bodies[0], Update = bodies[1], Extract = bodies.Count > 2 ? bodies[2] : new Identity(each.Position) },
        _ => head,
    };

    // 'if' Pipe 'then' Pipe ('elif' Pipe 'then' Pipe)* ('else' Pipe)? 'end'. Each branch is a partial
    // If whose else is Hole, completed from the last to the first.
    Node? ParseIf()
    {
        if (!Enter(current))
            return null;
        var branches = new List<Node>();
        do
        {
            var keyword = current;
            Advance();
            var condition = ParsePipe();
            if (condition is null || !Expect(TokenKind.Then, "'then'"))
                return null;
            var then = ParsePipe();
            if (then is null)
                return null;
            branches.Add(PartialIf(condition, then, keyword));
        }
        while (current.Kind == TokenKind.Elif);

        Node? otherwise = null;
        if (current.Kind == TokenKind.Else)
        {
            Advance();
            otherwise = ParsePipe();
            if (otherwise is null)
                return null;
        }

        return Close(TokenKind.End, "'end'", Complete(branches, otherwise ?? new Identity(Pos(current))));
    }

    static Node PartialIf(Node condition, Node then, Token keyword) => new If(condition, then, Hole, Pos(keyword));

    // 'try' Unary ('catch' Unary)?
    Node? ParseTry()
    {
        var keyword = current;
        if (!Enter(keyword))
            return null;
        Advance();
        var body = ParseExpression(operandOnly: true);
        if (body is null)
            return null;
        Node? handler = null;
        if (current.Kind == TokenKind.Catch)
        {
            Advance();
            handler = ParseExpression(operandOnly: true);
            if (handler is null)
                return null;
        }

        depth--;
        return new Try(body, handler, Pos(keyword));
    }

    // Patterns = Pattern ('?//' Pattern)*
    ImmutableArray<Pattern>? ParsePatterns()
    {
        var patterns = new List<Pattern>();
        while (true)
        {
            var pattern = ParsePattern();
            if (pattern is null)
                return null;
            patterns.Add(pattern);
            if (current.Kind != TokenKind.QuestionAlt)
                return [.. patterns];
            Advance();
        }
    }

    // Pattern = VAR | '[' Pattern (',' Pattern)* ']' | '{' PEntry (',' PEntry)* '}'
    Pattern? ParsePattern()
    {
        var token = current;
        var at = Pos(token);
        switch (token.Kind)
        {
            case TokenKind.Variable when !IsLocation(token):
                Advance();
                return new VariablePattern(token.Text, at);
            case TokenKind.LeftBracket:
                {
                    if (!Enter(token))
                        return null;
                    var elements = new List<Pattern>();
                    do
                    {
                        Advance();
                        var element = ParsePattern();
                        if (element is null)
                            return null;
                        elements.Add(element);
                    }
                    while (current.Kind == TokenKind.Comma);

                    if (!Expect(TokenKind.RightBracket, "']'"))
                        return null;
                    depth--;
                    return new ArrayPattern([.. elements], at);
                }

            case TokenKind.LeftBrace:
                {
                    if (!Enter(token))
                        return null;
                    var entries = new List<PatternEntry>();
                    do
                    {
                        Advance();
                        var entry = ParsePatternEntry();
                        if (entry is null)
                            return null;
                        entries.Add(entry);
                    }
                    while (current.Kind == TokenKind.Comma);

                    if (!Expect(TokenKind.RightBrace, "'}'"))
                        return null;
                    depth--;
                    return new ObjectPattern([.. entries], at);
                }

            default:
                return Fail<Pattern>(SyntaxMessages.Expected("a pattern ($name, [...] or {...})", token), token);
        }
    }

    // PEntry = VAR (':' Pattern)? | (IDENT | Keyword | String | '(' Pipe ')') ':' Pattern
    PatternEntry? ParsePatternEntry()
    {
        var token = current;
        var at = Pos(token);
        Node? key;
        string? variable = null;
        if (IsLocation(token))
            return Fail<PatternEntry>(SyntaxMessages.Unexpected(token), token);
        if (token.Kind == TokenKind.Variable)
        {
            Advance();
            key = new Literal(JqValue.FromString(token.Text), at);
            variable = token.Text;
            if (current.Kind != TokenKind.Colon)
                return new PatternEntry(key, variable, null, at);
        }
        else
        {
            key = token.Kind == TokenKind.LeftParen ? ParseParenthesizedKey() : ParseNameKey();
            if (key is null)
                return null;
        }

        if (!Expect(TokenKind.Colon, "':'"))
            return null;
        var value = ParsePattern();
        return value is null ? null : new PatternEntry(key, variable, value, at);
    }

    // Opens one level of nesting (spec §11).
    bool Enter(Token token)
    {
        if (++depth <= MaxNesting)
            return true;
        Fail(SyntaxMessages.TooDeeplyNested, token);
        return false;
    }

    // Expects the token that closes a nesting level, and leaves the level.
    Node? Close(TokenKind kind, string what, Node node)
    {
        if (!Expect(kind, what))
            return null;
        depth--;
        return node;
    }

    bool Expect(TokenKind kind, string what)
    {
        if (current.Kind != kind)
        {
            Fail(SyntaxMessages.Expected(what, current), current);
            return false;
        }

        Advance();
        return true;
    }

    Node? Fail(string message, Token token) => Fail<Node>(message, token);

    Node? Fail(string message, Position position)
    {
        error ??= new SyntaxError(message, position.Line, position.Column);
        return null;
    }

    JqValue? FailValue(string message, Token token)
    {
        Fail(message, token);
        return null;
    }

    // Records the error and returns null. At a lexical error token, the lexer's message wins.
    T? Fail<T>(string message, Token token) where T : class
    {
        error ??= token.Kind == TokenKind.Error
            ? new SyntaxError(token.Text, token.Line, token.Column)
            : new SyntaxError(message, token.Line, token.Column);
        return null;
    }
}