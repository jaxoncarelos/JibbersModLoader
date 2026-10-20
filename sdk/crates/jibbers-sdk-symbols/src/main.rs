//! Offline tool for the symbol catalogue (`crates/jibbers-mod-sdk/symbols.json`). Runs nothing in
//! the game: it reads dump files written in-game by the SDK runtime's `sdk.symbols.dump` command.
//!
//! ```text
//! sdk-symbols lint [--catalogue <extra.json>]...
//!     Checks the built-in catalogue (plus extra ones) for mistakes that need no game.
//!
//! sdk-symbols resolve <dump.json> [--catalogue <extra.json>]... [--json]
//!     What every symbol resolves to in that dump: class, real member names, or why it failed.
//!     Exits 1 if any symbol fails.
//!
//! sdk-symbols diff <old dump.json> <new dump.json> [--catalogue <extra.json>]...
//!     Per symbol, what it resolved to on each build: same, renamed, broke, fixed, missing.
//!     Exits 1 if anything broke.
//!
//! sdk-symbols find <dump.json> <text> [--limit <n>]
//!     Classes whose name, parent, or any field type or method signature contains <text>. For
//!     writing new rules ("which classes have a Terrain[] field?").
//!
//! sdk-symbols show <dump.json> <class full name>
//!     One class's fields and methods, as the matcher sees them.
//!
//! (C# bindings and the docs data come from the C# generator: sdk/dotnet/Jibbers.Sdk.Generator.)
//!
//! sdk-symbols docs [--dump <dump.json>] [--out <file>]
//!     The symbol reference (docs/SYMBOLS-REFERENCE.md); with a dump, includes the real names on
//!     that build.
//! ```

use jibbers_mod_sdk::shape::{self, Catalogue, ClassInfo, Failure, MemberRule, Resolution, Snapshot};
use jibbers_mod_sdk::symbols::CATALOGUE_JSON;
use std::fmt::Write;
use std::path::Path;
use std::process::ExitCode;

type R<T> = Result<T, String>;

fn read_json<T: serde::de::DeserializeOwned>(path: &str) -> R<T> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{path}: {e}"))?;
    serde_json::from_str(text.trim_start_matches('\u{feff}')).map_err(|e| format!("{path}: {e}"))
}

/// The built-in catalogue plus every `--catalogue` file in `args` (removed from `args`).
fn catalogue(args: &mut Vec<String>) -> R<Catalogue> {
    let mut cat: Catalogue = serde_json::from_str(CATALOGUE_JSON).map_err(|e| format!("built-in symbols.json: {e}"))?;
    while let Some(i) = args.iter().position(|a| a == "--catalogue") {
        let path = args.get(i + 1).cloned().ok_or("--catalogue needs a file")?;
        args.drain(i..i + 2);
        let extra: Catalogue = read_json(&path)?;
        cat.symbols.extend(extra.symbols);
    }
    Ok(cat)
}

fn take_opt(args: &mut Vec<String>, name: &str) -> R<Option<String>> {
    match args.iter().position(|a| a == name) {
        Some(i) => {
            let v = args.get(i + 1).cloned().ok_or(format!("{name} needs a value"))?;
            args.drain(i..i + 2);
            Ok(Some(v))
        }
        None => Ok(None),
    }
}

fn take_flag(args: &mut Vec<String>, name: &str) -> bool {
    let had = args.iter().any(|a| a == name);
    args.retain(|a| a != name);
    had
}

fn failure_text(f: &Failure) -> String {
    match f {
        Failure::NotFound => "no class matches".into(),
        Failure::Ambiguous { candidates } => format!("ambiguous: {}", candidates.join(", ")),
        Failure::Members { class, errors } => {
            format!("class {class}, but {}", errors.iter().map(|(m, e)| format!("{m}: {e}")).collect::<Vec<_>>().join("; "))
        }
        Failure::Dependency { id } => format!("needs @{id}, which did not resolve"),
        Failure::UnknownSymbol => "unknown symbol".into(),
        Failure::Cycle => "rules refer to each other in a cycle".into(),
    }
}

