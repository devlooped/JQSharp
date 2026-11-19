package com.devlooped.jqlite.binding;

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
import com.devlooped.jqlite.binding.Bound.EnvVariable;
import com.devlooped.jqlite.binding.Bound.Foreach;
import com.devlooped.jqlite.binding.Bound.Format;
import com.devlooped.jqlite.binding.Bound.Function;
import com.devlooped.jqlite.binding.Bound.HostVariable;
import com.devlooped.jqlite.binding.Bound.Identity;
import com.devlooped.jqlite.binding.Bound.If;
import com.devlooped.jqlite.binding.Bound.Index;
import com.devlooped.jqlite.binding.Bound.Iterate;
import com.devlooped.jqlite.binding.Bound.Label;
import com.devlooped.jqlite.binding.Bound.Literal;
import com.devlooped.jqlite.binding.Bound.Negate;
import com.devlooped.jqlite.binding.Bound.Node;
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
import com.devlooped.jqlite.binding.Modules.IModuleSource;
import com.devlooped.jqlite.binding.Modules.ModuleLoader;
import com.devlooped.jqlite.builtins.Native;
import com.devlooped.jqlite.builtins.Natives;
import com.devlooped.jqlite.syntax.Ast;
import com.devlooped.jqlite.syntax.Parser;
import com.devlooped.jqlite.syntax.SyntaxError;
import com.devlooped.jqlite.values.JqObjectBuilder;
import com.devlooped.jqlite.values.JqValue;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;

/**
 * Binds a parsed program (spec §7): walks the AST once and resolves every variable, closure
 * parameter, function call and label to its definition, producing the {@link BoundProgram}
 * described at the top of Bound.java. Unknown names are binding errors with a position; nothing throws.
 *
 * <p>The scope is a stack of entries searched from the top, so the innermost definition wins. A
 * call resolves to the innermost user definition or closure parameter of that name and arity,
 * then to the prelude, then to the natives (§7.7). Entries record the frame depth at which they
 * live, so the distance to a definition is the current depth minus the entry's.
 *
 * <p>The walk does not recurse: the AST is up to 1,000 levels deep through nesting and 10,000 or more
 * along sequences, and between them an operand chain of each precedence level can add a level,
 * which no host stack budget covers in every language. So the walk is a loop over a stack of tasks:
 * {@link #visit} resolves what it can at once and pushes the node's children, the scope changes
 * between them and the final {@link #build}; bound children wait on a stack of results. Only
 * patterns are walked recursively, one small frame per level of pattern nesting, which the parser
 * limits.
 *
 * <p>The module part of the reference (a partial class in Modules.cs: bindImports, bindModule) is at
 * the end of this file.
 */
public final class Resolver {
    private enum EntryKind {
        VARIABLE,
        PARAMETER,
        LABEL,
        FUNCTION,
        CONSTANT,
    }

    // VARIABLE, PARAMETER, LABEL: depth is the frame holding it, slot its slot there.
    // CONSTANT: a data module's value, constants[slot].
    // FUNCTION: depth is the number of frames where it is defined, slot its index in functions,
    // topLevel whether it is a top-level definition of the program or module.
    private record Entry(EntryKind kind, String name, int arity, int depth, int slot, boolean topLevel) {
        Entry(EntryKind kind, String name, int arity, int depth, int slot) {
            this(kind, name, arity, depth, slot, false);
        }
    }

    private enum Step {
        // Resolve node, or schedule its children and its build.
        VISIT,
        // Pop the bound children of node and push the bound node.
        BUILD,
        // Pop the arguments of the call node and push the call of functions[a], defined b frames out.
        BUILD_CALL,
        // Pop the arguments of the call node and push the call of Natives.ALL[a].
        BUILD_NATIVE,
        // Pop the bound pattern keys of the binding node and push its binder.
        MAKE_BINDER,
        // Push the frame of the binder on top of binders.
        ENTER_BINDER,
        // Define definition at this point of the scope (a = 1 at the top level of the program).
        DEFINE,
        // Pop the body of definition, which is functions[a].
        END_FUNCTION,
        // Restore the scope to a entries and the depth to b.
        LEAVE,
    }

    private record Task(Step step, Ast.Node node, Ast.Definition definition, int a, int b) {
        Task(Step step, Ast.Node node) {
            this(step, node, null, 0, 0);
        }
    }

