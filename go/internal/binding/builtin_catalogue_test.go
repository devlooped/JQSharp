package binding

import (
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"
	"testing"

	"github.com/devlooped/jqlite/go/internal/corpus"
)

// The native catalogue and the embedded prelude against spec §9: the prelude binds completely, and
// together they are exactly the builtins the spec names, natives where the spec says N and prelude
// definitions where it says P. The lists are read from the spec, so spec and code cannot drift.

func specFile(t *testing.T, name string) string {
	data, err := os.ReadFile(filepath.Join(corpus.SpecDirectory(), name))
	if err != nil {
		t.Fatal(err)
	}
	return strings.ReplaceAll(string(data), "\r\n", "\n")
}

// The copy that //go:embed reads (made by go generate) is spec/prelude.jq, line endings aside.
func TestTheEmbeddedPreludeIsTheSpecs(t *testing.T) {
	if strings.ReplaceAll(PreludeSource(), "\r\n", "\n") != specFile(t, "prelude.jq") {
		t.Error("internal/binding/prelude.jq differs from spec/prelude.jq: run go generate ./...")
	}
}

func TestThePreludeBindsAgainstTheNatives(t *testing.T) {
	prelude := PreludeInstance()
	if len(prelude.Definitions) != 102 {
		t.Errorf("%d definitions", len(prelude.Definitions))
	}
	// Nested definitions (recurse's r, walk's w) are functions of the prelude too.
	if len(prelude.Functions) <= len(prelude.Definitions) {
		t.Error("no nested functions")
	}
	signatures := map[string]bool{}
	for _, definition := range prelude.Definitions {
		signatures[definition.String()] = true
	}
	if len(signatures) != len(prelude.Definitions) {
		t.Error("repeated definitions")
	}
}

func TestAPreludeCannotUseHostVariables(t *testing.T) {
	defer func() {
		message, _ := recover().(string)
		if !strings.Contains(message, "$x is not defined at 1:8") {
			t.Errorf("panic: %q", message)
		}
	}()
	LoadPrelude("def f: $x;", true)
}

func TestTheCatalogueHasUniqueSignaturesAndIndices(t *testing.T) {
	signatures := map[string]bool{}
	for i, native := range Natives.All {
		signatures[native.String()] = true
		if native.Index != i || Natives.Find(native.Name, native.Arity) != native {
			t.Errorf("%v", native)
		}
	}
	if len(signatures) != len(Natives.All) || Natives.Find("map", 1) != nil {
		t.Error("not unique")
	}
}

func sortedKeys(set map[string]bool) []string {
	var keys []string
	for key := range set {
		keys = append(keys, key)
	}
	slices.Sort(keys)
	return keys
}

func subset(t *testing.T, what string, small, large map[string]bool) {
	for key := range small {
		if !large[key] {
			t.Errorf("%s: %s is missing", what, key)
		}
	}
}

func TestNativesAndPreludeAreTheBuiltinsOfTheSpec(t *testing.T) {
	all, native, prelude, moduleGroup := specBuiltins(t, specFile(t, "jqlite.md"))
	natives := map[string]bool{}
	overrides := map[string]bool{}
	modules := map[string]bool{}
	for _, n := range Natives.All {
		if n.OverridesPrelude {
			overrides[n.String()] = true
		} else {
			natives[n.String()] = true
		}
		if n.Group == GroupModules {
			modules[n.String()] = true
		}
	}
	defined := map[string]bool{}
	for _, f := range PreludeInstance().Definitions {
		defined[f.String()] = true
	}
	// An override implements a prelude builtin natively: it stays a prelude builtin of §9.
	subset(t, "overrides in the prelude", overrides, defined)

	union := map[string]bool{}
	for key := range natives {
		if defined[key] {
			t.Errorf("%s is both native and in the prelude", key)
		}
		union[key] = true
	}
	for key := range defined {
		union[key] = true
	}
	if !slices.Equal(sortedKeys(all), sortedKeys(union)) {
		t.Errorf("spec %v\ncode %v", sortedKeys(all), sortedKeys(union))
	}
	subset(t, "natives of the spec", natives, native)
	subset(t, "prelude of the spec", defined, prelude)
	if !slices.Equal(sortedKeys(moduleGroup), sortedKeys(modules)) {
		t.Errorf("modules: %v vs %v", sortedKeys(moduleGroup), sortedKeys(modules))
	}
}

