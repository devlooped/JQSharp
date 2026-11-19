import * as CodePoints from "../values/codePoints.js";
import * as ValueText from "../values/valueText.js";
import * as SyntaxMessages from "./syntaxError.js";
import { Token, TokenKind, keywordKind } from "./token.js";

// Punctuation, longest first where one is a prefix of another.
const Punctuation: readonly (readonly [text: string, kind: TokenKind])[] = [
  ["?//", TokenKind.QuestionAlt],
  ["//=", TokenKind.AltAssign],
  ["..", TokenKind.DotDot],
  ["|=", TokenKind.PipeAssign],
  ["==", TokenKind.Equal],
  ["!=", TokenKind.NotEqual],
  ["<=", TokenKind.LessEqual],
  [">=", TokenKind.GreaterEqual],
  ["+=", TokenKind.PlusAssign],
  ["-=", TokenKind.MinusAssign],
  ["*=", TokenKind.StarAssign],
  ["/=", TokenKind.SlashAssign],
  ["%=", TokenKind.PercentAssign],
  ["//", TokenKind.Alt],
  [".", TokenKind.Dot],
  ["[", TokenKind.LeftBracket],
  ["]", TokenKind.RightBracket],
  ["{", TokenKind.LeftBrace],
  ["}", TokenKind.RightBrace],
  ["(", TokenKind.LeftParen],
  [")", TokenKind.RightParen],
  ["|", TokenKind.Pipe],
  [",", TokenKind.Comma],
  [":", TokenKind.Colon],
  [";", TokenKind.Semicolon],
  ["?", TokenKind.Question],
  ["=", TokenKind.Assign],
  ["<", TokenKind.Less],
  [">", TokenKind.Greater],
  ["+", TokenKind.Plus],
  ["-", TokenKind.Minus],
  ["*", TokenKind.Star],
  ["/", TokenKind.Slash],
  ["%", TokenKind.Percent],
];

// One frame per open interpolation: the parentheses opened inside it, and the position of the
// opening quote of its string (for "Unterminated string").
interface Interpolation {
  parens: number;
  readonly line: number;
  readonly column: number;
}

const Quote = 0x22;
const Backslash = 0x5c;
const LineFeed = 0x0a;
const CarriageReturn = 0x0d;
const Dot = 0x2e;
const Colon = 0x3a;

/**
 * Turns jqlite source text into tokens (spec §2), longest match first. Never throws: a lexical
 * error ends the list with a token of kind `TokenKind.Error` whose text is the message;
 * otherwise the list ends with `TokenKind.Eof`.
 *
 * String interpolation is lexed with a stack, without recursion: `\(` ends the current string
 * piece (`StringStart` or `StringMiddle`) and pushes a frame; the lexer then produces the
 * ordinary tokens of the interpolated expression, counting the parentheses opened inside it;
 * the `)` that closes the frame resumes the string (`StringMiddle` or `StringEnd`). Nesting
 * (`"a\("b\("c")")"`) is any number of frames; the parser limits it (spec §11).
 *
 * Positions: lines are separated by LF (a CR before it is white space, a lone CR is white space
 * too, as in jq); columns count code points.
 */
export class Lexer {
  private readonly tokens: Token[] = [];
  private readonly interpolations: Interpolation[] = [];
  private index = 0;
  private line = 1;
  private column = 1;

  private constructor(private readonly source: string) {}

  /** The tokens of `source`, ending with an Eof or Error token. */
  static tokenize(source: string): Token[] {
    const lexer = new Lexer(source);
    while (lexer.next()) {
      // one token per call
    }

    return lexer.tokens;
  }

