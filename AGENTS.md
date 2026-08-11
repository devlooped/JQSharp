# AGENTS.md

## Project overview

JQSharp is a pure C# implementation of the [jq](https://jqlang.org) filter language
operating on `System.Text.Json.JsonElement` values. Public entry points are `Jq.Parse`,
`Jq.Evaluate` / `Jq.EvaluateAsync`, and `JqExpression`.

## Architecture notes

- **Parse phase**: `JqParser` builds an immutable `JqFilter` AST wrapped by `JqExpression`.
- **Evaluate phase**: filters thread a `JsonElement` input and a `JqEnvironment` (immutable
  variable / filter-arg bindings) and yield zero or more `JsonElement` outputs.
- **`$ENV`**: special-cased in `VariableFilter` and always materializes process environment
  variables at evaluation time.
- **External variables** (see below): host-supplied bindings injected into `JqEnvironment`
  before evaluation, analogous to jq `--arg` / `--argjson`.

## External variable bindings

`JqExpression.Evaluate(input, variables)` and matching `Jq.Evaluate` / `EvaluateAsync`
overloads accept `IReadOnlyDictionary<string, JsonElement>?`.

Rules:

- Dictionary **keys must not** start with `$`; `"root"` binds `$root`.
- Keys must be valid jq identifiers (`[A-Za-z_][A-Za-z0-9_]*`).
- Invalid keys throw `ArgumentException` before evaluation.
- Values are cloned into the environment so caller document lifetime is decoupled.
- Simple `$name` references are allowed at parse time even if unbound; missing bindings
  throw `JqException` (`$name is not defined`) at evaluation time.
- Module-qualified names (`$alias::name`) still require a successful import/definition at
  parse time.
- In-expression `as $name` bindings shadow external variables of the same name.

Implementation touchpoints:

- `JqEnvironment.FromVariables` / `ValidateVariableName`
- `JqExpression.Evaluate(..., variables)`
- `Jq.Evaluate` / `EvaluateAsync` overloads with `variables`
- `JqParser.ParsePrimary` — unbound simple `$ident` → `VariableFilter`
