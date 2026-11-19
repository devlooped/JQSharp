//! The engine of §9.10: compiles a pattern through `regex_syntax` and finds the next match from a
//! position. It is portable code, no host regex engine: none reads the flavor with one meaning
//! (docs/porting.md §3.3).
//!
//! The matcher reads the input once, from the start position on, and follows every way the
//! program can match at the same time (a Pike machine): a thread is an instruction that waits for
//! a code point, with the captures of the way that led to it. The threads are kept in the order a
//! backtracking matcher would try them (a greedy quantifier takes all it can first, a lazy one as
//! little, the left alternative first), and a state reached a second time at the same position is
//! dropped: the first way to it has priority and the same future. So the time is the length of the
//! input times the size of the program, and the memory depends on the program alone. The
//! translation bounds the program (§11), so nothing is refused here: within those limits a program
//! has at most 50,000 instructions, a hundred levels and 2,002 capture slots per thread. A search for
//! the leftmost match does not start again at each position: a new thread starts at each position,
//! behind the ones already running, until a match is found. Nothing recurses.
//!
//! A loop whose body can match nothing ends after an iteration that matched nothing (`Op::Mark`
//! and `Op::EmptyExit`). Whether an iteration has matched something yet belongs to the state of a
//! thread, as its level: of the loops with a mark around its instruction, how many have, counted
//! from the outermost (if an outer iteration has matched nothing, the inner ones it contains have
//! not either).
//!
//! The input is read by code point: a match never splits the UTF-8 sequence of one, and the
//! positions are byte offsets. Compiled programs are immutable and cached per process in a map
//! behind a mutex that is emptied when it reaches `CACHE_LIMIT` entries; the memory of a search is
//! a [`RegexMatcher`] of its own.

use std::collections::HashMap;
use std::sync::{Arc, LazyLock, Mutex};

use super::regex_syntax::{self, Op, Options, Translation};

const CACHE_LIMIT: usize = 256;

static CACHE: LazyLock<Mutex<HashMap<String, Arc<RegexProgram>>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

/// The program of `pattern` under `options`, or why the pattern is not of the flavor or beyond its
/// limits.
pub fn try_compile(pattern: &str, options: Options) -> Result<Arc<RegexProgram>, String> {
    let flag = |on: bool, letter: char| if on { letter } else { '-' };
    let key = format!("{}{}{}{pattern}", flag(options.extended, 'x'), flag(options.dot_all, 'm'), flag(options.ignore_case, 'i'));
    if let Ok(cache) = CACHE.lock() {
        if let Some(program) = cache.get(&key) {
            return Ok(Arc::clone(program));
        }
    }

    let program = Arc::new(RegexProgram::new(regex_syntax::try_translate(pattern, options)?));
    if let Ok(mut cache) = CACHE.lock() {
        if cache.len() >= CACHE_LIMIT {
            cache.clear();
        }
        cache.insert(key, Arc::clone(&program));
    }
    Ok(program)
}

/// A translated pattern ready to run: immutable, shared by the searches of every thread.
pub struct RegexProgram {
    /// One name (or `None`) per capturing group.
    pub groups: Vec<Option<String>>,
    /// The capture slots of a thread: 2 · (groups + 1).
    pub slots: usize,
    /// The states of the program: its instructions, each at every level it can have.
    pub states: usize,
    /// The instructions that read a code point.
    pub readers: usize,
    pub ops: Vec<Op>,
    pub a: Vec<i32>,
    pub b: Vec<i32>,
    pub sets: Vec<Vec<u32>>,
    /// Per instruction: the loops with a mark around it.
    pub depth: Vec<usize>,
    /// Per instruction: its first state (the one of level 0).
    pub base: Vec<usize>,
    /// The code point a match must start with.
    pub literal: Option<char>,
    /// Whether a match can only start at the start of the input.
    pub anchored: bool,
}

impl RegexProgram {
    pub fn new(translation: Translation) -> RegexProgram {
        let Translation { ops, a, b, sets, groups, .. } = translation;
        let mut depth = Vec::with_capacity(ops.len());
        let mut base = Vec::with_capacity(ops.len());
        let mut level = 0;
        let mut states = 0;
        let mut readers = 0;
        for &op in &ops {
            depth.push(level);
            base.push(states);
            states += level + 1;
            match op {
                Op::Mark => level += 1,
                Op::EmptyExit => level -= 1,
                _ if op <= Op::AnyButLineFeed => readers += 1,
                _ => {}
            }
        }

        // The program starts with the instruction that stores the start of the match: what follows
        // it is the first thing every match does.
        let literal = if ops[1] == Op::Char { char::from_u32(a[1] as u32) } else { None };
        let anchored = ops[1] == Op::Begin;
        RegexProgram { slots: 2 * (groups.len() + 1), groups, states, readers, ops, a, b, sets, depth, base, literal, anchored }
    }
}

