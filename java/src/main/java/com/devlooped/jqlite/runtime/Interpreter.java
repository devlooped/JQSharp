package com.devlooped.jqlite.runtime;

import com.devlooped.jqlite.binding.Bound.Alternative;
import com.devlooped.jqlite.binding.Bound.And;
import com.devlooped.jqlite.binding.Bound.ArrayExpr;
import com.devlooped.jqlite.binding.Bound.ArrayPattern;
import com.devlooped.jqlite.binding.Bound.Binary;
import com.devlooped.jqlite.binding.Bound.Bind;
import com.devlooped.jqlite.binding.Bound.Binder;
import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.binding.Bound.Break;
import com.devlooped.jqlite.binding.Bound.CallFunction;
import com.devlooped.jqlite.binding.Bound.CallNative;
import com.devlooped.jqlite.binding.Bound.CallParameter;
import com.devlooped.jqlite.binding.Bound.Comma;
import com.devlooped.jqlite.binding.Bound.Foreach;
import com.devlooped.jqlite.binding.Bound.Format;
import com.devlooped.jqlite.binding.Bound.HostVariable;
import com.devlooped.jqlite.binding.Bound.If;
import com.devlooped.jqlite.binding.Bound.Index;
import com.devlooped.jqlite.binding.Bound.Iterate;
import com.devlooped.jqlite.binding.Bound.Label;
import com.devlooped.jqlite.binding.Bound.Literal;
import com.devlooped.jqlite.binding.Bound.Negate;
import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.binding.Bound.NodeKind;
import com.devlooped.jqlite.binding.Bound.ObjectEntry;
import com.devlooped.jqlite.binding.Bound.ObjectExpr;
import com.devlooped.jqlite.binding.Bound.ObjectPattern;
import com.devlooped.jqlite.binding.Bound.Or;
import com.devlooped.jqlite.binding.Bound.Pattern;
import com.devlooped.jqlite.binding.Bound.PatternEntry;
import com.devlooped.jqlite.binding.Bound.Pipe;
import com.devlooped.jqlite.binding.Bound.Reduce;
import com.devlooped.jqlite.binding.Bound.Slice;
import com.devlooped.jqlite.binding.Bound.Template;
import com.devlooped.jqlite.binding.Bound.Try;
import com.devlooped.jqlite.binding.Bound.Variable;
import com.devlooped.jqlite.binding.Bound.VariablePattern;
import com.devlooped.jqlite.builtins.Formats;
import com.devlooped.jqlite.builtins.Native;
import com.devlooped.jqlite.builtins.NativeTable;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import com.devlooped.jqlite.runtime.Evaluation.IClock;
import com.devlooped.jqlite.runtime.Evaluation.Outcome;
import com.devlooped.jqlite.runtime.Evaluation.OutcomeKind;
import com.devlooped.jqlite.syntax.Ast.BinaryOp;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.function.Function;
import java.util.function.Predicate;
import java.util.function.Supplier;

/**
 * The push-based interpreter of a bound program (spec §6 to §8). One instance is one evaluation: it
 * holds every piece of per-evaluation state (host variables, {@code $ENV}, the call depth, label
 * numbers, scratch stacks), while the bound program and the prelude are immutable and shared, so one
 * program is evaluated concurrently from many threads, each with its own interpreter.
 *
 * <p>{@link #eval} evaluates a node with an input and an environment and pushes each output, with
 * its path in path mode, to {@code emit}. It returns null when the node is done, or the
 * {@link Signal} that stops it: an error, a break, a halt, or a signal returned by the consumer,
 * which is passed up unchanged. Nothing throws. Path mode (§8.1) is {@code path} not null: then every
 * output carries where it came from (see {@link Path}).
 *
 * <p>{@link #evalOne} is the same evaluation for nodes that yield at most one output (the resolver
 * marks them, {@code Node.atMostOne}): it writes the output to a {@link One} holder instead of
 * pushing it, so arithmetic, indexing, object construction and the like run without callbacks.
 * {@link #eval} hands such nodes to it. A call it evaluates (a single call, spec §7.7) has ended when
 * its output is returned, so it leaves the depth of §11 before that output is used; a call
 * {@link #eval} enters stays in the depth while its outputs are pushed.
 *
 * <p>The host stack: both are loops where a node's last step is another node (a pipe whose left side
 * yields at most one value, a branch of {@code if}, the body of a binding or of a function, a
 * closure, the last item of a comma), so tail positions and calls in them do not recurse; left-deep
 * chains of operators and indexes are walked with scratch stacks. What still recurses (nesting,
 * non-tail calls, generators feeding generators) checks the host stack where an evaluation starts
 * and where an output is pushed, and continues on a fresh stack when it runs short (StackGuard), so
 * the only recursion limit a program meets is MaxDepth (§11).
 */
public final class Interpreter {
    /**
     * The output of {@link #evalOne}: {@code has} tells whether the node gave one, {@code value} and
     * {@code at} are the output and its path. The caller owns the holder and reads it at once.
     */
    public static final class One {
        public boolean has;
        public JqValue value = JqValue.NULL;
        public Path at;
        // For bindOne: the environment with the new frame.
        Env frame;
        // For evalEntryOne: the key.
        String key;
    }

    private final BoundProgram program;
    // The host variables, by the index of BoundProgram.hostVariables; Java null for an absent one.
    private final JqValue[] hostValues;
    private final Supplier<JqValue> readEnvironment;
    private JqValue environment;
    private final int maxDepth;
    private int depth;
    private long labels;

    // The stack budget of the thread the evaluation runs on, and the hops so far (StackGuard).
    private StackGuard.Budget stack = StackGuard.current();
    private int hops;

    // Scratch stacks of the chain walks of evalOne; each walk uses the part above its mark.
    private final ArrayList<Node> spineNodes = new ArrayList<>();
    private final ArrayList<JqValue> spineValues = new ArrayList<>();

    private final IClock clock;
    private final java.util.function.Consumer<JqValue> debug;
    private final java.util.function.Consumer<JqValue> stderr;

    private Interpreter(BoundProgram program, EvaluationOptions options) {
        this.program = program;
        hostValues = new JqValue[program.hostVariables().length];
        for (int i = 0; i < hostValues.length; i++) {
            if (options.variables != null) {
                hostValues[i] = options.variables.get(program.hostVariables()[i]);
            }
        }

        readEnvironment = options.environment;
        maxDepth = options.maxDepth;
        clock = options.clock;
        debug = options.debug;
        stderr = options.stderr;
    }

    /** The program being evaluated. */
    public BoundProgram program() {
        return program;
    }

    /** The host's clock, for the date builtins; null without one. */
    public IClock clock() {
        return clock;
    }

    /** The host's handler of {@code debug}; null without one. */
    public java.util.function.Consumer<JqValue> debug() {
        return debug;
    }

    /** The host's handler of {@code stderr}; null without one. */
    public java.util.function.Consumer<JqValue> stderr() {
        return stderr;
    }

