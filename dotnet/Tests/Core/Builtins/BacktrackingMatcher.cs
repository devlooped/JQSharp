using JQLite.Core.Builtins;
using static JQLite.Core.Builtins.RegexSyntax;

namespace JQLite.Tests.Core.Builtins;

/// <summary>
/// What the programs of <see cref="RegexSyntax"/> mean, as the simplest machine that runs them: a
/// backtracking matcher that tries each start position in turn and each alternative in priority
/// order, with the positions of the marks as they are stored. It can take time exponential in the
/// input, so it is not the engine; <see cref="RegexEngineTests"/> compares the engine with it on
/// small patterns and inputs, and gives up on a search that takes it more than a budget of steps.
/// </summary>
static class BacktrackingMatcher
{
    // What the stack holds, three numbers an entry: an alternative to try (its instruction and
    // position), or the value of a slot or of a mark to put back when going back past it.
    const int Alternative = 0;
    const int Slot = 1;
    const int Mark = 2;

    /// <summary>
    /// The first match starting at or after UTF-16 index <paramref name="start"/>: its spans, as
    /// <see cref="RegexMatcher.TryMatch"/> gives them; null when the search is not over after
    /// <paramref name="budget"/> instructions.
    /// </summary>
    public static bool? TryMatch(Translation program, string input, int start, int[] spans, int budget)
    {
        var steps = budget;
        for (var at = start; at <= input.Length; at++)
        {
            if (Run(program, input, at, spans, ref steps))
                return true;
            if (steps < 0)
                return null;
            if (at < input.Length && char.IsHighSurrogate(input[at]))
                at++;
        }

        return false;
    }

    // Whether the program matches from at; the spans are then in spans.
    static bool Run(Translation program, string input, int at, int[] spans, ref int steps)
    {
        var ops = program.Ops;
        var marks = new int[program.Marks];
        Array.Fill(marks, -1);
        Array.Fill(spans, -1);
        var stack = new List<int>();
        var pc = 0;
        var pos = at;
        while (true)
        {
            if (--steps < 0)
                return false;
            var ok = true;
            var a = program.A[pc];
            switch (ops[pc])
            {
                case Op.Char or Op.Set or Op.Any or Op.AnyButLineFeed:
                    var c = pos < input.Length ? char.ConvertToUtf32(input, pos) : -1;
                    if (c >= 0 && (ops[pc] == Op.Char ? c == a : ops[pc] == Op.Set ? InSet(program.Sets[a], c) : ops[pc] == Op.Any || c != '\n'))
                    {
                        pos += c > 0xFFFF ? 2 : 1;
                        pc++;
                    }
                    else
                    {
                        ok = false;
                    }

                    break;
                case Op.Split:
                    stack.AddRange([Alternative, pc + program.B[pc], pos]);
                    pc += a;
                    break;
                case Op.Jump:
                    pc += a;
                    break;
                case Op.Save:
                    stack.AddRange([Slot, a, spans[a]]);
                    spans[a] = pos;
                    pc++;
                    break;
                case Op.Begin:
                    ok = pos == 0;
                    pc++;
                    break;
                case Op.End:
                    ok = pos == input.Length;
                    pc++;
                    break;
                case Op.WordBoundary or Op.NotWordBoundary:
                    var boundary = (pos > 0 && IsWord(input[pos - 1])) != (pos < input.Length && IsWord(input[pos]));
                    ok = boundary == (ops[pc] == Op.WordBoundary);
                    pc++;
                    break;
                case Op.Mark:
                    stack.AddRange([Mark, a, marks[a]]);
                    marks[a] = pos;
                    pc++;
                    break;
                case Op.EmptyExit:
                    pc += pos == marks[a] ? program.B[pc] : 1;
                    break;
                default:
                    return true;
            }

            if (ok)
                continue;

            // Back to the last alternative, undoing what was stored since.
            while (true)
            {
                if (stack.Count == 0)
                    return false;
                var value = stack[^1];
                var index = stack[^2];
                var kind = stack[^3];
                stack.RemoveRange(stack.Count - 3, 3);
                if (kind == Alternative)
                {
                    pc = index;
                    pos = value;
                    break;
                }

                if (kind == Slot)
                    spans[index] = value;
                else
                    marks[index] = value;
            }
        }
    }

    static bool IsWord(char unit) => unit is (>= '0' and <= '9') or (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_';

    static bool InSet(int[] ranges, int c)
    {
        for (var i = 0; i < ranges.Length; i += 2)
        {
            if (c >= ranges[i] && c <= ranges[i + 1])
                return true;
        }

        return false;
    }
}
