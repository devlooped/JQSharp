"""Turns jqlite source text into tokens (Lexer.cs; spec §2), longest match first.

Never raises: a lexical error ends the list with a token of kind ERROR whose text is the message;
otherwise the list ends with EOF.

String interpolation is lexed with a stack, without recursion: ``\\(`` ends the current string
piece (STRING_START or STRING_MIDDLE) and pushes a frame; the lexer then produces the ordinary
tokens of the interpolated expression, counting the parentheses opened inside it; the ``)`` that
closes the frame resumes the string (STRING_MIDDLE or STRING_END).

Positions: lines are separated by LF (a CR before it is white space, a lone CR is white space
too, as in jq); columns count code points.
"""

from __future__ import annotations

from ..values import value_text
from ..values.code_points import sanitize
from .syntax_error import SyntaxMessages
from .token import Token, TokenKind, keyword_kind

# Punctuation by length, so that the longest one matches first.
_PUNCTUATION3 = {"?//": TokenKind.QUESTION_ALT, "//=": TokenKind.ALT_ASSIGN}
_PUNCTUATION2 = {
    "..": TokenKind.DOT_DOT, "|=": TokenKind.PIPE_ASSIGN, "==": TokenKind.EQUAL, "!=": TokenKind.NOT_EQUAL,
    "<=": TokenKind.LESS_EQUAL, ">=": TokenKind.GREATER_EQUAL, "+=": TokenKind.PLUS_ASSIGN,
    "-=": TokenKind.MINUS_ASSIGN, "*=": TokenKind.STAR_ASSIGN, "/=": TokenKind.SLASH_ASSIGN,
    "%=": TokenKind.PERCENT_ASSIGN, "//": TokenKind.ALT,
}
_PUNCTUATION1 = {
    ".": TokenKind.DOT, "[": TokenKind.LEFT_BRACKET, "]": TokenKind.RIGHT_BRACKET, "{": TokenKind.LEFT_BRACE,
    "}": TokenKind.RIGHT_BRACE, "(": TokenKind.LEFT_PAREN, ")": TokenKind.RIGHT_PAREN, "|": TokenKind.PIPE,
    ",": TokenKind.COMMA, ":": TokenKind.COLON, ";": TokenKind.SEMICOLON, "?": TokenKind.QUESTION,
    "=": TokenKind.ASSIGN, "<": TokenKind.LESS, ">": TokenKind.GREATER, "+": TokenKind.PLUS,
    "-": TokenKind.MINUS, "*": TokenKind.STAR, "/": TokenKind.SLASH, "%": TokenKind.PERCENT,
}

_SIMPLE_ESCAPES = {'"': '"', "\\": "\\", "/": "/", "b": "\b", "f": "\f", "n": "\n", "r": "\r", "t": "\t"}
_HEX = {c: int(c, 16) for c in "0123456789abcdefABCDEF"}


def _is_digit(c: str) -> bool:
    return "0" <= c <= "9"


def _is_ident_start(c: str) -> bool:
    return ("a" <= c <= "z") or ("A" <= c <= "Z") or c == "_"


def _is_ident_part(c: str) -> bool:
    return ("a" <= c <= "z") or ("A" <= c <= "Z") or c == "_" or ("0" <= c <= "9")


