package com.devlooped.jqlite.syntax;

/**
 * A token with the position of its first code point (1-based line and column; columns count code
 * points).
 */
public record Token(TokenKind kind, String text, double number, int line, int column) {

    private record Keyword(String word, TokenKind kind) {
    }

    /** The keywords of jqlite: the only place they are listed. */
    private static final Keyword[] KEYWORDS = {
        new Keyword("def", TokenKind.DEF),
        new Keyword("as", TokenKind.AS),
        new Keyword("if", TokenKind.IF),
        new Keyword("then", TokenKind.THEN),
        new Keyword("elif", TokenKind.ELIF),
        new Keyword("else", TokenKind.ELSE),
        new Keyword("end", TokenKind.END),
        new Keyword("reduce", TokenKind.REDUCE),
        new Keyword("foreach", TokenKind.FOREACH),
        new Keyword("try", TokenKind.TRY),
        new Keyword("catch", TokenKind.CATCH),
        new Keyword("label", TokenKind.LABEL),
        new Keyword("break", TokenKind.BREAK),
        new Keyword("import", TokenKind.IMPORT),
        new Keyword("include", TokenKind.INCLUDE),
        new Keyword("module", TokenKind.MODULE),
        new Keyword("and", TokenKind.AND),
        new Keyword("or", TokenKind.OR),
    };

    /** The keyword kind of {@code word}, or {@link TokenKind#IDENT}. */
    public static TokenKind keywordKind(String word) {
        for (Keyword keyword : KEYWORDS) {
            if (keyword.word.equals(word)) {
                return keyword.kind;
            }
        }

        return TokenKind.IDENT;
    }

    public static boolean isKeyword(TokenKind kind) {
        return kind.compareTo(TokenKind.DEF) >= 0 && kind.compareTo(TokenKind.OR) <= 0;
    }

    /** The token as the parser's messages show it: {@code '|='}, {@code end of program}, {@code '.name'}. */
    public String describe() {
        return switch (kind) {
            case EOF -> "end of program";
            case ERROR -> text;
            case FIELD -> "'." + text + "'";
            case VARIABLE -> "'$" + text + "'";
            case FORMAT -> "'@" + text + "'";
            case STRING, STRING_START -> "string";
            case STRING_MIDDLE, STRING_END -> "')'";
            default -> "'" + text + "'";
        };
    }
}
