/** The kinds of tokens of jqlite (spec §2). */
export const enum TokenKind {
  /** The end of the source. */
  Eof,
  /** A lexical error; the token's text is the message. Always the last token. */
  Error,

  /** `name` or `module::name`; the text is the name as written. */
  Ident,
  /** `.name`; the text is the name without the dot. */
  Field,
  /** `$name` or `$module::name`; the text is the name without `$`. */
  Variable,
  /** `@name`; the text is the name without `@`. */
  Format,
  /** A number literal; `number` is its value, the text its source text. */
  Number,

  // String literals. The text is the decoded literal text of the piece. A string without
  // interpolation is one String token; "a\(x)b\(y)c" is StringStart("a"), the tokens of x,
  // StringMiddle("b"), the tokens of y, StringEnd("c"). StringMiddle and StringEnd are at the ')'
  // that closes the interpolation.
  String,
  StringStart,
  StringMiddle,
  StringEnd,

  // Keywords (the list is Keywords).
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

/** The keywords of jqlite: the only place they are listed. */
export const Keywords: readonly (readonly [word: string, kind: TokenKind])[] = [
  ["def", TokenKind.Def],
  ["as", TokenKind.As],
  ["if", TokenKind.If],
  ["then", TokenKind.Then],
  ["elif", TokenKind.Elif],
  ["else", TokenKind.Else],
  ["end", TokenKind.End],
  ["reduce", TokenKind.Reduce],
  ["foreach", TokenKind.Foreach],
  ["try", TokenKind.Try],
  ["catch", TokenKind.Catch],
  ["label", TokenKind.Label],
  ["break", TokenKind.Break],
  ["import", TokenKind.Import],
  ["include", TokenKind.Include],
  ["module", TokenKind.Module],
  ["and", TokenKind.And],
  ["or", TokenKind.Or],
];

/** The keyword kind of `word`, or `TokenKind.Ident`. */
export function keywordKind(word: string): TokenKind {
  for (const [keyword, kind] of Keywords) {
    if (keyword === word) return kind;
  }

  return TokenKind.Ident;
}

export function isKeyword(kind: TokenKind): boolean {
  return kind >= TokenKind.Def && kind <= TokenKind.Or;
}

/** A token with the position of its first code point (1-based line and column; columns count code points). */
export class Token {
  constructor(
    readonly kind: TokenKind,
    readonly text: string,
    readonly number: number,
    readonly line: number,
    readonly column: number,
  ) {}

  /** The token as the parser's messages show it: `'|='`, `end of program`, `'.name'`. */
  describe(): string {
    switch (this.kind) {
      case TokenKind.Eof:
        return "end of program";
      case TokenKind.Error:
        return this.text;
      case TokenKind.Field:
        return `'.${this.text}'`;
      case TokenKind.Variable:
        return `'$${this.text}'`;
      case TokenKind.Format:
        return `'@${this.text}'`;
      case TokenKind.String:
      case TokenKind.StringStart:
        return "string";
      case TokenKind.StringMiddle:
      case TokenKind.StringEnd:
        return "')'";
      default:
        return `'${this.text}'`;
    }
  }
}
