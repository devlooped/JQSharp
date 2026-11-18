using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Devlooped.Core.Builtins;
using Devlooped.Core.Syntax;
using S = Devlooped.Core.Syntax;

namespace Devlooped.Core.Binding;

/// <summary>
/// Binds a parsed program (spec §7): walks the AST once and resolves every variable, closure
/// parameter, function call and label to its definition, producing the <see cref="BoundProgram"/>
/// described at the top of Bound.cs. Unknown names are binding errors with a position; nothing throws.
/// </summary>
/// <remarks>
/// <para>
/// The scope is a stack of entries searched from the top, so the innermost definition wins. A
/// call resolves to the innermost user definition or closure parameter of that name and arity,
/// then to the prelude, then to the natives (§7.7). Entries record the frame depth at which they
/// live, so the distance to a definition is the current depth minus the entry's.
/// </para>
/// <para>
/// The walk does not recurse: the AST is up to 1,000 levels deep through nesting and 10,000 or more
/// along sequences (the parser folds <c>|</c>, <c>,</c>, operators, suffixes, <c>as</c>,
/// <c>def</c> and <c>label</c> chains iteratively), and between them an operand chain of each
/// precedence level can add a level, which no host stack budget covers in every language. So the
/// walk is a loop over a stack of tasks: <see cref="Visit"/> resolves what it can at once and
/// pushes the node's children, the scope changes between them and the final <see cref="Build"/>;
/// bound children wait on a stack of results. Only patterns are walked recursively, one small frame
/// per level of pattern nesting, which the parser limits.
/// </para>
/// </remarks>
sealed class Resolver
{
    enum EntryKind
    {
        Variable,
        Parameter,
        Label,
        Function,
    }

    // Variable, Parameter, Label: Depth is the frame holding it, Slot its slot there.
    // Function: Depth is the number of frames where it is defined, Slot its index in functions,
    // TopLevel whether it is a top-level definition of the program or module.
    readonly record struct Entry(EntryKind Kind, string Name, int Arity, int Depth, int Slot, bool TopLevel = false);

    enum Step
    {
        // Resolve Node, or schedule its children and its build.
        Visit,
        // Pop the bound children of Node and push the bound node.
        Build,
        // Pop the arguments of the call Node and push the call of functions[A], defined B frames out.
        BuildCall,
        // Pop the arguments of the call Node and push the call of Natives.All[A].
        BuildNative,
        // Pop the bound pattern keys of the binding Node and push its binder.
        MakeBinder,
        // Push the frame of the binder on top of binders.
        EnterBinder,
        // Define Definition at this point of the scope (A = 1 at the top level of the program).
        Define,
        // Pop the body of Definition, which is functions[A].
        EndFunction,
        // Restore the scope to A entries and the depth to B.
        Leave,
    }

    readonly record struct Task(Step Step, S.Node? Node = null, S.Definition? Definition = null, int A = 0, int B = 0);

    static readonly Node identity = new Identity();

    readonly Prelude? prelude;
    // Whether builtins with a native override bind to it (Native.OverridesPrelude).
    readonly bool nativeOverrides;
    readonly List<Entry> scope = [];
    readonly List<Function?> functions = [];
    readonly List<int> exports = [];
    readonly List<string> hostVariables = [];
    readonly List<Task> tasks = [];
    readonly List<Node> results = [];
    // The binders made and not yet built into their node, with the names of their slots.
    readonly List<(Binder Binder, List<string> Names)> binders = [];
    int depth;
    SyntaxError? error;

    Resolver(Prelude? prelude, bool nativeOverrides)
    {
        this.prelude = prelude;
        this.nativeOverrides = nativeOverrides;
        if (prelude is not null)
            functions.AddRange(prelude.Functions);
    }

    /// <summary>
    /// Binds a program against the prelude and the natives. Without <paramref name="nativeOverrides"/>
    /// every prelude builtin binds to its definition, even where a native overrides it.
    /// </summary>
    public static bool TryBind(S.Program program, [NotNullWhen(true)] out BoundProgram? bound, [NotNullWhen(false)] out SyntaxError? error, bool nativeOverrides = true)
    {
        var prelude = nativeOverrides ? Prelude.Instance : Prelude.WithoutOverrides;
        var resolver = new Resolver(prelude, nativeOverrides);
        bound = resolver.BindProgram(program) is { } body ? new BoundProgram(resolver.Functions(), [.. resolver.hostVariables], body) : null;
        error = resolver.error;
        return bound is not null;
    }

