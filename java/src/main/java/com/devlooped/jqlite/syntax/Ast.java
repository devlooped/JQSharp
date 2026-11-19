package com.devlooped.jqlite.syntax;

import com.devlooped.jqlite.values.JqValue;
import java.util.List;

/**
 * The abstract syntax tree: immutable plain data, no behaviour, no back-pointers. Every node and
 * pattern carries the position of the token that introduces it (its keyword, operator, bracket or
 * name). Names stay as written (without '$', '.' or '@'; a qualified name keeps its "::"): the
 * resolver binds them. Forms the spec defines by rewriting (§3.3) are already rewritten by the
 * parser (see the list at the top of Parser.java), so the node set below is all an interpreter
 * sees. Java has one public type per file, so the types of Ast.cs are nested here.
 */
public final class Ast {
    private Ast() {
    }

    /** A 1-based source position; columns count code points. */
    public record Position(int line, int column) {
    }

    public enum NodeKind {
        IDENTITY,
        LITERAL,
        TEMPLATE,
        FORMAT,
        INDEX,
        SLICE,
        ITERATE,
        VARIABLE,
        CALL,
        PIPE,
        COMMA,
        BINARY,
        AND,
        OR,
        ALTERNATIVE,
        NEGATE,
        ARRAY_EXPR,
        OBJECT_EXPR,
        IF,
        TRY,
        REDUCE,
        FOREACH,
        BIND,
        FUNC_DEF,
        LABEL,
        BREAK,
    }

    public sealed interface Node permits Identity, Literal, Template, Format, Index, Slice, Iterate, Variable, Call, Pipe, Comma,
        Binary, And, Or, Alternative, Negate, ArrayExpr, ObjectExpr, If, Try, Reduce, Foreach, Bind, FuncDef, Label, Break {
        NodeKind kind();

        Position position();
    }

