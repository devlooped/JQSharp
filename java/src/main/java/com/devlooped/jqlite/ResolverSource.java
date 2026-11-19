package com.devlooped.jqlite;

import com.devlooped.jqlite.binding.BindingMessages;
import com.devlooped.jqlite.binding.Modules.IModuleSource;
import com.devlooped.jqlite.binding.Modules.Lookup;
import com.devlooped.jqlite.binding.Modules.ModuleKind;
import com.devlooped.jqlite.values.JqValue;
import java.io.IOException;
import java.io.Reader;
import java.io.UncheckedIOException;

/**
 * A public {@link JqResolver} as the module source of the core (spec §9.11, §12): a resolver's
 * {@link JqException} or I/O exception becomes the binding error.
 */
final class ResolverSource implements IModuleSource {
    private final JqResolver resolver;

    ResolverSource(JqResolver resolver) {
        this.resolver = resolver;
    }

    @Override
    public Lookup tryResolve(String path, ModuleKind kind, JqValue metadata, String from) {
        String identity;
        try {
            identity = resolver.getCanonicalPath(path, kind == ModuleKind.DATA ? JqModuleKind.DATA : JqModuleKind.CODE, metadata, from);
        } catch (JqException | IOException | UncheckedIOException | SecurityException e) {
            return Lookup.failed(String.valueOf(e.getMessage()));
        }

        return identity == null || identity.isEmpty() ? Lookup.failed(BindingMessages.moduleNotFound(path)) : Lookup.found(identity);
    }

    @Override
    public Lookup tryRead(String identity) {
        try (Reader reader = resolver.open(identity)) {
            StringBuilder text = new StringBuilder();
            char[] buffer = new char[8192];
            for (int read; (read = reader.read(buffer)) >= 0;) {
                text.append(buffer, 0, read);
            }
            return Lookup.found(text.toString());
        } catch (JqException | IOException | UncheckedIOException | SecurityException e) {
            return Lookup.failed(String.valueOf(e.getMessage()));
        }
    }
}
