package com.devlooped.jqlite.builtins;

import static com.devlooped.jqlite.builtins.NativeTable.add;
import static com.devlooped.jqlite.builtins.NativeTable.fail;

import com.devlooped.jqlite.binding.BindingMessages;
import com.devlooped.jqlite.binding.Modules.ModuleTable;
import com.devlooped.jqlite.runtime.Interpreter;
import com.devlooped.jqlite.values.Errors;
import com.devlooped.jqlite.values.JqKind;
import com.devlooped.jqlite.values.JqValue;
import com.devlooped.jqlite.values.Result;

/**
 * {@code modulemeta/0} (§9.11), the native of the modules group: a port without the group (see
 * binding/Modules.java) leaves this file out and does not list it.
 */
final class ModuleNatives {
    private ModuleNatives() {
    }

    static void register() {
        add("modulemeta", 0, ModuleNatives::modulemeta);
    }

    // The description of the module the input names, resolved as from the main program with the
    // source the program was bound with: what the loader recorded when the program loaded it.
    private static Result modulemeta(Interpreter interpreter, JqValue input, JqValue[] arguments) {
        if (input.kind() != JqKind.STRING) {
            return fail(Errors.modulemetaNotString());
        }
        String path = input.asString();
        ModuleTable modules = interpreter.program().modules();
        if (modules == null) {
            return fail(JqValue.fromString(BindingMessages.moduleNotFound(path)));
        }
        return modules.tryDescribe(path);
    }
}