class Lexer:
    __slots__ = ("source", "tokens", "interpolations", "index", "line", "column")

    def __init__(self, source: str) -> None:
        # A lone surrogate of the host string is U+FFFD (§4.1).
        self.source = sanitize(source)
        self.tokens: list[Token] = []
        # One frame per open interpolation: [parentheses opened inside it, line, column of the
        # opening quote of its string] (for "Unterminated string").
        self.interpolations: list[list[int]] = []
        self.index = 0
        self.line = 1
        self.column = 1

    @staticmethod
    def tokenize(source: str) -> list[Token]:
        """The tokens of ``source``, ending with an EOF or ERROR token."""
        lexer = Lexer(source)
        while lexer._next():
            pass
        return lexer.tokens

    def _at(self, offset: int) -> str:
        source = self.source
        return source[offset] if offset < len(source) else "\0"

    def _advance(self) -> None:
        # Moves past one code point, keeping line and column.
        c = self.source[self.index]
        self.index += 1
        if c == "\n":
            self.line += 1
            self.column = 1
        else:
            self.column += 1

    def _advance_by(self, n: int) -> None:
        # Moves past n code points that are not line feeds.
        self.index += n
        self.column += n

    def _add(self, kind: int, text: str, line: int, column: int) -> None:
        self.tokens.append(Token(kind, text, 0.0, line, column))

    def _fail(self, message: str, line: int, column: int) -> bool:
        self._add(TokenKind.ERROR, message, line, column)
        return False

    def _next(self) -> bool:
        # Adds the next token; false after the last one (EOF or ERROR).
        self._skip_trivia()
        start_line = self.line
        start_column = self.column
        source = self.source
        index = self.index
        if index >= len(source):
            if self.interpolations:
                frame = self.interpolations[-1]
                return self._fail(SyntaxMessages.UNTERMINATED_STRING, frame[1], frame[2])
            self._add(TokenKind.EOF, "", start_line, start_column)
            return False

        c = source[index]
        if c == '"':
            self._advance()
            return self._lex_string_piece(start_line, start_column, start_line, start_column, True)

        if _is_digit(c) or (c == "." and _is_digit(self._at(index + 1))):
            return self._lex_number(start_line, start_column)

        if _is_ident_start(c):
            name = self._read_identifier(True)
            kind = TokenKind.IDENT if ":" in name else keyword_kind(name)
            self._add(kind, name, start_line, start_column)
            return True

        if c == "." and _is_ident_start(self._at(index + 1)):
            self._advance()
            self._add(TokenKind.FIELD, self._read_identifier(False), start_line, start_column)
            return True

        if c == "$":
            self._advance()
            if self.index >= len(source) or not _is_ident_start(source[self.index]):
                return self._fail(SyntaxMessages.VARIABLE_NAME_EXPECTED, start_line, start_column)
            self._add(TokenKind.VARIABLE, self._read_identifier(True), start_line, start_column)
            return True

        if c == "@":
            self._advance()
            start = self.index
            while _is_ident_part(self._at(self.index)):
                self._advance()
            if self.index == start:
                return self._fail(SyntaxMessages.FORMAT_NAME_EXPECTED, start_line, start_column)
            self._add(TokenKind.FORMAT, source[start:self.index], start_line, start_column)
            return True

        interpolations = self.interpolations
        if c == "(" and interpolations:
            interpolations[-1][0] += 1

        if c == ")" and interpolations:
            frame = interpolations[-1]
            if frame[0] == 0:
                # The ')' that closes "\(": the string resumes.
                interpolations.pop()
                self._advance()
                return self._lex_string_piece(start_line, start_column, frame[1], frame[2], False)
            frame[0] -= 1

        for table, size in ((_PUNCTUATION3, 3), (_PUNCTUATION2, 2), (_PUNCTUATION1, 1)):
            text = source[index:index + size]
            punctuation = table.get(text)
            if punctuation is not None:
                self._advance_by(size)
                self._add(punctuation, text, start_line, start_column)
                return True

        return self._fail(SyntaxMessages.invalid_character(ord(c)), start_line, start_column)

    def _skip_trivia(self) -> None:
        # White space (space, tab, CR, LF) and comments. A comment runs to the end of the line; when
        # an odd number of '\' ends the line (before LF or CR LF), the comment goes on with the next line.
        source = self.source
        n = len(source)
        while self.index < n:
            c = source[self.index]
            if c == " " or c == "\t" or c == "\r" or c == "\n":
                self._advance()
                continue
            if c != "#":
                return
            start = self.index
            while self.index < n:
                if source[self.index] == "\n":
                    j = self.index - 1
                    if j > start and source[j] == "\r":
                        j -= 1
                    backslashes = 0
                    while j > start and source[j] == "\\":
                        backslashes += 1
                        j -= 1
                    if backslashes % 2 == 0:
                        break
                self._advance()

    def _lex_string_piece(self, token_line: int, token_column: int, quote_line: int, quote_column: int, first: bool) -> bool:
        # A string piece: after the opening quote, or after the ')' that closes an interpolation.
        # Ends at the closing quote (STRING or STRING_END) or at "\(" (STRING_START or STRING_MIDDLE).
        source = self.source
        n = len(source)
        text: list[str] = []
        while True:
            if self.index >= n:
                return self._fail(SyntaxMessages.UNTERMINATED_STRING, quote_line, quote_column)
            c = source[self.index]
            if c == '"':
                self._advance()
                self._add(TokenKind.STRING if first else TokenKind.STRING_END, "".join(text), token_line, token_column)
                return True
            if c != "\\":
                text.append(c)
                self._advance()
                continue

            escape_line = self.line
            escape_column = self.column
            self._advance()
            e = self._at(self.index)
            simple = _SIMPLE_ESCAPES.get(e)
            if simple is not None:
                text.append(simple)
                self._advance()
                continue
            if e == "u":
                unit = self._hex_at(self.index + 1)
                if unit < 0:
                    return self._fail(SyntaxMessages.INVALID_ESCAPE, escape_line, escape_column)
                self._advance_by(5)
                low = self._hex_at(self.index + 2) if self._at(self.index) == "\\" and self._at(self.index + 1) == "u" else -1
                if 0xD800 <= unit <= 0xDBFF and 0xDC00 <= low <= 0xDFFF:
                    # A high surrogate escape followed by a low one: one code point.
                    text.append(chr(0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00)))
                    self._advance_by(6)
                else:
                    # Any other surrogate escape is U+FFFD.
                    text.append("�" if 0xD800 <= unit <= 0xDFFF else chr(unit))
                continue
            if e == "(":
                self._advance()
                self._add(TokenKind.STRING_START if first else TokenKind.STRING_MIDDLE, "".join(text), token_line, token_column)
                self.interpolations.append([0, quote_line, quote_column])
                return True
            if self.index >= n:
                return self._fail(SyntaxMessages.UNTERMINATED_STRING, quote_line, quote_column)
            return self._fail(SyntaxMessages.INVALID_ESCAPE, escape_line, escape_column)

    def _lex_number(self, start_line: int, start_column: int) -> bool:
        # ([0-9]+ ('.' [0-9]*)? | '.' [0-9]+) ([eE] [+-]? [0-9]+)?
        start = self.index
        while _is_digit(self._at(self.index)):
            self._advance()
        if self._at(self.index) == ".":
            self._advance()
            while _is_digit(self._at(self.index)):
                self._advance()
        if self._at(self.index) in ("e", "E"):
            digit = self.index + 2 if self._at(self.index + 1) in ("+", "-") else self.index + 1
            if _is_digit(self._at(digit)):
                while self.index < digit:
                    self._advance()
                while _is_digit(self._at(self.index)):
                    self._advance()
        text = self.source[start:self.index]
        # The grammar above is a subset of the numeric text of value_text, so the conversion succeeds.
        number = value_text.try_parse_number(text)
        self.tokens.append(Token(TokenKind.NUMBER, text, number if number is not None else 0.0, start_line, start_column))
        return True

    def _read_identifier(self, qualified: bool) -> str:
        # [A-Za-z_][A-Za-z0-9_]*, and when qualified, any number of "::" each followed by such a
        # name (jq 1.8.2 accepts a::b::c as one name: an import alias may itself be qualified).
        start = self.index
        while _is_ident_part(self._at(self.index)):
            self._advance()
        while qualified and self._at(self.index) == ":" and self._at(self.index + 1) == ":" and _is_ident_start(self._at(self.index + 2)):
            self._advance_by(2)
            while _is_ident_part(self._at(self.index)):
                self._advance()
        return self.source[start:self.index]

    def _hex_at(self, offset: int) -> int:
        # The four hex digits at offset, as a number, or -1.
        value = 0
        for i in range(4):
            digit = _HEX.get(self._at(offset + i), -1)
            if digit < 0:
                return -1
            value = value * 16 + digit
        return value
