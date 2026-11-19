package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.Utf8;
import com.devlooped.jqlite.values.ValueText;

/** The {@code @name} formats of §9.9. */
public final class Formats {
    private static final String HEX = "0123456789ABCDEF";
    private static final String BASE64_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    private Formats() {
    }

    /** Applies the format {@code name} (without '@') to {@code value}, giving a string. */
    public static Result tryApply(String name, JqValue value) {
        return switch (name) {
            case "text" -> JqValue.fromString(StringNatives.toText(value));
            case "json" -> JqValue.fromString(ValueText.toJson(value));
            case "html" -> JqValue.fromString(html(StringNatives.toText(value)));
            case "uri" -> JqValue.fromString(uri(StringNatives.toText(value)));
            case "urid" -> uriDecode(StringNatives.toText(value));
            case "csv" -> value.kind() == JqKind.ARRAY ? row(value.asArray(), false) : fail(Errors.cannotBeCsvFormatted(value));
            case "tsv" -> value.kind() == JqKind.ARRAY ? row(value.asArray(), true) : fail(Errors.cannotBeTsvFormatted(value));
            case "sh" -> shell(value.kind() == JqKind.ARRAY ? value.asArray() : JqArray.of(value));
            case "base64" -> JqValue.fromString(base64(StringNatives.toText(value)));
            case "base64d" -> base64Decode(StringNatives.toText(value));
            default -> fail(Errors.invalidFormat(name));
        };
    }

    // @html: & < > ' " as entities.
    private static String html(String text) {
        StringBuilder builder = new StringBuilder(text.length());
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            switch (c) {
                case '&' -> builder.append("&amp;");
                case '<' -> builder.append("&lt;");
                case '>' -> builder.append("&gt;");
                case '\'' -> builder.append("&apos;");
                case '"' -> builder.append("&quot;");
                default -> builder.append(c);
            }
        }

