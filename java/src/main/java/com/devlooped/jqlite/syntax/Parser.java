package com.devlooped.jqlite.syntax;

import com.devlooped.jqlite.syntax.Ast.Alternative;
import com.devlooped.jqlite.syntax.Ast.And;
import com.devlooped.jqlite.syntax.Ast.ArrayExpr;
import com.devlooped.jqlite.syntax.Ast.ArrayPattern;
import com.devlooped.jqlite.syntax.Ast.Binary;
import com.devlooped.jqlite.syntax.Ast.BinaryOp;
import com.devlooped.jqlite.syntax.Ast.Bind;
import com.devlooped.jqlite.syntax.Ast.Break;
import com.devlooped.jqlite.syntax.Ast.Call;
import com.devlooped.jqlite.syntax.Ast.Comma;
import com.devlooped.jqlite.syntax.Ast.Definition;
import com.devlooped.jqlite.syntax.Ast.Foreach;
import com.devlooped.jqlite.syntax.Ast.Format;
import com.devlooped.jqlite.syntax.Ast.FuncDef;
import com.devlooped.jqlite.syntax.Ast.Identity;
import com.devlooped.jqlite.syntax.Ast.If;
import com.devlooped.jqlite.syntax.Ast.Import;
import com.devlooped.jqlite.syntax.Ast.Index;
import com.devlooped.jqlite.syntax.Ast.Iterate;
import com.devlooped.jqlite.syntax.Ast.Label;
import com.devlooped.jqlite.syntax.Ast.Literal;
import com.devlooped.jqlite.syntax.Ast.Negate;
import com.devlooped.jqlite.syntax.Ast.Node;
import com.devlooped.jqlite.syntax.Ast.ObjectEntry;
import com.devlooped.jqlite.syntax.Ast.ObjectExpr;
import com.devlooped.jqlite.syntax.Ast.ObjectPattern;
import com.devlooped.jqlite.syntax.Ast.Or;
import com.devlooped.jqlite.syntax.Ast.Param;
import com.devlooped.jqlite.syntax.Ast.Pattern;
import com.devlooped.jqlite.syntax.Ast.PatternEntry;
import com.devlooped.jqlite.syntax.Ast.Pipe;
import com.devlooped.jqlite.syntax.Ast.Position;
import com.devlooped.jqlite.syntax.Ast.Program;
import com.devlooped.jqlite.syntax.Ast.Reduce;
import com.devlooped.jqlite.syntax.Ast.Slice;
import com.devlooped.jqlite.syntax.Ast.Template;
import com.devlooped.jqlite.syntax.Ast.Try;
import com.devlooped.jqlite.syntax.Ast.Variable;
import com.devlooped.jqlite.syntax.Ast.VariablePattern;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObjectBuilder;
import com.devlooped.jqlite.values.JqValue;
import java.util.ArrayList;
import java.util.List;

/**
 * Recursive descent parser of jqlite (spec §3) over the tokens of {@link Lexer}. It never throws for
 * bad input: it returns the {@link Program} or one {@link SyntaxError}.
 *
 * <p>Rewritten by the parser (spec §3.3), so the AST has one form for each:
 * <ul>
 * <li>{@code ..} is the call {@code recurse} (resolved like any call: a user {@code recurse/0} shadows it, as in jq).</li>
 * <li>{@code E?} and {@code try E} are {@link Try} without a handler (which discards the error).</li>
 * <li>{@code .name}, {@code ."s"}, {@code T.name}, {@code T."s"} are {@link Index} with a literal or template key.</li>
 * <li>{@code elif} nests {@link If}; a missing {@code else} is {@code .}; a {@code foreach} without extract has {@code .}.</li>
 * <li><code>{$x}</code> is <code>{"x": $x}</code>. The string shorthands keep a null value, see {@link ObjectEntry}.</li>
 * <li>{@code L = R} is {@code _assign(L; R)}, {@code L |= R} is {@code _modify(L; R)}, {@code L op= R} is
 * {@code R as $v | _modify(L; . op $v)} ({@code //=} likewise with {@code //}), where {@code $v} is
 * {@link #UPDATE_VARIABLE}, a name no program can write.</li>
 * <li>A string or {@code @f "s"} without interpolation is a {@link Literal} (a format applies to interpolations only); parentheses leave no node.</li>
 * </ul>
 *
 * <p>Where jq 1.8.2 parses differently from the EBNF of spec §3.2, this parser follows jq:
 * {@code def} and {@code label} start a pipe or comma item only, never an operand
 * ({@code 1 + def f: 1; f} fails); object values are {@code Alt ('|' Alt)*} without {@code as};
 * object constructions accept a trailing comma; {@code .[:]} is an error; a definition name may
 * be qualified ({@code def a::b:}); module metadata constants have no negative numbers.
 *
 * <p>Nesting (brackets, interpolations, {@code if}, {@code reduce}, {@code foreach}, {@code try},
 * {@code def}) is limited to {@link #MAX_NESTING}; within the limit the host stack is not
 * exhausted. Sequences do not recurse per element: pipes, commas, bindings and leading definitions
 * are folded from a list ({@link #parsePipe}), binary operators are parsed with an operator stack
 * ({@link #parseExpression}), and runs of unary minus and of {@code elif} are loops.
 *
 * <p>Host-specific (Java): a JVM frame of this parser is several times larger than the reference's
 * (C1-compiled frames take up to about 850 KB for 1,000 levels), so 1,000 levels would not fit a
 * 1 MB thread stack with a safe margin. Every {@link #HOP_LEVELS} levels of nesting, the parse
 * continues on a thread with a stack of its own while the current one waits ({@link #onFreshStack},
 * as Runtime/StackGuard does for the interpreter). A port whose frames fit (.NET, Go, Rust) has no
 * such step.
 */
public final class Parser {
    /** The nesting limit of spec §11. */
    public static final int MAX_NESTING = 1000;

