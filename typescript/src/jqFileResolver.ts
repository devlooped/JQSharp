// The one file of the package that needs Node: it is exported from "jqlite/node", not from the
// main entry, which runs in a browser too.

import { existsSync, readFileSync, statSync } from "node:fs";
import { dirname, extname, resolve } from "node:path";
import type { JqModuleKind, JqResolver } from "./jqResolver.js";
import type { JsonValue } from "./json/jsonValueAdapter.js";

/**
 * A JqResolver over the file system: a path is relative to the directory of the importing
 * module, or to `baseDirectory` for the expression itself, and gets the extension `.jq` (code)
 * or `.json` (data) when it has none. The canonical path is the full file path.
 */
export class JqFileResolver implements JqResolver {
  /** The directory that the expression's own directives are relative to. */
  readonly baseDirectory: string;

  constructor(baseDirectory: string) {
    this.baseDirectory = resolve(baseDirectory);
  }

  getCanonicalPath(path: string, kind: JqModuleKind, _metadata: JsonValue | undefined, fromPath: string | null): string | null {
    const directory = fromPath === null ? this.baseDirectory : dirname(fromPath);
    let file = resolve(directory, path);
    if (extname(file) === "") file += kind === "data" ? ".json" : ".jq";
    return existsSync(file) && statSync(file).isFile() ? file : null;
  }

  open(canonicalPath: string): string {
    return readFileSync(canonicalPath, "utf8");
  }
}