        return builder.toString();
    }

    // @uri: the UTF-8 bytes, unreserved ones (A-Z a-z 0-9 - _ . ~) kept, the others as %XX.
    private static String uri(String text) {
        Utf8.Bytes bytes = new Utf8.Bytes();
        Utf8.encode(text, bytes);
        StringBuilder builder = new StringBuilder(bytes.count());
        for (int i = 0; i < bytes.count(); i++) {
            int b = bytes.get(i);
            if ((b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || (b >= '0' && b <= '9') || b == '-' || b == '_' || b == '.' || b == '~') {
                builder.append((char) b);
            } else {
                builder.append('%').append(HEX.charAt(b >> 4)).append(HEX.charAt(b & 0xF));
            }
        }

        return builder.toString();
    }

    // @urid: %XX (either case) is that byte, any other code point its UTF-8 bytes; the bytes must be well-formed UTF-8.
    private static Result uriDecode(String text) {
        Utf8.Bytes bytes = new Utf8.Bytes();
        for (int i = 0; i < text.length(); i++) {
            int c = text.charAt(i);
            if (c == '%') {
                int high = i + 1 < text.length() ? hexValue(text.charAt(i + 1)) : -1;
                int low = i + 2 < text.length() ? hexValue(text.charAt(i + 2)) : -1;
                if (high < 0 || low < 0) {
                    return fail(Errors.notValidUriEncoding(JqValue.fromString(text)));
                }
                bytes.add((high << 4) | low);
                i += 2;
                continue;
            }

            if (Character.isHighSurrogate(text.charAt(i))) {
                c = 0x10000 + ((c - 0xD800) << 10) + (text.charAt(i + 1) - 0xDC00);
                i++;
            }

            Utf8.encode(c, bytes);
        }

        String decoded = Utf8.tryDecode(bytes);
        return decoded != null ? JqValue.fromString(decoded) : fail(Errors.notValidUriEncoding(JqValue.fromString(text)));
    }

    private static int hexValue(char c) {
        return c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
    }

    // @csv and @tsv: one row; strings quoted (csv) or escaped (tsv), numbers as canonical text
    // (NaN empty), booleans as such, null empty.
    private static Result row(JqArray items, boolean tsv) {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < items.count(); i++) {
            if (i > 0) {
                builder.append(tsv ? '\t' : ',');
            }
            JqValue item = items.get(i);
            switch (item.kind()) {
                case NULL:
                    break;
                case FALSE:
                case TRUE:
                    builder.append(item.kind() == JqKind.TRUE ? "true" : "false");
                    break;
                case NUMBER:
                    if (!Double.isNaN(item.asNumber())) {
                        builder.append(ValueText.formatNumber(item.asNumber()));
                    }
                    break;
                case STRING:
                    if (tsv) {
                        tsvEscape(builder, item.asString());
                    } else {
                        builder.append('"').append(item.asString().replace("\"", "\"\"")).append('"');
                    }
                    break;
                default:
                    return fail(Errors.notValidInCsvRow(item));
            }
        }

        return JqValue.fromString(builder.toString());
    }

    private static void tsvEscape(StringBuilder builder, String text) {
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            switch (c) {
                case '\\' -> builder.append("\\\\");
                case '\t' -> builder.append("\\t");
                case '\n' -> builder.append("\\n");
                case '\r' -> builder.append("\\r");
                default -> builder.append(c);
            }
        }
    }

    // @sh: space-separated words; strings in single quotes (' as '\''), other scalars as canonical text.
    private static Result shell(JqArray items) {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < items.count(); i++) {
            if (i > 0) {
                builder.append(' ');
            }
            JqValue item = items.get(i);
            switch (item.kind()) {
                case ARRAY, OBJECT -> {
                    return fail(Errors.cannotBeEscapedForShell(item));
                }
                case STRING -> builder.append('\'').append(item.asString().replace("'", "'\\''")).append('\'');
                default -> ValueText.write(builder, item);
            }
        }

        return JqValue.fromString(builder.toString());
    }

    // @base64: RFC 4648 of the UTF-8 bytes, with '=' padding.
    private static String base64(String text) {
        Utf8.Bytes bytes = new Utf8.Bytes();
        Utf8.encode(text, bytes);
        StringBuilder builder = new StringBuilder((bytes.count() + 2) / 3 * 4);
        for (int i = 0; i < bytes.count(); i += 3) {
            int left = bytes.count() - i;
            int group = bytes.get(i) << 16 | (left > 1 ? bytes.get(i + 1) << 8 : 0) | (left > 2 ? bytes.get(i + 2) : 0);
            builder.append(BASE64_ALPHABET.charAt(group >> 18));
            builder.append(BASE64_ALPHABET.charAt((group >> 12) & 0x3F));
            builder.append(left > 1 ? BASE64_ALPHABET.charAt((group >> 6) & 0x3F) : '=');
            builder.append(left > 2 ? BASE64_ALPHABET.charAt(group & 0x3F) : '=');
        }

        return builder.toString();
    }

    // @base64d: the characters before the first '=' must be in the alphabet; a final group of 2
    // or 3 characters gives 1 or 2 bytes, of 1 is an error; the bytes are decoded as UTF-8 with
    // replacement.
    private static Result base64Decode(String text) {
        Utf8.Bytes bytes = new Utf8.Bytes();
        int group = 0;
        int count = 0;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c == '=') {
                break;
            }
            int digit = base64Digit(c);
            if (digit < 0) {
                return fail(Errors.notValidBase64(JqValue.fromString(text)));
            }
            group = group << 6 | digit;
            if (++count == 4) {
                bytes.add((group >> 16) & 0xFF);
                bytes.add((group >> 8) & 0xFF);
                bytes.add(group & 0xFF);
                group = 0;
                count = 0;
            }
        }

        if (count == 1) {
            return fail(Errors.trailingBase64Byte(JqValue.fromString(text)));
        }
        if (count == 2) {
            bytes.add((group >> 4) & 0xFF);
        } else if (count == 3) {
            bytes.add((group >> 10) & 0xFF);
            bytes.add((group >> 2) & 0xFF);
        }

        return JqValue.fromString(Utf8.decodeReplacing(bytes));
    }

    private static int base64Digit(char c) {
        return c >= 'A' && c <= 'Z' ? c - 'A' : c >= 'a' && c <= 'z' ? c - 'a' + 26 : c >= '0' && c <= '9' ? c - '0' + 52 : c == '+' ? 62 : c == '/' ? 63 : -1;
    }
}