/// The memory of the searches of one caller over one program: one mark per state, a stack of at
/// most two entries per state, and two lists of threads of at most one thread per instruction that
/// reads a code point. Made at the first search and used again by the next ones; never shared
/// between threads.
pub struct RegexMatcher {
    program: Arc<RegexProgram>,
    memory: Memory,
    current: Threads,
    next: Threads,
}

// What following a thread uses.
#[derive(Default)]
struct Memory {
    // Per state: the generation that reached it last. A generation is one position of one search.
    seen: Vec<u32>,
    generation: u32,
    // Pairs: an instruction and its level, still to follow; or -1 - slot and the value to put back.
    stack: Vec<isize>,
    // The captures of the thread being followed: the first `width` slots.
    work: Vec<isize>,
    width: usize,
}

// The threads at one position, in priority order: the instruction of each and its captures
// (`width` per thread).
#[derive(Default)]
struct Threads {
    pcs: Vec<usize>,
    captures: Vec<isize>,
}

impl Threads {
    fn clear(&mut self) {
        self.pcs.clear();
        self.captures.clear();
    }
}

impl RegexMatcher {
    pub fn new(program: Arc<RegexProgram>) -> RegexMatcher {
        RegexMatcher { program, memory: Memory::default(), current: Threads::default(), next: Threads::default() }
    }

    pub fn program(&self) -> &RegexProgram {
        &self.program
    }

    /// The first match starting at or after the byte offset `start` (anchors and word boundaries
    /// see the whole input). `spans` receives start and end of the match, then of each group (-1,
    /// -1 for a group that did not participate): 2 · (groups + 1) entries. A shorter `spans`
    /// receives the first entries only; an empty one asks only whether there is a match.
    pub fn try_match(&mut self, input: &str, start: usize, spans: &mut [isize]) -> bool {
        let program: &RegexProgram = &self.program;
        let memory = &mut self.memory;
        if memory.seen.is_empty() {
            memory.seen = vec![0; program.states];
            memory.stack = vec![0; 2 * program.states];
            memory.work = vec![0; program.slots];
            // Room for the first threads, as the reference allocates it. Measured: a search without
            // spans, whose lists of captures were left without capacity, took three times as long.
            let capacity = program.readers.min(7) + 1;
            for threads in [&mut self.current, &mut self.next] {
                threads.pcs.reserve(capacity);
                threads.captures.reserve(capacity * program.slots);
            }
        }

        let width = spans.len().min(program.slots);
        memory.width = width;
        let bytes = input.as_bytes();
        let length = bytes.len();
        let mut current = &mut self.current;
        let mut next = &mut self.next;
        current.clear();
        let mut matched = false;
        let mut pos = start;
        memory.advance();
        loop {
            // The threads at pos are in current. Until a match is found, one more starts here, behind
            // them: a match that starts earlier has priority.
            if !matched {
                if current.pcs.is_empty() {
                    if program.anchored && pos > 0 {
                        return false;
                    }
                    if let Some(literal) = program.literal {
                        let Some(at) = input[pos..].find(literal) else {
                            return false;
                        };
                        if at > 0 {
                            pos += at;
                            memory.advance();
                        }
                    }
                }

                memory.work[..width].fill(-1);
                if memory.follow(program, current, 0, 0, bytes, pos) {
                    spans[..width].copy_from_slice(&memory.work[..width]);
                    if width == 0 {
                        return true;
                    }
                    matched = true;
                }
            }

            if pos >= length {
                return matched;
            }
            let (c, size) = decode(bytes, pos);
            if current.pcs.is_empty() {
                if matched {
                    return true;
                }
                pos += size;
                memory.advance();
                continue;
            }

            // Every thread reads the code point at pos: the ones that accept it go on after it.
            memory.advance();
            next.clear();
            for i in 0..current.pcs.len() {
                let pc = current.pcs[i];
                let accepted = match program.ops[pc] {
                    Op::Char => program.a[pc] as u32 == c,
                    Op::Set => in_set(&program.sets[program.a[pc] as usize], c),
                    Op::AnyButLineFeed => c != 0x0A,
                    _ => true,
                };
                if !accepted {
                    continue;
                }
                memory.work[..width].copy_from_slice(&current.captures[i * width..(i + 1) * width]);
                // It has read a code point: every loop around it has matched something.
                if memory.follow(program, next, pc + 1, program.depth[pc + 1], bytes, pos + size) {
                    spans[..width].copy_from_slice(&memory.work[..width]);
                    if width == 0 {
                        return true;
                    }
                    // The threads behind this one have a lower priority than the match.
                    matched = true;
                    break;
                }
            }

            pos += size;
            std::mem::swap(&mut current, &mut next);
        }
    }
}

impl Memory {
    // The next position of the search: no state has been reached at it yet.
    fn advance(&mut self) {
        if self.generation == u32::MAX {
            self.seen.fill(0);
            self.generation = 0;
        }

        self.generation += 1;
    }