    /// <summary>Parses and binds a program.</summary>
    public static bool TryCompile(string source, [NotNullWhen(true)] out BoundProgram? bound, [NotNullWhen(false)] out SyntaxError? error, bool nativeOverrides = true)
    {
        bound = null;
        return Parser.TryParse(source, out var program, out error) && TryBind(program, out bound, out error, nativeOverrides);
    }

    /// <summary>Binds the prelude module against the natives only: all its functions, and its top-level definitions in order.</summary>
    internal static bool TryBindPrelude(S.Program module, bool nativeOverrides, out ImmutableArray<Function> functions, out ImmutableArray<Function> definitions, [NotNullWhen(false)] out SyntaxError? error)
    {
        var resolver = new Resolver(null, nativeOverrides);
        functions = definitions = [];
        error = null;
        if (resolver.BindProgram(module) is null)
        {
            error = resolver.error!;
            return false;
        }

        functions = resolver.Functions();
        var tops = ImmutableArray.CreateBuilder<Function>(resolver.exports.Count);
        foreach (var index in resolver.exports)
            tops.Add(functions[index]);
        definitions = tops.MoveToImmutable();
        return true;
    }

    ImmutableArray<Function> Functions()
    {
        var all = ImmutableArray.CreateBuilder<Function>(functions.Count);
        foreach (var function in functions)
            all.Add(function!);
        return all.MoveToImmutable();
    }

    // The body of a program (Identity for a module, whose result is its definitions), or null.
    Node? BindProgram(S.Program program)
    {
        if (!BindImports(program.Imports))
            return null;

        // Pushed last first: the definitions in order, then the body.
        if (program.Body is null)
            results.Add(identity);
        else
            tasks.Add(new Task(Step.Visit, program.Body));
        for (var i = program.Definitions.Length - 1; i >= 0; i--)
            tasks.Add(new Task(Step.Define, Definition: program.Definitions[i], A: 1));

        while (tasks.Count > 0 && error is null)
        {
            var task = tasks[^1];
            tasks.RemoveAt(tasks.Count - 1);
            Run(task);
        }

        return error is null ? results[0] : null;
    }

    // Where modules enter the scope (step 9): the exported functions of each import become Function
    // entries (qualified by the alias for import, unqualified for include, at the point of the
    // directive), and data imports become constants for $alias and $alias::alias.
    bool BindImports(ImmutableArray<S.Import> imports)
    {
        if (imports.Length == 0)
            return true;
        Fail(BindingMessages.ModulesNotSupported, imports[0].Position);
        return false;
    }

    void Run(Task task)
    {
        switch (task.Step)
        {
            case Step.Visit:
                Visit(task.Node!);
                break;
            case Step.Build:
                results.Add(Build(task.Node!, task.A));
                break;
            case Step.BuildCall:
                results.Add(new CallFunction(task.A, task.B, PopAll(((S.Call)task.Node!).Arguments.Length)));
                break;
            case Step.BuildNative:
                results.Add(new CallNative(Natives.All[task.A], PopAll(((S.Call)task.Node!).Arguments.Length)));
                break;
            case Step.MakeBinder:
                MakeBinder(task.Node!);
                break;
            case Step.EnterBinder:
                EnterBinder();
                break;
            case Step.Define:
                Define(task.Definition!, task.A == 1);
                break;
            case Step.EndFunction:
                functions[task.A] = new Function(task.Definition!.Name, task.Definition.Params.Length, BindParameters(task.Definition, Pop()));
                break;
            default:
                scope.RemoveRange(task.A, scope.Count - task.A);
                depth = task.B;
                break;
        }
    }

