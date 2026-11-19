package com.devlooped.jqlite.core.syntax;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import com.devlooped.jqlite.syntax.Lexer;
import com.devlooped.jqlite.syntax.Token;
import com.devlooped.jqlite.syntax.TokenKind;
import java.util.List;
import java.util.stream.Collectors;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.MethodSource;

class LexerTests {
    private static List<Token> lex(String source) {
        return Lexer.tokenize(source);
    }

    // Kind, text and position of each token, without the final EOF.
    private static String describe(String source) {
        return lex(source).stream().filter(t -> t.kind() != TokenKind.EOF)
            .map(t -> t.kind() + ":" + t.text() + "@" + t.line() + ":" + t.column()).collect(Collectors.joining(" "));
    }

    private static String kinds(String source) {
        return lex(source).stream().filter(t -> t.kind() != TokenKind.EOF).map(t -> t.kind().toString()).collect(Collectors.joining(" "));
    }

    private static String texts(String source) {
        return lex(source).stream().filter(t -> t.kind() != TokenKind.EOF).map(Token::text).collect(Collectors.joining(" "));
    }

    private static Token single(String source) {
        List<Token> tokens = lex(source).stream().filter(t -> t.kind() != TokenKind.EOF).toList();
        assertEquals(1, tokens.size(), source);
        return tokens.get(0);
    }

    private static String error(String source) {
        List<Token> tokens = lex(source);
        Token last = tokens.get(tokens.size() - 1);
        assertEquals(TokenKind.ERROR, last.kind());
        return last.text() + " @" + last.line() + ":" + last.column();
    }

    @Test
    void everyPunctuationTokenLongestFirst() {
        String source = ". .. [ ] { } ( ) | , : ; ? ?// = == != < <= > >= + - * / % += -= *= /= %= // //= |=";
        assertEquals("DOT DOT_DOT LEFT_BRACKET RIGHT_BRACKET LEFT_BRACE RIGHT_BRACE LEFT_PAREN RIGHT_PAREN PIPE COMMA COLON SEMICOLON QUESTION QUESTION_ALT ASSIGN EQUAL NOT_EQUAL LESS LESS_EQUAL GREATER GREATER_EQUAL PLUS MINUS STAR SLASH PERCENT PLUS_ASSIGN MINUS_ASSIGN STAR_ASSIGN SLASH_ASSIGN PERCENT_ASSIGN ALT ALT_ASSIGN PIPE_ASSIGN", kinds(source));
        assertEquals(source, texts(source));
    }

    @Test
    void adjacentPunctuationTakesTheLongestMatch() {
        assertEquals("QUESTION_ALT ASSIGN", kinds("?//="));
        assertEquals("DOT_DOT DOT", kinds("..."));
        assertEquals("ALT SLASH", kinds("///"));
        assertEquals("PIPE_ASSIGN ASSIGN", kinds("|=="));
    }

    @Test
    void everyKeyword() {
        String[] words = {"def", "as", "if", "then", "elif", "else", "end", "reduce", "foreach", "try", "catch", "label", "break", "import", "include", "module", "and", "or"};
        for (String word : words) {
            Token token = single(word);
            assertEquals(TokenKind.valueOf(word.toUpperCase(java.util.Locale.ROOT)), token.kind());
            assertEquals(word, token.text());
            assertTrue(Token.isKeyword(token.kind()));
        }
    }

    @Test
    void names() {
        assertEquals("IDENT:foo@1:1 IDENT:a::b@1:5 IDENT:_x1@1:10 IDENT:null@1:14 IDENT:not@1:19", describe("foo a::b _x1 null not"));
        assertEquals("FIELD:a@1:1 FIELD:end@1:3 FIELD:e0@1:7 FIELD:_@1:10", describe(".a.end.e0._"));
        assertEquals("VARIABLE:x@1:1 VARIABLE:then@1:4 VARIABLE:a::b@1:10 VARIABLE:__loc__@1:16", describe("$x $then $a::b $__loc__"));
        assertEquals("FORMAT:base64@1:1 FORMAT:base64d@1:9 FORMAT:1x@1:18", describe("@base64 @base64d @1x"));
    }

