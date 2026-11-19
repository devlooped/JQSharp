import type { JsonValue } from "./json/jsonValueAdapter.js";

/** Whether an `import` asks for a code module (`.jq`) or a data module (`import "p" as $name;`, JSON values). */
export type JqModuleKind = "code" | "data";

/**
 * Finds the modules of `import` and `include` directives and of `modulemeta`: maps a path, as
 * written, to a canonical path (the module's identity), then opens it.
 *
 * A module is read once per canonical path while an expression is parsed. `modulemeta` calls
 * the resolver while expressions are evaluated. A resolver that throws makes the directive a
 * parse error (and `modulemeta` a runtime error) with the message of what it threw.
 */
export interface JqResolver {
  /**
   * The canonical path of the module `path` of the given kind, imported with `metadata` (the
   * directive's metadata object, or undefined) from the module at `fromPath` (a canonical path,
   * or null for the expression itself); null when there is no such module. The same canonical
   * path means the same module.
   */
  getCanonicalPath(path: string, kind: JqModuleKind, metadata: JsonValue | undefined, fromPath: string | null): string | null;

  /** The text of the module at a canonical path that `getCanonicalPath` returned. */
  open(canonicalPath: string): string;
}
