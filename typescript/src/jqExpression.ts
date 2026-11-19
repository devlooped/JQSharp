import type { BoundProgram } from "./binding/bound.js";
import { HostClock, systemClock } from "./host/systemClock.js";
import { JqError, JqHaltError } from "./jqError.js";
import type { JqOptions } from "./jqOptions.js";
import { type JsonInput, type JsonValue, toJson, toValue } from "./json/jsonValueAdapter.js";
import { EvaluationOptions, type IClock, OutcomeKind } from "./runtime/evaluation.js";
import { Interpreter } from "./runtime/interpreter.js";
import * as CodePoints from "./values/codePoints.js";
import { JqObjectBuilder } from "./values/jqObjectBuilder.js";
import { Fail, type JqValue } from "./values/jqValue.js";
import * as ValueText from "./values/valueText.js";

/** Host variables, by name without `$`: a plain object or a Map. */
export type JqVariables = Readonly<Record<string, JsonInput>> | ReadonlyMap<string, JsonInput>;

/**
 * A jq expression parsed and bound once by `Jq.parse`, to evaluate on any number of inputs.
 *
 * Immutable: evaluations do not affect each other, and one may start while another runs (from a
 * result callback, for example), each with its own input and variables. Results are plain values
 * of their own (see jsonValueAdapter.ts for numbers and key order).
 */
export class JqExpression {
  private readonly environment: () => JqValue;
  private readonly clock: IClock;
  private readonly maxDepth: number;
  private readonly debug: ((value: JqValue) => void) | null;
  private readonly stderr: ((value: JqValue) => void) | null;

  /** @internal Use `Jq.parse`. */
  constructor(
    private readonly program: BoundProgram,
    options: JqOptions,
  ) {
    this.clock = new HostClock(options.clock ?? systemClock);
    this.maxDepth = options.maxDepth ?? 1000;
    const onDebug = options.debug;
    this.debug = onDebug === undefined ? null : (value) => onDebug(toJson(value));
    const onStderr = options.stderr;
    this.stderr = onStderr === undefined ? null : (value) => onStderr(toJson(value));
    if (options.environment !== undefined) {
      const snapshot = toObject(options.environment);
      this.environment = () => snapshot;
    } else {
      this.environment = processEnvironment;
    }
  }

  /**
   * Evaluates the expression on `input` and returns all its results, computed before the array
   * is returned.
   *
   * `variables` are the host variables, by name without `$` (`"root"` is `$root`): each name a jq
   * identifier, `ENV` excluded (a TypeError otherwise). A variable the expression uses but the
   * host does not give is the runtime error `$name is not defined`; a binding in the expression
   * shadows a host variable.
   *
   * Throws a JqError when an error reaches the top of the expression (a JqHaltError for
   * `halt_error`); no results are returned then.
   */
  evaluate(input: JsonInput, variables?: JqVariables): JsonValue[] {
    const results: JsonValue[] = [];
    this.run(toValue(input), variables, (value) => {
      results.push(toJson(value));
      return true;
    });
    return results;
  }

  /**
   * Evaluates the expression on `input`, giving each result to `onResult` as soon as it is
   * produced; the callback returns false to stop the evaluation (stopping is not an error).
   *
   * Throws a JqError when an error reaches the top of the expression, after the results produced
   * before it were given to `onResult`.
   */
  evaluateEach(input: JsonInput, onResult: (result: JsonValue) => boolean, variables?: JqVariables): void {
    this.run(toValue(input), variables, (value) => onResult(toJson(value)));
  }

  /**
   * Evaluates the expression on the JSON text `input` and returns the canonical JSON text of each
   * result (spec §5.1). Nothing goes through plain objects, so the order of object keys is
   * exactly jqlite's, whatever the keys are.
   *
   * Throws a JqError when `input` is not one JSON value, or as `evaluate` does.
   */
  evaluateText(input: string, variables?: JqVariables): string[] {
    const value = ValueText.tryParse(CodePoints.sanitize(input));
    if (value instanceof Fail) throw new JqError(message(value.error), toJson(value.error));
    const results: string[] = [];
    this.run(value, variables, (result) => {
      results.push(ValueText.toJson(result));
      return true;
    });
    return results;
  }

  private run(input: JqValue, variables: JqVariables | undefined, output: (value: JqValue) => boolean): void {
    const options = new EvaluationOptions();
    options.variables = variables === undefined ? null : convert(variables);
    options.environment = this.environment;
    options.clock = this.clock;
    options.maxDepth = this.maxDepth;
    options.debug = this.debug;
    options.stderr = this.stderr;
    const outcome = Interpreter.run(this.program, input, options, output);
    if (outcome.kind === OutcomeKind.Error) throw new JqError(message(outcome.value), toJson(outcome.value));
    if (outcome.kind === OutcomeKind.Halt && outcome.hasValue) throw new JqHaltError(message(outcome.value), toJson(outcome.value), outcome.code);
  }
}

// The message of an error that reaches the host (spec §10.1).
function message(value: JqValue): string {
  return typeof value === "string" ? value : ValueText.toJson(value) + " (not a string)";
}

// Validates the names (spec §12) and converts the values, once per evaluation.
function convert(variables: JqVariables): Map<string, JqValue> {
  const converted = new Map<string, JqValue>();
  const entries: Iterable<[string, JsonInput]> = variables instanceof Map ? variables : Object.entries(variables as Record<string, JsonInput>);
  for (const [name, value] of entries) {
    if (!isIdentifier(name)) throw new TypeError(`'${name}' is not a valid variable name: give a jq identifier without '$'.`);
    if (name === "ENV") throw new TypeError("$ENV cannot be a host variable: use the environment option.");
    converted.set(name, toValue(value));
  }

  return converted;
}

function isIdentifier(name: unknown): boolean {
  if (typeof name !== "string" || name.length === 0) return false;
  for (let i = 0; i < name.length; i++) {
    const c = name.charCodeAt(i);
    const letter = (c >= 0x41 && c <= 0x5a) || (c >= 0x61 && c <= 0x7a) || c === 0x5f;
    if (!letter && !(i > 0 && c >= 0x30 && c <= 0x39)) return false;
  }

  return true;
}

// The environment of the process where there is one (Node), read when $ENV is first used.
function processEnvironment(): JqValue {
  const process = (globalThis as { process?: { env?: Record<string, string | undefined> } }).process;
  return toObject(process?.env ?? {});
}

function toObject(variables: Readonly<Record<string, string | undefined>> | ReadonlyMap<string, string>): JqValue {
  const builder = new JqObjectBuilder();
  const entries: Iterable<[string, string | undefined]> = variables instanceof Map ? variables : Object.entries(variables as Record<string, string | undefined>);
  for (const [name, value] of entries) {
    if (value !== undefined) builder.set(CodePoints.sanitize(name), CodePoints.sanitize(String(value)));
  }

  return builder.toObject();
}