    private record BinderNames(Binder binder, List<String> names) {
    }

    // A function whose body is being bound. single: whether its calls of itself in that body are
    // bound as single (spec §7.7): assumed at first, and checked when the body is bound, unless the
    // answer is known already. selfCalls counts the calls bound as single on that assumption.
    private static final class Defining {
        final int index;
        final int[] valueParameters;
        boolean single;
        boolean known;
        int selfCalls;

        Defining(int index, int[] valueParameters, boolean single, boolean known) {
            this.index = index;
            this.valueParameters = valueParameters;
            this.single = single;
            this.known = known;
        }
    }

    /** The outcome of {@link #tryBind} and {@link #tryCompile}: the bound program, or the error. */
    public record Compiled(BoundProgram program, SyntaxError error) {
    }

    /** The outcome of {@link #tryBindPrelude}: all its functions and the indexes of its top-level definitions, or the error. */
    record PreludeBinding(Function[] functions, int[] exports, SyntaxError error) {
    }

    private static final Node IDENTITY = new Identity();

    private final Prelude prelude;
    // Whether builtins with a native override bind to it (Native.overridesPrelude).
    private final boolean nativeOverrides;
    private final List<Entry> scope = new ArrayList<>();
    // Shared by the main program and its modules: one function table, one list of host variables.
    private final List<Function> functions;
    private final List<Integer> exports = new ArrayList<>();
    private final List<String> hostVariables;
    private final List<JqValue> constants;
    private final List<Task> tasks = new ArrayList<>();
    private final List<Node> results = new ArrayList<>();
    // The binders made and not yet built into their node, with the names of their slots.
    private final List<BinderNames> binders = new ArrayList<>();
    // The functions being defined, innermost last.
    private final List<Defining> defining = new ArrayList<>();
    // While the body of a function is bound a second time (endFunction): whether each function
    // nested in it is single, as the first time found, by index from replayStart.
    private List<Boolean> replay;
    private int replayStart;
    private int depth;
    private SyntaxError error;

    // The modules of this binding (shared by the main program and every module), and the module
    // this resolver binds (null for the main program and the prelude).
    private final ModuleLoader loader;
    private final ModuleLoader.Module module;

    private Resolver(Prelude prelude, boolean nativeOverrides, ModuleLoader loader) {
        this.prelude = prelude;
        this.nativeOverrides = nativeOverrides;
        this.loader = loader;
        this.module = null;
        this.hostVariables = new ArrayList<>();
        this.constants = new ArrayList<>();
        this.functions = new ArrayList<>();
        if (prelude != null) {
            Collections.addAll(this.functions, prelude.functions());
        }
    }

    // A resolver for a module: a scope of its own, the functions, host variables and constants of
    // the whole program.
    private Resolver(Resolver main, ModuleLoader.Module module) {
        this.prelude = main.prelude;
        this.nativeOverrides = main.nativeOverrides;
        this.loader = main.loader;
        this.module = module;
        this.hostVariables = main.hostVariables;
        this.constants = main.constants;
        this.functions = main.functions;
    }

    /**
     * Binds a program against the prelude and the natives, loading its modules from {@code modules}
     * (§9.11; null: no module exists). Without {@code nativeOverrides} every prelude builtin binds to
     * its definition, even where a native overrides it.
     */
    public static Compiled tryBind(Ast.Program program, boolean nativeOverrides, IModuleSource modules) {
        Prelude prelude = nativeOverrides ? Prelude.instance() : Prelude.withoutOverrides();
        Resolver resolver = new Resolver(prelude, nativeOverrides, new ModuleLoader(modules));
        Node body = resolver.bindProgram(program);
        if (body == null) {
            return new Compiled(null, resolver.error);
        }
        return new Compiled(new BoundProgram(resolver.functions(), resolver.hostVariables.toArray(new String[0]), body, resolver.loader.table()), null);
    }

    /** Binds a program with the native overrides and without modules. */
    public static Compiled tryBind(Ast.Program program) {
        return tryBind(program, true, null);
    }

    /** Parses and binds a program. */
    public static Compiled tryCompile(String source, boolean nativeOverrides, IModuleSource modules) {
        Parser.Parsed parsed = Parser.tryParse(source);
        if (parsed.program() == null) {
            return new Compiled(null, parsed.error());
        }
        return tryBind(parsed.program(), nativeOverrides, modules);
    }

