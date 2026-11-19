package syntax

import (
	"strings"
	"unicode/utf8"

	"github.com/devlooped/jqlite/go/internal/values"
)

type punctuation struct {
	text string
	kind TokenKind
}

// Punctuation, longest first where one is a prefix of another.
var punctuations = []punctuation{
	{"?//", TokenQuestionAlt},
	{"//=", TokenAltAssign},
	{"..", TokenDotDot},
	{"|=", TokenPipeAssign},
	{"==", TokenEqual},
	{"!=", TokenNotEqual},
	{"<=", TokenLessEqual},
	{">=", TokenGreaterEqual},
	{"+=", TokenPlusAssign},
	{"-=", TokenMinusAssign},
	{"*=", TokenStarAssign},
	{"/=", TokenSlashAssign},
	{"%=", TokenPercentAssign},
	{"//", TokenAlt},
	{".", TokenDot},
	{"[", TokenLeftBracket},
	{"]", TokenRightBracket},
	{"{", TokenLeftBrace},
	{"}", TokenRightBrace},
	{"(", TokenLeftParen},
	{")", TokenRightParen},
	{"|", TokenPipe},
	{",", TokenComma},
	{":", TokenColon},
	{";", TokenSemicolon},
	{"?", TokenQuestion},
	{"=", TokenAssign},
	{"<", TokenLess},
	{">", TokenGreater},
	{"+", TokenPlus},
	{"-", TokenMinus},
	{"*", TokenStar},
	{"/", TokenSlash},
	{"%", TokenPercent},
}

// interpolation is one open "\(": the parentheses opened inside it, and the position of the
// opening quote of its string (for "Unterminated string").
type interpolation struct {
	parens int
	line   int
	column int
}

// Lexer turns jqlite source text into tokens (spec §2), longest match first. It never panics: a
// lexical error ends the list with a token of kind TokenError whose text is the message; otherwise
// the list ends with TokenEof.
//
// String interpolation is lexed with a stack, without recursion: \( ends the current string piece
// (StringStart or StringMiddle) and pushes a frame; the lexer then produces the ordinary tokens of
// the interpolated expression, counting the parentheses opened inside it; the ) that closes the
// frame resumes the string (StringMiddle or StringEnd). Nesting is any number of frames; the parser
// limits it (spec §11).
//
// Positions: lines are separated by LF (a CR before it is white space, a lone CR is white space
// too, as in jq); columns count code points. The source is sanitized first: each ill-formed UTF-8
// sequence becomes U+FFFD.
type Lexer struct {
	source         string
	tokens         []*Token
	interpolations []interpolation
	index          int
	line           int
	column         int
}

// Tokenize gives the tokens of source, ending with an Eof or Error token.
func Tokenize(source string) []*Token {
	lexer := &Lexer{source: values.CodePoints.Sanitize(source), line: 1, column: 1}
	for lexer.next() {
	}
	return lexer.tokens
}

// next adds the next token; false after the last one (Eof or Error).
func (l *Lexer) next() bool {
	l.skipTrivia()
	startLine := l.line
	startColumn := l.column
	if l.index >= len(l.source) {
		if n := len(l.interpolations); n > 0 {
			return l.fail(UnterminatedString, l.interpolations[n-1].line, l.interpolations[n-1].column)
		}
		l.add(TokenEof, "", startLine, startColumn)
		return false
	}

	c := l.source[l.index]
	if c == '"' {
		l.advance()
		return l.lexStringPiece(startLine, startColumn, startLine, startColumn, true)
	}

	if isDigit(c) || (c == '.' && isDigit(l.at(l.index+1))) {
		return l.lexNumber(startLine, startColumn)
	}

	if isIdentStart(c) {
		name := l.readIdentifier(true)
		kind := TokenIdent
		if !strings.Contains(name, ":") {
			kind = KeywordKind(name)
		}
		l.add(kind, name, startLine, startColumn)
		return true
	}

	if c == '.' && isIdentStart(l.at(l.index+1)) {
		l.advance()
		l.add(TokenField, l.readIdentifier(false), startLine, startColumn)
		return true
	}

	if c == '$' {
		l.advance()
		if l.index >= len(l.source) || !isIdentStart(l.source[l.index]) {
			return l.fail(VariableNameExpected, startLine, startColumn)
		}
		l.add(TokenVariable, l.readIdentifier(true), startLine, startColumn)
		return true
	}

	if c == '@' {
		l.advance()
		start := l.index
		for isIdentPart(l.at(l.index)) {
			l.advance()
		}
		if l.index == start {
			return l.fail(FormatNameExpected, startLine, startColumn)
		}
		l.add(TokenFormat, l.source[start:l.index], startLine, startColumn)
		return true
	}

	if n := len(l.interpolations); c == '(' && n > 0 {
		l.interpolations[n-1].parens++
	}

	if n := len(l.interpolations); c == ')' && n > 0 {
		frame := l.interpolations[n-1]
		if frame.parens == 0 {
			// The ')' that closes "\(": the string resumes.
			l.interpolations = l.interpolations[:n-1]
			l.advance()
			return l.lexStringPiece(startLine, startColumn, frame.line, frame.column, false)
		}
		l.interpolations[n-1].parens--
	}

	for _, p := range punctuations {
		if strings.HasPrefix(l.source[l.index:], p.text) {
			for i := 0; i < len(p.text); i++ {
				l.advance()
			}
			l.add(p.kind, p.text, startLine, startColumn)
			return true
		}
	}

	return l.fail(SyntaxMessages.InvalidCharacter(l.codePointAt(l.index)), startLine, startColumn)
}

