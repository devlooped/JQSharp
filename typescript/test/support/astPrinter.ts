// Prints a parsed program as an S-expression, for the parser tests (dotnet/Tests/Core/Syntax/AstPrinter.cs).

import { BinaryOp, type Definition, type Node, NodeKind, type Pattern, PatternKind, type Program, type Template } from "../../src/syntax/ast.js";
import { Parser } from "../../src/syntax/parser.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import * as ValueText from "../../src/values/valueText.js";

/** The tree of a program that must parse. */
export function parse(source: string): string {
  const program = Parser.tryParse(source);
  if (program instanceof SyntaxError) throw new Error(`'${source}' does not parse: ${program.message} at ${program.line}:${program.column}`);
  return print(program);
}

/** The error of a program that must not parse: `message @line:column`. */
export function error(source: string): string {
  const program = Parser.tryParse(source);
  if (!(program instanceof SyntaxError)) throw new Error(`'${source}' parses as ${print(program)}`);
  return `${program.message} @${program.line}:${program.column}`;
}

export function print(program: Program): string {
  const parts: string[] = [];
  if (program.metadata !== undefined) parts.push(`(module ${ValueText.toJson(program.metadata)})`);
  for (const directive of program.imports) {
    const alias = directive.alias === null ? "" : directive.isData ? ` $${directive.alias}` : ` ${directive.alias}`;
    const meta = directive.metadata === undefined ? "" : " " + ValueText.toJson(directive.metadata);
    parts.push(`(${directive.alias === null ? "include" : "import"} "${directive.path}"${alias}${meta})`);
  }

  for (const definition of program.definitions) parts.push(`(def ${signature(definition)} ${printNode(definition.body)})`);
  if (program.body !== null) parts.push(printNode(program.body));
  return parts.join(" ");
}

export function printNode(node: Node | null): string {
  if (node === null) return "_";
  switch (node.kind) {
    case NodeKind.Identity:
      return ".";
    case NodeKind.Literal:
      return ValueText.toJson(node.value);
    case NodeKind.Template:
      return printTemplate(node);
    case NodeKind.Format:
      return "@" + node.name;
    case NodeKind.Index:
      return `(index ${printNode(node.target)} ${printNode(node.key)})`;
    case NodeKind.Slice:
      return `(slice ${printNode(node.target)} ${printNode(node.from)} ${printNode(node.to)})`;
    case NodeKind.Iterate:
      return `(iter ${printNode(node.target)})`;
    case NodeKind.Variable:
      return "$" + node.name;
    case NodeKind.Call:
      return node.arguments.length === 0 ? node.name : `(${node.name} ${node.arguments.map(printNode).join(" ")})`;
    case NodeKind.Pipe:
      return `(| ${printNode(node.left)} ${printNode(node.right)})`;
    case NodeKind.Comma:
      return `(, ${printNode(node.left)} ${printNode(node.right)})`;
    case NodeKind.Binary:
      return `(${symbol(node.op)} ${printNode(node.left)} ${printNode(node.right)})`;
    case NodeKind.And:
      return `(and ${printNode(node.left)} ${printNode(node.right)})`;
    case NodeKind.Or:
      return `(or ${printNode(node.left)} ${printNode(node.right)})`;
    case NodeKind.Alternative:
      return `(// ${printNode(node.left)} ${printNode(node.right)})`;
    case NodeKind.Negate:
      return `(neg ${printNode(node.operand)})`;
    case NodeKind.ArrayExpr:
      return node.body === null ? "(array)" : `(array ${printNode(node.body)})`;
    case NodeKind.ObjectExpr:
      return node.entries.length === 0 ? "(object)" : `(object ${node.entries.map((entry) => `(${printNode(entry.key)} ${printNode(entry.value)})`).join(" ")})`;
    case NodeKind.If:
      return `(if ${printNode(node.condition)} ${printNode(node.then)} ${printNode(node.else)})`;
    case NodeKind.Try:
      return node.handler === null ? `(try ${printNode(node.body)})` : `(try ${printNode(node.body)} ${printNode(node.handler)})`;
    case NodeKind.Reduce:
      return `(reduce ${printNode(node.source)} ${printPatterns(node.patterns)} ${printNode(node.init)} ${printNode(node.update)})`;
    case NodeKind.Foreach:
      return `(foreach ${printNode(node.source)} ${printPatterns(node.patterns)} ${printNode(node.init)} ${printNode(node.update)} ${printNode(node.extract)})`;
    case NodeKind.Bind:
      return `(as ${printNode(node.source)} ${printPatterns(node.patterns)} ${printNode(node.body)})`;
    case NodeKind.FuncDef:
      return `(def ${signature(node.definition)} ${printNode(node.definition.body)} ${printNode(node.rest)})`;
    case NodeKind.Label:
      return `(label $${node.name} ${printNode(node.body)})`;
    case NodeKind.Break:
      return `(break $${node.name})`;
  }
}

function signature(definition: Definition): string {
  return definition.params.length === 0 ? definition.name : `${definition.name}(${definition.params.map((p) => (p.isVariable ? "$" : "") + p.name).join(";")})`;
}

function printTemplate(template: Template): string {
  let text = "(str";
  if (template.format !== null) text += " @" + template.format;
  for (let i = 0; i < template.parts.length; i++) text += " " + ValueText.toJson(template.texts[i]!) + " " + printNode(template.parts[i]!);
  return text + " " + ValueText.toJson(template.texts[template.texts.length - 1]!) + ")";
}

function printPatterns(patterns: readonly Pattern[]): string {
  return patterns.length === 1 ? printPattern(patterns[0]!) : `(?// ${patterns.map(printPattern).join(" ")})`;
}

function printPattern(pattern: Pattern | null): string {
  if (pattern === null) return "_";
  switch (pattern.kind) {
    case PatternKind.Variable:
      return "$" + pattern.name;
    case PatternKind.Array:
      return `[${pattern.elements.map(printPattern).join(" ")}]`;
    case PatternKind.Object:
      return `{${pattern.entries.map((entry) => `(${printNode(entry.key)} ${entry.variable === null ? "_" : "$" + entry.variable} ${printPattern(entry.value)})`).join(" ")}}`;
  }
}

function symbol(op: BinaryOp): string {
  switch (op) {
    case BinaryOp.Add:
      return "+";
    case BinaryOp.Subtract:
      return "-";
    case BinaryOp.Multiply:
      return "*";
    case BinaryOp.Divide:
      return "/";
    case BinaryOp.Modulo:
      return "%";
    case BinaryOp.Equal:
      return "==";
    case BinaryOp.NotEqual:
      return "!=";
    case BinaryOp.Less:
      return "<";
    case BinaryOp.LessEqual:
      return "<=";
    case BinaryOp.Greater:
      return ">";
    default:
      return ">=";
  }
}
