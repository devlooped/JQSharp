//! Gives the runner the version of the compiler that builds it (`rustc 1.96.0`), which it reports
//! as its runtime: the standard library has no function for it.

use std::env;
use std::process::Command;

fn main() {
    let rustc = env::var("RUSTC").unwrap_or_else(|_| "rustc".to_owned());
    let version = Command::new(rustc).arg("--version").output().ok().and_then(|output| String::from_utf8(output.stdout).ok());
    // "rustc 1.96.0 (ac68faa20 2026-05-25)": the name and the number.
    let version = version.as_deref().map(|text| text.split_whitespace().take(2).collect::<Vec<_>>().join(" ")).unwrap_or_else(|| "rustc".to_owned());
    println!("cargo:rustc-env=JQLITE_BENCH_RUSTC={version}");
    println!("cargo:rerun-if-changed=build.rs");
}