// skipTrivia skips white space (space, tab, CR, LF) and comments. A comment runs to the end of the
// line; when an odd number of '\' ends the line (before LF or CR LF), the comment goes on with the
// next line.
func (l *Lexer) skipTrivia() {
	for l.index < len(l.source) {
		c := l.source[l.index]
		if c == ' ' || c == '\t' || c == '\r' || c == '\n' {
			l.advance()
			continue
		}

		if c != '#' {
			return
		}

		start := l.index
		for l.index < len(l.source) {
			if l.source[l.index] == '\n' {
				j := l.index - 1
				if j > start && l.source[j] == '\r' {
					j--
				}
				backslashes := 0
				for ; j > start && l.source[j] == '\\'; j-- {
					backslashes++
				}
				if backslashes%2 == 0 {
					break
				}
			}
			l.advance()
		}
	}
}

// lexStringPiece lexes a string piece: after the opening quote, or after the ')' that closes an
// interpolation. Ends at the closing quote (String or StringEnd) or at "\(" (StringStart or
// StringMiddle).
func (l *Lexer) lexStringPiece(tokenLine, tokenColumn, quoteLine, quoteColumn int, first bool) bool {
	var text strings.Builder
	for {
		if l.index >= len(l.source) {
			return l.fail(UnterminatedString, quoteLine, quoteColumn)
		}

		c := l.source[l.index]
		if c == '"' {
			l.advance()
			kind := TokenStringEnd
			if first {
				kind = TokenString
			}
			l.add(kind, text.String(), tokenLine, tokenColumn)
			return true
		}

		if c != '\\' {
			start := l.index
			l.advance()
			text.WriteString(l.source[start:l.index])
			continue
		}

		escapeLine := l.line
		escapeColumn := l.column
		l.advance()
		e := l.at(l.index)
		switch e {
		case '"', '\\', '/':
			text.WriteByte(e)
		case 'b':
			text.WriteByte('\b')
		case 'f':
			text.WriteByte('\f')
		case 'n':
			text.WriteByte('\n')
		case 'r':
			text.WriteByte('\r')
		case 't':
			text.WriteByte('\t')
		case 'u':
			unit := l.hexAt(l.index + 1)
			if unit < 0 {
				return l.fail(InvalidEscape, escapeLine, escapeColumn)
			}
			for i := 0; i < 5; i++ {
				l.advance()
			}
			low := -1
			if l.at(l.index) == '\\' && l.at(l.index+1) == 'u' {
				low = l.hexAt(l.index + 2)
			}
			if unit >= 0xD800 && unit <= 0xDBFF && low >= 0xDC00 && low <= 0xDFFF {
				// A high surrogate escape followed by a low one: one code point.
				text.WriteRune(rune(0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00)))
				for i := 0; i < 6; i++ {
					l.advance()
				}
			} else if unit >= 0xD800 && unit <= 0xDFFF {
				// Any other surrogate escape is U+FFFD.
				text.WriteRune(values.Replacement)
			} else {
				text.WriteRune(rune(unit))
			}
			continue
		case '(':
			l.advance()
			kind := TokenStringMiddle
			if first {
				kind = TokenStringStart
			}
			l.add(kind, text.String(), tokenLine, tokenColumn)
			l.interpolations = append(l.interpolations, interpolation{0, quoteLine, quoteColumn})
			return true
		default:
			if l.index >= len(l.source) {
				return l.fail(UnterminatedString, quoteLine, quoteColumn)
			}
			return l.fail(InvalidEscape, escapeLine, escapeColumn)
		}

		l.advance()
	}
}

