package binding

import (
	"strings"

	"github.com/devlooped/jqlite/go/internal/values"
)

// natives.go is the reference's Builtins/Natives.cs. It lives in the binding package because Go
// forbids import cycles: the resolver binds calls to this catalogue, and the builtins package,
// which implements the natives, imports the binding and runtime packages.

// NativeGroup is a group of spec §9: GroupModules is the optional group of §9.11.
type NativeGroup int

// The groups.
const (
	GroupCore NativeGroup = iota
	GroupModules
)

// Native is one native builtin of spec §9: identity, catalogue position, and how it takes its
// arguments. The catalogue is plain data, which the resolver binds calls to; the interpreter finds
// the implementation by Index in the NativeTable.
//
// By default a native takes value arguments (§9.1): each argument is evaluated on the input and
// the native gives one output, or an error, per combination. TakesClosures marks the entries of §9
// that say otherwise: they receive their arguments unevaluated and decide what to evaluate, in
// which mode, and how many outputs to give.
//
// OverridesPrelude marks a native implementation of a builtin that §9 defines in the prelude, for
// speed (§9.1 allows it when results, errors and paths are identical): calls that would bind to
// the prelude definition bind to the native instead (Resolver). It is not one of the natives of §9.
//
// SingleWhen is the arguments that must be single for a call to be single (spec §7.7), or nil when
// a call never is. A native with value arguments gives one output per combination of them: all of
// them. A native that takes closures says it in the singles list of the catalogue.
type Native struct {
	Name             string
	Arity            int
	Group            NativeGroup
	Index            int
	TakesClosures    bool
	OverridesPrelude bool
	SingleWhen       []int
}

// §7.7: the natives that take closures and yet give at most one output, when these arguments are
// single (none: always). The two overrides are single when their prelude definitions are.
var singles = map[string][]int{
	"empty/0": {}, "halt/0": {}, "halt_error/1": {}, "last/1": {}, "getpath/1": {0}, "until/2": {0, 1},
	"_modify/2": {}, "_assign/2": {1},
}

func singleWhen(signature string, arity int, takesClosures bool) []int {
	if takesClosures {
		return singles[signature]
	}
	all := make([]int, arity)
	for slot := range all {
		all[slot] = slot
	}
	return all
}

// IsInternal: internal builtins (a name starting with _) are callable but not listed by builtins (§9.1).
func (n *Native) IsInternal() bool { return strings.HasPrefix(n.Name, "_") }

func (n *Native) String() string { return n.Name + "/" + itoa(n.Arity) }

func itoa(n int) string { return values.CodePoints.Decimal(n) }

type nativeCatalogue struct {
	// All is every native, by Index.
	All   []*Native
	byKey map[string]*Native
}

// Natives is the native catalogue: every builtin that spec §9 marks N, in the order of the spec.
// With the definitions of spec/prelude.jq it is the whole builtin set, and what builtins/0 lists.
var Natives = buildCatalogue(
	[]string{
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
	},
	[]string{
		// §9.11 Modules
		"modulemeta/0",
	},
	[]string{
		// §9.2, §9.4, §9.5, §9.6: no output, a signal, generators, path-capable, many outputs.
		"empty/0", "halt/0", "halt_error/1", "last/1", "range/2", "limit/2", "skip/2", "while/2", "until/2", "repeat/1",
		"path/1", "getpath/1", "match/2",
	},
	// The overrides of prelude builtins, after the natives of §9; they take closures.
	[]string{"_modify/2", "_assign/2"},
)

func buildCatalogue(core, modules, closures, overrides []string) *nativeCatalogue {
	catalogue := &nativeCatalogue{byKey: map[string]*Native{}}
	add := func(signature string, group NativeGroup, takesClosures, overrides bool) {
		slash := strings.IndexByte(signature, '/')
		arity := 0
		for _, digit := range signature[slash+1:] {
			arity = arity*10 + int(digit-'0')
		}
		native := &Native{signature[:slash], arity, group, len(catalogue.All), takesClosures, overrides,
			singleWhen(signature, arity, takesClosures)}
		catalogue.All = append(catalogue.All, native)
		catalogue.byKey[signature] = native
	}
	isClosure := func(signature string) bool {
		for _, c := range closures {
			if c == signature {
				return true
			}
		}
		return false
	}
	for _, signature := range core {
		add(signature, GroupCore, isClosure(signature), false)
	}
	for _, signature := range modules {
		add(signature, GroupModules, isClosure(signature), false)
	}
	for _, signature := range overrides {
		add(signature, GroupCore, true, true)
	}
	return catalogue
}

// Find gives the native name/arity, or nil.
func (c *nativeCatalogue) Find(name string, arity int) *Native {
	return c.byKey[name+"/"+itoa(arity)]
}

// Override gives the native that overrides the prelude builtin name/arity, or nil.
func (c *nativeCatalogue) Override(name string, arity int) *Native {
	if native := c.Find(name, arity); native != nil && native.OverridesPrelude {
		return native
	}
	return nil
}
