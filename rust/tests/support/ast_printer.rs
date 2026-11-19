//! Printers of the AST and of the bound program, so that tests can state structure.
#![allow(dead_code)]

use jqlite::syntax::ast::{BinaryOp, Definition, Node, NodeKind, Pattern, PatternKind, Program};
use jqlite::syntax::parser::Parser;
use jqlite::values::{JqValue, value_text};

/// Parses a program and prints its top-level definitions and body, separated by spaces.
pub fn parse(source: &str) -> String {
    match Parser::try_parse(source) {
        Ok(program) => print_program(&program),
        Err(error) => panic!("'{source}' does not parse: {} at {}:{}", error.message, error.line, error.column),
    }
}

/// Parses a program that must fail and gives "message @line:column".
pub fn parse_error(source: &str) -> String {
    match Parser::try_parse(source) {
        Ok(program) => panic!("'{source}' parses as {}", print_program(&program)),
        Err(error) => format!("{} @{}:{}", error.message, error.line, error.column),
    }
}

/// Prints an AST as an S-expression: `.`, literals as JSON, `$x`, a call without arguments as its
/// name, `(f a b)`, `(| a b)`, `(, a b)`, `(+ a b)`, `(neg a)`, `(index t k)`, `(slice t from to)`,
/// `(iter t)`, `(str @fmt "a" e "b")`, `(object (k v) (k _))`, `(as source pattern body)`, …; a
/// missing child is `_`. Recursive: for the trees of ordinary tests only.
pub fn print_program(program: &Program) -> String {
    let mut parts = Vec::new();
    if let Some(metadata) = &program.metadata {
        parts.push(format!("(module {})", value_text::to_json(metadata)));
    }
    for import in &program.imports {
        let alias = match &import.alias {
            None => String::new(),
            Some(alias) if import.is_data => format!(" ${alias}"),
            Some(alias) => format!(" {alias}"),
        };
        let meta = import.metadata.as_ref().map_or(String::new(), |value| format!(" {}", value_text::to_json(value)));
        parts.push(format!("({} \"{}\"{alias}{meta})", if import.alias.is_none() { "include" } else { "import" }, import.path));
    }
    for definition in &program.definitions {
        parts.push(format!("(def {} {})", signature(definition), print(&definition.body)));
    }
    if let Some(body) = &program.body {
        parts.push(print(body));
    }
    parts.join(" ")
}

fn optional(node: Option<&Node>) -> String {
    node.map_or("_".to_owned(), print)
}

fn quoted(text: &str) -> String {
    value_text::to_json(&JqValue::from_text(text))
}

pub fn print(node: &Node) -> String {
    match &node.kind {
        NodeKind::Identity => ".".to_owned(),
        NodeKind::Literal(value) => value_text::to_json(value),
        NodeKind::Template { format, texts, parts } => {
            let mut builder = "(str".to_owned();
            if let Some(format) = format {
                builder.push_str(&format!(" @{format}"));
            }
            for (i, part) in parts.iter().enumerate() {
                builder.push_str(&format!(" {} {}", quoted(&texts[i]), print(part)));
            }
            builder.push_str(&format!(" {})", quoted(&texts[texts.len() - 1])));
            builder
        }
        NodeKind::Format(name) => format!("@{name}"),
        NodeKind::Index { target, key } => format!("(index {} {})", print(target), print(key)),
        NodeKind::Slice { target, from, to } => format!("(slice {} {} {})", print(target), optional(from.as_deref()), optional(to.as_deref())),
        NodeKind::Iterate { target } => format!("(iter {})", print(target)),
        NodeKind::Variable(name) => format!("${name}"),
        NodeKind::Call { name, arguments } => {
            if arguments.is_empty() {
                name.clone()
            } else {
                format!("({name} {})", arguments.iter().map(print).collect::<Vec<_>>().join(" "))
            }
        }
        NodeKind::Pipe { left, right } => format!("(| {} {})", print(left), print(right)),
        NodeKind::Comma { left, right } => format!("(, {} {})", print(left), print(right)),
        NodeKind::Binary { op, left, right } => format!("({} {} {})", symbol(*op), print(left), print(right)),
        NodeKind::And { left, right } => format!("(and {} {})", print(left), print(right)),
        NodeKind::Or { left, right } => format!("(or {} {})", print(left), print(right)),
        NodeKind::Alternative { left, right } => format!("(// {} {})", print(left), print(right)),
        NodeKind::Negate { operand } => format!("(neg {})", print(operand)),
        NodeKind::ArrayExpr { body } => match body {
            None => "(array)".to_owned(),
            Some(body) => format!("(array {})", print(body)),
        },
        NodeKind::ObjectExpr { entries } => {
            if entries.is_empty() {
                "(object)".to_owned()
            } else {
                let entries: Vec<String> = entries.iter().map(|entry| format!("({} {})", print(&entry.key), optional(entry.value.as_ref()))).collect();
                format!("(object {})", entries.join(" "))
            }
        }
        NodeKind::If { condition, then, otherwise } => format!("(if {} {} {})", print(condition), print(then), print(otherwise)),
        NodeKind::Try { body, handler } => match handler {
            None => format!("(try {})", print(body)),
            Some(handler) => format!("(try {} {})", print(body), print(handler)),
        },
        NodeKind::Reduce { source, patterns, init, update } => {
            format!("(reduce {} {} {} {})", print(source), print_patterns(patterns), print(init), print(update))
        }
        NodeKind::Foreach { source, patterns, init, update, extract } => {
            format!("(foreach {} {} {} {} {})", print(source), print_patterns(patterns), print(init), print(update), print(extract))
        }
        NodeKind::Bind { source, patterns, body } => format!("(as {} {} {})", print(source), print_patterns(patterns), print(body)),
        NodeKind::FuncDef { definition, rest } => format!("(def {} {} {})", signature(definition), print(&definition.body), print(rest)),
        NodeKind::Label { name, body } => format!("(label ${name} {})", print(body)),
        NodeKind::Break { name } => format!("(break ${name})"),
    }
}

fn signature(definition: &Definition) -> String {
    if definition.params.is_empty() {
        return definition.name.clone();
    }
    let params: Vec<String> = definition.params.iter().map(|param| format!("{}{}", if param.is_variable { "$" } else { "" }, param.name)).collect();
    format!("{}({})", definition.name, params.join(";"))
}

fn print_patterns(patterns: &[Pattern]) -> String {
    if patterns.len() == 1 {
        return print_pattern(&patterns[0]);
    }
    format!("(?// {})", patterns.iter().map(print_pattern).collect::<Vec<_>>().join(" "))
}

fn print_pattern(pattern: &Pattern) -> String {
    match &pattern.kind {
        PatternKind::Variable(name) => format!("${name}"),
        PatternKind::Array(elements) => format!("[{}]", elements.iter().map(print_pattern).collect::<Vec<_>>().join(" ")),
        PatternKind::Object(entries) => {
            let entries: Vec<String> = entries
                .iter()
                .map(|entry| {
                    let variable = entry.variable.as_ref().map_or("_".to_owned(), |name| format!("${name}"));
                    format!("({} {variable} {})", print(&entry.key), entry.value.as_ref().map_or("_".to_owned(), print_pattern))
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
