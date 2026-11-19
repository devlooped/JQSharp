"""The tokens of jqlite (Token.cs; spec §2)."""

from __future__ import annotations


class TokenKind:
    """The kinds of tokens (an int each)."""

    EOF = 0
    # A lexical error; the token's text is the message. Always the last token.
    ERROR = 1

    # ``name`` or ``module::name``; text is the name as written.
    IDENT = 2
    # ``.name``; text is the name without the dot.
    FIELD = 3
    # ``$name`` or ``$module::name``; text is the name without ``$``.
    VARIABLE = 4
    # ``@name``; text is the name without ``@``.
    FORMAT = 5
    # A number literal; number is its value, text its source text.
    NUMBER = 6

    # String literals. Text is the decoded literal text of the piece. A string without
    # interpolation is one STRING token; "a\(x)b\(y)c" is STRING_START("a"), the tokens of x,
    # STRING_MIDDLE("b"), the tokens of y, STRING_END("c").
    STRING = 7
    STRING_START = 8
    STRING_MIDDLE = 9
    STRING_END = 10

    # Keywords (the list is KEYWORDS).
    DEF = 11
    AS = 12
    IF = 13
    THEN = 14
    ELIF = 15
    ELSE = 16
    END = 17
    REDUCE = 18
    FOREACH = 19
    TRY = 20
    CATCH = 21
    LABEL = 22
    BREAK = 23
    IMPORT = 24
    INCLUDE = 25
    MODULE = 26
    AND = 27
    OR = 28

    # Punctuation.
    DOT = 29
    DOT_DOT = 30
    LEFT_BRACKET = 31
    RIGHT_BRACKET = 32
    LEFT_BRACE = 33
    RIGHT_BRACE = 34
    LEFT_PAREN = 35
    RIGHT_PAREN = 36
    PIPE = 37
    COMMA = 38
    COLON = 39
    SEMICOLON = 40
    QUESTION = 41
    QUESTION_ALT = 42
    ASSIGN = 43
    EQUAL = 44
    NOT_EQUAL = 45
    LESS = 46
    LESS_EQUAL = 47
    GREATER = 48
    GREATER_EQUAL = 49
    PLUS = 50
    MINUS = 51
    STAR = 52
    SLASH = 53
    PERCENT = 54
    PLUS_ASSIGN = 55
    MINUS_ASSIGN = 56
    STAR_ASSIGN = 57
    SLASH_ASSIGN = 58
    PERCENT_ASSIGN = 59
    ALT = 60
    ALT_ASSIGN = 61
    PIPE_ASSIGN = 62


# The keywords of jqlite: the only place they are listed.
KEYWORDS: tuple[tuple[str, int], ...] = (
    ("def", TokenKind.DEF),
    ("as", TokenKind.AS),
    ("if", TokenKind.IF),
    ("then", TokenKind.THEN),
    ("elif", TokenKind.ELIF),
    ("else", TokenKind.ELSE),
    ("end", TokenKind.END),
    ("reduce", TokenKind.REDUCE),
    ("foreach", TokenKind.FOREACH),
    ("try", TokenKind.TRY),
    ("catch", TokenKind.CATCH),
    ("label", TokenKind.LABEL),
    ("break", TokenKind.BREAK),
    ("import", TokenKind.IMPORT),
    ("include", TokenKind.INCLUDE),
    ("module", TokenKind.MODULE),
    ("and", TokenKind.AND),
    ("or", TokenKind.OR),
)

_KEYWORD_KINDS = dict(KEYWORDS)


def keyword_kind(word: str) -> int:
    """The keyword kind of ``word``, or IDENT."""
    return _KEYWORD_KINDS.get(word, TokenKind.IDENT)


def is_keyword(kind: int) -> bool:
    return TokenKind.DEF <= kind <= TokenKind.OR


class Token:
    """A token with the position of its first code point (1-based line and column; columns count code points)."""

    __slots__ = ("kind", "text", "number", "line", "column")

    def __init__(self, kind: int, text: str, number: float, line: int, column: int) -> None:
        self.kind = kind
        self.text = text
        self.number = number
        self.line = line
        self.column = column

    def describe(self) -> str:
        """The token as the parser's messages show it: ``'|='``, ``end of program``, ``'.name'``."""
        k = self.kind
        if k == TokenKind.EOF:
            return "end of program"
        if k == TokenKind.ERROR:
            return self.text
        if k == TokenKind.FIELD:
            return f"'.{self.text}'"
        if k == TokenKind.VARIABLE:
            return f"'${self.text}'"
        if k == TokenKind.FORMAT:
            return f"'@{self.text}'"
        if k == TokenKind.STRING or k == TokenKind.STRING_START:
            return "string"
        if k == TokenKind.STRING_MIDDLE or k == TokenKind.STRING_END:
            return "')'"
        return f"'{self.text}'"

    def __repr__(self) -> str:
        return f"Token({self.kind}, {self.text!r}, {self.line}:{self.column})"