    /** Parses and binds a program with the native overrides and without modules. */
    public static Compiled tryCompile(String source) {
        return tryCompile(source, true, null);
    }

    /** Binds the prelude module against the natives only: all its functions, and its top-level definitions in order. */
    static PreludeBinding tryBindPrelude(Ast.Program module, boolean nativeOverrides) {
        Resolver resolver = new Resolver(null, nativeOverrides, null);
        if (resolver.bindProgram(module) == null) {
            return new PreludeBinding(null, null, resolver.error);
        }

        int[] tops = new int[resolver.exports.size()];
        for (int i = 0; i < tops.length; i++) {
            tops[i] = resolver.exports.get(i);
        }
        return new PreludeBinding(resolver.functions(), tops, null);
    }

    private Function[] functions() {
        return functions.toArray(new Function[0]);
    }

    // The body of a program (Identity for a module, whose result is its definitions), or null.
    private Node bindProgram(Ast.Program program) {
        if (!bindImports(program.imports())) {
            return null;
        }

        // Pushed last first: the definitions in order, then the body.
        if (program.body() == null) {
            results.add(IDENTITY);
        } else {
            tasks.add(new Task(Step.VISIT, program.body()));
        }
        for (int i = program.definitions().size() - 1; i >= 0; i--) {
            tasks.add(new Task(Step.DEFINE, null, program.definitions().get(i), 1, 0));
        }

        while (!tasks.isEmpty() && error == null) {
            Task task = tasks.remove(tasks.size() - 1);
            run(task);
        }

        return error == null ? results.get(0) : null;
    }

    private void run(Task task) {
        switch (task.step) {
            case VISIT -> visit(task.node);
            case BUILD -> results.add(build(task.node, task.a));
            case BUILD_CALL -> results.add(call(task.a, task.b, popAll(((Ast.Call) task.node).arguments().size())));
            case BUILD_NATIVE -> results.add(new CallNative(Natives.ALL.get(task.a), popAll(((Ast.Call) task.node).arguments().size())));
            case MAKE_BINDER -> makeBinder(task.node);
            case ENTER_BINDER -> enterBinder();
            case DEFINE -> define(task.definition, task.a == 1);
            case END_FUNCTION -> endFunction(task.definition, task.a);
            case LEAVE -> {
                scope.subList(task.a, scope.size()).clear();
                depth = task.b;
            }
        }
    }

    // Tasks run last pushed first, so each case pushes its steps in reverse order.
    private void visit(Ast.Node node) {
        switch (node) {
            case Ast.Identity identity -> results.add(IDENTITY);
            case Ast.Literal literal -> results.add(new Literal(literal.value()));
            case Ast.Format format -> results.add(new Format(format.name()));
            case Ast.Variable variable -> add(bindVariable(variable));
            case Ast.Break brk -> add(bindBreak(brk));
            case Ast.Call call -> visitCall(call);
            case Ast.Template template -> {
                push(Step.BUILD, node);
                pushAll(template.parts());
            }
            case Ast.Pipe pipe -> push(Step.BUILD, node, pipe.right(), pipe.left(), null);
            case Ast.Comma comma -> visitComma(comma);
            case Ast.Binary binary -> push(Step.BUILD, node, binary.right(), binary.left(), null);
            case Ast.And and -> push(Step.BUILD, node, and.right(), and.left(), null);
            case Ast.Or or -> push(Step.BUILD, node, or.right(), or.left(), null);
            case Ast.Alternative alternative -> push(Step.BUILD, node, alternative.right(), alternative.left(), null);
            case Ast.Negate negate -> push(Step.BUILD, node, negate.operand(), null, null);
            case Ast.Index index -> push(Step.BUILD, node, index.key(), index.target(), null);
            case Ast.Slice slice -> push(Step.BUILD, node, slice.to(), slice.from(), slice.target());
            case Ast.Iterate iterate -> push(Step.BUILD, node, iterate.target(), null, null);
            case Ast.Try attempt -> push(Step.BUILD, node, attempt.handler(), attempt.body(), null);
            case Ast.If branch -> push(Step.BUILD, node, branch.otherwise(), branch.then(), branch.condition());
            case Ast.ArrayExpr array -> push(Step.BUILD, node, array.body(), null, null);
            case Ast.ObjectExpr obj -> {
                push(Step.BUILD, node);
                for (int i = obj.entries().size() - 1; i >= 0; i--) {
                    push(obj.entries().get(i).value(), obj.entries().get(i).key(), null);
                }
            }
            case Ast.Bind bind -> {
                // The source and the pattern keys in the current scope, the body with the variables' frame.
                leave();
                push(Step.BUILD, node, bind.body(), null, null);
                tasks.add(new Task(Step.ENTER_BINDER, null));
                pushBinder(node, bind.patterns());
                push(bind.source(), null, null);
            }
            case Ast.Reduce reduce -> {
                leave();
                push(Step.BUILD, node, reduce.update(), null, null);
                tasks.add(new Task(Step.ENTER_BINDER, null));
                push(reduce.init(), null, null);
                pushBinder(node, reduce.patterns());
                push(reduce.source(), null, null);
            }
            case Ast.Foreach each -> {
                leave();
                push(Step.BUILD, node, each.extract(), each.update(), null);
                tasks.add(new Task(Step.ENTER_BINDER, null));
                push(each.init(), null, null);
                pushBinder(node, each.patterns());
                push(each.source(), null, null);
            }
            case Ast.Label label -> {
                leave();
                push(Step.BUILD, node, label.body(), null, null);
                depth++;
                scope.add(new Entry(EntryKind.LABEL, label.name(), 0, depth, 0));
            }
            case Ast.FuncDef definition -> {
                // The definition is in functions; the node's result is the rest's.
                leave();
                push(definition.rest(), null, null);
                tasks.add(new Task(Step.DEFINE, null, definition.definition(), 0, 0));
            }
        }
    }

