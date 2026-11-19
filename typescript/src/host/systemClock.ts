import type { JqClock } from "../jqOptions.js";
import type { IClock } from "../runtime/evaluation.js";

// The instants a Date represents, in seconds: ±100,000,000 days around the epoch.
const MaxDateSeconds = 8.64e12;

/**
 * The system's clock and local zone, from the host's `Date`: the offset at an instant follows
 * the zone's daylight saving time; instants outside what a Date represents take the offset of
 * the epoch. JavaScript gives no zone abbreviation that does not depend on a locale: `%Z` prints
 * the offset (§9.12).
 */
export const systemClock: JqClock = {
  now: () => Date.now() / 1000,
  offset: (seconds) => {
    const inRange = Math.abs(seconds) <= MaxDateSeconds;
    // getTimezoneOffset is in minutes west of UTC.
    return -new Date(inRange ? Math.floor(seconds) * 1000 : 0).getTimezoneOffset() * 60;
  },
};

/** A public JqClock as the clock and zone of the core (spec §12, §9.12). */
export class HostClock implements IClock {
  constructor(private readonly clock: JqClock) {}

  now(): number {
    return this.clock.now();
  }

  offset(seconds: number): number {
    return Math.trunc(this.clock.offset(seconds));
  }

  abbreviation(seconds: number): string | null {
    return this.clock.abbreviation === undefined ? null : this.clock.abbreviation(seconds);
  }
}