    // Tasks run last pushed first, so each case pushes its steps in reverse order.
    void Visit(S.Node node)
    {
        switch (node)
        {
            case S.Identity:
                results.Add(identity);
                break;
            case S.Literal literal:
                results.Add(new Literal(literal.Value));
                break;
            case S.Format format:
                results.Add(new Format(format.Name));
                break;
            case S.Variable variable:
                Add(BindVariable(variable));
                break;
            case S.Break brk:
                Add(BindBreak(brk));
                break;
            case S.Call call:
                VisitCall(call);
                break;
            case S.Template template:
                Push(Step.Build, node);
                PushAll(template.Parts);
                break;
            case S.Pipe pipe:
                Push(Step.Build, node, pipe.Right, pipe.Left);
                break;
            case S.Comma comma:
                VisitComma(comma);
                break;
            case S.Binary binary:
                Push(Step.Build, node, binary.Right, binary.Left);
                break;
            case S.And and:
                Push(Step.Build, node, and.Right, and.Left);
                break;
            case S.Or or:
                Push(Step.Build, node, or.Right, or.Left);
                break;
            case S.Alternative alternative:
                Push(Step.Build, node, alternative.Right, alternative.Left);
                break;
            case S.Negate negate:
                Push(Step.Build, node, negate.Operand);
                break;
            case S.Index index:
                Push(Step.Build, node, index.Key, index.Target);
                break;
            case S.Slice slice:
                Push(Step.Build, node, slice.To, slice.From, slice.Target);
                break;
            case S.Iterate iterate:
                Push(Step.Build, node, iterate.Target);
                break;
            case S.Try attempt:
                Push(Step.Build, node, attempt.Handler, attempt.Body);
                break;
            case S.If branch:
                Push(Step.Build, node, branch.Else, branch.Then, branch.Condition);
                break;
            case S.ArrayExpr array:
                Push(Step.Build, node, array.Body);
                break;
            case S.ObjectExpr obj:
                Push(Step.Build, node);
                for (var i = obj.Entries.Length - 1; i >= 0; i--)
                    Push(obj.Entries[i].Value, obj.Entries[i].Key);
                break;
            case S.Bind bind:
                // The source and the pattern keys in the current scope, the body with the variables' frame.
                Leave();
                Push(Step.Build, node, bind.Body);
                tasks.Add(new Task(Step.EnterBinder));
                PushBinder(node, bind.Patterns);
                Push(bind.Source);
                break;
            case S.Reduce reduce:
                Leave();
                Push(Step.Build, node, reduce.Update);
                tasks.Add(new Task(Step.EnterBinder));
                Push(reduce.Init);
                PushBinder(node, reduce.Patterns);
                Push(reduce.Source);
                break;
            case S.Foreach each:
                Leave();
                Push(Step.Build, node, each.Extract, each.Update);
                tasks.Add(new Task(Step.EnterBinder));
                Push(each.Init);
                PushBinder(node, each.Patterns);
                Push(each.Source);
                break;
            case S.Label label:
                Leave();
                Push(Step.Build, node, label.Body);
                depth++;
                scope.Add(new Entry(EntryKind.Label, label.Name, 0, depth, 0));
                break;
            case S.FuncDef definition:
                // The definition is in functions; the node's result is the rest's.
                Leave();
                Push(definition.Rest);
                tasks.Add(new Task(Step.Define, Definition: definition.Definition));
                break;
            default:
                throw new InvalidOperationException($"Unknown node {node.Kind}");
        }
    }

    // A left-nested chain of commas becomes one node with the items in order.
    void VisitComma(S.Comma comma)
    {
        var items = new List<S.Node>();
        while (true)
        {
            items.Add(comma.Right);
            if (comma.Left is not S.Comma left)
                break;
            comma = left;
        }

        items.Add(comma.Left);
        items.Reverse();
        tasks.Add(new Task(Step.Build, comma, A: items.Count));
        PushAll([.. items]);
    }