    // A left-nested chain of commas becomes one node with the items in order.
    private void visitComma(Ast.Comma comma) {
        List<Ast.Node> items = new ArrayList<>();
        while (true) {
            items.add(comma.right());
            if (!(comma.left() instanceof Ast.Comma left)) {
                break;
            }
            comma = left;
        }

        items.add(comma.left());
        Collections.reverse(items);
        tasks.add(new Task(Step.BUILD, comma, null, items.size(), 0));
        pushAll(items);
    }

    // Pops the bound children of node, last first, and builds its bound node; count is the number of items of a comma.
    private Node build(Ast.Node node, int count) {
        switch (node) {
            case Ast.Template template:
                return new Template(template.format(), template.texts().toArray(new String[0]), popAll(template.parts().size()));
            case Ast.Pipe pipe: {
                Node right = pop();
                return new Pipe(pop(), right);
            }
            case Ast.Comma comma:
                return new Comma(popAll(count));
            case Ast.Binary binary: {
                Node right = pop();
                return new Binary(binary.op(), pop(), right);
            }
            case Ast.And and: {
                Node right = pop();
                return new And(pop(), right);
            }
            case Ast.Or or: {
                Node right = pop();
                return new Or(pop(), right);
            }
            case Ast.Alternative alternative: {
                Node right = pop();
                return new Alternative(pop(), right);
            }
            case Ast.Negate negate:
                return new Negate(pop());
            case Ast.Index index: {
                Node key = pop();
                return new Index(pop(), key);
            }
            case Ast.Slice slice: {
                Node to = slice.to() == null ? null : pop();
                Node from = slice.from() == null ? null : pop();
                return new Slice(pop(), from, to);
            }
            case Ast.Iterate iterate:
                return new Iterate(pop());
            case Ast.Try attempt: {
                Node handler = attempt.handler() == null ? null : pop();
                return new Try(pop(), handler);
            }
            case Ast.If branch: {
                Node otherwise = pop();
                Node then = pop();
                return new If(pop(), then, otherwise);
            }
            case Ast.ArrayExpr array:
                return new ArrayExpr(array.body() == null ? null : pop());
            case Ast.ObjectExpr obj: {
                ObjectEntry[] entries = new ObjectEntry[obj.entries().size()];
                for (int i = entries.length - 1; i >= 0; i--) {
                    Node value = obj.entries().get(i).value() == null ? null : pop();
                    entries[i] = new ObjectEntry(pop(), value);
                }

                return new ObjectExpr(entries);
            }
            case Ast.Bind bind: {
                Node body = pop();
                return new Bind(pop(), popBinder(), body);
            }
            case Ast.Reduce reduce: {
                Node update = pop();
                Node init = pop();
                return new Reduce(pop(), popBinder(), init, update);
            }
            case Ast.Foreach each: {
                Node extract = pop();
                Node update = pop();
                Node init = pop();
                return new Foreach(pop(), popBinder(), init, update, extract);
            }
            default:
                return new Label(pop());
        }
    }

