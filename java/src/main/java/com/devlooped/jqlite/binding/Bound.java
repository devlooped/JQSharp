package com.devlooped.jqlite.binding;

import com.devlooped.jqlite.binding.Modules.ModuleTable;
import com.devlooped.jqlite.builtins.Native;
import com.devlooped.jqlite.syntax.Ast;
import com.devlooped.jqlite.values.JqValue;

/**
 * The bound program: what the interpreter evaluates. A tree of its own node types rather than the
 * AST plus a side table, because every port can build it bottom-up as plain immutable data: no node
 * is mutated after construction, no map is keyed by node identity, and nothing is cyclic.
 *
 * <p>Names are gone. The environment at run time is a chain of frames, innermost first, and every
 * reference counts frames outward from where it is written:
 * <ul>
 * <li>a frame is pushed by {@code as} and by the binder of {@code reduce}/{@code foreach} (one value
 * slot per variable of all its patterns), by {@code label} (the label instance), and by a call of a
 * function with parameters (one closure slot per parameter);</li>
 * <li>a variable is (hops, slot), a closure parameter (hops, slot), a {@code break} hops;</li>
 * <li>a function call names its definition by index in BoundProgram.functions (recursion is an
 * index, not a cycle), and hops is the number of frames between the call and the definition: the
 * callee runs in the caller's environment with hops frames removed, plus its parameters.</li>
 * </ul>
 * Host variables are the one late-bound name (spec §7.5): each has an index in
 * BoundProgram.hostVariables, so the host's values can be looked up once per evaluation.
 *
 * <p>Every node also says whether it yields at most one output ({@code atMostOne}), computed from
 * its children when it is built: the interpreter evaluates such nodes directly to a value, without
 * callbacks (Interpreter.evalOne); these are the single expressions of spec §7.7. A call is single
 * when its function is; the resolver knows that of a function still being defined by assuming it
 * (Resolver.call). Nodes are final classes with public final fields (rather than
 * records) so that this derived flag is a field, and the interpreter switches on {@code kind}.
 */
public final class Bound {
    private Bound() {
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
        HOST_VARIABLE,
        ENV_VARIABLE,
        CALL_FUNCTION,
        CALL_PARAMETER,
        CALL_NATIVE,
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
        LABEL,
        BREAK,
    }

    /** A bound node; {@code atMostOne}: it yields zero or one output whatever its input and environment. */
    public abstract static sealed class Node permits Identity, Literal, Template, Format, Index, Slice, Iterate, Variable, HostVariable,
        EnvVariable, CallFunction, CallParameter, CallNative, Pipe, Comma, Binary, And, Or, Alternative, Negate, ArrayExpr, ObjectExpr, If,
        Try, Reduce, Foreach, Bind, Label, Break {
        public final NodeKind kind;
        public final boolean atMostOne;

        Node(NodeKind kind, boolean atMostOne) {
            this.kind = kind;
            this.atMostOne = atMostOne;
        }

        static boolean all(Node[] nodes) {
            for (Node node : nodes) {
                if (!node.atMostOne) {
                    return false;
                }
            }

            return true;
        }

        static boolean one(Node node) {
            return node == null || node.atMostOne;
        }

        /** Whether the arguments in {@code slots} are all single: what makes a call single (spec §7.7). */
        public static boolean all(int[] slots, Node[] arguments) {
            for (int slot : slots) {
                if (!arguments[slot].atMostOne) {
                    return false;
                }
            }

            return true;
        }
    }

    public static final class Identity extends Node {
        public Identity() {
            super(NodeKind.IDENTITY, true);
        }
    }

    public static final class Literal extends Node {
        public final JqValue value;

        public Literal(JqValue value) {
            super(NodeKind.LITERAL, true);
            this.value = value;
        }
    }

    /** An interpolated string; see {@link Ast.Template}. */
    public static final class Template extends Node {
        public final String format;
        public final String[] texts;
        public final Node[] parts;

        public Template(String format, String[] texts, Node[] parts) {
            super(NodeKind.TEMPLATE, all(parts));
            this.format = format;
            this.texts = texts;
            this.parts = parts;
        }
    }

    public static final class Format extends Node {
        public final String name;

        public Format(String name) {
            super(NodeKind.FORMAT, true);
            this.name = name;
        }
    }