    // Pops the bound children of node, last first, and builds its bound node; count is the number of items of a comma.
    Node Build(S.Node node, int count)
    {
        switch (node)
        {
            case S.Template template:
                return new Template(template.Format, template.Texts, PopAll(template.Parts.Length));
            case S.Pipe:
                {
                    var right = Pop();
                    return new Pipe(Pop(), right);
                }

            case S.Comma:
                return new Comma(PopAll(count));

            case S.Binary binary:
                {
                    var right = Pop();
                    return new Binary(binary.Op, Pop(), right);
                }

            case S.And:
                {
                    var right = Pop();
                    return new And(Pop(), right);
                }

            case S.Or:
                {
                    var right = Pop();
                    return new Or(Pop(), right);
                }

            case S.Alternative:
                {
                    var right = Pop();
                    return new Alternative(Pop(), right);
                }

            case S.Negate:
                return new Negate(Pop());
            case S.Index:
                {
                    var key = Pop();
                    return new Index(Pop(), key);
                }

            case S.Slice slice:
                {
                    var to = slice.To is null ? null : Pop();
                    var from = slice.From is null ? null : Pop();
                    return new Slice(Pop(), from, to);
                }

            case S.Iterate:
                return new Iterate(Pop());
            case S.Try attempt:
                {
                    var handler = attempt.Handler is null ? null : Pop();
                    return new Try(Pop(), handler);
                }

            case S.If:
                {
                    var otherwise = Pop();
                    var then = Pop();
                    return new If(Pop(), then, otherwise);
                }

            case S.ArrayExpr array:
                return new ArrayExpr(array.Body is null ? null : Pop());
            case S.ObjectExpr obj:
                {
                    var entries = new ObjectEntry[obj.Entries.Length];
                    for (var i = entries.Length - 1; i >= 0; i--)
                    {
                        var value = obj.Entries[i].Value is null ? null : Pop();
                        entries[i] = new ObjectEntry(Pop(), value);
                    }

                    return new ObjectExpr([.. entries]);
                }

            case S.Bind:
                {
                    var body = Pop();
                    return new Bind(Pop(), PopBinder(), body);
                }

            case S.Reduce:
                {
                    var update = Pop();
                    var init = Pop();
                    return new Reduce(Pop(), PopBinder(), init, update);
                }

            case S.Foreach:
                {
                    var extract = Pop();
                    var update = Pop();
                    var init = Pop();
                    return new Foreach(Pop(), PopBinder(), init, update, extract);
                }

            default:
                return new Label(Pop());
        }
    }

    // Defines a function at the current point of the scope, visible in its own body and after it.
    void Define(S.Definition definition, bool topLevel)
    {
        var index = functions.Count;
        functions.Add(null);
        if (topLevel)
            exports.Add(index);
        scope.Add(new Entry(EntryKind.Function, definition.Name, definition.Params.Length, depth, index, topLevel));

        Leave();
        tasks.Add(new Task(Step.EndFunction, Definition: definition, A: index));
        Push(definition.Body);
        if (definition.Params.Length == 0)
            return;
        depth++;
        for (var slot = 0; slot < definition.Params.Length; slot++)
            scope.Add(new Entry(EntryKind.Parameter, definition.Params[slot].Name, 0, depth, slot));
        // Each $name parameter then has a frame of its own, first parameter outermost (BindParameters).
        foreach (var parameter in definition.Params)
        {
            if (parameter.IsVariable)
            {
                depth++;
                scope.Add(new Entry(EntryKind.Variable, parameter.Name, 0, depth, 0));
            }
        }
    }

    static readonly Binder parameterBinder = new([new VariablePattern(0)], 1);

    // def f($a; $b): B is def f(a; b): a as $a | b as $b | B, except that each binding names its
    // parameter by position: the k-th $ parameter's frame is k frames inside the parameters' frame.
    static Node BindParameters(S.Definition definition, Node body)
    {
        var variables = 0;
        foreach (var parameter in definition.Params)
            variables += parameter.IsVariable ? 1 : 0;
        for (var slot = definition.Params.Length - 1; slot >= 0; slot--)
        {
            if (definition.Params[slot].IsVariable)
                body = new Bind(new CallParameter(--variables, slot), parameterBinder, body);
        }

        return body;
    }

    Node? BindVariable(S.Variable variable)
    {
        for (var i = scope.Count - 1; i >= 0; i--)
        {
            var entry = scope[i];
            if (entry.Kind == EntryKind.Variable && entry.Name == variable.Name)
                return new Variable(depth - entry.Depth, entry.Slot);
        }

        if (variable.Name == "ENV")
            return new EnvVariable();
        // A qualified name can only come from a data import (BindImports); the prelude has no host variables.
        if (variable.Name.Contains("::", StringComparison.Ordinal) || prelude is null)
            return Fail(BindingMessages.VariableNotDefined(variable.Name), variable.Position);

        var index = hostVariables.IndexOf(variable.Name);
        if (index < 0)
        {
            index = hostVariables.Count;
            hostVariables.Add(variable.Name);
        }

        return new HostVariable(variable.Name, index);
    }