    // Defines a function at the current point of the scope, visible in its own body and after it.
    private void define(Ast.Definition definition, boolean topLevel) {
        int index = functions.size();
        functions.add(null);
        if (topLevel) {
            exports.add(index);
        }
        scope.add(new Entry(EntryKind.FUNCTION, definition.name(), definition.params().size(), depth, index, topLevel));

        // Single until its body says otherwise, or what the first binding of this body found.
        boolean replayed = replay != null && index - replayStart < replay.size();
        defining.add(new Defining(index, valueParameters(definition), !replayed || replay.get(index - replayStart), replayed));

        leave();
        tasks.add(new Task(Step.END_FUNCTION, null, definition, index, 0));
        push(definition.body(), null, null);
        if (definition.params().isEmpty()) {
            return;
        }
        depth++;
        for (int slot = 0; slot < definition.params().size(); slot++) {
            scope.add(new Entry(EntryKind.PARAMETER, definition.params().get(slot).name(), 0, depth, slot));
        }
        // Each $name parameter then has a frame of its own, first parameter outermost (bindParameters).
        for (Ast.Param parameter : definition.params()) {
            if (parameter.isVariable()) {
                depth++;
                scope.add(new Entry(EntryKind.VARIABLE, parameter.name(), 0, depth, 0));
            }
        }
    }

    // The body of functions[index] is bound: the function is single when its body is (spec §7.7).
    private void endFunction(Ast.Definition definition, int index) {
        Node body = pop();
        Defining own = defining.get(defining.size() - 1);
        if (own.single && !body.atMostOne && !own.known) {
            // It was assumed single and is not. The calls of itself bound as single are wrong, and so
            // is every node built on them: bind the body again, this time knowing. The functions
            // nested in it do not depend on the assumption (their calls of this one are never
            // single, call), so they keep what this binding found and are not bound a third time.
            own.single = false;
            own.known = true;
            if (own.selfCalls > 0) {
                replay = new ArrayList<>();
                replayStart = index + 1;
                for (int i = replayStart; i < functions.size(); i++) {
                    replay.add(functions.get(i).single());
                }
                functions.subList(replayStart, functions.size()).clear();
                tasks.add(new Task(Step.END_FUNCTION, null, definition, index, 0));
                push(definition.body(), null, null);
                return;
            }
        }

        defining.remove(defining.size() - 1);
        if (replay != null && replayStart == index + 1) {
            replay = null;
        }
        functions.set(index, new Function(definition.name(), definition.params().size(), bindParameters(definition, body), body.atMostOne, own.valueParameters));
    }

    // A call of functions[function]: single when the function is and the argument of each of its $
    // parameters is. A function still being defined is single in its own body as long as its
    // Defining says so; seen from a function nested in it, it is not.
    private CallFunction call(int function, int hops, Node[] arguments) {
        boolean single = false;
        Function defined = functions.get(function);
        if (defined != null) {
            single = defined.single() && Node.all(defined.valueParameters(), arguments);
        } else if (!defining.isEmpty()) {
            Defining own = defining.get(defining.size() - 1);
            if (own.single && own.index == function && Node.all(own.valueParameters, arguments)) {
                single = true;
                if (!own.known) {
                    own.selfCalls++;
                }
            }
        }

        return new CallFunction(function, hops, arguments, single);
    }

    // The slots of the $ parameters.
    private static int[] valueParameters(Ast.Definition definition) {
        int count = 0;
        for (Ast.Param parameter : definition.params()) {
            count += parameter.isVariable() ? 1 : 0;
        }
        int[] slots = new int[count];
        int next = 0;
        for (int slot = 0; slot < definition.params().size(); slot++) {
            if (definition.params().get(slot).isVariable()) {
                slots[next++] = slot;
            }
        }

        return slots;
    }

    private static final Binder PARAMETER_BINDER = new Binder(new Pattern[] {new VariablePattern(0)}, 1);

