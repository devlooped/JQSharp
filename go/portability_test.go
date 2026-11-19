package jqlite

import (
	"go/ast"
	"go/parser"
	"go/token"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

// The portability rules of AGENTS.md as tests: the core (internal/values, syntax, binding, runtime,
// builtins) must read the same in every language, so a host feature in its code fails here, naming
// the file and the line. The core imports only the packages of plainImports; every other use of
// the standard library, and every exception to the rules on panics, goroutines and channels, is
// listed by file with its reason: a new use anywhere else must be justified by adding it here. Test
// files, the go generate tool of binding and the test support of internal/corpus are not the core.

var coreLayers = []string{"values", "syntax", "binding", "runtime", "builtins"}

// plainImports are what every language has under another name: math, string and UTF-8 helpers,
// sorting.
var plainImports = map[string]bool{"math": true, "strings": true, "unicode/utf8": true, "slices": true}

// importExceptions: the other imports of the core, by file (relative to internal/), with the reason.
var importExceptions = map[string]map[string]string{
	"strconv": {
		"values/value_text.go": "number text: shortest round-trip digits and decimal parsing (AGENTS.md)",
	},
	"embed": {
		"binding/prelude.go": "reads the embedded prelude (AGENTS.md)",
	},
	"sync": {
		"values/jq_object.go":      "the key index shared by the versions of a buffer, extended under a lock (guide §2.9)",
		"builtins/regex_engine.go": "the cache of compiled programs, a concurrent map (guide §3.8)",
		"binding/prelude.go":       "the prelude bound once, by whoever needs it first (the reference's static Instance and Lazy)",
		"builtins/core_natives.go": "the list of builtins/0 computed once (the reference's Lazy)",
	},
	"sync/atomic": {
		"values/jq_array.go":       "the compare-and-swap that claims slots of a shared buffer (guide §2.9)",
		"values/jq_object.go":      "the compare-and-swap that claims slots, and the cached sorted keys (guide §2.9)",
		"values/jq_string.go":      "the compare-and-swap that claims bytes of a shared buffer, and the cached text (guide §2.9)",
		"builtins/regex_engine.go": "the size of the cache of compiled programs (sync.Map has no count)",
	},
}

// callExceptions: the calls the core must not make (no exceptions as control flow), by file.
var callExceptions = map[string]map[string]string{
	"recover": {
		"runtime/stack_guard.go": "carries a panic of a step from the fresh goroutine back to the caller's",
	},
	"panic": {
		"values/jq_value.go":          "the As* accessors on a value of another kind: a programming error",
		"values/jq_array.go":          "index guards of the host caller",
		"values/jq_array_builder.go":  "index guard, and use after ToArray: programming errors",
		"values/jq_object.go":         "index guards of the host caller",
		"values/jq_object_builder.go": "use after ToObject: a programming error",
		"binding/prelude.go":          "the embedded prelude does not bind: a build defect",
		"binding/resolver.go":         "a node kind the resolver does not know: unreachable",
		"runtime/native_table.go":     "start-up checks of the registrations against the catalogue",
		"runtime/interpreter.go":      "Run without the natives registered: a build defect",
		"runtime/stack_guard.go":      "raises again, on the caller's goroutine, a panic of the step",
	},
}

// concurrencyExceptions: the files that may start goroutines and use channels.
var concurrencyExceptions = map[string]string{
	"runtime/stack_guard.go": "continues a step on a fresh goroutine and waits for it (the reference's thread)",
}

func TestTheCoreFollowsThePortabilityRules(t *testing.T) {
	files := token.NewFileSet()
	for _, layer := range coreLayers {
		paths, _ := filepath.Glob(filepath.Join("internal", layer, "*.go"))
		for _, path := range paths {
			if strings.HasSuffix(path, "_test.go") {
				continue
			}
			relative := filepath.ToSlash(strings.TrimPrefix(path, "internal"+string(filepath.Separator)))
			source, err := os.ReadFile(path)
			if err != nil {
				t.Fatal(err)
			}
			file, err := parser.ParseFile(files, path, source, parser.ParseComments)
			if err != nil {
				t.Fatal(err)
			}
			if file.Name.Name == "main" {
				continue // copy_prelude.go, a //go:build ignore tool of go generate
			}

			for _, spec := range file.Imports {
				name, _ := strconv.Unquote(spec.Path.Value)
				if plainImports[name] || strings.HasPrefix(name, "github.com/devlooped/jqlite/go/internal/") {
					continue
				}
				if _, ok := importExceptions[name][relative]; !ok {
					t.Errorf("%s: imports %s", files.Position(spec.Pos()), name)
				}
			}

			_, concurrent := concurrencyExceptions[relative]
			ast.Inspect(file, func(n ast.Node) bool {
				switch node := n.(type) {
				case *ast.CallExpr:
					if identifier, ok := node.Fun.(*ast.Ident); ok {
						if allowed, ruled := callExceptions[identifier.Name]; ruled {
							if _, ok := allowed[relative]; !ok {
								t.Errorf("%s: calls %s", files.Position(node.Pos()), identifier.Name)
							}
						}
					}
				case *ast.GoStmt:
					if !concurrent {
						t.Errorf("%s: starts a goroutine", files.Position(node.Pos()))
					}
				case *ast.ChanType, *ast.SendStmt, *ast.SelectStmt:
					if !concurrent {
						t.Errorf("%s: uses a channel", files.Position(node.Pos()))
					}
				case *ast.UnaryExpr:
					if node.Op == token.ARROW && !concurrent {
						t.Errorf("%s: receives from a channel", files.Position(node.Pos()))
					}
				}
				return true
			})
		}
	}
}

// Only the host boundary (the module root) knows Go's JSON and reflection, in value.go: it converts
// values through the engine's reader and writer and the conversions there.
func TestOnlyValueGoConvertsHostValues(t *testing.T) {
	paths, _ := filepath.Glob("*.go")
	files := token.NewFileSet()
	for _, path := range paths {
		if strings.HasSuffix(path, "_test.go") || path == "value.go" {
			continue
		}
		file, err := parser.ParseFile(files, path, nil, parser.ImportsOnly)
		if err != nil {
			t.Fatal(err)
		}
		for _, spec := range file.Imports {
			if name, _ := strconv.Unquote(spec.Path.Value); name == "encoding/json" || name == "reflect" {
				t.Errorf("%s imports %s", path, name)
			}
		}
	}
}
