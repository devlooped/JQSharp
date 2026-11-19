package syntax

import (
	"fmt"
	"math"
	"strings"
	"testing"
)

var tokenKindNames = map[TokenKind]string{
	TokenEof: "Eof", TokenError: "Error", TokenIdent: "Ident", TokenField: "Field", TokenVariable: "Variable",
	TokenFormat: "Format", TokenNumber: "Number", TokenString: "String", TokenStringStart: "StringStart",
	TokenStringMiddle: "StringMiddle", TokenStringEnd: "StringEnd", TokenDot: "Dot", TokenDotDot: "DotDot",
	TokenLeftBracket: "LeftBracket", TokenRightBracket: "RightBracket", TokenLeftBrace: "LeftBrace",
	TokenRightBrace: "RightBrace", TokenLeftParen: "LeftParen", TokenRightParen: "RightParen", TokenPipe: "Pipe",
	TokenComma: "Comma", TokenColon: "Colon", TokenSemicolon: "Semicolon", TokenQuestion: "Question",
	TokenQuestionAlt: "QuestionAlt", TokenAssign: "Assign", TokenEqual: "Equal", TokenNotEqual: "NotEqual",
	TokenLess: "Less", TokenLessEqual: "LessEqual", TokenGreater: "Greater", TokenGreaterEqual: "GreaterEqual",
	TokenPlus: "Plus", TokenMinus: "Minus", TokenStar: "Star", TokenSlash: "Slash", TokenPercent: "Percent",
	TokenPlusAssign: "PlusAssign", TokenMinusAssign: "MinusAssign", TokenStarAssign: "StarAssign",
	TokenSlashAssign: "SlashAssign", TokenPercentAssign: "PercentAssign", TokenAlt: "Alt", TokenAltAssign: "AltAssign",
	TokenPipeAssign: "PipeAssign",
}

func (k TokenKind) String() string {
	if name, ok := tokenKindNames[k]; ok {
		return name
	}
	return fmt.Sprint(int(k))
}

func lexTokens(source string) []*Token {
	var tokens []*Token
	for _, token := range Tokenize(source) {
		if token.Kind != TokenEof {
			tokens = append(tokens, token)
		}
	}
	return tokens
}

// describe gives the kind, text and position of each token, without the final Eof.
func describe(source string) string {
	var parts []string
	for _, t := range lexTokens(source) {
		parts = append(parts, fmt.Sprintf("%v:%s@%d:%d", t.Kind, t.Text, t.Line, t.Column))
	}
	return strings.Join(parts, " ")
}

func kinds(source string) string {
	var parts []string
	for _, t := range lexTokens(source) {
		parts = append(parts, t.Kind.String())
	}
	return strings.Join(parts, " ")
}

func texts(source string) string {
	var parts []string
	for _, t := range lexTokens(source) {
		parts = append(parts, t.Text)
	}
	return strings.Join(parts, " ")
}

func lexError(t *testing.T, source string) string {
	tokens := Tokenize(source)
	last := tokens[len(tokens)-1]
	if last.Kind != TokenError {
		t.Errorf("%s: no error", source)
	}
	return fmt.Sprintf("%s @%d:%d", last.Text, last.Line, last.Column)
}

func same(t *testing.T, expected, actual string) {
	t.Helper()
	if expected != actual {
		t.Errorf("\nexpected %s\ngot      %s", expected, actual)
	}
}

func TestEveryPunctuationTokenLongestFirst(t *testing.T) {
	source := ". .. [ ] { } ( ) | , : ; ? ?// = == != < <= > >= + - * / % += -= *= /= %= // //= |="
	same(t, "Dot DotDot LeftBracket RightBracket LeftBrace RightBrace LeftParen RightParen Pipe Comma Colon Semicolon Question QuestionAlt "+
		"Assign Equal NotEqual Less LessEqual Greater GreaterEqual Plus Minus Star Slash Percent "+
		"PlusAssign MinusAssign StarAssign SlashAssign PercentAssign Alt AltAssign PipeAssign", kinds(source))
	same(t, source, texts(source))
}

func TestAdjacentPunctuationTakesTheLongestMatch(t *testing.T) {
	same(t, "QuestionAlt Assign", kinds("?//="))
	same(t, "DotDot Dot", kinds("..."))
	same(t, "Alt Slash", kinds("///"))
	same(t, "PipeAssign Assign", kinds("|=="))
}

