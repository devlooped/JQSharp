import { type IModuleSource, ModuleError, ModuleKind } from "../binding/modules.js";
import * as BindingMessages from "../binding/resolver.js";
import type { JqResolver } from "../jqResolver.js";
import { toJson } from "../json/jsonValueAdapter.js";
import * as CodePoints from "../values/codePoints.js";
import type { JqValue } from "../values/jqValue.js";

/**
 * A public JqResolver as the module source of the core (spec §9.11, §12): what the resolver
 * throws becomes the error of the directive.
 */
export class ResolverSource implements IModuleSource {
  constructor(private readonly resolver: JqResolver) {}

  tryResolve(path: string, kind: ModuleKind, metadata: JqValue | undefined, from: string | null): string | ModuleError {
    let identity: string | null;
    try {
      identity = this.resolver.getCanonicalPath(path, kind === ModuleKind.Data ? "data" : "code", metadata === undefined ? undefined : toJson(metadata), from);
    } catch (e) {
      return new ModuleError(messageOf(e));
    }

    return identity === null || identity.length === 0 ? new ModuleError(BindingMessages.moduleNotFound(path)) : identity;
  }

  tryRead(identity: string): string | ModuleError {
    try {
      return CodePoints.sanitize(this.resolver.open(identity));
    } catch (e) {
      return new ModuleError(messageOf(e));
    }
  }
}

function messageOf(thrown: unknown): string {
  return thrown instanceof Error ? thrown.message : String(thrown);
}
