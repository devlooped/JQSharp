// The modules group of spec §9.11, which a port may leave out. Everything of it is here and in
// builtins/moduleNatives.ts, behind bindImports. A port without the group rejects `import`,
// `include` and `module` in its parser, so a program never has imports: it drops this file, the
// call of bindImports and the modules property of BoundProgram, and does not list modulemeta/0.

import type * as S from "../syntax/ast.js";
import { Parser } from "../syntax/parser.js";
import { SyntaxError } from "../syntax/syntaxError.js";
import { JqArray } from "../values/jqArray.js";
import { JqObject } from "../values/jqObject.js";
import { Fail, type JqValue } from "../values/jqValue.js";
import * as ValueText from "../values/valueText.js";
import * as BindingMessages from "./resolver.js";
import { Resolver } from "./resolver.js";

/** What an import asks for (§9.11): a code module or a data module. */
export const enum ModuleKind {
  Code,
  Data,
}

/** The failure of a module source: a message. */
export class ModuleError {
  constructor(readonly message: string) {}
}

/**
 * What the core needs from its host to load modules (§9.11, §12): the host decides where a
 * module comes from. Called while binding, and at run time by `modulemeta`.
 */
export interface IModuleSource {
  /**
   * Resolves the module `path` (as written in the directive) of the given kind, imported with
   * `metadata` (the directive's metadata object, or undefined), from the module `from` (its
   * identity, or null for the main program), to the module's identity: the same identity means
   * the same module. On failure, the error is a message naming the path.
   */
  tryResolve(path: string, kind: ModuleKind, metadata: JqValue | undefined, from: string | null): string | ModuleError;

  /** The source text of a module `tryResolve` found: read once per identity and binding. */
  tryRead(identity: string): string | ModuleError;
}

/**
 * The modules of a bound program, kept for `modulemeta`: the host's source and the description
 * of every module loaded while binding. Immutable after binding.
 */
export class ModuleTable {
  constructor(
    private readonly source: IModuleSource,
    private readonly descriptions: ReadonlyMap<string, JqValue>,
  ) {}

  /**
   * `modulemeta` of a path resolved as from the main program: the loaded module's description,
   * or, for a module the program does not import, the description of its parse.
   */
  tryDescribe(path: string): JqValue | ModuleError {
    const identity = this.source.tryResolve(path, ModuleKind.Code, undefined, null);
    if (identity instanceof ModuleError) return identity;
    const description = this.descriptions.get(identity);
    if (description !== undefined) return description;
    const text = this.source.tryRead(identity);
    if (text instanceof ModuleError) return text;
    const module = Parser.tryParseModule(text);
    if (module instanceof SyntaxError) return new ModuleError(BindingMessages.inModule(module.message, identity, module.line, module.column));

    return ModuleTable.describe(module);
  }

  /**
   * What `modulemeta` outputs for a module (§9.11): its metadata keys, then `deps` (per
   * directive: its metadata keys, then `as` unless it is an include, `is_data`, `relpath`), then
   * `defs`, each top-level `name/arity` once, by first definition.
   */
  static describe(module: S.Program): JqValue {
    const description = module.metadata === undefined ? JqObject.Empty : (module.metadata as JqObject);
    const deps: JqValue[] = [];
    for (const directive of module.imports) {
      let dep = directive.metadata === undefined ? JqObject.Empty : (directive.metadata as JqObject);
      if (directive.alias !== null) dep = dep.set("as", directive.alias);
      deps.push(dep.set("is_data", directive.isData).set("relpath", directive.path));
    }

    const defs: JqValue[] = [];
    const seen = new Set<string>();
    for (const definition of module.definitions) {
      const signature = `${definition.name}/${definition.params.length}`;
      if (!seen.has(signature)) {
        seen.add(signature);
        defs.push(signature);
      }
    }

    return description.set("deps", JqArray.fromOwned(deps)).set("defs", JqArray.fromOwned(defs));
  }
}

/** One export of a code module: a top-level name/arity with its function. */
export interface ModuleExport {
  readonly name: string;
  readonly arity: number;
  readonly function: number;
}

/** A loaded module (the reference's ModuleLoader.Module). */
export class LoadedModule {
  /** The identities the module's directives resolved to, in order. */
  readonly imports: string[] = [];

  /** A code module's exports once bound: each top-level name/arity with its function, the last definition winning. */
  readonly exports = new Map<string, ModuleExport>();

  // On the loading stack: importing it again is a cycle.
  loading = false;

  constructor(
    readonly identity: string,
    readonly kind: ModuleKind,
    /** A code module's parse. */
    readonly program: S.Program | null,
    /** A data module's values, as one array. */
    readonly data: JqValue,
  ) {}
}

// One frame of ModuleLoader.tryLoad: a module being loaded, its directives, the next one to
// resolve, and where the identities go.
interface LoadFrame {
  readonly module: LoadedModule | null;
  readonly imports: readonly S.Import[];
  readonly identities: string[];
  next: number;
}

/**
 * Loads the modules of one binding: resolves and parses each identity once, depth first from
 * the main program's directives, with an explicit stack (an import chain of any length is no
 * recursion), detecting cycles. Code modules are then bound one after the other, each after the
 * modules it imports, each in a scope of its own (Resolver). Loaded modules live as long as the
 * binding: a host that wants to share parses between programs caches in its source.
 */
