using System.Text;
using JQLite.Core.Binding;
using JQLite.Core.Values;
using Xunit.Sdk;

namespace JQLite.Tests.Core.Binding;

// The bound node, not System.Index; the operators are the syntax's.
using BinaryOp = JQLite.Core.Syntax.BinaryOp;
using Index = JQLite.Core.Binding.Index;

/// <summary>
/// Prints a bound program as an S-expression stating what every reference resolved to, in the
/// shapes of AstPrinter, with:
/// <c>$h.s</c> a variable (slot s of the frame h frames out), <c>$$name</c> a host variable,
/// <c>$ENV</c>; <c>name/n#i^h</c> a call of the program's function i (0 = its first own function)
/// defined h frames out, <c>prelude:name/n^h</c> a call of a prelude builtin,
/// <c>native:name/n</c> a native, <c>param^h.s</c> a closure parameter; <c>(break ^h)</c>; in
/// patterns <c>$s</c> stores to slot s and <c>$_</c> discards. The program's own functions come
/// first as <c>(def name/n#i body)</c>. Recursive: for the trees of ordinary tests only.
/// </summary>
static class BoundPrinter
{
    static int PreludeCount => Prelude.Instance.Functions.Length;

    /// <summary>Parses and binds a program and prints its own functions and its body, separated by spaces.</summary>
    public static string Bind(string source)
    {
        if (!Resolver.TryCompile(source, out var program, out var error))
            throw new XunitException($"'{source}' does not bind: {error.Message} at {error.Line}:{error.Column}");
        return Print(program);
    }

    /// <summary>Binds a program that must fail and gives "message @line:column".</summary>
    public static string Error(string source)
    {
        if (Resolver.TryCompile(source, out var program, out var error))
            throw new XunitException($"'{source}' binds as {Print(program)}");
        return $"{error.Message} @{error.Line}:{error.Column}";
    }

    public static string Print(BoundProgram program)
    {
        var parts = new List<string>();
        for (var i = PreludeCount; i < program.Functions.Length; i++)
            parts.Add($"(def {Name(program, i)} {Print(program, program.Functions[i].Body)})");
        parts.Add(Print(program, program.Body));
        return string.Join(" ", parts);
    }

    static string Name(BoundProgram program, int index)
    {
        var function = program.Functions[index];
        return index < PreludeCount ? $"prelude:{function}" : $"{function}#{index - PreludeCount}";
    }

    static string Print(BoundProgram program, Node? node)
    {
        string P(Node? child) => Print(program, child);
        string All(IEnumerable<Node> children) => string.Concat(children.Select(child => " " + P(child)));
        return node switch
        {
            null => "_",
            Identity => ".",
            Literal literal => ValueText.ToJson(literal.Value),
            Template template => PrintTemplate(template, P),
            Format format => "@" + format.Name,
            Index index => $"(index {P(index.Target)} {P(index.Key)})",
            Slice slice => $"(slice {P(slice.Target)} {P(slice.From)} {P(slice.To)})",
            Iterate iterate => $"(iter {P(iterate.Target)})",
            Variable variable => $"${variable.Hops}.{variable.Slot}",
            HostVariable host => "$$" + host.Name,
            EnvVariable => "$ENV",
            CallFunction call => call.Arguments.Length == 0
                ? $"{Name(program, call.Function)}^{call.Hops}"
                : $"({Name(program, call.Function)}^{call.Hops}{All(call.Arguments)})",
            CallParameter parameter => $"param^{parameter.Hops}.{parameter.Slot}",
            CallNative call => call.Arguments.Length == 0 ? $"native:{call.Native}" : $"(native:{call.Native}{All(call.Arguments)})",
            JQLite.Core.Binding.Pipe pipe => $"(| {P(pipe.Left)} {P(pipe.Right)})",
            Comma comma => $"(,{All(comma.Items)})",
            Binary binary => $"({Symbol(binary.Op)} {P(binary.Left)} {P(binary.Right)})",
            And and => $"(and {P(and.Left)} {P(and.Right)})",
            Or or => $"(or {P(or.Left)} {P(or.Right)})",
            Alternative alternative => $"(// {P(alternative.Left)} {P(alternative.Right)})",
            Negate negate => $"(neg {P(negate.Operand)})",
            ArrayExpr array => array.Body is null ? "(array)" : $"(array {P(array.Body)})",
            ObjectExpr obj => obj.Entries.Length == 0 ? "(object)"
                : $"(object {string.Join(" ", obj.Entries.Select(entry => $"({P(entry.Key)} {P(entry.Value)})"))})",
            If cond => $"(if {P(cond.Condition)} {P(cond.Then)} {P(cond.Else)})",
            Try attempt => attempt.Handler is null ? $"(try {P(attempt.Body)})" : $"(try {P(attempt.Body)} {P(attempt.Handler)})",
            Reduce reduce => $"(reduce {P(reduce.Source)} {Print(reduce.Binder, P)} {P(reduce.Init)} {P(reduce.Update)})",
            Foreach each => $"(foreach {P(each.Source)} {Print(each.Binder, P)} {P(each.Init)} {P(each.Update)} {P(each.Extract)})",
            Bind bind => $"(as {P(bind.Source)} {Print(bind.Binder, P)} {P(bind.Body)})",
            Label label => $"(label {P(label.Body)})",
            Break brk => $"(break ^{brk.Hops})",
            _ => throw new XunitException($"Unknown node {node.Kind}"),
        };
    }

    static string PrintTemplate(Template template, Func<Node?, string> print)
    {
        var builder = new StringBuilder("(str");
        if (template.Format is not null)
            builder.Append(" @").Append(template.Format);
        for (var i = 0; i < template.Parts.Length; i++)
            builder.Append(' ').Append(ValueText.ToJson(JqValue.FromString(template.Texts[i]))).Append(' ').Append(print(template.Parts[i]));
        return builder.Append(' ').Append(ValueText.ToJson(JqValue.FromString(template.Texts[^1]))).Append(')').ToString();
    }

    static string Print(Binder binder, Func<Node?, string> print)
        => binder.Patterns.Length == 1 ? Print(binder.Patterns[0], print) : $"(?// {string.Join(" ", binder.Patterns.Select(p => Print(p, print)))})";

    static string Print(Pattern? pattern, Func<Node?, string> print) => pattern switch
    {
        null => "_",
        VariablePattern variable => Slot(variable.Slot),
        ArrayPattern array => $"[{string.Join(" ", array.Elements.Select(p => Print(p, print)))}]",
        ObjectPattern obj => $"{{{string.Join(" ", obj.Entries.Select(entry => $"({print(entry.Key)} {(entry.Slot < 0 ? "_" : Slot(entry.Slot))} {Print(entry.Value, print)})"))}}}",
        _ => throw new XunitException($"Unknown pattern {pattern.Kind}"),
    };

    static string Slot(int slot) => slot < 0 ? "$_" : "$" + slot;

    static string Symbol(BinaryOp op) => op switch
    {
        BinaryOp.Add => "+",
        BinaryOp.Subtract => "-",
        BinaryOp.Multiply => "*",
        BinaryOp.Divide => "/",
        BinaryOp.Modulo => "%",
        BinaryOp.Equal => "==",
        BinaryOp.NotEqual => "!=",
        BinaryOp.Less => "<",
        BinaryOp.LessEqual => "<=",
        BinaryOp.Greater => ">",
        _ => ">=",
    };
}
