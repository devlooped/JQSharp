package com.devlooped.jqlite.syntax;

import com.devlooped.jqlite.values.CodePoints;
import com.devlooped.jqlite.values.ValueText;
import java.util.ArrayList;
import java.util.List;

/**
 * Turns jqlite source text into tokens (spec §2), longest match first. Never throws: a lexical
 * error ends the list with a token of kind {@link TokenKind#ERROR} whose text is the message;
 * otherwise the list ends with {@link TokenKind#EOF}.
 *
 * <p>String interpolation is lexed with a stack, without recursion: {@code \(} ends the current
 * string piece (STRING_START or STRING_MIDDLE) and pushes a frame; the lexer then produces the
 * ordinary tokens of the interpolated expression, counting the parentheses opened inside it; the
 * {@code )} that closes the frame resumes the string (STRING_MIDDLE or STRING_END). Nesting is any
 * number of frames; the parser limits it (spec §11).
 *
 * <p>Positions: lines are separated by LF (a CR before it is white space, a lone CR is white space
 * too, as in jq); columns count code points.
 */
public final class Lexer {
    private record Punct(String text, TokenKind kind) {
    }

    // Punctuation, longest first where one is a prefix of another.
    private static final Punct[] PUNCTUATION = {
        new Punct("?//", TokenKind.QUESTION_ALT),
        new Punct("//=", TokenKind.ALT_ASSIGN),
        new Punct("..", TokenKind.DOT_DOT),
        new Punct("|=", TokenKind.PIPE_ASSIGN),
        new Punct("==", TokenKind.EQUAL),
        new Punct("!=", TokenKind.NOT_EQUAL),
        new Punct("<=", TokenKind.LESS_EQUAL),
        new Punct(">=", TokenKind.GREATER_EQUAL),
        new Punct("+=", TokenKind.PLUS_ASSIGN),
        new Punct("-=", TokenKind.MINUS_ASSIGN),
        new Punct("*=", TokenKind.STAR_ASSIGN),
        new Punct("/=", TokenKind.SLASH_ASSIGN),
        new Punct("%=", TokenKind.PERCENT_ASSIGN),
        new Punct("//", TokenKind.ALT),
        new Punct(".", TokenKind.DOT),
        new Punct("[", TokenKind.LEFT_BRACKET),
        new Punct("]", TokenKind.RIGHT_BRACKET),
        new Punct("{", TokenKind.LEFT_BRACE),
        new Punct("}", TokenKind.RIGHT_BRACE),
        new Punct("(", TokenKind.LEFT_PAREN),
        new Punct(")", TokenKind.RIGHT_PAREN),
        new Punct("|", TokenKind.PIPE),
        new Punct(",", TokenKind.COMMA),
        new Punct(":", TokenKind.COLON),
        new Punct(";", TokenKind.SEMICOLON),
        new Punct("?", TokenKind.QUESTION),
        new Punct("=", TokenKind.ASSIGN),
        new Punct("<", TokenKind.LESS),
        new Punct(">", TokenKind.GREATER),
        new Punct("+", TokenKind.PLUS),
        new Punct("-", TokenKind.MINUS),
        new Punct("*", TokenKind.STAR),
        new Punct("/", TokenKind.SLASH),
        new Punct("%", TokenKind.PERCENT),
    };

    // One frame per open interpolation: the parentheses opened inside it, and the position of the
    // opening quote of its string (for "Unterminated string").
    private static final class Interpolation {
        int parens;
        final int line;
        final int column;

        Interpolation(int line, int column) {
            this.line = line;
            this.column = column;
        }
    }

    private final String source;
    private final List<Token> tokens = new ArrayList<>();
    private final List<Interpolation> interpolations = new ArrayList<>();
    private int index;
    private int line = 1;
    private int column = 1;

    private Lexer(String source) {
        this.source = source;
    }

    /** The tokens of {@code source}, ending with an EOF or ERROR token. */
    public static List<Token> tokenize(String source) {
        Lexer lexer = new Lexer(source);
        while (lexer.next()) {
            // Each call adds one token.
        }

        return lexer.tokens;
    }

