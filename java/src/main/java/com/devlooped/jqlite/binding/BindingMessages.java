package com.devlooped.jqlite.binding;

/** The messages of the binding layer; the first three are fixed by spec §10.2. (In the reference, this class shares Resolver.cs.) */
public final class BindingMessages {
    private BindingMessages() {
    }

    public static String notDefined(String name, int arity) {
        return name + "/" + arity + " is not defined";
    }

    public static String labelNotDefined(String name) {
        return "$*label-" + name + " is not defined";
    }

    public static String variableNotDefined(String name) {
        return "$" + name + " is not defined";
    }

    // The modules group (Modules.java); the texts are jq's.
    public static String moduleNotFound(String path) {
        return "module not found: " + path;
    }

    public static String circularImport(String identity) {
        return "circular import of " + identity;
    }

    public static String invalidData(String identity, String reason) {
        return "error loading data file " + identity + ": " + reason;
    }

    /** An error of a module, with where it is: {@code … at <identity>, line L, column C}. */
    public static String inModule(String message, String identity, int line, int column) {
        return message + " at " + identity + ", line " + line + ", column " + column;
    }
}