    @Test
    void aQualifiedNameMayHaveSeveralDoubleColons() {
        assertEquals("IDENT", kinds("a::b::c"));
        assertEquals("a::b::c", texts("a::b::c"));
        assertEquals("VARIABLE", kinds("$a::b::c"));
        assertEquals("a : :", texts("a::"));
        assertEquals("FIELD COLON COLON IDENT", kinds(".a::b"));
    }

    static Object[][] numbers() {
        return new Object[][] {
            {"0", 0d}, {"42", 42d}, {".5", 0.5}, {"1.", 1d}, {"1.5", 1.5}, {"1e3", 1000d}, {"1E+3", 1000d}, {"1.e3", 1000d},
            {".5e1", 5d}, {"25e-1", 2.5}, {"0.1", 0.1}, {"1e1000", Double.POSITIVE_INFINITY}, {"1e-1000", 0d},
            {"100000000000000000000000000001", 1e29},
        };
    }

    @ParameterizedTest
    @MethodSource("numbers")
    void numbersLex(String source, double value) {
        Token token = single(source);
        assertEquals(TokenKind.NUMBER, token.kind());
        assertEquals(value, token.number());
        assertEquals(source, token.text());
    }

    @Test
    void numberBoundaries() {
        assertEquals("FIELD", kinds(".e0"));
        assertEquals("NUMBER IDENT", kinds("1e"));
        assertEquals("NUMBER IDENT PLUS", kinds("1e+"));
        assertEquals("1.5 .3", texts("1.5.3"));
        assertEquals("DOT_DOT NUMBER", kinds("..5"));
        assertEquals("NUMBER IDENT", kinds("1x"));
    }

    @Test
    void positionsAcrossLineBreaksAndNonBmpCharacters() {
        // "😀😀" is 4 code points; columns count code points, not UTF-16 units.
        assertEquals("STRING:😀😀@1:1 PLUS:+@1:6 NUMBER:1@2:3 PLUS:+@3:1 FIELD:a@3:3", describe("\"😀😀\" +\n  1\r\n+ .a"));
        assertEquals("STRING:a\nb@1:1 IDENT:x@2:4", describe("\"a\nb\" x"));
    }

    @Test
    void aLoneCrIsWhiteSpaceNotALineBreak() {
        assertEquals("NUMBER:1@1:1 PLUS:+@1:3 NUMBER:2@1:5", describe("1\r+\r2"));
    }

    @Test
    void comments() {
        assertEquals("NUMBER:1@1:1 PLUS:+@2:1 NUMBER:2@2:3", describe("1 # a comment\n+ 2 # another"));
        // An odd number of backslashes at the end of the line continues the comment.
        assertEquals("1 + 10", texts("1 # c \\\n+ 1\n+ 10"));
        assertEquals("1 + 1 + 10", texts("1 # c \\\\\n+ 1\n+ 10"));
        assertEquals("1 + 10", texts("1 # c \\\\\\\n+ 1\n+ 10"));
        assertEquals("1 + 10", texts("1 # c \\\r\n+ 1\r\n+ 10"));
        assertEquals("1 + 1 + 10", texts("1 # c \\ x\n+ 1\n+ 10"));
        // As in jq, a lone CR does not end a comment.
        assertEquals("1", texts("1 # c\r+ 1"));
        assertEquals("1 + 10", texts("1 # c \\\r+ 1\n+ 10"));
    }

