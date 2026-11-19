//! The modules group of spec §9.11, which a port may leave out. Everything of it is here and in
//! builtins/module_natives.rs, behind `Resolver::bind_imports`. A port without the group rejects
//! `import`, `include` and `module` in its parser, so a program never has imports: it drops this
//! file, the call of `bind_imports` and the `modules` field of `BoundProgram`, and does not list
//! `modulemeta/0`.

use std::collections::{HashMap, HashSet};
use std::fmt;
use std::rc::Rc;
use std::sync::Arc;

use crate::syntax::ast as s;
use crate::syntax::ast::Position;
use crate::syntax::parser::Parser;
use crate::syntax::syntax_error::SyntaxError;
use crate::values::{JqArray, JqObject, JqValue, value_text};

use super::resolver::{Entry, EntryKind, Resolver, binding_messages};

/// What an import asks for (§9.11): a code module or a data module.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ModuleKind {
    Code,
    Data,
}

/// What the core needs from its host to load modules (§9.11, §12): the host decides where a module
/// comes from. Called while binding, and at run time by `modulemeta`, possibly from several
/// threads at once.
pub trait ModuleSource: Send + Sync {
    /// Resolves the module `path` (as written in the directive) of the given kind, imported with
    /// `metadata` (the directive's metadata object, if any), from the module `from` (its identity,
    /// or `None` for the main program), to the module's identity: the same identity means the same
    /// module. On failure, the error is a message naming the path.
    fn try_resolve(&self, path: &str, kind: ModuleKind, metadata: Option<&JqValue>, from: Option<&str>) -> Result<String, String>;

    /// The source text of a module `try_resolve` found: read once per identity and binding.
    fn try_read(&self, identity: &str) -> Result<String, String>;
}

/// The modules of a bound program, kept for `modulemeta`: the host's source and the description
/// of every module loaded while binding. Immutable after binding.
pub struct ModuleTable {
    source: Arc<dyn ModuleSource>,
    descriptions: HashMap<String, JqValue>,
}

impl fmt::Debug for ModuleTable {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ModuleTable").field("descriptions", &self.descriptions).finish()
    }
}

impl ModuleTable {
    /// `modulemeta` of a path resolved as from the main program: the loaded module's description,
    /// or, for a module the program does not import, the description of its parse.
    pub fn try_describe(&self, path: &str) -> Result<JqValue, String> {
        let identity = self.source.try_resolve(path, ModuleKind::Code, None, None)?;
        if let Some(description) = self.descriptions.get(&identity) {
            return Ok(description.clone());
        }
        let text = self.source.try_read(&identity)?;
        match Parser::try_parse_module(&text) {
            Ok(module) => Ok(describe(&module)),
            Err(syntax) => Err(binding_messages::in_module(&syntax.message, &identity, syntax.line, syntax.column)),
        }
    }
}

fn key(name: &str) -> Arc<String> {
    Arc::new(name.to_owned())
}

/// What `modulemeta` outputs for a module (§9.11): its metadata keys, then `deps` (per directive:
/// its metadata keys, then `as` unless it is an include, `is_data`, `relpath`), then `defs`, each
/// top-level `name/arity` once, by first definition.
pub fn describe(module: &s::Program) -> JqValue {
    let description = match &module.metadata {
        Some(JqValue::Object(metadata)) => metadata.clone(),
        _ => JqObject::empty(),
    };
    let mut deps = Vec::with_capacity(module.imports.len());
    for import in &module.imports {
        let mut dep = match &import.metadata {
            Some(JqValue::Object(metadata)) => metadata.clone(),
            _ => JqObject::empty(),
        };
        if let Some(alias) = &import.alias {
            dep.insert(key("as"), JqValue::from_text(alias));
        }
        dep.insert(key("is_data"), JqValue::from_bool(import.is_data));
        dep.insert(key("relpath"), JqValue::from_text(&import.path));
        deps.push(JqValue::Object(dep));
    }

    let mut defs = Vec::new();
    let mut seen = HashSet::new();
    for definition in &module.definitions {
        let signature = format!("{}/{}", definition.name, definition.params.len());
        if seen.insert(signature.clone()) {
            defs.push(JqValue::from_string(signature));
        }
    }

    JqValue::Object(description.set(key("deps"), JqValue::Array(JqArray::from_owned(deps))).set(key("defs"), JqValue::Array(JqArray::from_owned(defs))))
}

