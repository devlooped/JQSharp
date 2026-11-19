//! The module resolver of the host boundary.

use crate::values::JqValue;

/// Whether an `import` asks for a code module (`.jq`) or a data module (`import "p" as $name;`,
/// JSON values).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum JqModuleKind {
    /// A module of jq definitions.
    Code,
    /// A sequence of JSON values, bound as an array.
    Data,
}

/// Finds the modules of `import` and `include` directives and of `modulemeta`: maps a path, as
/// written, to a canonical path (the module's identity), then reads it.
///
/// A module is read once per canonical path while an expression is parsed. `modulemeta` calls the
/// resolver while expressions are evaluated, possibly from several threads at once, hence `Send`
/// and `Sync`.
pub trait JqResolver: Send + Sync {
    /// The canonical path of the module `path` of the given kind, imported with `metadata` (the
    /// directive's metadata object, if any) from the module at `from_path` (a canonical path, or
    /// `None` for the expression itself); `None` when there is no such module. The same canonical
    /// path means the same module.
    fn canonical_path(&self, path: &str, kind: JqModuleKind, metadata: Option<&JqValue>, from_path: Option<&str>) -> Option<String>;

    /// The text of the module at a canonical path that `canonical_path` returned.
    fn read(&self, canonical_path: &str) -> std::io::Result<String>;
}
