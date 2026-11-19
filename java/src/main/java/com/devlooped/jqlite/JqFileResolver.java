package com.devlooped.jqlite;

import com.devlooped.jqlite.values.JqValue;
import java.io.IOException;
import java.io.Reader;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/**
 * A {@link JqResolver} over the file system: a path is relative to the directory of the importing
 * module, or to {@link #baseDirectory} for the expression itself, and gets the extension {@code .jq}
 * (code) or {@code .json} (data) when it has none. The canonical path is the full file path. Safe to
 * use from several threads.
 */
public class JqFileResolver extends JqResolver {
    private final Path baseDirectory;

    /** A resolver whose expression directives are relative to {@code baseDirectory}. */
    public JqFileResolver(Path baseDirectory) {
        this.baseDirectory = baseDirectory.toAbsolutePath().normalize();
    }

    /** The directory that the expression's own directives are relative to. */
    public Path baseDirectory() {
        return baseDirectory;
    }

    @Override
    public String getCanonicalPath(String path, JqModuleKind kind, JqValue metadata, String fromPath) {
        Path directory = fromPath == null ? baseDirectory : Path.of(fromPath).getParent();
        if (directory == null) {
            directory = baseDirectory;
        }
        Path file = directory.resolve(path).toAbsolutePath().normalize();
        String name = file.getFileName() == null ? "" : file.getFileName().toString();
        if (name.lastIndexOf('.') <= 0) {
            file = file.resolveSibling(name + (kind == JqModuleKind.DATA ? ".json" : ".jq"));
        }
        return Files.isRegularFile(file) ? file.toString() : null;
    }

    @Override
    public Reader open(String canonicalPath) throws IOException {
        return Files.newBufferedReader(Path.of(canonicalPath), StandardCharsets.UTF_8);
    }
}
