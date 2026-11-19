package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.addClosure;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.binding.Bound.Node;
import com.devlooped.jqlite.runtime.Emit;
import com.devlooped.jqlite.runtime.Env;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.runtime.Path;
import com.devlooped.jqlite.runtime.Signal;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqArray;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqObject;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;
import com.devlooped.jqlite.values.ValueOps;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.List;

/** §9.5 and §8: {@code path}, {@code getpath}, {@code setpath}, {@code delpaths}. */
public final class PathNatives {
    // setpath's index limit (§11).
    private static final double MAX_INDEX = 536870911;

    private PathNatives() {
    }

    static void register() {
        addClosure("path", 1, PathNatives::pathOf);
        addClosure("getpath", 1, PathNatives::getPath);
        add("setpath", 2, (interpreter, input, arguments) -> trySetPath(input, arguments[0], arguments[1]));
        add("delpaths", 1, (interpreter, input, arguments) -> tryDeletePaths(input, arguments[0]));
    }

    // path(f): f in path mode from the empty path; each output must have a path.
    private static Signal pathOf(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        Path at = Path.of(path);
        return interpreter.eval(arguments[0], env, input, Path.ROOT, (value, valueAt) ->
            valueAt != null && valueAt.isTracked() ? interpreter.output(emit, valueAt.toValue(), at) : Interpreter.fail(Errors.invalidPathResult(value)));
    }

    // getpath(p): p in value mode; the output's path is the input's followed by p, even when absent.
    private static Signal getPath(Interpreter interpreter, Env env, Node[] arguments, JqValue input, Path path, Emit emit) {
        return interpreter.forEach(arguments[0], env, input, null, (keys, unused) -> {
            if (keys.kind() != JqKind.ARRAY) {
                return Interpreter.fail(Errors.pathNotArray());
            }
            JqArray steps = keys.asArray();
            JqValue value = input;
            for (int i = 0; i < steps.count(); i++) {
                Result next = ValueOps.tryIndex(value, steps.get(i));
                if (next instanceof Result.Err err) {
                    return Signal.error(err.error());
                }
                value = (JqValue) next;
            }

            return interpreter.output(emit, value, path != null && path.isTracked() ? path.append(steps) : path);
        });
    }

    /**
     * setpath(p; v) (§8.2): each container on the way is indexed first (so its errors come first),
     * then the containers are rebuilt from the innermost out, each copied one level.
     */
    public static Result trySetPath(JqValue root, JqValue keys, JqValue value) {
        if (keys.kind() != JqKind.ARRAY) {
            return fail(Errors.pathNotArray());
        }
        JqArray path = keys.asArray();
        JqValue[] containers = new JqValue[path.count()];
        JqValue current = root;
        for (int i = 0; i < path.count(); i++) {
            containers[i] = current;
            Result next = ValueOps.tryIndex(current, path.get(i));
            if (next instanceof Result.Err) {
                return next;
            }
            current = (JqValue) next;
        }

        for (int i = path.count() - 1; i >= 0; i--) {
            Result set = trySet(containers[i], path.get(i), value);
            if (set instanceof Result.Err) {
                return set;
            }
            value = (JqValue) set;
        }

        return value;
    }

    // One level of setpath: the table of §8.2.
    static Result trySet(JqValue container, JqValue key, JqValue value) {
        JqKind kind = container.kind();
        switch (key.kind()) {
            case STRING:
                if (kind == JqKind.OBJECT || kind == JqKind.NULL) {
                    JqObject obj = kind == JqKind.NULL ? JqObject.EMPTY : container.asObject();
                    return obj.set(key.asString(), value);
                }
                break;
            case NUMBER:
                if (kind == JqKind.ARRAY || kind == JqKind.NULL) {
                    JqArray array = kind == JqKind.NULL ? JqArray.EMPTY : container.asArray();
                    double number = key.asNumber();
                    if (Double.isNaN(number)) {
                        return fail(Errors.nanIndex());
                    }
                    double index = number < 0 ? Math.ceil(number) : Math.floor(number);
                    if (index < 0) {
                        index += array.count();
                    }
                    if (index < 0) {
                        return fail(Errors.negativeIndex());
                    }
                    if (index > MAX_INDEX) {
                        return fail(Errors.indexTooLarge());
                    }
                    return array.setItem((int) index, value);
                }
                break;
            case OBJECT:
                if (kind == JqKind.ARRAY || kind == JqKind.NULL) {
                    return trySetSlice(kind == JqKind.NULL ? JqArray.EMPTY : container.asArray(), key.asObject(), value);
                }
                if (kind == JqKind.STRING) {
                    return fail(Errors.cannotUpdateStringSlices());
                }
                break;
            default:
                break;
        }

        return fail(Errors.cannotUpdateField(key, container));
    }

    // .[s:e] = v on an array: v (an array) replaces the elements the bounds select.
    private static Result trySetSlice(JqArray array, JqObject slice, JqValue value) {
        JqValue start = slice.get("start");
        JqValue end = slice.get("end");
        if (start == null || end == null) {
            return fail(Errors.sliceIndicesNotIntegers());
        }
        int[] range = new int[2];
        JqValue error = ValueOps.trySliceRange(array.count(), start, end, range);
        if (error != null) {
            return fail(error);
        }
        if (value.kind() != JqKind.ARRAY) {
            return fail(Errors.sliceNeedsArray());
        }
        return array.slice(0, range[0]).concat(value.asArray()).concat(array.slice(range[1], array.count()));
    }

