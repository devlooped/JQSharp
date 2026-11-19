package com.devlooped.jqlite;

import com.devlooped.jqlite.runtime.Evaluation.IClock;
import java.time.Clock;
import java.time.Instant;
import java.time.zone.ZoneRules;

/**
 * A {@link java.time.Clock} as the clock and zone of the core (spec §12, §9.12): the instant of
 * {@code now}, and the offset of the clock's zone at an instant, so that daylight saving time follows
 * the date. No zone abbreviations, as in the reference: {@code %Z} prints the offset.
 */
final class ClockSource implements IClock {
    private static final double MIN_SECONDS = Instant.MIN.getEpochSecond();
    private static final double MAX_SECONDS = Instant.MAX.getEpochSecond();

    private final Clock clock;
    private final ZoneRules rules;

    ClockSource(Clock clock) {
        this.clock = clock;
        this.rules = clock.getZone().getRules();
    }

    @Override
    public double now() {
        Instant instant = clock.instant();
        return instant.getEpochSecond() + instant.getNano() / 1e9;
    }

    // Instants outside what java.time represents take the offset at the epoch.
    @Override
    public int offset(double seconds) {
        if (Double.isNaN(seconds) || seconds < MIN_SECONDS || seconds > MAX_SECONDS) {
            return rules.getOffset(Instant.EPOCH).getTotalSeconds();
        }
        return rules.getOffset(Instant.ofEpochSecond((long) Math.floor(seconds))).getTotalSeconds();
    }

    @Override
    public String abbreviation(double seconds) {
        return null;
    }
}
