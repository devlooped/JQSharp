//! A public `JqResolver` as the module source of the core (spec §9.11, §12).

use std::sync::Arc;

use crate::binding::resolver::binding_messages;
use crate::binding::{ModuleKind, ModuleSource};
use crate::jq_resolver::{JqModuleKind, JqResolver};
use crate::values::JqValue;

pub struct ResolverSource {
    resolver: Arc<dyn JqResolver>,
}

impl ResolverSource {
    pub fn new(resolver: Arc<dyn JqResolver>) -> ResolverSource {
        ResolverSource { resolver }
    }
}

impl ModuleSource for ResolverSource {
    fn try_resolve(&self, path: &str, kind: ModuleKind, metadata: Option<&JqValue>, from: Option<&str>) -> Result<String, String> {
        let kind = if kind == ModuleKind::Data { JqModuleKind::Data } else { JqModuleKind::Code };
        self.resolver.canonical_path(path, kind, metadata, from).ok_or_else(|| binding_messages::module_not_found(path))
    }

    // An I/O error of the resolver becomes the binding error.
    fn try_read(&self, identity: &str) -> Result<String, String> {
        self.resolver.read(identity).map_err(|error| error.to_string())
    }
}