    // def f($a; $b): B is def f(a; b): a as $a | b as $b | B, except that each binding names its
    // parameter by position: the k-th $ parameter's frame is k frames inside the parameters' frame.
    private static Node bindParameters(Ast.Definition definition, Node body) {
        int variables = 0;
        for (Ast.Param parameter : definition.params()) {
            variables += parameter.isVariable() ? 1 : 0;
        }
        for (int slot = definition.params().size() - 1; slot >= 0; slot--) {
            if (definition.params().get(slot).isVariable()) {
                body = new Bind(new CallParameter(--variables, slot), PARAMETER_BINDER, body);
            }
        }

        return body;
    }

    private Node bindVariable(Ast.Variable variable) {
        // The parser lets nothing bind $__loc__ (§7.5).
        if (variable.name().equals(Parser.LOCATION_VARIABLE)) {
            return new Literal(location(variable.position()));
        }

        for (int i = scope.size() - 1; i >= 0; i--) {
            Entry entry = scope.get(i);
            if (entry.kind == EntryKind.VARIABLE && entry.name.equals(variable.name())) {
                return new Variable(depth - entry.depth, entry.slot);
            }
            if (entry.kind == EntryKind.CONSTANT && entry.name.equals(variable.name())) {
                return new Literal(constants.get(entry.slot));
            }
        }

        if (variable.name().equals("ENV")) {
            return new EnvVariable();
        }
        // A qualified name can only come from a data import (bindImports); the prelude has no host variables.
        if (variable.name().contains("::") || prelude == null) {
            return fail(BindingMessages.variableNotDefined(variable.name()), variable.position());
        }

        int index = hostVariables.indexOf(variable.name());
        if (index < 0) {
            index = hostVariables.size();
            hostVariables.add(variable.name());
        }

        return new HostVariable(variable.name(), index);
    }

    // $__loc__: {"file": the module's identity or "<top-level>", "line": the line of its '$'}.
    private JqValue location(Ast.Position position) {
        JqObjectBuilder builder = new JqObjectBuilder(2);
        builder.set("file", JqValue.fromString(module != null ? module.identity : "<top-level>"));
        builder.set("line", JqValue.fromNumber(position.line()));
        return builder.toObject();
    }

    private void visitCall(Ast.Call call) {
        int arity = call.arguments().size();
        for (int i = scope.size() - 1; i >= 0; i--) {
            Entry entry = scope.get(i);
            if (!entry.name.equals(call.name()) || entry.arity != arity) {
                continue;
            }
            if (entry.kind == EntryKind.PARAMETER) {
                results.add(new CallParameter(depth - entry.depth, entry.slot));
                return;
            }

            if (entry.kind == EntryKind.FUNCTION) {
                // Inside the prelude, its own builtins with a native override bind to the native too.
                Native builtin = prelude == null && entry.topLevel ? override(call.name(), arity) : null;
                if (builtin != null) {
                    tasks.add(new Task(Step.BUILD_NATIVE, call, null, builtin.index(), 0));
                } else {
                    tasks.add(new Task(Step.BUILD_CALL, call, null, entry.slot, depth - entry.depth));
                }
                pushAll(call.arguments());
                return;
            }
        }

        // The prelude is defined in the outermost, empty environment: every frame of the caller is left.
        int function = prelude == null ? -1 : prelude.find(call.name(), arity);
        if (function >= 0) {
            Native builtin = override(call.name(), arity);
            if (builtin != null) {
                tasks.add(new Task(Step.BUILD_NATIVE, call, null, builtin.index(), 0));
            } else {
                tasks.add(new Task(Step.BUILD_CALL, call, null, function, depth));
            }
        } else {
            Native builtin = Natives.find(call.name(), arity);
            if (builtin != null) {
                tasks.add(new Task(Step.BUILD_NATIVE, call, null, builtin.index(), 0));
            } else {
                fail(BindingMessages.notDefined(call.name(), arity), call.position());
            }
        }
        pushAll(call.arguments());
    }

    // The native override of a prelude builtin, when overrides are on.
    private Native override(String name, int arity) {
        return nativeOverrides ? Natives.override(name, arity) : null;
    }

    private Node bindBreak(Ast.Break node) {
        for (int i = scope.size() - 1; i >= 0; i--) {
            Entry entry = scope.get(i);
            if (entry.kind == EntryKind.LABEL && entry.name.equals(node.name())) {
                return new Break(depth - entry.depth);
            }
        }

        return fail(BindingMessages.labelNotDefined(node.name()), node.position());
    }