fn resolution_json(cat: &Catalogue, snap: &Snapshot, res: &Resolution) -> serde_json::Value {
    let mut out = serde_json::Map::new();
    for s in &cat.symbols {
        let v = match res.ok.get(&s.id) {
            Some(r) => {
                let members: serde_json::Map<_, _> = r.members.iter().map(|(n, m)| (n.clone(), serde_json::json!(shape::member_name(snap, m)))).collect();
                serde_json::json!({"class": snap.classes[r.class].full_name, "members": members})
            }
            None => serde_json::json!({"error": res.failed.get(&s.id)}),
        };
        out.insert(s.id.clone(), v);
    }
    serde_json::json!({"game_build": snap.game_build, "symbols": out})
}

fn cmd_lint(mut args: Vec<String>) -> R<bool> {
    let cat = catalogue(&mut args)?;
    let errs = shape::lint(&cat);
    for e in &errs {
        println!("{e}");
    }
    println!("{} symbols, {} problems", cat.symbols.len(), errs.len());
    Ok(errs.is_empty())
}

fn cmd_resolve(mut args: Vec<String>) -> R<bool> {
    let cat = catalogue(&mut args)?;
    let as_json = take_flag(&mut args, "--json");
    let [dump] = args.as_slice() else { return Err("usage: resolve <dump.json>".into()) };
    let snap: Snapshot = read_json(dump)?;
    let res = shape::resolve(&cat, &snap);
    if as_json {
        println!("{}", serde_json::to_string_pretty(&resolution_json(&cat, &snap, &res)).unwrap());
    } else {
        for s in &cat.symbols {
            match res.ok.get(&s.id) {
                Some(r) => {
                    println!("ok    {:<28} {}", s.id, snap.classes[r.class].full_name);
                    for (n, m) in &r.members {
                        println!("        .{n} = {}", shape::member_name(&snap, m));
                    }
                }
                None => println!("FAIL  {:<28} {}", s.id, res.failed.get(&s.id).map(failure_text).unwrap_or_default()),
            }
        }
        println!("{} resolved, {} failed ({} classes in dump, build {:?})", res.ok.len(), res.failed.len(), snap.classes.len(), snap.game_build);
    }
    Ok(res.failed.is_empty())
}

fn cmd_diff(mut args: Vec<String>) -> R<bool> {
    let cat = catalogue(&mut args)?;
    let [old, new] = args.as_slice() else { return Err("usage: diff <old.json> <new.json>".into()) };
    let (a, b): (Snapshot, Snapshot) = (read_json(old)?, read_json(new)?);
    let rows = shape::diff(&cat, &a, &b);
    for r in &rows {
        println!("{:<8} {:<28} {} -> {}", r.change, r.id, r.old.as_deref().unwrap_or("-"), r.new.as_deref().unwrap_or("-"));
    }
    Ok(!rows.iter().any(|r| r.change == "broke"))
}

fn signature(k: &ClassInfo) -> String {
    let mut s = String::new();
    let _ = writeln!(s, "{} {} : {}{}", if k.valuetype { "struct" } else { "class" }, k.full_name, k.parent, if k.interfaces.is_empty() { String::new() } else { format!(", {}", k.interfaces.join(", ")) });
    for f in &k.fields {
        let _ = writeln!(s, "  field {}{} : {}", if f.is_static { "static " } else { "" }, f.name, f.ty);
    }
    for m in &k.methods {
        let _ = writeln!(s, "  method {}{}({}) -> {}", if m.is_static { "static " } else { "" }, m.name, m.params.join(", "), m.ret);
    }
    s
}

fn cmd_find(mut args: Vec<String>) -> R<bool> {
    let limit: usize = take_opt(&mut args, "--limit")?.map(|v| v.parse().map_err(|_| "--limit needs a number")).transpose()?.unwrap_or(50);
    let [dump, text] = args.as_slice() else { return Err("usage: find <dump.json> <text>".into()) };
    let snap: Snapshot = read_json(dump)?;
    let hits: Vec<&ClassInfo> = snap.classes.iter().filter(|k| signature(k).contains(text.as_str())).collect();
    for k in hits.iter().take(limit) {
        print!("{}", signature(k).lines().filter(|l| !l.starts_with("  ") || l.contains(text.as_str())).map(|l| format!("{l}\n")).collect::<String>());
    }
    println!("{} classes match{}", hits.len(), if hits.len() > limit { format!(" (first {limit} shown)") } else { String::new() });
    Ok(true)
}

