package com.devlooped.jqlite.builtins;

/**
 * One native builtin of spec §9: identity, catalogue position, and how it takes its arguments. The
 * catalogue is plain data, which the resolver binds calls to; the interpreter finds the
 * implementation by {@code index} in NativeTable. (In the reference, this record shares Natives.cs.)
 *
 * <p>By default a native takes value arguments (§9.1): each argument is evaluated on the input and
 * the native gives one output, or an error, per combination. {@code takesClosures} marks the entries
 * of §9 that say otherwise: they receive their arguments unevaluated and decide what to evaluate, in
 * which mode, and how many outputs to give.
 *
 * <p>{@code overridesPrelude}: a native implementation of a builtin that §9 defines in the prelude,
 * for speed (§9.1 allows it when results, errors and paths are identical): calls that would bind to
 * the prelude definition bind to the native instead (Resolver). It is not one of the natives of §9.
 *
 * <p>{@code singleWhen}: the arguments that must be single for a call to be single (spec §7.7), or
 * null when a call never is. A native with value arguments gives one output per combination of
 * them: all of them. A native that takes closures says it in {@link Natives}.
 */
public record Native(String name, int arity, NativeGroup group, int index, boolean takesClosures, boolean overridesPrelude, int[] singleWhen) {

    /** The groups of spec §9: {@code MODULES} is the optional group of §9.11. */
    public enum NativeGroup {
        CORE,
        MODULES,
    }

    /** Internal builtins (a name starting with {@code _}) are callable but not listed by {@code builtins} (§9.1). */
    public boolean isInternal() {
        return name.startsWith("_");
    }

    @Override
    public String toString() {
        return name + "/" + arity;
    }
}
