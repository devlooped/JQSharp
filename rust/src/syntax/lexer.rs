//! Turns jqlite source text into tokens (spec §2), longest match first. Never panics: a lexical
//! error ends the list with a token of kind [`TokenKind::Error`] whose text is the message;
//! otherwise the list ends with [`TokenKind::Eof`].
//!
//! String interpolation is lexed with a stack, without recursion: `\(` ends the current string
//! piece (`StringStart` or `StringMiddle`) and pushes a frame; the lexer then produces the
//! ordinary tokens of the interpolated expression, counting the parentheses opened inside it; the
//! `)` that closes the frame resumes the string (`StringMiddle` or `StringEnd`). Nesting
//! (`"a\("b\("c")")"`) is any number of frames; the parser limits it (spec §11).
//!
//! Positions: lines are separated by LF (a CR before it is white space, a lone CR is white space
//! too, as in jq); columns count code points. The source is held as code points, so an index is a
//! code point index.

use std::borrow::Cow;

use crate::values::value_text;

use super::syntax_error::syntax_messages;
use super::token::{Token, TokenKind, keyword_kind};

// Punctuation, longest first where one is a prefix of another.
const PUNCTUATION: [(&str, TokenKind); 34] = [
    ("?//", TokenKind::QuestionAlt),
    ("//=", TokenKind::AltAssign),
    ("..", TokenKind::DotDot),
    ("|=", TokenKind::PipeAssign),
    ("==", TokenKind::Equal),
    ("!=", TokenKind::NotEqual),
    ("<=", TokenKind::LessEqual),
    (">=", TokenKind::GreaterEqual),
    ("+=", TokenKind::PlusAssign),
    ("-=", TokenKind::MinusAssign),
    ("*=", TokenKind::StarAssign),
    ("/=", TokenKind::SlashAssign),
    ("%=", TokenKind::PercentAssign),
    ("//", TokenKind::Alt),
    (".", TokenKind::Dot),
    ("[", TokenKind::LeftBracket),
    ("]", TokenKind::RightBracket),
    ("{", TokenKind::LeftBrace),
    ("}", TokenKind::RightBrace),
    ("(", TokenKind::LeftParen),
    (")", TokenKind::RightParen),
    ("|", TokenKind::Pipe),
    (",", TokenKind::Comma),
    (":", TokenKind::Colon),
    (";", TokenKind::Semicolon),
    ("?", TokenKind::Question),
    ("=", TokenKind::Assign),
    ("<", TokenKind::Less),
    (">", TokenKind::Greater),
    ("+", TokenKind::Plus),
    ("-", TokenKind::Minus),
    ("*", TokenKind::Star),
    ("/", TokenKind::Slash),
    ("%", TokenKind::Percent),
];

// One frame per open interpolation: the parentheses opened inside it, and the position of the
// opening quote of its string (for "Unterminated string").
struct Interpolation {
    parens: u32,
    line: u32,
    column: u32,
}

pub struct Lexer {
    source: Vec<char>,
    tokens: Vec<Token>,
    interpolations: Vec<Interpolation>,
    index: usize,
    line: u32,
    column: u32,
}

impl Lexer {
    /// The tokens of `source`, ending with an Eof or Error token.
    pub fn tokenize(source: &str) -> Vec<Token> {
        // Room for every code point, and for the tokens of a source written as most are.
        let mut code_points = Vec::with_capacity(source.len());
        code_points.extend(source.chars());
        let tokens = Vec::with_capacity(source.len() / 3 + 2);
        let mut lexer = Lexer { source: code_points, tokens, interpolations: Vec::new(), index: 0, line: 1, column: 1 };
        while lexer.next() {}
        lexer.tokens
    }

