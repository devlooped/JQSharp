// Package syntax holds the lexer, the AST and the parser of jqlite (spec §2, §3). Nothing here
// panics on bad input: parsing gives the program or one SyntaxError.
package syntax

// TokenKind is the kind of a token of jqlite (spec §2).
type TokenKind int

// The kinds of tokens.
const (
	// TokenEof is the end of the source.
	TokenEof TokenKind = iota
	// TokenError is a lexical error; Text is the message. Always the last token.
	TokenError

	// TokenIdent is name or module::name; Text is the name as written.
	TokenIdent
	// TokenField is .name; Text is the name without the dot.
	TokenField
	// TokenVariable is $name or $module::name; Text is the name without $.
	TokenVariable
	// TokenFormat is @name; Text is the name without @.
	TokenFormat
	// TokenNumber is a number literal; Number is its value, Text its source text.
	TokenNumber

	// String literals. Text is the decoded literal text of the piece. A string without
	// interpolation is one String token; "a\(x)b\(y)c" is StringStart("a"), the tokens of x,
	// StringMiddle("b"), the tokens of y, StringEnd("c"). StringMiddle and StringEnd are at the ')'
	// that closes the interpolation.
	TokenString
	TokenStringStart
	TokenStringMiddle
	TokenStringEnd

	// Keywords (the list is Keywords).
	TokenDef
	TokenAs
	TokenIf
	TokenThen
	TokenElif
	TokenElse
	TokenEnd
	TokenReduce
	TokenForeach
	TokenTry
	TokenCatch
	TokenLabel
	TokenBreak
	TokenImport
	TokenInclude
	TokenModule
	TokenAnd
	TokenOr

	// Punctuation.
	TokenDot
	TokenDotDot
	TokenLeftBracket
	TokenRightBracket
	TokenLeftBrace
	TokenRightBrace
	TokenLeftParen
	TokenRightParen
	TokenPipe
	TokenComma
	TokenColon
	TokenSemicolon
	TokenQuestion
	TokenQuestionAlt
	TokenAssign
	TokenEqual
	TokenNotEqual
	TokenLess
	TokenLessEqual
	TokenGreater
	TokenGreaterEqual
	TokenPlus
	TokenMinus
	TokenStar
	TokenSlash
	TokenPercent
	TokenPlusAssign
	TokenMinusAssign
	TokenStarAssign
	TokenSlashAssign
	TokenPercentAssign
	TokenAlt
	TokenAltAssign
	TokenPipeAssign
)

// Token is a token with the position of its first code point (1-based line and column; columns
// count code points).
type Token struct {
	Kind   TokenKind
	Text   string
	Number float64
	Line   int
	Column int
}

// Keyword is a keyword of jqlite and its token kind.
type Keyword struct {
	Word string
	Kind TokenKind
}

// Keywords are the keywords of jqlite: the only place they are listed.
var Keywords = []Keyword{
	{"def", TokenDef},
	{"as", TokenAs},
	{"if", TokenIf},
	{"then", TokenThen},
	{"elif", TokenElif},
	{"else", TokenElse},
	{"end", TokenEnd},
	{"reduce", TokenReduce},
	{"foreach", TokenForeach},
	{"try", TokenTry},
	{"catch", TokenCatch},
	{"label", TokenLabel},
	{"break", TokenBreak},
	{"import", TokenImport},
	{"include", TokenInclude},
	{"module", TokenModule},
	{"and", TokenAnd},
	{"or", TokenOr},
}

// KeywordKind gives the keyword kind of word, or TokenIdent.
func KeywordKind(word string) TokenKind {
	for _, keyword := range Keywords {
		if keyword.Word == word {
			return keyword.Kind
		}
	}
	return TokenIdent
}

// IsKeyword tells whether kind is a keyword.
func IsKeyword(kind TokenKind) bool { return kind >= TokenDef && kind <= TokenOr }

// Describe gives the token as the parser's messages show it: '|=', end of program, '.name'.
func (t *Token) Describe() string {
	switch t.Kind {
	case TokenEof:
		return "end of program"
	case TokenError:
		return t.Text
	case TokenField:
		return "'." + t.Text + "'"
	case TokenVariable:
		return "'$" + t.Text + "'"
	case TokenFormat:
		return "'@" + t.Text + "'"
	case TokenString, TokenStringStart:
		return "string"
	case TokenStringMiddle, TokenStringEnd:
		return "')'"
	default:
		return "'" + t.Text + "'"
	}
}