// lexNumber lexes ([0-9]+ ('.' [0-9]*)? | '.' [0-9]+) ([eE] [+-]? [0-9]+)?
func (l *Lexer) lexNumber(startLine, startColumn int) bool {
	start := l.index
	for isDigit(l.at(l.index)) {
		l.advance()
	}
	if l.at(l.index) == '.' {
		l.advance()
		for isDigit(l.at(l.index)) {
			l.advance()
		}
	}

	if e := l.at(l.index); e == 'e' || e == 'E' {
		digit := l.index + 1
		if s := l.at(l.index + 1); s == '+' || s == '-' {
			digit = l.index + 2
		}
		if isDigit(l.at(digit)) {
			for l.index < digit {
				l.advance()
			}
			for isDigit(l.at(l.index)) {
				l.advance()
			}
		}
	}

	text := l.source[start:l.index]
	// The grammar above is a subset of the numeric text of ValueText, so the conversion succeeds.
	number, _ := values.ValueText.TryParseNumber(text)
	l.tokens = append(l.tokens, &Token{TokenNumber, text, number, startLine, startColumn})
	return true
}

// readIdentifier reads [A-Za-z_][A-Za-z0-9_]*, and when qualified, any number of "::" each
// followed by such a name (jq 1.8.2 accepts a::b::c as one name: an import alias may itself be
// qualified).
func (l *Lexer) readIdentifier(qualified bool) string {
	start := l.index
	for isIdentPart(l.at(l.index)) {
		l.advance()
	}
	for qualified && l.at(l.index) == ':' && l.at(l.index+1) == ':' && isIdentStart(l.at(l.index+2)) {
		l.advance()
		l.advance()
		for isIdentPart(l.at(l.index)) {
			l.advance()
		}
	}
	return l.source[start:l.index]
}

// hexAt gives the four hex digits at offset, as a number, or -1.
func (l *Lexer) hexAt(offset int) int {
	value := 0
	for i := 0; i < 4; i++ {
		c := l.at(offset + i)
		digit := -1
		switch {
		case c >= '0' && c <= '9':
			digit = int(c - '0')
		case c >= 'a' && c <= 'f':
			digit = int(c-'a') + 10
		case c >= 'A' && c <= 'F':
			digit = int(c-'A') + 10
		}
		if digit < 0 {
			return -1
		}
		value = value*16 + digit
	}
	return value
}

// codePointAt gives the code point at offset.
func (l *Lexer) codePointAt(offset int) int {
	r, _ := utf8.DecodeRuneInString(l.source[offset:])
	return int(r)
}

// advance moves past one code point, keeping line and column.
func (l *Lexer) advance() {
	c := l.source[l.index]
	if c < utf8.RuneSelf {
		l.index++
	} else {
		_, size := utf8.DecodeRuneInString(l.source[l.index:])
		l.index += size
	}
	if c == '\n' {
		l.line++
		l.column = 1
	} else {
		l.column++
	}
}

func (l *Lexer) at(offset int) byte {
	if offset < len(l.source) {
		return l.source[offset]
	}
	return 0
}

func (l *Lexer) add(kind TokenKind, text string, tokenLine, tokenColumn int) {
	l.tokens = append(l.tokens, &Token{Kind: kind, Text: text, Line: tokenLine, Column: tokenColumn})
}

func (l *Lexer) fail(message string, errorLine, errorColumn int) bool {
	l.add(TokenError, message, errorLine, errorColumn)
	return false
}

func isDigit(c byte) bool { return c >= '0' && c <= '9' }

func isIdentStart(c byte) bool { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' }

func isIdentPart(c byte) bool { return isIdentStart(c) || isDigit(c) }
