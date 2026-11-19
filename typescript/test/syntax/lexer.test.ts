// The port of dotnet/Tests/Core/Syntax/LexerTests.cs.

import assert from "node:assert/strict";
import { test } from "node:test";
import { Lexer } from "../../src/syntax/lexer.js";
import { Keywords, type Token, TokenKind, isKeyword } from "../../src/syntax/token.js";

// The names of the token kinds, in the order of the enumeration.
const KindNames = (
  "Eof Error Ident Field Variable Format Number String StringStart StringMiddle StringEnd " +
  "Def As If Then Elif Else End Reduce Foreach Try Catch Label Break Import Include Module And Or " +
  "Dot DotDot LeftBracket RightBracket LeftBrace RightBrace LeftParen RightParen Pipe Comma Colon Semicolon Question QuestionAlt " +
  "Assign Equal NotEqual Less LessEqual Greater GreaterEqual Plus Minus Star Slash Percent " +
  "PlusAssign MinusAssign StarAssign SlashAssign PercentAssign Alt AltAssign PipeAssign"
).split(" ");

function lex(source: string): Token[] {
  return Lexer.tokenize(source);
}

function tokens(source: string): Token[] {
  return lex(source).filter((t) => t.kind !== TokenKind.Eof);
}

// Kind, text and position of each token, without the final Eof.
function describe(source: string): string {
  return tokens(source)
    .map((t) => `${KindNames[t.kind]}:${t.text}@${t.line}:${t.column}`)
    .join(" ");
}

function kinds(source: string): string {
  return tokens(source)
    .map((t) => KindNames[t.kind])
    .join(" ");
}

function texts(source: string): string {
  return tokens(source)
    .map((t) => t.text)
    .join(" ");
}

function error(source: string): string {
  const all = lex(source);
  const last = all[all.length - 1]!;
  assert.equal(last.kind, TokenKind.Error);
  return `${last.text} @${last.line}:${last.column}`;
}

function single(source: string): Token {
  const all = tokens(source);
  assert.equal(all.length, 1, source);
  return all[0]!;
}

test("the names of the kinds follow the enumeration", () => {
  assert.equal(KindNames[TokenKind.Eof], "Eof");
  assert.equal(KindNames[TokenKind.Def], "Def");
  assert.equal(KindNames[TokenKind.Or], "Or");
  assert.equal(KindNames[TokenKind.Dot], "Dot");
  assert.equal(KindNames[TokenKind.PipeAssign], "PipeAssign");
  assert.equal(KindNames.length, TokenKind.PipeAssign + 1);
});

test("every punctuation token, longest first", () => {
  const source = ". .. [ ] { } ( ) | , : ; ? ?// = == != < <= > >= + - * / % += -= *= /= %= // //= |=";
  assert.equal(
    kinds(source),
    "Dot DotDot LeftBracket RightBracket LeftBrace RightBrace LeftParen RightParen Pipe Comma Colon Semicolon Question QuestionAlt " +
      "Assign Equal NotEqual Less LessEqual Greater GreaterEqual Plus Minus Star Slash Percent " +
      "PlusAssign MinusAssign StarAssign SlashAssign PercentAssign Alt AltAssign PipeAssign",
  );
  assert.equal(texts(source), source);
});

test("adjacent punctuation takes the longest match", () => {
  assert.equal(kinds("?//="), "QuestionAlt Assign");
  assert.equal(kinds("..."), "DotDot Dot");
  assert.equal(kinds("///"), "Alt Slash");
  assert.equal(kinds("|=="), "PipeAssign Assign");
});

test("every keyword", () => {
  for (const [word, kind] of Keywords) {
    const token = single(word);
    assert.equal(token.kind, kind);
    assert.equal(token.text, word);
    assert.ok(isKeyword(token.kind));
  }

  assert.equal(Keywords.length, 18);
});

test("names", () => {
  assert.equal(describe("foo a::b _x1 null not"), "Ident:foo@1:1 Ident:a::b@1:5 Ident:_x1@1:10 Ident:null@1:14 Ident:not@1:19");
  assert.equal(describe(".a.end.e0._"), "Field:a@1:1 Field:end@1:3 Field:e0@1:7 Field:_@1:10");
  assert.equal(describe("$x $then $a::b $__loc__"), "Variable:x@1:1 Variable:then@1:4 Variable:a::b@1:10 Variable:__loc__@1:16");
  assert.equal(describe("@base64 @base64d @1x"), "Format:base64@1:1 Format:base64d@1:9 Format:1x@1:18");
});

test("a qualified name may have several double colons", () => {
  assert.equal(kinds("a::b::c"), "Ident");
  assert.equal(texts("a::b::c"), "a::b::c");
  assert.equal(kinds("$a::b::c"), "Variable");
  assert.equal(texts("a::"), "a : :");
  assert.equal(kinds(".a::b"), "Field Colon Colon Ident");
});

test("numbers", () => {
  const cases: [string, number][] = [
    ["0", 0],
    ["42", 42],
    [".5", 0.5],
    ["1.", 1],
    ["1.5", 1.5],
    ["1e3", 1000],
    ["1E+3", 1000],
    ["1.e3", 1000],
    [".5e1", 5],
    ["25e-1", 2.5],
    ["0.1", 0.1],
    ["1e1000", Infinity],
    ["1e-1000", 0],
    ["100000000000000000000000000001", 1e29],
  ];
  for (const [source, value] of cases) {
    const token = single(source);
    assert.equal(token.kind, TokenKind.Number);
    assert.equal(token.number, value);
    assert.equal(token.text, source);
  }
});

