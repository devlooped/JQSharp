// The port of dotnet/Tests/Core/Runtime/NativeTableTests.cs.

import assert from "node:assert/strict";
import { test } from "node:test";
import * as NativeTable from "../../src/builtins/nativeTable.js";
import * as Natives from "../../src/builtins/natives.js";

// A native of the catalogue without an implementation is a defect of the build: the table
// refuses to load, and this test names it.
test("every native of the catalogue is implemented", () => {
  assert.deepEqual(
    Natives.All.filter((native) => !NativeTable.isImplemented(native)).map((native) => native.toString()),
    [],
  );
});
