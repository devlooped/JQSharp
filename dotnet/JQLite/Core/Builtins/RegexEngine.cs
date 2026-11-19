using System.Collections.Concurrent;
using static JQLite.Core.Builtins.RegexSyntax;

namespace JQLite.Core.Builtins;

/// <summary>
/// The engine of §9.10: compiles a pattern through <see cref="RegexSyntax"/> and finds the next match
/// from a position. It is portable code, no host regex engine: none reads the flavor with one meaning
/// (docs/porting.md §3.3).
/// </summary>
/// <remarks>
/// <para>
/// The matcher reads the input once, from the start position on, and follows every way the program
/// can match at the same time (a Pike machine): a thread is an instruction that waits for a code
/// point, with the captures of the way that led to it. The threads are kept in the order a
/// backtracking matcher would try them (a greedy quantifier takes all it can first, a lazy one as
/// little, the left alternative first), and a state reached a second time at the same position is
/// dropped: the first way to it has priority and the same future. So the time is the length of the
/// input times the size of the program, and the memory depends on the program alone. The
/// translation bounds the program (§11), so nothing is refused here: within those limits a program
/// has at most 50,000 instructions, a hundred levels and 2,002 capture slots per thread. A search for
/// the leftmost match does not start again at each position: a new thread starts at each position,
/// behind the ones already running, until a match is found. Nothing recurses.
/// </para>
/// <para>
/// A loop whose body can match nothing ends after an iteration that matched nothing
/// (<see cref="Op.Mark"/> and <see cref="Op.EmptyExit"/>). Whether an iteration has matched something
/// yet belongs to the state of a thread, as its level: of the loops with a mark around its
/// instruction, how many have, counted from the outermost (if an outer iteration has matched nothing,
/// the inner ones it contains have not either).
/// </para>
/// <para>
/// The input is read by code point: a match never splits a surrogate pair, and the positions are
/// UTF-16 indices. Compiled programs are immutable and cached per process in a thread-safe map that
/// is emptied when it reaches <see cref="CacheLimit"/> entries; the memory of a search is a
/// <see cref="RegexMatcher"/> of its own.
/// </para>
/// </remarks>
static class RegexEngine
{
    const int CacheLimit = 256;

    static readonly ConcurrentDictionary<string, RegexProgram> cache = new();

    /// <summary>The program of <paramref name="pattern"/> under <paramref name="options"/>, or why the pattern is not of the flavor or beyond its limits.</summary>
    public static bool TryCompile(string pattern, Options options, out RegexProgram program, out string reason)
    {
        var key = (options.Extended ? "x" : "-") + (options.DotAll ? "m" : "-") + (options.IgnoreCase ? "i" : "-") + pattern;
        reason = "";
        if (cache.TryGetValue(key, out program!))
            return true;
        if (!TryTranslate(pattern, options, out var translation, out reason))
            return false;
        program = new RegexProgram(translation);
        if (cache.Count >= CacheLimit)
            cache.Clear();
        cache[key] = program;
        return true;
    }
}

/// <summary>A translated pattern ready to run: immutable, shared by the searches of every thread.</summary>
sealed class RegexProgram
{
    public RegexProgram(Translation translation)
    {
        var ops = translation.Ops;
        Ops = ops;
        A = translation.A;
        B = translation.B;
        Sets = translation.Sets;
        Groups = translation.Groups;
        Slots = 2 * (translation.Groups.Length + 1);
        Depth = new int[ops.Length];
        Base = new int[ops.Length];
        var depth = 0;
        var states = 0;
        var readers = 0;
        for (var pc = 0; pc < ops.Length; pc++)
        {
            Depth[pc] = depth;
            Base[pc] = states;
            states += depth + 1;
            if (ops[pc] == Op.Mark)
                depth++;
            else if (ops[pc] == Op.EmptyExit)
                depth--;
            else if (ops[pc] <= Op.AnyButLineFeed)
                readers++;
        }

        States = states;
        Readers = readers;
        // The program starts with the instruction that stores the start of the match: what follows
        // it is the first thing every match does.
        Literal = ops[1] == Op.Char && translation.A[1] <= 0xFFFF ? translation.A[1] : -1;
        Anchored = ops[1] == Op.Begin;
    }

    /// <summary>One name (or null) per capturing group.</summary>
    public string?[] Groups { get; }

    /// <summary>The capture slots of a thread: 2 · (groups + 1).</summary>
    public int Slots { get; }

    /// <summary>The states of the program: its instructions, each at every level it can have.</summary>
    public int States { get; }

