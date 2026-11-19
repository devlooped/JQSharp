package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.addClosure;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.builtins.RegexEngine.RegexMatcher;
import com.devlooped.jqlite.builtins.RegexEngine.RegexProgram;
import com.devlooped.jqlite.runtime.Emit;
import com.devlooped.jqlite.runtime.Env;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.runtime.Path;
import com.devlooped.jqlite.runtime.Signal;
import com.devlooped.jqlite.values.CodePoints;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObjectBuilder;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;

/**
 * §9.10 {@code test/2} and {@code match/2}: the flags, the search loop and the match objects, with
 * offsets and lengths in code points. The pattern goes through {@link RegexSyntax} to the engine
 * ({@link RegexEngine}), which works on UTF-16 units.
 */
final class RegexNatives {
    private RegexNatives() {
    }

    static void register() {
        add("test", 2, RegexNatives::test);
        addClosure("match", 2, RegexNatives::match);
    }

    private static final int[] NO_SPANS = new int[0];

    // A search ready to run: the input, the matcher of the pattern and the flags.
    private record Search(String input, RegexMatcher matcher, boolean global, boolean skipEmpty) {
        String[] groups() {
            return matcher.program().groups;
        }
    }

    // Receives the spans of each match; returns false to stop.
    @FunctionalInterface
    private interface Report {
        boolean report(int[] spans, CodePointMap map);
    }

    private static Result test(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        Object prepared = tryPrepare(input, arguments[0], arguments[1]);
        if (prepared instanceof JqValue error) {
            return fail(error);
        }
        Search search = (Search) prepared;
        // Whether there is a match, and nothing of it; with the n flag, a match that is not empty.
        if (!search.skipEmpty) {
            return JqValue.fromBool(search.matcher.tryMatch(search.input, 0, NO_SPANS));
        }
        boolean[] found = {false};
        run(search, (spans, map) -> {
            found[0] = true;
            return false;
        });
        return JqValue.fromBool(found[0]);
    }

    // match(re; flags): flags outermost, re innermost (re fastest); one output per reported match.
    private static Signal match(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        return interpreter.forEach(arguments[1], env, input, null, (flags, unused1) ->
            interpreter.forEach(arguments[0], env, input, null, (re, unused2) -> {
                Object prepared = tryPrepare(input, re, flags);
                if (prepared instanceof JqValue error) {
                    return Interpreter.fail(error);
                }
                Search search = (Search) prepared;
                Signal[] signal = {null};
                run(search, (spans, map) -> (signal[0] = interpreter.output(emit, matchObject(search, spans, map), Path.of(path))) == null);
                return signal[0];
            }));
    }

    // The search, or the error value.
    private static Object tryPrepare(JqValue input, JqValue re, JqValue flags) {
        if (input.kind() != JqKind.STRING) {
            return Errors.cannotBeMatched(input);
        }
        if (re.kind() != JqKind.STRING) {
            return Errors.notAString(re);
        }
        if (flags.kind() != JqKind.STRING && flags.kind() != JqKind.NULL) {
            return Errors.notAString(flags);
        }

        boolean global = false;
        boolean ignoreCase = false;
        boolean extended = false;
        boolean skipEmpty = false;
        boolean dotAll = false;
        String letters = flags.kind() == JqKind.STRING ? flags.asString() : "";
        for (int i = 0; i < letters.length(); i++) {
            switch (letters.charAt(i)) {
                case 'g' -> global = true;
                case 'i' -> ignoreCase = true;
                case 'x' -> extended = true;
                case 'n' -> skipEmpty = true;
                case 'm', 'p' -> dotAll = true;
                case 's', 'l' -> {
                    // Accepted, no effect.
                }
                default -> {
                    return Errors.invalidModifiers(letters);
                }
            }
        }

        String[] reason = {""};
        RegexProgram program = RegexEngine.tryCompile(re.asString(), new RegexSyntax.Options(extended, dotAll, ignoreCase), reason);
        if (program == null) {
            return Errors.regexFailure(reason[0]);
        }

        return new Search(input.asString(), new RegexMatcher(program), global, skipEmpty);
    }

    /** The search loop of §9.10, in code points; {@code report} returns false to stop. */
    private static void run(Search search, Report report) {
        CodePointMap map = new CodePointMap(search.input);
        int[] spans = new int[2 * (search.groups().length + 1)];
        for (int p = 0; p <= map.length;) {
            if (!search.matcher.tryMatch(search.input, map.unit(p), spans)) {
                return;
            }
            int start = map.codePoint(spans[0]);
            int end = map.codePoint(spans[1]);
            if (!(search.skipEmpty && start == end) && (!report.report(spans, map) || !search.global)) {
                return;
            }
            p = end > start ? end : end + 1;
        }
    }

    private static JqValue matchObject(Search search, int[] spans, CodePointMap map) {
        String[] groups = search.groups();
        JqValue[] captures = new JqValue[groups.length];
        for (int g = 0; g < captures.length; g++) {
            JqValue name = groups[g] != null ? JqValue.fromString(groups[g]) : JqValue.NULL;
            JqObjectBuilder capture = new JqObjectBuilder(4);
            int start = spans[2 * g + 2];
            int end = spans[2 * g + 3];
            if (start == end || start < 0) {
                // An empty or absent group has jq's other key order.
                capture.set("offset", JqValue.fromNumber(start < 0 ? -1 : map.codePoint(start)));
                capture.set("string", start < 0 ? JqValue.NULL : JqValue.fromString(""));
                capture.set("length", JqValue.fromNumber(0));
            } else {
                addSpan(capture, search.input, map, start, end);
            }

            capture.set("name", name);
            captures[g] = capture.toObject();
        }

        JqObjectBuilder match = new JqObjectBuilder(4);
        addSpan(match, search.input, map, spans[0], spans[1]);
        match.set("captures", JqArray.fromOwned(captures));
        return match.toObject();
    }

    // "offset", "length", "string" of a span given in UTF-16 units.
    private static void addSpan(JqObjectBuilder target, String input, CodePointMap map, int start, int end) {
        int offset = map.codePoint(start);
        target.set("offset", JqValue.fromNumber(offset));
        target.set("length", JqValue.fromNumber(map.codePoint(end) - offset));
        target.set("string", JqValue.fromString(input.substring(start, end)));
    }

    /** UTF-16 indices to code point indices and back, for a well-formed string. */
    private static final class CodePointMap {
        private final int[] toCodePoint;
        private final int[] toUnit;

        /** The number of code points. */
        final int length;

        CodePointMap(String text) {
            length = CodePoints.length(text);
            if (length == text.length()) {
                toCodePoint = null;
                toUnit = null;
                return;
            }
            toCodePoint = new int[text.length() + 1];
            toUnit = new int[length + 1];
            int codePoint = 0;
            for (int i = 0; i < text.length(); i++) {
                toCodePoint[i] = codePoint;
                if (!Character.isLowSurrogate(text.charAt(i))) {
                    toUnit[codePoint++] = i;
                }
            }

            toCodePoint[text.length()] = length;
            toUnit[length] = text.length();
        }

        int codePoint(int unit) {
            return toCodePoint == null ? unit : toCodePoint[unit];
        }

        int unit(int codePoint) {
            return toUnit == null ? codePoint : toUnit[codePoint];
        }
    }
}
