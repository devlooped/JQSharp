package jqlite

import (
	"io"
	"io/fs"
	"os"
	"path"
	"path/filepath"

	"github.com/devlooped/jqlite/go/internal/binding"
	"github.com/devlooped/jqlite/go/internal/values"
)

// ModuleKind tells whether an import asks for a code module (.jq) or a data module
// (import "p" as $name;, JSON values).
type ModuleKind int

// The kinds of modules.
const (
	ModuleCode ModuleKind = iota
	ModuleData
)

// Resolver finds the modules of import and include directives and of modulemeta: maps a path, as
// written, to a canonical path (the module's identity), then opens it. A module is read once per
// canonical path while an expression is parsed. modulemeta calls the resolver while expressions are
// evaluated, possibly from several goroutines at once: implementations must allow that.
type Resolver interface {
	// CanonicalPath gives the canonical path of the module path of the given kind, imported with
	// metadata (the directive's metadata object, or nil) from the module at fromPath (a canonical
	// path, or "" for the expression itself); "" when there is no such module. The same canonical
	// path means the same module.
	CanonicalPath(path string, kind ModuleKind, metadata *Value, fromPath string) (string, error)

	// Open opens the module at a canonical path that CanonicalPath gave.
	Open(canonicalPath string) (io.ReadCloser, error)
}

// FileResolver is a Resolver over the file system: a path is relative to the directory of the
// importing module, or to BaseDirectory for the expression itself, and gets the extension .jq
// (code) or .json (data) when it has none. The canonical path is the absolute file path.
type FileResolver struct {
	BaseDirectory string
}

// NewFileResolver gives a resolver whose expressions' own directives are relative to baseDirectory.
func NewFileResolver(baseDirectory string) *FileResolver {
	if absolute, err := filepath.Abs(baseDirectory); err == nil {
		baseDirectory = absolute
	}
	return &FileResolver{BaseDirectory: baseDirectory}
}

func (r *FileResolver) CanonicalPath(p string, kind ModuleKind, _ *Value, fromPath string) (string, error) {
	directory := r.BaseDirectory
	if fromPath != "" {
		directory = filepath.Dir(fromPath)
	}
	file, err := filepath.Abs(filepath.Join(directory, p))
	if err != nil {
		return "", err
	}
	if filepath.Ext(file) == "" {
		file += extension(kind)
	}
	if info, err := os.Stat(file); err != nil || info.IsDir() {
		return "", nil
	}
	return file, nil
}

func (r *FileResolver) Open(canonicalPath string) (io.ReadCloser, error) {
	return os.Open(canonicalPath)
}

// FSResolver is a Resolver over an fs.FS (an embed.FS, for modules built into the program): a
// path is relative to the directory of the importing module, or to Root for the expression
// itself, and gets the extension .jq or .json when it has none. The canonical path is the path in
// the file system.
type FSResolver struct {
	FS   fs.FS
	Root string
}

func (r *FSResolver) CanonicalPath(p string, kind ModuleKind, _ *Value, fromPath string) (string, error) {
	directory := r.Root
	if fromPath != "" {
		directory = path.Dir(fromPath)
	}
	file := path.Join(directory, p)
	if path.Ext(file) == "" {
		file += extension(kind)
	}
	if info, err := fs.Stat(r.FS, file); err != nil || info.IsDir() {
		return "", nil
	}
	return file, nil
}

func (r *FSResolver) Open(canonicalPath string) (io.ReadCloser, error) {
	return r.FS.Open(canonicalPath)
}

func extension(kind ModuleKind) string {
	if kind == ModuleData {
		return ".json"
	}
	return ".jq"
}

// resolverSource is a public Resolver as the module source of the core (spec §9.11, §12): a
// resolver's error becomes the binding error.
type resolverSource struct{ resolver Resolver }

func (s resolverSource) TryResolve(p string, kind binding.ModuleKind, metadata *values.Value, from string) (string, string, bool) {
	publicKind := ModuleCode
	if kind == binding.ModuleData {
		publicKind = ModuleData
	}
	identity, err := s.resolver.CanonicalPath(p, publicKind, metadata, from)
	if err != nil {
		return "", err.Error(), false
	}
	if identity == "" {
		return "", binding.BindingMessages.ModuleNotFound(p), false
	}
	return identity, "", true
}

func (s resolverSource) TryRead(identity string) (string, string, bool) {
	reader, err := s.resolver.Open(identity)
	if err != nil {
		return "", err.Error(), false
	}
	defer reader.Close()
	data, err := io.ReadAll(reader)
	if err != nil {
		return "", err.Error(), false
	}
	return string(data), "", true
}
