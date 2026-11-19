//! A resolver over the file system.

use std::path::{Path, PathBuf};

use crate::jq_resolver::{JqModuleKind, JqResolver};
use crate::values::JqValue;

/// A [`JqResolver`] over the file system: a path is relative to the directory of the importing
/// module, or to the base directory for the expression itself, and gets the extension `.jq`
/// (code) or `.json` (data) when it has none. The canonical path is the absolute file path.
#[derive(Clone, Debug)]
pub struct JqFileResolver {
    base_directory: PathBuf,
}

impl JqFileResolver {
    /// A resolver whose expression's own directives are relative to `base_directory`.
    pub fn new(base_directory: impl AsRef<Path>) -> JqFileResolver {
        let base_directory = base_directory.as_ref();
        JqFileResolver { base_directory: std::path::absolute(base_directory).unwrap_or_else(|_| base_directory.to_path_buf()) }
    }

    /// The directory that the expression's own directives are relative to.
    pub fn base_directory(&self) -> &Path {
        &self.base_directory
    }
}

impl JqResolver for JqFileResolver {
    fn canonical_path(&self, path: &str, kind: JqModuleKind, _: Option<&JqValue>, from_path: Option<&str>) -> Option<String> {
        let directory = from_path.and_then(|from| Path::new(from).parent()).unwrap_or(&self.base_directory);
        let mut file = std::path::absolute(directory.join(path)).ok()?;
        if file.extension().is_none() {
            file.set_extension(if kind == JqModuleKind::Data { "json" } else { "jq" });
        }
        if file.is_file() { Some(file.to_string_lossy().into_owned()) } else { None }
    }

    fn read(&self, canonical_path: &str) -> std::io::Result<String> {
        std::fs::read_to_string(canonical_path)
    }
}
