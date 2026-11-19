package com.devlooped.jqlite.syntax;

/** The kinds of tokens of jqlite (spec §2). */
public enum TokenKind {
    /** The end of the source. */
    EOF,
    /** A lexical error; {@link Token#text} is the message. Always the last token. */
    ERROR,

    /** {@code name} or {@code module::name}; the text is the name as written. */
    IDENT,
    /** {@code .name}; the text is the name without the dot. */
    FIELD,
    /** {@code $name} or {@code $module::name}; the text is the name without {@code $}. */
    VARIABLE,
    /** {@code @name}; the text is the name without {@code @}. */
    FORMAT,
    /** A number literal; {@link Token#number} is its value, the text its source text. */
    NUMBER,

    // String literals. Text is the decoded literal text of the piece. A string without interpolation
    // is one STRING token; "a\(x)b\(y)c" is STRING_START("a"), the tokens of x, STRING_MIDDLE("b"),
    // the tokens of y, STRING_END("c"). STRING_MIDDLE and STRING_END are at the ')' that closes the
    // interpolation.
    STRING,
    STRING_START,
    STRING_MIDDLE,
    STRING_END,

    // Keywords (the list is Token.KEYWORDS).
    DEF,
    AS,
    IF,
    THEN,
    ELIF,
    ELSE,
    END,
    REDUCE,
    FOREACH,
    TRY,
    CATCH,
    LABEL,
    BREAK,
    IMPORT,
    INCLUDE,
    MODULE,
    AND,
    OR,

    // Punctuation.
    DOT,
    DOT_DOT,
    LEFT_BRACKET,
    RIGHT_BRACKET,
    LEFT_BRACE,
    RIGHT_BRACE,
    LEFT_PAREN,
    RIGHT_PAREN,
    PIPE,
    COMMA,
    COLON,
    SEMICOLON,
    QUESTION,
    QUESTION_ALT,
    ASSIGN,
    EQUAL,
    NOT_EQUAL,
    LESS,
    LESS_EQUAL,
    GREATER,
    GREATER_EQUAL,
    PLUS,
    MINUS,
    STAR,
    SLASH,
    PERCENT,
    PLUS_ASSIGN,
    MINUS_ASSIGN,
    STAR_ASSIGN,
    SLASH_ASSIGN,
    PERCENT_ASSIGN,
    ALT,
    ALT_ASSIGN,
    PIPE_ASSIGN,
}
