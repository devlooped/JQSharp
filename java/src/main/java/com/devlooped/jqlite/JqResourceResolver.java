package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.Reader;
import java.nio.charset.StandardCharsets;

/**
 * A {@link JqResolver} over the resources of a class loader. A resource name is the {@link #prefix},
 * a slash and the path, plus {@code .jq} (code) or {@code .json} (data) unless the path ends with
 * one; a directive in a module is relative to that module's directory. With the prefix
 * {@code myapp/filters}, {@code include "utils"} is {@code myapp/filters/utils.jq}, and
 * {@code include "sub/helper"} in it is {@code myapp/filters/sub/helper.jq}. Safe to use from several
 * threads.
 */
public class JqResourceResolver extends JqResolver {
    private final ClassLoader loader;
    private final String prefix;

    /** A resolver over the resources of {@code loader} under {@code prefix} (slash-separated, no final slash; may be empty). */
    public JqResourceResolver(ClassLoader loader, String prefix) {
        if (loader == null || prefix == null) {
            throw new NullPointerException("loader and prefix are required");
        }
        this.loader = loader;
        this.prefix = prefix;
    }

    /** The slash-separated prefix of the resource names; may be empty. */
    public String prefix() {
        return prefix;
    }

    @Override
    public String getCanonicalPath(String path, JqModuleKind kind, JqValue metadata, String fromPath) {
        String directory = prefix;
        if (fromPath != null) {
            int slash = fromPath.lastIndexOf('/');
            directory = slash >= 0 ? fromPath.substring(0, slash) : "";
        }

        String normalized = path.replace('\\', '/');
        String name = directory.isEmpty() ? normalized : directory + "/" + normalized;
        String suffix = kind == JqModuleKind.DATA ? ".json" : ".jq";
        if (!name.endsWith(suffix)) {
            name += suffix;
        }
        return loader.getResource(name) == null ? null : name;
    }

    @Override
    public Reader open(String canonicalPath) throws IOException {
        InputStream stream = loader.getResourceAsStream(canonicalPath);
        if (stream == null) {
            throw new JqException("module not found: " + canonicalPath);
        }
        return new InputStreamReader(stream, StandardCharsets.UTF_8);
    }
}
