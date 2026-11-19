//! `spec/prelude.jq`, parsed and bound once against the natives, then shared by every program
//! (immutable, so safe to use from any thread). A program's function table starts with
//! `Prelude::functions`, so prelude calls keep their indices in every program.
//!
//! Host-specific: the prelude text is embedded in the crate with `include_str!`, from the shared
//! file `spec/prelude.jq` of the repository (the reference reads an assembly resource).

use std::collections::HashMap;
use std::sync::{Arc, LazyLock};

use crate::syntax::parser::Parser;

use super::bound::Function;
use super::resolver::Resolver;

/// The text of `spec/prelude.jq`, unchanged.
pub const SOURCE: &str = include_str!("../../../spec/prelude.jq");

pub struct Prelude {
    /// Every function of the prelude, nested ones included, in the order of definition.
    pub functions: Vec<Arc<Function>>,
    /// The top-level definitions: the builtins the prelude defines.
    pub definitions: Vec<Arc<Function>>,
    // name to (arity, index in functions) of the visible builtins.
    visible: HashMap<String, Vec<(usize, usize)>>,
}

static INSTANCE: LazyLock<Prelude> = LazyLock::new(|| Prelude::load(SOURCE, true));
static WITHOUT_OVERRIDES: LazyLock<Prelude> = LazyLock::new(|| Prelude::load(SOURCE, false));

impl Prelude {
    /// The prelude, with the native overrides of its builtins (`Native::overrides_prelude`).
    pub fn instance() -> &'static Prelude {
        &INSTANCE
    }

    /// The prelude exactly as written, every builtin bound to its definition: to check that the
    /// overrides change nothing.
    pub fn without_overrides() -> &'static Prelude {
        &WITHOUT_OVERRIDES
    }

    /// The index in `functions` of the builtin `name/arity`, if the prelude defines it.
    pub fn find(&self, name: &str, arity: usize) -> Option<usize> {
        self.visible.get(name)?.iter().find(|(known, _)| *known == arity).map(|(_, index)| *index)
    }

    /// Parses and binds a prelude source. The embedded prelude must bind: a failure is a defect of
    /// the build, not of a program.
    pub fn load(source: &str, native_overrides: bool) -> Prelude {
        let bound = Parser::try_parse_module(source).and_then(|module| Resolver::try_bind_prelude(&module, native_overrides));
        let (functions, definitions) = match bound {
            Ok(bound) => bound,
            Err(error) => panic!("The prelude does not bind: {} at {}:{}", error.message, error.line, error.column),
        };

        // A later definition of a name and arity replaces the earlier one.
        let mut visible: HashMap<String, Vec<(usize, usize)>> = HashMap::new();
        for definition in &definitions {
            let index = functions.iter().position(|function| Arc::ptr_eq(function, definition)).unwrap_or(0);
            let arities = visible.entry(definition.name.clone()).or_default();
            arities.retain(|(arity, _)| *arity != definition.arity);
            arities.push((definition.arity, index));
        }

        Prelude { functions, definitions, visible }
    }
}