func TestInternalBuiltinsStartWithAnUnderscore(t *testing.T) {
	internal := map[string]bool{}
	for _, n := range Natives.All {
		if n.IsInternal() && !n.OverridesPrelude {
			internal[n.String()] = true
		}
	}
	if !slices.Equal([]string{"_group_by_impl/1", "_max_by_impl/1", "_min_by_impl/1", "_sort_by_impl/1", "_strindices/1"}, sortedKeys(internal)) {
		t.Errorf("%v", sortedKeys(internal))
	}
	defined := map[string]bool{}
	for _, f := range PreludeInstance().Definitions {
		if strings.HasPrefix(f.Name, "_") {
			defined[f.String()] = true
		}
	}
	if !slices.Equal([]string{"_assign/2", "_flatten/1", "_modify/2"}, sortedKeys(defined)) {
		t.Errorf("%v", sortedKeys(defined))
	}
}

var (
	signatures  = regexp.MustCompile("`([A-Za-z_][A-Za-z0-9_]*/[0-9]+)`")
	libraryList = regexp.MustCompile("(?m)^\\*\\*Library functions\\*\\*(.*?`([a-z0-9 ]+)` \\(one input\\).*)$")
	firstCells  = regexp.MustCompile(`(?m)^\| ([^|\n]*) \|`)
	headings    = regexp.MustCompile("(?m)^`[A-Za-z_][A-Za-z0-9_]*/[0-9]+`(, `[A-Za-z_][A-Za-z0-9_]*/[0-9]+`)*")
	moduleGroup = regexp.MustCompile(`A port MAY leave out the whole group:[^.]*\.`)
	formulas    = regexp.MustCompile(`(?m)^\s*([a-z][a-z0-9]*)\(x\):`)
	kindRows    = regexp.MustCompile(`(?m)^\| ([^|]*) \| ([NP]) \|`)
)

func addSignatures(set map[string]bool, text string) {
	for _, m := range signatures.FindAllStringSubmatch(text, -1) {
		set[m[1]] = true
	}
}

// specBuiltins reads section 9 of spec/jqlite.md, only where it declares builtins, so that examples
// in its prose cannot add any: the first cell of each table row; the §9.8 library paragraph (its
// signatures and its list of names "(one input)"); the lines that start with signatures (the §9.8
// formula headings and abs/0); the functions §9.8 defines as `name(x):`; and the group sentence of
// §9.11. Rows whose K column says N or P give the kind; §9.8 is native but abs/0.
func specBuiltins(t *testing.T, text string) (all, native, prelude, modules map[string]bool) {
	section := specSection(t, text, "## 9. ", "## 10. ")
	math := specSection(t, section, "### 9.8 ", "### 9.9 ")
	mathDeclared := declarations(math)
	library := libraryList.FindStringSubmatch(math)
	if library == nil {
		t.Fatal("The library functions of §9.8 were not found")
	}
	for _, name := range strings.Split(library[2], " ") {
		mathDeclared[name+"/0"] = true
	}
	for _, m := range formulas.FindAllStringSubmatch(math, -1) {
		mathDeclared[m[1]+"/0"] = true
	}

	group := moduleGroup.FindString(specSection(t, section, "### 9.11 ", "### 9.12 "))
	if group == "" {
		t.Fatal("The group sentence of §9.11 was not found")
	}
	modules = map[string]bool{}
	addSignatures(modules, group)

	all = declarations(section)
	native = map[string]bool{}
	prelude = map[string]bool{}
	for key := range mathDeclared {
		all[key] = true
	}
	for key := range modules {
		all[key] = true
		native[key] = true
	}
	for _, row := range kindRows.FindAllStringSubmatch(section, -1) {
		if row[2] == "N" {
			addSignatures(native, row[1])
		} else {
			addSignatures(prelude, row[1])
		}
	}
	for signature := range mathDeclared {
		if signature == "abs/0" {
			prelude[signature] = true
		} else {
			native[signature] = true
		}
	}
	return all, native, prelude, modules
}

func declarations(text string) map[string]bool {
	declared := map[string]bool{}
	for _, cell := range firstCells.FindAllStringSubmatch(text, -1) {
		addSignatures(declared, cell[1])
	}
	for _, library := range libraryList.FindAllStringSubmatch(text, -1) {
		addSignatures(declared, library[1])
	}
	for _, heading := range headings.FindAllString(text, -1) {
		addSignatures(declared, heading)
	}
	return declared
}

func specSection(t *testing.T, text, start, end string) string {
	from := strings.Index(text, "\n"+start)
	if from < 0 {
		t.Fatalf("Section '%s' not found in the spec", start)
	}
	to := strings.Index(text[from+1:], "\n"+end)
	if to < 0 {
		t.Fatalf("Section '%s' not found in the spec", end)
	}
	return text[from : from+1+to]
}