  // Adds the next token; false after the last one (Eof or Error).
  private next(): boolean {
    this.skipTrivia();
    const source = this.source;
    const startLine = this.line;
    const startColumn = this.column;
    const interpolations = this.interpolations;
    if (this.index >= source.length) {
      if (interpolations.length > 0) {
        const open = interpolations[interpolations.length - 1]!;
        return this.fail(SyntaxMessages.UnterminatedString, open.line, open.column);
      }

      this.add(TokenKind.Eof, "", startLine, startColumn);
      return false;
    }

    const c = source.charCodeAt(this.index);
    if (c === Quote) {
      this.advance();
      return this.lexStringPiece(startLine, startColumn, startLine, startColumn, true);
    }

    if (isDigit(c) || (c === Dot && isDigit(this.at(this.index + 1)))) return this.lexNumber(startLine, startColumn);

    if (isIdentStart(c)) {
      const name = this.readIdentifier(true);
      const kind = name.includes(":") ? TokenKind.Ident : keywordKind(name);
      this.add(kind, name, startLine, startColumn);
      return true;
    }

    if (c === Dot && isIdentStart(this.at(this.index + 1))) {
      this.advance();
      this.add(TokenKind.Field, this.readIdentifier(false), startLine, startColumn);
      return true;
    }

    if (c === 0x24 /* $ */) {
      this.advance();
      if (this.index >= source.length || !isIdentStart(source.charCodeAt(this.index))) return this.fail(SyntaxMessages.VariableNameExpected, startLine, startColumn);
      this.add(TokenKind.Variable, this.readIdentifier(true), startLine, startColumn);
      return true;
    }

    if (c === 0x40 /* @ */) {
      this.advance();
      const start = this.index;
      while (isIdentPart(this.at(this.index))) this.advance();
      if (this.index === start) return this.fail(SyntaxMessages.FormatNameExpected, startLine, startColumn);
      this.add(TokenKind.Format, source.substring(start, this.index), startLine, startColumn);
      return true;
    }

    if (c === 0x28 /* ( */ && interpolations.length > 0) interpolations[interpolations.length - 1]!.parens++;

    if (c === 0x29 /* ) */ && interpolations.length > 0) {
      const frame = interpolations[interpolations.length - 1]!;
      if (frame.parens === 0) {
        // The ')' that closes "\(": the string resumes.
        interpolations.pop();
        this.advance();
        return this.lexStringPiece(startLine, startColumn, frame.line, frame.column, false);
      }

      frame.parens--;
    }

    for (const [text, kind] of Punctuation) {
      if (source.startsWith(text, this.index)) {
        for (let i = 0; i < text.length; i++) this.advance();
        this.add(kind, text, startLine, startColumn);
        return true;
      }
    }

    return this.fail(SyntaxMessages.invalidCharacter(this.codePointAt(this.index)), startLine, startColumn);
  }

  // White space (space, tab, CR, LF) and comments. A comment runs to the end of the line; when an
  // odd number of '\' ends the line (before LF or CR LF), the comment goes on with the next line.
  private skipTrivia(): void {
    const source = this.source;
    while (this.index < source.length) {
      const c = source.charCodeAt(this.index);
      if (c === 0x20 || c === 0x09 || c === CarriageReturn || c === LineFeed) {
        this.advance();
        continue;
      }

      if (c !== 0x23 /* # */) return;

      const start = this.index;
      while (this.index < source.length) {
        if (source.charCodeAt(this.index) === LineFeed) {
          let j = this.index - 1;
          if (j > start && source.charCodeAt(j) === CarriageReturn) j--;
          let backslashes = 0;
          for (; j > start && source.charCodeAt(j) === Backslash; j--) backslashes++;
          if (backslashes % 2 === 0) break;
        }

        this.advance();
      }
    }
  }

  // A string piece: after the opening quote, or after the ')' that closes an interpolation. Ends
  // at the closing quote (String or StringEnd) or at "\(" (StringStart or StringMiddle).
  private lexStringPiece(tokenLine: number, tokenColumn: number, quoteLine: number, quoteColumn: number, first: boolean): boolean {
    const source = this.source;
    let text = "";
    while (true) {
      if (this.index >= source.length) return this.fail(SyntaxMessages.UnterminatedString, quoteLine, quoteColumn);

      const c = source.charCodeAt(this.index);
      if (c === Quote) {
        this.advance();
        this.add(first ? TokenKind.String : TokenKind.StringEnd, text, tokenLine, tokenColumn);
        return true;
      }

      if (c !== Backslash) {
        text += CodePoints.toText(this.codePointAt(this.index));
        this.advance();
        continue;
      }

      const escapeLine = this.line;
      const escapeColumn = this.column;
      this.advance();
      const e = this.at(this.index);
      switch (e) {
        case Quote:
        case Backslash:
        case 0x2f /* / */:
          text += String.fromCharCode(e);
          break;
        case 0x62 /* b */:
          text += "\b";
          break;
        case 0x66 /* f */:
          text += "\f";
          break;
        case 0x6e /* n */:
          text += "\n";
          break;
        case 0x72 /* r */:
          text += "\r";
          break;
        case 0x74 /* t */:
          text += "\t";
          break;
        case 0x75 /* u */: {
          const unit = this.hexAt(this.index + 1);
          if (unit < 0) return this.fail(SyntaxMessages.InvalidEscape, escapeLine, escapeColumn);
          for (let i = 0; i < 5; i++) this.advance();
          const low = this.at(this.index) === Backslash && this.at(this.index + 1) === 0x75 ? this.hexAt(this.index + 2) : -1;
          if (unit >= 0xd800 && unit <= 0xdbff && low >= 0xdc00 && low <= 0xdfff) {
            // A high surrogate escape followed by a low one: one code point.
            text += CodePoints.toText(0x10000 + ((unit - 0xd800) << 10) + (low - 0xdc00));
            for (let i = 0; i < 6; i++) this.advance();
          } else {
            // Any other surrogate escape is U+FFFD.
            text += CodePoints.toText(unit >= 0xd800 && unit <= 0xdfff ? CodePoints.Replacement : unit);
          }

          continue;
        }
        case 0x28 /* ( */:
          this.advance();
          this.add(first ? TokenKind.StringStart : TokenKind.StringMiddle, text, tokenLine, tokenColumn);
          this.interpolations.push({ parens: 0, line: quoteLine, column: quoteColumn });
          return true;
        default:
          return this.index >= source.length
            ? this.fail(SyntaxMessages.UnterminatedString, quoteLine, quoteColumn)
            : this.fail(SyntaxMessages.InvalidEscape, escapeLine, escapeColumn);
      }

      this.advance();
    }
  }