fn cmd_show(args: Vec<String>) -> R<bool> {
    let [dump, name] = args.as_slice() else { return Err("usage: show <dump.json> <class full name>".into()) };
    let snap: Snapshot = read_json(dump)?;
    let k = snap.classes.iter().find(|k| k.full_name == *name).ok_or(format!("no class {name}"))?;
    print!("{}", signature(k));
    Ok(true)
}

fn rule_text(r: &MemberRule) -> String {
    let mut parts = vec![];
    match r {
        MemberRule::Field(f) => {
            parts.push("field".to_string());
            parts.extend(f.name.as_ref().map(|n| format!("named `{n}`")));
            parts.extend(f.ty.as_ref().map(|t| format!("of type `{t}`")));
            parts.extend(f.is_static.map(|s| if s { "static" } else { "instance" }.to_string()));
            parts.extend(f.nth.map(|n| format!("#{n}")));
            if f.inherited {
                parts.push("(inherited)".into());
            }
        }
        MemberRule::Method(m) => {
            parts.push("method".to_string());
            parts.extend(m.name.as_ref().map(|n| format!("named `{n}`")));
            parts.extend(m.params.as_ref().map(|p| format!("`({})`", p.join(", "))));
            parts.extend(m.argc.map(|n| format!("{n} params")));
            parts.extend(m.ret.as_ref().map(|t| format!("-> `{t}`")));
            parts.extend(m.is_static.map(|s| if s { "static" } else { "instance" }.to_string()));
            parts.extend(m.nth.map(|n| format!("#{n}")));
            if m.inherited {
                parts.push("(inherited)".into());
            }
        }
    }
    parts.join(" ")
}

/// Markdown reference of the catalogue; with a dump, the real names on that build.
pub fn docs(cat: &Catalogue, dump: Option<&Snapshot>) -> String {
    let res = dump.map(|d| shape::resolve(cat, d));
    let mut s = String::from("# Symbol reference\n\nGenerated by `sdk-symbols docs` from `crates/jibbers-mod-sdk/symbols.json`; do not edit by hand.\nSee [SYMBOLS.md](SYMBOLS.md) for how symbols work.\n\n");
    if let Some(d) = dump {
        let _ = writeln!(s, "Real names below are from the dump of build `{}`. They change between builds; use the symbol ids.\n", d.game_build);
    }
    let _ = writeln!(s, "| Symbol | Tags | What it is |\n| --- | --- | --- |");
    for sym in &cat.symbols {
        let _ = writeln!(s, "| [`{}`](#{}) | {} | {} |", sym.id, sym.id.replace('.', ""), sym.tags.join(", "), sym.summary);
    }
    for sym in &cat.symbols {
        let _ = writeln!(s, "\n## `{}`\n\n{}\n", sym.id, sym.summary);
        if !sym.notes.is_empty() {
            let _ = writeln!(s, "{}\n", sym.notes);
        }
        let ok = res.as_ref().and_then(|r| r.ok.get(&sym.id));
        if let (Some(d), Some(r)) = (dump, ok) {
            let _ = writeln!(s, "Resolves to `{}` on this build.\n", d.classes[r.class].full_name);
        } else if let (Some(r), Some(_)) = (&res, dump) {
            let _ = writeln!(s, "**Does not resolve on this build:** {}\n", r.failed.get(&sym.id).map(failure_text).unwrap_or_default());
        }
        if !sym.members.is_empty() {
            let _ = writeln!(s, "| Member | Found as |{}", if dump.is_some() { " Real name |" } else { "" });
            let _ = writeln!(s, "| --- | --- |{}", if dump.is_some() { " --- |" } else { "" });
            for (name, rule) in &sym.members {
                let real = match (dump, ok) {
                    (Some(d), Some(r)) => format!(" `{}` |", r.members.get(name).map(|m| shape::member_name(d, m)).unwrap_or_default()),
                    (Some(_), None) => " - |".into(),
                    _ => String::new(),
                };
                let _ = writeln!(s, "| `{}.{name}` | {} |{real}", sym.id, rule_text(rule));
            }
        }
    }
    s
}