/// A loaded module.
pub(super) struct Module {
    pub(super) identity: String,
    /// A code module's parse.
    pub(super) program: Option<Rc<s::Program>>,
    /// A data module's values, as one array.
    pub(super) data: JqValue,
    /// The identities the module's directives resolved to, in order.
    pub(super) imports: Vec<String>,
    /// A code module's exports once bound: each top-level name/arity with its function, the last
    /// definition winning, in the order of their first definition.
    pub(super) exports: Vec<(String, usize, usize)>,
    // On the loading stack: importing it again is a cycle.
    loading: bool,
}

/// Loads the modules of one binding: resolves and parses each identity once, depth first from the
/// main program's directives, with an explicit stack (an import chain of any length is no
/// recursion), detecting cycles. Code modules are then bound one after the other, each after the
/// modules it imports, each in a scope of its own (Resolver). Loaded modules live as long as the
/// binding: a host that wants to share parses between programs caches in its source.
pub struct ModuleLoader {
    source: Option<Arc<dyn ModuleSource>>,
    pub(super) modules: HashMap<String, Module>,
}

// One module being loaded: its identity (None for the main program), its directives, the next
// one to resolve, and what they resolved to.
struct Loading {
    module: Option<String>,
    program: Option<Rc<s::Program>>,
    next: usize,
    identities: Vec<String>,
}

impl ModuleLoader {
    pub fn new(source: Option<Arc<dyn ModuleSource>>) -> ModuleLoader {
        ModuleLoader { source, modules: HashMap::new() }
    }

    /// Loads what `imports` (the main program's directives) need: the identities they resolved to,
    /// and the code modules to bind, each after those it imports.
    pub(super) fn try_load(&mut self, imports: &[s::Import]) -> Result<(Vec<String>, Vec<String>), SyntaxError> {
        let mut order = Vec::new();
        let mut frames = vec![Loading { module: None, program: None, next: 0, identities: Vec::new() }];
        loop {
            let depth = frames.len();
            let program = frames[depth - 1].program.clone();
            let directives: &[s::Import] = match &program {
                Some(program) => &program.imports,
                None => imports,
            };
            let frame = &mut frames[depth - 1];
            let i = frame.next;
            if i == directives.len() {
                let Some(done) = frames.pop() else {
                    break;
                };
                match done.module {
                    Some(identity) => {
                        if let Some(module) = self.modules.get_mut(&identity) {
                            module.loading = false;
                            module.imports = done.identities;
                        }
                        order.push(identity);
                    }
                    None => return Ok((done.identities, order)),
                }
                continue;
            }

            frame.next += 1;
            let import = &directives[i];
            let kind = if import.is_data { ModuleKind::Data } else { ModuleKind::Code };
            let from = frame.module.clone();
            let identity = match self.try_resolve(import, kind, from.as_deref()) {
                Ok(identity) => identity,
                Err(failure) => return Err(error(failure, import.position, from)),
            };

            frame.identities.push(identity.clone());
            if let Some(loaded) = self.modules.get(&identity) {
                if loaded.loading {
                    return Err(error(binding_messages::circular_import(&identity), import.position, from));
                }

                continue;
            }

            let text = match self.source.as_ref().map(|source| source.try_read(&identity)) {
                Some(Ok(text)) => text,
                Some(Err(failure)) => return Err(error(failure, import.position, from)),
                None => return Err(error(binding_messages::module_not_found(&import.path), import.position, from)),
            };

            if kind == ModuleKind::Data {
                let values = match value_text::try_parse_sequence(&text) {
                    Ok(values) => values,
                    Err(invalid) => return Err(error(binding_messages::invalid_data(&identity, invalid.as_string()), import.position, from)),
                };
                self.modules
                    .insert(identity.clone(), Module { identity, program: None, data: values, imports: Vec::new(), exports: Vec::new(), loading: false });
                continue;
            }

            let program = match Parser::try_parse_module(&text) {
                Ok(program) => Rc::new(program),
                Err(mut syntax) => {
                    syntax.module = Some(identity);
                    return Err(syntax);
                }
            };

            self.modules.insert(
                identity.clone(),
                Module {
                    identity: identity.clone(),
                    program: Some(program.clone()),
                    data: JqValue::Null,
                    imports: Vec::new(),
                    exports: Vec::new(),
                    loading: true,
                },
            );
            frames.push(Loading { module: Some(identity), program: Some(program), next: 0, identities: Vec::new() });
        }

        Ok((Vec::new(), order))
    }

