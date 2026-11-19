//! Binding: the resolver (names to frame hops and slots, calls to function indexes), the bound
//! program, the prelude, and the optional modules group.

pub mod bound;
pub mod modules;
pub mod prelude;
pub mod resolver;

pub use bound::BoundProgram;
pub use modules::{ModuleKind, ModuleSource};
pub use resolver::Resolver;