    // Adds the next token; false after the last one (Eof or Error).
    fn next(&mut self) -> bool {
        self.skip_trivia();
        let start_line = self.line;
        let start_column = self.column;
        if self.index >= self.source.len() {
            if let Some(frame) = self.interpolations.last() {
                let (line, column) = (frame.line, frame.column);
                return self.fail(syntax_messages::UNTERMINATED_STRING.to_owned(), line, column);
            }

            self.add(TokenKind::Eof, String::new(), start_line, start_column);
            return false;
        }

        let c = self.source[self.index];
        if c == '"' {
            self.advance();
            return self.lex_string_piece(start_line, start_column, start_line, start_column, true);
        }

        if is_digit(c) || (c == '.' && is_digit(self.at(self.index + 1))) {
            return self.lex_number(start_line, start_column);
        }

        if is_ident_start(c) {
            let name = self.read_identifier(true);
            let kind = if name.contains(':') { TokenKind::Ident } else { keyword_kind(&name) };
            self.add(kind, name, start_line, start_column);
            return true;
        }

        if c == '.' && is_ident_start(self.at(self.index + 1)) {
            self.advance();
            let name = self.read_identifier(false);
            self.add(TokenKind::Field, name, start_line, start_column);
            return true;
        }

        if c == '$' {
            self.advance();
            if self.index >= self.source.len() || !is_ident_start(self.source[self.index]) {
                return self.fail(syntax_messages::VARIABLE_NAME_EXPECTED.to_owned(), start_line, start_column);
            }
            let name = self.read_identifier(true);
            self.add(TokenKind::Variable, name, start_line, start_column);
            return true;
        }

        if c == '@' {
            self.advance();
            let start = self.index;
            while is_ident_part(self.at(self.index)) {
                self.advance();
            }
            if self.index == start {
                return self.fail(syntax_messages::FORMAT_NAME_EXPECTED.to_owned(), start_line, start_column);
            }
            let name = self.text(start, self.index);
            self.add(TokenKind::Format, name, start_line, start_column);
            return true;
        }

        if c == '(' {
            if let Some(frame) = self.interpolations.last_mut() {
                frame.parens += 1;
            }
        }

        if c == ')' {
            if let Some(frame) = self.interpolations.last_mut() {
                if frame.parens == 0 {
                    // The ')' that closes "\(": the string resumes.
                    let (line, column) = (frame.line, frame.column);
                    self.interpolations.pop();
                    self.advance();
                    return self.lex_string_piece(start_line, start_column, line, column, false);
                }

                frame.parens -= 1;
            }
        }

        for (text, kind) in PUNCTUATION {
            if self.matches(text) {
                for _ in 0..text.len() {
                    self.advance();
                }
                self.add(kind, text, start_line, start_column);
                return true;
            }
        }

        self.fail(syntax_messages::invalid_character(c), start_line, start_column)
    }

    // Whether the (ASCII) text is at the current index.
    fn matches(&self, text: &str) -> bool {
        text.chars().enumerate().all(|(i, c)| self.at(self.index + i) == c && self.index + i < self.source.len())
    }

    // White space (space, tab, CR, LF) and comments. A comment runs to the end of the line; when an
    // odd number of '\' ends the line (before LF or CR LF), the comment goes on with the next line.
    fn skip_trivia(&mut self) {
        while self.index < self.source.len() {
            let c = self.source[self.index];
            if c == ' ' || c == '\t' || c == '\r' || c == '\n' {
                self.advance();
                continue;
            }

            if c != '#' {
                return;
            }

            let start = self.index;
            while self.index < self.source.len() {
                if self.source[self.index] == '\n' {
                    let mut j = self.index - 1;
                    if j > start && self.source[j] == '\r' {
                        j -= 1;
                    }
                    let mut backslashes = 0;
                    while j > start && self.source[j] == '\\' {
                        backslashes += 1;
                        j -= 1;
                    }
                    if backslashes % 2 == 0 {
                        break;
                    }
                }

                self.advance();
            }
        }
    }

    // A string piece: after the opening quote, or after the ')' that closes an interpolation. Ends
    // at the closing quote (String or StringEnd) or at "\(" (StringStart or StringMiddle).
    fn lex_string_piece(&mut self, token_line: u32, token_column: u32, quote_line: u32, quote_column: u32, first: bool) -> bool {
        let mut text = String::new();
        loop {
            if self.index >= self.source.len() {
                return self.fail(syntax_messages::UNTERMINATED_STRING.to_owned(), quote_line, quote_column);
            }

            let c = self.source[self.index];
            if c == '"' {
                self.advance();
                self.add(if first { TokenKind::String } else { TokenKind::StringEnd }, text, token_line, token_column);
                return true;
            }

            if c != '\\' {
                text.push(c);
                self.advance();
                continue;
            }

            let escape_line = self.line;
            let escape_column = self.column;
            self.advance();
            let e = self.at(self.index);
            match e {
                '"' | '\\' | '/' => text.push(e),
                'b' => text.push('\u{8}'),
                'f' => text.push('\u{c}'),
                'n' => text.push('\n'),
                'r' => text.push('\r'),
                't' => text.push('\t'),
                'u' => {
                    let Some(unit) = self.hex_at(self.index + 1) else {
                        return self.fail(syntax_messages::INVALID_ESCAPE.to_owned(), escape_line, escape_column);
                    };
                    for _ in 0..5 {
                        self.advance();
                    }
                    let low = if self.at(self.index) == '\\' && self.at(self.index + 1) == 'u' { self.hex_at(self.index + 2) } else { None };
                    match low {
                        Some(low) if (0xD800..=0xDBFF).contains(&unit) && (0xDC00..=0xDFFF).contains(&low) => {
                            // A high surrogate escape followed by a low one: one code point.
                            text.push(char::from_u32(0x10000 + ((unit - 0xD800) << 10) + (low - 0xDC00)).unwrap_or('\u{FFFD}'));
                            for _ in 0..6 {
                                self.advance();
                            }
                        }
                        // Any other surrogate escape is U+FFFD.
                        _ => text.push(char::from_u32(unit).unwrap_or('\u{FFFD}')),
                    }

                    continue;
                }
                '(' if self.index < self.source.len() => {
                    self.advance();
                    self.add(if first { TokenKind::StringStart } else { TokenKind::StringMiddle }, text, token_line, token_column);
                    self.interpolations.push(Interpolation { parens: 0, line: quote_line, column: quote_column });
                    return true;
                }
                _ => {
                    return if self.index >= self.source.len() {
                        self.fail(syntax_messages::UNTERMINATED_STRING.to_owned(), quote_line, quote_column)
                    } else {
                        self.fail(syntax_messages::INVALID_ESCAPE.to_owned(), escape_line, escape_column)
                    };
                }
            }

            self.advance();
        }
    }