    /**
     * Evaluates {@code program} on {@code input}, giving each output to {@code output}, which returns
     * false to stop the stream.
     */
    public static Outcome run(BoundProgram program, JqValue input, EvaluationOptions options, Predicate<JqValue> output) {
        Interpreter interpreter = new Interpreter(program, options);
        Emit emit = (value, at) -> output.test(value) ? null : Signal.STOP;
        Signal signal = program.startsDeep()
            ? interpreter.onFreshStack(() -> interpreter.eval(program.body(), null, input, null, emit))
            : interpreter.eval(program.body(), null, input, null, emit);
        if (interpreter.hops >= StackGuard.DEEP_HOPS) {
            program.markDeep();
        }
        if (signal == null) {
            return new Outcome(OutcomeKind.COMPLETED);
        }
        return switch (signal.kind) {
            case ERROR -> new Outcome(OutcomeKind.ERROR, signal.value, true, 0);
            case HALT -> new Outcome(OutcomeKind.HALT, signal.value, signal.hasValue, signal.code);
            // A break always meets its label: the resolver binds every break to an enclosing one.
            default -> new Outcome(OutcomeKind.STOPPED);
        };
    }

    /**
     * Evaluates {@code node} and pushes each output to {@code emit}. Each nesting level of the program
     * costs a frame of this method (or of {@link #evalOne}) and one of a helper, so both keep few
     * locals: every case is a call, and the tail cases only reassign the parameters.
     */
    public Signal eval(Node node, Env env, JqValue input, Path path, Emit emit) {
        if (!StackGuard.hasStack(stack)) {
            return evalOnFreshStack(node, env, input, path, emit);
        }

        stack.active++;
        // Function calls entered by this frame (tail calls): they stay active, counting in the depth
        // of §11, while their outputs are consumed, that is until this frame returns.
        int calls = 0;
        Signal signal;
        One one = null;
        try {
            loop:
            while (true) {
                if (node.atMostOne) {
                    if (one == null) {
                        one = new One();
                    }
                    signal = evalOne(node, env, input, path, one);
                    if (signal == null && one.has) {
                        signal = output(emit, one.value, one.at);
                    }
                    break;
                }

                switch (node.kind) {
                    case COMMA: {
                        Comma comma = (Comma) node;
                        signal = evalItems(comma, env, input, path, emit);
                        if (signal != null) {
                            break loop;
                        }
                        node = comma.items[comma.items.length - 1];
                        continue;
                    }
                    case PIPE: {
                        Pipe pipe = (Pipe) node;
                        if (!pipe.left.atMostOne) {
                            signal = evalPipe(pipe, env, input, path, emit);
                            break loop;
                        }
                        if (one == null) {
                            one = new One();
                        }
                        signal = evalOne(pipe.left, env, input, path, one);
                        if (signal != null || !one.has) {
                            break loop;
                        }
                        input = one.value;
                        path = one.at;
                        node = pipe.right;
                        continue;
                    }
                    case IF: {
                        If branch = (If) node;
                        if (!branch.condition.atMostOne) {
                            signal = evalIf(branch, env, input, path, emit);
                            break loop;
                        }
                        if (one == null) {
                            one = new One();
                        }
                        signal = evalOne(branch.condition, env, input, null, one);
                        if (signal != null || !one.has) {
                            break loop;
                        }
                        node = one.value.isTruthy() ? branch.then : branch.otherwise;
                        continue;
                    }
                    case ALTERNATIVE: {
                        Alternative alternative = (Alternative) node;
                        boolean[] found = {false};
                        signal = evalAlternative(alternative, env, input, path, emit, found);
                        if (signal != null || found[0]) {
                            break loop;
                        }
                        node = alternative.right;
                        continue;
                    }
                    case BIND: {
                        Bind bind = (Bind) node;
                        if (!isSimple(bind)) {
                            signal = evalBind(bind, env, input, path, emit);
                            break loop;
                        }
                        if (one == null) {
                            one = new One();
                        }
                        signal = bindOne(bind, env, input, one);
                        if (signal != null || !one.has) {
                            break loop;
                        }
                        env = one.frame;
                        node = bind.body;
                        continue;
                    }
                    case CALL_FUNCTION: {
                        CallFunction call = (CallFunction) node;
                        calls++;
                        if (++depth > maxDepth) {
                            signal = fail(Errors.depthExceeded());
                            break loop;
                        }

                        env = callEnv(call.hops, call.arguments, env);
                        node = program.functions()[call.function].body();
                        continue;
                    }
                    case CALL_PARAMETER: {
                        CallParameter parameter = (CallParameter) node;
                        Env.Closure closure = Env.up(env, parameter.hops).closures[parameter.slot];
                        env = closure.env;
                        node = closure.node;
                        continue;
                    }
                    default:
                        signal = evalMany(node, env, input, path, emit);
                        break loop;
                }
            }
        } finally {
            stack.active--;
        }

        depth -= calls;
        return signal;
    }

    // The items of a comma but the last, which eval evaluates in tail position.
    private Signal evalItems(Comma node, Env env, JqValue input, Path path, Emit emit) {
        Node[] items = node.items;
        for (int i = 0; i < items.length - 1; i++) {
            Signal signal = eval(items[i], env, input, path, emit);
            if (signal != null) {
                return signal;
            }
        }

        return null;
    }

    // A binding eval and evalOne take in tail position: one pattern, a source and keys that yield at most one output.
    private static boolean isSimple(Bind node) {
        return node.source.atMostOne && node.binder.keysAtMostOne && node.binder.patterns.length == 1;
    }

    // Binds the output of a simple binding's source: one.frame gets the environment with the new
    // frame; one.has is false when nothing binds.
    private Signal bindOne(Bind node, Env env, JqValue input, One one) {
        Signal signal = evalOne(node.source, env, input, null, one);
        if (signal != null || !one.has) {
            return signal;
        }
        JqValue source = one.value;
        JqValue[] slots = newSlots(node.binder.slots);
        signal = matchOne(node.binder.patterns[0], source, slots, env, one);
        if (signal == null && one.has) {
            one.frame = new Env(env, slots);
        }
        return signal;
    }

    // The constructs of eval that are not in tail position.
    private Signal evalMany(Node node, Env env, JqValue input, Path path, Emit emit) {
        return switch (node.kind) {
            case PIPE -> evalPipe((Pipe) node, env, input, path, emit);
            case IF -> evalIf((If) node, env, input, path, emit);
            case BIND -> evalBind((Bind) node, env, input, path, emit);
            case ITERATE -> evalIterate((Iterate) node, env, input, path, emit);
            case INDEX -> evalIndex((Index) node, env, input, path, emit);
            case SLICE -> evalSlice((Slice) node, env, input, path, emit);
            case BINARY -> evalBinary((Binary) node, env, input, path, emit);
            case AND, OR -> evalLogic(node, env, input, path, emit);
            case NEGATE -> evalNegate((Negate) node, env, input, path, emit);
            case OBJECT_EXPR -> evalObject((ObjectExpr) node, 0, JqObject.EMPTY, env, input, Path.of(path), emit);
            case TEMPLATE -> {
                Template template = (Template) node;
                yield evalTemplate(template, template.parts.length - 1, new String[template.parts.length], env, input, Path.of(path), emit);
            }
            case TRY -> evalTry((Try) node, env, input, path, emit);
            case REDUCE -> evalReduce((Reduce) node, env, input, path, emit);
            case FOREACH -> evalForeach((Foreach) node, env, input, path, emit);
            case LABEL -> evalLabel((Label) node, env, input, path, emit);
            default -> evalNative((CallNative) node, env, input, path, emit);
        };
    }