    /** The variable that the rewriting of {@code L op= R} binds; '*' cannot appear in a source name. */
    public static final String UPDATE_VARIABLE = "*update";

    /** {@code $__loc__}, the location object of §7.5: the resolver replaces it; no pattern, parameter or import binds it. */
    public static final String LOCATION_VARIABLE = "__loc__";

    /** The outcome of a parse: the program, or the error. */
    public record Parsed(Program program, SyntaxError error) {
    }

    private enum Assoc {
        LEFT,
        RIGHT,
        NONE,
    }

    /**
     * The binary operators (spec §3.1, levels 4 to 10) with their level, higher binding tighter;
     * level 0 for any other token. Levels 1 to 3 ({@code |}, {@code ,}, {@code as}) are parsed by
     * {@link #parsePipe}, levels 11 to 13 (unary minus, suffixes, terms) by {@link #parseExpression}.
     */
    private static int binaryLevel(TokenKind kind) {
        return switch (kind) {
            case ALT -> 4;
            case ASSIGN, PIPE_ASSIGN, PLUS_ASSIGN, MINUS_ASSIGN, STAR_ASSIGN, SLASH_ASSIGN, PERCENT_ASSIGN, ALT_ASSIGN -> 5;
            case OR -> 6;
            case AND -> 7;
            case EQUAL, NOT_EQUAL, LESS, LESS_EQUAL, GREATER, GREATER_EQUAL -> 8;
            case PLUS, MINUS -> 9;
            case STAR, SLASH, PERCENT -> 10;
            default -> 0;
        };
    }

    /** The associativity of the binary operators of {@link #binaryLevel}. */
    private static Assoc binaryAssoc(TokenKind kind) {
        return switch (binaryLevel(kind)) {
            case 4 -> Assoc.RIGHT;
            case 5, 8 -> Assoc.NONE;
            default -> Assoc.LEFT;
        };
    }

    private final List<Token> tokens;
    // The token at next, kept in a field: reading it is the commonest operation of the parser.
    private Token current;
    private int next;
    private int depth;
    // The nesting depth at which the current stack started (the last hop).
    private int stackStart;
    private SyntaxError error;

    /** The levels of nesting parsed on one stack before the parse continues on a fresh one. */
    static final int HOP_LEVELS = 128;

    private static final long STACK_SIZE = 16L * 1024 * 1024;

    private Parser(String source) {
        tokens = Lexer.tokenize(source);
        current = tokens.get(0);
    }

    /** Parses a program (spec §3.2, rule {@code Program}). */
    public static Parsed tryParse(String source) {
        return new Parser(source).run(false);
    }

    /** Parses a module file (spec §3.2, rule {@code ModuleBody}): directives and definitions only. */
    public static Parsed tryParseModule(String source) {
        return new Parser(source).run(true);
    }

    private Parsed run(boolean module) {
        Program program = parseProgram(module);
        return new Parsed(program, program == null ? error : null);
    }

    private Token peek() {
        return tokens.get(Math.min(next + 1, tokens.size() - 1));
    }

    private void advance() {
        if (next < tokens.size() - 1) {
            current = tokens.get(++next);
        }
    }

    private static Position pos(Token token) {
        return new Position(token.line(), token.column());
    }

    private static boolean isLocation(Token token) {
        return token.kind() == TokenKind.VARIABLE && token.text().equals(LOCATION_VARIABLE);
    }

    // Program = ModuleDecl? Import* FuncDef* Pipe?      ModuleBody = ModuleDecl? Import* FuncDef*
    private Program parseProgram(boolean module) {
        JqValue metadata = null;
        if (current.kind() == TokenKind.MODULE) {
            advance();
            JqValue value = parseMetadata();
            if (value == null || !expect(TokenKind.SEMICOLON, "';'")) {
                return null;
            }
            metadata = value;
        }

        List<Import> imports = new ArrayList<>();
        while (current.kind() == TokenKind.IMPORT || current.kind() == TokenKind.INCLUDE) {
            Import directive = parseImport();
            if (directive == null) {
                return null;
            }
            imports.add(directive);
        }

        List<Definition> definitions = new ArrayList<>();
        while (current.kind() == TokenKind.DEF) {
            Definition definition = parseDefinition();
            if (definition == null) {
                return null;
            }
            definitions.add(definition);
        }

        Node body = null;
        if (module) {
            if (current.kind() != TokenKind.EOF) {
                return failNull(SyntaxMessages.MODULE_HAS_BODY, current);
            }
        } else {
            body = current.kind() == TokenKind.EOF ? new Identity(pos(current)) : parsePipe();
            if (body == null) {
                return null;
            }
            if (current.kind() != TokenKind.EOF) {
                return failNull(SyntaxMessages.unexpected(current), current);
            }
        }

        return new Program(metadata, List.copyOf(imports), List.copyOf(definitions), body);
    }

    // Import = 'import' STRING 'as' (IDENT | VAR) ConstObject? ';' | 'include' STRING ConstObject? ';'
    private Import parseImport() {
        Token keyword = current;
        advance();
        if (current.kind() == TokenKind.STRING_START) {
            return failNull(SyntaxMessages.IMPORT_PATH_NOT_CONSTANT, current);
        }
        if (current.kind() != TokenKind.STRING) {
            return failNull(SyntaxMessages.expected("a module path string", current), current);
        }
        String path = current.text();
        advance();

        String alias = null;
        boolean isData = false;
        if (keyword.kind() == TokenKind.IMPORT) {
            if (!expect(TokenKind.AS, "'as'")) {
                return null;
            }
            if ((current.kind() != TokenKind.IDENT && current.kind() != TokenKind.VARIABLE) || isLocation(current)) {
                return failNull(SyntaxMessages.expected("a module name or a $name", current), current);
            }
            alias = current.text();
            isData = current.kind() == TokenKind.VARIABLE;
            advance();
        }

        JqValue metadata = null;
        if (current.kind() == TokenKind.LEFT_BRACE) {
            metadata = parseMetadata();
            if (metadata == null) {
                return null;
            }
        }

        return expect(TokenKind.SEMICOLON, "';'") ? new Import(path, alias, isData, metadata, pos(keyword)) : null;
    }