func TestEveryKeyword(t *testing.T) {
	for _, keyword := range Keywords {
		tokens := lexTokens(keyword.Word)
		if len(tokens) != 1 || tokens[0].Kind != keyword.Kind || tokens[0].Text != keyword.Word || !IsKeyword(tokens[0].Kind) {
			t.Errorf("%s", keyword.Word)
		}
	}
	if len(Keywords) != 18 {
		t.Errorf("%d keywords", len(Keywords))
	}
}

func TestNames(t *testing.T) {
	same(t, "Ident:foo@1:1 Ident:a::b@1:5 Ident:_x1@1:10 Ident:null@1:14 Ident:not@1:19", describe("foo a::b _x1 null not"))
	same(t, "Field:a@1:1 Field:end@1:3 Field:e0@1:7 Field:_@1:10", describe(".a.end.e0._"))
	same(t, "Variable:x@1:1 Variable:then@1:4 Variable:a::b@1:10 Variable:__loc__@1:16", describe("$x $then $a::b $__loc__"))
	same(t, "Format:base64@1:1 Format:base64d@1:9 Format:1x@1:18", describe("@base64 @base64d @1x"))
}

func TestAQualifiedNameMayHaveSeveralDoubleColons(t *testing.T) {
	same(t, "Ident", kinds("a::b::c"))
	same(t, "a::b::c", texts("a::b::c"))
	same(t, "Variable", kinds("$a::b::c"))
	same(t, "a : :", texts("a::"))
	same(t, "Field Colon Colon Ident", kinds(".a::b"))
}

func TestNumbers(t *testing.T) {
	for _, c := range []struct {
		source string
		value  float64
	}{
		{"0", 0}, {"42", 42}, {".5", 0.5}, {"1.", 1}, {"1.5", 1.5}, {"1e3", 1000}, {"1E+3", 1000},
		{"1.e3", 1000}, {".5e1", 5}, {"25e-1", 2.5}, {"0.1", 0.1}, {"1e1000", math.Inf(1)},
		{"1e-1000", 0}, {"100000000000000000000000000001", 1e29},
	} {
		tokens := lexTokens(c.source)
		if len(tokens) != 1 || tokens[0].Kind != TokenNumber || tokens[0].Number != c.value || tokens[0].Text != c.source {
			t.Errorf("%s", c.source)
		}
	}
}

func TestNumberBoundaries(t *testing.T) {
	same(t, "Field", kinds(".e0"))
	same(t, "Number Ident", kinds("1e"))
	same(t, "Number Ident Plus", kinds("1e+"))
	same(t, "1.5 .3", texts("1.5.3"))
	same(t, "DotDot Number", kinds("..5"))
	same(t, "Number Ident", kinds("1x"))
}

func TestPositionsAcrossLineBreaksAndNonBMPCharacters(t *testing.T) {
	// "😀😀" is 4 code points with the quotes; columns count code points, not bytes.
	same(t, "String:😀😀@1:1 Plus:+@1:6 Number:1@2:3 Plus:+@3:1 Field:a@3:3", describe("\"😀😀\" +\n  1\r\n+ .a"))
	same(t, "String:a\nb@1:1 Ident:x@2:4", describe("\"a\nb\" x"))
}

func TestALoneCRIsWhiteSpaceNotALineBreak(t *testing.T) {
	same(t, "Number:1@1:1 Plus:+@1:3 Number:2@1:5", describe("1\r+\r2"))
}

func TestComments(t *testing.T) {
	same(t, "Number:1@1:1 Plus:+@2:1 Number:2@2:3", describe("1 # a comment\n+ 2 # another"))
	// An odd number of backslashes at the end of the line continues the comment.
	same(t, "1 + 10", texts("1 # c \\\n+ 1\n+ 10"))
	same(t, "1 + 1 + 10", texts("1 # c \\\\\n+ 1\n+ 10"))
	same(t, "1 + 10", texts("1 # c \\\\\\\n+ 1\n+ 10"))
	same(t, "1 + 10", texts("1 # c \\\r\n+ 1\r\n+ 10"))
	same(t, "1 + 1 + 10", texts("1 # c \\ x\n+ 1\n+ 10"))
	// As in jq, a lone CR does not end a comment.
	same(t, "1", texts("1 # c\r+ 1"))
	same(t, "1 + 10", texts("1 # c \\\r+ 1\n+ 10"))
}