    /// The descriptions of the code modules loaded, for `modulemeta`; `None` without a source.
    pub fn table(&self) -> Option<ModuleTable> {
        let source = self.source.clone()?;
        let mut descriptions = HashMap::new();
        for module in self.modules.values() {
            if let Some(program) = &module.program {
                descriptions.insert(module.identity.clone(), describe(program));
            }
        }

        Some(ModuleTable { source, descriptions })
    }

    // Without a source no module exists.
    fn try_resolve(&self, import: &s::Import, kind: ModuleKind, from: Option<&str>) -> Result<String, String> {
        match &self.source {
            Some(source) => source.try_resolve(&import.path, kind, import.metadata.as_ref(), from),
            None => Err(binding_messages::module_not_found(&import.path)),
        }
    }
}

fn error(message: String, position: Position, module: Option<String>) -> SyntaxError {
    SyntaxError { message, line: position.line, column: position.column, module }
}

impl Resolver<'_> {
    /// Where modules enter a scope (§9.11), before the top-level definitions: the main program
    /// loads and binds every module it needs first. Each directive then adds its module's exports
    /// as definitions at that point: `alias::f` for `import`, `f` for `include`, so that later
    /// definitions and directives shadow them as usual; a data import adds the constants `$alias`
    /// and `$alias::alias`.
    pub(super) fn bind_imports(&mut self, imports: &[s::Import]) -> bool {
        if imports.is_empty() {
            return true;
        }

        let Some(mut loader) = self.loader.take() else {
            // The prelude has no imports.
            return false;
        };
        let ok = self.bind_imports_with(&mut loader, imports);
        self.loader = Some(loader);
        ok
    }

    fn bind_imports_with(&mut self, loader: &mut ModuleLoader, imports: &[s::Import]) -> bool {
        let identities;
        if let Some(module) = &self.module {
            identities = loader.modules.get(module).map(|module| module.imports.clone()).unwrap_or_default();
        } else {
            let order;
            (identities, order) = match loader.try_load(imports) {
                Ok(loaded) => loaded,
                Err(failure) => {
                    self.error.get_or_insert(failure);
                    return false;
                }
            };

            for code in order {
                if !self.bind_module(loader, &code) {
                    return false;
                }
            }
        }

        for (import, identity) in imports.iter().zip(&identities) {
            let Some(loaded) = loader.modules.get(identity) else {
                continue;
            };
            let alias = import.alias.as_deref().unwrap_or("");
            if import.is_data {
                self.shared.constants.push(loaded.data.clone());
                let slot = self.shared.constants.len() - 1;
                self.scope.push(Entry { kind: EntryKind::Constant, name: alias.to_owned(), arity: 0, depth: 0, slot, top_level: false });
                self.scope.push(Entry { kind: EntryKind::Constant, name: format!("{alias}::{alias}"), arity: 0, depth: 0, slot, top_level: false });
                continue;
            }

            let prefix = if import.alias.is_none() { String::new() } else { format!("{alias}::") };
            for (name, arity, function) in &loaded.exports {
                self.scope.push(Entry {
                    kind: EntryKind::Function,
                    name: format!("{prefix}{name}"),
                    arity: *arity,
                    depth: 0,
                    slot: *function,
                    top_level: false,
                });
            }
        }

        true
    }

    // Binds a code module in a scope of its own and records its exports.
    fn bind_module(&mut self, loader: &mut ModuleLoader, identity: &str) -> bool {
        let Some(program) = loader.modules.get(identity).and_then(|module| module.program.clone()) else {
            return true;
        };

        // The module's resolver takes the tables of the whole program and the loader, and gives them back.
        let shared = std::mem::take(&mut self.shared);
        let mut resolver = Resolver::new(self.prelude, self.native_overrides, Some(std::mem::replace(loader, ModuleLoader::new(None))), Some(shared));
        resolver.module = Some(identity.to_owned());
        let bound = resolver.bind_program(&program).is_some();
        self.shared = std::mem::take(&mut resolver.shared);
        if let Some(used) = resolver.loader.take() {
            *loader = used;
        }
        if !bound {
            if self.error.is_none() {
                self.error = resolver.error.take();
            }
            return false;
        }

        let mut exports: Vec<(String, usize, usize)> = Vec::new();
        for index in &resolver.exports {
            let Some(function) = &self.shared.functions[*index] else {
                continue;
            };
            match exports.iter_mut().find(|(name, arity, _)| *name == function.name && *arity == function.arity) {
                Some(export) => export.2 = *index,
                None => exports.push((function.name.clone(), function.arity, *index)),
            }
        }
        if let Some(module) = loader.modules.get_mut(identity) {
            module.exports = exports;
        }

        true
    }
}
