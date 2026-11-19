package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.List;

/** §9.6: text conversion and the string natives the prelude builds on. */
public final class StringNatives {
    private StringNatives() {
    }

    static void register() {
        add("tostring", 0, (interpreter, input, arguments) -> JqValue.fromString(toText(input)));
        add("tojson", 0, (interpreter, input, arguments) -> JqValue.fromString(ValueText.toJson(input)));
        add("fromjson", 0, (interpreter, input, arguments) -> input.kind() == JqKind.STRING
            ? ValueText.tryParse(input.asString())
            : fail(Errors.onlyStringsParse(input)));
        add("tonumber", 0, StringNatives::toNumber);
        add("toboolean", 0, StringNatives::toBoolean);
        add("format", 1, (interpreter, input, arguments) -> arguments[0].kind() == JqKind.STRING
            ? Formats.tryApply(arguments[0].asString(), input)
            : fail(Errors.notAValidFormat(arguments[0])));
        add("explode", 0, (interpreter, input, arguments) -> ValueOps.tryExplode(input));
        add("implode", 0, (interpreter, input, arguments) -> ValueOps.tryImplode(input));
        add("ascii_downcase", 0, (interpreter, input, arguments) -> asciiCase(input, false));
        add("ascii_upcase", 0, (interpreter, input, arguments) -> asciiCase(input, true));
        add("startswith", 1, (interpreter, input, arguments) -> bothStrings(input, arguments[0])
            ? JqValue.fromBool(input.asString().startsWith(arguments[0].asString()))
            : fail(Errors.startsWithStrings()));
        add("endswith", 1, (interpreter, input, arguments) -> bothStrings(input, arguments[0])
            ? JqValue.fromBool(input.asString().endsWith(arguments[0].asString()))
            : fail(Errors.endsWithStrings()));
        add("trim", 0, (interpreter, input, arguments) -> trim(input, true, true));
        add("ltrim", 0, (interpreter, input, arguments) -> trim(input, true, false));
        add("rtrim", 0, (interpreter, input, arguments) -> trim(input, false, true));
        add("split", 1, (interpreter, input, arguments) -> bothStrings(input, arguments[0])
            ? ValueOps.split(input.asString(), arguments[0].asString())
            : fail(Errors.splitStrings()));
        add("_strindices", 1, StringNatives::stringIndices);
    }

    /** {@code tostring}: a string as it is, any other value as its canonical text. */
    public static String toText(JqValue value) {
        return value.kind() == JqKind.STRING ? value.asString() : ValueText.toJson(value);
    }

    private static Result toNumber(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (input.kind() == JqKind.NUMBER) {
            return input;
        }
        if (input.kind() == JqKind.STRING) {
            Double number = ValueText.tryParseNumber(input.asString());
            if (number != null) {
                return JqValue.fromNumber(number);
            }
        }
        return fail(Errors.cannotParseNumber(input));
    }

    private static Result toBoolean(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (input.kind() == JqKind.TRUE || input.kind() == JqKind.FALSE) {
            return input;
        }
        if (input.kind() == JqKind.STRING && (input.asString().equals("true") || input.asString().equals("false"))) {
            return JqValue.fromBool(input.asString().equals("true"));
        }
        return fail(Errors.cannotParseBoolean(input));
    }

    // trim, ltrim, rtrim: the Unicode White_Space code points, all of them single UTF-16 units.
    private static Result trim(JqValue input, boolean start, boolean end) {
        if (input.kind() != JqKind.STRING) {
            return fail(Errors.trimNotString());
        }
        String text = input.asString();
        int from = 0;
        int to = text.length();
        while (start && from < to && isWhiteSpace(text.charAt(from))) {
            from++;
        }
        while (end && to > from && isWhiteSpace(text.charAt(to - 1))) {
            to--;
        }
        return from == 0 && to == text.length() ? input : JqValue.fromString(text.substring(from, to));
    }

    private static boolean isWhiteSpace(char c) {
        return (c >= 0x0009 && c <= 0x000D) || c == 0x0020 || c == 0x0085 || c == 0x00A0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A)
            || c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
    }

    private static Result asciiCase(JqValue input, boolean upper) {
        if (input.kind() != JqKind.STRING) {
            return fail(Errors.explodeNotString());
        }
        String text = input.asString();
        StringBuilder builder = new StringBuilder(text.length());
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            builder.append(upper ? (c >= 'a' && c <= 'z' ? (char) (c - 32) : c) : (c >= 'A' && c <= 'Z' ? (char) (c + 32) : c));
        }
        return JqValue.fromString(builder.toString());
    }

    // _strindices(needle): the code point offsets of every occurrence, overlapping ones included.
    private static Result stringIndices(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (input.kind() != JqKind.STRING) {
            return fail(Errors.cannotSearchNotString(input));
        }
        if (arguments[0].kind() != JqKind.STRING) {
            return fail(Errors.notAString(arguments[0]));
        }
        String text = input.asString();
        String needle = arguments[0].asString();
        List<JqValue> offsets = new ArrayList<>();
        for (int i = 0, codePoint = 0; !needle.isEmpty() && i + needle.length() <= text.length(); codePoint++) {
            if (text.startsWith(needle, i)) {
                offsets.add(JqValue.fromNumber(codePoint));
            }
            i += Character.isHighSurrogate(text.charAt(i)) && i + 1 < text.length() ? 2 : 1;
        }

        return JqArray.fromOwned(offsets.toArray(new JqValue[0]));
    }

    private static boolean bothStrings(JqValue a, JqValue b) {
        return a.kind() == JqKind.STRING && b.kind() == JqKind.STRING;
    }
}
