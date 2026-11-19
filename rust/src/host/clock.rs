//! The clock and zone of the host boundary (spec §12, §9.12).

use std::time::{SystemTime, UNIX_EPOCH};

use crate::runtime::Clock;

/// The source of `now` and of the local time zone (`localtime`, `strflocaltime`, `%z`, `%Z`). It
/// may be asked from any thread that evaluates an expression.
pub trait JqClock: Send + Sync {
    /// The current time, in seconds since the Unix epoch.
    fn now(&self) -> f64;

    /// The offset of the local zone from UTC at the instant `seconds`, in seconds east of UTC (so
    /// that it may follow daylight saving time).
    fn offset(&self, seconds: f64) -> i32;

    /// The abbreviation of the local zone at the instant `seconds`; without one, `%Z` prints the
    /// offset.
    fn abbreviation(&self, _seconds: f64) -> Option<String> {
        None
    }
}

/// The system clock, with UTC as the local zone: the standard library has no access to the time
/// zone of the host. Give [`FixedOffsetClock`] or a [`JqClock`] of your own for another zone.
#[derive(Clone, Copy, Debug, Default)]
pub struct SystemClock;

fn system_now() -> f64 {
    match SystemTime::now().duration_since(UNIX_EPOCH) {
        Ok(elapsed) => elapsed.as_secs_f64(),
        Err(before) => -before.duration().as_secs_f64(),
    }
}

impl JqClock for SystemClock {
    fn now(&self) -> f64 {
        system_now()
    }

    fn offset(&self, _: f64) -> i32 {
        0
    }
}

/// The system clock, with a zone at a fixed offset from UTC, in seconds east of UTC.
#[derive(Clone, Copy, Debug)]
pub struct FixedOffsetClock {
    pub offset_seconds: i32,
}

impl JqClock for FixedOffsetClock {
    fn now(&self) -> f64 {
        system_now()
    }

    fn offset(&self, _: f64) -> i32 {
        self.offset_seconds
    }
}

/// A `JqClock` as the clock of the core.
pub(crate) struct ClockAdapter<'a>(pub &'a dyn JqClock);

impl Clock for ClockAdapter<'_> {
    fn now(&self) -> f64 {
        self.0.now()
    }

    fn offset(&self, seconds: f64) -> i32 {
        self.0.offset(seconds)
    }

    fn abbreviation(&self, seconds: f64) -> Option<String> {
        self.0.abbreviation(seconds)
    }
}