    // Schedules the binder of node: its pattern keys, in destructuring order, then makeBinder.
    private void pushBinder(Ast.Node node, List<Ast.Pattern> patterns) {
        tasks.add(new Task(Step.MAKE_BINDER, node));
        List<Ast.Node> keys = new ArrayList<>();
        for (Ast.Pattern pattern : patterns) {
            collectKeys(pattern, keys);
        }
        pushAll(keys);
    }

    private static void collectKeys(Ast.Pattern pattern, List<Ast.Node> keys) {
        switch (pattern) {
            case Ast.ArrayPattern array -> {
                for (int i = array.elements().size() - 1; i >= 0; i--) {
                    collectKeys(array.elements().get(i), keys);
                }
            }
            case Ast.ObjectPattern obj -> {
                for (Ast.PatternEntry entry : obj.entries()) {
                    keys.add(entry.key());
                    if (entry.value() != null) {
                        collectKeys(entry.value(), keys);
                    }
                }
            }
            case Ast.VariablePattern variable -> {
                // No key.
            }
        }
    }

    // Builds the ?// alternatives of a binding from its bound keys (the last results, in the order
    // of collectKeys). Every distinct variable name of all the alternatives gets a slot. A name
    // repeated in one pattern (§7.5): without ?// its first occurrence in matching order gives the
    // value, so later ones discard theirs; with ?// every occurrence stores and the last one stays.
    private void makeBinder(Ast.Node node) {
        List<Ast.Pattern> patterns = switch (node) {
            case Ast.Bind bind -> bind.patterns();
            case Ast.Reduce reduce -> reduce.patterns();
            default -> ((Ast.Foreach) node).patterns();
        };

        int keys = 0;
        for (Ast.Pattern pattern : patterns) {
            keys += countKeys(pattern);
        }
        int[] next = {results.size() - keys};
        List<String> names = new ArrayList<>();
        HashSet<String> seen = patterns.size() == 1 ? new HashSet<>() : null;
        Pattern[] bound = new Pattern[patterns.size()];
        for (int i = 0; i < bound.length; i++) {
            bound[i] = bindPattern(patterns.get(i), names, seen, next);
        }

        results.subList(results.size() - keys, results.size()).clear();
        binders.add(new BinderNames(new Binder(bound, names.size()), names));
    }

    private static int countKeys(Ast.Pattern pattern) {
        List<Ast.Node> keys = new ArrayList<>();
        collectKeys(pattern, keys);
        return keys.size();
    }

    // Visits elements and entries in the order of collectKeys, which is matching order (spec §7.5).
    private Pattern bindPattern(Ast.Pattern pattern, List<String> names, HashSet<String> seen, int[] next) {
        switch (pattern) {
            case Ast.VariablePattern variable:
                return new VariablePattern(slot(variable.name(), names, seen));
            case Ast.ArrayPattern array: {
                Pattern[] elements = new Pattern[array.elements().size()];
                for (int i = elements.length - 1; i >= 0; i--) {
                    elements[i] = bindPattern(array.elements().get(i), names, seen, next);
                }
                return new ArrayPattern(elements);
            }
            case Ast.ObjectPattern obj: {
                List<Ast.PatternEntry> entries = obj.entries();
                PatternEntry[] bound = new PatternEntry[entries.size()];
                for (int i = 0; i < bound.length; i++) {
                    Ast.PatternEntry entry = entries.get(i);
                    Node key = results.get(next[0]++);
                    int slot = entry.variable() == null ? -1 : slot(entry.variable(), names, seen);
                    Pattern value = entry.value() == null ? null : bindPattern(entry.value(), names, seen, next);
                    bound[i] = new PatternEntry(key, slot, value);
                }

                return new ObjectPattern(bound);
            }
        }
    }

    // The slot of a variable in names, or -1 when seen (first occurrence wins) already has the name.
    private static int slot(String name, List<String> names, HashSet<String> seen) {
        if (seen != null && !seen.add(name)) {
            return -1;
        }
        int slot = names.indexOf(name);
        if (slot < 0) {
            slot = names.size();
            names.add(name);
        }

        return slot;
    }

