//! What must stay linear (porting guide §2.9): growing an array, an object or a string in a loop
//! changes the accumulator in place when it is owned once. The reference measures the bytes its
//! thread allocated; here a counting allocator does, per thread.

mod support;

use std::alloc::{GlobalAlloc, Layout, System};
use std::cell::Cell;
use std::sync::Arc;

use jqlite::values::{JqArray, JqObject, JqValue};

struct Counting;

thread_local! {
    static ALLOCATED: Cell<usize> = const { Cell::new(0) };
}

fn count(bytes: usize) {
    // The thread-local may be gone while a thread ends: such allocations are not counted.
    let _ = ALLOCATED.try_with(|allocated| allocated.set(allocated.get() + bytes));
}

// SAFETY: every request goes to the system allocator unchanged; only its size is added to a counter.
unsafe impl GlobalAlloc for Counting {
    unsafe fn alloc(&self, layout: Layout) -> *mut u8 {
        count(layout.size());
        // SAFETY: the caller's contract is the system allocator's.
        unsafe { System.alloc(layout) }
    }

    unsafe fn dealloc(&self, pointer: *mut u8, layout: Layout) {
        // SAFETY: the pointer came from the system allocator with this layout.
        unsafe { System.dealloc(pointer, layout) }
    }

    unsafe fn realloc(&self, pointer: *mut u8, layout: Layout, new_size: usize) -> *mut u8 {
        count(new_size);
        // SAFETY: the pointer came from the system allocator with this layout.
        unsafe { System.realloc(pointer, layout, new_size) }
    }
}

#[global_allocator]
static ALLOCATOR: Counting = Counting;

// The bytes this thread allocates while `work` runs.
fn allocated<T>(work: impl FnOnce() -> T) -> (T, usize) {
    let before = ALLOCATED.get();
    let result = work();
    (result, ALLOCATED.get() - before)
}

const MEGABYTE: usize = 1024 * 1024;

#[test]
fn appending_in_a_loop_does_not_copy_the_array() {
    // reduce range(10000) as $i ([]; . + [$i]); copying on each append would allocate ~800 MB.
    let (array, bytes) = allocated(|| {
        let mut array = JqArray::empty();
        for i in 0..10000 {
            array = array.concat(JqArray::of(&[JqValue::Number(i as f64)]));
        }
        array
    });
    assert_eq!(array.count(), 10000);
    assert_eq!(array.get(9999).as_number(), 9999.0);
    assert!(bytes < 4 * MEGABYTE, "{bytes} bytes");
}

#[test]
fn adding_keys_in_a_loop_does_not_copy_the_object() {
    let (object, bytes) = allocated(|| {
        let mut object = JqObject::empty();
        for i in 0..10000 {
            object = object.merge(JqObject::empty().set(Arc::new(format!("k{i}")), JqValue::Number(i as f64)));
        }
        object
    });
    assert_eq!(object.count(), 10000);
    assert_eq!(object.get("k1234").map(JqValue::as_number), Some(1234.0));
    assert!(bytes < 16 * MEGABYTE, "{bytes} bytes");
}

// Programs whose accumulator grows 10,000 times (4,000 for the update): the output, and the most
// the evaluation may allocate. A copy per step would allocate hundreds of megabytes.
#[test]
fn accumulators_grow_in_place() {
    let cases = [
        ("reduce range(10000) as $i ([]; . + [$i]) | length", "10000", 4),
        ("[foreach range(10000) as $i ([]; . + [$i]; length)] | length", "10000", 4),
        ("reduce range(10000) as $i ([]; .[$i] = $i) | length", "10000", 8),
        ("reduce range(10000) as $i ([]; setpath([$i]; $i)) | length", "10000", 8),
        ("reduce range(10000) as $i ({}; . + {(\"k\\($i)\"): $i}) | length", "10000", 16),
        ("reduce range(10000) as $i ({}; .[\"k\\($i)\"] = $i) | length", "10000", 16),
        ("reduce range(10000) as $i ({}; setpath([\"k\\($i)\"]; $i)) | length", "10000", 16),
        ("reduce range(10000) as $i ({a: {b: []}}; .a.b += [$i]) | .a.b | length", "10000", 16),
        ("reduce range(10000) as $i (\"\"; . + \"x\") | length", "10000", 4),
        ("[range(10000) | tostring] | join(\",\") | length", "48889", 8),
        ("[range(10000)] | INDEX(.) | length", "10000", 16),
        ("[range(4000)] | .[] |= . + 1 | length", "4000", 8),
        ("[range(4000)] | map_values(. + 1) | add", "8002000", 8),
        ("[range(4000) | {a: .}] | .[].a |= . + 1 | map(.a) | add", "8002000", 16),
    ];
    for (program, expected, megabytes) in cases {
        let bound = support::bound_printer::compile(program);
        let (outputs, bytes) = allocated(|| {
            let mut outputs = Vec::new();
            jqlite::runtime::Interpreter::run(&bound, JqValue::Null, &jqlite::runtime::EvaluationOptions::default(), &mut |value| {
                outputs.push(jqlite::values::value_text::to_json(&value));
                true
            });
            outputs
        });
        assert_eq!(outputs, [expected], "{program}");
        assert!(bytes < megabytes * MEGABYTE, "{program}: {bytes} bytes");
    }
}
