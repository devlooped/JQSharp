// Package corpus reads the conformance corpus of spec/tests (format in spec/tests/README.md) for
// the tests of every layer. It is test support, not part of the engine: it uses the host's file
// system and regular expressions.
package corpus

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strings"

	"github.com/devlooped/jqlite/go/internal/values"
)

// Case is one case of a .test file.
type Case struct {
	Program         string
	Input           string
	ExpectedOutputs []string
	ShouldFail      bool
	// ExpectedError is the message lines of a %%FAIL case; HasExpectedError is false for
	// %%FAIL IGNORE MSG.
	ExpectedError    string
	HasExpectedError bool
	LineNumber       int
}

// SpecDirectory finds spec/ above the working directory.
func SpecDirectory() string {
	directory, _ := os.Getwd()
	for {
		path := filepath.Join(directory, "spec")
		if info, err := os.Stat(filepath.Join(path, "prelude.jq")); err == nil && !info.IsDir() {
			return path
		}
		parent := filepath.Dir(directory)
		if parent == directory {
			panic("spec/prelude.jq not found above the working directory")
		}
		directory = parent
	}
}

// TestsDirectory is spec/tests.
func TestsDirectory() string { return filepath.Join(SpecDirectory(), "tests") }

// Files gives the .test files of spec/tests, in ordinal order.
func Files() []string {
	files, _ := filepath.Glob(filepath.Join(TestsDirectory(), "*.test"))
	slices.Sort(files)
	return files
}

// ParseFile reads the cases of a .test file.
func ParseFile(path string) []Case {
	data, err := os.ReadFile(path)
	if err != nil {
		panic(err)
	}
	// Lines end at LF (a CR before it is part of the line ending); a lone CR is part of the line.
	lines := strings.Split(string(data), "\n")
	for i, line := range lines {
		lines[i] = strings.TrimSuffix(line, "\r")
	}

	var cases []Case
	index := 0
	for index < len(lines) {
		if isSeparator(lines[index]) {
			index++
			continue
		}

		if isFailMarker(lines[index]) {
			// "%%FAIL IGNORE MSG": the program must not parse, whatever the message.
			ignoreMessage := strings.Contains(lines[index], "IGNORE MSG")
			index++
			for index < len(lines) && isSeparator(lines[index]) {
				index++
			}
			if index >= len(lines) {
				break
			}

			program := lines[index]
			lineNumber := index + 1
			index++
			var errorLines []string
			for index < len(lines) && !isSeparator(lines[index]) && !isFailMarker(lines[index]) {
				errorLines = append(errorLines, lines[index])
				index++
			}

			cases = append(cases, Case{
				Program: program, ShouldFail: true, ExpectedError: strings.Join(errorLines, "\n"),
				HasExpectedError: !ignoreMessage, LineNumber: lineNumber,
			})
			continue
		}

		program := lines[index]
		lineNumber := index + 1
		index++
		if index >= len(lines) {
			panic(fmt.Sprintf("Missing input line for test program at line %d.", lineNumber))
		}
		input := lines[index]
		index++
		var expected []string
		for index < len(lines) && !isSeparator(lines[index]) && !isFailMarker(lines[index]) {
			expected = append(expected, lines[index])
			index++
		}
		cases = append(cases, Case{Program: program, Input: input, ExpectedOutputs: expected, LineNumber: lineNumber})
	}
	return cases
}

func isFailMarker(line string) bool {
	return strings.HasPrefix(strings.TrimLeft(line, " \t"), "%%FAIL")
}

func isSeparator(line string) bool {
	trimmed := strings.TrimSpace(line)
	return trimmed == "" || strings.HasPrefix(trimmed, "#")
}

var fixedText = regexp.MustCompile(`^(\S+/\d+ is not defined|\$\*label-\S+ is not defined|\$\S+::\S+ is not defined|Cannot use .+ as object key|May need parentheses around object key expression|Module metadata must be constant|Module metadata must be an object|Import path must be constant|Invalid escape)$`)

// MissingFixedText checks that the message lines of a %%FAIL case that are fixed texts of spec
// §10.2 are contained in the error; the other lines are jq's own wording and are not compared
// (spec §14.1). Gives "" when they are.
func MissingFixedText(c Case, message string) string {
	if !c.HasExpectedError {
		return ""
	}
	for _, line := range strings.Split(c.ExpectedError, "\n") {
		if fixedText.MatchString(line) && !strings.Contains(message, line) {
			return fmt.Sprintf("expected an error containing '%s', got '%s'", line, message)
		}
	}
	return ""
}

// Compare compares the outputs, as text, with the expected lines: each read back and compared
// structurally (spec §14.2). Gives "" when they match.
func Compare(expected, actual []string) string {
	same := len(expected) == len(actual)
	for i := 0; same && i < len(expected); i++ {
		same = TextEquals(expected[i], actual[i])
	}
	if same {
		return ""
	}
	return fmt.Sprintf("expected [%s] but got [%s]", strings.Join(expected, ", "), strings.Join(actual, ", "))
}

// TextEquals reads two JSON texts and compares them structurally.
func TextEquals(expected, actual string) bool {
	left, _, ok1 := values.ValueText.TryParse(expected)
	right, _, ok2 := values.ValueText.TryParse(actual)
	return ok1 && ok2 && Same(left, right)
}

// Same compares numbers as doubles (-0 equals 0), strings by code points, arrays in order,
// objects with the same keys in the same order.
func Same(left, right values.Value) bool {
	if left.Kind() != right.Kind() {
		return false
	}
	switch left.Kind() {
	case values.KindNumber:
		return left.AsNumber() == right.AsNumber()
	case values.KindString:
		return left.AsString() == right.AsString()
	case values.KindArray:
		a, b := left.AsArray(), right.AsArray()
		if a.Count() != b.Count() {
			return false
		}
		for i := 0; i < a.Count(); i++ {
			if !Same(a.At(i), b.At(i)) {
				return false
			}
		}
		return true
	case values.KindObject:
		x, y := left.AsObject(), right.AsObject()
		if x.Count() != y.Count() {
			return false
		}
		for i := 0; i < x.Count(); i++ {
			if x.KeyAt(i) != y.KeyAt(i) || !Same(x.ValueAt(i), y.ValueAt(i)) {
				return false
			}
		}
		return true
	}
	return true
}
