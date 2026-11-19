package com.devlooped.jqlite.syntax;

import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqValue;

/**
 * Every message text of the syntax layer, in one place. The texts of spec §10.2 are fixed; the
 * others are this implementation's. (In the reference, this class shares SyntaxError.cs.)
 */
public final class SyntaxMessages {
    private SyntaxMessages() {
    }

    // Fixed by spec §10.2.
    public static final String INVALID_ESCAPE = "Invalid escape";
    public static final String MAY_NEED_PARENTHESES = "May need parentheses around object key expression";
    public static final String METADATA_NOT_CONSTANT = "Module metadata must be constant";
    public static final String METADATA_NOT_OBJECT = "Module metadata must be an object";
    public static final String IMPORT_PATH_NOT_CONSTANT = "Import path must be constant";

    /** {@code Cannot use number (1) as object key}. */
    public static String invalidObjectKey(JqValue key) {
        return Errors.cannotUseAsKey(key).asString();
    }

    // Fixed by spec §11.
    public static final String TOO_DEEPLY_NESTED = "Program too deeply nested";

    // This implementation's.
    public static final String UNTERMINATED_STRING = "Unterminated string";
    public static final String VARIABLE_NAME_EXPECTED = "Expected a variable name after '$'";
    public static final String FORMAT_NAME_EXPECTED = "Expected a format name after '@'";
    public static final String MODULE_HAS_BODY = "A module contains only definitions";

    public static String invalidCharacter(int codePoint) {
        if (codePoint < 0x20 || codePoint == 0x7F || (codePoint >= 0xD800 && codePoint <= 0xDFFF)) {
            String digits = "0123456789ABCDEF";
            StringBuilder hex = new StringBuilder();
            for (int shift = 12; shift >= 0; shift -= 4) {
                hex.append(digits.charAt((codePoint >> shift) & 0xF));
            }
            return "Invalid character U+" + hex;
        }
        return "Invalid character '" + new String(Character.toChars(codePoint)) + "'";
    }

    public static String unexpected(Token token) {
        return "Unexpected " + token.describe();
    }

    public static String expected(String what, Token token) {
        return "Expected " + what + " but found " + token.describe();
    }

    public static String notAssociative(Token token) {
        return "Operator " + token.describe() + " cannot be chained: add parentheses";
    }

    public static String questionAlt(Token token) {
        return "Unexpected " + token.describe() + ": write '? //' for an optional followed by an alternative";
    }
}
