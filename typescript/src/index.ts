// jqlite: the public API. The engine is under values/, syntax/, binding/, runtime/ and
// builtins/ (the port of dotnet/JQLite/Core); this boundary is the only part that knows the
// host's JSON values and throws the host's errors.

export { Jq } from "./jq.js";
export { JqExpression, type JqVariables } from "./jqExpression.js";
export type { JqClock, JqOptions } from "./jqOptions.js";
export type { JqModuleKind, JqResolver } from "./jqResolver.js";
export { JqError, JqHaltError, JqParseError } from "./jqError.js";
export type { JsonInput, JsonValue } from "./json/jsonValueAdapter.js";