    /// <summary>The instructions that read a code point.</summary>
    public int Readers { get; }

    public Op[] Ops { get; }
    public int[] A { get; }
    public int[] B { get; }
    public int[][] Sets { get; }

    /// <summary>Per instruction: the loops with a mark around it.</summary>
    public int[] Depth { get; }

    /// <summary>Per instruction: its first state (the one of level 0).</summary>
    public int[] Base { get; }

    /// <summary>The UTF-16 unit a match must start with, or -1.</summary>
    public int Literal { get; }

    /// <summary>Whether a match can only start at the start of the input.</summary>
    public bool Anchored { get; }
}

/// <summary>
/// The memory of the searches of one caller over one program: one mark per state, a stack of at most
/// two entries per state, and two lists of threads of at most one thread per instruction that reads
/// a code point. Made at the first search and used again by the next ones; never shared between
/// threads.
/// </summary>
sealed class RegexMatcher(RegexProgram program)
{
    // Per state: the generation that reached it last. A generation is one position of one search.
    int[] seen = [];
    int generation;
    // Pairs: an instruction and its level, still to follow; or -1 - slot and the value to put back.
    int[] stack = [];
    // The captures of the thread being followed.
    int[] work = [];
    Threads current = null!;
    Threads next = null!;

    public RegexProgram Program => program;

    /// <summary>
    /// The first match starting at or after UTF-16 index <paramref name="start"/> (anchors and word
    /// boundaries see the whole input). <paramref name="spans"/> receives start and end of the match,
    /// then of each group (-1, -1 for a group that did not participate): 2 · (groups + 1) entries. A
    /// shorter <paramref name="spans"/> receives the first entries only; an empty one asks only
    /// whether there is a match.
    /// </summary>
    public bool TryMatch(string input, int start, int[] spans)
    {
        var ops = program.Ops;
        var a = program.A;
        var sets = program.Sets;
        var depth = program.Depth;
        var length = input.Length;
        var width = Math.Min(spans.Length, program.Slots);
        if (seen.Length == 0)
        {
            seen = new int[program.States];
            stack = new int[2 * program.States];
            work = new int[program.Slots];
            var capacity = Math.Min(8, program.Readers + 1);
            current = new Threads(capacity, program.Slots);
            next = new Threads(capacity, program.Slots);
        }

        current.Count = 0;
        var matched = false;
        var pos = start;
        Advance();
        while (true)
        {
            // The threads at pos are in current. Until a match is found, one more starts here, behind
            // them: a match that starts earlier has priority.
            if (!matched)
            {
                if (current.Count == 0)
                {
                    if (program.Anchored && pos > 0)
                        return false;
                    if (program.Literal >= 0)
                    {
                        var at = input.IndexOf((char)program.Literal, pos);
                        if (at < 0)
                            return false;
                        if (at != pos)
                        {
                            pos = at;
                            Advance();
                        }
                    }
                }

                for (var slot = 0; slot < width; slot++)
                    work[slot] = -1;
                if (Follow(current, 0, 0, input, pos, width, spans))
                {
                    if (width == 0)
                        return true;
                    matched = true;
                }
            }

            if (pos >= length)
                return matched;
            int c = input[pos];
            var size = 1;
            if (char.IsHighSurrogate(input[pos]) && pos + 1 < length && char.IsLowSurrogate(input[pos + 1]))
            {
                c = char.ConvertToUtf32(input[pos], input[pos + 1]);
                size = 2;
            }

            if (current.Count == 0)
            {
                if (matched)
                    return true;
                pos += size;
                Advance();
                continue;
            }

            // Every thread reads the code point at pos: the ones that accept it go on after it.
            Advance();
            next.Count = 0;
            for (var i = 0; i < current.Count; i++)
            {
                var pc = current.Pcs[i];
                var op = ops[pc];
                if (op == Op.Char ? a[pc] != c : op == Op.Set ? !InSet(sets[a[pc]], c) : op == Op.AnyButLineFeed && c == '\n')
                    continue;
                Array.Copy(current.Captures, i * program.Slots, work, 0, width);
                // It has read a code point: every loop around it has matched something.
                if (Follow(next, pc + 1, depth[pc + 1], input, pos + size, width, spans))
                {
                    if (width == 0)
                        return true;
                    // The threads behind this one have a lower priority than the match.
                    matched = true;
                    break;
                }
            }

            pos += size;
            (current, next) = (next, current);
        }
    }

    // The next position of the search: no state has been reached at it yet.
    void Advance()
    {
        if (generation == int.MaxValue)
        {
            Array.Clear(seen);
            generation = 0;
        }

        generation++;
    }

