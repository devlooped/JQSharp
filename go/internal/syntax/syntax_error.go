package syntax

import "github.com/devlooped/jqlite/go/internal/values"

// SyntaxError is the one error a lexing, parsing or binding run gives: a message and the 1-based
// position of the token where it was detected, in the main program or, when Module is not empty,
// in the module of that identity (§9.11).
type SyntaxError struct {
	Message string
	Line    int
	Column  int
	Module  string
}

type syntaxMessages struct{}

// SyntaxMessages holds every message text of the syntax layer, in one place. The texts of spec
// §10.2 are fixed; the others are this implementation's.
var SyntaxMessages syntaxMessages

// Fixed by spec §10.2.
const (
	InvalidEscape         = "Invalid escape"
	MayNeedParentheses    = "May need parentheses around object key expression"
	MetadataNotConstant   = "Module metadata must be constant"
	MetadataNotObject     = "Module metadata must be an object"
	ImportPathNotConstant = "Import path must be constant"
)

// InvalidObjectKey is "Cannot use number (1) as object key".
func (syntaxMessages) InvalidObjectKey(key values.Value) string {
	return values.Errors.CannotUseAsKey(key).AsString()
}

// TooDeeplyNested is fixed by spec §11.
const TooDeeplyNested = "Program too deeply nested"

// This implementation's.
const (
	UnterminatedString   = "Unterminated string"
	VariableNameExpected = "Expected a variable name after '$'"
	FormatNameExpected   = "Expected a format name after '@'"
	ModuleHasBody        = "A module contains only definitions"
)

func (syntaxMessages) InvalidCharacter(codePoint int) string {
	if codePoint < 0x20 || codePoint == 0x7F || (codePoint >= 0xD800 && codePoint <= 0xDFFF) {
		return "Invalid character U+" + values.CodePoints.Hex(codePoint, 4)
	}
	return "Invalid character '" + string(rune(codePoint)) + "'"
}

func (syntaxMessages) Unexpected(token *Token) string { return "Unexpected " + token.Describe() }

func (syntaxMessages) Expected(what string, token *Token) string {
	return "Expected " + what + " but found " + token.Describe()
}

func (syntaxMessages) NotAssociative(token *Token) string {
	return "Operator " + token.Describe() + " cannot be chained: add parentheses"
}

func (syntaxMessages) QuestionAlt(token *Token) string {
	return "Unexpected " + token.Describe() + ": write '? //' for an optional followed by an alternative"
}