test("number boundaries", () => {
  assert.equal(kinds(".e0"), "Field");
  assert.equal(kinds("1e"), "Number Ident");
  assert.equal(kinds("1e+"), "Number Ident Plus");
  assert.equal(texts("1.5.3"), "1.5 .3");
  assert.equal(kinds("..5"), "DotDot Number");
  assert.equal(kinds("1x"), "Number Ident");
});

test("positions across line breaks and non-BMP characters", () => {
  // "😀😀" is 4 code points; columns count code points, not UTF-16 units.
  assert.equal(describe('"😀😀" +\n  1\r\n+ .a'), "String:😀😀@1:1 Plus:+@1:6 Number:1@2:3 Plus:+@3:1 Field:a@3:3");
  assert.equal(describe('"a\nb" x'), "String:a\nb@1:1 Ident:x@2:4");
});

test("a lone CR is white space, not a line break", () => {
  assert.equal(describe("1\r+\r2"), "Number:1@1:1 Plus:+@1:3 Number:2@1:5");
});

test("comments", () => {
  assert.equal(describe("1 # a comment\n+ 2 # another"), "Number:1@1:1 Plus:+@2:1 Number:2@2:3");
  // An odd number of backslashes at the end of the line continues the comment.
  assert.equal(texts("1 # c \\\n+ 1\n+ 10"), "1 + 10");
  assert.equal(texts("1 # c \\\\\n+ 1\n+ 10"), "1 + 1 + 10");
  assert.equal(texts("1 # c \\\\\\\n+ 1\n+ 10"), "1 + 10");
  assert.equal(texts("1 # c \\\r\n+ 1\r\n+ 10"), "1 + 10");
  assert.equal(texts("1 # c \\ x\n+ 1\n+ 10"), "1 + 1 + 10");
  // As in jq, a lone CR does not end a comment.
  assert.equal(texts("1 # c\r+ 1"), "1");
  assert.equal(texts("1 # c \\\r+ 1\n+ 10"), "1 + 10");
});

test("string escapes", () => {
  const cases: [string, string][] = [
    ['"abc"', "abc"],
    ['"\\"\\\\\\/\\b\\f\\n\\r\\t"', '"\\/\b\f\n\r\t'],
    ['"\\u00e9\\u00C9"', "éÉ"],
    ['"\\uD83D\\uDE00"', "😀"],
    ['"\\ud83d x"', "� x"],
    ['"\\ude00"', "�"],
    ['"\\uDE00\\uD83D"', "��"],
    ['"\\uD83D\\u0041"', "�A"],
    ['"\\uD83D\\uD83D\\uDE00"', "�😀"],
    ['"\\u12345"', "ሴ5"],
    ['"é\t"', "é\t"],
  ];
  for (const [source, text] of cases) {
    const token = single(source);
    assert.equal(token.kind, TokenKind.String);
    assert.equal(token.text, text, source);
  }
});

test("a lone surrogate in the source is U+FFFD", () => {
  assert.equal(single('"a\uD800b\uDC00"').text, "a�b�");
  assert.equal(error("a\uD800"), "Invalid character '�' @1:2");
  // A lone surrogate is one column.
  assert.equal(describe('"\uD800" +'), "String:�@1:1 Plus:+@1:5");
});

test("interpolation is split into pieces", () => {
  assert.equal(
    describe('"a\\(1 + (2))b\\("c")d"'),
    "StringStart:a@1:1 Number:1@1:5 Plus:+@1:7 LeftParen:(@1:9 Number:2@1:10 RightParen:)@1:11 StringMiddle:b@1:12 String:c@1:16 StringEnd:d@1:19",
  );
});

test("interpolation nests", () => {
  assert.equal(kinds('"a\\("b\\("c")")"'), "StringStart StringStart String StringEnd StringEnd");
  assert.equal(texts('"a\\("b\\("c")")"').trim(), "a b c");
});

test("a parenthesis outside interpolation is punctuation", () => {
  assert.equal(kinds(")"), "RightParen");
});

test("errors", () => {
  const cases: [string, string][] = [
    ['"abc', "Unterminated string @1:1"],
    ['1 + "a\\(1', "Unterminated string @1:5"],
    ['"a\\("b" + (1', "Unterminated string @1:1"],
    ['"a\\', "Unterminated string @1:1"],
    ['"a\\qb"', "Invalid escape @1:3"],
    ['"x"\n "\\u12"', "Invalid escape @2:3"],
    ['"\\u12G4"', "Invalid escape @1:2"],
    ['"\\u"', "Invalid escape @1:2"],
    ["1 $ x", "Expected a variable name after '$' @1:3"],
    ["@", "Expected a format name after '@' @1:1"],
    ["{aé: 1}", "Invalid character 'é' @1:3"],
    ["😀", "Invalid character '😀' @1:1"],
    ["1\f+2", "Invalid character U+000C @1:2"],
    ["1 ! 2", "Invalid character '!' @1:3"],
  ];
  for (const [source, expected] of cases) assert.equal(error(source), expected, source);
});

test("an error is the last token", () => {
  assert.equal(
    lex('1 + "a\\q" + 2')
      .map((t) => KindNames[t.kind])
      .join(" "),
    "Number Plus Error",
  );
});

test("empty source is eof", () => {
  const all = lex("  # nothing\n");
  assert.equal(all.length, 1);
  assert.equal(all[0]!.kind, TokenKind.Eof);
  assert.deepEqual([all[0]!.line, all[0]!.column], [2, 1]);
});