    private Signal evalPipe(Pipe node, Env env, JqValue input, Path path, Emit emit) {
        return eval(node.left, env, input, path, (value, at) -> eval(node.right, env, value, at, emit));
    }

    private Signal evalIf(If node, Env env, JqValue input, Path path, Emit emit) {
        return eval(node.condition, env, input, null, (condition, unused) -> eval(condition.isTruthy() ? node.then : node.otherwise, env, input, path, emit));
    }

    private Signal evalNegate(Negate node, Env env, JqValue input, Path path, Emit emit) {
        Path at = Path.of(path);
        return eval(node.operand, env, input, null, (value, unused) -> apply(ValueOps.tryNegate(value), at, emit));
    }

    /**
     * Evaluates a node that yields at most one output ({@code Node.atMostOne}) into {@code one}:
     * {@code one.has} tells whether it did, {@code one.value} and {@code one.at} are the output and
     * its path. Any signal returned is the node's own: there is no consumer.
     */
    public Signal evalOne(Node node, Env env, JqValue input, Path path, One one) {
        if (!StackGuard.hasStack(stack)) {
            return evalOneOnFreshStack(node, env, input, path, one);
        }

        stack.active++;
        try {
            return evalOneLoop(node, env, input, path, one);
        } finally {
            stack.active--;
        }
    }

    private Signal evalOneLoop(Node node, Env env, JqValue input, Path path, One one) {
        Signal signal;
        while (true) {
            switch (node.kind) {
                case IDENTITY:
                    return one(input, path, one);
                case LITERAL:
                    return one(((Literal) node).value, Path.of(path), one);
                case VARIABLE: {
                    Variable variable = (Variable) node;
                    return one(Env.up(env, variable.hops).values[variable.slot], Path.of(path), one);
                }
                case PIPE: {
                    Pipe pipe = (Pipe) node;
                    signal = evalOne(pipe.left, env, input, path, one);
                    if (signal != null || !one.has) {
                        return signal;
                    }
                    input = one.value;
                    path = one.at;
                    node = pipe.right;
                    continue;
                }
                case IF: {
                    If branch = (If) node;
                    signal = evalOne(branch.condition, env, input, null, one);
                    if (signal != null || !one.has) {
                        return signal;
                    }
                    node = one.value.isTruthy() ? branch.then : branch.otherwise;
                    continue;
                }
                case ALTERNATIVE: {
                    Alternative alternative = (Alternative) node;
                    signal = evalOne(alternative.left, env, input, path, one);
                    if (signal != null || (one.has && one.value.isTruthy())) {
                        return signal;
                    }
                    node = alternative.right;
                    continue;
                }
                case TRY: {
                    Try attempt = (Try) node;
                    signal = evalOne(attempt.body, env, input, path, one);
                    if (signal == null || signal.kind != Signal.SignalKind.ERROR) {
                        return signal;
                    }
                    one.has = false;
                    if (attempt.handler == null) {
                        return null;
                    }
                    node = attempt.handler;
                    input = signal.value;
                    path = Path.of(path);
                    continue;
                }
                case BIND: {
                    Bind bind = (Bind) node;
                    if (bind.binder.patterns.length != 1) {
                        return evalOther(node, env, input, path, one);
                    }
                    signal = bindOne(bind, env, input, one);
                    if (signal != null || !one.has) {
                        return signal;
                    }
                    env = one.frame;
                    node = bind.body;
                    continue;
                }
                case CALL_FUNCTION:
                    return evalCallOne((CallFunction) node, env, input, path, one);
                case CALL_PARAMETER: {
                    // The argument of a $ parameter of a single call (Resolver.call): it is single too.
                    CallParameter parameter = (CallParameter) node;
                    Env.Closure closure = Env.up(env, parameter.hops).closures[parameter.slot];
                    env = closure.env;
                    node = closure.node;
                    continue;
                }
                default:
                    return evalOther(node, env, input, path, one);
            }
        }
    }

    // A single call (spec §7.7): it has ended when its output is here, so, unlike a call eval
    // enters, it does not count in the depth of §11 while that output is consumed.
    private Signal evalCallOne(CallFunction call, Env env, JqValue input, Path path, One one) {
        if (++depth > maxDepth) {
            depth--;
            one.has = false;
            return fail(Errors.depthExceeded());
        }

        Signal signal = evalOne(program.functions()[call.function].body(), callEnv(call.hops, call.arguments, env), input, path, one);
        depth--;
        return signal;
    }

    // The nodes of evalOne that are not in tail position.
    private Signal evalOther(Node node, Env env, JqValue input, Path path, One one) {
        Path at = Path.of(path);
        Signal signal;
        switch (node.kind) {
            case INDEX, SLICE:
                return evalPostfixOne(node, env, input, path, one);
            case LABEL:
                return evalLabelOne((Label) node, env, input, path, one);
            case BIND:
                return evalBindOne((Bind) node, env, input, path, one);
            case REDUCE:
                return evalReduceOne((Reduce) node, env, input, path, one);
            case CALL_NATIVE:
                if (((CallNative) node).nativeFunction.takesClosures()) {
                    return evalClosureNativeOne((CallNative) node, env, input, path, one);
                }
                signal = evalNativeOne((CallNative) node, env, input, one);
                break;
            case BINARY:
                signal = evalBinaryOne((Binary) node, env, input, one);
                break;
            case AND, OR:
                signal = evalLogicOne(node, env, input, one);
                break;
            case NEGATE:
                signal = evalNegateOne((Negate) node, env, input, one);
                break;
            case ARRAY_EXPR:
                signal = evalArrayOne((ArrayExpr) node, env, input, one);
                break;
            case OBJECT_EXPR:
                signal = evalObjectOne((ObjectExpr) node, env, input, one);
                break;
            case TEMPLATE:
                signal = evalTemplateOne((Template) node, env, input, one);
                break;
            default:
                signal = evalLeafOne(node, env, input, one);
                break;
        }

        one.at = at;
        return signal;
    }

    // Variables, formats and break: no children.
    private Signal evalLeafOne(Node node, Env env, JqValue input, One one) {
        one.has = false;
        switch (node) {
            case HostVariable host -> {
                JqValue hostValue = hostValues[host.index];
                if (hostValue == null) {
                    return fail(Errors.variableNotDefined(host.name));
                }
                one.has = true;
                one.value = hostValue;
                return null;
            }
            case Format format -> {
                Result formatted = Formats.tryApply(format.name, input);
                if (formatted instanceof Result.Err err) {
                    return Signal.error(err.error());
                }
                one.has = true;
                one.value = (JqValue) formatted;
                return null;
            }
            case Break brk -> {
                return Signal.breakTo(Env.up(env, brk.hops).label);
            }
            default -> {
                // EnvVariable
                if (environment == null) {
                    environment = readEnvironment.get();
                }
                one.has = true;
                one.value = environment;
                return null;
            }
        }
    }