    // ([0-9]+ ('.' [0-9]*)? | '.' [0-9]+) ([eE] [+-]? [0-9]+)?
    fn lex_number(&mut self, start_line: u32, start_column: u32) -> bool {
        let start = self.index;
        while is_digit(self.at(self.index)) {
            self.advance();
        }
        if self.at(self.index) == '.' {
            self.advance();
            while is_digit(self.at(self.index)) {
                self.advance();
            }
        }

        if matches!(self.at(self.index), 'e' | 'E') {
            let digit = if matches!(self.at(self.index + 1), '+' | '-') { self.index + 2 } else { self.index + 1 };
            if is_digit(self.at(digit)) {
                while self.index < digit {
                    self.advance();
                }
                while is_digit(self.at(self.index)) {
                    self.advance();
                }
            }
        }

        let text = self.text(start, self.index);
        // The grammar above is a subset of the numeric text of value_text, so the conversion succeeds.
        let number = value_text::try_parse_number(&text).unwrap_or(0.0);
        self.tokens.push(Token { kind: TokenKind::Number, text: text.into(), number, line: start_line, column: start_column });
        true
    }

    // [A-Za-z_][A-Za-z0-9_]*, and when qualified, any number of "::" each followed by such a name
    // (jq 1.8.2 accepts a::b::c as one name: an import alias may itself be qualified).
    fn read_identifier(&mut self, qualified: bool) -> String {
        let start = self.index;
        while is_ident_part(self.at(self.index)) {
            self.advance();
        }
        while qualified && self.at(self.index) == ':' && self.at(self.index + 1) == ':' && is_ident_start(self.at(self.index + 2)) {
            self.advance();
            self.advance();
            while is_ident_part(self.at(self.index)) {
                self.advance();
            }
        }

        self.text(start, self.index)
    }

    // The four hex digits at offset, as a number.
    fn hex_at(&self, offset: usize) -> Option<u32> {
        let mut value = 0;
        for i in 0..4 {
            value = value * 16 + self.at(offset + i).to_digit(16)?;
        }

        Some(value)
    }

    // Moves past one code point, keeping line and column.
    fn advance(&mut self) {
        let c = self.source[self.index];
        self.index += 1;
        if c == '\n' {
            self.line += 1;
            self.column = 1;
        } else {
            self.column += 1;
        }
    }

    fn at(&self, offset: usize) -> char {
        if offset < self.source.len() { self.source[offset] } else { '\0' }
    }

    fn text(&self, start: usize, end: usize) -> String {
        self.source[start..end].iter().collect()
    }

    fn add(&mut self, kind: TokenKind, text: impl Into<Cow<'static, str>>, line: u32, column: u32) {
        self.tokens.push(Token { kind, text: text.into(), number: 0.0, line, column });
    }

    fn fail(&mut self, message: String, line: u32, column: u32) -> bool {
        self.add(TokenKind::Error, message, line, column);
        false
    }
}

fn is_digit(c: char) -> bool {
    c.is_ascii_digit()
}

fn is_ident_start(c: char) -> bool {
    c.is_ascii_alphabetic() || c == '_'
}

fn is_ident_part(c: char) -> bool {
    is_ident_start(c) || is_digit(c)
}