    // The metadata of 'module' and of imports: a constant object.
    private JqValue parseMetadata() {
        Token start = current;
        JqValue value = parseConstant(start);
        if (value == null) {
            return null;
        }
        return value.kind() == JqKind.OBJECT ? value : failNull(SyntaxMessages.METADATA_NOT_OBJECT, start);
    }

    // Const = NUMBER | STRING | null | true | false | '[' (Const (',' Const)*)? ']' | '{' (CEntry (',' CEntry)* ','?)? '}'
    // CEntry = (IDENT | Keyword | STRING) ':' Const. Any other token: "Module metadata must be constant" at the metadata.
    private JqValue parseConstant(Token start) {
        return switch (current.kind()) {
            case LEFT_BRACKET -> parseConstantArray(start);
            case LEFT_BRACE -> parseConstantObject(start);
            default -> parseConstantScalar(start);
        };
    }

    private JqValue parseConstantScalar(Token start) {
        Token token = current;
        advance();
        if (token.kind() == TokenKind.NUMBER) {
            return JqValue.fromNumber(token.number());
        }
        if (token.kind() == TokenKind.STRING) {
            return JqValue.fromString(token.text());
        }
        if (token.kind() == TokenKind.IDENT && token.text().equals("null")) {
            return JqValue.NULL;
        }
        if (token.kind() == TokenKind.IDENT && (token.text().equals("true") || token.text().equals("false"))) {
            return JqValue.fromBool(token.text().equals("true"));
        }
        return failNull(SyntaxMessages.METADATA_NOT_CONSTANT, start);
    }

    private JqValue parseConstantArray(Token start) {
        if (!enter(current)) {
            return null;
        }
        advance();
        List<JqValue> items = new ArrayList<>();
        while (current.kind() != TokenKind.RIGHT_BRACKET) {
            if (!items.isEmpty() && !expectConstant(TokenKind.COMMA, start)) {
                return null;
            }
            JqValue item = parseConstant(start);
            if (item == null) {
                return null;
            }
            items.add(item);
        }

        advance();
        depth--;
        return JqArray.fromOwned(items.toArray(new JqValue[0]));
    }

    private JqValue parseConstantObject(Token start) {
        if (!enter(current)) {
            return null;
        }
        advance();
        JqObjectBuilder builder = new JqObjectBuilder();
        while (current.kind() != TokenKind.RIGHT_BRACE) {
            Token key = current;
            if (key.kind() != TokenKind.IDENT && key.kind() != TokenKind.STRING && !Token.isKeyword(key.kind())) {
                return failNull(SyntaxMessages.METADATA_NOT_CONSTANT, start);
            }
            advance();
            if (!expectConstant(TokenKind.COLON, start)) {
                return null;
            }
            JqValue value = parseConstant(start);
            if (value == null) {
                return null;
            }
            builder.set(key.text(), value);
            if (current.kind() == TokenKind.COMMA) {
                advance();
            } else if (current.kind() != TokenKind.RIGHT_BRACE) {
                return failNull(SyntaxMessages.METADATA_NOT_CONSTANT, start);
            }
        }

        advance();
        depth--;
        return builder.toObject();
    }

    private boolean expectConstant(TokenKind kind, Token start) {
        if (current.kind() != kind) {
            failNull(SyntaxMessages.METADATA_NOT_CONSTANT, start);
            return false;
        }

        advance();
        return true;
    }

    // FuncDef = 'def' IDENT ('(' Param (';' Param)* ')')? ':' Pipe ';'
    private Definition parseDefinition() {
        Token keyword = current;
        if (!enter(keyword)) {
            return null;
        }
        advance();
        if (current.kind() != TokenKind.IDENT) {
            return failNull(SyntaxMessages.expected("a function name", current), current);
        }
        String name = current.text();
        advance();
        List<Param> parameters = parseParams();
        if (parameters == null || !expect(TokenKind.COLON, "':'")) {
            return null;
        }
        Node body = parsePipe();
        if (body == null || !expect(TokenKind.SEMICOLON, "';'")) {
            return null;
        }
        depth--;
        return new Definition(name, List.copyOf(parameters), body, pos(keyword));
    }

    // ('(' Param (';' Param)* ')')?      Param = IDENT | VAR
    private List<Param> parseParams() {
        List<Param> parameters = new ArrayList<>();
        if (current.kind() != TokenKind.LEFT_PAREN) {
            return parameters;
        }
        do {
            advance();
            if ((current.kind() != TokenKind.IDENT && current.kind() != TokenKind.VARIABLE) || isLocation(current)) {
                return failNull(SyntaxMessages.expected("a parameter name", current), current);
            }
            parameters.add(new Param(current.text(), current.kind() == TokenKind.VARIABLE, pos(current)));
            advance();
        } while (current.kind() == TokenKind.SEMICOLON);

        return expect(TokenKind.RIGHT_PAREN, "')'") ? parameters : null;
    }

