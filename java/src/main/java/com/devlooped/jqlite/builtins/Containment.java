package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import java.util.ArrayList;
import java.util.List;

/** §9.4 {@code contains/1}: whether the input contains the argument. */
final class Containment {
    private Containment() {
    }

    static void register() {
        add("contains", 1, Containment::contains);
    }

    // At the top level the kinds must be equal; below it a kind mismatch is false.
    private static Result contains(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        return input.kind() == arguments[0].kind()
            ? JqValue.fromBool(check(input, arguments[0]))
            : fail(Errors.cannotCheckContainment(input, arguments[0]));
    }

    // A pending check of containers: a contains b. For arrays, every element b[item] must be
    // contained by some a[candidate]; for objects, every key of b (b's entry item) must be in a
    // with a value that contains b's.
    private static final class Frame {
        final JqValue a;
        final JqValue b;
        int item;
        int candidate;

        Frame(JqValue a, JqValue b) {
            this.a = a;
            this.b = b;
        }
    }

    // The result of a step: 1 true, 0 false, -1 a child check in child[0], child[1].
    private static final int CHILD = -1;

    // Containers are checked with an explicit stack, so that deep values cannot exhaust the host
    // stack (§11): each frame asks for one child result at a time, which comes back in 'answer'.
    private static boolean check(JqValue a, JqValue b) {
        int scalar = tryScalar(a, b);
        if (scalar != CHILD) {
            return scalar == 1;
        }

        List<Frame> stack = new ArrayList<>();
        stack.add(new Frame(a, b));
        boolean answer = false;
        boolean answered = false;
        JqValue[] child = new JqValue[2];
        while (!stack.isEmpty()) {
            Frame frame = stack.get(stack.size() - 1);
            int done = frame.b.kind() == JqKind.ARRAY
                ? stepArray(frame, answered, answer, child)
                : stepObject(frame, answered, answer, child);
            answered = false;

            if (done != CHILD) {
                stack.remove(stack.size() - 1);
                answer = done == 1;
                answered = true;
            } else {
                int direct = tryScalar(child[0], child[1]);
                if (direct != CHILD) {
                    answer = direct == 1;
                    answered = true;
                } else {
                    stack.add(new Frame(child[0], child[1]));
                }
            }
        }

        return answer;
    }

    // The next child check of an array frame, or its result.
    private static int stepArray(Frame frame, boolean answered, boolean answer, JqValue[] child) {
        JqArray a = frame.a.asArray();
        JqArray b = frame.b.asArray();
        if (answered) {
            if (answer) {
                frame.item++;
                frame.candidate = 0;
            } else {
                frame.candidate++;
            }
        }

        if (frame.item == b.count()) {
            return 1;
        }
        if (frame.candidate == a.count()) {
            return 0;
        }
        child[0] = a.get(frame.candidate);
        child[1] = b.get(frame.item);
        return CHILD;
    }

    // The next child check of an object frame, or its result.
    private static int stepObject(Frame frame, boolean answered, boolean answer, JqValue[] child) {
        JqObject a = frame.a.asObject();
        JqObject b = frame.b.asObject();
        if (answered) {
            if (!answer) {
                return 0;
            }
            frame.item++;
        }

        if (frame.item == b.count()) {
            return 1;
        }
        JqValue value = a.get(b.keyAt(frame.item));
        if (value == null) {
            return 0;
        }
        child[0] = value;
        child[1] = b.valueAt(frame.item);
        return CHILD;
    }

    // The result of a check that needs no frame (kinds differ, strings, or other scalars), or CHILD.
    private static int tryScalar(JqValue a, JqValue b) {
        if (a.kind() != b.kind()) {
            return 0;
        }

        return switch (a.kind()) {
            case ARRAY, OBJECT -> CHILD;
            case STRING -> a.asString().contains(b.asString()) ? 1 : 0;
            default -> ValueOps.areEqual(a, b) ? 1 : 0;
        };
    }
}
