package com.devlooped.jqlite.runtime;

import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import java.util.Map;
import java.util.function.Consumer;
import java.util.function.Supplier;

/** The host interface of an evaluation (§12). Java has one public type per file, so the types of Evaluation.cs are nested here. */
public final class Evaluation {
    private Evaluation() {
    }

    /** The host's clock and time zone (§12): {@code now}, and the local zone of {@code localtime}, {@code strflocaltime}, {@code %z}, {@code %Z}. */
    public interface IClock {
        /** The current time, in seconds since the Unix epoch. */
        double now();

        /** The offset of the local zone from UTC at the instant {@code seconds}, in seconds east of UTC. */
        int offset(double seconds);

        /** The abbreviation of the local zone at the instant {@code seconds}, or null. */
        String abbreviation(double seconds);
    }

    /** What an evaluation takes from its host besides the program and the input (§12). */
    public static final class EvaluationOptions {
        /** The host variables, by name without '$'; names are validated by the host boundary. Null: none. */
        public Map<String, JqValue> variables;

        /**
         * The object {@code $ENV} is (string to string, in the order the host gives), asked for once per
         * evaluation, when the program first reads it: a host can read its process environment then.
         */
        public Supplier<JqValue> environment = () -> JqObject.EMPTY;

        /** The call depth limit of §11. */
        public int maxDepth = 1000;

        /** The clock and zone, for the date builtins. */
        public IClock clock;

        /** Receives the values of {@code debug}; they are dropped when null. */
        public Consumer<JqValue> debug;

        /** Receives the values of {@code stderr}; they are dropped when null. */
        public Consumer<JqValue> stderr;
    }

    public enum OutcomeKind {
        /** Every output was produced. */
        COMPLETED,

        /** The host stopped the stream. */
        STOPPED,

        /** An error reached the top: {@link Outcome#value}. */
        ERROR,

        /** {@code halt} or {@code halt_error}: {@link Outcome#code}, and {@link Outcome#value} when {@link Outcome#hasValue}. */
        HALT,
    }

    /** How an evaluation ended (§12). */
    public record Outcome(OutcomeKind kind, JqValue value, boolean hasValue, int code) {
        public Outcome(OutcomeKind kind) {
            this(kind, JqValue.NULL, false, 0);
        }
    }
}