    private Signal evalLabelOne(Label node, Env env, JqValue input, Path path, One one) {
        long label = ++labels;
        Signal signal = evalOne(node.body, new Env(env, label), input, path, one);
        return signal != null && signal.kind == Signal.SignalKind.BREAK && signal.label == label ? null : signal;
    }

    // A native that takes closures and gives at most one output (Native.singleWhen): its output is kept.
    private Signal evalClosureNativeOne(CallNative node, Env env, JqValue input, Path path, One one) {
        Capture capture = new Capture();
        Signal signal = NativeTable.closure(node.nativeFunction).call(this, env, node.arguments, input, path, capture::emit);
        one.has = capture.has;
        one.value = capture.value;
        one.at = capture.at;
        return signal;
    }

    // Reduce with one initial value: through the general evaluation, keeping its output.
    private Signal evalReduceOne(Reduce node, Env env, JqValue input, Path path, One one) {
        Capture capture = new Capture();
        Signal signal = evalReduce(node, env, input, path, capture::emit);
        one.has = capture.has;
        one.value = capture.value;
        one.at = capture.at;
        return signal;
    }

    /** Evaluates {@code node} and calls {@code each} with each output, without a callback for a node that yields at most one. */
    public Signal forEach(Node node, Env env, JqValue input, Path path, Emit each) {
        if (!node.atMostOne) {
            return eval(node, env, input, path, each);
        }
        One one = new One();
        Signal signal = evalOne(node, env, input, path, one);
        return signal == null && one.has ? each.emit(one.value, one.at) : signal;
    }

    /**
     * Pushes an output to a consumer. Every output goes through here, because consumers chain: the
     * outputs of a deep recursion flow back through one callback per level, which adds to the stack
     * the evaluation used on the way down, so the guard of StackGuard is needed here as much as at
     * the start of an evaluation.
     */
    public Signal output(Emit emit, JqValue value, Path at) {
        if (!StackGuard.hasStack(stack)) {
            return onFreshStack(() -> emit.emit(value, at));
        }

        stack.active++;
        try {
            return emit.emit(value, at);
        } finally {
            stack.active--;
        }
    }

    // The continuations of eval and evalOne on a fresh stack (StackGuard), with the budget of the
    // fresh stack while the step runs. Recursion only grows through function calls, which the depth
    // counter stops at MaxDepth, so the number of hops is bounded.
    private Signal evalOnFreshStack(Node node, Env env, JqValue input, Path path, Emit emit) {
        return onFreshStack(() -> eval(node, env, input, path, emit));
    }

    private Signal evalOneOnFreshStack(Node node, Env env, JqValue input, Path path, One one) {
        return onFreshStack(() -> evalOne(node, env, input, path, one));
    }

    private Signal onFreshStack(Supplier<Signal> step) {
        hops++;
        StackGuard.Budget saved = stack;
        return StackGuard.onFreshStack(() -> {
            stack = StackGuard.current();
            try {
                return step.get();
            } finally {
                stack = saved;
            }
        });
    }

    /** The next label instance, unique within this evaluation. */
    public long newLabel() {
        return ++labels;
    }

    /** An error signal. */
    public static Signal fail(JqValue error) {
        return Signal.error(error);
    }

    private static Signal one(JqValue output, Path path, One one) {
        one.has = true;
        one.value = output;
        one.at = path;
        return null;
    }

    /** Pushes a value native's or an operation's result, or returns its error. */
    public Signal apply(Result result, Path at, Emit emit) {
        return result instanceof Result.Err err ? Signal.error(err.error()) : output(emit, (JqValue) result, at);
    }

    // The environment of a call: the caller's with the frames between the call and the definition
    // removed, plus the closures of the arguments when there are any.
    private static Env callEnv(int hops, Node[] arguments, Env env) {
        Env defined = Env.up(env, hops);
        if (arguments.length == 0) {
            return defined;
        }
        Env.Closure[] closures = new Env.Closure[arguments.length];
        for (int i = 0; i < closures.length; i++) {
            closures[i] = new Env.Closure(arguments[i], env);
        }
        return new Env(defined, closures);
    }

    // a // b: the true outputs of a; found[0] tells whether there was one (then b is not evaluated).
    private Signal evalAlternative(Alternative node, Env env, JqValue input, Path path, Emit emit, boolean[] found) {
        return eval(node.left, env, input, path, (value, at) -> {
            if (!value.isTruthy()) {
                return null;
            }
            found[0] = true;
            return output(emit, value, at);
        });
    }

    // T[]: in path mode each element's path is the target's plus its index or key.
    private Signal evalIterate(Iterate node, Env env, JqValue input, Path path, Emit emit) {
        return forEach(node.target, env, input, path, (target, at) -> {
            if (at != null && !at.isTracked()) {
                return fail(Errors.invalidPathIterate(target));
            }
            if (target.kind() == JqKind.ARRAY) {
                JqArray array = target.asArray();
                for (int i = 0; i < array.count(); i++) {
                    Signal stop = output(emit, array.get(i), at == null ? null : at.append(JqValue.fromNumber(i)));
                    if (stop != null) {
                        return stop;
                    }
                }

                return null;
            }

            if (target.kind() != JqKind.OBJECT) {
                return fail(Errors.cannotIterate(target));
            }
            JqObject obj = target.asObject();
            for (int i = 0; i < obj.count(); i++) {
                Signal stop = output(emit, obj.valueAt(i), at == null ? null : at.append(JqValue.fromString(obj.keyAt(i))));
                if (stop != null) {
                    return stop;
                }
            }

            return null;
        });
    }

    // T[e]: for each key (on the input of T, in value mode), for each target (§6.3).
    private Signal evalIndex(Index node, Env env, JqValue input, Path path, Emit emit) {
        return forEach(node.key, env, input, null, (key, unused) ->
            forEach(node.target, env, input, path, (target, at) -> {
                One step = new One();
                Signal signal = indexStep(target, at, key, step);
                return signal != null ? signal : output(emit, step.value, step.at);
            }));
    }

    // T[from:to]: from outermost, then to, then T (§6.3).
    private Signal evalSlice(Slice node, Env env, JqValue input, Path path, Emit emit) {
        return bound(node.from, env, input, (from, unused1) ->
            bound(node.to, env, input, (to, unused2) ->
                forEach(node.target, env, input, path, (target, at) -> {
                    One step = new One();
                    Signal signal = sliceStep(target, at, from, to, step);
                    return signal != null ? signal : output(emit, step.value, step.at);
                })));
    }

    // A slice bound: null when absent.
    private Signal bound(Node bound, Env env, JqValue input, Emit each) {
        return bound == null ? each.emit(JqValue.NULL, null) : forEach(bound, env, input, null, each);
    }

