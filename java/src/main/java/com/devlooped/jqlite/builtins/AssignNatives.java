package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.addClosure;

import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.runtime.Emit;
import com.devlooped.jqlite.runtime.Env;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.runtime.Path;
import com.devlooped.jqlite.runtime.Signal;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqArrayBuilder;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqObjectBuilder;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;

/**
 * {@code _modify/2} and {@code _assign/2} (§8.3) implemented natively, overriding their prelude
 * definitions (Native.overridesPrelude): same results, errors, order of evaluation and path behavior,
 * but linear in the number of paths, where the prelude's one {@code setpath} per path copies the
 * container each time. The value being updated is a {@link WorkingTree}.
 */
final class AssignNatives {
    private AssignNatives() {
    }

    static void register() {
        addClosure("_modify", 2, AssignNatives::modify);
        addClosure("_assign", 2, AssignNatives::assign);
    }

    // def _modify(paths; update): for each path of paths (on the input, lazily), the first output
    // of update applied to the current value there replaces it; no output: the path is deleted at
    // the end. update runs in value mode; the result has no path.
    private static Signal modify(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        WorkingTree tree = new WorkingTree(input);
        List<JqValue> deletions = new ArrayList<>();
        Signal first = Signal.breakTo(interpreter.newLabel());
        Signal signal = interpreter.eval(arguments[0], env, input, Path.ROOT, (value, at) -> {
            if (at == null || !at.isTracked()) {
                return Interpreter.fail(Errors.invalidPathResult(value));
            }
            JqValue keys = at.toValue();
            Result current = tree.tryGet(keys.asArray());
            if (current instanceof Result.Err err) {
                return Signal.error(err.error());
            }
            boolean[] updated = {false};
            JqValue[] next = {JqValue.NULL};
            Signal stop = interpreter.eval(arguments[1], env, (JqValue) current, null, (output, unused) -> {
                updated[0] = true;
                next[0] = output;
                return first;
            });
            if (stop != null && stop != first) {
                return stop;
            }
            if (!updated[0]) {
                deletions.add(keys);
                return null;
            }

            JqValue error = tree.trySet(keys.asArray(), next[0]);
            return error == null ? null : Signal.error(error);
        });
        if (signal != null) {
            return signal;
        }
        JqValue paths = JqArray.fromOwned(deletions.toArray(new JqValue[0]));
        Result result = PathNatives.tryDeletePaths(tree.freeze(), paths);
        return result instanceof Result.Err err ? Signal.error(err.error()) : interpreter.output(emit, (JqValue) result, Path.of(path));
    }

    // def _assign(paths; $value): for each output of value (on the input), every path of paths set
    // to it. With no path the result is the input itself, with its path (the reduce's initial state).
    private static Signal assign(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        return interpreter.forEach(arguments[1], env, input, null, (value, unused) -> {
            WorkingTree tree = new WorkingTree(input);
            boolean[] any = {false};
            Signal signal = interpreter.eval(arguments[0], env, input, Path.ROOT, (target, at) -> {
                if (at == null || !at.isTracked()) {
                    return Interpreter.fail(Errors.invalidPathResult(target));
                }
                any[0] = true;
                JqValue error = tree.trySet(at.toValue().asArray(), value);
                return error == null ? null : Signal.error(error);
            });
            if (signal != null) {
                return signal;
            }
            return any[0] ? interpreter.output(emit, tree.freeze(), Path.of(path)) : interpreter.output(emit, input, path);
        });
    }

    /**
     * A value under update, owned by one call of {@code _modify} or {@code _assign}. Each node is
     * either an immutable value or a draft: a container the tree copied (one level) and then updates
     * in place, so a container is copied at most once however many paths go through it.
     *
     * <p>Nothing a program can see is ever changed. {@link #trySet} stores the new value as an
     * immutable leaf and opens drafts only along its path; {@link #tryGet}, which hands a value to the
     * update closure, and {@link #freeze} turn the drafts below the path back into values, giving them
     * their storage without copying, so a later write there copies that level again. Paths with keys
     * the drafts do not handle (slices, keys that {@code setpath} rejects) fall back to
     * {@code getpath}/{@code setpath} on the frozen value below that point, which keeps their results
     * and errors, in their order, those of the prelude definition. No walk recurses: paths and drafts
     * are followed with loops and an explicit stack. (In the reference, this class shares AssignNatives.cs.)
     */
    static final class WorkingTree {
        // setpath's index limit (§11).
        private static final double MAX_INDEX = 536870911;