func TestStringEscapes(t *testing.T) {
	for _, c := range lexerStringEscapes {
		tokens := lexTokens(c[0])
		if len(tokens) != 1 || tokens[0].Kind != TokenString || tokens[0].Text != c[1] {
			t.Errorf("%s: %q", c[0], tokens[0].Text)
		}
	}
}

// Ill-formed UTF-8 in the source text itself (the reference's lone surrogates).
func TestIllFormedUtf8InTheSourceIsUFFFD(t *testing.T) {
	tokens := lexTokens("\"a\xED\xA0\x80b\x80\"")
	same(t, "a\uFFFD\uFFFD\uFFFDb\uFFFD", tokens[0].Text)
	same(t, "Invalid character '\uFFFD' @1:2", lexError(t, "a\x80"))
	// A replaced byte is one column.
	same(t, "String:\uFFFD@1:1 Plus:+@1:5", describe("\"\x80\" +"))
}

func TestInterpolationIsSplitIntoPieces(t *testing.T) {
	same(t, "StringStart:a@1:1 Number:1@1:5 Plus:+@1:7 LeftParen:(@1:9 Number:2@1:10 RightParen:)@1:11 StringMiddle:b@1:12 String:c@1:16 StringEnd:d@1:19",
		describe("\"a\\(1 + (2))b\\(\"c\")d\""))
}

func TestInterpolationNests(t *testing.T) {
	same(t, "StringStart StringStart String StringEnd StringEnd", kinds("\"a\\(\"b\\(\"c\")\")\""))
	same(t, "a b c", strings.TrimSpace(texts("\"a\\(\"b\\(\"c\")\")\"")))
}

func TestAParenthesisOutsideInterpolationIsPunctuation(t *testing.T) {
	same(t, "RightParen", kinds(")"))
}

func TestLexerErrors(t *testing.T) {
	for _, c := range lexerErrors {
		same(t, c[1], lexError(t, c[0]))
	}
}

func TestAnErrorIsTheLastToken(t *testing.T) {
	var parts []string
	for _, token := range Tokenize("1 + \"a\\q\" + 2") {
		parts = append(parts, token.Kind.String())
	}
	same(t, "Number Plus Error", strings.Join(parts, " "))
}

func TestEmptySourceIsEof(t *testing.T) {
	tokens := Tokenize("  # nothing\n")
	if len(tokens) != 1 || tokens[0].Kind != TokenEof || tokens[0].Line != 2 || tokens[0].Column != 1 {
		t.Error("not one Eof at 2:1")
	}
}

var lexerStringEscapes = [][]string{
	{`"abc"`, `abc`},
	{`"\"\\\/\b\f\n\r\t"`, "\"\\/\x08\x0c\n\r\t"},
	{`"\u00e9\u00C9"`, `éÉ`},
	{`"\uD83D\uDE00"`, `😀`},
	{`"\ud83d x"`, "\uFFFD x"},
	{`"\ude00"`, "\uFFFD"},
	{`"\uDE00\uD83D"`, "\uFFFD\uFFFD"},
	{`"\uD83D\u0041"`, "\uFFFDA"},
	{`"\uD83D\uD83D\uDE00"`, "\uFFFD😀"},
	{`"\u12345"`, "\u12345"},
	{"\"é\t\"", "é\t"},
}

var lexerErrors = [][]string{
	{`"abc`, `Unterminated string @1:1`},
	{`1 + "a\(1`, `Unterminated string @1:5`},
	{`"a\("b" + (1`, `Unterminated string @1:1`},
	{`"a\`, `Unterminated string @1:1`},
	{`"a\qb"`, `Invalid escape @1:3`},
	{"\"x\"\n \"\\u12\"", `Invalid escape @2:3`},
	{`"\u12G4"`, `Invalid escape @1:2`},
	{`"\u"`, `Invalid escape @1:2`},
	{`1 $ x`, `Expected a variable name after '$' @1:3`},
	{`@`, `Expected a format name after '@' @1:1`},
	{`{aé: 1}`, `Invalid character 'é' @1:3`},
	{`😀`, `Invalid character '😀' @1:1`},
	{"1\x0c+2", `Invalid character U+000C @1:2`},
	{`1 ! 2`, `Invalid character '!' @1:3`},
}