    public static final class Index extends Node {
        public final Node target;
        public final Node key;

        public Index(Node target, Node key) {
            super(NodeKind.INDEX, target.atMostOne && key.atMostOne);
            this.target = target;
            this.key = key;
        }
    }

    public static final class Slice extends Node {
        public final Node target;
        public final Node from;
        public final Node to;

        public Slice(Node target, Node from, Node to) {
            super(NodeKind.SLICE, target.atMostOne && one(from) && one(to));
            this.target = target;
            this.from = from;
            this.to = to;
        }
    }

    public static final class Iterate extends Node {
        public final Node target;

        public Iterate(Node target) {
            super(NodeKind.ITERATE, false);
            this.target = target;
        }
    }

    /** A lexical variable: slot {@code slot} of the frame {@code hops} frames out. */
    public static final class Variable extends Node {
        public final int hops;
        public final int slot;

        public Variable(int hops, int slot) {
            super(NodeKind.VARIABLE, true);
            this.hops = hops;
            this.slot = slot;
        }
    }

    /** A {@code $name} without lexical binding: entry {@code index} of BoundProgram.hostVariables. */
    public static final class HostVariable extends Node {
        public final String name;
        public final int index;

        public HostVariable(String name, int index) {
            super(NodeKind.HOST_VARIABLE, true);
            this.name = name;
            this.index = index;
        }
    }

    /** {@code $ENV} where no binding shadows it. */
    public static final class EnvVariable extends Node {
        public EnvVariable() {
            super(NodeKind.ENV_VARIABLE, true);
        }
    }

    /**
     * A call of BoundProgram.functions[function], defined {@code hops} frames out. {@code single}: the
     * function is single and the argument of each of its {@code $} parameters is (spec §7.7).
     */
    public static final class CallFunction extends Node {
        public final int function;
        public final int hops;
        public final Node[] arguments;
        public final boolean single;

        public CallFunction(int function, int hops, Node[] arguments, boolean single) {
            super(NodeKind.CALL_FUNCTION, single);
            this.function = function;
            this.hops = hops;
            this.arguments = arguments;
            this.single = single;
        }
    }

    /** A call of a closure parameter: slot {@code slot} of the call frame {@code hops} frames out. */
    public static final class CallParameter extends Node {
        public final int hops;
        public final int slot;

        public CallParameter(int hops, int slot) {
            super(NodeKind.CALL_PARAMETER, false);
            this.hops = hops;
            this.slot = slot;
        }
    }

    /** A call of a native; the arguments are closures over the caller's environment. */
    public static final class CallNative extends Node {
        public final Native nativeFunction;
        public final Node[] arguments;

        public CallNative(Native nativeFunction, Node[] arguments) {
            super(NodeKind.CALL_NATIVE, nativeFunction.singleWhen() != null && all(nativeFunction.singleWhen(), arguments));
            this.nativeFunction = nativeFunction;
            this.arguments = arguments;
        }
    }

    public static final class Pipe extends Node {
        public final Node left;
        public final Node right;

        public Pipe(Node left, Node right) {
            super(NodeKind.PIPE, left.atMostOne && right.atMostOne);
            this.left = left;
            this.right = right;
        }
    }

    /** {@code a, b, c}: a left-nested chain of commas is one node, so that evaluating it does not recurse per term. */
    public static final class Comma extends Node {
        public final Node[] items;

        public Comma(Node[] items) {
            super(NodeKind.COMMA, false);
            this.items = items;
        }
    }

    public static final class Binary extends Node {
        public final Ast.BinaryOp op;
        public final Node left;
        public final Node right;

        public Binary(Ast.BinaryOp op, Node left, Node right) {
            super(NodeKind.BINARY, left.atMostOne && right.atMostOne);
            this.op = op;
            this.left = left;
            this.right = right;
        }
    }

    public static final class And extends Node {
        public final Node left;
        public final Node right;

        public And(Node left, Node right) {
            super(NodeKind.AND, left.atMostOne && right.atMostOne);
            this.left = left;
            this.right = right;
        }
    }

    public static final class Or extends Node {
        public final Node left;
        public final Node right;