    static String[][] escapes() {
        return new String[][] {
            {"\"abc\"", "abc"},
            {"\"\\\"\\\\\\/\\b\\f\\n\\r\\t\"", "\"\\/\b\f\n\r\t"},
            {"\"\\u00e9\\u00C9\"", "éÉ"},
            {"\"\\uD83D\\uDE00\"", "😀"},
            {"\"\\ud83d x\"", "\uFFFD x"},
            {"\"\\ude00\"", "\uFFFD"},
            {"\"\\uDE00\\uD83D\"", "\uFFFD\uFFFD"},
            {"\"\\uD83D\\u0041\"", "\uFFFDA"},
            {"\"\\uD83D\\uD83D\\uDE00\"", "\uFFFD😀"},
            {"\"\\u12345\"", "\u12345"},
            {"\"é\t\"", "é\t"},
        };
    }

    @ParameterizedTest
    @MethodSource("escapes")
    void stringEscapes(String source, String text) {
        Token token = single(source);
        assertEquals(TokenKind.STRING, token.kind());
        assertEquals(text, token.text());
    }

    @Test
    void aLoneSurrogateInTheSourceIsUFffd() {
        Token token = single("\"a\uD800b\uDC00\"");
        assertEquals("a\uFFFDb\uFFFD", token.text());
        assertEquals("Invalid character '\uFFFD' @1:2", error("a\uD800"));
        // A lone surrogate is one column.
        assertEquals("STRING:\uFFFD@1:1 PLUS:+@1:5", describe("\"\uD800\" +"));
    }

    @Test
    void interpolationIsSplitIntoPieces() {
        assertEquals(
            "STRING_START:a@1:1 NUMBER:1@1:5 PLUS:+@1:7 LEFT_PAREN:(@1:9 NUMBER:2@1:10 RIGHT_PAREN:)@1:11 STRING_MIDDLE:b@1:12 STRING:c@1:16 STRING_END:d@1:19",
            describe("\"a\\(1 + (2))b\\(\"c\")d\""));
    }

    @Test
    void interpolationNests() {
        assertEquals("STRING_START STRING_START STRING STRING_END STRING_END", kinds("\"a\\(\"b\\(\"c\")\")\""));
        assertEquals("a b c", texts("\"a\\(\"b\\(\"c\")\")\"").trim());
    }

    @Test
    void aParenthesisOutsideInterpolationIsPunctuation() {
        assertEquals("RIGHT_PAREN", kinds(")"));
    }

    static String[][] errors() {
        return new String[][] {
            {"\"abc", "Unterminated string @1:1"},
            {"1 + \"a\\(1", "Unterminated string @1:5"},
            {"\"a\\(\"b\" + (1", "Unterminated string @1:1"},
            {"\"a\\", "Unterminated string @1:1"},
            {"\"a\\qb\"", "Invalid escape @1:3"},
            {"\"x\"\n \"\\u12\"", "Invalid escape @2:3"},
            {"\"\\u12G4\"", "Invalid escape @1:2"},
            {"\"\\u\"", "Invalid escape @1:2"},
            {"1 $ x", "Expected a variable name after '$' @1:3"},
            {"@", "Expected a format name after '@' @1:1"},
            {"{aé: 1}", "Invalid character 'é' @1:3"},
            {"😀", "Invalid character '😀' @1:1"},
            {"1\f+2", "Invalid character U+000C @1:2"},
            {"1 ! 2", "Invalid character '!' @1:3"},
        };
    }

    @ParameterizedTest
    @MethodSource("errors")
    void errorsAreReported(String source, String expected) {
        assertEquals(expected, error(source));
    }

    @Test
    void anErrorIsTheLastToken() {
        assertEquals("NUMBER PLUS ERROR", lex("1 + \"a\\q\" + 2").stream().map(t -> t.kind().toString()).collect(Collectors.joining(" ")));
    }

    @Test
    void emptySourceIsEof() {
        List<Token> tokens = lex("  # nothing\n");
        assertEquals(1, tokens.size());
        assertEquals(TokenKind.EOF, tokens.get(0).kind());
        assertEquals(2, tokens.get(0).line());
        assertEquals(1, tokens.get(0).column());
    }
}
