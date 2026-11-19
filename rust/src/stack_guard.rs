//! The one host-specific mechanism of the engine's control flow: what to do when the host stack
//! runs short. Spec §11 lets a program observe no recursion limit but MaxDepth, so a deep
//! evaluation step that finds the stack nearly exhausted continues on a fresh stack instead of
//! failing, and only the call depth counter (Interpreter) ever stops a recursion.
//!
//! Rust: this is the reference's `Runtime/StackGuard.cs`, placed beside the five layers instead of
//! inside `runtime`, because three of them use it and a layer uses only the layers before it:
//! the interpreter (every evaluation step and every output), the parser (each level of nesting:
//! a level takes about 2 KB of stack in a release build and 9 KB in a debug build, so the 1,000
//! levels of §11 do not fit the 1 MB stack of a Windows thread), and `regex_engine` (the host
//! engine compiles a deeply nested pattern by recursion). It knows nothing of jqlite.
//!
//! Host-specific: Rust uses the `stacker` crate. `stacker::grow` runs the step on a new stack
//! segment of [`STACK_SIZE`] bytes and comes back to the old one when the step returns (the
//! reference runs the step on a new thread and joins it). `stacker::remaining_stack` tells how
//! much of the current segment is left; it is asked once per segment and thread, and the lowest
//! address a step may start at is kept in a thread-local, so that the check on every evaluation
//! step is one comparison with the address of a local variable.

use std::cell::Cell;

// What must stay free below a step that starts: the frames between two checks (one evaluation
// step and what it calls that does not check, such as a native walking a value), with a margin.
const RED_ZONE: usize = 160 * 1024;

// The size of a fresh segment: a hop costs an allocation, so each one should last.
const STACK_SIZE: usize = 8 * 1024 * 1024;

const UNKNOWN: usize = usize::MAX;

thread_local! {
    // The lowest address at which a step may start on the current segment; UNKNOWN until asked.
    static LIMIT: Cell<usize> = const { Cell::new(UNKNOWN) };
}

// The address of a local variable: where the stack is now (it grows toward lower addresses).
#[inline(always)]
fn here() -> usize {
    let marker = 0u8;
    std::hint::black_box(&marker) as *const u8 as usize
}

// The limit of the segment the caller runs on: 0 when the host cannot tell what is left.
fn measure() -> usize {
    match stacker::remaining_stack() {
        Some(remaining) => here().saturating_sub(remaining).saturating_add(RED_ZONE),
        None => 0,
    }
}

/// Whether the current stack has room for a step.
#[inline]
pub fn has_stack() -> bool {
    let mut limit = LIMIT.get();
    if limit == UNKNOWN {
        limit = measure();
        LIMIT.set(limit);
    }
    here() > limit
}

/// Whether the current stack has `bytes` left for a step that does not check the stack itself
/// (the host regex engine compiling a pattern).
#[inline]
pub fn has_room(bytes: usize) -> bool {
    let mut limit = LIMIT.get();
    if limit == UNKNOWN {
        limit = measure();
        LIMIT.set(limit);
    }
    // The limit is the end of the segment plus the red zone; 0 when the host cannot tell.
    limit == 0 || here() > limit.saturating_sub(RED_ZONE).saturating_add(bytes)
}

/// Runs `step` on a fresh stack and returns its result.
pub fn on_fresh_stack<T>(step: impl FnOnce() -> T) -> T {
    on_stack_of(STACK_SIZE, step)
}

/// Runs `step` on a fresh stack of `size` bytes and returns its result.
pub fn on_stack_of<T>(size: usize, step: impl FnOnce() -> T) -> T {
    let outer = LIMIT.get();
    let result = stacker::grow(size, || {
        LIMIT.set(measure());
        step()
    });
    LIMIT.set(outer);
    result
}
