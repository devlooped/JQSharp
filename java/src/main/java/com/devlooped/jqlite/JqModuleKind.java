package com.devlooped.jqlite;

/** Whether an {@code import} asks for a code module ({@code .jq}) or a data module ({@code import "p" as $name;}, JSON values). */
public enum JqModuleKind {
    /** A module of jq definitions. */
    CODE,

    /** A sequence of JSON values, bound as an array. */
    DATA,
}
