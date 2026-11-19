import { Parser } from "../syntax/parser.js";
import { SyntaxError } from "../syntax/syntaxError.js";
import type { Function } from "./bound.js";
import { PreludeSource } from "./preludeSource.js";
import { Resolver } from "./resolver.js";

function key(name: string, arity: number): string {
  return `${name}/${arity}`;
}

/**
 * `spec/prelude.jq`, parsed and bound once against the natives, then shared by every program
 * (immutable). A program's function table starts with `functions`, so prelude calls keep their
 * indices in every program.
 *
 * Host-specific: where the text comes from. The reference reads a resource embedded in its
 * assembly; here it is the string constant of preludeSource.ts, which
 * scripts/generate-prelude.mjs writes from spec/prelude.jq at build time (test/prelude.test.ts
 * checks that the two are the same text).
 */
export class Prelude {
  private static loaded: Prelude | null = null;
  private static loadedWithoutOverrides: Prelude | null = null;

  /** The prelude, with the native overrides of its builtins (Native.overridesPrelude). */
  static get instance(): Prelude {
    return (Prelude.loaded ??= Prelude.load(PreludeSource));
  }

  /** The prelude exactly as written, every builtin bound to its definition: to check that the overrides change nothing. */
  static get withoutOverrides(): Prelude {
    return (Prelude.loadedWithoutOverrides ??= Prelude.load(PreludeSource, false));
  }

  private readonly visible = new Map<string, number>();

  private constructor(
    /** Every function of the prelude, nested ones included, in the order of definition. */
    readonly functions: readonly Function[],
    /** The top-level definitions: the builtins the prelude defines. */
    readonly definitions: readonly Function[],
  ) {
    for (const definition of definitions) this.visible.set(key(definition.name, definition.arity), functions.indexOf(definition));
  }

  /** The index in `functions` of the builtin `name`/`arity`, or null. */
  find(name: string, arity: number): number | null {
    return this.visible.get(key(name, arity)) ?? null;
  }

  /** Parses and binds a prelude source. The embedded prelude must bind: a failure is a defect of the build, not of a program. */
  static load(source: string, nativeOverrides: boolean = true): Prelude {
    const module = Parser.tryParseModule(source);
    const bound = module instanceof SyntaxError ? module : Resolver.tryBindPrelude(module, nativeOverrides);
    if (bound instanceof SyntaxError) throw new Error(`The prelude does not bind: ${bound.message} at ${bound.line}:${bound.column}`);
    return new Prelude(bound.functions, bound.definitions);
  }
}