    void VisitCall(S.Call call)
    {
        var arity = call.Arguments.Length;
        for (var i = scope.Count - 1; i >= 0; i--)
        {
            var entry = scope[i];
            if (entry.Name != call.Name || entry.Arity != arity)
                continue;
            if (entry.Kind == EntryKind.Parameter)
            {
                results.Add(new CallParameter(depth - entry.Depth, entry.Slot));
                return;
            }

            if (entry.Kind == EntryKind.Function)
            {
                // Inside the prelude, its own builtins with a native override bind to the native too.
                if (prelude is null && entry.TopLevel && Override(call.Name, arity) is { } native)
                    tasks.Add(new Task(Step.BuildNative, call, A: native.Index));
                else
                    tasks.Add(new Task(Step.BuildCall, call, A: entry.Slot, B: depth - entry.Depth));
                PushAll(call.Arguments);
                return;
            }
        }

        // The prelude is defined in the outermost, empty environment: every frame of the caller is left.
        if (prelude?.Find(call.Name, arity) is { } function)
        {
            if (Override(call.Name, arity) is { } native)
                tasks.Add(new Task(Step.BuildNative, call, A: native.Index));
            else
                tasks.Add(new Task(Step.BuildCall, call, A: function, B: depth));
        }
        else if (Natives.Find(call.Name, arity) is { } native)
            tasks.Add(new Task(Step.BuildNative, call, A: native.Index));
        else
            Fail(BindingMessages.NotDefined(call.Name, arity), call.Position);
        PushAll(call.Arguments);
    }

    // The native override of a prelude builtin, when overrides are on.
    Native? Override(string name, int arity) => nativeOverrides ? Natives.Override(name, arity) : null;

    Node? BindBreak(S.Break node)
    {
        for (var i = scope.Count - 1; i >= 0; i--)
        {
            var entry = scope[i];
            if (entry.Kind == EntryKind.Label && entry.Name == node.Name)
                return new Break(depth - entry.Depth);
        }

        return Fail(BindingMessages.LabelNotDefined(node.Name), node.Position);
    }

    // Schedules the binder of node: its pattern keys, in destructuring order, then MakeBinder.
    void PushBinder(S.Node node, ImmutableArray<S.Pattern> patterns)
    {
        tasks.Add(new Task(Step.MakeBinder, node));
        var keys = new List<S.Node>();
        foreach (var pattern in patterns)
            CollectKeys(pattern, keys);
        PushAll([.. keys]);
    }

    static void CollectKeys(S.Pattern pattern, List<S.Node> keys)
    {
        switch (pattern)
        {
            case S.ArrayPattern array:
                for (var i = array.Elements.Length - 1; i >= 0; i--)
                    CollectKeys(array.Elements[i], keys);
                break;
            case S.ObjectPattern obj:
                foreach (var entry in obj.Entries)
                {
                    keys.Add(entry.Key);
                    if (entry.Value is not null)
                        CollectKeys(entry.Value, keys);
                }

                break;
        }
    }

    // Builds the ?// alternatives of a binding from its bound keys (the last results, in the order
    // of CollectKeys). Every distinct variable name of all the alternatives gets a slot. A name
    // repeated in one pattern (§7.5): without ?// its first occurrence in matching order gives the
    // value, so later ones discard theirs; with ?// every occurrence stores and the last one stays.
    void MakeBinder(S.Node node)
    {
        var patterns = node switch
        {
            S.Bind bind => bind.Patterns,
            S.Reduce reduce => reduce.Patterns,
            _ => ((S.Foreach)node).Patterns,
        };

        var keys = 0;
        foreach (var pattern in patterns)
            keys += CountKeys(pattern);
        var next = results.Count - keys;
        var names = new List<string>();
        var seen = patterns.Length == 1 ? new HashSet<string>() : null;
        var bound = ImmutableArray.CreateBuilder<Pattern>(patterns.Length);
        foreach (var pattern in patterns)
            bound.Add(BindPattern(pattern, names, seen, ref next));

        results.RemoveRange(results.Count - keys, keys);
        binders.Add((new Binder(bound.MoveToImmutable(), names.Count), names));
    }

