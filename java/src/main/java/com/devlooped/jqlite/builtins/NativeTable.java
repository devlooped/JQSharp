package com.devlooped.jqlite.builtins;

import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.runtime.Emit;
import com.devlooped.jqlite.runtime.Env;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.runtime.Path;
import com.devlooped.jqlite.runtime.Signal;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;

/**
 * The implementations of the native catalogue, by {@link Native#index}. Each family of natives
 * registers its entries; every entry of the catalogue has one, which the table checks once, when it
 * is built (a missing one is a defect of the build, not a run-time error).
 */
public final class NativeTable {
    /**
     * A native with value arguments (§9.1): called once per combination of its arguments' outputs,
     * it gives one output (a value) or an error ({@link Result.Err}).
     */
    @FunctionalInterface
    public interface ValueNative {
        Result call(Interpreter interpreter, JqValue input, JqValue[] arguments);
    }

    /**
     * A native that takes its arguments as closures ({@link Native#takesClosures}): it evaluates them
     * itself with {@code env}, pushes its outputs to {@code emit} (with their paths in path mode, when
     * it passes paths) and returns a signal, as Interpreter.eval does.
     */
    @FunctionalInterface
    public interface ClosureNative {
        Signal call(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit);
    }

    private static final ValueNative[] VALUES = new ValueNative[Natives.ALL.size()];
    private static final ClosureNative[] CLOSURES = new ClosureNative[Natives.ALL.size()];

    static {
        CoreNatives.register();
        LoopNatives.register();
        PathNatives.register();
        CollectionNatives.register();
        StringNatives.register();
        AssignNatives.register();
        ModuleNatives.register();
        Containment.register();
        MathNatives.register();
        DateNatives.register();
        RegexNatives.register();

        for (Native builtin : Natives.ALL) {
            if (!isImplemented(builtin)) {
                throw new IllegalStateException(builtin + " has no implementation");
            }
        }
    }

    private NativeTable() {
    }

    /** The implementation of a value native (one that does not take closures). */
    public static ValueNative value(Native builtin) {
        return VALUES[builtin.index()];
    }

    /** The implementation of a native that takes closures. */
    public static ClosureNative closure(Native builtin) {
        return CLOSURES[builtin.index()];
    }

    /** Whether {@code builtin} has an implementation. */
    public static boolean isImplemented(Native builtin) {
        return VALUES[builtin.index()] != null || CLOSURES[builtin.index()] != null;
    }

    // Registration checks the kind against the catalogue: a mismatch is a defect of the build.
    static void add(String name, int arity, ValueNative function) {
        VALUES[find(name, arity, false).index()] = function;
    }

    static void addClosure(String name, int arity, ClosureNative function) {
        CLOSURES[find(name, arity, true).index()] = function;
    }

    private static Native find(String name, int arity, boolean closures) {
        Native builtin = Natives.find(name, arity);
        if (builtin == null) {
            throw new IllegalStateException(name + "/" + arity + " is not in the native catalogue");
        }
        if (builtin.takesClosures() != closures) {
            throw new IllegalStateException(name + "/" + arity + " is registered with the wrong kind");
        }
        return builtin;
    }

    /** A value native's error. */
    static Result fail(JqValue message) {
        return Result.err(message);
    }
}