    // Follows a thread from an instruction at pos, with the captures in work, through the
    // instructions that read nothing: each reading instruction it reaches first becomes a thread of
    // threads, in priority order. True when it reached the end of the program: the match is then in
    // work, and what it had still to follow has a lower priority.
    fn follow(&mut self, program: &RegexProgram, threads: &mut Threads, pc: usize, level: usize, input: &[u8], pos: usize) -> bool {
        let (mut pc, mut level) = (pc, level);
        let width = self.width;
        let mut top = 0;
        loop {
            let op = program.ops[pc];
            // What an instruction that reads does next does not depend on the level.
            let state = if op <= Op::AnyButLineFeed { program.base[pc] } else { program.base[pc] + level };
            let mut alive = self.seen[state] != self.generation;
            if alive {
                self.seen[state] = self.generation;
                match op {
                    Op::Char | Op::Set | Op::Any | Op::AnyButLineFeed => {
                        threads.pcs.push(pc);
                        threads.captures.extend_from_slice(&self.work[..width]);
                        alive = false;
                    }
                    Op::Split => {
                        self.stack[top] = jump(pc, program.b[pc]) as isize;
                        self.stack[top + 1] = level as isize;
                        top += 2;
                        pc = jump(pc, program.a[pc]);
                    }
                    Op::Jump => pc = jump(pc, program.a[pc]),
                    Op::Save => {
                        let slot = program.a[pc] as usize;
                        if slot < width {
                            self.stack[top] = -1 - slot as isize;
                            self.stack[top + 1] = self.work[slot];
                            top += 2;
                            self.work[slot] = pos as isize;
                        }

                        pc += 1;
                    }
                    Op::Begin => {
                        if pos == 0 {
                            pc += 1;
                        } else {
                            alive = false;
                        }
                    }
                    Op::End => {
                        if pos == input.len() {
                            pc += 1;
                        } else {
                            alive = false;
                        }
                    }
                    Op::WordBoundary | Op::NotWordBoundary => {
                        let boundary = (pos > 0 && is_word(input[pos - 1])) != (pos < input.len() && is_word(input[pos]));
                        if boundary == (op == Op::WordBoundary) {
                            pc += 1;
                        } else {
                            alive = false;
                        }
                    }
                    // An iteration starts: it has matched nothing yet, so the level stays.
                    Op::Mark => pc += 1,
                    Op::EmptyExit => {
                        // The iteration of the innermost loop ends: it matched something if the level counts it.
                        let inner = program.depth[pc];
                        pc = if level < inner { jump(pc, program.b[pc]) } else { pc + 1 };
                        if level == inner {
                            level = inner - 1;
                        }
                    }
                    Op::Match => return true,
                }
            }

            if alive {
                continue;
            }

            // This way ended: back to the last one left aside, putting back the captures stored since.
            loop {
                if top == 0 {
                    return false;
                }
                top -= 2;
                let (entry, value) = (self.stack[top], self.stack[top + 1]);
                if entry >= 0 {
                    pc = entry as usize;
                    level = value as usize;
                    break;
                }

                self.work[(-1 - entry) as usize] = value;
            }
        }
    }
}

// The instruction `by` instructions after `pc`.
fn jump(pc: usize, by: i32) -> usize {
    (pc as isize + by as isize) as usize
}

// The code point at `pos` of well-formed UTF-8, and its length in bytes.
fn decode(input: &[u8], pos: usize) -> (u32, usize) {
    let first = input[pos] as u32;
    let next = |k: usize| input[pos + k] as u32 & 0x3F;
    if first < 0x80 {
        (first, 1)
    } else if first < 0xE0 {
        ((first & 0x1F) << 6 | next(1), 2)
    } else if first < 0xF0 {
        ((first & 0x0F) << 12 | next(1) << 6 | next(2), 3)
    } else {
        ((first & 0x07) << 18 | next(1) << 12 | next(2) << 6 | next(3), 4)
    }
}

// The ASCII word characters of §9.10: a byte of the UTF-8 sequence of another code point is not one.
fn is_word(byte: u8) -> bool {
    byte.is_ascii_alphanumeric() || byte == b'_'
}

// Whether a code point is in a set: sorted pairs low, high.
fn in_set(ranges: &[u32], c: u32) -> bool {
    if ranges.len() <= 8 {
        for pair in ranges.chunks_exact(2) {
            if c < pair[0] {
                return false;
            }
            if c <= pair[1] {
                return true;
            }
        }

        return false;
    }

    let (mut low, mut high) = (0, ranges.len() / 2);
    while low < high {
        let middle = (low + high) / 2;
        if c < ranges[2 * middle] {
            high = middle;
        } else if c > ranges[2 * middle + 1] {
            low = middle + 1;
        } else {
            return true;
        }
    }

    false
}
