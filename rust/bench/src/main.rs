//! The runner of the cross-language benchmark (bench/README.md): the same command line, the same
//! three measurements and the same line of output as the reference's `CrossBench.cs`. It drives
//! the core engine (its values, JSON reader and writer), so the numbers do not include the host
//! boundary.

use std::hint::black_box;
use std::process::ExitCode;
use std::time::Instant;

use jqlite::binding::resolver::Resolver;
use jqlite::runtime::{EvaluationOptions, Interpreter, Outcome};
use jqlite::values::{JqValue, value_text};

const ROUNDS: usize = 10;

fn main() -> ExitCode {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let numbers: Vec<Option<u64>> = arguments.iter().skip(2).take(3).map(|text| text.parse().ok()).collect();
    let (&[Some(take), Some(warmup_ms), Some(measure_ms)], 6) = (numbers.as_slice(), arguments.len()) else {
        eprintln!("usage: jqlite-bench <program.jq> <input.json> <take> <warmup-ms> <measure-ms> <output-file>");
        return ExitCode::from(2);
    };
    let take = take as usize;

    let (source, input_text) = match (std::fs::read_to_string(&arguments[0]), std::fs::read_to_string(&arguments[1])) {
        (Ok(source), Ok(input_text)) => (source, input_text),
        (Err(error), _) => return failure(&format!("{}: {error}", arguments[0])),
        (_, Err(error)) => return failure(&format!("{}: {error}", arguments[1])),
    };

    let program = match Resolver::try_compile(&source, true, None) {
        Ok(program) => program,
        Err(error) => return failure(&format!("{} at {}:{}", error.message, error.line, error.column)),
    };
    let input = match value_text::try_parse(&input_text) {
        Ok(input) => input,
        Err(error) => return failure(&format!("bad input: {}", value_text::to_json(&error))),
    };
    let options = EvaluationOptions::default();

    // The check run: the outputs as canonical text, one per line.
    let mut text = String::new();
    let mut count = 0;
    let outcome = Interpreter::run(&program, input.clone(), &options, &mut |output| {
        value_text::write(&mut text, &output);
        text.push('\n');
        count += 1;
        take == 0 || count < take
    });
    if let Outcome::Error(value) | Outcome::Halt { value: Some(value), .. } = outcome {
        return failure(&format!("error: {}", value_text::to_json(&value)));
    }
    if let Err(error) = std::fs::write(&arguments[5], text) {
        return failure(&format!("{}: {error}", arguments[5]));
    }

    // compile: the program text to a bound program (the prelude is loaded already).
    let compile_ns = measure(warmup_ms, measure_ms, || {
        black_box(Resolver::try_compile(black_box(&source), true, None).is_ok());
    });

    // eval: the bound program on the input value, outputs collected as values.
    let eval_ns = measure(warmup_ms, measure_ms, || {
        let mut outputs: Vec<JqValue> = Vec::new();
        Interpreter::run(&program, input.clone(), &options, &mut |output| {
            outputs.push(output);
            take == 0 || outputs.len() < take
        });
        black_box(outputs);
    });

    // text: the input text read, the program evaluated, the outputs written as text.
    let text_ns = measure(warmup_ms, measure_ms, || {
        let value = value_text::try_parse(black_box(&input_text)).unwrap_or_default();
        let mut written = String::new();
        let mut outputs = 0;
        Interpreter::run(&program, value, &options, &mut |output| {
            value_text::write(&mut written, &output);
            written.push('\n');
            outputs += 1;
            take == 0 || outputs < take
        });
        black_box(written);
    });

    let runtime = env!("JQLITE_BENCH_RUSTC");
    println!(
        "{{\"impl\":\"rust\",\"runtime\":\"{runtime}\",\"compile_ns\":{compile_ns:.1},\"eval_ns\":{eval_ns:.1},\"text_ns\":{text_ns:.1},\"outputs\":{count}}}"
    );
    ExitCode::SUCCESS
}

fn failure(message: &str) -> ExitCode {
    eprintln!("{message}");
    ExitCode::from(1)
}

// Nanoseconds per operation: the median of ten rounds of equal size, sized from the warm-up.
fn measure(warmup_ms: u64, measure_ms: u64, mut operation: impl FnMut()) -> f64 {
    let clock = Instant::now();
    let mut warmups = 0u64;
    loop {
        operation();
        warmups += 1;
        if clock.elapsed().as_secs_f64() * 1000.0 >= warmup_ms as f64 {
            break;
        }
    }

    let per_operation_ms = clock.elapsed().as_secs_f64() * 1000.0 / warmups as f64;
    let batch = ((measure_ms as f64 / ROUNDS as f64 / per_operation_ms) as u64).max(1);

    let mut rounds = [0.0f64; ROUNDS];
    for round in &mut rounds {
        let start = Instant::now();
        for _ in 0..batch {
            operation();
        }
        *round = start.elapsed().as_secs_f64() * 1e9 / batch as f64;
    }

    rounds.sort_by(f64::total_cmp);
    (rounds[ROUNDS / 2 - 1] + rounds[ROUNDS / 2]) / 2.0
}
