using Devlooped.Core.Runtime;
using Devlooped.Core.Values;
using static Devlooped.Core.Builtins.NativeTable;

namespace Devlooped.Core.Builtins;

/// <summary>§9.4 <c>contains/1</c>: whether the input contains the argument.</summary>
static class Containment
{
    public static void Register() => Add("contains", 1, Contains);

    // At the top level the kinds must be equal; below it a kind mismatch is false.
    static bool Contains(Interpreter interpreter, JqValue input, JqValue[] arguments, out JqValue result, out JqValue error)
        => input.Kind == arguments[0].Kind
            ? Ok(JqValue.FromBool(Check(input, arguments[0])), out result, out error)
            : Fail(Errors.CannotCheckContainment(input, arguments[0]), out result, out error);

    // A pending check of containers: a contains b. For arrays, every element b[Item] must be
    // contained by some a[Candidate]; for objects, every key of b (b's entry Item) must be in a
    // with a value that contains b's.
    struct Frame(JqValue a, JqValue b)
    {
        public JqValue A = a;
        public JqValue B = b;
        public int Item;
        public int Candidate;
    }

    // Containers are checked with an explicit stack, so that deep values cannot exhaust the host
    // stack (§11): each frame asks for one child result at a time, which comes back in 'answer'.
    static bool Check(JqValue a, JqValue b)
    {
        if (TryScalar(a, b, out var scalar))
            return scalar;

        var stack = new List<Frame> { new(a, b) };
        var answer = false;
        var answered = false;
        while (stack.Count > 0)
        {
            var frame = stack[^1];
            bool? done;
            JqValue childA = default, childB = default;
            if (frame.B.Kind == JqKind.Array)
                done = StepArray(ref frame, answered, answer, ref childA, ref childB);
            else
                done = StepObject(ref frame, answered, answer, ref childA, ref childB);
            stack[^1] = frame;
            answered = false;

            if (done is { } finished)
            {
                stack.RemoveAt(stack.Count - 1);
                (answer, answered) = (finished, true);
            }
            else if (TryScalar(childA, childB, out var direct))
            {
                (answer, answered) = (direct, true);
            }
            else
            {
                stack.Add(new Frame(childA, childB));
            }
        }

        return answer;
    }

    // The next child check of an array frame, or its result.
    static bool? StepArray(ref Frame frame, bool answered, bool answer, ref JqValue childA, ref JqValue childB)
    {
        var a = frame.A.AsArray();
        var b = frame.B.AsArray();
        if (answered)
        {
            if (answer)
                (frame.Item, frame.Candidate) = (frame.Item + 1, 0);
            else
                frame.Candidate++;
        }

        if (frame.Item == b.Count)
            return true;
        if (frame.Candidate == a.Count)
            return false;
        (childA, childB) = (a[frame.Candidate], b[frame.Item]);
        return null;
    }

    // The next child check of an object frame, or its result.
    static bool? StepObject(ref Frame frame, bool answered, bool answer, ref JqValue childA, ref JqValue childB)
    {
        var a = frame.A.AsObject();
        var b = frame.B.AsObject();
        if (answered)
        {
            if (!answer)
                return false;
            frame.Item++;
        }

        if (frame.Item == b.Count)
            return true;
        if (!a.TryGetValue(b.KeyAt(frame.Item), out var value))
            return false;
        (childA, childB) = (value, b.ValueAt(frame.Item));
        return null;
    }

    // The result of a check that needs no frame: kinds differ, strings, or other scalars.
    static bool TryScalar(JqValue a, JqValue b, out bool result)
    {
        if (a.Kind != b.Kind)
        {
            result = false;
            return true;
        }

        switch (a.Kind)
        {
            case JqKind.Array or JqKind.Object:
                result = false;
                return false;
            case JqKind.String:
                result = a.AsString().Contains(b.AsString(), StringComparison.Ordinal);
                return true;
            default:
                result = ValueOps.AreEqual(a, b);
                return true;
        }
    }
}
