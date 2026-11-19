package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;
import java.io.IOException;
import java.io.Reader;

/**
 * Finds the modules of {@code import} and {@code include} directives and of {@code modulemeta}: maps
 * a path, as written, to a canonical path (the module's identity), then opens it.
 *
 * <p>A module is read once per canonical path while an expression is parsed. {@code modulemeta}
 * calls the resolver while expressions are evaluated, possibly from several threads at once:
 * implementations must allow that.
 */
public abstract class JqResolver {
    /**
     * The canonical path of the module {@code path} of the given kind, imported with {@code metadata}
     * (the directive's metadata object, or null) from the module at {@code fromPath} (a canonical
     * path, or null for the expression itself); null when there is no such module. The same canonical
     * path means the same module.
     */
    public abstract String getCanonicalPath(String path, JqModuleKind kind, JqValue metadata, String fromPath) throws IOException;

    /** Opens the module at a canonical path that {@link #getCanonicalPath} returned. */
    public abstract Reader open(String canonicalPath) throws IOException;
}
