import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { type IModuleSource, ModuleError, ModuleKind } from "../../src/binding/modules.js";
import type { JqValue } from "../../src/values/jqValue.js";

/**
 * A module source over a directory, with the rule of spec/tests/README.md: the path `p`
 * imported from a file names `p.jq` (code) or `p.json` (data) in that file's directory, the main
 * program's directory being the root. Identities are paths relative to the root.
 */
export class DirectoryModules implements IModuleSource {
  /** How many module files were read. */
  reads = 0;

  constructor(private readonly root: string) {}

  tryResolve(path: string, kind: ModuleKind, _metadata: JqValue | undefined, from: string | null): string | ModuleError {
    const directory = from === null ? "" : dirname(from);
    const identity = join(directory === "." ? "" : directory, path + (kind === ModuleKind.Code ? ".jq" : ".json")).replaceAll("\\", "/");
    return existsSync(join(this.root, identity)) ? identity : new ModuleError(`module not found: ${path}`);
  }

  tryRead(identity: string): string | ModuleError {
    this.reads++;
    return readFileSync(join(this.root, identity), "utf8");
  }
}

/** A module source over a map of identities and texts; a path is its identity without the extension. */
export class MemoryModules implements IModuleSource {
  constructor(private readonly modules: ReadonlyMap<string, string>) {}

  tryResolve(path: string, kind: ModuleKind): string | ModuleError {
    const identity = path + (kind === ModuleKind.Code ? ".jq" : ".json");
    return this.modules.has(identity) ? identity : new ModuleError(`module not found: ${path}`);
  }

  tryRead(identity: string): string | ModuleError {
    return this.modules.get(identity) ?? new ModuleError(`module not found: ${identity}`);
  }
}