    // One index of a target with its path: in path mode the target must be tracked.
    private static Signal indexStep(JqValue target, Path at, JqValue key, One next) {
        next.at = at;
        if (at != null && !at.isTracked()) {
            return fail(Errors.invalidPathAccess(key, target));
        }

        Result result = ValueOps.tryIndex(target, key);
        if (result instanceof Result.Err err) {
            return Signal.error(err.error());
        }
        next.has = true;
        next.value = (JqValue) result;
        next.at = at == null ? null : at.append(key);
        return null;
    }

    // One slice of a target with its path; in a path the slice is the key {"start": from, "end": to} (§8.1).
    private static Signal sliceStep(JqValue target, Path at, JqValue from, JqValue to, One next) {
        next.at = at;
        if (at != null) {
            JqValue key = JqObject.EMPTY.set("start", from).set("end", to);
            if (!at.isTracked()) {
                return fail(Errors.invalidPathAccess(key, target));
            }
            next.at = at.append(key);
        }

        Result result = ValueOps.trySlice(target, from, to);
        if (result instanceof Result.Err err) {
            return Signal.error(err.error());
        }
        next.has = true;
        next.value = (JqValue) result;
        return null;
    }

    // a op b: for each right output, for each left output (§6.3); operands in value mode. A right
    // operand that yields at most one value is evaluated first, so the common case has one callback.
    private Signal evalBinary(Binary node, Env env, JqValue input, Path path, Emit emit) {
        Path at = Path.of(path);
        if (!node.right.atMostOne) {
            return evalRights(node, env, input, at, emit);
        }
        One one = new One();
        Signal signal = evalOne(node.right, env, input, null, one);
        return signal == null && one.has ? evalLeft(node, one.value, env, input, at, emit) : signal;
    }

    private Signal evalRights(Binary node, Env env, JqValue input, Path at, Emit emit) {
        return eval(node.right, env, input, null, (right, unused) -> evalLeft(node, right, env, input, at, emit));
    }

    private Signal evalLeft(Binary node, JqValue right, Env env, JqValue input, Path at, Emit emit) {
        return eval(node.left, env, input, null, (left, unused) -> apply(tryApply(node.op, left, right), at, emit));
    }

    /** The arithmetic operators and comparisons of §4. */
    public static Result tryApply(BinaryOp op, JqValue left, JqValue right) {
        return switch (op) {
            case ADD -> ValueOps.tryAdd(left, right);
            case SUBTRACT -> ValueOps.trySubtract(left, right);
            case MULTIPLY -> ValueOps.tryMultiply(left, right);
            case DIVIDE -> ValueOps.tryDivide(left, right);
            case MODULO -> ValueOps.tryModulo(left, right);
            case EQUAL -> JqValue.fromBool(ValueOps.areEqual(left, right));
            case NOT_EQUAL -> JqValue.fromBool(!ValueOps.areEqual(left, right));
            case LESS -> JqValue.fromBool(ValueOps.compare(left, right) < 0);
            case LESS_EQUAL -> JqValue.fromBool(ValueOps.compare(left, right) <= 0);
            case GREATER -> JqValue.fromBool(ValueOps.compare(left, right) > 0);
            case GREATER_EQUAL -> JqValue.fromBool(ValueOps.compare(left, right) >= 0);
        };
    }

    // a and b, a or b (§7.3): for each left output, the right side only when the left does not decide.
    private Signal evalLogic(Node node, Env env, JqValue input, Path path, Emit emit) {
        boolean isAnd = node.kind == NodeKind.AND;
        Node left = isAnd ? ((And) node).left : ((Or) node).left;
        Node right = isAnd ? ((And) node).right : ((Or) node).right;
        Path at = Path.of(path);
        return forEach(left, env, input, null, (first, unused1) ->
            first.isTruthy() != isAnd
                ? output(emit, JqValue.fromBool(!isAnd), at)
                : forEach(right, env, input, null, (second, unused2) -> output(emit, JqValue.fromBool(second.isTruthy()), at)));
    }

    // {k1: v1, …} from entry i on, with the entries before it in obj: keys and values in value
    // mode, k1 outermost (§6.3). Entries that yield at most one output are taken in a loop.
    private Signal evalObject(ObjectExpr node, int i, JqObject obj, Env env, JqValue input, Path at, Emit emit) {
        One one = null;
        for (; i < node.entries.length; i++) {
            ObjectEntry entry = node.entries[i];
            if (!entry.key.atMostOne || (entry.value != null && !entry.value.atMostOne)) {
                break;
            }
            if (one == null) {
                one = new One();
            }
            Signal signal = evalEntryOne(entry, env, input, one);
            if (signal != null || !one.has) {
                return signal;
            }
            obj = obj.set(one.key, one.value);
        }

        if (i == node.entries.length) {
            return output(emit, obj, at);
        }
        ObjectEntry next = node.entries[i];
        int index = i;
        JqObject before = obj;
        return forEach(next.key, env, input, null, (key, unused1) -> {
            if (key.kind() != JqKind.STRING) {
                return fail(Errors.cannotUseAsKey(key));
            }
            if (next.value == null) {
                Result indexed = ValueOps.tryIndex(input, key);
                return indexed instanceof Result.Err err
                    ? Signal.error(err.error())
                    : evalObject(node, index + 1, before.set(key.asString(), (JqValue) indexed), env, input, at, emit);
            }

            return forEach(next.value, env, input, null, (value, unused2) -> evalObject(node, index + 1, before.set(key.asString(), value), env, input, at, emit));
        });
    }

    // One entry whose key and value yield at most one output: one.key is the key, one.value the value.
    private Signal evalEntryOne(ObjectEntry entry, Env env, JqValue input, One one) {
        Signal signal = evalOne(entry.key, env, input, null, one);
        if (signal != null || !one.has) {
            return signal;
        }
        JqValue name = one.value;
        if (name.kind() != JqKind.STRING) {
            return fail(Errors.cannotUseAsKey(name));
        }
        String key = name.asString();
        if (entry.value == null) {
            Result indexed = ValueOps.tryIndex(input, name);
            if (indexed instanceof Result.Err err) {
                return Signal.error(err.error());
            }
            one.key = key;
            one.has = true;
            one.value = (JqValue) indexed;
            return null;
        }
        signal = evalOne(entry.value, env, input, null, one);
        one.key = key;
        return signal;
    }

    // "a\(x)b\(y)": the parts from part i down to the first, the last part outermost (§6.3).
    private Signal evalTemplate(Template node, int i, String[] texts, Env env, JqValue input, Path at, Emit emit) {
        if (i < 0) {
            return output(emit, JqValue.fromString(join(node, texts)), at);
        }
        return forEach(node.parts[i], env, input, null, (part, unused) -> {
            Result text = convert(node.format, part);
            if (text instanceof Result.Err err) {
                return Signal.error(err.error());
            }
            texts[i] = ((JqValue) text).asString();
            return evalTemplate(node, i - 1, texts, env, input, at, emit);
        });
    }