    static int CountKeys(S.Pattern pattern)
    {
        var keys = new List<S.Node>();
        CollectKeys(pattern, keys);
        return keys.Count;
    }

    // Visits elements and entries in the order of CollectKeys, which is matching order (spec §7.5).
    Pattern BindPattern(S.Pattern pattern, List<string> names, HashSet<string>? seen, ref int next)
    {
        switch (pattern)
        {
            case S.VariablePattern variable:
                return new VariablePattern(Slot(variable.Name, names, seen));
            case S.ArrayPattern array:
                {
                    var elements = new Pattern[array.Elements.Length];
                    for (var i = elements.Length - 1; i >= 0; i--)
                        elements[i] = BindPattern(array.Elements[i], names, seen, ref next);
                    return new ArrayPattern([.. elements]);
                }

            default:
                {
                    var entries = ((S.ObjectPattern)pattern).Entries;
                    var bound = ImmutableArray.CreateBuilder<PatternEntry>(entries.Length);
                    foreach (var entry in entries)
                    {
                        var key = results[next++];
                        var slot = entry.Variable is null ? -1 : Slot(entry.Variable, names, seen);
                        var value = entry.Value is null ? null : BindPattern(entry.Value, names, seen, ref next);
                        bound.Add(new PatternEntry(key, slot, value));
                    }

                    return new ObjectPattern(bound.MoveToImmutable());
                }
        }
    }

    // The slot of a variable in names, or -1 when seen (first occurrence wins) already has the name.
    static int Slot(string name, List<string> names, HashSet<string>? seen)
    {
        if (seen is not null && !seen.Add(name))
            return -1;
        var slot = names.IndexOf(name);
        if (slot < 0)
        {
            slot = names.Count;
            names.Add(name);
        }

        return slot;
    }

    // Pushes the frame of the last binder made: one variable per name, in slot order.
    void EnterBinder()
    {
        var names = binders[^1].Names;
        depth++;
        for (var slot = 0; slot < names.Count; slot++)
            scope.Add(new Entry(EntryKind.Variable, names[slot], 0, depth, slot));
    }

    Binder PopBinder()
    {
        var binder = binders[^1].Binder;
        binders.RemoveAt(binders.Count - 1);
        return binder;
    }

    // Schedules the restoring of the scope as it is now, after the tasks pushed later.
    void Leave() => tasks.Add(new Task(Step.Leave, A: scope.Count, B: depth));

    void Push(Step step, S.Node node, S.Node? last = null, S.Node? middle = null, S.Node? first = null)
    {
        tasks.Add(new Task(step, node));
        Push(last, middle, first);
    }

    // Schedules the visit of the given nodes, missing ones skipped, the last argument first.
    void Push(S.Node? last, S.Node? middle = null, S.Node? first = null)
    {
        if (last is not null)
            tasks.Add(new Task(Step.Visit, last));
        if (middle is not null)
            tasks.Add(new Task(Step.Visit, middle));
        if (first is not null)
            tasks.Add(new Task(Step.Visit, first));
    }

    void PushAll(ImmutableArray<S.Node> nodes)
    {
        for (var i = nodes.Length - 1; i >= 0; i--)
            tasks.Add(new Task(Step.Visit, nodes[i]));
    }

    void Add(Node? node)
    {
        if (node is not null)
            results.Add(node);
    }

    Node Pop()
    {
        var node = results[^1];
        results.RemoveAt(results.Count - 1);
        return node;
    }

    ImmutableArray<Node> PopAll(int count)
    {
        var nodes = ImmutableArray.CreateBuilder<Node>(count);
        for (var i = results.Count - count; i < results.Count; i++)
            nodes.Add(results[i]);
        results.RemoveRange(results.Count - count, count);
        return nodes.MoveToImmutable();
    }

    Node? Fail(string message, Position position)
    {
        error ??= new SyntaxError(message, position.Line, position.Column);
        return null;
    }
}

/// <summary>The messages of the binding layer; the first three are fixed by spec §10.2.</summary>
static class BindingMessages
{
    public static string NotDefined(string name, int arity) => $"{name}/{arity} is not defined";

    public static string LabelNotDefined(string name) => $"$*label-{name} is not defined";

    public static string VariableNotDefined(string name) => $"${name} is not defined";

    public const string ModulesNotSupported = "import/include are not supported yet";
}