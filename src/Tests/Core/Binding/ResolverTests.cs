using Devlooped.Core.Binding;
using static Devlooped.Tests.Core.Binding.BoundPrinter;

namespace Devlooped.Tests.Core.Binding;

/// <summary>Lexical binding (spec §7.5, §7.7, §7.15) stated on the printed bound program, and the binding errors of §10.2.</summary>
public class ResolverTests
{
    [Theory]
    // Variables: the innermost binding, counted in frames outward.
    [InlineData("1 as $x | $x", "(as 1 $0 $0.0)")]
    [InlineData("1 as $x | 2 as $x | $x", "(as 1 $0 (as 2 $0 $0.0))")]
    [InlineData("1 as $x | 2 as $y | $x, $y", "(as 1 $0 (as 2 $0 (, $1.0 $0.0)))")]
    [InlineData("1 as $x | (2 as $x | $x), $x", "(as 1 $0 (, (as 2 $0 $0.0) $0.0))")]
    // A binding is not visible in its own source.
    [InlineData("1 as $x | ($x + 1) as $x | $x", "(as 1 $0 (as (+ $0.0 1) $0 $0.0))")]
    // A function body sees the variables of its definition, not of its caller.
    [InlineData("1 as $x | def f: $x; 2 as $x | f", "(def f/0#0 $0.0) (as 1 $0 (as 2 $0 f/0#0^1))")]
    [InlineData("def f: 1; 2 as $x | f", "(def f/0#0 1) (as 2 $0 f/0#0^1)")]
    public void Variables_bind_lexically(string program, string bound)
        => Assert.Equal(bound, Bind(program));

    [Theory]
    // Recursion: a definition is visible in its own body.
    [InlineData("def f: f; f", "(def f/0#0 f/0#0^0) f/0#0^0")]
    // Later definitions shadow earlier ones; an earlier body keeps what it saw.
    [InlineData("def f: 1; def g: f; def f: 2; g", "(def f/0#0 1) (def g/0#1 f/0#0^0) (def f/0#2 2) g/0#1^0")]
    // Name and arity together identify a function.
    [InlineData("def f: 1; def f(a): 2; f, f(3)", "(def f/0#0 1) (def f/1#1 2) (, f/0#0^0 (f/1#1^0 3))")]
    // Local definitions are visible in their scope only.
    [InlineData("def f: 1; [f, (def f: 2; f), f]", "(def f/0#0 1) (def f/0#1 2) (array (, f/0#0^0 f/0#1^0 f/0#0^0))")]
    // A call of an enclosing function from a nested one, across the parameters' frame.
    [InlineData("def f(g): def h: f(g); h; f(.)", "(def f/1#0 h/0#1^0) (def h/0#1 (f/1#0^1 param^0.0)) (f/1#0^0 .)")]
    public void Functions_bind_to_the_innermost_visible_definition(string program, string bound)
        => Assert.Equal(bound, Bind(program));