    // Pipe = Item (',' Item)* ('|' Pipe)?, where an Item is
    //   'def' … ';' Pipe  |  'label' VAR '|' Pipe  |  Alt 'as' Patterns '|' Pipe  |  Alt
    // The first three, and '|', take the rest of the pipe. To avoid recursing per element, each of
    // them is kept as a partial node whose last child is HOLE, and the partial nodes are completed
    // from the last to the first when the pipe ends.
    private Node parsePipe() {
        if (depth - stackStart >= HOP_LEVELS) {
            return onFreshStack(this::parsePipe);
        }

        List<Node> pending = new ArrayList<>();
        Node items = null;
        Token comma = current;
        while (true) {
            Node partial;
            if (current.kind() == TokenKind.DEF) {
                partial = parseFuncDef();
            } else if (current.kind() == TokenKind.LABEL) {
                partial = parseLabel();
            } else {
                Node item = parseExpression(false);
                if (item == null) {
                    return null;
                }
                if (current.kind() == TokenKind.AS) {
                    partial = parseBinding(item);
                } else {
                    items = addItem(items, item, comma);
                    if (current.kind() == TokenKind.COMMA) {
                        comma = current;
                        advance();
                        continue;
                    }

                    if (current.kind() != TokenKind.PIPE) {
                        break;
                    }
                    pending.add(partialPipe(items, current));
                    advance();
                    items = null;
                    continue;
                }
            }

            if (partial == null) {
                return null;
            }

            // A binding takes the rest of the pipe; the items before it are the left side of a comma.
            pending.add(items == null ? partial : addItem(items, partial, comma));
            items = null;
        }

        return complete(pending, items);
    }

    private static Node addItem(Node items, Node item, Token comma) {
        return items == null ? item : new Comma(items, item, pos(comma));
    }

    private static Node partialPipe(Node left, Token bar) {
        return new Pipe(left, HOLE, pos(bar));
    }

    // The pending partial nodes of parsePipe completed from the last to the first.
    private static Node complete(List<Node> pending, Node last) {
        for (int i = pending.size() - 1; i >= 0; i--) {
            last = complete(pending.get(i), last);
        }
        return last;
    }

    // The placeholder for "the rest of the pipe" in a partial node of parsePipe.
    private static final Node HOLE = new Identity(new Position(0, 0));

    // A partial node of parsePipe with its hole filled.
    private static Node complete(Node partial, Node rest) {
        return switch (partial) {
            case Pipe pipe -> new Pipe(pipe.left(), rest, pipe.position());
            case Comma comma -> new Comma(comma.left(), complete(comma.right(), rest), comma.position());
            case FuncDef definition -> new FuncDef(definition.definition(), rest, definition.position());
            case Label label -> new Label(label.name(), rest, label.position());
            case Bind bind -> new Bind(bind.source(), bind.patterns(), rest, bind.position());
            case If branch -> new If(branch.condition(), branch.then(), rest, branch.position());
            default -> rest;
        };
    }

    // 'def' … ';', as a partial node.
    private Node parseFuncDef() {
        Definition definition = parseDefinition();
        return definition == null ? null : new FuncDef(definition, HOLE, definition.position());
    }

    // 'label' VAR '|', as a partial node.
    private Node parseLabel() {
        Token start = current;
        advance();
        if (current.kind() != TokenKind.VARIABLE) {
            return fail(SyntaxMessages.expected("a label name ($name)", current), current);
        }
        String name = current.text();
        advance();
        return expect(TokenKind.PIPE, "'|'") ? new Label(name, HOLE, pos(start)) : null;
    }

    // Source 'as' Patterns '|', as a partial node.
    private Node parseBinding(Node source) {
        Token keyword = current;
        advance();
        List<Pattern> patterns = parsePatterns();
        if (patterns == null || !expect(TokenKind.PIPE, "'|'")) {
            return null;
        }
        return new Bind(source, patterns, HOLE, pos(keyword));
    }

    // ObjVal = Alt ('|' Alt)*: an object value, which has no top-level ',' and no 'as', 'def' or 'label' (as jq).
    private Node parseObjectValue() {
        List<Node> items = new ArrayList<>();
        List<Token> bars = new ArrayList<>();
        while (true) {
            Node item = parseExpression(false);
            if (item == null) {
                return null;
            }
            items.add(item);
            if (current.kind() != TokenKind.PIPE) {
                break;
            }
            bars.add(current);
            advance();
        }

        Node result = items.get(items.size() - 1);
        for (int i = items.size() - 2; i >= 0; i--) {
            result = new Pipe(items.get(i), result, pos(bars.get(i)));
        }
        return result;
    }

    // Levels 4 to 13 of the precedence table: binary operators with an operand stack and an
    // operator stack (shunting-yard), so a chain of any length does not recurse; each operand is
    // Unary = '-'* Postfix, Postfix = Term Suffix*. With operandOnly, one operand (the Unary of
    // 'try' and 'catch').
    private Node parseExpression(boolean operandOnly) {
        List<Node> operands = new ArrayList<>();
        List<Token> operators = new ArrayList<>();
        while (true) {
            int firstMinus = next;
            while (current.kind() == TokenKind.MINUS) {
                advance();
            }
            int termStart = next;

            Node operand = depth - stackStart >= HOP_LEVELS ? onFreshStack(this::parseTerm) : parseTerm();

            while (operand != null && isSuffix()) {
                operand = isBracketSuffix() ? parseBracketSuffix(operand) : parseSuffix(operand);
            }
            if (operand == null) {
                return null;
            }
            operand = negate(operand, firstMinus, termStart);
            if (operandOnly) {
                return operand;
            }

            operands.add(operand);
            if (binaryLevel(current.kind()) == 0) {
                return reduceAll(operands, operators);
            }
            if (!pushOperator(operands, operators)) {
                return null;
            }
        }
    }

    // The term an operand starts with.
    private Node parseTerm() {
        return switch (current.kind()) {
            case LEFT_PAREN -> parseParenthesized();
            case LEFT_BRACKET -> parseArray();
            case LEFT_BRACE -> parseObject();
            case STRING, STRING_START -> parseString(null);
            case REDUCE, FOREACH -> parseReduceOrForeach();
            case IF -> parseIf();
            case TRY -> parseTry();
            case IDENT -> isLiteralName(current.text()) ? parseAtom() : parseCall();
            default -> parseAtom();
        };
    }