        // A container copied from a value: its builder, the drafts below it by slot, and where it hangs.
        private static final class Draft {
            final JqArrayBuilder array;
            final JqObjectBuilder object;
            final Draft parent;
            final int index;
            final String key;
            HashMap<Integer, Draft> items;
            HashMap<String, Draft> fields;

            Draft(JqArrayBuilder array, JqObjectBuilder object, Draft parent, int index, String key) {
                this.array = array;
                this.object = object;
                this.parent = parent;
                this.index = index;
                this.key = key;
            }

            JqValue build() {
                return array != null ? array.toArray() : object.toObject();
            }
        }

        // A slot of the tree: the root (parent null), an element of an array draft or a field of an object draft.
        private record Slot(Draft parent, int index, String key) {
        }

        private static final Slot ROOT = new Slot(null, 0, null);

        private JqValue root;
        private Draft rootDraft;

        WorkingTree(JqValue root) {
            this.root = root;
        }

        /** The value of the whole tree; the tree holds no draft afterwards. */
        JqValue freeze() {
            return freezeAt(ROOT);
        }

        /**
         * {@code getpath(keys)} on the current value. The value returned is frozen in the tree, so the
         * caller may keep it: the tree never changes it.
         */
        Result tryGet(JqArray keys) {
            Slot slot = ROOT;
            for (int i = 0; i < keys.count(); i++) {
                Draft draft = draftAt(slot);
                if (draft != null) {
                    Slot next = tryRead(draft, keys.get(i));
                    if (next != null) {
                        if (next != MISSING) {
                            slot = next;
                            continue;
                        }

                        // An array element out of range: null, which the rest of the path indexes.
                        return tryIndex(JqValue.NULL, keys, i + 1);
                    }
                }

                return tryIndex(freezeAt(slot), keys, i);
            }

            return freezeAt(slot);
        }

        /** {@code setpath(keys; value)} on the current value: Java null, or the error. */
        JqValue trySet(JqArray keys, JqValue value) {
            Slot slot = ROOT;
            for (int i = 0; i < keys.count(); i++) {
                JqValue key = keys.get(i);
                Draft draft = draftAt(slot);
                Slot next = draft == null ? null : tryWrite(draft, key);
                if (next != null) {
                    slot = next;
                    continue;
                }

                JqValue current = draft == null ? valueAt(slot) : freezeAt(slot);
                draft = tryOpen(current, key, slot);
                next = draft == null ? null : tryWrite(draft, key);
                if (next != null) {
                    slot = next;
                    continue;
                }

                // A key the drafts do not handle: setpath with the rest of the path, on the value here.
                Result result = PathNatives.trySetPath(current, keys.slice(i, keys.count()), value);
                if (result instanceof Result.Err err) {
                    return err.error();
                }
                store(slot, (JqValue) result);
                return null;
            }

            store(slot, value);
            return null;
        }

        // keys[from..] indexed from value, as getpath does.
        private static Result tryIndex(JqValue value, JqArray keys, int from) {
            Result result = value;
            for (int i = from; i < keys.count(); i++) {
                result = ValueOps.tryIndex((JqValue) result, keys.get(i));
                if (result instanceof Result.Err) {
                    return result;
                }
            }

            return result;
        }

        // The answer of tryRead for an element out of range (the value there is null).
        private static final Slot MISSING = new Slot(null, -1, null);

        // The slot a read goes to next: a field (present or not) or an element in range; MISSING for
        // an element out of range; null for keys a draft does not read itself.
        private static Slot tryRead(Draft draft, JqValue key) {
            if (draft.object != null) {
                return key.kind() == JqKind.STRING ? new Slot(draft, 0, key.asString()) : null;
            }

            if (key.kind() != JqKind.NUMBER) {
                return null;
            }
            double number = key.asNumber();
            double index = Double.isNaN(number) ? -1 : number < 0 ? Math.ceil(number) : Math.floor(number);
            if (index < 0) {
                index += draft.array.count();
            }
            boolean missing = Double.isNaN(number) || index < 0 || index >= draft.array.count();
            return missing ? MISSING : new Slot(draft, (int) index, null);
        }

        // The slot a write goes to next, when setpath accepts the key for this container without error; else null.
        private static Slot tryWrite(Draft draft, JqValue key) {
            if (draft.object != null) {
                return key.kind() == JqKind.STRING ? new Slot(draft, 0, key.asString()) : null;
            }

            int index = tryArrayIndex(key, draft.array.count());
            return index < 0 ? null : new Slot(draft, index, null);
        }

