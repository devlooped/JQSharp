package com.devlooped.jqlite.bench;

import com.devlooped.jqlite.binding.Bound.BoundProgram;
import com.devlooped.jqlite.binding.Resolver;
import com.devlooped.jqlite.runtime.Evaluation.EvaluationOptions;
import com.devlooped.jqlite.runtime.Evaluation.Outcome;
import com.devlooped.jqlite.runtime.Evaluation.OutcomeKind;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueText;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Locale;

/**
 * The runner of the cross-language benchmark (bench/README.md): the same command line, the same
 * three measurements and the same line of output in every port. It drives the core engine, so the
 * numbers do not include the host boundary. Built into target/jqlite-tests.jar, whose manifest
 * names this class and the library jar beside it.
 */
public final class CrossBench {
    private static final int ROUNDS = 10;

    private CrossBench() {
    }

    public static void main(String[] args) throws IOException {
        System.exit(run(args));
    }

    static int run(String[] args) throws IOException {
        if (args.length != 6) {
            System.err.println("usage: CrossBench <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>");
            return 2;
        }

        String source = Files.readString(Path.of(args[0]), StandardCharsets.UTF_8);
        String inputText = Files.readString(Path.of(args[1]), StandardCharsets.UTF_8);
        int take = Integer.parseInt(args[2]);
        int warmupMs = Integer.parseInt(args[3]);
        int measureMs = Integer.parseInt(args[4]);

        Resolver.Compiled compiled = Resolver.tryCompile(source);
        if (compiled.program() == null) {
            System.err.println(compiled.error().message() + " at " + compiled.error().line() + ":" + compiled.error().column());
            return 1;
        }
        BoundProgram program = compiled.program();

        Result parsed = ValueText.tryParse(inputText);
        if (parsed instanceof Result.Err err) {
            System.err.println("bad input: " + ValueText.toJson(err.error()));
            return 1;
        }
        JqValue input = (JqValue) parsed;

        EvaluationOptions options = new EvaluationOptions();

        // The check run: the outputs as canonical text, one per line.
        StringBuilder text = new StringBuilder();
        int[] count = {0};
        Outcome outcome = Interpreter.run(program, input, options, output -> {
            ValueText.write(text, output);
            text.append('\n');
            count[0]++;
            return take == 0 || count[0] < take;
        });
        if (outcome.kind() == OutcomeKind.ERROR || (outcome.kind() == OutcomeKind.HALT && outcome.hasValue())) {
            System.err.println("error: " + ValueText.toJson(outcome.value()));
            return 1;
        }

        Files.writeString(Path.of(args[5]), text.toString(), StandardCharsets.UTF_8);

        // compile: the program text to a bound program (the prelude is loaded already).
        double compileNs = measure(warmupMs, measureMs, () -> Resolver.tryCompile(source));

        // eval: the bound program on the input value, outputs collected as values.
        double evalNs = measure(warmupMs, measureMs, () -> {
            List<JqValue> outputs = new ArrayList<>();
            Interpreter.run(program, input, options, output -> {
                outputs.add(output);
                return take == 0 || outputs.size() < take;
            });
        });

        // text: the input text read, the program evaluated, the outputs written as text.
        double textNs = measure(warmupMs, measureMs, () -> {
            JqValue value = (JqValue) ValueText.tryParse(inputText);
            StringBuilder written = new StringBuilder();
            int[] outputs = {0};
            Interpreter.run(program, value, options, output -> {
                ValueText.write(written, output);
                written.append('\n');
                outputs[0]++;
                return take == 0 || outputs[0] < take;
            });
            written.toString();
        });

        String runtime = "Java " + System.getProperty("java.version") + " (" + System.getProperty("java.vm.name") + ")";
        System.out.println(String.format(Locale.ROOT,
            "{\"impl\":\"java\",\"runtime\":\"%s\",\"compile_ns\":%.1f,\"eval_ns\":%.1f,\"text_ns\":%.1f,\"outputs\":%d}",
            runtime.replace("\"", "'"), compileNs, evalNs, textNs, count[0]));
        return 0;
    }

    // Nanoseconds per operation: the median of ten rounds of equal size, sized from the warm-up.
    private static double measure(int warmupMs, int measureMs, Runnable operation) {
        long started = System.nanoTime();
        long warmups = 0;
        do {
            operation.run();
            warmups++;
        } while ((System.nanoTime() - started) / 1e6 < warmupMs);

        double perOperationMs = (System.nanoTime() - started) / 1e6 / warmups;
        long batch = Math.max(1, (long) (measureMs / (double) ROUNDS / perOperationMs));

        double[] rounds = new double[ROUNDS];
        for (int round = 0; round < ROUNDS; round++) {
            long start = System.nanoTime();
            for (long i = 0; i < batch; i++) {
                operation.run();
            }
            rounds[round] = (System.nanoTime() - start) / (double) batch;
        }

        Arrays.sort(rounds);
        return (rounds[ROUNDS / 2 - 1] + rounds[ROUNDS / 2]) / 2;
    }
}
