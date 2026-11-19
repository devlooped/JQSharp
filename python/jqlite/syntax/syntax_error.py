"""The one error of lexing, parsing and binding, and the message texts of the syntax layer (SyntaxError.cs)."""

from __future__ import annotations

from typing import TYPE_CHECKING

from ..values import errors

if TYPE_CHECKING:
    from ..values.jq_value import JqValue
    from .token import Token


class SyntaxError:  # noqa: A001 - the reference's name; never raised
    """A message and the 1-based position of the token where it was detected, in the main program
    or, when ``module`` is set, in the module of that identity (§9.11)."""

    __slots__ = ("message", "line", "column", "module")

    def __init__(self, message: str, line: int, column: int, module: str | None = None) -> None:
        self.message = message
        self.line = line
        self.column = column
        self.module = module

    def with_module(self, module: str) -> SyntaxError:
        return SyntaxError(self.message, self.line, self.column, module)

    def __eq__(self, other: object) -> bool:
        return (isinstance(other, SyntaxError) and self.message == other.message and self.line == other.line
                and self.column == other.column and self.module == other.module)

    def __hash__(self) -> int:
        return hash((self.message, self.line, self.column, self.module))

    def __repr__(self) -> str:
        return f"SyntaxError({self.message!r}, {self.line}, {self.column}, {self.module!r})"


class SyntaxMessages:
    """Every message text of the syntax layer, in one place. The texts of spec §10.2 are fixed;
    the others are this implementation's."""

    # Fixed by spec §10.2.
    INVALID_ESCAPE = "Invalid escape"
    MAY_NEED_PARENTHESES = "May need parentheses around object key expression"
    METADATA_NOT_CONSTANT = "Module metadata must be constant"
    METADATA_NOT_OBJECT = "Module metadata must be an object"
    IMPORT_PATH_NOT_CONSTANT = "Import path must be constant"

    # Fixed by spec §11.
    TOO_DEEPLY_NESTED = "Program too deeply nested"

    # This implementation's.
    UNTERMINATED_STRING = "Unterminated string"
    VARIABLE_NAME_EXPECTED = "Expected a variable name after '$'"
    FORMAT_NAME_EXPECTED = "Expected a format name after '@'"
    MODULE_HAS_BODY = "A module contains only definitions"

    @staticmethod
    def invalid_object_key(key: JqValue) -> str:
        """``Cannot use number (1) as object key``."""
        return errors.cannot_use_as_key(key)

    @staticmethod
    def invalid_character(code_point: int) -> str:
        if code_point < 0x20 or code_point == 0x7F or 0xD800 <= code_point <= 0xDFFF:
            return f"Invalid character U+{code_point:04X}"
        return f"Invalid character '{chr(code_point)}'"

    @staticmethod
    def unexpected(token: Token) -> str:
        return f"Unexpected {token.describe()}"

    @staticmethod
    def expected(what: str, token: Token) -> str:
        return f"Expected {what} but found {token.describe()}"

    @staticmethod
    def not_associative(token: Token) -> str:
        return f"Operator {token.describe()} cannot be chained: add parentheses"

    @staticmethod
    def question_alt(token: Token) -> str:
        return f"Unexpected {token.describe()}: write '? //' for an optional followed by an alternative"
