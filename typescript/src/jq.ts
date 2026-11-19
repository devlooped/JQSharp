import { Resolver } from "./binding/resolver.js";
import { ResolverSource } from "./host/resolverSource.js";
import { JqParseError } from "./jqError.js";
import { JqExpression, type JqVariables } from "./jqExpression.js";
import type { JqOptions } from "./jqOptions.js";
import type { JsonInput, JsonValue } from "./json/jsonValueAdapter.js";
import { SyntaxError } from "./syntax/syntaxError.js";
import * as CodePoints from "./values/codePoints.js";

const defaults: JqOptions = {};

/** Parses and evaluates jq expressions (the jqlite profile of jq 1.8) on plain JSON values. */
export class Jq {
  private constructor() {}

  /**
   * Parses and binds `expression`, loading its modules through the options' resolver, into an
   * expression to evaluate any number of times. An empty expression is `.`.
   *
   * Throws a JqParseError for a syntax or binding error (an unknown function, label or module, a
   * cycle of imports), with its line and column.
   */
  static parse(expression: string, options: JqOptions = defaults): JqExpression {
    if (typeof expression !== "string") throw new TypeError("The expression must be a string.");
    const modules = options.resolver === undefined ? null : new ResolverSource(options.resolver);
    const program = Resolver.tryCompile(CodePoints.sanitize(expression), true, modules);
    if (program instanceof SyntaxError) {
      const where = program.module === null ? "" : `${program.module}, `;
      throw new JqParseError(`${program.message} at ${where}line ${program.line}, column ${program.column}`, program.line, program.column);
    }

    return new JqExpression(program, options);
  }

  /**
   * Parses `expression` with the default options and evaluates it on `input`, returning all its
   * results; `variables` as for `JqExpression.evaluate`.
   */
  static evaluate(expression: string, input: JsonInput, variables?: JqVariables): JsonValue[] {
    return Jq.parse(expression).evaluate(input, variables);
  }

  /**
   * Evaluates `expression` (parsed with the default options when it is a string) on each value of
   * `inputs` in turn (a JSON Lines stream, for example), yielding the results of each; the
   * results of one input are computed before they are yielded. Each value is evaluated alone, as
   * by `JqExpression.evaluate`: `halt` ends only the evaluation of its value.
   */
  static async *evaluateAsync(expression: string | JqExpression, inputs: AsyncIterable<JsonInput> | Iterable<JsonInput>, variables?: JqVariables): AsyncGenerator<JsonValue, void, undefined> {
    const parsed = typeof expression === "string" ? Jq.parse(expression) : expression;
    for await (const input of inputs) yield* parsed.evaluate(input, variables);
  }
}