    // Follows a thread from an instruction at pos, with the captures in work, through the
    // instructions that read nothing: each reading instruction it reaches first becomes a thread of
    // threads, in priority order. True when it reached the end of the program: the match is then in
    // spans, and what it had still to follow has a lower priority.
    bool Follow(Threads threads, int pc, int level, string input, int pos, int width, int[] spans)
    {
        var ops = program.Ops;
        var a = program.A;
        var b = program.B;
        var depth = program.Depth;
        var states = program.Base;
        var slots = program.Slots;
        var top = 0;
        while (true)
        {
            var op = ops[pc];
            // What an instruction that reads does next does not depend on the level.
            var state = op <= Op.AnyButLineFeed ? states[pc] : states[pc] + level;
            var alive = seen[state] != generation;
            if (alive)
            {
                seen[state] = generation;
                switch (op)
                {
                    case Op.Char or Op.Set or Op.Any or Op.AnyButLineFeed:
                        var index = threads.Count;
                        if (index == threads.Pcs.Length)
                            threads.Grow();
                        threads.Pcs[index] = pc;
                        Array.Copy(work, 0, threads.Captures, index * slots, width);
                        threads.Count = index + 1;
                        alive = false;
                        break;
                    case Op.Split:
                        stack[top++] = pc + b[pc];
                        stack[top++] = level;
                        pc += a[pc];
                        break;
                    case Op.Jump:
                        pc += a[pc];
                        break;
                    case Op.Save:
                        var slot = a[pc];
                        if (slot < width)
                        {
                            stack[top++] = -1 - slot;
                            stack[top++] = work[slot];
                            work[slot] = pos;
                        }

                        pc++;
                        break;
                    case Op.Begin:
                        if (pos == 0)
                            pc++;
                        else
                            alive = false;
                        break;
                    case Op.End:
                        if (pos == input.Length)
                            pc++;
                        else
                            alive = false;
                        break;
                    case Op.WordBoundary or Op.NotWordBoundary:
                        var boundary = (pos > 0 && IsWord(input[pos - 1])) != (pos < input.Length && IsWord(input[pos]));
                        if (boundary == (op == Op.WordBoundary))
                            pc++;
                        else
                            alive = false;
                        break;
                    case Op.Mark:
                        // An iteration starts: it has matched nothing yet, so the level stays.
                        pc++;
                        break;
                    case Op.EmptyExit:
                        // The iteration of the innermost loop ends: it matched something if the level counts it.
                        var inner = depth[pc];
                        pc += level < inner ? b[pc] : 1;
                        if (level == inner)
                            level = inner - 1;
                        break;
                    default:
                        Array.Copy(work, spans, width);
                        return true;
                }
            }

            if (alive)
                continue;

            // This way ended: back to the last one left aside, putting back the captures stored since.
            while (true)
            {
                if (top == 0)
                    return false;
                var value = stack[--top];
                var entry = stack[--top];
                if (entry >= 0)
                {
                    pc = entry;
                    level = value;
                    break;
                }

                work[-1 - entry] = value;
            }
        }
    }

    // The ASCII word characters of §9.10: a UTF-16 unit of a code point above U+FFFF is not one.
    static bool IsWord(char unit) => unit is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    // Whether a code point is in a set: sorted pairs low, high.
    static bool InSet(int[] ranges, int c)
    {
        if (ranges.Length <= 8)
        {
            for (var i = 0; i < ranges.Length; i += 2)
            {
                if (c < ranges[i])
                    return false;
                if (c <= ranges[i + 1])
                    return true;
            }

            return false;
        }

        var low = 0;
        var high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            var middle = (low + high) >> 1;
            if (c < ranges[2 * middle])
                high = middle - 1;
            else if (c > ranges[2 * middle + 1])
                low = middle + 1;
            else
                return true;
        }

        return false;
    }

    // The threads at one position, in priority order: the instruction of each and its captures.
    sealed class Threads(int capacity, int slots)
    {
        public int[] Pcs { get; private set; } = new int[capacity];
        public int[] Captures { get; private set; } = new int[capacity * slots];
        public int Count { get; set; }

        // Room for one more thread: there are never more than the program has reading instructions.
        public void Grow()
        {
            var pcs = new int[Pcs.Length * 2];
            Array.Copy(Pcs, pcs, Pcs.Length);
            Pcs = pcs;
            var captures = new int[pcs.Length * slots];
            Array.Copy(Captures, captures, Captures.Length);
            Captures = captures;
        }
    }
}
