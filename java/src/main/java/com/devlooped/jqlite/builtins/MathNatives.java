package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import java.util.function.DoubleBinaryOperator;
import java.util.function.DoubleUnaryOperator;

/**
 * §9.8 Math. One-input functions take a number input; multi-input ones ignore the input and take
 * number arguments (the first non-number, in argument order, is the error).
 *
 * <p>Host-specific: the library functions of §9.8 are {@link StrictMath}'s (fdlibm, the same results
 * on every JVM); the few Java lacks (acosh, asinh, atanh, log2) and pow's C99 special cases are in
 * {@link MathExtra}.
 */
final class MathNatives {
    private MathNatives() {
    }

    private record Unary(String name, DoubleUnaryOperator function) {
    }

    private record Binary(String name, DoubleBinaryOperator function) {
    }

    // The functions of one number: the exactly specified ones, the library functions (the host's
    // libm), and those the spec defines by formula (MathExtra: exp2, expm1, log1p and the special
    // functions).
    private static final Unary[] UNARY = {
        new Unary("floor", Math::floor),
        new Unary("ceil", Math::ceil),
        new Unary("trunc", MathExtra::truncate),
        new Unary("round", MathExtra::round),
        new Unary("nearbyint", MathExtra::nearbyInt),
        new Unary("rint", MathExtra::nearbyInt),
        new Unary("fabs", Math::abs),
        new Unary("sqrt", StrictMath::sqrt),
        new Unary("logb", MathExtra::logb),
        new Unary("significand", MathExtra::significand),
        new Unary("acos", StrictMath::acos),
        new Unary("asin", StrictMath::asin),
        new Unary("atan", StrictMath::atan),
        new Unary("acosh", MathExtra::acosh),
        new Unary("asinh", MathExtra::asinh),
        new Unary("atanh", MathExtra::atanh),
        new Unary("cbrt", StrictMath::cbrt),
        new Unary("cos", StrictMath::cos),
        new Unary("cosh", StrictMath::cosh),
        new Unary("exp", StrictMath::exp),
        new Unary("exp2", MathExtra::exp2),
        new Unary("exp10", MathExtra::exp10),
        new Unary("expm1", MathExtra::expm1),
        new Unary("log", StrictMath::log),
        new Unary("log10", StrictMath::log10),
        new Unary("log1p", MathExtra::log1p),
        new Unary("log2", MathExtra::log2),
        new Unary("sin", StrictMath::sin),
        new Unary("sinh", StrictMath::sinh),
        new Unary("tan", StrictMath::tan),
        new Unary("tanh", StrictMath::tanh),
        new Unary("erf", MathExtra::erf),
        new Unary("erfc", MathExtra::erfc),
        new Unary("tgamma", MathExtra::tGamma),
        new Unary("lgamma", MathExtra::lGamma),
        new Unary("gamma", MathExtra::lGamma),
        new Unary("j0", MathExtra::j0),
        new Unary("j1", MathExtra::j1),
        new Unary("y0", MathExtra::y0),
        new Unary("y1", MathExtra::y1),
    };

    private static final Binary[] BINARY = {
        new Binary("fmod", (x, y) -> x % y),
        new Binary("remainder", MathExtra::remainder),
        new Binary("drem", MathExtra::remainder),
        new Binary("fmin", MathExtra::min),
        new Binary("fmax", MathExtra::max),
        new Binary("fdim", MathExtra::fdim),
        new Binary("copysign", MathExtra::copySign),
        new Binary("nextafter", MathExtra::nextAfter),
        new Binary("nexttoward", MathExtra::nextAfter),
        new Binary("ldexp", MathExtra::scaleB),
        new Binary("scalbln", MathExtra::scaleB),
        new Binary("scalb", MathExtra::scalb),
        new Binary("atan2", StrictMath::atan2),
        new Binary("pow", MathExtra::pow),
        new Binary("hypot", StrictMath::hypot),
        new Binary("jn", MathExtra::jn),
        new Binary("yn", MathExtra::yn),
    };

    static void register() {
        for (Unary unary : UNARY) {
            DoubleUnaryOperator function = unary.function;
            add(unary.name, 0, (interpreter, input, arguments) -> input.kind() == JqKind.NUMBER
                ? JqValue.fromNumber(function.applyAsDouble(input.asNumber()))
                : fail(Errors.numberRequired(input)));
        }

        for (Binary binary : BINARY) {
            DoubleBinaryOperator function = binary.function;
            add(binary.name, 2, (interpreter, input, arguments) -> {
                JqValue error = numbers(arguments);
                return error == null
                    ? JqValue.fromNumber(function.applyAsDouble(arguments[0].asNumber(), arguments[1].asNumber()))
                    : fail(error);
            });
        }

        add("fma", 3, (interpreter, input, arguments) -> {
            JqValue error = numbers(arguments);
            return error == null
                ? JqValue.fromNumber(Math.fma(arguments[0].asNumber(), arguments[1].asNumber(), arguments[2].asNumber()))
                : fail(error);
        });
        add("frexp", 0, (interpreter, input, arguments) -> {
            if (input.kind() != JqKind.NUMBER) {
                return fail(Errors.numberRequired(input));
            }
            double[] parts = MathExtra.frexp(input.asNumber());
            return pair(parts[0], parts[1]);
        });
        add("lgamma_r", 0, (interpreter, input, arguments) -> {
            if (input.kind() != JqKind.NUMBER) {
                return fail(Errors.numberRequired(input));
            }
            double x = input.asNumber();
            return pair(MathExtra.lGamma(x), MathExtra.gammaSign(x));
        });
        add("modf", 0, (interpreter, input, arguments) -> {
            if (input.kind() != JqKind.NUMBER) {
                return fail(Errors.numberRequired(input));
            }
            double[] parts = MathExtra.modf(input.asNumber());
            return pair(parts[0], parts[1]);
        });
    }

    // The error of the first argument that is not a number, or Java null.
    private static JqValue numbers(JqValue[] arguments) {
        for (JqValue argument : arguments) {
            if (argument.kind() != JqKind.NUMBER) {
                return Errors.numberRequired(argument);
            }
        }

        return null;
    }

    private static JqValue pair(double first, double second) {
        return JqArray.of(JqValue.fromNumber(first), JqValue.fromNumber(second));
    }
}