    // An interpolated value as text (a string value): tostring, or the template's format.
    private static Result convert(String format, JqValue part) {
        if (format == null) {
            return part.kind() == JqKind.STRING ? part : JqValue.fromString(ValueText.toJson(part));
        }

        return Formats.tryApply(format, part);
    }

    private static String join(Template node, String[] parts) {
        StringBuilder builder = new StringBuilder(node.texts[0]);
        for (int i = 0; i < parts.length; i++) {
            builder.append(parts[i]).append(node.texts[i + 1]);
        }
        return builder.toString();
    }

    // try B catch H (§7.13): only B's own errors are caught, never one its consumer returned (§6.2).
    private Signal evalTry(Try node, Env env, JqValue input, Path path, Emit emit) {
        Consumer consumer = new Consumer(emit);
        Signal signal = eval(node.body, env, input, path, consumer::emit);
        if (signal == null || signal.kind != Signal.SignalKind.ERROR || consumer.raised(signal)) {
            return signal;
        }
        return node.handler == null ? null : eval(node.handler, env, signal.value, Path.of(path), emit);
    }

    /**
     * Tells an operand's own signals from those of its consumer (§6.2). It stands between the
     * operand and the consumer and remembers the last signal the consumer returned; every construct
     * passes a signal up unchanged and stops at once, so when the operand returns that very object,
     * it came from the consumer. Comparing identities costs nothing per output.
     */
    public final class Consumer {
        private final Emit emit;
        private Signal last;

        public Consumer(Emit emit) {
            this.emit = emit;
        }

        public Signal emit(JqValue value, Path path) {
            last = output(emit, value, path);
            return last;
        }

        public boolean raised(Signal signal) {
            return signal == last;
        }
    }

    // Keeps the output of a node that yields at most one.
    private static final class Capture {
        boolean has;
        JqValue value = JqValue.NULL;
        Path at;

        Signal emit(JqValue value, Path at) {
            this.has = true;
            this.value = value;
            this.at = at;
            return null;
        }
    }

    private Signal evalLabel(Label node, Env env, JqValue input, Path path, Emit emit) {
        long label = ++labels;
        Signal signal = eval(node.body, new Env(env, label), input, path, emit);
        return signal != null && signal.kind == Signal.SignalKind.BREAK && signal.label == label ? null : signal;
    }

    // E as P1 ?// P2 | B, in general: for each output of E (value mode), each binding, B in path mode.
    private Signal evalBind(Bind node, Env env, JqValue input, Path path, Emit emit) {
        Consumer consumer = node.binder.patterns.length > 1 ? new Consumer(emit) : null;
        Emit body = consumer == null ? emit : consumer::emit;
        return forEach(node.source, env, input, null, (source, unused) ->
            bindEach(node.binder, source, env, frame -> eval(node.body, frame, input, path, body), consumer, null));
    }

    // E as P1 ?// P2 | B where E, the keys and B yield at most one output.
    private Signal evalBindOne(Bind node, Env env, JqValue input, Path path, One one) {
        Signal signal = evalOne(node.source, env, input, null, one);
        if (signal != null || !one.has) {
            return signal;
        }
        JqValue source = one.value;
        Pattern[] patterns = node.binder.patterns;
        for (int i = 0; i < patterns.length; i++) {
            JqValue[] slots = newSlots(node.binder.slots);
            signal = matchOne(patterns[i], source, slots, env, one);
            if (signal == null && one.has) {
                signal = evalOne(node.body, new Env(env, slots), input, path, one);
            }
            if (signal == null || signal.kind != Signal.SignalKind.ERROR || i == patterns.length - 1) {
                return signal;
            }
            one.has = false;
        }

        return null;
    }

    /**
     * Binds {@code value} by the binder's patterns and runs {@code body} in the environment with the
     * new frame, once per binding (several when a key has several outputs). With {@code ?//}
     * alternatives (§7.5), when matching fails or the body raises its own error (not one of
     * {@code consumer}), the next pattern is tried with the same value, all variables null again,
     * after restoring what {@code save} saved before the first (the state of reduce and foreach);
     * errors with the last pattern propagate.
     */
    private Signal bindEach(Binder binder, JqValue value, Env env, Function<Env, Signal> body, Consumer consumer, Supplier<Runnable> save) {
        Pattern[] patterns = binder.patterns;
        Runnable restore = save == null ? null : save.get();
        for (int i = 0; ; i++) {
            if (i > 0 && restore != null) {
                restore.run();
            }
            JqValue[] slots = newSlots(binder.slots);
            Signal signal;
            if (binder.keysAtMostOne) {
                One one = new One();
                signal = matchOne(patterns[i], value, slots, env, one);
                if (signal == null && one.has) {
                    signal = body.apply(new Env(env, slots));
                }
            } else {
                signal = match(patterns[i], value, slots, env, () -> body.apply(new Env(env, slots.clone())));
            }

            if (signal == null || signal.kind != Signal.SignalKind.ERROR || i == patterns.length - 1 || (consumer != null && consumer.raised(signal))) {
                return signal;
            }
        }
    }

    // Destructures value by a pattern whose keys yield at most one output; one.has (matched) is false
    // when a key yields none. Array elements from the last, object entries from the first (§7.5);
    // the keys are evaluated in the binding's outer environment, on the value being matched.
    private Signal matchOne(Pattern pattern, JqValue value, JqValue[] slots, Env env, One one) {
        one.has = true;
        switch (pattern) {
            case VariablePattern variable -> {
                if (variable.slot >= 0) {
                    slots[variable.slot] = value;
                }
                return null;
            }
            case ArrayPattern array -> {
                for (int i = array.elements.length - 1; i >= 0; i--) {
                    Result element = ValueOps.tryIndex(value, JqValue.fromNumber(i));
                    if (element instanceof Result.Err err) {
                        return Signal.error(err.error());
                    }
                    Signal signal = matchOne(array.elements[i], (JqValue) element, slots, env, one);
                    if (signal != null || !one.has) {
                        return signal;
                    }
                }

                return null;
            }
            case ObjectPattern obj -> {
                for (PatternEntry entry : obj.entries) {
                    Signal signal = evalOne(entry.key, env, value, null, one);
                    if (signal != null || !one.has) {
                        return signal;
                    }
                    Result member = ValueOps.tryIndex(value, one.value);
                    if (member instanceof Result.Err err) {
                        return Signal.error(err.error());
                    }
                    if (entry.slot >= 0) {
                        slots[entry.slot] = (JqValue) member;
                    }
                    if (entry.value != null) {
                        signal = matchOne(entry.value, (JqValue) member, slots, env, one);
                        if (signal != null || !one.has) {
                            return signal;
                        }
                    }
                }

                one.has = true;
                return null;
            }
        }
    }

    // Destructures value when a key may yield several outputs: next runs once per complete binding.
    private Signal match(Pattern pattern, JqValue value, JqValue[] slots, Env env, Supplier<Signal> next) {
        return switch (pattern) {
            case VariablePattern variable -> {
                if (variable.slot >= 0) {
                    slots[variable.slot] = value;
                }
                yield next.get();
            }
            case ArrayPattern array -> matchElements(array, array.elements.length - 1, value, slots, env, next);
            case ObjectPattern obj -> matchEntries(obj, 0, value, slots, env, next);
        };
    }