    [Theory]
    // A parameter is a closure: a call of slot s of the call frame.
    [InlineData("def f(g): g; f(.)", "(def f/1#0 param^0.0) (f/1#0^0 .)")]
    [InlineData("def f(a; b): b, a; f(1; 2)", "(def f/2#0 (, param^0.1 param^0.0)) (f/2#0^0 1 2)")]
    // A repeated parameter name: the last one wins (jq 1.8.2: 2).
    [InlineData("def f(a; a): a; f(1; 2)", "(def f/2#0 param^0.1) (f/2#0^0 1 2)")]
    // Seen from a nested function, the parameter is one frame further when the nested one has parameters.
    [InlineData("def f(g): def h(x): g, x; h(1); f(2)", "(def f/1#0 (h/1#1^0 1)) (def h/1#1 (, param^1.0 param^0.0)) (f/1#0^0 2)")]
    // A parameter shadows a function of its name and arity 0 only (jq 1.8.2: 7).
    [InlineData("def x: 7; def f(x): x; f(1)", "(def x/0#0 7) (def f/1#1 param^0.0) (f/1#1^0 1)")]
    [InlineData("def x(a): 7; def f(x): x(0); f(1)", "(def x/1#0 7) (def f/1#1 (x/1#0^1 0)) (f/1#1^0 1)")]
    // A definition inside the body shadows the parameter.
    [InlineData("def f(g): def g: 1; g; f(2)", "(def f/1#0 g/0#1^0) (def g/0#1 1) (f/1#0^0 2)")]
    // def f($a) is def f(a): a as $a | …: both the filter and the variable are visible.
    [InlineData("def f($a): $a, a; f(1)", "(def f/1#0 (as param^0.0 $0 (, $0.0 param^1.0))) (f/1#0^0 1)")]
    [InlineData("def f($a; $b): $a, $b; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (as param^1.1 $0 (, $1.0 $0.0)))) (f/2#0^0 1 2)")]
    // The binding of $a names its parameter by position; the filter a is the last parameter of that name (spec §7.7).
    [InlineData("def f(a; $a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.1 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)")]
    [InlineData("def f($a; a): [a, $a]; f(1; 2)", "(def f/2#0 (as param^0.0 $0 (array (, param^1.1 $0.0)))) (f/2#0^0 1 2)")]
    [InlineData("def f(x): def x: 5; x; f(1)", "(def f/1#0 x/0#1^0) (def x/0#1 5) (f/1#0^0 1)")]
    // Qualified names are ordinary names when defined (jq 1.8.2).
    [InlineData("def f(a::b): a::b; f(3)", "(def f/1#0 param^0.0) (f/1#0^0 3)")]
    [InlineData("def f::g: 1; f::g", "(def f::g/0#0 1) f::g/0#0^0")]
    public void Parameters_are_closures(string program, string bound)
        => Assert.Equal(bound, Bind(program));

    [Theory]
    // A break refers to the label visible where it is written, also inside a function: the spec's example.
    [InlineData("[label $f | def g: break $f; (label $f | 1, g, 2), 3]", "(def g/0#0 (break ^0)) (array (label (, (label (, 1 g/0#0^1 2)) 3)))")]
    [InlineData("label $a | label $b | break $a, break $b", "(label (label (, (break ^1) (break ^0))))")]
    [InlineData("label $a | 1 as $x | break $a", "(label (as 1 $0 (break ^1)))")]
    // Labels and variables are separate name spaces.
    [InlineData("label $x | 1 as $x | $x, break $x", "(label (as 1 $0 (, $0.0 (break ^1))))")]
    [InlineData("label $a::b | break $a::b", "(label (break ^0))")]
    public void Labels_bind_lexically(string program, string bound)
        => Assert.Equal(bound, Bind(program));

    [Theory]
    [InlineData(". as [$a, $b] | $a, $b", "(as . [$1 $0] (, $0.1 $0.0))")]
    [InlineData(". as {$a, b: [$c]} | $a, $c", "(as . {(\"a\" $0 _) (\"b\" _ [$1])} (, $0.0 $0.1))")]
    [InlineData(". as {$a: [$b]} | $a, $b", "(as . {(\"a\" $0 [$1])} (, $0.0 $0.1))")]
    // Every variable of every alternative has a slot; a name shared by alternatives shares it.
    [InlineData(". as [$a] ?// {a: $b} ?// $a | [$a, $b]", "(as . (?// [$0] {(\"a\" _ $1)} $0) (array (, $0.0 $0.1)))")]
    // A repeated name: the first occurrence in destructuring order owns the slot (array from the last element, object from the first entry).
    [InlineData(". as [$a, $a] | $a", "(as . [$_ $0] $0.0)")]
    [InlineData(". as {$a, b: $a} | $a", "(as . {(\"a\" $0 _) (\"b\" _ $_)} $0.0)")]
    [InlineData(". as [$a, [$a]] | $a", "(as . [$_ [$0]] $0.0)")]
    // With ?// every occurrence stores and the last in matching order stays (spec §7.5).
    [InlineData(". as {$a, b: $a} ?// $c | $a", "(as . (?// {(\"a\" $0 _) (\"b\" _ $0)} $1) $0.0)")]
    [InlineData(". as {x: $c} ?// [$a, $a] | $a", "(as . (?// {(\"x\" _ $0)} [$1 $1]) $0.1)")]
    // Key expressions see the scope outside the binding, not the pattern's variables (jq 1.8.2).
    [InlineData("\"b\" as $x | . as {a: $x, ($x): $y} | $y", "(as \"b\" $0 (as . {(\"a\" _ $0) ($0.0 _ $1)} $0.1))")]
    [InlineData(". as {a: $x, ($x): $y} | $y", "(as . {(\"a\" _ $0) ($$x _ $1)} $0.1)")]
    // reduce/foreach: the source and the initial value outside the binder's frame, update and extract inside.
    [InlineData("1 as $x | reduce . as $y ($x; $x, $y)", "(as 1 $0 (reduce . $0 $0.0 (, $1.0 $0.0)))")]
    [InlineData("foreach .[] as [$a] (0; $a; [$a, .])", "(foreach (iter .) [$0] 0 $0.0 (array (, $0.0 .)))")]
    [InlineData("reduce .[] as $x (.; .) | $x", "(| (reduce (iter .) $0 . .) $$x)")]
    public void Patterns_bind_every_variable(string program, string bound)
        => Assert.Equal(bound, Bind(program));