fn cmd_docs(mut args: Vec<String>) -> R<bool> {
    let cat = catalogue(&mut args)?;
    let dump: Option<Snapshot> = take_opt(&mut args, "--dump")?.map(|p| read_json(&p)).transpose()?;
    let out = take_opt(&mut args, "--out")?;
    let text = docs(&cat, dump.as_ref());
    match out {
        Some(p) => std::fs::write(Path::new(&p), text).map_err(|e| format!("{p}: {e}"))?,
        None => print!("{text}"),
    }
    Ok(true)
}

fn main() -> ExitCode {
    let mut args: Vec<String> = std::env::args().skip(1).collect();
    if args.is_empty() {
        eprintln!("usage: sdk-symbols <lint|resolve|diff|find|show|docs> ... (see the header of src/main.rs)");
        return ExitCode::from(2);
    }
    let cmd = args.remove(0);
    let r = match cmd.as_str() {
        "lint" => cmd_lint(args),
        "resolve" => cmd_resolve(args),
        "diff" => cmd_diff(args),
        "find" => cmd_find(args),
        "show" => cmd_show(args),
        "docs" => cmd_docs(args),
        other => Err(format!("unknown command {other:?}")),
    };
    match r {
        Ok(true) => ExitCode::SUCCESS,
        Ok(false) => ExitCode::from(1),
        Err(e) => {
            eprintln!("sdk-symbols: {e}");
            ExitCode::from(2)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn docs_list_every_symbol_and_member() {
        let cat: Catalogue = serde_json::from_str(CATALOGUE_JSON).unwrap();
        let d = docs(&cat, None);
        for s in &cat.symbols {
            assert!(d.contains(&format!("## `{}`", s.id)));
            for m in s.members.keys() {
                assert!(d.contains(&format!("`{}.{m}`", s.id)), "{}.{m}", s.id);
            }
        }
    }

    /// Every symbol must resolve against the real dump of the validated build (skipped when the repo
    /// has no dump: dumps come from the game, see docs/guides/reverse-engineering.md).
    #[test]
    fn every_built_in_symbol_resolves_on_the_dumped_build() {
        let cat: Catalogue = serde_json::from_str(CATALOGUE_JSON).unwrap();
        let dump = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../out/dumps/2026-10-18-build.json");
        let Ok(text) = std::fs::read_to_string(&dump) else {
            eprintln!("no dump at {}; skipped", dump.display());
            return;
        };
        let snap: Snapshot = serde_json::from_str(&text).unwrap();
        let res = shape::resolve(&cat, &snap);
        let failed: Vec<String> = res.failed.iter().map(|(id, f)| format!("{id}: {}", failure_text(f))).collect();
        assert!(failed.is_empty(), "{failed:#?}");
        let real = |path: &str| {
            let (id, m) = path.rsplit_once('.').unwrap();
            shape::member_name(&snap, &res.ok[id].members[m])
        };
        // Members the original mods hard-coded by obfuscated name, now found by shape.
        assert_eq!(real("player.ragdoll_spawner.rider"), "pha");
        assert_eq!(real("player.ragdoll_spawner.selected_spawn"), "dlz()");
        assert_eq!(real("map.importer.import"), "bjc()");
        assert_eq!(real("ui.navigatable_menu.layout"), "gwp()");
        assert_eq!(real("ui.behaviour.interactable"), "<uhg>k__BackingField");
    }

    #[test]
    fn docs_with_a_dump_show_failures() {
        let cat: Catalogue = serde_json::from_str(CATALOGUE_JSON).unwrap();
        let d = docs(&cat, Some(&Snapshot { game_build: "empty".into(), classes: vec![] }));
        assert!(d.contains("Does not resolve on this build"));
    }
}