  // ([0-9]+ ('.' [0-9]*)? | '.' [0-9]+) ([eE] [+-]? [0-9]+)?
  private lexNumber(startLine: number, startColumn: number): boolean {
    const start = this.index;
    while (isDigit(this.at(this.index))) this.advance();
    if (this.at(this.index) === Dot) {
      this.advance();
      while (isDigit(this.at(this.index))) this.advance();
    }

    const e = this.at(this.index);
    if (e === 0x65 || e === 0x45) {
      const sign = this.at(this.index + 1);
      const digit = sign === 0x2b || sign === 0x2d ? this.index + 2 : this.index + 1;
      if (isDigit(this.at(digit))) {
        while (this.index < digit) this.advance();
        while (isDigit(this.at(this.index))) this.advance();
      }
    }

    const text = this.source.substring(start, this.index);
    // The grammar above is a subset of the numeric text of ValueText, so the conversion succeeds.
    const number = ValueText.tryParseNumber(text) ?? 0;
    this.tokens.push(new Token(TokenKind.Number, text, number, startLine, startColumn));
    return true;
  }

  // [A-Za-z_][A-Za-z0-9_]*, and when qualified, any number of "::" each followed by such a name
  // (jq 1.8.2 accepts a::b::c as one name: an import alias may itself be qualified).
  private readIdentifier(qualified: boolean): string {
    const start = this.index;
    while (isIdentPart(this.at(this.index))) this.advance();
    while (qualified && this.at(this.index) === Colon && this.at(this.index + 1) === Colon && isIdentStart(this.at(this.index + 2))) {
      this.advance();
      this.advance();
      while (isIdentPart(this.at(this.index))) this.advance();
    }

    return this.source.substring(start, this.index);
  }

  // The four hex digits at offset, as a number, or -1.
  private hexAt(offset: number): number {
    let value = 0;
    for (let i = 0; i < 4; i++) {
      const c = this.at(offset + i);
      const digit = c >= 0x30 && c <= 0x39 ? c - 0x30 : c >= 0x61 && c <= 0x66 ? c - 0x61 + 10 : c >= 0x41 && c <= 0x46 ? c - 0x41 + 10 : -1;
      if (digit < 0) return -1;
      value = value * 16 + digit;
    }

    return value;
  }

  // The code point at offset: a surrogate pair is one code point, a lone surrogate is U+FFFD.
  private codePointAt(offset: number): number {
    const c = this.source.codePointAt(offset)!;
    return c >= 0xd800 && c <= 0xdfff ? CodePoints.Replacement : c;
  }

  // Moves past one code point, keeping line and column.
  private advance(): void {
    const source = this.source;
    const c = source.charCodeAt(this.index);
    this.index += CodePoints.isHighSurrogate(c) && this.index + 1 < source.length && CodePoints.isLowSurrogate(source.charCodeAt(this.index + 1)) ? 2 : 1;
    if (c === LineFeed) {
      this.line++;
      this.column = 1;
    } else {
      this.column++;
    }
  }

  // The UTF-16 unit at offset, or 0 past the end.
  private at(offset: number): number {
    return offset < this.source.length ? this.source.charCodeAt(offset) : 0;
  }

  private add(kind: TokenKind, text: string, tokenLine: number, tokenColumn: number): void {
    this.tokens.push(new Token(kind, text, 0, tokenLine, tokenColumn));
  }

  private fail(message: string, errorLine: number, errorColumn: number): boolean {
    this.add(TokenKind.Error, message, errorLine, errorColumn);
    return false;
  }
}

function isDigit(c: number): boolean {
  return c >= 0x30 && c <= 0x39;
}

function isIdentStart(c: number): boolean {
  return (c >= 0x61 && c <= 0x7a) || (c >= 0x41 && c <= 0x5a) || c === 0x5f;
}

function isIdentPart(c: number): boolean {
  return isIdentStart(c) || isDigit(c);
}
