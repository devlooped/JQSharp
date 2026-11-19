//! Prints a bound program, so that tests can state what every reference resolved to.
#![allow(dead_code)]

use jqlite::binding::bound as b;
use jqlite::binding::prelude::Prelude;
use jqlite::binding::{BoundProgram, Resolver};
use jqlite::syntax::ast::BinaryOp;
use jqlite::values::{JqValue, value_text};

/// Compiles a program without modules; panics when it does not bind.
pub fn compile(source: &str) -> BoundProgram {
    match Resolver::try_compile(source, true, None) {
        Ok(program) => program,
        Err(error) => panic!("{source} does not bind: {} at {}:{}", error.message, error.line, error.column),
    }
}

/// Binds a program that must fail and gives "message @line:column".
pub fn bind_error(source: &str) -> String {
    match Resolver::try_compile(source, true, None) {
        Ok(program) => panic!("{source} binds as {}", print_program(&program)),
        Err(error) => format!("{} @{}:{}", error.message, error.line, error.column),
    }
}

/// Parses and binds a program and prints its own functions and its body, separated by spaces.
pub fn bind(source: &str) -> String {
    print_program(&compile(source))
}

fn prelude_count() -> usize {
    Prelude::instance().functions.len()
}

/// Prints a bound program as an S-expression stating what every reference resolved to, in the
/// shapes of the AST printer, with: `$h.s` a variable (slot s of the frame h frames out),
/// `$$name` a host variable, `$ENV`; `name/n#i^h` a call of the program's function i (0 = its
/// first own function) defined h frames out, `prelude:name/n^h` a call of a prelude builtin,
/// `native:name/n` a native, `param^h.s` a closure parameter; `(break ^h)`; in patterns `$s`
/// stores to slot s and `$_` discards. The program's own functions come first as
/// `(def name/n#i body)`. Recursive: for the trees of ordinary tests only.
pub fn print_program(program: &BoundProgram) -> String {
    let mut parts = Vec::new();
    for index in prelude_count()..program.functions.len() {
        parts.push(format!("(def {} {})", name(program, index), print(&program.functions[index].body, program)));
    }
    parts.push(print(&program.body, program));
    parts.join(" ")
}

fn name(program: &BoundProgram, index: usize) -> String {
    let function = &program.functions[index];
    if index < prelude_count() { format!("prelude:{}", function.signature()) } else { format!("{}#{}", function.signature(), index - prelude_count()) }
}

fn optional(node: Option<&b::Node>, program: &BoundProgram) -> String {
    node.map_or("_".to_owned(), |node| print(node, program))
}

fn all(nodes: &[b::Node], program: &BoundProgram) -> String {
    nodes.iter().map(|node| format!(" {}", print(node, program))).collect()
}

fn quoted(text: &str) -> String {
    value_text::to_json(&JqValue::from_text(text))
}

