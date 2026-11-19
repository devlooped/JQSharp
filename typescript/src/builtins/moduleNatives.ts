// modulemeta/0 (§9.11), the native of the modules group: a port without the group (see
// binding/modules.ts) leaves this file out and does not list it.

import { ModuleError } from "../binding/modules.js";
import * as BindingMessages from "../binding/resolver.js";
import type { Interpreter } from "../runtime/interpreter.js";
import * as CodePoints from "../values/codePoints.js";
import * as Errors from "../values/errors.js";
import { Fail, type JqValue } from "../values/jqValue.js";
import { add } from "./nativeTable.js";

export function register(): void {
  add("modulemeta", 0, modulemeta);
}

// The description of the module the input names, resolved as from the main program with the
// source the program was bound with: what the loader recorded when the program loaded it.
function modulemeta(interpreter: Interpreter, input: JqValue): JqValue | Fail {
  if (typeof input !== "string") return new Fail(Errors.modulemetaNotString);
  const modules = interpreter.program.modules;
  if (modules === null) return new Fail(BindingMessages.moduleNotFound(input));
  const description = modules.tryDescribe(input);
  return description instanceof ModuleError ? new Fail(CodePoints.sanitize(description.message)) : description;
}
