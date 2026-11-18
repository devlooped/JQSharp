using System.Text;
using Devlooped.Core.Syntax;
using Devlooped.Core.Values;
using Xunit.Sdk;

namespace Devlooped.Tests.Core.Syntax;

// The AST node, not System.Index.
using Index = Devlooped.Core.Syntax.Index;

/// <summary>
/// Prints an AST as an S-expression so tests can state structure: <c>.</c>, literals as JSON,
/// <c>$x</c>, a call without arguments as its name, <c>(f a b)</c>, <c>(| a b)</c>, <c>(, a b)</c>,
/// <c>(+ a b)</c>, <c>(neg a)</c>, <c>(index t k)</c>, <c>(slice t from to)</c>, <c>(iter t)</c>,
/// <c>(str @fmt "a" e "b")</c>, <c>(object (k v) (k _))</c>, <c>(as source pattern body)</c>, …; a
/// missing child is <c>_</c>. Recursive: for the trees of ordinary tests only.
/// </summary>
static class AstPrinter
{
    /// <summary>Parses a program and prints its top-level definitions and body, separated by spaces.</summary>
    public static string Parse(string source)
    {
        if (!Parser.TryParse(source, out var program, out var error))
            throw new XunitException($"'{source}' does not parse: {error.Message} at {error.Line}:{error.Column}");
        return Print(program);
    }

    /// <summary>Parses a program that must fail and gives "message @line:column".</summary>
    public static string Error(string source)
    {
        if (Parser.TryParse(source, out var program, out var error))
            throw new XunitException($"'{source}' parses as {Print(program)}");
        return $"{error.Message} @{error.Line}:{error.Column}";
    }

    public static string Print(Program program)
    {
        var parts = new List<string>();
        if (program.Metadata is { } metadata)
            parts.Add($"(module {ValueText.ToJson(metadata)})");
        foreach (var import in program.Imports)
        {
            var alias = import.Alias is null ? "" : import.IsData ? $" ${import.Alias}" : $" {import.Alias}";
            var meta = import.Metadata is { } value ? " " + ValueText.ToJson(value) : "";
            parts.Add($"({(import.Alias is null ? "include" : "import")} \"{import.Path}\"{alias}{meta})");
        }

        foreach (var definition in program.Definitions)
            parts.Add($"(def {Signature(definition)} {Print(definition.Body)})");
        if (program.Body is not null)
            parts.Add(Print(program.Body));
        return string.Join(" ", parts);
    }

    public static string Print(Node? node) => node switch
    {
        null => "_",
        Identity => ".",
        Literal literal => ValueText.ToJson(literal.Value),
        Template template => PrintTemplate(template),
        Format format => "@" + format.Name,
        Index index => $"(index {Print(index.Target)} {Print(index.Key)})",
        Slice slice => $"(slice {Print(slice.Target)} {Print(slice.From)} {Print(slice.To)})",
        Iterate iterate => $"(iter {Print(iterate.Target)})",
        Variable variable => "$" + variable.Name,
        Call call => call.Arguments.Length == 0 ? call.Name : $"({call.Name} {string.Join(" ", call.Arguments.Select(Print))})",
        Devlooped.Core.Syntax.Pipe pipe => $"(| {Print(pipe.Left)} {Print(pipe.Right)})",
        Comma comma => $"(, {Print(comma.Left)} {Print(comma.Right)})",
        Binary binary => $"({Symbol(binary.Op)} {Print(binary.Left)} {Print(binary.Right)})",
        And and => $"(and {Print(and.Left)} {Print(and.Right)})",
        Or or => $"(or {Print(or.Left)} {Print(or.Right)})",
        Alternative alternative => $"(// {Print(alternative.Left)} {Print(alternative.Right)})",
        Negate negate => $"(neg {Print(negate.Operand)})",
        ArrayExpr array => array.Body is null ? "(array)" : $"(array {Print(array.Body)})",
        ObjectExpr obj => obj.Entries.Length == 0 ? "(object)"
            : $"(object {string.Join(" ", obj.Entries.Select(entry => $"({Print(entry.Key)} {Print(entry.Value)})"))})",
        If cond => $"(if {Print(cond.Condition)} {Print(cond.Then)} {Print(cond.Else)})",
        Try attempt => attempt.Handler is null ? $"(try {Print(attempt.Body)})" : $"(try {Print(attempt.Body)} {Print(attempt.Handler)})",
        Reduce reduce => $"(reduce {Print(reduce.Source)} {Print(reduce.Patterns)} {Print(reduce.Init)} {Print(reduce.Update)})",
        Foreach each => $"(foreach {Print(each.Source)} {Print(each.Patterns)} {Print(each.Init)} {Print(each.Update)} {Print(each.Extract)})",
        Bind bind => $"(as {Print(bind.Source)} {Print(bind.Patterns)} {Print(bind.Body)})",
        FuncDef def => $"(def {Signature(def.Definition)} {Print(def.Definition.Body)} {Print(def.Rest)})",
        Label label => $"(label ${label.Name} {Print(label.Body)})",
        Break brk => $"(break ${brk.Name})",
        _ => throw new XunitException($"Unknown node {node.Kind}"),
    };

    static string Signature(Definition definition) => definition.Params.Length == 0
        ? definition.Name
        : $"{definition.Name}({string.Join(";", definition.Params.Select(p => (p.IsVariable ? "$" : "") + p.Name))})";

    static string PrintTemplate(Template template)
    {
        var builder = new StringBuilder("(str");
        if (template.Format is not null)
            builder.Append(" @").Append(template.Format);
        for (var i = 0; i < template.Parts.Length; i++)
            builder.Append(' ').Append(ValueText.ToJson(JqValue.FromString(template.Texts[i]))).Append(' ').Append(Print(template.Parts[i]));
        return builder.Append(' ').Append(ValueText.ToJson(JqValue.FromString(template.Texts[^1]))).Append(')').ToString();
    }

    static string Print(IReadOnlyList<Pattern> patterns)
        => patterns.Count == 1 ? Print(patterns[0]) : $"(?// {string.Join(" ", patterns.Select(Print))})";

    static string Print(Pattern? pattern) => pattern switch
    {
        null => "_",
        VariablePattern variable => "$" + variable.Name,
        ArrayPattern array => $"[{string.Join(" ", array.Elements.Select(Print))}]",
        ObjectPattern obj => $"{{{string.Join(" ", obj.Entries.Select(entry => $"({Print(entry.Key)} {(entry.Variable is null ? "_" : "$" + entry.Variable)} {Print(entry.Value)})"))}}}",
        _ => throw new XunitException($"Unknown pattern {pattern.Kind}"),
    };

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
