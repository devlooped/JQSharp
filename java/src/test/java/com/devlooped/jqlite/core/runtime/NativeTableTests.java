package com.devlooped.jqlite.core.runtime;

import static org.junit.jupiter.api.Assertions.assertEquals;

import com.devlooped.jqlite.builtins.NativeTable;
import com.devlooped.jqlite.builtins.Natives;
import java.util.List;
import org.junit.jupiter.api.Test;

class NativeTableTests {
    // A native of the catalogue without an implementation is a defect of the build: the table
    // refuses to load, and this test names it.
    @Test
    void everyNativeOfTheCatalogueIsImplemented() {
        assertEquals(List.of(), Natives.ALL.stream().filter(n -> !NativeTable.isImplemented(n)).map(Object::toString).toList());
    }
}