    private Signal matchElements(ArrayPattern array, int i, JqValue value, JqValue[] slots, Env env, Supplier<Signal> next) {
        if (i < 0) {
            return next.get();
        }
        Result element = ValueOps.tryIndex(value, JqValue.fromNumber(i));
        if (element instanceof Result.Err err) {
            return Signal.error(err.error());
        }
        return match(array.elements[i], (JqValue) element, slots, env, () -> matchElements(array, i - 1, value, slots, env, next));
    }

    private Signal matchEntries(ObjectPattern obj, int i, JqValue value, JqValue[] slots, Env env, Supplier<Signal> next) {
        if (i == obj.entries.length) {
            return next.get();
        }
        PatternEntry entry = obj.entries[i];
        return forEach(entry.key, env, value, null, (key, unused) -> {
            // The value of a key of an object pattern: v[k] with the indexing rules of §7.6, whatever
            // the kind of the key (§7.5; only literal keys are checked, statically).
            Result member = ValueOps.tryIndex(value, key);
            if (member instanceof Result.Err err) {
                return Signal.error(err.error());
            }
            if (entry.slot >= 0) {
                slots[entry.slot] = (JqValue) member;
            }
            Supplier<Signal> rest = () -> matchEntries(obj, i + 1, value, slots, env, next);
            return entry.value == null ? rest.get() : match(entry.value, (JqValue) member, slots, env, rest);
        });
    }

    // reduce SRC as P (INIT; UPDATE) (§7.8): one reduction per initial value; the state is the last
    // output of UPDATE, or null. In path mode the state carries its path. With ?// alternatives, a
    // retried alternative starts from the state before the source value.
    private Signal evalReduce(Reduce node, Env env, JqValue input, Path path, Emit emit) {
        return forEach(node.init, env, input, path, (initial, initialAt) -> {
            State state = new State(initial, initialAt);
            One one = node.update.atMostOne ? new One() : null;
            Signal signal = eval(node.source, env, input, null, (source, unused) -> bindEach(node.binder, source, env, frame -> {
                if (one != null) {
                    Signal failed = evalOne(node.update, frame, state.value, state.at, one);
                    if (failed == null) {
                        state.set(one.has ? one.value : JqValue.NULL, one.has ? one.at : Path.of(path));
                    }
                    return failed;
                }

                Capture last = new Capture();
                Signal stop = eval(node.update, frame, state.value, state.at, last::emit);
                if (stop == null) {
                    state.set(last.has ? last.value : JqValue.NULL, last.has ? last.at : Path.of(path));
                }
                return stop;
            }, null, restart(node.binder, state)));
            return signal != null ? signal : output(emit, state.value, state.at);
        });
    }

    // foreach SRC as P (INIT; UPDATE; EXTRACT) (§7.9).
    private Signal evalForeach(Foreach node, Env env, JqValue input, Path path, Emit emit) {
        Consumer consumer = node.binder.patterns.length > 1 ? new Consumer(emit) : null;
        Emit output = consumer == null ? emit : consumer::emit;
        return forEach(node.init, env, input, path, (initial, initialAt) -> {
            State state = new State(initial, initialAt);
            return eval(node.source, env, input, null, (source, unused) -> bindEach(node.binder, source, env, frame -> {
                boolean[] updated = {false};
                Signal signal = eval(node.update, frame, state.value, state.at, (next, nextAt) -> {
                    updated[0] = true;
                    state.set(next, nextAt);
                    return eval(node.extract, frame, next, nextAt, output);
                });
                if (signal == null && !updated[0]) {
                    state.set(JqValue.NULL, Path.of(path));
                }
                return signal;
            }, consumer, restart(node.binder, state)));
        });
    }

    // The state of a reduce or foreach, with its path.
    private static final class State {
        JqValue value;
        Path at;

        State(JqValue value, Path at) {
            this.value = value;
            this.at = at;
        }

        void set(JqValue value, Path at) {
            this.value = value;
            this.at = at;
        }

        // Takes the state back to what it is now.
        Runnable saved() {
            JqValue savedValue = value;
            Path savedAt = at;
            return () -> set(savedValue, savedAt);
        }
    }

    // With ?// alternatives: what restores the state before a source value, for a retried alternative (§7.8).
    private static Supplier<Runnable> restart(Binder binder, State state) {
        return binder.patterns.length > 1 ? state::saved : null;
    }

    // A native: a closure native decides everything; a value native is called once per combination
    // of its arguments' outputs, the last argument outermost (§6.3, §9.1).
    private Signal evalNative(CallNative node, Env env, JqValue input, Path path, Emit emit) {
        Native builtin = node.nativeFunction;
        if (builtin.takesClosures()) {
            return NativeTable.closure(builtin).call(this, env, node.arguments, input, path, emit);
        }

        JqValue[] arguments = new JqValue[builtin.arity()];
        return combine(NativeTable.value(builtin), node.arguments, arguments.length - 1, arguments, env, input, Path.of(path), emit);
    }

    private Signal combine(NativeTable.ValueNative function, Node[] nodes, int i, JqValue[] arguments, Env env, JqValue input, Path at, Emit emit) {
        if (i < 0) {
            return apply(function.call(this, input, arguments), at, emit);
        }
        return forEach(nodes[i], env, input, null, (argument, unused) -> {
            arguments[i] = argument;
            return combine(function, nodes, i - 1, arguments, env, input, at, emit);
        });
    }

    private static final JqValue[] NO_ARGUMENTS = new JqValue[0];

    // The frame of a binder: every variable null until a pattern binds it.
    private static JqValue[] newSlots(int slots) {
        JqValue[] values = new JqValue[slots];
        java.util.Arrays.fill(values, JqValue.NULL);
        return values;
    }

    // A value native whose arguments yield at most one output each.
    private Signal evalNativeOne(CallNative node, Env env, JqValue input, One one) {
        Native builtin = node.nativeFunction;
        NativeTable.ValueNative function = NativeTable.value(builtin);
        JqValue[] arguments = builtin.arity() == 0 ? NO_ARGUMENTS : new JqValue[builtin.arity()];
        for (int i = arguments.length - 1; i >= 0; i--) {
            Signal signal = evalOne(node.arguments[i], env, input, null, one);
            if (signal != null || !one.has) {
                return signal;
            }
            arguments[i] = one.value;
        }

        Result result = function.call(this, input, arguments);
        if (result instanceof Result.Err err) {
            one.has = false;
            return Signal.error(err.error());
        }
        one.has = true;
        one.value = (JqValue) result;
        return null;
    }