    [Theory]
    // An unbound simple $name is a host variable, numbered by first appearance.
    [InlineData("$foo, $bar, $foo", "(, $$foo $$bar $$foo)")]
    [InlineData("$__loc__", "$$__loc__")]
    [InlineData("$ENV", "$ENV")]
    [InlineData("1 as $ENV | $ENV", "(as 1 $0 $0.0)")]
    [InlineData("def env: $ENV; env", "(def env/0#0 $ENV) env/0#0^0")]
    public void Unbound_variables_are_host_variables(string program, string bound)
        => Assert.Equal(bound, Bind(program));

    [Fact]
    public void Host_variables_are_listed_once()
    {
        Assert.True(Resolver.TryCompile("$foo, $bar, ($foo as $bar | $bar), $baz", out var program, out _));
        Assert.Equal(["foo", "bar", "baz"], program.HostVariables.ToArray());
    }

    [Theory]
    // Builtins after user definitions: prelude, then natives (§7.7). A prelude function is defined
    // in the outermost environment, so the call leaves every frame of the caller.
    [InlineData("length", "native:length/0")]
    [InlineData("map(.)", "(prelude:map/1^0 .)")]
    [InlineData("1 as $x | 2 as $y | map($x)", "(as 1 $0 (as 2 $0 (prelude:map/1^2 $1.0)))")]
    [InlineData("def map(f): 1; map(.)", "(def map/1#0 1) (map/1#0^0 .)")]
    [InlineData("def length: 1; length", "(def length/0#0 1) length/0#0^0")]
    // The parser's own calls resolve the same way: .. is recurse/0, |= is _modify/2, op= binds $*update.
    [InlineData("..", "prelude:recurse/0^0")]
    [InlineData("def recurse: 1; ..", "(def recurse/0#0 1) recurse/0#0^0")]
    // _modify and _assign are prelude builtins with a native override (Native.OverridesPrelude).
    [InlineData(".a |= 1", "(native:_modify/2 (index . \"a\") 1)")]
    [InlineData(".a = 1", "(native:_assign/2 (index . \"a\") 1)")]
    [InlineData(".a += 1", "(as 1 $0 (native:_modify/2 (index . \"a\") (+ . $0.0)))")]
    [InlineData("def _modify(a; b): 42; .a |= 1", "(def _modify/2#0 42) (_modify/2#0^0 (index . \"a\") 1)")]
    // A definition named like a literal does not change the literal (spec §2).
    [InlineData("def true: 1; true", "(def true/0#0 1) true")]
    public void Calls_resolve_to_user_then_prelude_then_native(string program, string bound)
        => Assert.Equal(bound, Bind(program));

    [Fact]
    public void Without_overrides_builtins_bind_to_the_prelude()
    {
        Assert.True(Resolver.TryCompile(".a |= 1, (.b = 2), map_values(.)", out var program, out _, nativeOverrides: false));
        Assert.Equal("(, (prelude:_modify/2^0 (index . \"a\") 1) (prelude:_assign/2^0 (index . \"b\") 2) (prelude:map_values/1^0 .))", Print(program));

        // Inside the prelude too: map_values calls _modify.
        var mapValues = Prelude.Instance.Functions[Prelude.Instance.Find("map_values", 1)!.Value].Body;
        Assert.Equal(NodeKind.CallNative, mapValues.Kind);
        mapValues = Prelude.WithoutOverrides.Functions[Prelude.WithoutOverrides.Find("map_values", 1)!.Value].Body;
        Assert.Equal(NodeKind.CallFunction, mapValues.Kind);
    }