    // Pushes the frame of the last binder made: one variable per name, in slot order.
    private void enterBinder() {
        List<String> names = binders.get(binders.size() - 1).names;
        depth++;
        for (int slot = 0; slot < names.size(); slot++) {
            scope.add(new Entry(EntryKind.VARIABLE, names.get(slot), 0, depth, slot));
        }
    }

    private Binder popBinder() {
        return binders.remove(binders.size() - 1).binder;
    }

    // Schedules the restoring of the scope as it is now, after the tasks pushed later.
    private void leave() {
        tasks.add(new Task(Step.LEAVE, null, null, scope.size(), depth));
    }

    private void push(Step step, Ast.Node node) {
        tasks.add(new Task(step, node));
    }

    private void push(Step step, Ast.Node node, Ast.Node last, Ast.Node middle, Ast.Node first) {
        tasks.add(new Task(step, node));
        push(last, middle, first);
    }

    // Schedules the visit of the given nodes, missing ones skipped, the last argument first.
    private void push(Ast.Node last, Ast.Node middle, Ast.Node first) {
        if (last != null) {
            tasks.add(new Task(Step.VISIT, last));
        }
        if (middle != null) {
            tasks.add(new Task(Step.VISIT, middle));
        }
        if (first != null) {
            tasks.add(new Task(Step.VISIT, first));
        }
    }

    private void pushAll(List<Ast.Node> nodes) {
        for (int i = nodes.size() - 1; i >= 0; i--) {
            tasks.add(new Task(Step.VISIT, nodes.get(i)));
        }
    }

    private void add(Node node) {
        if (node != null) {
            results.add(node);
        }
    }

    private Node pop() {
        return results.remove(results.size() - 1);
    }

    private Node[] popAll(int count) {
        Node[] nodes = new Node[count];
        int start = results.size() - count;
        for (int i = 0; i < count; i++) {
            nodes[i] = results.get(start + i);
        }
        results.subList(start, results.size()).clear();
        return nodes;
    }

    private Node fail(String message, Ast.Position position) {
        if (error == null) {
            error = new SyntaxError(message, position.line(), position.column(), module != null ? module.identity : null);
        }
        return null;
    }

    // ---- The modules group (Modules.cs in the reference) ----

    /**
     * Where modules enter a scope (§9.11), before the top-level definitions: the main program loads
     * and binds every module it needs first. Each directive then adds its module's exports as
     * definitions at that point: {@code alias::f} for {@code import}, {@code f} for {@code include},
     * so that later definitions and directives shadow them as usual; a data import adds the
     * constants {@code $alias} and {@code $alias::alias}.
     */
    private boolean bindImports(List<Ast.Import> imports) {
        if (imports.isEmpty()) {
            return true;
        }

        List<String> identities;
        if (module != null) {
            identities = module.imports;
        } else {
            ModuleLoader.Loaded loaded = loader.tryLoad(imports);
            if (loaded.error() != null) {
                if (error == null) {
                    error = loaded.error();
                }
                return false;
            }

            identities = loaded.identities();
            for (ModuleLoader.Module code : loaded.order()) {
                if (!bindModule(code)) {
                    return false;
                }
            }
        }

        for (int i = 0; i < imports.size(); i++) {
            Ast.Import directive = imports.get(i);
            ModuleLoader.Module loaded = loader.get(identities.get(i));
            if (directive.isData()) {
                constants.add(loaded.data);
                scope.add(new Entry(EntryKind.CONSTANT, directive.alias(), 0, 0, constants.size() - 1));
                scope.add(new Entry(EntryKind.CONSTANT, directive.alias() + "::" + directive.alias(), 0, 0, constants.size() - 1));
                continue;
            }

            String prefix = directive.alias() == null ? "" : directive.alias() + "::";
            for (ModuleLoader.Export export : loaded.exports.values()) {
                scope.add(new Entry(EntryKind.FUNCTION, prefix + export.name(), export.arity(), 0, export.function()));
            }
        }

        return true;
    }

    // Binds a code module in a scope of its own and records its exports.
    private boolean bindModule(ModuleLoader.Module code) {
        Resolver resolver = new Resolver(this, code);
        if (resolver.bindProgram(code.program) == null) {
            if (error == null) {
                error = resolver.error;
            }
            return false;
        }

        for (int index : resolver.exports) {
            Function function = functions.get(index);
            code.exports.put(function.name() + "/" + function.arity(), new ModuleLoader.Export(function.name(), function.arity(), index));
        }

        return true;
    }
}
