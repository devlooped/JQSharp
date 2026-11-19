package com.devlooped.jqlite.core.syntax;

import com.devlooped.jqlite.syntax.Ast;
import com.devlooped.jqlite.syntax.Ast.BinaryOp;
import com.devlooped.jqlite.syntax.Ast.Node;
import com.devlooped.jqlite.syntax.Ast.Pattern;
import com.devlooped.jqlite.syntax.Parser;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.List;
import java.util.stream.Collectors;
import org.junit.jupiter.api.Assertions;

/**
 * Prints an AST as an S-expression so tests can state structure: {@code .}, literals as JSON,
 * {@code $x}, a call without arguments as its name, {@code (f a b)}, {@code (| a b)}, {@code (, a b)},
 * {@code (+ a b)}, {@code (neg a)}, {@code (index t k)}, {@code (slice t from to)}, {@code (iter t)},
 * {@code (str @fmt "a" e "b")}, {@code (object (k v) (k _))}, {@code (as source pattern body)}, …; a
 * missing child is {@code _}. Recursive: for the trees of ordinary tests only.
 */
public final class AstPrinter {
    private AstPrinter() {
    }

    /** Parses a program and prints its top-level definitions and body, separated by spaces. */
    public static String parse(String source) {
        Parser.Parsed parsed = Parser.tryParse(source);
        if (parsed.program() == null) {
            Assertions.fail("'" + source + "' does not parse: " + parsed.error().message() + " at " + parsed.error().line() + ":" + parsed.error().column());
        }
        return print(parsed.program());
    }

    /** Parses a program that must fail and gives "message @line:column". */
    public static String error(String source) {
        Parser.Parsed parsed = Parser.tryParse(source);
        if (parsed.program() != null) {
            Assertions.fail("'" + source + "' parses as " + print(parsed.program()));
        }
        return parsed.error().message() + " @" + parsed.error().line() + ":" + parsed.error().column();
    }

    public static String print(Ast.Program program) {
        List<String> parts = new ArrayList<>();
        if (program.metadata() != null) {
            parts.add("(module " + ValueText.toJson(program.metadata()) + ")");
        }
        for (Ast.Import directive : program.imports()) {
            String alias = directive.alias() == null ? "" : directive.isData() ? " $" + directive.alias() : " " + directive.alias();
            String meta = directive.metadata() != null ? " " + ValueText.toJson(directive.metadata()) : "";
            parts.add("(" + (directive.alias() == null ? "include" : "import") + " \"" + directive.path() + "\"" + alias + meta + ")");
        }

        for (Ast.Definition definition : program.definitions()) {
            parts.add("(def " + signature(definition) + " " + print(definition.body()) + ")");
        }
        if (program.body() != null) {
            parts.add(print(program.body()));
        }
        return String.join(" ", parts);
    }