    [Fact]
    public void Every_construct_binds_its_children()
        => Assert.Equal(
            "(as . $0 (array (, (object (\"a\" $0.0) (\"b\" _) ($0.0 1)) (str @base64 \"x\" $0.0 \"\") (slice (index . $0.0) $0.0 _) (if $0.0 (try $0.0 $0.0) (// (or (and $0.0 $0.0) (neg $0.0)) @csv)) (iter $0.0) (try (index . \"a\")))))",
            Bind(". as $x | [{a: $x, b, ($x): 1}, @base64 \"x\\($x)\", .[$x][$x:], if $x then try $x catch $x else $x and $x or -$x // @csv end, $x[], .a?]"));

    [Fact]
    public void Prelude_functions_come_first_in_every_program()
    {
        Assert.True(Resolver.TryCompile("def f: 1; f", out var first, out _));
        Assert.True(Resolver.TryCompile("map(.)", out var second, out _));
        var prelude = Prelude.Instance.Functions;
        Assert.Equal(prelude.Length + 1, first.Functions.Length);
        Assert.Equal(prelude.Length, second.Functions.Length);
        for (var i = 0; i < prelude.Length; i++)
        {
            Assert.Same(prelude[i], first.Functions[i]);
            Assert.Same(prelude[i], second.Functions[i]);
        }
    }

    [Theory]
    [InlineData("foo", "foo/0 is not defined @1:1")]
    [InlineData("1 | foo(1; 2)", "foo/2 is not defined @1:5")]
    [InlineData("map(1; 2)", "map/2 is not defined @1:1")]
    [InlineData("1 |\n  foo", "foo/0 is not defined @2:3")]
    [InlineData("foo::bar", "foo::bar/0 is not defined @1:1")]
    // The first unknown name in source order.
    [InlineData("1 + foo, bar", "foo/0 is not defined @1:5")]
    [InlineData("foo(bar)", "foo/1 is not defined @1:1")]
    [InlineData("map(bar)", "bar/0 is not defined @1:5")]
    // A definition is visible after its own def only, and local ones in their scope only (jq 1.8.2 positions).
    [InlineData("def f: g; def g: 1; f", "g/0 is not defined @1:8")]
    [InlineData("def f: def g: 3; g; g", "g/0 is not defined @1:21")]
    [InlineData("(def f: 1; f), f", "f/0 is not defined @1:16")]
    // A parameter is f/0 only.
    [InlineData("def f(g): g(1); f(1)", "g/1 is not defined @1:11")]
    [InlineData("def f($a): a(1); f(1)", "a/1 is not defined @1:12")]
    [InlineData(". as $foo | break $foo", "$*label-foo is not defined @1:13")]
    [InlineData("label $f | def g: 1; break $g", "$*label-g is not defined @1:22")]
    // A label is not visible in a function defined outside it.
    [InlineData("def g: break $f; label $f | g", "$*label-f is not defined @1:8")]
    [InlineData("$foo::bar", "$foo::bar is not defined @1:1")]
    [InlineData("1 as $x | $x::y", "$x::y is not defined @1:11")]
    // Errors inside patterns, reduce and foreach.
    [InlineData(". as {(foo): $x} | $x", "foo/0 is not defined @1:8")]
    [InlineData("reduce . as $x (0; foo)", "foo/0 is not defined @1:20")]
    [InlineData("foreach . as $x (0; .; foo)", "foo/0 is not defined @1:24")]
    // Without a module source no module exists.
    [InlineData("import \"a\" as a; 1", "module not found: a @1:1")]
    [InlineData("1 as $x | include \"a\"; 1", "Unexpected 'include' @1:11")]
    public void Unknown_names_are_binding_errors(string program, string error)
        => Assert.Equal(error, Error(program));

    [Fact]
    public void Syntax_errors_come_from_the_parser()
        => Assert.Equal("Unexpected end of program @1:4", Error("1 +"));

    [Fact]
    public void Module_metadata_alone_is_accepted()
        => Assert.Equal("1", Bind("module {a: 1}; 1"));
}