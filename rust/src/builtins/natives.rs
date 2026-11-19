//! The native catalogue: every builtin that spec §9 marks N, in the order of the spec. With the
//! definitions of `spec/prelude.jq` it is the whole builtin set, and what `builtins/0` lists.
//! Plain data, which the resolver binds calls to; the interpreter finds the implementation by
//! `Native::index` in `native_table`.

use std::collections::HashMap;
use std::sync::LazyLock;

/// The groups of spec §9: `Modules` is the optional group of §9.11.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeGroup {
    Core,
    Modules,
}

/// One native builtin of spec §9: identity, catalogue position, and how it takes its arguments.
///
/// By default a native takes value arguments (§9.1): each argument is evaluated on the input and
/// the native gives one output, or an error, per combination. `takes_closures` marks the entries
/// of §9 that say otherwise: they receive their arguments unevaluated and decide what to evaluate,
/// in which mode, and how many outputs to give.
#[derive(Debug)]
pub struct Native {
    pub name: &'static str,
    pub arity: usize,
    pub group: NativeGroup,
    pub index: usize,
    pub takes_closures: bool,
    /// A native implementation of a builtin that §9 defines in the prelude, for speed (§9.1 allows
    /// it when results, errors and paths are identical): calls that would bind to the prelude
    /// definition bind to the native instead (Resolver). It is not one of the natives of §9.
    pub overrides_prelude: bool,
    /// The arguments that must be single for a call to be single (spec §7.7), or `None` when a
    /// call never is. A native with value arguments gives one output per combination of them: all
    /// of them. A native that takes closures says it in `SINGLES`.
    pub single_when: Option<Vec<usize>>,
}

impl Native {
    /// Internal builtins (a name starting with `_`) are callable but not listed by `builtins` (§9.1).
    pub fn is_internal(&self) -> bool {
        self.name.starts_with('_')
    }

    /// `name/arity`.
    pub fn signature(&self) -> String {
        format!("{}/{}", self.name, self.arity)
    }
}

// §7.7: the natives that take closures and yet give at most one output, when these arguments are
// single (none: always). The two overrides are single when their prelude definitions are.
const SINGLES: [(&str, &[usize]); 8] = [
    ("empty/0", &[]),
    ("halt/0", &[]),
    ("halt_error/1", &[]),
    ("last/1", &[]),
    ("getpath/1", &[0]),
    ("until/2", &[0, 1]),
    ("_modify/2", &[]),
    ("_assign/2", &[1]),
];

#[rustfmt::skip]
const CORE: &[&str] = &[
    // §9.2 Core
    "empty/0", "error/0", "type/0", "length/0", "utf8bytelength/0", "builtins/0", "halt/0", "halt_error/1", "last/1",
    "debug/0", "stderr/0",
    // §9.3 Types and selection
    "infinite/0", "nan/0", "isinfinite/0", "isnan/0", "isnormal/0",
    // §9.4 Objects, arrays and collections
    "keys/0", "keys_unsorted/0", "has/1", "contains/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
    "sort/0", "_sort_by_impl/1", "_group_by_impl/1", "_min_by_impl/1", "_max_by_impl/1", "unique/0", "min/0", "max/0", "bsearch/1",
    // §9.5 Paths
    "path/1", "getpath/1", "setpath/2", "delpaths/1",
    // §9.6 Strings
    "tostring/0", "tojson/0", "fromjson/0", "tonumber/0", "toboolean/0", "format/1", "explode/0", "implode/0", "ascii_downcase/0", "ascii_upcase/0",
    "startswith/1", "endswith/1", "trim/0", "ltrim/0", "rtrim/0", "split/1", "_strindices/1", "test/2", "match/2",
    // §9.7 Dates
    "now/0", "gmtime/0", "localtime/0", "mktime/0", "strftime/1", "strflocaltime/1", "strptime/1",
    // §9.8 Math: exactly specified, library functions, defined by formula
    "floor/0", "ceil/0", "trunc/0", "round/0", "nearbyint/0", "rint/0", "fabs/0", "sqrt/0", "fmod/2", "remainder/2", "drem/2", "fma/3",
    "fmin/2", "fmax/2", "fdim/2", "copysign/2", "nextafter/2", "nexttoward/2", "ldexp/2", "scalbln/2", "scalb/2", "logb/0",
    "significand/0", "frexp/0", "modf/0",
    "acos/0", "asin/0", "atan/0", "acosh/0", "asinh/0", "atanh/0", "cbrt/0", "cos/0", "cosh/0", "exp/0", "exp2/0",
    "exp10/0", "expm1/0", "log/0", "log10/0", "log1p/0", "log2/0", "sin/0", "sinh/0", "tan/0", "tanh/0", "atan2/2", "pow/2", "hypot/2",
    "erf/0", "erfc/0", "tgamma/0", "lgamma/0", "gamma/0", "lgamma_r/0", "j0/0", "j1/0", "y0/0", "y1/0", "jn/2", "yn/2",
];

// §9.11 Modules
const MODULES: &[&str] = &["modulemeta/0"];

// §9.2, §9.4, §9.5, §9.6: no output, a signal, generators, path-capable, many outputs.
#[rustfmt::skip]
const CLOSURES: &[&str] = &[
    "empty/0", "halt/0", "halt_error/1", "last/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
    "path/1", "getpath/1", "match/2",
];

// The overrides of prelude builtins, after the natives of §9; they take closures.
const OVERRIDES: &[&str] = &["_modify/2", "_assign/2"];

struct Catalogue {
    all: Vec<Native>,
    by_key: HashMap<(&'static str, usize), usize>,
}

static CATALOGUE: LazyLock<Catalogue> = LazyLock::new(|| {
    let mut all = Vec::with_capacity(CORE.len() + MODULES.len() + OVERRIDES.len());
    add(&mut all, CORE, NativeGroup::Core, false);
    add(&mut all, MODULES, NativeGroup::Modules, false);
    add(&mut all, OVERRIDES, NativeGroup::Core, true);
    let by_key = all.iter().map(|native| ((native.name, native.arity), native.index)).collect();
    Catalogue { all, by_key }
});

fn add(natives: &mut Vec<Native>, signatures: &[&'static str], group: NativeGroup, overrides: bool) {
    for signature in signatures {
        let (name, arity) = split(signature);
        let takes_closures = overrides || CLOSURES.contains(signature);
        natives.push(Native {
            name,
            arity,
            group,
            index: natives.len(),
            takes_closures,
            overrides_prelude: overrides,
            single_when: single_when(signature, arity, takes_closures),
        });
    }
}

fn split(signature: &'static str) -> (&'static str, usize) {
    let (name, arity) = signature.split_once('/').unwrap_or((signature, "0"));
    (name, arity.parse().unwrap_or(0))
}

fn single_when(signature: &str, arity: usize, takes_closures: bool) -> Option<Vec<usize>> {
    if takes_closures {
        return SINGLES.iter().find(|(single, _)| *single == signature).map(|(_, arguments)| arguments.to_vec());
    }

    Some((0..arity).collect())
}

/// Every native, in catalogue order.
pub fn all() -> &'static [Native] {
    &CATALOGUE.all
}

/// The native `name/arity`, if any.
pub fn find(name: &str, arity: usize) -> Option<&'static Native> {
    CATALOGUE.by_key.get(&(name, arity)).map(|index| &CATALOGUE.all[*index])
}

/// The native that overrides the prelude builtin `name/arity`, if any.
pub fn override_of(name: &str, arity: usize) -> Option<&'static Native> {
    find(name, arity).filter(|native| native.overrides_prelude)
}
