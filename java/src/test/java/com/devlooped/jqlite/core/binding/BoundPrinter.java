package com.devlooped.jqlite.core.binding;

import com.devlooped.jqlite.binding.Bound;
import com.devlooped.jqlite.binding.Bound.Binder;
import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.binding.Bound.Pattern;
import com.devlooped.jqlite.binding.Prelude;
import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.syntax.Ast.BinaryOp;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.stream.Collectors;
import org.junit.jupiter.api.Assertions;

/**
 * Prints a bound program as an S-expression stating what every reference resolved to, in the shapes
 * of AstPrinter, with: {@code $h.s} a variable (slot s of the frame h frames out), {@code $$name} a
 * host variable, {@code $ENV}; {@code name/n#i^h} a call of the program's function i (0 = its first
 * own function) defined h frames out, {@code prelude:name/n^h} a call of a prelude builtin,
 * {@code native:name/n} a native, {@code param^h.s} a closure parameter; {@code (break ^h)}; in
 * patterns {@code $s} stores to slot s and {@code $_} discards. The program's own functions come first
 * as {@code (def name/n#i body)}. Recursive: for the trees of ordinary tests only.
 */
public final class BoundPrinter {
    private BoundPrinter() {
    }

    private static int preludeCount() {
        return Prelude.instance().functions().length;
    }

    /** Parses and binds a program and prints its own functions and its body, separated by spaces. */
    public static String bind(String source) {
        Resolver.Compiled compiled = Resolver.tryCompile(source);
        if (compiled.program() == null) {
            Assertions.fail("'" + source + "' does not bind: " + compiled.error());
        }
        return print(compiled.program());
    }

    /** Binds a program that must fail and gives "message @line:column". */
    public static String error(String source) {
        Resolver.Compiled compiled = Resolver.tryCompile(source);
        if (compiled.program() != null) {
            Assertions.fail("'" + source + "' binds as " + print(compiled.program()));
        }
        return compiled.error().message() + " @" + compiled.error().line() + ":" + compiled.error().column();
    }

    public static String print(BoundProgram program) {
        List<String> parts = new ArrayList<>();
        for (int i = preludeCount(); i < program.functions().length; i++) {
            parts.add("(def " + name(program, i) + " " + print(program, program.functions()[i].body()) + ")");
        }
        parts.add(print(program, program.body()));
        return String.join(" ", parts);
    }

    private static String name(BoundProgram program, int index) {
        Bound.Function function = program.functions()[index];
        return index < preludeCount() ? "prelude:" + function : function + "#" + (index - preludeCount());
    }

    private static String all(BoundProgram program, Node[] children) {
        return Arrays.stream(children).map(child -> " " + print(program, child)).collect(Collectors.joining());
    }