    // Runs a step of the parse on a thread with a stack of its own and waits for it; the parser's
    // state is only touched by one thread at a time.
    private Node onFreshStack(java.util.function.Supplier<Node> step) {
        int saved = stackStart;
        stackStart = depth;
        Node[] result = new Node[1];
        Throwable[] failure = new Throwable[1];
        Thread thread = new Thread(null, () -> {
            try {
                result[0] = step.get();
            } catch (RuntimeException | Error e) {
                failure[0] = e;
            }
        }, "jqlite-parse", STACK_SIZE);
        thread.start();
        boolean interrupted = false;
        while (true) {
            try {
                thread.join();
                break;
            } catch (InterruptedException e) {
                interrupted = true;
            }
        }
        if (interrupted) {
            Thread.currentThread().interrupt();
        }
        stackStart = saved;
        if (failure[0] instanceof RuntimeException runtime) {
            throw runtime;
        }
        if (failure[0] instanceof Error fatal) {
            throw fatal;
        }
        return result[0];
    }

    private static Node reduceAll(List<Node> operands, List<Token> operators) {
        while (!operators.isEmpty()) {
            reduce(operands, operators);
        }
        return operands.get(0);
    }

    private static boolean isLiteralName(String name) {
        return name.equals("null") || name.equals("true") || name.equals("false");
    }

    // Reduces the operators that bind at least as tightly as the current one, then pushes it.
    private boolean pushOperator(List<Node> operands, List<Token> operators) {
        Token op = current;
        int level = binaryLevel(op.kind());
        Assoc assoc = binaryAssoc(op.kind());
        while (!operators.isEmpty()) {
            int top = binaryLevel(operators.get(operators.size() - 1).kind());
            if (top < level || (top == level && assoc == Assoc.RIGHT)) {
                break;
            }
            if (top == level && assoc == Assoc.NONE) {
                fail(SyntaxMessages.notAssociative(op), op);
                return false;
            }

            reduce(operands, operators);
        }

        operators.add(op);
        advance();
        return true;
    }

    // Replaces the last two operands with the last operator applied to them.
    private static void reduce(List<Node> operands, List<Token> operators) {
        Token op = operators.remove(operators.size() - 1);
        Node right = operands.remove(operands.size() - 1);
        Node left = operands.remove(operands.size() - 1);
        operands.add(makeBinary(op, left, right));
    }

    private static Node makeBinary(Token op, Node left, Node right) {
        Position at = pos(op);
        return switch (op.kind()) {
            case PLUS -> new Binary(BinaryOp.ADD, left, right, at);
            case MINUS -> new Binary(BinaryOp.SUBTRACT, left, right, at);
            case STAR -> new Binary(BinaryOp.MULTIPLY, left, right, at);
            case SLASH -> new Binary(BinaryOp.DIVIDE, left, right, at);
            case PERCENT -> new Binary(BinaryOp.MODULO, left, right, at);
            case EQUAL -> new Binary(BinaryOp.EQUAL, left, right, at);
            case NOT_EQUAL -> new Binary(BinaryOp.NOT_EQUAL, left, right, at);
            case LESS -> new Binary(BinaryOp.LESS, left, right, at);
            case LESS_EQUAL -> new Binary(BinaryOp.LESS_EQUAL, left, right, at);
            case GREATER -> new Binary(BinaryOp.GREATER, left, right, at);
            case GREATER_EQUAL -> new Binary(BinaryOp.GREATER_EQUAL, left, right, at);
            case AND -> new And(left, right, at);
            case OR -> new Or(left, right, at);
            case ALT -> new Alternative(left, right, at);
            case ASSIGN -> new Call("_assign", List.of(left, right), at);
            case PIPE_ASSIGN -> new Call("_modify", List.of(left, right), at);
            default -> makeUpdate(op.kind(), left, right, at);
        };
    }

    // L op= R  =>  R as $v | _modify(L; . op $v)
    private static Node makeUpdate(TokenKind kind, Node left, Node right, Position at) {
        Node dot = new Identity(at);
        Node value = new Variable(UPDATE_VARIABLE, at);
        Node update = switch (kind) {
            case PLUS_ASSIGN -> new Binary(BinaryOp.ADD, dot, value, at);
            case MINUS_ASSIGN -> new Binary(BinaryOp.SUBTRACT, dot, value, at);
            case STAR_ASSIGN -> new Binary(BinaryOp.MULTIPLY, dot, value, at);
            case SLASH_ASSIGN -> new Binary(BinaryOp.DIVIDE, dot, value, at);
            case PERCENT_ASSIGN -> new Binary(BinaryOp.MODULO, dot, value, at);
            default -> new Alternative(dot, value, at);
        };
        return new Bind(right, List.of(new VariablePattern(UPDATE_VARIABLE, at)), new Call("_modify", List.of(left, update), at), at);
    }

    // The minus tokens from first (included) to end (excluded) applied to node, the last one innermost.
    private Node negate(Node node, int first, int end) {
        for (int i = end - 1; i >= first; i--) {
            node = new Negate(node, pos(tokens.get(i)));
        }
        return node;
    }

    private boolean isBracketSuffix() {
        return current.kind() == TokenKind.LEFT_BRACKET || (current.kind() == TokenKind.DOT && peek().kind() == TokenKind.LEFT_BRACKET);
    }

    private boolean isSuffix() {
        TokenKind kind = current.kind();
        if (kind == TokenKind.QUESTION || kind == TokenKind.FIELD || kind == TokenKind.LEFT_BRACKET || kind == TokenKind.QUESTION_ALT) {
            return true;
        }
        if (kind != TokenKind.DOT) {
            return false;
        }
        TokenKind after = peek().kind();
        return after == TokenKind.LEFT_BRACKET || after == TokenKind.STRING || after == TokenKind.STRING_START;
    }