        public Or(Node left, Node right) {
            super(NodeKind.OR, left.atMostOne && right.atMostOne);
            this.left = left;
            this.right = right;
        }
    }

    public static final class Alternative extends Node {
        public final Node left;
        public final Node right;

        public Alternative(Node left, Node right) {
            super(NodeKind.ALTERNATIVE, left.atMostOne && right.atMostOne);
            this.left = left;
            this.right = right;
        }
    }

    public static final class Negate extends Node {
        public final Node operand;

        public Negate(Node operand) {
            super(NodeKind.NEGATE, operand.atMostOne);
            this.operand = operand;
        }
    }

    public static final class ArrayExpr extends Node {
        public final Node body;

        public ArrayExpr(Node body) {
            super(NodeKind.ARRAY_EXPR, true);
            this.body = body;
        }
    }

    public static final class ObjectExpr extends Node {
        public final ObjectEntry[] entries;

        public ObjectExpr(ObjectEntry[] entries) {
            super(NodeKind.OBJECT_EXPR, allEntries(entries));
            this.entries = entries;
        }

        private static boolean allEntries(ObjectEntry[] entries) {
            for (ObjectEntry entry : entries) {
                if (!entry.key.atMostOne || !one(entry.value)) {
                    return false;
                }
            }

            return true;
        }
    }

    /** See {@link Ast.ObjectEntry}: a null value is the input indexed by each output of the key. */
    public static final class ObjectEntry {
        public final Node key;
        public final Node value;

        public ObjectEntry(Node key, Node value) {
            this.key = key;
            this.value = value;
        }
    }

    public static final class If extends Node {
        public final Node condition;
        public final Node then;
        public final Node otherwise;

        public If(Node condition, Node then, Node otherwise) {
            super(NodeKind.IF, condition.atMostOne && then.atMostOne && otherwise.atMostOne);
            this.condition = condition;
            this.then = then;
            this.otherwise = otherwise;
        }
    }

    public static final class Try extends Node {
        public final Node body;
        public final Node handler;

        public Try(Node body, Node handler) {
            super(NodeKind.TRY, body.atMostOne && one(handler));
            this.body = body;
            this.handler = handler;
        }
    }

    /** {@code update} runs with the binder's frame pushed; {@code init} without it. */
    public static final class Reduce extends Node {
        public final Node source;
        public final Binder binder;
        public final Node init;
        public final Node update;

        public Reduce(Node source, Binder binder, Node init, Node update) {
            super(NodeKind.REDUCE, init.atMostOne);
            this.source = source;
            this.binder = binder;
            this.init = init;
            this.update = update;
        }
    }

    /** {@code update} and {@code extract} run with the binder's frame pushed. */
    public static final class Foreach extends Node {
        public final Node source;
        public final Binder binder;
        public final Node init;
        public final Node update;
        public final Node extract;

        public Foreach(Node source, Binder binder, Node init, Node update, Node extract) {
            super(NodeKind.FOREACH, false);
            this.source = source;
            this.binder = binder;
            this.init = init;
            this.update = update;
            this.extract = extract;
        }
    }

    /** {@code source as patterns | body}: {@code body} runs with the binder's frame pushed. */
    public static final class Bind extends Node {
        public final Node source;
        public final Binder binder;
        public final Node body;

        public Bind(Node source, Binder binder, Node body) {
            super(NodeKind.BIND, source.atMostOne && binder.keysAtMostOne && body.atMostOne);
            this.source = source;
            this.binder = binder;
            this.body = body;
        }
    }

    /** {@code body} runs with a frame holding a new label instance. */
    public static final class Label extends Node {
        public final Node body;

        public Label(Node body) {
            super(NodeKind.LABEL, body.atMostOne);
            this.body = body;
        }
    }

    /** Breaks to the label instance of the frame {@code hops} frames out. */
    public static final class Break extends Node {
        public final int hops;

        public Break(int hops) {
            super(NodeKind.BREAK, true);
            this.hops = hops;
        }
    }

    /**
     * The patterns of one binding (several are the {@code ?//} alternatives) and the size of the
     * frame they fill: one slot per distinct variable name of all of them, null until a pattern binds it.
     */
    public static final class Binder {
        public final Pattern[] patterns;
        public final int slots;

        /** Every key expression of the patterns yields at most one output, so a value binds at most once per pattern. */
        public final boolean keysAtMostOne;