pub fn print(node: &b::Node, program: &BoundProgram) -> String {
    match &node.kind {
        b::NodeKind::Identity => ".".to_owned(),
        b::NodeKind::Literal(value) => value_text::to_json(value),
        b::NodeKind::Template { format, texts, parts } => {
            let mut builder = "(str".to_owned();
            if let Some(format) = format {
                builder.push_str(&format!(" @{format}"));
            }
            for (i, part) in parts.iter().enumerate() {
                builder.push_str(&format!(" {} {}", quoted(&texts[i]), print(part, program)));
            }
            builder.push_str(&format!(" {})", quoted(&texts[texts.len() - 1])));
            builder
        }
        b::NodeKind::Format(name) => format!("@{name}"),
        b::NodeKind::Index { target, key } => format!("(index {} {})", print(target, program), print(key, program)),
        b::NodeKind::Slice { target, from, to } => {
            format!("(slice {} {} {})", print(target, program), optional(from.as_deref(), program), optional(to.as_deref(), program))
        }
        b::NodeKind::Iterate { target } => format!("(iter {})", print(target, program)),
        b::NodeKind::Variable { hops, slot } => format!("${hops}.{slot}"),
        b::NodeKind::HostVariable { name, .. } => format!("$${name}"),
        b::NodeKind::EnvVariable => "$ENV".to_owned(),
        b::NodeKind::CallFunction { function, hops, arguments, .. } => {
            if arguments.is_empty() {
                format!("{}^{hops}", name(program, *function))
            } else {
                format!("({}^{hops}{})", name(program, *function), all(arguments, program))
            }
        }
        b::NodeKind::CallParameter { hops, slot } => format!("param^{hops}.{slot}"),
        b::NodeKind::CallNative { native, arguments } => {
            if arguments.is_empty() {
                format!("native:{}", native.signature())
            } else {
                format!("(native:{}{})", native.signature(), all(arguments, program))
            }
        }
        b::NodeKind::Pipe { left, right } => format!("(| {} {})", print(left, program), print(right, program)),
        b::NodeKind::Comma { items } => format!("(,{})", all(items, program)),
        b::NodeKind::Binary { op, left, right } => format!("({} {} {})", symbol(*op), print(left, program), print(right, program)),
        b::NodeKind::And { left, right } => format!("(and {} {})", print(left, program), print(right, program)),
        b::NodeKind::Or { left, right } => format!("(or {} {})", print(left, program), print(right, program)),
        b::NodeKind::Alternative { left, right } => format!("(// {} {})", print(left, program), print(right, program)),
        b::NodeKind::Negate { operand } => format!("(neg {})", print(operand, program)),
        b::NodeKind::ArrayExpr { body } => match body {
            None => "(array)".to_owned(),
            Some(body) => format!("(array {})", print(body, program)),
        },
        b::NodeKind::ObjectExpr { entries } => {
            if entries.is_empty() {
                "(object)".to_owned()
            } else {
                let entries: Vec<String> =
                    entries.iter().map(|entry| format!("({} {})", print(&entry.key, program), optional(entry.value.as_ref(), program))).collect();
                format!("(object {})", entries.join(" "))
            }
        }
        b::NodeKind::If { condition, then, otherwise } => format!("(if {} {} {})", print(condition, program), print(then, program), print(otherwise, program)),
        b::NodeKind::Try { body, handler } => match handler {
            None => format!("(try {})", print(body, program)),
            Some(handler) => format!("(try {} {})", print(body, program), print(handler, program)),
        },
        b::NodeKind::Reduce { source, binder, init, update } => {
            format!("(reduce {} {} {} {})", print(source, program), print_binder(binder, program), print(init, program), print(update, program))
        }
        b::NodeKind::Foreach { source, binder, init, update, extract } => format!(
            "(foreach {} {} {} {} {})",
            print(source, program),
            print_binder(binder, program),
            print(init, program),
            print(update, program),
            print(extract, program)
        ),
        b::NodeKind::Bind { source, binder, body } => format!("(as {} {} {})", print(source, program), print_binder(binder, program), print(body, program)),
        b::NodeKind::Label { body } => format!("(label {})", print(body, program)),
        b::NodeKind::Break { hops } => format!("(break ^{hops})"),
    }
}

fn print_binder(binder: &b::Binder, program: &BoundProgram) -> String {
    let patterns: Vec<String> = binder.patterns.iter().map(|pattern| print_pattern(pattern, program)).collect();
    if patterns.len() == 1 { patterns[0].clone() } else { format!("(?// {})", patterns.join(" ")) }
}

fn slot(slot: Option<usize>) -> String {
    slot.map_or("$_".to_owned(), |slot| format!("${slot}"))
}

fn print_pattern(pattern: &b::Pattern, program: &BoundProgram) -> String {
    match pattern {
        b::Pattern::Variable { slot: variable } => slot(*variable),
        b::Pattern::Array { elements } => format!("[{}]", elements.iter().map(|element| print_pattern(element, program)).collect::<Vec<_>>().join(" ")),
        b::Pattern::Object { entries } => {
            let entries: Vec<String> = entries
                .iter()
                .map(|entry| {
                    let variable = if entry.slot.is_none() { "_".to_owned() } else { slot(entry.slot) };
                    let value = entry.value.as_ref().map_or("_".to_owned(), |value| print_pattern(value, program));
                    format!("({} {variable} {value})", print(&entry.key, program))
                })
                .collect();
            format!("{{{}}}", entries.join(" "))
        }
    }
}

fn symbol(op: BinaryOp) -> &'static str {
    match op {
        BinaryOp::Add => "+",
        BinaryOp::Subtract => "-",
        BinaryOp::Multiply => "*",
        BinaryOp::Divide => "/",
        BinaryOp::Modulo => "%",
        BinaryOp::Equal => "==",
        BinaryOp::NotEqual => "!=",
        BinaryOp::Less => "<",
        BinaryOp::LessEqual => "<=",
        BinaryOp::Greater => ">",
        BinaryOp::GreaterEqual => ">=",
    }
}
