// Package modules holds the module sources of the tests: a directory with the path rule of
// spec/tests/README.md, and a map of texts. Test support, not part of the engine.
package modules

import (
	"os"
	"path"
	"path/filepath"
	"sync"
	"sync/atomic"

	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/values"
)

// DirectoryModules is a module source over a directory, with the rule of spec/tests/README.md: the
// path p imported from a file names p.jq (code) or p.json (data) in that file's directory, the main
// program's directory being the root. Identities are paths relative to the root.
type DirectoryModules struct {
	Root  string
	reads atomic.Int64
}

// Reads gives how many module files were read.
func (d *DirectoryModules) Reads() int { return int(d.reads.Load()) }

func extension(kind binding.ModuleKind) string {
	if kind == binding.ModuleCode {
		return ".jq"
	}
	return ".json"
}

func (d *DirectoryModules) TryResolve(p string, kind binding.ModuleKind, _ *values.Value, from string) (string, string, bool) {
	directory := ""
	if from != "" {
		directory = path.Dir(from)
	}
	identity := path.Join(directory, p+extension(kind))
	if _, err := os.Stat(filepath.Join(d.Root, identity)); err != nil {
		return "", "module not found: " + p, false
	}
	return identity, "", true
}

func (d *DirectoryModules) TryRead(identity string) (string, string, bool) {
	d.reads.Add(1)
	data, err := os.ReadFile(filepath.Join(d.Root, identity))
	if err != nil {
		return "", err.Error(), false
	}
	return string(data), "", true
}

// MemoryModules is a module source over a map of identities and texts; a path is its identity
// without the extension.
type MemoryModules struct {
	mu      sync.Mutex
	Modules map[string]string
}

// Set changes or adds a module text.
func (m *MemoryModules) Set(identity, text string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.Modules[identity] = text
}

func (m *MemoryModules) TryResolve(p string, kind binding.ModuleKind, _ *values.Value, _ string) (string, string, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	identity := p + extension(kind)
	if _, ok := m.Modules[identity]; !ok {
		return "", "module not found: " + p, false
	}
	return identity, "", true
}

func (m *MemoryModules) TryRead(identity string) (string, string, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	text, ok := m.Modules[identity]
	return text, "", ok
}