        public Binder(Pattern[] patterns, int slots) {
            this.patterns = patterns;
            this.slots = slots;
            this.keysAtMostOne = allKeys(patterns);
        }

        private static boolean allKeys(Pattern[] patterns) {
            for (Pattern pattern : patterns) {
                if (!keysOf(pattern)) {
                    return false;
                }
            }

            return true;
        }

        private static boolean keysOf(Pattern pattern) {
            switch (pattern) {
                case ArrayPattern array:
                    return allKeys(array.elements);
                case ObjectPattern obj:
                    for (PatternEntry entry : obj.entries) {
                        if (!entry.key.atMostOne || (entry.value != null && !keysOf(entry.value))) {
                            return false;
                        }
                    }

                    return true;
                default:
                    return true;
            }
        }
    }

    public enum PatternKind {
        VARIABLE,
        ARRAY,
        OBJECT,
    }

    public abstract static sealed class Pattern permits VariablePattern, ArrayPattern, ObjectPattern {
        public final PatternKind kind;

        Pattern(PatternKind kind) {
            this.kind = kind;
        }
    }

    /**
     * Stores the value in {@code slot}; -1 discards it. Patterns are matched in order (array
     * elements from the last, object entries from the first), so a slot written twice keeps the later
     * value; the resolver gives -1 to the repetitions that must not win (spec §7.5).
     */
    public static final class VariablePattern extends Pattern {
        public final int slot;

        public VariablePattern(int slot) {
            super(PatternKind.VARIABLE);
            this.slot = slot;
        }
    }

    public static final class ArrayPattern extends Pattern {
        public final Pattern[] elements;

        public ArrayPattern(Pattern[] elements) {
            super(PatternKind.ARRAY);
            this.elements = elements;
        }
    }

    public static final class ObjectPattern extends Pattern {
        public final PatternEntry[] entries;

        public ObjectPattern(PatternEntry[] entries) {
            super(PatternKind.OBJECT);
            this.entries = entries;
        }
    }

    /**
     * See {@link Ast.PatternEntry}; {@code slot} is -1 without a variable. The key runs in the scope
     * outside the binding: the pattern's own variables are not visible in it.
     */
    public static final class PatternEntry {
        public final Node key;
        public final int slot;
        public final Pattern value;

        public PatternEntry(Node key, int slot, Pattern value) {
            this.key = key;
            this.slot = slot;
            this.value = value;
        }
    }

    /**
     * A jqlite-defined function (user or prelude). Its body runs with its parameters' frame pushed
     * when {@code arity} &gt; 0. {@code single}: the body yields at most one output for each binding of
     * the {@code $} parameters, whose slots are {@code valueParameters} (spec §7.7).
     */
    public record Function(String name, int arity, Node body, boolean single, int[] valueParameters) {
        @Override
        public String toString() {
            return name + "/" + arity;
        }
    }

    /**
     * A bound program: immutable and shareable between threads. {@code functions} starts with the
     * prelude's functions (shared by every program), followed by the program's own. {@code modules}
     * are the modules loaded, for {@code modulemeta}; null when the host gave no module source (§9.11).
     *
     * <p>Java: one volatile flag beside the data, a hint of the runtime's StackGuard: an evaluation of
     * the program had to continue on a fresh stack again and again, so later ones start there. It
     * changes no result.
     */
    public static final class BoundProgram {
        private final Function[] functions;
        private final String[] hostVariables;
        private final Node body;
        private final ModuleTable modules;
        private volatile boolean deep;

        public BoundProgram(Function[] functions, String[] hostVariables, Node body, ModuleTable modules) {
            this.functions = functions;
            this.hostVariables = hostVariables;
            this.body = body;
            this.modules = modules;
        }

        public Function[] functions() {
            return functions;
        }

        public String[] hostVariables() {
            return hostVariables;
        }

        public Node body() {
            return body;
        }

        public ModuleTable modules() {
            return modules;
        }

        /** Whether evaluations of the program start on a fresh stack (StackGuard). */
        public boolean startsDeep() {
            return deep;
        }

        /** Marks the program as one whose evaluations start on a fresh stack. */
        public void markDeep() {
            deep = true;
        }
    }
}