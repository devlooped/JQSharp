// Prints a bound program with its hops and slots, for the resolver tests (dotnet/Tests/Core/Binding/BoundPrinter.cs).

import { type Binder, type BoundProgram, type Node, NodeKind, type Pattern, PatternKind, type Template } from "../../src/binding/bound.js";
import { Prelude } from "../../src/binding/prelude.js";
import { Resolver } from "../../src/binding/resolver.js";
import { BinaryOp } from "../../src/syntax/ast.js";
import { SyntaxError } from "../../src/syntax/syntaxError.js";
import * as ValueText from "../../src/values/valueText.js";

function preludeCount(): number {
  return Prelude.instance.functions.length;
}

/** The tree of a program that must bind. */
export function bind(source: string): string {
  const program = Resolver.tryCompile(source);
  if (program instanceof SyntaxError) throw new Error(`'${source}' does not bind: ${program.message} at ${program.line}:${program.column}`);
  return print(program);
}

/** The error of a program that must not bind: `message @line:column`. */
export function error(source: string): string {
  const program = Resolver.tryCompile(source);
  if (!(program instanceof SyntaxError)) throw new Error(`'${source}' binds as ${print(program)}`);
  return `${program.message} @${program.line}:${program.column}`;
}

export function print(program: BoundProgram): string {
  const parts: string[] = [];
  for (let i = preludeCount(); i < program.functions.length; i++) parts.push(`(def ${name(program, i)} ${printNode(program, program.functions[i]!.body)})`);
  parts.push(printNode(program, program.body));
  return parts.join(" ");
}

function name(program: BoundProgram, index: number): string {
  const func = program.functions[index]!;
  return index < preludeCount() ? `prelude:${func}` : `${func}#${index - preludeCount()}`;
}

function printNode(program: BoundProgram, node: Node | null): string {
  const P = (child: Node | null): string => printNode(program, child);
  const all = (children: readonly Node[]): string => children.map((child) => " " + P(child)).join("");
  if (node === null) return "_";
  switch (node.kind) {
    case NodeKind.Identity:
      return ".";
    case NodeKind.Literal:
      return ValueText.toJson(node.value);
    case NodeKind.Template:
      return printTemplate(node, P);
    case NodeKind.Format:
      return "@" + node.name;
    case NodeKind.Index:
      return `(index ${P(node.target)} ${P(node.key)})`;
    case NodeKind.Slice:
      return `(slice ${P(node.target)} ${P(node.from)} ${P(node.to)})`;
    case NodeKind.Iterate:
      return `(iter ${P(node.target)})`;
    case NodeKind.Variable:
      return `$${node.hops}.${node.slot}`;
    case NodeKind.HostVariable:
      return "$$" + node.name;
    case NodeKind.EnvVariable:
      return "$ENV";
    case NodeKind.CallFunction:
      return node.arguments.length === 0 ? `${name(program, node.function)}^${node.hops}` : `(${name(program, node.function)}^${node.hops}${all(node.arguments)})`;
    case NodeKind.CallParameter:
      return `param^${node.hops}.${node.slot}`;
    case NodeKind.CallNative:
      return node.arguments.length === 0 ? `native:${node.native}` : `(native:${node.native}${all(node.arguments)})`;
    case NodeKind.Pipe:
      return `(| ${P(node.left)} ${P(node.right)})`;
    case NodeKind.Comma:
      return `(,${all(node.items)})`;
    case NodeKind.Binary:
      return `(${symbol(node.op)} ${P(node.left)} ${P(node.right)})`;
    case NodeKind.And:
      return `(and ${P(node.left)} ${P(node.right)})`;
    case NodeKind.Or:
      return `(or ${P(node.left)} ${P(node.right)})`;
    case NodeKind.Alternative:
      return `(// ${P(node.left)} ${P(node.right)})`;
    case NodeKind.Negate:
      return `(neg ${P(node.operand)})`;
    case NodeKind.ArrayExpr:
      return node.body === null ? "(array)" : `(array ${P(node.body)})`;
    case NodeKind.ObjectExpr:
      return node.entries.length === 0 ? "(object)" : `(object ${node.entries.map((entry) => `(${P(entry.key)} ${P(entry.value)})`).join(" ")})`;
    case NodeKind.If:
      return `(if ${P(node.condition)} ${P(node.then)} ${P(node.else)})`;
    case NodeKind.Try:
      return node.handler === null ? `(try ${P(node.body)})` : `(try ${P(node.body)} ${P(node.handler)})`;
    case NodeKind.Reduce:
      return `(reduce ${P(node.source)} ${printBinder(node.binder, P)} ${P(node.init)} ${P(node.update)})`;
    case NodeKind.Foreach:
      return `(foreach ${P(node.source)} ${printBinder(node.binder, P)} ${P(node.init)} ${P(node.update)} ${P(node.extract)})`;
    case NodeKind.Bind:
      return `(as ${P(node.source)} ${printBinder(node.binder, P)} ${P(node.body)})`;
    case NodeKind.Label:
      return `(label ${P(node.body)})`;
    case NodeKind.Break:
      return `(break ^${node.hops})`;
  }
}

function printTemplate(template: Template, P: (node: Node | null) => string): string {
  let text = "(str";
  if (template.format !== null) text += " @" + template.format;
  for (let i = 0; i < template.parts.length; i++) text += " " + ValueText.toJson(template.texts[i]!) + " " + P(template.parts[i]!);
  return text + " " + ValueText.toJson(template.texts[template.texts.length - 1]!) + ")";
}

function printBinder(binder: Binder, P: (node: Node | null) => string): string {
  return binder.patterns.length === 1 ? printPattern(binder.patterns[0]!, P) : `(?// ${binder.patterns.map((p) => printPattern(p, P)).join(" ")})`;
}

function printPattern(pattern: Pattern | null, P: (node: Node | null) => string): string {
  if (pattern === null) return "_";
  switch (pattern.kind) {
    case PatternKind.Variable:
      return slot(pattern.slot);
    case PatternKind.Array:
      return `[${pattern.elements.map((p) => printPattern(p, P)).join(" ")}]`;
    case PatternKind.Object:
      return `{${pattern.entries.map((entry) => `(${P(entry.key)} ${entry.slot < 0 ? "_" : slot(entry.slot)} ${printPattern(entry.value, P)})`).join(" ")}}`;
  }
}

function slot(index: number): string {
  return index < 0 ? "$_" : "$" + index;
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
