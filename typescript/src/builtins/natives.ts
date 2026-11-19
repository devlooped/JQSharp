/** The groups of spec §9: `Modules` is the optional group of §9.11. */
export const enum NativeGroup {
  Core,
  Modules,
}

/**
 * One native builtin of spec §9: identity, catalogue position, and how it takes its arguments.
 * The catalogue is plain data, which the resolver binds calls to; the interpreter finds the
 * implementation by `index` in NativeTable.
 *
 * By default a native takes value arguments (§9.1): each argument is evaluated on the input and
 * the native gives one output, or an error, per combination. `takesClosures` marks the entries
 * of §9 that say otherwise: they receive their arguments unevaluated and decide what to
 * evaluate, in which mode, and how many outputs to give.
 */
export class Native {
  constructor(
    readonly name: string,
    readonly arity: number,
    readonly group: NativeGroup,
    readonly index: number,
    readonly takesClosures: boolean,
    /**
     * A native implementation of a builtin that §9 defines in the prelude, for speed (§9.1
     * allows it when results, errors and paths are identical): calls that would bind to the
     * prelude definition bind to the native instead (Resolver). It is not one of the natives of §9.
     */
    readonly overridesPrelude: boolean = false,
    /**
     * The arguments that must be single for a call to be single (spec §7.7), or null when a
     * call never is. A native with value arguments gives one output per combination of them:
     * all of them. A native that takes closures says it in `singles`.
     */
    readonly singleWhen: readonly number[] | null = null,
  ) {}

  /** Internal builtins (a name starting with `_`) are callable but not listed by `builtins` (§9.1). */
  get isInternal(): boolean {
    return this.name.startsWith("_");
  }

  toString(): string {
    return `${this.name}/${this.arity}`;
  }
}

function key(name: string, arity: number): string {
  return `${name}/${arity}`;
}

// §7.7: the natives that take closures and yet give at most one output, when these arguments
// are single (none: always). The two overrides are single when their prelude definitions are.
const singles: readonly (readonly [signature: string, args: readonly number[]])[] = [
  ["empty/0", []], ["halt/0", []], ["halt_error/1", []], ["last/1", []], ["getpath/1", [0]], ["until/2", [0, 1]],
  ["_modify/2", []], ["_assign/2", [1]],
];

function singleWhen(signature: string, takesClosures: boolean): readonly number[] | null {
  if (takesClosures) {
    for (const [single, args] of singles) {
      if (single === signature) return args;
    }

    return null;
  }

  const all: number[] = [];
  for (let slot = 0; slot < Number(signature.substring(signature.indexOf("/") + 1)); slot++) all.push(slot);
  return all;
}

function add(natives: Native[], signatures: readonly string[], group: NativeGroup, closures: readonly string[]): void {
  for (const signature of signatures) {
    const slash = signature.indexOf("/");
    const takesClosures = closures.includes(signature);
    natives.push(
      new Native(signature.substring(0, slash), Number(signature.substring(slash + 1)), group, natives.length, takesClosures, false, singleWhen(signature, takesClosures)),
    );
  }
}

function build(core: readonly string[], modules: readonly string[], closures: readonly string[]): Native[] {
  const natives: Native[] = [];
  add(natives, core, NativeGroup.Core, closures);
  add(natives, modules, NativeGroup.Modules, closures);
  return natives;
}

// The overrides of prelude builtins, after the natives of §9; they take closures.
function withOverrides(overrides: readonly string[], natives: Native[]): readonly Native[] {
  for (const signature of overrides) {
    const slash = signature.indexOf("/");
    natives.push(new Native(signature.substring(0, slash), Number(signature.substring(slash + 1)), NativeGroup.Core, natives.length, true, true, singleWhen(signature, true)));
  }

  return natives;
}

/**
 * The native catalogue: every builtin that spec §9 marks N, in the order of the spec. With the
 * definitions of `spec/prelude.jq` it is the whole builtin set, and what `builtins/0` lists.
 */
export const All: readonly Native[] = withOverrides(
  ["_modify/2", "_assign/2"],
  build(
    [
      // §9.2 Core
      "empty/0", "error/0", "type/0", "length/0", "utf8bytelength/0", "builtins/0", "halt/0", "halt_error/1", "last/1",
      "debug/0", "stderr/0",
      // §9.3 Types and selection
      "infinite/0", "nan/0", "isinfinite/0", "isnan/0", "isnormal/0",
      // §9.4 Objects, arrays and collections
      "keys/0", "keys_unsorted/0", "has/1", "contains/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
      "sort/0", "_sort_by_impl/1", "_group_by_impl/1", "_min_by_impl/1", "_max_by_impl/1", "unique/0", "min/0", "max/0", "bsearch/1",
      // §9.5 Paths
      "path/1", "getpath/1", "setpath/2", "delpaths/1",
      // §9.6 Strings
      "tostring/0", "tojson/0", "fromjson/0", "tonumber/0", "toboolean/0", "format/1", "explode/0", "implode/0", "ascii_downcase/0", "ascii_upcase/0",
      "startswith/1", "endswith/1", "trim/0", "ltrim/0", "rtrim/0", "split/1", "_strindices/1", "test/2", "match/2",
      // §9.7 Dates
      "now/0", "gmtime/0", "localtime/0", "mktime/0", "strftime/1", "strflocaltime/1", "strptime/1",
      // §9.8 Math: exactly specified, library functions, defined by formula
      "floor/0", "ceil/0", "trunc/0", "round/0", "nearbyint/0", "rint/0", "fabs/0", "sqrt/0", "fmod/2", "remainder/2", "drem/2", "fma/3",
      "fmin/2", "fmax/2", "fdim/2", "copysign/2", "nextafter/2", "nexttoward/2", "ldexp/2", "scalbln/2", "scalb/2", "logb/0",
      "significand/0", "frexp/0", "modf/0",
      "acos/0", "asin/0", "atan/0", "acosh/0", "asinh/0", "atanh/0", "cbrt/0", "cos/0", "cosh/0", "exp/0", "exp2/0",
      "exp10/0", "expm1/0", "log/0", "log10/0", "log1p/0", "log2/0", "sin/0", "sinh/0", "tan/0", "tanh/0", "atan2/2", "pow/2", "hypot/2",
      "erf/0", "erfc/0", "tgamma/0", "lgamma/0", "gamma/0", "lgamma_r/0", "j0/0", "j1/0", "y0/0", "y1/0", "jn/2", "yn/2",
    ],
    [
      // §9.11 Modules
      "modulemeta/0",
    ],
    [
      // §9.2, §9.4, §9.5, §9.6: no output, a signal, generators, path-capable, many outputs.
      "empty/0", "halt/0", "halt_error/1", "last/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
      "path/1", "getpath/1", "match/2",
    ],
  ),
);

const byKey = new Map<string, Native>(All.map((native) => [key(native.name, native.arity), native]));

/** The native `name`/`arity`, or null. */
export function find(name: string, arity: number): Native | null {
  return byKey.get(key(name, arity)) ?? null;
}

/** The native that overrides the prelude builtin `name`/`arity`, or null. */
export function override(name: string, arity: number): Native | null {
  const native = find(name, arity);
  return native !== null && native.overridesPrelude ? native : null;
}
