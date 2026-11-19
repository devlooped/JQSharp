package com.devlooped.jqlite.runtime;

import com.devlooped.jqlite.values.JqValue;

/** Receives one output of an evaluation; returns null to go on, or the signal that stops it. (In the reference, this delegate shares Interpreter.cs.) */
@FunctionalInterface
public interface Emit {
    Signal emit(JqValue value, Path path);
}
