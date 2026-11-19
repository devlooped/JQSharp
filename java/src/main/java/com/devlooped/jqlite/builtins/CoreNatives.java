package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.addClosure;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.binding.Bound.Function;
import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.binding.Prelude;
import com.devlooped.jqlite.runtime.Emit;
import com.devlooped.jqlite.runtime.Env;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.runtime.Path;
import com.devlooped.jqlite.runtime.Signal;
import com.devlooped.jqlite.values.CodePoints;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.Utf8;
import java.util.ArrayList;
import java.util.List;

/** §9.2 Core and §9.3 Types: the natives of the evaluation model itself and of its host (§12). */
final class CoreNatives {
    private CoreNatives() {
    }

    // builtins/0: every non-internal builtin, natives and prelude, sorted by code point.
    private static final class BuiltinsHolder {
        static final JqValue VALUE = listBuiltins();
    }

    static void register() {
        addClosure("empty", 0, (interpreter, env, arguments, input, path, emit) -> null);
        add("error", 0, (interpreter, input, arguments) -> fail(input));
        add("type", 0, (interpreter, input, arguments) -> JqValue.fromString(input.typeName()));
        add("length", 0, CoreNatives::length);
        add("utf8bytelength", 0, CoreNatives::utf8ByteLength);
        add("builtins", 0, (interpreter, input, arguments) -> BuiltinsHolder.VALUE);
        addClosure("halt", 0, (interpreter, env, arguments, input, path, emit) -> Signal.halt(null, 0));
        addClosure("halt_error", 1, CoreNatives::haltError);
        addClosure("last", 1, CoreNatives::last);
        add("debug", 0, (interpreter, input, arguments) -> {
            if (interpreter.debug() != null) {
                interpreter.debug().accept(input);
            }
            return input;
        });
        add("stderr", 0, (interpreter, input, arguments) -> {
            if (interpreter.stderr() != null) {
                interpreter.stderr().accept(input);
            }
            return input;
        });
        add("infinite", 0, (interpreter, input, arguments) -> JqValue.fromNumber(Double.POSITIVE_INFINITY));
        add("nan", 0, (interpreter, input, arguments) -> JqValue.fromNumber(Double.NaN));
        add("isinfinite", 0, (interpreter, input, arguments) -> JqValue.fromBool(input.kind() == JqKind.NUMBER && Double.isInfinite(input.asNumber())));
        add("isnan", 0, (interpreter, input, arguments) -> JqValue.fromBool(input.kind() == JqKind.NUMBER && Double.isNaN(input.asNumber())));
        add("isnormal", 0, (interpreter, input, arguments) -> JqValue.fromBool(input.kind() == JqKind.NUMBER && isNormal(input.asNumber())));
    }

    private static boolean isNormal(double number) {
        return !Double.isNaN(number) && !Double.isInfinite(number) && number != 0 && Math.abs(number) >= Double.MIN_NORMAL;
    }

    private static Result length(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        return switch (input.kind()) {
            case NULL -> JqValue.fromNumber(0);
            case NUMBER -> JqValue.fromNumber(Math.abs(input.asNumber()));
            case STRING -> JqValue.fromNumber(CodePoints.length(input.asString()));
            case ARRAY -> JqValue.fromNumber(input.asArray().count());
            case OBJECT -> JqValue.fromNumber(input.asObject().count());
            default -> fail(Errors.hasNoLength(input));
        };
    }

    private static Result utf8ByteLength(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        return input.kind() == JqKind.STRING
            ? JqValue.fromNumber(Utf8.byteCount(input.asString()))
            : fail(Errors.onlyStringsHaveUtf8Length(input));
    }

    // halt_error(c): halts with the input and trunc(c) for the first output c.
    private static Signal haltError(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        Signal first = Signal.breakTo(interpreter.newLabel());
        JqValue[] code = {JqValue.NULL};
        Signal signal = interpreter.eval(arguments[0], env, input, null, (value, at) -> {
            code[0] = value;
            return first;
        });
        if (signal != first) {
            return signal;
        }
        if (code[0].kind() != JqKind.NUMBER) {
            return Interpreter.fail(Errors.haltErrorNumberRequired(input));
        }
        double n = code[0].asNumber();
        double number = n < 0 ? Math.ceil(n) : Math.floor(n);
        return Signal.halt(input, Double.isNaN(number) ? 0 : (int) Math.clamp(number, Integer.MIN_VALUE, Integer.MAX_VALUE));
    }

    // last(f): the last output of f, with its path. *loop*
    private static Signal last(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        boolean[] has = {false};
        JqValue[] last = {JqValue.NULL};
        Path[] lastAt = {null};
        Signal signal = interpreter.eval(arguments[0], env, input, path, (value, at) -> {
            has[0] = true;
            last[0] = value;
            lastAt[0] = at;
            return null;
        });
        return signal != null ? signal : has[0] ? interpreter.output(emit, last[0], lastAt[0]) : null;
    }

    private static JqValue listBuiltins() {
        List<String> names = new ArrayList<>();
        for (Native builtin : Natives.ALL) {
            if (!builtin.isInternal() && !builtin.overridesPrelude()) {
                names.add(builtin.toString());
            }
        }

        for (Function function : Prelude.instance().definitions()) {
            if (!function.name().startsWith("_")) {
                names.add(function.toString());
            }
        }

        names.sort(CodePoints::compare);
        JqValue[] items = new JqValue[names.size()];
        for (int i = 0; i < items.length; i++) {
            items[i] = JqValue.fromString(names.get(i));
        }
        return JqArray.fromOwned(items);
    }
}
