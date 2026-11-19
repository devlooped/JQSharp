"""The native catalogue (Natives.cs): every builtin that spec §9 marks N, in the order of the spec.

With the definitions of ``spec/prelude.jq`` it is the whole builtin set, and what ``builtins/0``
lists. The catalogue is plain data, which the resolver binds calls to; the interpreter finds the
implementation by ``index`` in the native table.

By default a native takes value arguments (§9.1): each argument is evaluated on the input and the
native gives one output, or an error, per combination. ``takes_closures`` marks the entries of §9
that say otherwise: they receive their arguments unevaluated and decide what to evaluate.
"""

from __future__ import annotations


class NativeGroup:
    """The groups of spec §9: MODULES is the optional group of §9.11."""

    CORE = 0
    MODULES = 1


class Native:
    """One native builtin of spec §9: identity, catalogue position, and how it takes its arguments."""

    __slots__ = ("name", "arity", "group", "index", "takes_closures", "overrides_prelude", "single_when")

    def __init__(self, name: str, arity: int, group: int, index: int, takes_closures: bool, overrides_prelude: bool = False,
                 single_when: tuple[int, ...] | None = None) -> None:
        self.name = name
        self.arity = arity
        self.group = group
        self.index = index
        self.takes_closures = takes_closures
        # A native implementation of a builtin that §9 defines in the prelude, for speed (§9.1):
        # calls that would bind to the prelude definition bind to the native instead (Resolver).
        self.overrides_prelude = overrides_prelude
        # The arguments that must be single for a call to be single (spec §7.7), or None when a call
        # never is. A native with value arguments gives one output per combination of them: all of
        # them. A native that takes closures says it in _SINGLES.
        self.single_when = single_when

    @property
    def is_internal(self) -> bool:
        """Internal builtins (a name starting with ``_``) are callable but not listed by ``builtins`` (§9.1)."""
        return self.name.startswith("_")

    def __repr__(self) -> str:
        return f"{self.name}/{self.arity}"


_CORE = [
    # §9.2 Core
    "empty/0", "error/0", "type/0", "length/0", "utf8bytelength/0", "builtins/0", "halt/0", "halt_error/1", "last/1",
    "debug/0", "stderr/0",
    # §9.3 Types and selection
    "infinite/0", "nan/0", "isinfinite/0", "isnan/0", "isnormal/0",
    # §9.4 Objects, arrays and collections
    "keys/0", "keys_unsorted/0", "has/1", "contains/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
    "sort/0", "_sort_by_impl/1", "_group_by_impl/1", "_min_by_impl/1", "_max_by_impl/1", "unique/0", "min/0", "max/0", "bsearch/1",
    # §9.5 Paths
    "path/1", "getpath/1", "setpath/2", "delpaths/1",
    # §9.6 Strings
    "tostring/0", "tojson/0", "fromjson/0", "tonumber/0", "toboolean/0", "format/1", "explode/0", "implode/0", "ascii_downcase/0",
    "ascii_upcase/0", "startswith/1", "endswith/1", "trim/0", "ltrim/0", "rtrim/0", "split/1", "_strindices/1", "test/2", "match/2",
    # §9.7 Dates
    "now/0", "gmtime/0", "localtime/0", "mktime/0", "strftime/1", "strflocaltime/1", "strptime/1",
    # §9.8 Math: exactly specified, library functions, defined by formula
    "floor/0", "ceil/0", "trunc/0", "round/0", "nearbyint/0", "rint/0", "fabs/0", "sqrt/0", "fmod/2", "remainder/2", "drem/2", "fma/3",
    "fmin/2", "fmax/2", "fdim/2", "copysign/2", "nextafter/2", "nexttoward/2", "ldexp/2", "scalbln/2", "scalb/2", "logb/0",
    "significand/0", "frexp/0", "modf/0",
    "acos/0", "asin/0", "atan/0", "acosh/0", "asinh/0", "atanh/0", "cbrt/0", "cos/0", "cosh/0", "exp/0", "exp2/0",
    "exp10/0", "expm1/0", "log/0", "log10/0", "log1p/0", "log2/0", "sin/0", "sinh/0", "tan/0", "tanh/0", "atan2/2", "pow/2", "hypot/2",
    "erf/0", "erfc/0", "tgamma/0", "lgamma/0", "gamma/0", "lgamma_r/0", "j0/0", "j1/0", "y0/0", "y1/0", "jn/2", "yn/2",
]

_MODULES = [
    # §9.11 Modules
    "modulemeta/0",
]

# §9.2, §9.4, §9.5, §9.6: no output, a signal, generators, path-capable, many outputs.
_CLOSURES = frozenset([
    "empty/0", "halt/0", "halt_error/1", "last/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
    "path/1", "getpath/1", "match/2",
])

# §7.7: the natives that take closures and yet give at most one output, when these arguments are
# single (none: always). The two overrides are single when their prelude definitions are.
_SINGLES: dict[str, tuple[int, ...]] = {
    "empty/0": (), "halt/0": (), "halt_error/1": (), "last/1": (), "getpath/1": (0,), "until/2": (0, 1),
    "_modify/2": (), "_assign/2": (1,),
}

# The overrides of prelude builtins, after the natives of §9; they take closures.
_OVERRIDES = ["_modify/2", "_assign/2"]


def _single_when(signature: str, arity: int, takes_closures: bool) -> tuple[int, ...] | None:
    if takes_closures:
        return _SINGLES.get(signature)
    return tuple(range(arity))


def _build() -> tuple[Native, ...]:
    natives: list[Native] = []
    for signatures, group in ((_CORE, NativeGroup.CORE), (_MODULES, NativeGroup.MODULES)):
        for signature in signatures:
            name, arity = signature.split("/")
            closures = signature in _CLOSURES
            natives.append(Native(name, int(arity), group, len(natives), closures, False,
                                  _single_when(signature, int(arity), closures)))
    for signature in _OVERRIDES:
        name, arity = signature.split("/")
        natives.append(Native(name, int(arity), NativeGroup.CORE, len(natives), True, True, _single_when(signature, int(arity), True)))
    return tuple(natives)


ALL: tuple[Native, ...] = _build()

_BY_KEY: dict[tuple[str, int], Native] = {(n.name, n.arity): n for n in ALL}


def find(name: str, arity: int) -> Native | None:
    """The native ``name``/``arity``, or None."""
    return _BY_KEY.get((name, arity))


def override(name: str, arity: int) -> Native | None:
    """The native that overrides the prelude builtin ``name``/``arity``, or None."""
    native = _BY_KEY.get((name, arity))
    return native if native is not None and native.overrides_prelude else None
