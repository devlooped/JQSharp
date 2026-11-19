//! Tokens (spec §2).

use std::borrow::Cow;

/// The kinds of tokens of jqlite (spec §2).
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum TokenKind {
    /// The end of the source.
    Eof,
    /// A lexical error; the token's text is the message. Always the last token.
    Error,

    /// `name` or `module::name`; the text is the name as written.
    Ident,
    /// `.name`; the text is the name without the dot.
    Field,
    /// `$name` or `$module::name`; the text is the name without `$`.
    Variable,
    /// `@name`; the text is the name without `@`.
    Format,
    /// A number literal; `number` is its value, the text its source text.
    Number,

    // String literals. Text is the decoded literal text of the piece. A string without
    // interpolation is one String token; "a\(x)b\(y)c" is StringStart("a"), the tokens of x,
    // StringMiddle("b"), the tokens of y, StringEnd("c"). StringMiddle and StringEnd are at the
    // ')' that closes the interpolation.
    String,
    StringStart,
    StringMiddle,
    StringEnd,

    // Keywords (the list is KEYWORDS).
    Def,
    As,
    If,
    Then,
    Elif,
    Else,
    End,
    Reduce,
    Foreach,
    Try,
    Catch,
    Label,
    Break,
    Import,
    Include,
    Module,
    And,
    Or,

    // Punctuation.
    Dot,
    DotDot,
    LeftBracket,
    RightBracket,
    LeftBrace,
    RightBrace,
    LeftParen,
    RightParen,
    Pipe,
    Comma,
    Colon,
    Semicolon,
    Question,
    QuestionAlt,
    Assign,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    Plus,
    Minus,
    Star,
    Slash,
    Percent,
    PlusAssign,
    MinusAssign,
    StarAssign,
    SlashAssign,
    PercentAssign,
    Alt,
    AltAssign,
    PipeAssign,
}

/// A token with the position of its first code point (1-based line and column; columns count
/// code points).
#[derive(Clone, Debug, PartialEq)]
pub struct Token {
    pub kind: TokenKind,
    /// The text of the token. Rust: punctuation borrows its text from the lexer's table, so that
    /// only names, strings and messages allocate.
    pub text: Cow<'static, str>,
    pub number: f64,
    pub line: u32,
    pub column: u32,
}

/// The keywords of jqlite: the only place they are listed.
pub const KEYWORDS: [(&str, TokenKind); 18] = [
    ("def", TokenKind::Def),
    ("as", TokenKind::As),
    ("if", TokenKind::If),
    ("then", TokenKind::Then),
    ("elif", TokenKind::Elif),
    ("else", TokenKind::Else),
    ("end", TokenKind::End),
    ("reduce", TokenKind::Reduce),
    ("foreach", TokenKind::Foreach),
    ("try", TokenKind::Try),
    ("catch", TokenKind::Catch),
    ("label", TokenKind::Label),
    ("break", TokenKind::Break),
    ("import", TokenKind::Import),
    ("include", TokenKind::Include),
    ("module", TokenKind::Module),
    ("and", TokenKind::And),
    ("or", TokenKind::Or),
];

/// The keyword kind of `word`, or [`TokenKind::Ident`].
pub fn keyword_kind(word: &str) -> TokenKind {
    for (keyword, kind) in KEYWORDS {
        if keyword == word {
            return kind;
        }
    }

    TokenKind::Ident
}

pub fn is_keyword(kind: TokenKind) -> bool {
    kind >= TokenKind::Def && kind <= TokenKind::Or
}

impl Token {
    /// The token as the parser's messages show it: `'|='`, `end of program`, `'.name'`.
    pub fn describe(&self) -> String {
        match self.kind {
            TokenKind::Eof => "end of program".to_owned(),
            TokenKind::Error => self.text.to_string(),
            TokenKind::Field => format!("'.{}'", self.text),
            TokenKind::Variable => format!("'${}'", self.text),
            TokenKind::Format => format!("'@{}'", self.text),
            TokenKind::String | TokenKind::StringStart => "string".to_owned(),
            TokenKind::StringMiddle | TokenKind::StringEnd => "')'".to_owned(),
            _ => format!("'{}'", self.text),
        }
    }
}