    // Suffix = '?' | FIELD | '.' String; the bracket suffixes are parseBracketSuffix. '?//' is an
    // error here: it separates patterns only.
    private Node parseSuffix(Node target) {
        Token token = current;
        switch (token.kind()) {
            case QUESTION:
                advance();
                return new Try(target, null, pos(token));
            case FIELD:
                advance();
                return new Index(target, new Literal(JqValue.fromString(token.text()), pos(token)), pos(token));
            case QUESTION_ALT:
                return fail(SyntaxMessages.questionAlt(token), token);
            default:
                advance();
                Node key = parseString(null);
                return key == null ? null : new Index(target, key, pos(token));
        }
    }

    // '.'? '[' ']' | '[' Pipe ']' | '[' Pipe ':' Pipe? ']' | '[' ':' Pipe ']'
    private Node parseBracketSuffix(Node target) {
        if (current.kind() == TokenKind.DOT) {
            advance();
        }
        Token open = current;
        if (!enter(open)) {
            return null;
        }
        advance();
        Node result;
        if (current.kind() == TokenKind.RIGHT_BRACKET) {
            result = new Iterate(target, pos(open));
        } else {
            Node from = null;
            if (current.kind() != TokenKind.COLON) {
                from = parsePipe();
                if (from == null) {
                    return null;
                }
            }

            if (current.kind() == TokenKind.COLON) {
                advance();
                Node to = null;
                if (from == null || current.kind() != TokenKind.RIGHT_BRACKET) {
                    to = parsePipe();
                    if (to == null) {
                        return null;
                    }
                }

                result = new Slice(target, from, to, pos(open));
            } else {
                result = new Index(target, from, pos(open));
            }
        }

        return close(TokenKind.RIGHT_BRACKET, "']'", result);
    }

    // '(' Pipe ')'
    private Node parseParenthesized() {
        if (!enter(current)) {
            return null;
        }
        advance();
        Node inner = parsePipe();
        return inner == null ? null : close(TokenKind.RIGHT_PAREN, "')'", inner);
    }

    // '[' Pipe? ']'
    private Node parseArray() {
        Token open = current;
        if (!enter(open)) {
            return null;
        }
        advance();
        if (current.kind() == TokenKind.RIGHT_BRACKET) {
            return close(TokenKind.RIGHT_BRACKET, "']'", new ArrayExpr(null, pos(open)));
        }
        Node body = parsePipe();
        return body == null ? null : close(TokenKind.RIGHT_BRACKET, "']'", new ArrayExpr(body, pos(open)));
    }

    // '.', '..', '.' String, NUMBER, FORMAT String?, FIELD, VAR, null, true, false, 'break' VAR.
    private Node parseAtom() {
        Token token = current;
        Position at = pos(token);
        switch (token.kind()) {
            case DOT:
                advance();
                if (current.kind() == TokenKind.STRING || current.kind() == TokenKind.STRING_START) {
                    Node key = parseString(null);
                    return key == null ? null : new Index(new Identity(at), key, at);
                }

                return new Identity(at);
            case DOT_DOT:
                advance();
                return new Call("recurse", List.of(), at);
            case NUMBER:
                advance();
                return new Literal(JqValue.fromNumber(token.number()), at);
            case FORMAT:
                // Any name parses: an unknown format is a runtime error (spec §9.9).
                advance();
                return current.kind() == TokenKind.STRING || current.kind() == TokenKind.STRING_START
                    ? parseString(token.text())
                    : new Format(token.text(), at);
            case FIELD:
                advance();
                return new Index(new Identity(at), new Literal(JqValue.fromString(token.text()), at), at);
            case VARIABLE:
                advance();
                return new Variable(token.text(), at);
            case IDENT:
                advance();
                return new Literal(token.text().equals("null") ? JqValue.NULL : JqValue.fromBool(token.text().equals("true")), at);
            case BREAK:
                advance();
                if (current.kind() != TokenKind.VARIABLE) {
                    return fail(SyntaxMessages.expected("a label name ($name)", current), current);
                }
                String label = current.text();
                advance();
                return new Break(label, at);
            default:
                return fail(SyntaxMessages.unexpected(token), token);
        }
    }

    // IDENT ('(' Pipe (';' Pipe)* ')')?
    private Node parseCall() {
        Token name = current;
        advance();
        List<Node> arguments = new ArrayList<>();
        if (current.kind() == TokenKind.LEFT_PAREN) {
            if (!enter(current)) {
                return null;
            }
            do {
                advance();
                Node argument = parsePipe();
                if (argument == null) {
                    return null;
                }
                arguments.add(argument);
            } while (current.kind() == TokenKind.SEMICOLON);

            if (!expect(TokenKind.RIGHT_PAREN, "')'")) {
                return null;
            }
            depth--;
        }

        return new Call(name.text(), List.copyOf(arguments), pos(name));
    }

    // A STRING, or STRING_START Pipe (STRING_MIDDLE Pipe)* STRING_END.
    private Node parseString(String format) {
        Token start = current;
        if (start.kind() == TokenKind.STRING) {
            return parseLiteralString();
        }
        if (!enter(start)) {
            return null;
        }
        List<String> texts = new ArrayList<>();
        List<Node> parts = new ArrayList<>();
        do {
            texts.add(current.text());
            advance();
            Node part = parsePipe();
            if (part == null) {
                return null;
            }
            parts.add(part);
            if (current.kind() != TokenKind.STRING_MIDDLE && current.kind() != TokenKind.STRING_END) {
                return fail(SyntaxMessages.expected("')'", current), current);
            }
        } while (current.kind() == TokenKind.STRING_MIDDLE);

        texts.add(current.text());
        advance();
        depth--;
        return makeTemplate(format, texts, parts, start);
    }

    private Node parseLiteralString() {
        Token token = current;
        advance();
        return new Literal(JqValue.fromString(token.text()), pos(token));
    }

    private static Node makeTemplate(String format, List<String> texts, List<Node> parts, Token start) {
        return new Template(format, List.copyOf(texts), List.copyOf(parts), pos(start));
    }