export class ModuleLoader {
  private readonly modules = new Map<string, LoadedModule>();

  /** What the main program's directives resolved to, after tryLoad. */
  identities: string[] = [];

  /** The code modules to bind, each after those it imports, after tryLoad. */
  order: LoadedModule[] = [];

  constructor(readonly source: IModuleSource | null) {}

  get(identity: string): LoadedModule {
    return this.modules.get(identity)!;
  }

  /**
   * Loads what `imports` (the main program's directives) need, into `identities` and `order`;
   * returns the error, or null.
   */
  tryLoad(imports: readonly S.Import[]): SyntaxError | null {
    this.order = [];
    this.identities = [];
    const frames: LoadFrame[] = [{ module: null, imports, identities: this.identities, next: 0 }];
    while (frames.length > 0) {
      const frame = frames[frames.length - 1]!;
      const module = frame.module;
      const i = frame.next;
      if (i === frame.imports.length) {
        frames.pop();
        if (module !== null) {
          module.loading = false;
          this.order.push(module);
        }

        continue;
      }

      frame.next = i + 1;
      const directive = frame.imports[i]!;
      const kind = directive.isData ? ModuleKind.Data : ModuleKind.Code;
      const from = module === null ? null : module.identity;
      const identity = this.tryResolve(directive, kind, from);
      if (identity instanceof ModuleError) return error(identity.message, directive.position, from);

      frame.identities.push(identity);
      const loaded = this.modules.get(identity);
      if (loaded !== undefined) {
        if (loaded.loading) return error(BindingMessages.circularImport(identity), directive.position, from);
        continue;
      }

      const text = this.source!.tryRead(identity);
      if (text instanceof ModuleError) return error(text.message, directive.position, from);

      if (kind === ModuleKind.Data) {
        const values = ValueText.tryParseSequence(text);
        if (values instanceof Fail) return error(BindingMessages.invalidData(identity, values.error as string), directive.position, from);
        this.modules.set(identity, new LoadedModule(identity, kind, null, values));
        continue;
      }

      const program = Parser.tryParseModule(text);
      if (program instanceof SyntaxError) return new SyntaxError(program.message, program.line, program.column, identity);

      const code = new LoadedModule(identity, kind, program, null);
      code.loading = true;
      this.modules.set(identity, code);
      frames.push({ module: code, imports: program.imports, identities: code.imports, next: 0 });
    }

    return null;
  }

  /** The descriptions of the code modules loaded, for `modulemeta`; null without a source. */
  table(): ModuleTable | null {
    if (this.source === null) return null;
    const descriptions = new Map<string, JqValue>();
    for (const module of this.modules.values()) {
      if (module.program !== null) descriptions.set(module.identity, ModuleTable.describe(module.program));
    }

    return new ModuleTable(this.source, descriptions);
  }

  // Without a source no module exists.
  private tryResolve(directive: S.Import, kind: ModuleKind, from: string | null): string | ModuleError {
    if (this.source !== null) return this.source.tryResolve(directive.path, kind, directive.metadata, from);
    return new ModuleError(BindingMessages.moduleNotFound(directive.path));
  }
}

function error(message: string, position: S.Position, module: string | null): SyntaxError {
  return new SyntaxError(message, position.line, position.column, module);
}

// The part of Resolver that the reference keeps in Modules.cs (a partial class).

/**
 * Where modules enter a scope (§9.11), before the top-level definitions: the main program loads
 * and binds every module it needs first. Each directive then adds its module's exports as
 * definitions at that point: `alias::f` for `import`, `f` for `include`, so that later
 * definitions and directives shadow them as usual; a data import adds the constants `$alias`
 * and `$alias::alias`.
 */
export function bindImports(resolver: Resolver, imports: readonly S.Import[]): boolean {
  if (imports.length === 0) return true;

  const loader = resolver.loader!;
  let identities: string[];
  if (resolver.module !== null) {
    identities = resolver.module.imports;
  } else {
    const failure = loader.tryLoad(imports);
    if (failure !== null) {
      resolver.error ??= failure;
      return false;
    }

    identities = loader.identities;
    for (const code of loader.order) {
      if (!bindModule(resolver, code)) return false;
    }
  }

  for (let i = 0; i < imports.length; i++) {
    const directive = imports[i]!;
    const loaded = loader.get(identities[i]!);
    if (directive.isData) {
      resolver.constants.push(loaded.data);
      resolver.addConstant(directive.alias!, resolver.constants.length - 1);
      resolver.addConstant(`${directive.alias}::${directive.alias}`, resolver.constants.length - 1);
      continue;
    }

    const prefix = directive.alias === null ? "" : directive.alias + "::";
    for (const exported of loaded.exports.values()) resolver.addFunction(prefix + exported.name, exported.arity, exported.function);
  }

  return true;
}

// Binds a code module in a scope of its own and records its exports.
function bindModule(resolver: Resolver, code: LoadedModule): boolean {
  const module = Resolver.forModule(resolver, code);
  if (module.bindProgram(code.program!) === null) {
    resolver.error ??= module.error;
    return false;
  }

  for (const index of module.exports) {
    const func = resolver.functions[index]!;
    code.exports.set(`${func.name}/${func.arity}`, { name: func.name, arity: func.arity, function: index });
  }

  return true;
}