    private static String print(BoundProgram program, Node node) {
        if (node == null) {
            return "_";
        }
        return switch (node) {
            case Bound.Identity identity -> ".";
            case Bound.Literal literal -> ValueText.toJson(literal.value);
            case Bound.Template template -> printTemplate(program, template);
            case Bound.Format format -> "@" + format.name;
            case Bound.Index index -> "(index " + print(program, index.target) + " " + print(program, index.key) + ")";
            case Bound.Slice slice -> "(slice " + print(program, slice.target) + " " + print(program, slice.from) + " " + print(program, slice.to) + ")";
            case Bound.Iterate iterate -> "(iter " + print(program, iterate.target) + ")";
            case Bound.Variable variable -> "$" + variable.hops + "." + variable.slot;
            case Bound.HostVariable host -> "$$" + host.name;
            case Bound.EnvVariable env -> "$ENV";
            case Bound.CallFunction call -> call.arguments.length == 0
                ? name(program, call.function) + "^" + call.hops
                : "(" + name(program, call.function) + "^" + call.hops + all(program, call.arguments) + ")";
            case Bound.CallParameter parameter -> "param^" + parameter.hops + "." + parameter.slot;
            case Bound.CallNative call -> call.arguments.length == 0 ? "native:" + call.nativeFunction
                : "(native:" + call.nativeFunction + all(program, call.arguments) + ")";
            case Bound.Pipe pipe -> "(| " + print(program, pipe.left) + " " + print(program, pipe.right) + ")";
            case Bound.Comma comma -> "(," + all(program, comma.items) + ")";
            case Bound.Binary binary -> "(" + symbol(binary.op) + " " + print(program, binary.left) + " " + print(program, binary.right) + ")";
            case Bound.And and -> "(and " + print(program, and.left) + " " + print(program, and.right) + ")";
            case Bound.Or or -> "(or " + print(program, or.left) + " " + print(program, or.right) + ")";
            case Bound.Alternative alternative -> "(// " + print(program, alternative.left) + " " + print(program, alternative.right) + ")";
            case Bound.Negate negate -> "(neg " + print(program, negate.operand) + ")";
            case Bound.ArrayExpr array -> array.body == null ? "(array)" : "(array " + print(program, array.body) + ")";
            case Bound.ObjectExpr obj -> obj.entries.length == 0 ? "(object)"
                : "(object " + Arrays.stream(obj.entries).map(e -> "(" + print(program, e.key) + " " + print(program, e.value) + ")").collect(Collectors.joining(" ")) + ")";
            case Bound.If cond -> "(if " + print(program, cond.condition) + " " + print(program, cond.then) + " " + print(program, cond.otherwise) + ")";
            case Bound.Try attempt -> attempt.handler == null ? "(try " + print(program, attempt.body) + ")"
                : "(try " + print(program, attempt.body) + " " + print(program, attempt.handler) + ")";
            case Bound.Reduce reduce -> "(reduce " + print(program, reduce.source) + " " + print(program, reduce.binder) + " " + print(program, reduce.init) + " " + print(program, reduce.update) + ")";
            case Bound.Foreach each -> "(foreach " + print(program, each.source) + " " + print(program, each.binder) + " " + print(program, each.init) + " " + print(program, each.update) + " " + print(program, each.extract) + ")";
            case Bound.Bind bind -> "(as " + print(program, bind.source) + " " + print(program, bind.binder) + " " + print(program, bind.body) + ")";
            case Bound.Label label -> "(label " + print(program, label.body) + ")";
            case Bound.Break brk -> "(break ^" + brk.hops + ")";
        };
    }

    private static String printTemplate(BoundProgram program, Bound.Template template) {
        StringBuilder builder = new StringBuilder("(str");
        if (template.format != null) {
            builder.append(" @").append(template.format);
        }
        for (int i = 0; i < template.parts.length; i++) {
            builder.append(' ').append(ValueText.toJson(JqValue.fromString(template.texts[i]))).append(' ').append(print(program, template.parts[i]));
        }
        return builder.append(' ').append(ValueText.toJson(JqValue.fromString(template.texts[template.texts.length - 1]))).append(')').toString();
    }

    private static String print(BoundProgram program, Binder binder) {
        return binder.patterns.length == 1 ? print(program, binder.patterns[0])
            : "(?// " + Arrays.stream(binder.patterns).map(p -> print(program, p)).collect(Collectors.joining(" ")) + ")";
    }

    private static String print(BoundProgram program, Pattern pattern) {
        if (pattern == null) {
            return "_";
        }
        return switch (pattern) {
            case Bound.VariablePattern variable -> slot(variable.slot);
            case Bound.ArrayPattern array -> "[" + Arrays.stream(array.elements).map(p -> print(program, p)).collect(Collectors.joining(" ")) + "]";
            case Bound.ObjectPattern obj -> "{" + Arrays.stream(obj.entries)
                .map(e -> "(" + print(program, e.key) + " " + (e.slot < 0 ? "_" : slot(e.slot)) + " " + print(program, e.value) + ")")
                .collect(Collectors.joining(" ")) + "}";
        };
    }

    private static String slot(int slot) {
        return slot < 0 ? "$_" : "$" + slot;
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
