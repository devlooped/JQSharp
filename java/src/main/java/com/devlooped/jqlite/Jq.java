package com.devlooped.jqlite;

import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.syntax.SyntaxError;
import com.devlooped.jqlite.values.JqValue;
import java.io.BufferedReader;
import java.io.IOException;
import java.io.Reader;
import java.util.List;
import java.util.Map;
import java.util.function.Predicate;

/** Parses and evaluates jq expressions (the jqlite profile of jq 1.8) on {@link JqValue} values. */
public final class Jq {
    private Jq() {
    }

    /**
     * Parses and binds {@code expression} with the default options (no modules) into an expression
     * to evaluate any number of times, concurrently if need be. An empty expression is {@code .}.
     *
     * @throws JqParseException a syntax or binding error (an unknown function or label, an {@code import} or {@code include}), with its line and column.
     */
    public static JqExpression parse(String expression) {
        return parse(expression, JqOptions.defaults());
    }

    /**
     * Parses and binds {@code expression}, loading its modules through the options' resolver, into an
     * expression to evaluate any number of times, concurrently if need be.
     *
     * @throws JqParseException a syntax or binding error (an unknown function, label or module, a cycle of imports), with its line and column.
     */
    public static JqExpression parse(String expression, JqOptions options) {
        if (expression == null || options == null) {
            throw new NullPointerException("expression and options are required");
        }
        ResolverSource modules = options.resolver() != null ? new ResolverSource(options.resolver()) : null;
        Resolver.Compiled compiled = Resolver.tryCompile(expression, true, modules);
        if (compiled.program() == null) {
            SyntaxError error = compiled.error();
            String where = error.module() == null ? "" : error.module() + ", ";
            throw new JqParseException(error.message() + " at " + where + "line " + error.line() + ", column " + error.column(), error.line(), error.column());
        }

        return new JqExpression(compiled.program(), options);
    }

    /** Parses and binds {@code expression}, loading its modules through {@code resolver}, with the default options otherwise. */
    public static JqExpression parse(String expression, JqResolver resolver) {
        if (resolver == null) {
            throw new NullPointerException("resolver");
        }
        return parse(expression, JqOptions.defaults().withResolver(resolver));
    }

    /** Parses {@code expression} with the default options and evaluates it on {@code input}, returning all its results. */
    public static List<JqValue> evaluate(String expression, JqValue input) {
        return parse(expression).evaluate(input);
    }

    /** Parses {@code expression} with the default options and evaluates it on {@code input} with host variables. */
    public static List<JqValue> evaluate(String expression, JqValue input, Map<String, JqValue> variables) {
        return parse(expression).evaluate(input, variables);
    }

    /**
     * Evaluates {@code expression} on each JSON value of {@code jsonLines} in turn (JSON Lines: one
     * value per line, blank lines skipped), giving each result to {@code onResult}, which returns
     * false to stop. Each value is evaluated alone ({@code halt} ends only the evaluation of its value).
     */
    public static void evaluateLines(JqExpression expression, Reader jsonLines, Map<String, JqValue> variables, Predicate<JqValue> onResult) throws IOException {
        BufferedReader reader = jsonLines instanceof BufferedReader buffered ? buffered : new BufferedReader(jsonLines);
        boolean[] stopped = {false};
        for (String line; !stopped[0] && (line = reader.readLine()) != null;) {
            if (line.isBlank()) {
                continue;
            }
            expression.evaluate(JqJson.parse(line), variables, result -> {
                if (!onResult.test(result)) {
                    stopped[0] = true;
                    return false;
                }
                return true;
            });
        }
    }
}