    /** {@code .} */
    public record Identity(Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.IDENTITY;
        }
    }

    /** A constant: a number, a string without interpolation, {@code null}, {@code true}, {@code false}. */
    public record Literal(JqValue value, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.LITERAL;
        }
    }

    /**
     * A string with interpolations: {@code texts[0] parts[0] texts[1] … parts[n-1] texts[n]}, so
     * {@code texts.size() == parts.size() + 1}. Each part's outputs are converted with
     * {@code format} (null: {@code tostring}); the texts are taken as they are.
     */
    public record Template(String format, List<String> texts, List<Node> parts, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.TEMPLATE;
        }
    }

    /** {@code @name} alone: the format applied to the input. */
    public record Format(String name, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.FORMAT;
        }
    }

    /** {@code target[key]}; also {@code .name} and {@code ."s"}. {@code key} runs on the input of {@code target}. */
    public record Index(Node target, Node key, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.INDEX;
        }
    }

    /** {@code target[from:to]}; a missing bound is null. */
    public record Slice(Node target, Node from, Node to, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.SLICE;
        }
    }

    /** {@code target[]} */
    public record Iterate(Node target, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.ITERATE;
        }
    }

    /** {@code $name} or {@code $alias::name}. */
    public record Variable(String name, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.VARIABLE;
        }
    }

    /** {@code name} or {@code name(a; b)}: a function, closure parameter or builtin of arity {@code arguments.size()}. */
    public record Call(String name, List<Node> arguments, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.CALL;
        }
    }

    /** {@code left | right} */
    public record Pipe(Node left, Node right, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.PIPE;
        }
    }

    /** {@code left, right} */
    public record Comma(Node left, Node right, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.COMMA;
        }
    }

    public enum BinaryOp {
        ADD,
        SUBTRACT,
        MULTIPLY,
        DIVIDE,
        MODULO,
        EQUAL,
        NOT_EQUAL,
        LESS,
        LESS_EQUAL,
        GREATER,
        GREATER_EQUAL,
    }

    /** An arithmetic operator or a comparison: one output per pair of outputs of the operands. */
    public record Binary(BinaryOp op, Node left, Node right, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.BINARY;
        }
    }

    /** {@code left and right} */
    public record And(Node left, Node right, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.AND;
        }
    }

    /** {@code left or right} */
    public record Or(Node left, Node right, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.OR;
        }
    }

    /** {@code left // right} */
    public record Alternative(Node left, Node right, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.ALTERNATIVE;
        }
    }

    /** Unary {@code -operand}. */
    public record Negate(Node operand, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.NEGATE;
        }
    }

    /** {@code [body]}; {@code []} has no body. */
    public record ArrayExpr(Node body, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.ARRAY_EXPR;
        }
    }

    /** <code>{…}</code> */
    public record ObjectExpr(List<ObjectEntry> entries, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.OBJECT_EXPR;
        }
    }

    /**
     * One entry of an object construction. {@code key} is the key expression (a literal for
     * {@code name:} and {@code "s":}, a template for interpolated keys, a variable for {@code $x:},
     * any expression for {@code (e):}). A null {@code value} is the shorthand (<code>{"s"}</code>,
     * <code>{name}</code>, <code>{@f "s"}</code>): the value is the input indexed by each key,
     * evaluated once per key. (<code>{$x}</code> is rewritten to the key "x" and the value $x.)
     */
    public record ObjectEntry(Node key, Node value, Position position) {
    }

    /** {@code if condition then then else otherwise end}; {@code elif} nests, a missing else is {@link Identity}. */
    public record If(Node condition, Node then, Node otherwise, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.IF;
        }
    }

    /** {@code try body catch handler}; also {@code body?}. A null handler discards the error. */
    public record Try(Node body, Node handler, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.TRY;
        }
    }

    /** {@code reduce source as patterns (init; update)} */
    public record Reduce(Node source, List<Pattern> patterns, Node init, Node update, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.REDUCE;
        }
    }

    /** {@code foreach source as patterns (init; update; extract)}; a missing extract is {@link Identity}. */
    public record Foreach(Node source, List<Pattern> patterns, Node init, Node update, Node extract, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.FOREACH;
        }
    }

    /** {@code source as patterns | body}. Several patterns are the {@code ?//} alternatives. */
    public record Bind(Node source, List<Pattern> patterns, Node body, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.BIND;
        }
    }

    /** {@code def …; rest}: the definition is visible in its own body and in {@code rest}. */
    public record FuncDef(Definition definition, Node rest, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.FUNC_DEF;
        }
    }

    /** {@code label $name | body} */
    public record Label(String name, Node body, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.LABEL;
        }
    }

    /** {@code break $name} */
    public record Break(String name, Position position) implements Node {
        @Override
        public NodeKind kind() {
            return NodeKind.BREAK;
        }
    }

    /**
     * {@code def name(params): body;}. Every parameter is a filter parameter; a {@code $name}
     * parameter ({@link Param#isVariable}) also binds {@code $name} to each output of its own
     * argument, first parameter outermost (§3.3). The resolver does that binding by position, since a
     * name may repeat: in {@code def f($a; a)} the filter {@code a} is the second parameter and
     * {@code $a} the first.
     */
    public record Definition(String name, List<Param> params, Node body, Position position) {
    }

    public record Param(String name, boolean isVariable, Position position) {
    }

    public enum PatternKind {
        VARIABLE,
        ARRAY,
        OBJECT,
    }

    public sealed interface Pattern permits VariablePattern, ArrayPattern, ObjectPattern {
        PatternKind kind();

        Position position();
    }

    /** {@code $name} */
    public record VariablePattern(String name, Position position) implements Pattern {
        @Override
        public PatternKind kind() {
            return PatternKind.VARIABLE;
        }
    }

    /** {@code [P0, P1, …]}: element i is matched against {@code .[i]}. */
    public record ArrayPattern(List<Pattern> elements, Position position) implements Pattern {
        @Override
        public PatternKind kind() {
            return PatternKind.ARRAY;
        }
    }

    /** <code>{…}</code> */
    public record ObjectPattern(List<PatternEntry> entries, Position position) implements Pattern {
        @Override
        public PatternKind kind() {
            return PatternKind.OBJECT;
        }
    }

    /**
     * One entry of an object pattern: for each output of {@code key}, the value {@code .[key]} is
     * bound to {@code variable} (when not null) and matched against {@code value} (when not null).
     * {@code $name} is the key "name" with the variable {@code name}; {@code $name: P} adds the
     * pattern; {@code name: P}, {@code "s": P} and {@code (e): P} have no variable.
     */
    public record PatternEntry(Node key, String variable, Pattern value, Position position) {
    }

    /** {@code import "path" as alias metadata;}, {@code import "path" as $alias …;} (data) or {@code include "path" …;} (no alias). */
    public record Import(String path, String alias, boolean isData, JqValue metadata, Position position) {
    }

    /**
     * A parsed program or module file. {@code definitions} are the top-level definitions in order
     * (what a module exports); {@code body} is the main expression, {@link Identity} for a program of
     * definitions only, and null for a module file.
     */
    public record Program(JqValue metadata, List<Import> imports, List<Definition> definitions, Node body) {
    }
}