    // Adds the next token; false after the last one (EOF or ERROR).
    private boolean next() {
        skipTrivia();
        int startLine = line;
        int startColumn = column;
        if (index >= source.length()) {
            if (!interpolations.isEmpty()) {
                Interpolation last = interpolations.get(interpolations.size() - 1);
                return fail(SyntaxMessages.UNTERMINATED_STRING, last.line, last.column);
            }

            add(TokenKind.EOF, "", startLine, startColumn);
            return false;
        }

        char c = source.charAt(index);
        if (c == '"') {
            advance();
            return lexStringPiece(startLine, startColumn, startLine, startColumn, true);
        }

        if (isDigit(c) || (c == '.' && isDigit(at(index + 1)))) {
            return lexNumber(startLine, startColumn);
        }

        if (isIdentStart(c)) {
            String name = readIdentifier(true);
            TokenKind kind = name.indexOf(':') >= 0 ? TokenKind.IDENT : Token.keywordKind(name);
            add(kind, name, startLine, startColumn);
            return true;
        }

        if (c == '.' && isIdentStart(at(index + 1))) {
            advance();
            add(TokenKind.FIELD, readIdentifier(false), startLine, startColumn);
            return true;
        }

        if (c == '$') {
            advance();
            if (index >= source.length() || !isIdentStart(source.charAt(index))) {
                return fail(SyntaxMessages.VARIABLE_NAME_EXPECTED, startLine, startColumn);
            }
            add(TokenKind.VARIABLE, readIdentifier(true), startLine, startColumn);
            return true;
        }

        if (c == '@') {
            advance();
            int start = index;
            while (isIdentPart(at(index))) {
                advance();
            }
            if (index == start) {
                return fail(SyntaxMessages.FORMAT_NAME_EXPECTED, startLine, startColumn);
            }
            add(TokenKind.FORMAT, source.substring(start, index), startLine, startColumn);
            return true;
        }

        if (c == '(' && !interpolations.isEmpty()) {
            interpolations.get(interpolations.size() - 1).parens++;
        }

        if (c == ')' && !interpolations.isEmpty()) {
            Interpolation frame = interpolations.get(interpolations.size() - 1);
            if (frame.parens == 0) {
                // The ')' that closes "\(": the string resumes.
                interpolations.remove(interpolations.size() - 1);
                advance();
                return lexStringPiece(startLine, startColumn, frame.line, frame.column, false);
            }

            frame.parens--;
        }

        for (Punct punct : PUNCTUATION) {
            if (source.startsWith(punct.text, index)) {
                for (int i = 0; i < punct.text.length(); i++) {
                    advance();
                }
                add(punct.kind, punct.text, startLine, startColumn);
                return true;
            }
        }

        return fail(SyntaxMessages.invalidCharacter(codePointAt(index)), startLine, startColumn);
    }

