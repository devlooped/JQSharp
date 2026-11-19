package com.devlooped.jqlite;

import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import com.devlooped.jqlite.runtime.Evaluation.IClock;
import com.devlooped.jqlite.runtime.Evaluation.Outcome;
import com.devlooped.jqlite.runtime.Evaluation.OutcomeKind;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.CodePoints;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObjectBuilder;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;
import java.util.function.Predicate;
import java.util.function.Supplier;

/**
 * A jq expression parsed and bound once by {@link Jq#parse(String, JqOptions)}, to evaluate on any
 * number of inputs. Immutable and safe to evaluate concurrently from many threads, each evaluation
 * with its own input and variables.
 */
public final class JqExpression {
    private final BoundProgram program;
    private final Supplier<JqValue> environment;
    private final IClock clock;
    private final int maxDepth;
    private final Consumer<JqValue> debug;
    private final Consumer<JqValue> stderr;

    JqExpression(BoundProgram program, JqOptions options) {
        this.program = program;
        this.clock = new ClockSource(options.clock());
        this.maxDepth = options.maxDepth();
        this.debug = options.debug();
        this.stderr = options.stderr();
        if (options.environment() != null) {
            JqValue snapshot = toObject(options.environment());
            this.environment = () -> snapshot;
        } else {
            this.environment = () -> toObject(System.getenv());
        }
    }

    /** Evaluates the expression on {@code input} and returns all its results, computed before the list is returned. */
    public List<JqValue> evaluate(JqValue input) {
        return evaluate(input, Map.of());
    }

    /**
     * Evaluates the expression on {@code input} with host variables and returns all its results.
     *
     * @param variables host variables, by name without {@code $} ({@code "root"} is {@code $root}):
     *     each name a jq identifier, {@code ENV} excluded. A variable the expression uses but the host
     *     does not give is the runtime error {@code $name is not defined}; a binding in the
     *     expression shadows a host variable.
     * @throws IllegalArgumentException a variable name is not a valid identifier, starts with {@code $}, or is {@code ENV}.
     * @throws JqException an error reached the top of the expression ({@link JqHaltException} for {@code halt_error}); no results are returned.
     */
    public List<JqValue> evaluate(JqValue input, Map<String, JqValue> variables) {
        List<JqValue> results = new ArrayList<>();
        evaluate(input, variables, result -> {
            results.add(result);
            return true;
        });
        return results;
    }

    /**
     * Evaluates the expression on {@code input}, giving each result to {@code onResult} as soon as it
     * is produced; the callback returns false to stop the evaluation (stopping is not an error).
     */
    public void evaluate(JqValue input, Predicate<JqValue> onResult) {
        evaluate(input, Map.of(), onResult);
    }

    /**
     * Evaluates the expression on {@code input} with host variables, giving each result to
     * {@code onResult} as soon as it is produced; the callback returns false to stop the evaluation.
     *
     * @throws IllegalArgumentException a variable name is not valid.
     * @throws JqException an error reached the top of the expression, after the results produced before it were given to {@code onResult}.
     */
    public void evaluate(JqValue input, Map<String, JqValue> variables, Predicate<JqValue> onResult) {
        if (input == null || onResult == null) {
            throw new NullPointerException("input and onResult are required");
        }
        EvaluationOptions options = new EvaluationOptions();
        options.variables = variables == null ? null : validate(variables);
        options.environment = environment;
        options.clock = clock;
        options.maxDepth = maxDepth;
        options.debug = debug;
        options.stderr = stderr;

        Outcome outcome = Interpreter.run(program, input, options, onResult);
        if (outcome.kind() == OutcomeKind.ERROR) {
            throw new JqException(message(outcome.value()), outcome.value());
        }
        if (outcome.kind() == OutcomeKind.HALT && outcome.hasValue()) {
            throw new JqHaltException(message(outcome.value()), outcome.value(), outcome.code());
        }
    }

    // The message of an error that reaches the host (spec §10.1).
    private static String message(JqValue value) {
        return value.kind() == JqKind.STRING ? value.asString() : ValueText.toJson(value) + " (not a string)";
    }

    // Validates the names (spec §12); values are immutable, so they are used as they are.
    private static Map<String, JqValue> validate(Map<String, JqValue> variables) {
        Map<String, JqValue> checked = new HashMap<>(variables.size() * 2);
        for (Map.Entry<String, JqValue> entry : variables.entrySet()) {
            String name = entry.getKey();
            if (!isIdentifier(name)) {
                throw new IllegalArgumentException("'" + name + "' is not a valid variable name: give a jq identifier without '$'.");
            }
            if (name.equals("ENV")) {
                throw new IllegalArgumentException("$ENV cannot be a host variable: use JqOptions.withEnvironment.");
            }
            if (entry.getValue() == null) {
                throw new IllegalArgumentException("The variable '" + name + "' has no value: give JqValue.NULL for null.");
            }
            checked.put(name, entry.getValue());
        }

        return checked;
    }

    private static boolean isIdentifier(String name) {
        if (name == null || name.isEmpty() || !(isLetter(name.charAt(0)) || name.charAt(0) == '_')) {
            return false;
        }
        for (int i = 0; i < name.length(); i++) {
            char c = name.charAt(i);
            if (!(isLetter(c) || (c >= '0' && c <= '9') || c == '_')) {
                return false;
            }
        }

        return true;
    }

    private static boolean isLetter(char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }

    private static JqValue toObject(Map<String, String> variables) {
        JqObjectBuilder builder = new JqObjectBuilder(variables.size() + 1);
        for (Map.Entry<String, String> entry : variables.entrySet()) {
            builder.set(CodePoints.sanitize(entry.getKey()), JqValue.fromString(entry.getValue() == null ? "" : entry.getValue()));
        }
        return builder.toObject();
    }
}