    // '{' (Entry (',' Entry)* ','?)? '}'
    private Node parseObject() {
        Token open = current;
        if (!enter(open)) {
            return null;
        }
        advance();
        List<ObjectEntry> entries = new ArrayList<>();
        while (current.kind() != TokenKind.RIGHT_BRACE) {
            ObjectEntry entry = parseObjectKey();
            if (entry != null && entry.value() == null && current.kind() == TokenKind.COLON) {
                advance();
                Node value = parseObjectValue();
                entry = value == null ? null : new ObjectEntry(entry.key(), value, entry.position());
            }

            if (entry == null) {
                return null;
            }
            entries.add(entry);
            if (current.kind() == TokenKind.COMMA) {
                advance();
            } else if (current.kind() != TokenKind.RIGHT_BRACE) {
                return fail(SyntaxMessages.expected("',' or '}'", current), current);
            }
        }

        advance();
        depth--;
        return new ObjectExpr(List.copyOf(entries), pos(open));
    }

    // Entry = (IDENT | Keyword | VAR | String | '(' Pipe ')') ':' ObjVal  |  IDENT | Keyword | VAR | String
    // The key of an entry, with a null value when a ':' and the value follow. A shorthand is
    // complete: {$x} has its value, {name} and {"s"} have none and no ':' after them.
    private ObjectEntry parseObjectKey() {
        Token token = current;
        Position at = pos(token);
        if (token.kind() == TokenKind.VARIABLE) {
            advance();
            return current.kind() == TokenKind.COLON
                ? new ObjectEntry(new Variable(token.text(), at), null, at)
                : new ObjectEntry(new Literal(JqValue.fromString(token.text()), at), new Variable(token.text(), at), at);
        }

        Node key = token.kind() == TokenKind.LEFT_PAREN ? parseParenthesizedKey() : parseNameKey();
        if (key == null) {
            return null;
        }
        if (token.kind() == TokenKind.LEFT_PAREN && current.kind() != TokenKind.COLON) {
            return failNull(SyntaxMessages.expected("':'", current), current);
        }
        return new ObjectEntry(key, null, at);
    }

    // A key written as a name or a string (objects and object patterns): IDENT | Keyword | FORMAT? String.
    private Node parseNameKey() {
        Token token = current;
        TokenKind kind = token.kind();
        if (kind == TokenKind.IDENT || Token.isKeyword(kind)) {
            advance();
            return new Literal(JqValue.fromString(token.text()), pos(token));
        }

        if (kind == TokenKind.STRING || kind == TokenKind.STRING_START) {
            return parseString(null);
        }

        if (kind == TokenKind.FORMAT && (peek().kind() == TokenKind.STRING || peek().kind() == TokenKind.STRING_START)) {
            advance();
            return parseString(token.text());
        }

        boolean closing = switch (kind) {
            case COMMA, RIGHT_BRACE, RIGHT_BRACKET, RIGHT_PAREN, SEMICOLON, COLON, PIPE, EOF, ERROR -> true;
            default -> false;
        };
        return closing ? fail(SyntaxMessages.unexpected(token), token) : fail(SyntaxMessages.MAY_NEED_PARENTHESES, token);
    }

    // '(' Pipe ')' as a key; a number, boolean or null literal there is a static error (spec §3.2).
    private Node parseParenthesizedKey() {
        Token open = current;
        if (!enter(open)) {
            return null;
        }
        advance();
        Node key = parsePipe();
        if (key == null || !expect(TokenKind.RIGHT_PAREN, "')'")) {
            return null;
        }
        depth--;
        if (key instanceof Literal literal && literal.value().kind() != JqKind.STRING) {
            return fail(SyntaxMessages.invalidObjectKey(literal.value()), literal.position());
        }
        return key;
    }

    // 'reduce' Alt 'as' Patterns '(' Pipe ';' Pipe ')'   |   'foreach' Alt 'as' Patterns '(' Pipe ';' Pipe (';' Pipe)? ')'
    private Node parseReduceOrForeach() {
        if (!enter(current)) {
            return null;
        }
        Node head = parseReduceOrForeachHead();
        if (head == null) {
            return null;
        }
        int most = head instanceof Reduce ? 2 : 3;
        List<Node> bodies = new ArrayList<>();
        do {
            if (!bodies.isEmpty()) {
                advance();
            }
            Node body = parsePipe();
            if (body == null) {
                return null;
            }
            bodies.add(body);
        } while (current.kind() == TokenKind.SEMICOLON && bodies.size() < most);

        return bodies.size() < 2
            ? fail(SyntaxMessages.expected("';'", current), current)
            : close(TokenKind.RIGHT_PAREN, "')'", completeReduceOrForeach(head, bodies));
    }

    // ('reduce' | 'foreach') Alt 'as' Patterns '(', as a node whose bodies are HOLE.
    private Node parseReduceOrForeachHead() {
        Token keyword = current;
        advance();
        Node source = parseExpression(false);
        if (source == null || !expect(TokenKind.AS, "'as'")) {
            return null;
        }
        List<Pattern> patterns = parsePatterns();
        if (patterns == null || !expect(TokenKind.LEFT_PAREN, "'('")) {
            return null;
        }
        return keyword.kind() == TokenKind.REDUCE
            ? new Reduce(source, patterns, HOLE, HOLE, pos(keyword))
            : new Foreach(source, patterns, HOLE, HOLE, HOLE, pos(keyword));
    }

    private static Node completeReduceOrForeach(Node head, List<Node> bodies) {
        return switch (head) {
            case Reduce reduce -> new Reduce(reduce.source(), reduce.patterns(), bodies.get(0), bodies.get(1), reduce.position());
            case Foreach each -> new Foreach(each.source(), each.patterns(), bodies.get(0), bodies.get(1),
                bodies.size() > 2 ? bodies.get(2) : new Identity(each.position()), each.position());
            default -> head;
        };
    }