    /** delpaths(ps) (§8.2): the paths sorted, then deleted level by level. */
    public static Result tryDeletePaths(JqValue root, JqValue paths) {
        if (paths.kind() != JqKind.ARRAY) {
            return fail(Errors.pathsNotArray());
        }
        JqArray list = paths.asArray();
        for (int i = 0; i < list.count(); i++) {
            if (list.get(i).kind() != JqKind.ARRAY) {
                return fail(Errors.pathElementNotArray(list.get(i)));
            }
            if (list.get(i).asArray().count() == 0) {
                return JqValue.NULL;
            }
        }

        JqArray sorted = ValueOps.sort(list);
        return tryDelete(root, sorted, 0, sorted.count(), 0);
    }

    // One container being rebuilt by tryDelete: the paths [next, to) still to look at, at depth; the
    // keys deleted at this level; and the key of the child being rebuilt below it, if any.
    private static final class Level {
        JqValue value;
        int next;
        final int to;
        final int depth;
        final List<JqValue> deleted = new ArrayList<>();
        JqValue childKey;
        int childEnd;

        Level(JqValue value, int from, int to, int depth) {
            this.value = value;
            this.next = from;
            this.to = to;
            this.depth = depth;
        }
    }

    // Deletes paths[from, to) (sorted, all longer than depth) from value at the given depth: groups
    // by the key at that depth; a group holding the path that ends there deletes the key, the
    // others go into the child first. All the keys of one level are removed at once. The levels
    // being rebuilt are on an explicit stack, so a path of any length is no recursion.
    private static Result tryDelete(JqValue value, JqArray paths, int from, int to, int depth) {
        ArrayDeque<Level> levels = new ArrayDeque<>();
        levels.push(new Level(value, from, to, depth));
        while (true) {
            Level level = levels.peek();
            boolean descended = false;
            while (level.value.kind() != JqKind.NULL && level.next < level.to && !descended) {
                JqValue key = paths.get(level.next).asArray().get(level.depth);
                int end = level.next + 1;
                boolean ends = paths.get(level.next).asArray().count() == level.depth + 1;
                for (; end < level.to && ValueOps.compare(paths.get(end).asArray().get(level.depth), key) == 0; end++) {
                    ends |= paths.get(end).asArray().count() == level.depth + 1;
                }

                if (ends) {
                    level.deleted.add(key);
                    level.next = end;
                    continue;
                }

                Result child = ValueOps.tryIndex(level.value, key);
                if (child instanceof Result.Err) {
                    return child;
                }
                if (((JqValue) child).kind() == JqKind.NULL) {
                    level.next = end;
                    continue;
                }

                level.childKey = key;
                level.childEnd = end;
                levels.push(new Level((JqValue) child, level.next, end, level.depth + 1));
                descended = true;
            }

            if (descended) {
                continue;
            }

            // This level is done: remove its keys, then put it back into its parent.
            Result done = tryRemove(level.value, level.deleted);
            if (done instanceof Result.Err) {
                return done;
            }
            levels.pop();
            if (levels.isEmpty()) {
                return done;
            }
            Level parent = levels.peek();
            Result set = trySet(parent.value, parent.childKey, (JqValue) done);
            if (set instanceof Result.Err) {
                return set;
            }
            parent.value = (JqValue) set;
            parent.next = parent.childEnd;
        }
    }

    // Removes keys from a container, all relative to the container as it is.
    private static Result tryRemove(JqValue container, List<JqValue> keys) {
        if (keys.isEmpty() || container.kind() == JqKind.NULL) {
            return container;
        }
        if (container.kind() == JqKind.OBJECT) {
            JqObject obj = container.asObject();
            for (JqValue key : keys) {
                if (key.kind() != JqKind.STRING) {
                    return fail(Errors.cannotDeleteField(key));
                }
                obj = obj.remove(key.asString());
            }

            return obj;
        }

        if (container.kind() != JqKind.ARRAY) {
            return fail(Errors.cannotDeleteFrom(container));
        }

        JqArray array = container.asArray();
        boolean[] removed = new boolean[array.count()];
        for (JqValue key : keys) {
            if (key.kind() == JqKind.NUMBER) {
                double number = key.asNumber();
                if (Double.isNaN(number)) {
                    continue;
                }
                double index = number < 0 ? Math.ceil(number) : Math.floor(number);
                if (index < 0) {
                    index += array.count();
                }
                if (index >= 0 && index < array.count()) {
                    removed[(int) index] = true;
                }
            } else if (key.kind() == JqKind.OBJECT) {
                JqObject slice = key.asObject();
                JqValue start = slice.get("start");
                JqValue end = slice.get("end");
                if (start == null || end == null) {
                    return fail(Errors.sliceIndicesNotIntegers());
                }
                int[] range = new int[2];
                JqValue error = ValueOps.trySliceRange(array.count(), start, end, range);
                if (error != null) {
                    return fail(error);
                }
                for (int i = range[0]; i < range[1]; i++) {
                    removed[i] = true;
                }
            } else {
                return fail(Errors.cannotDeleteElement(key));
            }
        }

        List<JqValue> kept = new ArrayList<>(array.count());
        for (int i = 0; i < array.count(); i++) {
            if (!removed[i]) {
                kept.add(array.get(i));
            }
        }

        return JqArray.fromOwned(kept.toArray(new JqValue[0]));
    }
}
