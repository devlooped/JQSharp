package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;
import java.time.Clock;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.function.Consumer;

/**
 * What an expression takes from its host besides the input and the variables: modules,
 * {@code $ENV}, the clock, the depth limit and the handlers of {@code debug} and {@code stderr}.
 * Immutable: each {@code with} method returns a copy. Options are read when the expression is parsed
 * ({@link Jq#parse(String, JqOptions)}) and apply to every evaluation of it.
 */
public final class JqOptions {
    private static final JqOptions DEFAULTS = new JqOptions(null, null, Clock.systemDefaultZone(), 1000, null, null);

    private final JqResolver resolver;
    private final Map<String, String> environment;
    private final Clock clock;
    private final int maxDepth;
    private final Consumer<JqValue> debug;
    private final Consumer<JqValue> stderr;

    private JqOptions(JqResolver resolver, Map<String, String> environment, Clock clock, int maxDepth, Consumer<JqValue> debug, Consumer<JqValue> stderr) {
        this.resolver = resolver;
        this.environment = environment;
        this.clock = clock;
        this.maxDepth = maxDepth;
        this.debug = debug;
        this.stderr = stderr;
    }

    /** No modules, the process environment, the system clock and zone, a depth of 1,000, no handlers. */
    public static JqOptions defaults() {
        return DEFAULTS;
    }

    /** Resolves {@code import} and {@code include} directives and {@code modulemeta}; without one (null), no module exists. */
    public JqResolver resolver() {
        return resolver;
    }

    public JqOptions withResolver(JqResolver value) {
        return new JqOptions(value, environment, clock, maxDepth, debug, stderr);
    }

    /**
     * The variables of {@code $ENV} and {@code env}, in their iteration order. When null (the
     * default), the process environment, read when an evaluation first uses {@code $ENV}.
     */
    public Map<String, String> environment() {
        return environment;
    }

    public JqOptions withEnvironment(Map<String, String> value) {
        return new JqOptions(resolver, value == null ? null : java.util.Collections.unmodifiableMap(new LinkedHashMap<>(value)), clock, maxDepth, debug, stderr);
    }

    /** The source of {@code now} and of the local time zone ({@code localtime}, {@code strflocaltime}, {@code %z}, {@code %Z}). */
    public Clock clock() {
        return clock;
    }

    public JqOptions withClock(Clock value) {
        if (value == null) {
            throw new NullPointerException("clock");
        }
        return new JqOptions(resolver, environment, value, maxDepth, debug, stderr);
    }

    /** The largest {@link #maxDepth} the port honours: a level of recursion takes up to about 4 KB of thread stack. */
    public static final int MAX_DEPTH_LIMIT = 100_000;

    /**
     * The maximum number of calls of jq-defined functions active at once (spec §11); beyond it, the
     * catchable error {@code Maximum call depth exceeded}. Deeper recursion never exhausts the host
     * stack: it continues on threads of 64 MB stacks, each level taking about 2 KB, up to 4 KB, of
     * stack committed as it is used, so {@link #MAX_DEPTH_LIMIT} levels may take up to 400 MB.
     */
    public int maxDepth() {
        return maxDepth;
    }

    /** @throws IllegalArgumentException {@code value} is below 1 or above {@link #MAX_DEPTH_LIMIT}. */
    public JqOptions withMaxDepth(int value) {
        if (value < 1 || value > MAX_DEPTH_LIMIT) {
            throw new IllegalArgumentException("maxDepth must be between 1 and " + MAX_DEPTH_LIMIT + ", not " + value + ".");
        }
        return new JqOptions(resolver, environment, clock, value, debug, stderr);
    }

    /** Receives the value of each {@code debug} (its input) and each message of {@code debug(msgs)}, as is; without one, they are dropped. */
    public Consumer<JqValue> debug() {
        return debug;
    }

    public JqOptions withDebug(Consumer<JqValue> value) {
        return new JqOptions(resolver, environment, clock, maxDepth, value, stderr);
    }

    /** Receives the input of each {@code stderr}, as is; without one, the values are dropped. */
    public Consumer<JqValue> stderr() {
        return stderr;
    }

    public JqOptions withStderr(Consumer<JqValue> value) {
        return new JqOptions(resolver, environment, clock, maxDepth, debug, value);
    }
}