    public static String print(Node node) {
        if (node == null) {
            return "_";
        }
        return switch (node) {
            case Ast.Identity identity -> ".";
            case Ast.Literal literal -> ValueText.toJson(literal.value());
            case Ast.Template template -> printTemplate(template);
            case Ast.Format format -> "@" + format.name();
            case Ast.Index index -> "(index " + print(index.target()) + " " + print(index.key()) + ")";
            case Ast.Slice slice -> "(slice " + print(slice.target()) + " " + print(slice.from()) + " " + print(slice.to()) + ")";
            case Ast.Iterate iterate -> "(iter " + print(iterate.target()) + ")";
            case Ast.Variable variable -> "$" + variable.name();
            case Ast.Call call -> call.arguments().isEmpty() ? call.name()
                : "(" + call.name() + " " + call.arguments().stream().map(AstPrinter::print).collect(Collectors.joining(" ")) + ")";
            case Ast.Pipe pipe -> "(| " + print(pipe.left()) + " " + print(pipe.right()) + ")";
            case Ast.Comma comma -> "(, " + print(comma.left()) + " " + print(comma.right()) + ")";
            case Ast.Binary binary -> "(" + symbol(binary.op()) + " " + print(binary.left()) + " " + print(binary.right()) + ")";
            case Ast.And and -> "(and " + print(and.left()) + " " + print(and.right()) + ")";
            case Ast.Or or -> "(or " + print(or.left()) + " " + print(or.right()) + ")";
            case Ast.Alternative alternative -> "(// " + print(alternative.left()) + " " + print(alternative.right()) + ")";
            case Ast.Negate negate -> "(neg " + print(negate.operand()) + ")";
            case Ast.ArrayExpr array -> array.body() == null ? "(array)" : "(array " + print(array.body()) + ")";
            case Ast.ObjectExpr obj -> obj.entries().isEmpty() ? "(object)"
                : "(object " + obj.entries().stream().map(e -> "(" + print(e.key()) + " " + print(e.value()) + ")").collect(Collectors.joining(" ")) + ")";
            case Ast.If cond -> "(if " + print(cond.condition()) + " " + print(cond.then()) + " " + print(cond.otherwise()) + ")";
            case Ast.Try attempt -> attempt.handler() == null ? "(try " + print(attempt.body()) + ")"
                : "(try " + print(attempt.body()) + " " + print(attempt.handler()) + ")";
            case Ast.Reduce reduce -> "(reduce " + print(reduce.source()) + " " + print(reduce.patterns()) + " " + print(reduce.init()) + " " + print(reduce.update()) + ")";
            case Ast.Foreach each -> "(foreach " + print(each.source()) + " " + print(each.patterns()) + " " + print(each.init()) + " " + print(each.update()) + " " + print(each.extract()) + ")";
            case Ast.Bind bind -> "(as " + print(bind.source()) + " " + print(bind.patterns()) + " " + print(bind.body()) + ")";
            case Ast.FuncDef def -> "(def " + signature(def.definition()) + " " + print(def.definition().body()) + " " + print(def.rest()) + ")";
            case Ast.Label label -> "(label $" + label.name() + " " + print(label.body()) + ")";
            case Ast.Break brk -> "(break $" + brk.name() + ")";
        };
    }

    private static String signature(Ast.Definition definition) {
        return definition.params().isEmpty()
            ? definition.name()
            : definition.name() + "(" + definition.params().stream().map(p -> (p.isVariable() ? "$" : "") + p.name()).collect(Collectors.joining(";")) + ")";
    }

    private static String printTemplate(Ast.Template template) {
        StringBuilder builder = new StringBuilder("(str");
        if (template.format() != null) {
            builder.append(" @").append(template.format());
        }
        for (int i = 0; i < template.parts().size(); i++) {
            builder.append(' ').append(ValueText.toJson(JqValue.fromString(template.texts().get(i)))).append(' ').append(print(template.parts().get(i)));
        }
        return builder.append(' ').append(ValueText.toJson(JqValue.fromString(template.texts().get(template.texts().size() - 1)))).append(')').toString();
    }

    private static String print(List<Pattern> patterns) {
        return patterns.size() == 1 ? print(patterns.get(0))
            : "(?// " + patterns.stream().map(AstPrinter::print).collect(Collectors.joining(" ")) + ")";
    }

    private static String print(Pattern pattern) {
        if (pattern == null) {
            return "_";
        }
        return switch (pattern) {
            case Ast.VariablePattern variable -> "$" + variable.name();
            case Ast.ArrayPattern array -> "[" + array.elements().stream().map(AstPrinter::print).collect(Collectors.joining(" ")) + "]";
            case Ast.ObjectPattern obj -> "{" + obj.entries().stream()
                .map(e -> "(" + print(e.key()) + " " + (e.variable() == null ? "_" : "$" + e.variable()) + " " + print(e.value()) + ")")
                .collect(Collectors.joining(" ")) + "}";
        };
    }

    private static String symbol(BinaryOp op) {
        return switch (op) {
            case ADD -> "+";
            case SUBTRACT -> "-";
            case MULTIPLY -> "*";
            case DIVIDE -> "/";
            case MODULO -> "%";
            case EQUAL -> "==";
            case NOT_EQUAL -> "!=";
            case LESS -> "<";
            case LESS_EQUAL -> "<=";
            case GREATER -> ">";
            case GREATER_EQUAL -> ">=";
        };
    }
}
