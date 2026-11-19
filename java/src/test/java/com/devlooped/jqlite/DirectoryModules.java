package com.devlooped.jqlite;

import com.devlooped.jqlite.binding.Modules.IModuleSource;
import com.devlooped.jqlite.binding.Modules.Lookup;
import com.devlooped.jqlite.binding.Modules.ModuleKind;
import com.devlooped.jqlite.values.JqValue;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * A module source over a directory, with the rule of spec/tests/README.md: the path p imported from
 * a file names p.jq (code) or p.json (data) in that file's directory, the main program's directory
 * being the root. Identities are paths relative to the root.
 */
public final class DirectoryModules implements IModuleSource {
    private final Path root;
    private final AtomicInteger reads = new AtomicInteger();

    public DirectoryModules(Path root) {
        this.root = root;
    }

    /** How many module files were read. */
    public int reads() {
        return reads.get();
    }

    @Override
    public Lookup tryResolve(String path, ModuleKind kind, JqValue metadata, String from) {
        String directory = "";
        if (from != null) {
            int slash = from.lastIndexOf('/');
            directory = slash < 0 ? "" : from.substring(0, slash + 1);
        }
        String identity = directory + path + (kind == ModuleKind.CODE ? ".jq" : ".json");
        return Files.isRegularFile(root.resolve(identity)) ? Lookup.found(identity) : Lookup.failed("module not found: " + path);
    }

    @Override
    public Lookup tryRead(String identity) {
        reads.incrementAndGet();
        try {
            return Lookup.found(Files.readString(root.resolve(identity), StandardCharsets.UTF_8));
        } catch (IOException e) {
            throw new UncheckedIOException(e);
        }
    }
}