    // A chain of indexes and slices, T[k1][k2]…: the keys from the outermost in, each on the input
    // (§6.3: the key before its target), then the innermost target, then each index outward.
    private Signal evalPostfixOne(Node node, Env env, JqValue input, Path path, One one) {
        int nodes = spineNodes.size();
        int keys = spineValues.size();
        Signal signal = null;
        boolean has = true;
        while (has && signal == null && (node.kind == NodeKind.INDEX || node.kind == NodeKind.SLICE)) {
            spineNodes.add(node);
            if (node instanceof Index index) {
                signal = evalOne(index.key, env, input, null, one);
                has = one.has;
                spineValues.add(one.value);
                node = index.target;
            } else {
                Slice slice = (Slice) node;
                JqValue from = JqValue.NULL;
                JqValue to = JqValue.NULL;
                signal = evalBound(slice.from, env, input, one);
                has = one.has;
                if (signal == null && has) {
                    from = one.value;
                    signal = evalBound(slice.to, env, input, one);
                    has = one.has;
                    to = one.value;
                }
                spineValues.add(from);
                spineValues.add(to);
                node = slice.target;
            }
        }

        if (signal == null && has) {
            signal = evalOne(node, env, input, path, one);
            has = one.has;
        }
        JqValue value = one.value;
        Path at = one.at;
        for (int n = spineNodes.size() - 1, k = spineValues.size(); n >= nodes && signal == null && has; n--) {
            if (spineNodes.get(n).kind == NodeKind.INDEX) {
                signal = indexStep(value, at, spineValues.get(--k), one);
            } else {
                k -= 2;
                signal = sliceStep(value, at, spineValues.get(k), spineValues.get(k + 1), one);
            }
            value = one.value;
            at = one.at;
        }

        truncate(spineNodes, nodes);
        truncate(spineValues, keys);
        one.has = has;
        one.value = value;
        one.at = at;
        return signal;
    }

    // Drops the scratch entries above a walk's mark.
    private static <T> void truncate(ArrayList<T> stack, int mark) {
        for (int i = stack.size() - 1; i >= mark; i--) {
            stack.remove(i);
        }
    }

    // A bound of a slice: null when absent.
    private Signal evalBound(Node bound, Env env, JqValue input, One one) {
        if (bound != null) {
            return evalOne(bound, env, input, null, one);
        }
        one.has = true;
        one.value = JqValue.NULL;
        return null;
    }

    // A left-deep chain of operators, ((a op1 b) op2 c)…: the right operands from the outermost in
    // (§6.3: the right operand first), then the innermost left one, then each operation outward.
    private Signal evalBinaryOne(Binary node, Env env, JqValue input, One one) {
        int nodes = spineNodes.size();
        int operands = spineValues.size();
        Signal signal = null;
        boolean has = true;
        Node left = node;
        while (has && signal == null && left instanceof Binary binary) {
            signal = evalOne(binary.right, env, input, null, one);
            has = one.has;
            spineNodes.add(binary);
            spineValues.add(one.value);
            left = binary.left;
        }

        JqValue value = JqValue.NULL;
        if (signal == null && has) {
            signal = evalOne(left, env, input, null, one);
            has = one.has;
            value = one.value;
        }
        for (int n = spineNodes.size() - 1; n >= nodes && signal == null && has; n--) {
            Result result = tryApply(((Binary) spineNodes.get(n)).op, value, spineValues.get(n - nodes + operands));
            if (result instanceof Result.Err err) {
                signal = Signal.error(err.error());
            } else {
                value = (JqValue) result;
            }
        }

        truncate(spineNodes, nodes);
        truncate(spineValues, operands);
        one.has = has;
        one.value = value;
        return signal;
    }

    // A left-deep chain of and/or: the innermost left operand first, then each operator outward,
    // evaluating its right operand only when the left value does not decide.
    private Signal evalLogicOne(Node node, Env env, JqValue input, One one) {
        int nodes = spineNodes.size();
        while (node.kind == NodeKind.AND || node.kind == NodeKind.OR) {
            spineNodes.add(node);
            node = node instanceof And conjunction ? conjunction.left : ((Or) node).left;
        }

        Signal signal = evalOne(node, env, input, null, one);
        boolean has = one.has;
        JqValue value = one.value;
        for (int n = spineNodes.size() - 1; n >= nodes && signal == null && has; n--) {
            Node operator = spineNodes.get(n);
            boolean isAnd = operator.kind == NodeKind.AND;
            if (value.isTruthy() == isAnd) {
                Node right = isAnd ? ((And) operator).right : ((Or) operator).right;
                signal = evalOne(right, env, input, null, one);
                has = one.has;
                value = one.value;
            }

            value = JqValue.fromBool(value.isTruthy());
        }

        truncate(spineNodes, nodes);
        one.has = has;
        one.value = value;
        return signal;
    }

    // -…-x: the operand, then each minus.
    private Signal evalNegateOne(Negate node, Env env, JqValue input, One one) {
        int count = 0;
        Node operand = node;
        for (; operand instanceof Negate negate; operand = negate.operand) {
            count++;
        }
        Signal signal = evalOne(operand, env, input, null, one);
        for (; signal == null && one.has && count > 0; count--) {
            Result result = ValueOps.tryNegate(one.value);
            if (result instanceof Result.Err err) {
                signal = Signal.error(err.error());
            } else {
                one.value = (JqValue) result;
            }
        }

        return signal;
    }

    private Signal evalArrayOne(ArrayExpr node, Env env, JqValue input, One one) {
        if (node.body == null) {
            one.has = true;
            one.value = JqArray.EMPTY;
            return null;
        }
        if (node.body.atMostOne) {
            Signal signal = evalOne(node.body, env, input, null, one);
            one.value = signal == null && one.has ? JqArray.of(one.value) : JqArray.EMPTY;
            one.has = signal == null;
            return signal;
        }

        return collect(node.body, env, input, one);
    }

    // [f] for an f with several outputs.
    private Signal collect(Node body, Env env, JqValue input, One one) {
        ArrayList<JqValue> items = new ArrayList<>();
        Signal stop = eval(body, env, input, null, (item, unused) -> {
            items.add(item);
            return null;
        });
        one.has = stop == null;
        one.value = JqArray.fromOwned(items.toArray(NO_ARGUMENTS));
        return stop;
    }

    private Signal evalObjectOne(ObjectExpr node, Env env, JqValue input, One one) {
        JqObject obj = JqObject.EMPTY;
        for (ObjectEntry entry : node.entries) {
            Signal signal = evalEntryOne(entry, env, input, one);
            if (signal != null || !one.has) {
                return signal;
            }
            obj = obj.set(one.key, one.value);
        }

        one.has = true;
        one.value = obj;
        return null;
    }

    private Signal evalTemplateOne(Template node, Env env, JqValue input, One one) {
        String[] texts = new String[node.parts.length];
        for (int i = texts.length - 1; i >= 0; i--) {
            Signal signal = evalOne(node.parts[i], env, input, null, one);
            if (signal != null || !one.has) {
                return signal;
            }
            Result text = convert(node.format, one.value);
            if (text instanceof Result.Err err) {
                return Signal.error(err.error());
            }
            texts[i] = ((JqValue) text).asString();
        }

        one.has = true;
        one.value = JqValue.fromString(join(node, texts));
        return null;
    }
}