    // White space (space, tab, CR, LF) and comments. A comment runs to the end of the line; when an
    // odd number of '\' ends the line (before LF or CR LF), the comment goes on with the next line.
    private void skipTrivia() {
        while (index < source.length()) {
            char c = source.charAt(index);
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') {
                advance();
                continue;
            }

            if (c != '#') {
                return;
            }

            int start = index;
            while (index < source.length()) {
                if (source.charAt(index) == '\n') {
                    int j = index - 1;
                    if (j > start && source.charAt(j) == '\r') {
                        j--;
                    }
                    int backslashes = 0;
                    for (; j > start && source.charAt(j) == '\\'; j--) {
                        backslashes++;
                    }
                    if (backslashes % 2 == 0) {
                        break;
                    }
                }

                advance();
            }
        }
    }

    // A string piece: after the opening quote, or after the ')' that closes an interpolation. Ends
    // at the closing quote (STRING or STRING_END) or at "\(" (STRING_START or STRING_MIDDLE).
    private boolean lexStringPiece(int tokenLine, int tokenColumn, int quoteLine, int quoteColumn, boolean first) {
        StringBuilder text = new StringBuilder();
        while (true) {
            if (index >= source.length()) {
                return fail(SyntaxMessages.UNTERMINATED_STRING, quoteLine, quoteColumn);
            }

            char c = source.charAt(index);
            if (c == '"') {
                advance();
                add(first ? TokenKind.STRING : TokenKind.STRING_END, text.toString(), tokenLine, tokenColumn);
                return true;
            }

            if (c != '\\') {
                CodePoints.append(text, codePointAt(index));
                advance();
                continue;
            }

            int escapeLine = line;
            int escapeColumn = column;
            advance();
            char e = at(index);
            switch (e) {
                case '"', '\\', '/' -> text.append(e);
                case 'b' -> text.append('\b');
                case 'f' -> text.append('\f');
                case 'n' -> text.append('\n');
                case 'r' -> text.append('\r');
                case 't' -> text.append('\t');
                case 'u' -> {
                    int unit = hexAt(index + 1);
                    if (unit < 0) {
                        return fail(SyntaxMessages.INVALID_ESCAPE, escapeLine, escapeColumn);
                    }
                    for (int i = 0; i < 5; i++) {
                        advance();
                    }
                    int low = at(index) == '\\' && at(index + 1) == 'u' ? hexAt(index + 2) : -1;
                    if (unit >= 0xD800 && unit <= 0xDBFF && low >= 0xDC00 && low <= 0xDFFF) {
                        // A high surrogate escape followed by a low one: one code point.
                        CodePoints.append(text, 0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00));
                        for (int i = 0; i < 6; i++) {
                            advance();
                        }
                    } else {
                        // Any other surrogate escape is U+FFFD.
                        CodePoints.append(text, unit >= 0xD800 && unit <= 0xDFFF ? CodePoints.REPLACEMENT : unit);
                    }

                    continue;
                }
                case '(' -> {
                    advance();
                    add(first ? TokenKind.STRING_START : TokenKind.STRING_MIDDLE, text.toString(), tokenLine, tokenColumn);
                    interpolations.add(new Interpolation(quoteLine, quoteColumn));
                    return true;
                }
                default -> {
                    return index >= source.length()
                        ? fail(SyntaxMessages.UNTERMINATED_STRING, quoteLine, quoteColumn)
                        : fail(SyntaxMessages.INVALID_ESCAPE, escapeLine, escapeColumn);
                }
            }

            advance();
        }
    }

    // ([0-9]+ ('.' [0-9]*)? | '.' [0-9]+) ([eE] [+-]? [0-9]+)?
    private boolean lexNumber(int startLine, int startColumn) {
        int start = index;
        while (isDigit(at(index))) {
            advance();
        }
        if (at(index) == '.') {
            advance();
            while (isDigit(at(index))) {
                advance();
            }
        }

        if (at(index) == 'e' || at(index) == 'E') {
            int digit = at(index + 1) == '+' || at(index + 1) == '-' ? index + 2 : index + 1;
            if (isDigit(at(digit))) {
                while (index < digit) {
                    advance();
                }
                while (isDigit(at(index))) {
                    advance();
                }
            }
        }

        String text = source.substring(start, index);
        // The grammar above is a subset of the numeric text of ValueText, so the conversion succeeds.
        Double number = ValueText.tryParseNumber(text);
        tokens.add(new Token(TokenKind.NUMBER, text, number == null ? 0 : number, startLine, startColumn));
        return true;
    }

    // [A-Za-z_][A-Za-z0-9_]*, and when qualified, any number of "::" each followed by such a name
    // (jq 1.8.2 accepts a::b::c as one name: an import alias may itself be qualified).
    private String readIdentifier(boolean qualified) {
        int start = index;
        while (isIdentPart(at(index))) {
            advance();
        }
        while (qualified && at(index) == ':' && at(index + 1) == ':' && isIdentStart(at(index + 2))) {
            advance();
            advance();
            while (isIdentPart(at(index))) {
                advance();
            }
        }

        return source.substring(start, index);
    }

    // The four hex digits at offset, as a number, or -1.
    private int hexAt(int offset) {
        int value = 0;
        for (int i = 0; i < 4; i++) {
            char c = at(offset + i);
            int digit = c >= '0' && c <= '9' ? c - '0'
                : c >= 'a' && c <= 'f' ? c - 'a' + 10
                : c >= 'A' && c <= 'F' ? c - 'A' + 10
                : -1;
            if (digit < 0) {
                return -1;
            }
            value = value * 16 + digit;
        }

        return value;
    }

    // The code point at offset: a surrogate pair is one code point, a lone surrogate is U+FFFD.
    private int codePointAt(int offset) {
        char c = source.charAt(offset);
        if (Character.isHighSurrogate(c) && offset + 1 < source.length() && Character.isLowSurrogate(source.charAt(offset + 1))) {
            return Character.toCodePoint(c, source.charAt(offset + 1));
        }
        return Character.isSurrogate(c) ? CodePoints.REPLACEMENT : c;
    }

    // Moves past one code point, keeping line and column.
    private void advance() {
        char c = source.charAt(index);
        index += Character.isHighSurrogate(c) && index + 1 < source.length() && Character.isLowSurrogate(source.charAt(index + 1)) ? 2 : 1;
        if (c == '\n') {
            line++;
            column = 1;
        } else {
            column++;
        }
    }

    private char at(int offset) {
        return offset < source.length() ? source.charAt(offset) : '\0';
    }

    private void add(TokenKind kind, String text, int tokenLine, int tokenColumn) {
        tokens.add(new Token(kind, text, 0, tokenLine, tokenColumn));
    }

    private boolean fail(String message, int errorLine, int errorColumn) {
        add(TokenKind.ERROR, message, errorLine, errorColumn);
        return false;
    }

    private static boolean isDigit(char c) {
        return c >= '0' && c <= '9';
    }

    private static boolean isIdentStart(char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';
    }

    private static boolean isIdentPart(char c) {
        return isIdentStart(c) || isDigit(c);
    }
}
