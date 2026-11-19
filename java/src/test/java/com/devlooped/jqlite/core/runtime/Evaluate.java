package com.devlooped.jqlite.core.runtime;

import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import com.devlooped.jqlite.runtime.Evaluation.Outcome;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.List;
import org.junit.jupiter.api.Assertions;

/** Runs programs on the core for the runtime tests: outputs as canonical text, and how the evaluation ended. */
public final class Evaluate {
    private Evaluate() {
    }

    public static JqValue json(String text) {
        Result value = ValueText.tryParse(text);
        if (value instanceof Result.Err err) {
            Assertions.fail("bad JSON " + text + ": " + err.error());
        }
        return (JqValue) value;
    }

    public static BoundProgram compile(String program) {
        Resolver.Compiled compiled = Resolver.tryCompile(program);
        if (compiled.program() == null) {
            Assertions.fail("'" + program + "': " + compiled.error().message() + " at " + compiled.error().line() + ":" + compiled.error().column());
        }
        return compiled.program();
    }

    public static List<String> run(String program) {
        return run(compile(program), json("null"), null, Integer.MAX_VALUE);
    }

    public static List<String> run(String program, String input) {
        return run(compile(program), json(input), null, Integer.MAX_VALUE);
    }

    public static List<String> run(String program, String input, EvaluationOptions options) {
        return run(compile(program), json(input), options, Integer.MAX_VALUE);
    }

    public static List<String> run(BoundProgram program, JqValue input) {
        return run(program, input, null, Integer.MAX_VALUE);
    }

    public static List<String> run(BoundProgram program, JqValue input, EvaluationOptions options) {
        return run(program, input, options, Integer.MAX_VALUE);
    }

    /** The outputs, each as canonical text, then "error: value" or "halt …" when the evaluation did not complete. */
    public static List<String> run(BoundProgram program, JqValue input, EvaluationOptions options, int take) {
        List<String> outputs = new ArrayList<>();
        Outcome outcome = Interpreter.run(program, input, options != null ? options : new EvaluationOptions(), output -> {
            outputs.add(ValueText.toJson(output));
            return outputs.size() < take;
        });
        switch (outcome.kind()) {
            case ERROR -> outputs.add("error: " + ValueText.toJson(outcome.value()));
            case HALT -> outputs.add("halt " + outcome.code() + (outcome.hasValue() ? " " + ValueText.toJson(outcome.value()) : ""));
            case STOPPED -> {
                if (outputs.size() < take) {
                    outputs.add("stopped");
                }
            }
            default -> {
                // Completed.
            }
        }

        return outputs;
    }

    /** One line: the outputs separated by ", ". */
    public static String line(String program) {
        return String.join(", ", run(program));
    }

    public static String line(String program, String input) {
        return String.join(", ", run(program, input));
    }

    public static String line(String program, String input, EvaluationOptions options) {
        return String.join(", ", run(program, input, options));
    }

    /** As line, on a thread with a stack of the given size. */
    public static String onStack(long stackSize, String program, String input, EvaluationOptions options) {
        BoundProgram bound = compile(program);
        JqValue value = json(input);
        String[] result = new String[1];
        Throwable[] failure = new Throwable[1];
        Thread thread = new Thread(null, () -> {
            try {
                result[0] = String.join(", ", run(bound, value, options));
            } catch (Throwable e) {
                failure[0] = e;
            }
        }, "small-stack", stackSize);
        thread.start();
        try {
            thread.join();
        } catch (InterruptedException e) {
            throw new IllegalStateException(e);
        }
        if (failure[0] != null) {
            Assertions.fail(failure[0]);
        }
        return result[0];
    }

    /** As line, on a thread with a 1 MB stack (the default of a Java thread). */
    public static String onSmallStack(String program, String input, EvaluationOptions options) {
        return onStack(1024 * 1024, program, input, options);
    }
}
