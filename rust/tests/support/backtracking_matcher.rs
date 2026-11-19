//! What the programs of `regex_syntax` mean, as the simplest machine that runs them: a
//! backtracking matcher that tries each start position in turn and each alternative in priority
//! order, with the positions of the marks as they are stored. It can take time exponential in the
//! input, so it is not the engine; `tests/regex_engine.rs` compares the engine with it on small
//! patterns and inputs, and gives up on a search that takes it more than a budget of steps.

use jqlite::builtins::regex_syntax::{Op, Translation};

// What the stack holds, three numbers an entry: an alternative to try (its instruction and
// position), or the value of a slot or of a mark to put back when going back past it.
const ALTERNATIVE: isize = 0;
const SLOT: isize = 1;
const MARK: isize = 2;

/// The first match starting at or after the byte offset `start`: its spans, as
/// `RegexMatcher::try_match` gives them; `None` when the search is not over after `budget`
/// instructions.
pub fn try_match(program: &Translation, input: &str, start: usize, spans: &mut [isize], budget: isize) -> Option<bool> {
    let mut steps = budget;
    let mut at = start;
    while at <= input.len() {
        if run(program, input, at, spans, &mut steps) {
            return Some(true);
        }
        if steps < 0 {
            return None;
        }
        at += input[at..].chars().next().map_or(1, char::len_utf8);
    }

    Some(false)
}

// Whether the program matches from at; the spans are then in spans.
fn run(program: &Translation, input: &str, at: usize, spans: &mut [isize], steps: &mut isize) -> bool {
    let ops = &program.ops;
    let bytes = input.as_bytes();
    let mut marks = vec![-1; program.marks];
    spans.fill(-1);
    let mut stack: Vec<isize> = Vec::new();
    let mut pc = 0;
    let mut pos = at;
    loop {
        *steps -= 1;
        if *steps < 0 {
            return false;
        }
        let mut ok = true;
        let a = program.a[pc];
        match ops[pc] {
            Op::Char | Op::Set | Op::Any | Op::AnyButLineFeed => {
                let accepted = input[pos..].chars().next().filter(|&c| match ops[pc] {
                    Op::Char => c as i32 == a,
                    Op::Set => in_set(&program.sets[a as usize], c as u32),
                    Op::Any => true,
                    _ => c != '\n',
                });
                match accepted {
                    Some(c) => {
                        pos += c.len_utf8();
                        pc += 1;
                    }
                    None => ok = false,
                }
            }
            Op::Split => {
                stack.extend([ALTERNATIVE, pc as isize + program.b[pc] as isize, pos as isize]);
                pc = (pc as isize + a as isize) as usize;
            }
            Op::Jump => pc = (pc as isize + a as isize) as usize,
            Op::Save => {
                stack.extend([SLOT, a as isize, spans[a as usize]]);
                spans[a as usize] = pos as isize;
                pc += 1;
            }
            Op::Begin => {
                ok = pos == 0;
                pc += 1;
            }
            Op::End => {
                ok = pos == input.len();
                pc += 1;
            }
            Op::WordBoundary | Op::NotWordBoundary => {
                let boundary = (pos > 0 && is_word(bytes[pos - 1])) != (pos < bytes.len() && is_word(bytes[pos]));
                ok = boundary == (ops[pc] == Op::WordBoundary);
                pc += 1;
            }
            Op::Mark => {
                stack.extend([MARK, a as isize, marks[a as usize]]);
                marks[a as usize] = pos as isize;
                pc += 1;
            }
            Op::EmptyExit => {
                pc = if pos as isize == marks[a as usize] { (pc as isize + program.b[pc] as isize) as usize } else { pc + 1 };
            }
            Op::Match => return true,
        }

        if ok {
            continue;
        }

        // Back to the last alternative, undoing what was stored since.
        loop {
            let Some(value) = stack.pop() else {
                return false;
            };
            let index = stack.pop().unwrap_or_default();
            let kind = stack.pop().unwrap_or_default();
            if kind == ALTERNATIVE {
                pc = index as usize;
                pos = value as usize;
                break;
            }

            if kind == SLOT {
                spans[index as usize] = value;
            } else {
                marks[index as usize] = value;
            }
        }
    }
}

fn is_word(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || byte == b'_'
}

fn in_set(ranges: &[u32], c: u32) -> bool {
    ranges.chunks_exact(2).any(|pair| c >= pair[0] && c <= pair[1])
}