        // setpath's index of an array of the given length: not NaN, negative counted from the end,
        // within the limit; -1 otherwise.
        private static int tryArrayIndex(JqValue key, int length) {
            if (key.kind() != JqKind.NUMBER || Double.isNaN(key.asNumber())) {
                return -1;
            }
            double number = key.asNumber();
            double i = number < 0 ? Math.ceil(number) : Math.floor(number);
            if (i < 0) {
                i += length;
            }
            if (i < 0 || i > MAX_INDEX) {
                return -1;
            }
            return (int) i;
        }

        // Copies one level of the value at a slot into a draft there, when setpath would write into it
        // with this key (null becomes the container the key asks for); else null.
        private Draft tryOpen(JqValue value, JqValue key, Slot slot) {
            JqKind kind = value.kind();
            Draft draft;
            if (key.kind() == JqKind.STRING && (kind == JqKind.OBJECT || kind == JqKind.NULL)) {
                draft = new Draft(null, new JqObjectBuilder(kind == JqKind.NULL ? JqObject.EMPTY : value.asObject()), slot.parent, slot.index, slot.key);
            } else if (key.kind() == JqKind.NUMBER && (kind == JqKind.ARRAY || kind == JqKind.NULL)
                && tryArrayIndex(key, kind == JqKind.NULL ? 0 : value.asArray().count()) >= 0) {
                draft = new Draft(new JqArrayBuilder(kind == JqKind.NULL ? JqArray.EMPTY : value.asArray()), null, slot.parent, slot.index, slot.key);
            } else {
                return null;
            }

            attach(slot, draft);
            return draft;
        }

        private Draft draftAt(Slot slot) {
            if (slot.parent == null) {
                return rootDraft;
            }
            if (slot.key != null) {
                return slot.parent.fields == null ? null : slot.parent.fields.get(slot.key);
            }
            return slot.parent.items == null ? null : slot.parent.items.get(slot.index);
        }

        // The value at a slot that holds no draft.
        private JqValue valueAt(Slot slot) {
            if (slot.parent == null) {
                return root;
            }
            if (slot.key != null) {
                JqValue field = slot.parent.object.get(slot.key);
                return field != null ? field : JqValue.NULL;
            }
            return slot.index < slot.parent.array.count() ? slot.parent.array.get(slot.index) : JqValue.NULL;
        }

        // Puts a value at a slot, replacing whatever was there (a value or a draft).
        private void store(Slot slot, JqValue value) {
            if (slot.parent == null) {
                root = value;
                rootDraft = null;
            } else if (slot.key != null) {
                slot.parent.object.set(slot.key, value);
                if (slot.parent.fields != null) {
                    slot.parent.fields.remove(slot.key);
                }
            } else {
                slot.parent.array.set(slot.index, value);
                if (slot.parent.items != null) {
                    slot.parent.items.remove(slot.index);
                }
            }
        }

        // Hangs a draft at a slot. Its place in the parent is taken now (a new key goes last now,
        // missing elements become null now), as setpath would; the value is written when it freezes.
        private void attach(Slot slot, Draft draft) {
            store(slot, JqValue.NULL);
            if (slot.parent == null) {
                rootDraft = draft;
            } else if (slot.key != null) {
                if (slot.parent.fields == null) {
                    slot.parent.fields = new HashMap<>();
                }
                slot.parent.fields.put(slot.key, draft);
            } else {
                if (slot.parent.items == null) {
                    slot.parent.items = new HashMap<>();
                }
                slot.parent.items.put(slot.index, draft);
            }
        }

        // The value at a slot, freezing the drafts there first: children before parents, each written
        // into its parent's builder, the top one stored at the slot.
        private JqValue freezeAt(Slot slot) {
            Draft top = draftAt(slot);
            if (top == null) {
                return valueAt(slot);
            }

            List<Draft> order = new ArrayList<>();
            ArrayDeque<Draft> pending = new ArrayDeque<>();
            pending.push(top);
            while (!pending.isEmpty()) {
                Draft draft = pending.pop();
                order.add(draft);
                if (draft.items != null) {
                    for (Draft child : draft.items.values()) {
                        pending.push(child);
                    }
                }
                if (draft.fields != null) {
                    for (Draft child : draft.fields.values()) {
                        pending.push(child);
                    }
                }
            }

            for (int i = order.size() - 1; i > 0; i--) {
                Draft draft = order.get(i);
                JqValue value = draft.build();
                if (draft.key != null) {
                    draft.parent.object.set(draft.key, value);
                } else {
                    draft.parent.array.set(draft.index, value);
                }
            }

            JqValue frozen = top.build();
            store(slot, frozen);
            return frozen;
        }
    }
}
