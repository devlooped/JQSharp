package com.devlooped.jqlite;

import com.devlooped.jqlite.binding.Modules.IModuleSource;
import com.devlooped.jqlite.binding.Modules.Lookup;
import com.devlooped.jqlite.binding.Modules.ModuleKind;
import com.devlooped.jqlite.values.JqValue;
import java.util.Map;

/** A module source over a map of identities and texts; a path is its identity without the extension. */
public final class MemoryModules implements IModuleSource {
    private final Map<String, String> modules;

    public MemoryModules(Map<String, String> modules) {
        this.modules = modules;
    }

    @Override
    public Lookup tryResolve(String path, ModuleKind kind, JqValue metadata, String from) {
        String identity = path + (kind == ModuleKind.CODE ? ".jq" : ".json");
        return modules.containsKey(identity) ? Lookup.found(identity) : Lookup.failed("module not found: " + path);
    }

    @Override
    public Lookup tryRead(String identity) {
        String text = modules.get(identity);
        return text != null ? Lookup.found(text) : Lookup.failed("module not found: " + identity);
    }
}