    // 'if' Pipe 'then' Pipe ('elif' Pipe 'then' Pipe)* ('else' Pipe)? 'end'. Each branch is a partial
    // If whose else is HOLE, completed from the last to the first.
    private Node parseIf() {
        if (!enter(current)) {
            return null;
        }
        List<Node> branches = new ArrayList<>();
        do {
            Token keyword = current;
            advance();
            Node condition = parsePipe();
            if (condition == null || !expect(TokenKind.THEN, "'then'")) {
                return null;
            }
            Node then = parsePipe();
            if (then == null) {
                return null;
            }
            branches.add(partialIf(condition, then, keyword));
        } while (current.kind() == TokenKind.ELIF);

        Node otherwise = null;
        if (current.kind() == TokenKind.ELSE) {
            advance();
            otherwise = parsePipe();
            if (otherwise == null) {
                return null;
            }
        }

        return close(TokenKind.END, "'end'", complete(branches, otherwise != null ? otherwise : new Identity(pos(current))));
    }

    private static Node partialIf(Node condition, Node then, Token keyword) {
        return new If(condition, then, HOLE, pos(keyword));
    }

    // 'try' Unary ('catch' Unary)?
    private Node parseTry() {
        Token keyword = current;
        if (!enter(keyword)) {
            return null;
        }
        advance();
        Node body = parseExpression(true);
        if (body == null) {
            return null;
        }
        Node handler = null;
        if (current.kind() == TokenKind.CATCH) {
            advance();
            handler = parseExpression(true);
            if (handler == null) {
                return null;
            }
        }

        depth--;
        return new Try(body, handler, pos(keyword));
    }

    // Patterns = Pattern ('?//' Pattern)*
    private List<Pattern> parsePatterns() {
        List<Pattern> patterns = new ArrayList<>();
        while (true) {
            Pattern pattern = parsePattern();
            if (pattern == null) {
                return null;
            }
            patterns.add(pattern);
            if (current.kind() != TokenKind.QUESTION_ALT) {
                return List.copyOf(patterns);
            }
            advance();
        }
    }

    // Pattern = VAR | '[' Pattern (',' Pattern)* ']' | '{' PEntry (',' PEntry)* '}'
    private Pattern parsePattern() {
        Token token = current;
        Position at = pos(token);
        if (token.kind() == TokenKind.VARIABLE && !isLocation(token)) {
            advance();
            return new VariablePattern(token.text(), at);
        }

        if (token.kind() == TokenKind.LEFT_BRACKET) {
            if (!enter(token)) {
                return null;
            }
            List<Pattern> elements = new ArrayList<>();
            do {
                advance();
                Pattern element = parsePattern();
                if (element == null) {
                    return null;
                }
                elements.add(element);
            } while (current.kind() == TokenKind.COMMA);

            if (!expect(TokenKind.RIGHT_BRACKET, "']'")) {
                return null;
            }
            depth--;
            return new ArrayPattern(List.copyOf(elements), at);
        }

        if (token.kind() == TokenKind.LEFT_BRACE) {
            if (!enter(token)) {
                return null;
            }
            List<PatternEntry> entries = new ArrayList<>();
            do {
                advance();
                PatternEntry entry = parsePatternEntry();
                if (entry == null) {
                    return null;
                }
                entries.add(entry);
            } while (current.kind() == TokenKind.COMMA);

            if (!expect(TokenKind.RIGHT_BRACE, "'}'")) {
                return null;
            }
            depth--;
            return new ObjectPattern(List.copyOf(entries), at);
        }

        return failNull(SyntaxMessages.expected("a pattern ($name, [...] or {...})", token), token);
    }

    // PEntry = VAR (':' Pattern)? | (IDENT | Keyword | String | '(' Pipe ')') ':' Pattern
    private PatternEntry parsePatternEntry() {
        Token token = current;
        Position at = pos(token);
        Node key;
        String variable = null;
        if (isLocation(token)) {
            return failNull(SyntaxMessages.unexpected(token), token);
        }
        if (token.kind() == TokenKind.VARIABLE) {
            advance();
            key = new Literal(JqValue.fromString(token.text()), at);
            variable = token.text();
            if (current.kind() != TokenKind.COLON) {
                return new PatternEntry(key, variable, null, at);
            }
        } else {
            key = token.kind() == TokenKind.LEFT_PAREN ? parseParenthesizedKey() : parseNameKey();
            if (key == null) {
                return null;
            }
        }

        if (!expect(TokenKind.COLON, "':'")) {
            return null;
        }
        Pattern value = parsePattern();
        return value == null ? null : new PatternEntry(key, variable, value, at);
    }

    // Opens one level of nesting (spec §11).
    private boolean enter(Token token) {
        if (++depth <= MAX_NESTING) {
            return true;
        }
        fail(SyntaxMessages.TOO_DEEPLY_NESTED, token);
        return false;
    }

    // Expects the token that closes a nesting level, and leaves the level.
    private Node close(TokenKind kind, String what, Node node) {
        if (!expect(kind, what)) {
            return null;
        }
        depth--;
        return node;
    }

    private boolean expect(TokenKind kind, String what) {
        if (current.kind() != kind) {
            fail(SyntaxMessages.expected(what, current), current);
            return false;
        }

        advance();
        return true;
    }

    private Node fail(String message, Token token) {
        return failNull(message, token);
    }

    private Node fail(String message, Position position) {
        if (error == null) {
            error = new SyntaxError(message, position.line(), position.column());
        }
        return null;
    }

    // Records the error and returns null. At a lexical error token, the lexer's message wins.
    private <T> T failNull(String message, Token token) {
        if (error == null) {
            error = token.kind() == TokenKind.ERROR
                ? new SyntaxError(token.text(), token.line(), token.column())
                : new SyntaxError(message, token.line(), token.column());
        }
        return null;
    }
}