//! Finding obfuscated game classes and members by their shape instead of their names.
//!
//! The game's own classes, fields and methods are renamed to two or three lowercase letters
//! (`uzv`, `zo`), and the names change between builds. Types do not: a field's type, a method's
//! parameter list, a class's parent and the engine/library classes it uses survive a rebuild. A
//! [`Symbol`] describes one class by those facts and gives it and its members stable names
//! (`map.importer`, `map.importer.terrains`).
//!
//! This module is pure data and matching: no IL2CPP, so it runs in unit tests and in the offline
//! `sdk-symbols` tool. [`crate::symbols`] reads the live game into a [`Snapshot`] and resolves the
//! built-in catalogue (`symbols.json`) against it.
//!
//! Matching rules:
//! - Every condition that is present must hold; absent conditions match anything.
//! - A class symbol must match exactly one class. None is "not found", more than one is
//!   "ambiguous": both mean the rule needs updating for this build, never "pick the first".
//! - A member must match exactly one declared member of the resolved class, unless the rule gives
//!   `nth` (the n-th match, 0-based, in declaration order: for "the second `int` field"). With
//!   `"inherited": true` the parent classes in the snapshot are searched too, class first; the same
//!   flag on a class condition lets an inherited member satisfy it.
//! - Type references are full IL2CPP type names (`UnityEngine.Terrain[]`,
//!   `System.Collections.Generic.List<UnityEngine.Vector3>`). `@id` inside one stands for the full
//!   name of the class another symbol resolves to (`@chat.command[]`), so obfuscated classes can
//!   refer to each other. `*` alone matches any type.

use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, HashMap, HashSet};

/// Default image for game classes.
pub const GAME_IMAGE: &str = "Assembly-CSharp.dll";

// ---------------------------------------------------------------------------------------------
// The game's metadata, as read from IL2CPP (or loaded from a dump file).

/// Every class of one or more images. The format of `sdk-symbols` dumps.
#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
pub struct Snapshot {
    /// Fingerprint of the game build the snapshot came from (`gamebuild` label), if known.
    #[serde(default)]
    pub game_build: String,
    pub classes: Vec<ClassInfo>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
pub struct ClassInfo {
    pub image: String,
    pub namespace: String,
    pub name: String,
    /// IL2CPP type name of the class, the form field and parameter types use.
    pub full_name: String,
    /// Full name of the parent class ("" for none).
    #[serde(default)]
    pub parent: String,
    /// Full name of the enclosing class for a nested class ("" otherwise).
    #[serde(default)]
    pub declaring: String,
    #[serde(default)]
    pub valuetype: bool,
    #[serde(default)]
    pub interfaces: Vec<String>,
    #[serde(default)]
    pub fields: Vec<FieldInfo>,
    #[serde(default)]
    pub methods: Vec<MethodInfo>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
pub struct FieldInfo {
    pub name: String,
    #[serde(rename = "type")]
    pub ty: String,
    #[serde(default, rename = "static")]
    pub is_static: bool,
    #[serde(default)]
    pub offset: usize,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize, PartialEq)]
pub struct MethodInfo {
    pub name: String,
    #[serde(default)]
    pub params: Vec<String>,
    #[serde(rename = "return")]
    pub ret: String,
    #[serde(default, rename = "static")]
    pub is_static: bool,
}

/// Two or three lowercase ASCII letters: how the game names obfuscated classes and members. A
/// property's backing field (`<abc>k__BackingField`) counts by its inner name.
pub fn is_obfuscated(name: &str) -> bool {
    let n = name.strip_prefix('<').and_then(|s| s.strip_suffix(">k__BackingField")).unwrap_or(name);
    (2..=3).contains(&n.len()) && n.bytes().all(|b| b.is_ascii_lowercase())
}

// ---------------------------------------------------------------------------------------------
// Rules.

/// The catalogue file: a list of symbols.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Catalogue {
    #[serde(default)]
    pub symbols: Vec<Symbol>,
}

/// One game class and the members mods use, by stable name.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Symbol {
    /// Stable dotted id: `area.thing` (`map.importer`). Lowercase, digits, `_` and `.`.
    pub id: String,
    /// What the class is, in one sentence.
    pub summary: String,
    #[serde(default)]
    pub tags: Vec<String>,
    /// Which mods use it and how sure we are (free text for the docs).
    #[serde(default)]
    pub notes: String,
    #[serde(rename = "match")]
    pub class: ClassRule,
    /// Member name -> rule. Addressed as `<symbol id>.<member>`.
    #[serde(default)]
    pub members: BTreeMap<String, MemberRule>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ClassRule {
    /// Image the class lives in (default `Assembly-CSharp.dll`).
    #[serde(default)]
    pub image: Option<String>,
    #[serde(default)]
    pub namespace: Option<String>,
    /// Exact name, for classes that are not obfuscated.
    #[serde(default)]
    pub name: Option<String>,
    /// The class name must (true) or must not (false) look obfuscated.
    #[serde(default)]
    pub obfuscated: Option<bool>,
    #[serde(default)]
    pub parent: Option<String>,
    /// Enclosing class of a nested class.
    #[serde(default)]
    pub declaring: Option<String>,
    #[serde(default)]
    pub valuetype: Option<bool>,
    /// Interfaces the class must implement.
    #[serde(default)]
    pub interfaces: Vec<String>,
    /// Each rule must match at least one declared field.
    #[serde(default)]
    pub fields: Vec<FieldRule>,
    /// Each rule must match at least one declared method.
    #[serde(default)]
    pub methods: Vec<MethodRule>,
    /// Bounds on the number of declared instance fields, `[min, max]`.
    #[serde(default)]
    pub instance_fields: Option<[usize; 2]>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FieldRule {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub obfuscated: Option<bool>,
    /// The field is (true) or is not (false) a property's backing field (`<name>k__BackingField`).
    #[serde(default)]
    pub backing: Option<bool>,
    #[serde(default, rename = "type")]
    pub ty: Option<String>,
    #[serde(default, rename = "static")]
    pub is_static: Option<bool>,
    /// As a member: the n-th matching field (0-based, declaration order).
    #[serde(default)]
    pub nth: Option<usize>,
    /// Also search parent classes.
    #[serde(default)]
    pub inherited: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct MethodRule {
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub obfuscated: Option<bool>,
    /// Exact parameter types (and so the count). Absent: any parameters.
    #[serde(default)]
    pub params: Option<Vec<String>>,
    /// Only the parameter count.
    #[serde(default)]
    pub argc: Option<usize>,
    #[serde(default, rename = "return")]
    pub ret: Option<String>,
    #[serde(default, rename = "static")]
    pub is_static: Option<bool>,
    #[serde(default)]
    pub nth: Option<usize>,
    #[serde(default)]
    pub inherited: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum MemberRule {
    Field(FieldRule),
    Method(MethodRule),
}

// ---------------------------------------------------------------------------------------------
// Results.

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum MemberHit {
    /// Index into the class's `fields`.
    Field(usize),
    /// Index into the class's `methods`.
    Method(usize),
}

/// A resolved member: the class that declares it (the symbol's class or a parent) and which one.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Member {
    /// Index into the snapshot's `classes`.
    pub class: usize,
    pub hit: MemberHit,
}

#[derive(Clone, Debug, PartialEq)]
pub struct Resolved {
    /// Index into the snapshot's `classes`.
    pub class: usize,
    pub members: BTreeMap<String, Member>,
}

#[derive(Clone, Debug, PartialEq, Serialize)]
#[serde(tag = "status", rename_all = "snake_case")]
pub enum Failure {
    NotFound,
    /// Several classes match; their full names.
    Ambiguous { candidates: Vec<String> },
    /// The class resolved but members did not: member -> reason.
    Members { class: String, errors: BTreeMap<String, String> },
    /// A symbol this one refers to (`@id`) did not resolve.
    Dependency { id: String },
    UnknownSymbol,
    Cycle,
}

/// Outcome of resolving a whole catalogue.
#[derive(Clone, Debug, Default)]
pub struct Resolution {
    pub ok: BTreeMap<String, Resolved>,
    pub failed: BTreeMap<String, Failure>,
}

impl Resolution {
    /// Full name of the class a symbol resolved to.
    pub fn class_name<'a>(&self, snap: &'a Snapshot, id: &str) -> Option<&'a str> {
        self.ok.get(id).map(|r| snap.classes[r.class].full_name.as_str())
    }
}

// ---------------------------------------------------------------------------------------------
// Matching.

/// Resolves every symbol of `cat` against `snap`.
pub fn resolve(cat: &Catalogue, snap: &Snapshot) -> Resolution {
    let by_name = snap.classes.iter().enumerate().map(|(i, k)| (k.full_name.as_str(), i)).collect();
    let mut r = Resolver { snap, by_id: cat.symbols.iter().map(|s| (s.id.as_str(), s)).collect(), by_name, out: Resolution::default(), active: HashSet::new(), picked: HashMap::new() };
    for s in &cat.symbols {
        // Failures are recorded in `out`.
        let _ = r.symbol(&s.id);
    }
    r.out
}

struct Resolver<'a> {
    snap: &'a Snapshot,
    by_id: HashMap<&'a str, &'a Symbol>,
    by_name: HashMap<&'a str, usize>,
    out: Resolution,
    active: HashSet<String>,
    /// Symbols being resolved whose class is already chosen (their members are pending). A member
    /// rule may refer to such a symbol: only its class is needed, so that is not a cycle.
    picked: HashMap<String, usize>,
}

impl<'a> Resolver<'a> {
    /// Full class name for symbol `id`, resolving it on demand.
    fn symbol(&mut self, id: &str) -> Result<String, Failure> {
        if let Some(r) = self.out.ok.get(id) {
            return Ok(self.snap.classes[r.class].full_name.clone());
        }
        if let Some(f) = self.out.failed.get(id) {
            return Err(f.clone());
        }
        let Some(sym) = self.by_id.get(id).copied() else { return Err(Failure::UnknownSymbol) };
        if !self.active.insert(id.to_string()) {
            return match self.picked.get(id) {
                Some(&k) => Ok(self.snap.classes[k].full_name.clone()),
                None => Err(Failure::Cycle),
            };
        }
        let res = self.match_symbol(sym);
        self.active.remove(id);
        self.picked.remove(id);
        match res {
            Ok(r) => {
                let name = self.snap.classes[r.class].full_name.clone();
                self.out.ok.insert(id.to_string(), r);
                Ok(name)
            }
            Err(f) => {
                self.out.failed.insert(id.to_string(), f.clone());
                Err(f)
            }
        }
    }

    /// Class `k` and, with `inherited`, its parents that are in the snapshot (class first).
    fn chain(&self, k: usize, inherited: bool) -> Vec<usize> {
        let mut out = vec![k];
        while inherited {
            let parent = &self.snap.classes[*out.last().unwrap()].parent;
            match self.by_name.get(parent.as_str()) {
                Some(&p) if !out.contains(&p) => out.push(p),
                _ => break,
            }
        }
        out
    }

    /// Every member of class `k` (and its parents, per the rule) that `rule` matches, in search order.
    fn members_matching(&mut self, k: usize, rule: &MemberRule) -> Result<Vec<Member>, Failure> {
        let snap = self.snap;
        let mut v = vec![];
        match rule {
            MemberRule::Field(fr) => {
                for c in self.chain(k, fr.inherited) {
                    for (i, f) in snap.classes[c].fields.iter().enumerate() {
                        if self.field_ok(fr, f)? {
                            v.push(Member { class: c, hit: MemberHit::Field(i) });
                        }
                    }
                }
            }
            MemberRule::Method(mr) => {
                for c in self.chain(k, mr.inherited) {
                    for (i, m) in snap.classes[c].methods.iter().enumerate() {
                        if self.method_ok(mr, m)? {
                            v.push(Member { class: c, hit: MemberHit::Method(i) });
                        }
                    }
                }
            }
        }
        Ok(v)
    }

    /// Replaces each `@id` in a type pattern with that symbol's class name.
    fn expand(&mut self, pattern: &str) -> Result<String, Failure> {
        let mut out = String::new();
        let mut rest = pattern;
        while let Some(at) = rest.find('@') {
            out.push_str(&rest[..at]);
            let tail = &rest[at + 1..];
            let end = tail.find(|c: char| !(c.is_ascii_alphanumeric() || c == '_' || c == '.')).unwrap_or(tail.len());
            // A trailing '.' belongs to the surrounding text, not the id.
            let id = tail[..end].trim_end_matches('.');
            let name = self.symbol(id).map_err(|_| Failure::Dependency { id: id.to_string() })?;
            out.push_str(&name);
            rest = &tail[id.len()..];
        }
        out.push_str(rest);
        Ok(out)
    }

    fn ty_ok(&mut self, pattern: &Option<String>, actual: &str) -> Result<bool, Failure> {
        match pattern {
            None => Ok(true),
            Some(p) if p == "*" => Ok(true),
            Some(p) => Ok(self.expand(p)? == actual),
        }
    }

    fn field_ok(&mut self, r: &FieldRule, f: &FieldInfo) -> Result<bool, Failure> {
        Ok(r.name.as_ref().is_none_or(|n| *n == f.name)
            && r.obfuscated.is_none_or(|o| o == is_obfuscated(&f.name))
            && r.backing.is_none_or(|b| b == f.name.ends_with(">k__BackingField"))
            && r.is_static.is_none_or(|s| s == f.is_static)
            && self.ty_ok(&r.ty, &f.ty)?)
    }

    fn method_ok(&mut self, r: &MethodRule, m: &MethodInfo) -> Result<bool, Failure> {
        if !(r.name.as_ref().is_none_or(|n| *n == m.name)
            && r.obfuscated.is_none_or(|o| o == is_obfuscated(&m.name))
            && r.is_static.is_none_or(|s| s == m.is_static)
            && r.argc.is_none_or(|n| n == m.params.len()))
        {
            return Ok(false);
        }
        if let Some(ps) = &r.params {
            if ps.len() != m.params.len() {
                return Ok(false);
            }
            for (p, actual) in ps.iter().zip(&m.params) {
                if !self.ty_ok(&Some(p.clone()), actual)? {
                    return Ok(false);
                }
            }
        }
        self.ty_ok(&r.ret, &m.ret)
    }

    fn class_ok(&mut self, r: &ClassRule, ki: usize) -> Result<bool, Failure> {
        let k = &self.snap.classes[ki];
        let image = r.image.as_deref().unwrap_or(GAME_IMAGE);
        if k.image != image
            || r.namespace.as_ref().is_some_and(|n| *n != k.namespace)
            || r.name.as_ref().is_some_and(|n| *n != k.name)
            || r.obfuscated.is_some_and(|o| o != is_obfuscated(&k.name))
            || r.valuetype.is_some_and(|v| v != k.valuetype)
        {
            return Ok(false);
        }
        if let Some([lo, hi]) = r.instance_fields {
            let n = k.fields.iter().filter(|f| !f.is_static).count();
            if n < lo || n > hi {
                return Ok(false);
            }
        }
        if !self.ty_ok(&r.parent, &k.parent)? || !self.ty_ok(&r.declaring, &k.declaring)? {
            return Ok(false);
        }
        for i in &r.interfaces {
            let want = self.expand(i)?;
            if !k.interfaces.contains(&want) {
                return Ok(false);
            }
        }
        for fr in &r.fields {
            if self.members_matching(ki, &MemberRule::Field(fr.clone()))?.is_empty() {
                return Ok(false);
            }
        }
        for mr in &r.methods {
            if self.members_matching(ki, &MemberRule::Method(mr.clone()))?.is_empty() {
                return Ok(false);
            }
        }
        Ok(true)
    }

    fn match_symbol(&mut self, sym: &Symbol) -> Result<Resolved, Failure> {
        let snap = self.snap;
        let mut hits = vec![];
        for i in 0..snap.classes.len() {
            if self.class_ok(&sym.class, i)? {
                hits.push(i);
            }
        }
        let class = match hits.as_slice() {
            [] => return Err(Failure::NotFound),
            [one] => *one,
            many => return Err(Failure::Ambiguous { candidates: many.iter().map(|&i| snap.classes[i].full_name.clone()).collect() }),
        };
        self.picked.insert(sym.id.clone(), class);
        let mut members = BTreeMap::new();
        let mut errors = BTreeMap::new();
        for (name, rule) in &sym.members {
            let found = self.members_matching(class, rule)?;
            let nth = match rule {
                MemberRule::Field(f) => f.nth,
                MemberRule::Method(m) => m.nth,
            };
            let pick = match nth {
                Some(n) => found.get(n).cloned().ok_or_else(|| format!("only {} matches, wanted #{n}", found.len())),
                None if found.len() == 1 => Ok(found[0].clone()),
                None if found.is_empty() => Err("no match".to_string()),
                None => Err(format!("{} matches: {}", found.len(), found.iter().map(|m| member_name(snap, m)).collect::<Vec<_>>().join(", "))),
            };
            match pick {
                Ok(h) => {
                    members.insert(name.clone(), h);
                }
                Err(e) => {
                    errors.insert(name.clone(), e);
                }
            }
        }
        if !errors.is_empty() {
            return Err(Failure::Members { class: snap.classes[class].full_name.clone(), errors });
        }
        Ok(Resolved { class, members })
    }
}

/// The real (obfuscated) name of a resolved member.
pub fn member_name(snap: &Snapshot, m: &Member) -> String {
    let k = &snap.classes[m.class];
    match &m.hit {
        MemberHit::Field(i) => k.fields[*i].name.clone(),
        MemberHit::Method(i) => {
            let m = &k.methods[*i];
            format!("{}({})", m.name, m.params.join(", "))
        }
    }
}

/// Checks a catalogue for mistakes that need no game: bad ids, duplicates, `@` references to
/// unknown symbols, and members without any condition.
pub fn lint(cat: &Catalogue) -> Vec<String> {
    let mut errs = vec![];
    let ids: HashSet<&str> = cat.symbols.iter().map(|s| s.id.as_str()).collect();
    let mut seen = HashSet::new();
    for s in &cat.symbols {
        if s.id.is_empty() || !s.id.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'_' || b == b'.') {
            errs.push(format!("{}: id must be lowercase letters, digits, '_' and '.'", s.id));
        }
        if !seen.insert(s.id.as_str()) {
            errs.push(format!("{}: duplicate id", s.id));
        }
        if s.summary.trim().is_empty() {
            errs.push(format!("{}: missing summary", s.id));
        }
        let c = &s.class;
        if c.name.is_none() && c.fields.is_empty() && c.methods.is_empty() && c.parent.is_none() {
            errs.push(format!("{}: class rule has no name, parent, fields or methods; it would match anything", s.id));
        }
        let mut patterns: Vec<&str> = vec![];
        patterns.extend(c.parent.as_deref());
        patterns.extend(c.declaring.as_deref());
        patterns.extend(c.interfaces.iter().map(String::as_str));
        patterns.extend(c.fields.iter().filter_map(|f| f.ty.as_deref()));
        for m in &c.methods {
            patterns.extend(m.ret.as_deref());
            patterns.extend(m.params.iter().flatten().map(String::as_str));
        }
        for (name, m) in &s.members {
            match m {
                MemberRule::Field(f) => {
                    if f.name.is_none() && f.ty.is_none() && f.backing.is_none() {
                        errs.push(format!("{}.{name}: field rule needs a name or a type", s.id));
                    }
                    patterns.extend(f.ty.as_deref());
                }
                MemberRule::Method(m) => {
                    if m.name.is_none() && m.params.is_none() && m.ret.is_none() {
                        errs.push(format!("{}.{name}: method rule needs a name, params or a return type", s.id));
                    }
                    patterns.extend(m.ret.as_deref());
                    patterns.extend(m.params.iter().flatten().map(String::as_str));
                }
            }
        }
        for p in patterns {
            for part in p.split('@').skip(1) {
                let end = part.find(|ch: char| !(ch.is_ascii_alphanumeric() || ch == '_' || ch == '.')).unwrap_or(part.len());
                let id = part[..end].trim_end_matches('.');
                if !ids.contains(id) {
                    errs.push(format!("{}: refers to unknown symbol @{id}", s.id));
                }
            }
        }
    }
    errs
}

/// Per-symbol comparison of two builds: what each symbol resolved to before and after.
#[derive(Clone, Debug, Serialize, PartialEq)]
pub struct DiffRow {
    pub id: String,
    pub old: Option<String>,
    pub new: Option<String>,
    /// "same", "renamed", "broke", "fixed", "missing"
    pub change: &'static str,
}

pub fn diff(cat: &Catalogue, old: &Snapshot, new: &Snapshot) -> Vec<DiffRow> {
    let (a, b) = (resolve(cat, old), resolve(cat, new));
    cat.symbols
        .iter()
        .map(|s| {
            let (o, n) = (a.class_name(old, &s.id).map(str::to_string), b.class_name(new, &s.id).map(str::to_string));
            let change = match (&o, &n) {
                (Some(x), Some(y)) if x == y => "same",
                (Some(_), Some(_)) => "renamed",
                (Some(_), None) => "broke",
                (None, Some(_)) => "fixed",
                (None, None) => "missing",
            };
            DiffRow { id: s.id.clone(), old: o, new: n, change }
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    fn class(name: &str, parent: &str, fields: &[(&str, &str)], methods: &[(&str, &[&str], &str)]) -> ClassInfo {
        ClassInfo {
            image: GAME_IMAGE.into(),
            namespace: String::new(),
            name: name.into(),
            full_name: name.into(),
            parent: parent.into(),
            fields: fields.iter().map(|(n, t)| FieldInfo { name: n.to_string(), ty: t.to_string(), ..Default::default() }).collect(),
            methods: methods
                .iter()
                .map(|(n, p, r)| MethodInfo { name: n.to_string(), params: p.iter().map(|s| s.to_string()).collect(), ret: r.to_string(), is_static: false })
                .collect(),
            ..Default::default()
        }
    }

    fn snap() -> Snapshot {
        Snapshot {
            game_build: "test".into(),
            classes: vec![
                class("MapRuntimeImporter", "UnityEngine.MonoBehaviour", &[("uzv", "UnityEngine.Terrain[]"), ("ab", "System.Int32")], &[("qa", &["MapData"], "System.Void")]),
                class("kq", "System.Object", &[("aa", "System.String"), ("ab", "System.String"), ("ac", "System.Int32")], &[("Run", &["System.String[]"], "System.Void")]),
                class("zz", "System.Object", &[("cm", "kq[]")], &[]),
                class("xy", "System.Object", &[("aa", "System.String")], &[]),
                class("xw", "System.Object", &[("aa", "System.String")], &[]),
            ],
        }
    }

    fn cat(v: serde_json::Value) -> Catalogue {
        serde_json::from_value(v).unwrap()
    }

    #[test]
    fn obfuscated_names() {
        assert!(is_obfuscated("uzv") && is_obfuscated("zo") && is_obfuscated("<abc>k__BackingField"));
        assert!(!is_obfuscated("Update") && !is_obfuscated("a") && !is_obfuscated("abcd") && !is_obfuscated("Ab"));
    }

    #[test]
    fn resolves_by_shape_and_by_reference() {
        let c = cat(json!({"symbols": [
            {"id": "map.importer", "summary": "s", "match": {"name": "MapRuntimeImporter"},
             "members": {"terrains": {"field": {"type": "UnityEngine.Terrain[]"}},
                         "load": {"method": {"params": ["MapData"], "return": "System.Void"}}}},
            {"id": "chat.registry", "summary": "s", "match": {"obfuscated": true, "fields": [{"type": "@chat.command[]"}]},
             "members": {"commands": {"field": {"type": "@chat.command[]"}}}},
            {"id": "chat.command", "summary": "s", "match": {"obfuscated": true, "methods": [{"name": "Run", "params": ["System.String[]"]}]},
             "members": {"usage": {"field": {"type": "System.String", "nth": 1}}}}
        ]}));
        assert!(lint(&c).is_empty(), "{:?}", lint(&c));
        let s = snap();
        let r = resolve(&c, &s);
        assert!(r.failed.is_empty(), "{:?}", r.failed);
        let imp = &r.ok["map.importer"];
        assert_eq!(member_name(&s, &imp.members["terrains"]), "uzv");
        assert_eq!(member_name(&s, &imp.members["load"]), "qa(MapData)");
        assert_eq!(r.class_name(&s, "chat.registry"), Some("zz"));
        let cmd = &r.ok["chat.command"];
        assert_eq!(member_name(&s, &cmd.members["usage"]), "ab");
    }

    #[test]
    fn inherited_members_come_from_parents() {
        let mut s = snap();
        s.classes.push(class("Button", "Selectable", &[("cb", "System.Action<Button>")], &[]));
        s.classes.push(class("Selectable", "UnityEngine.MonoBehaviour", &[("<uhg>k__BackingField", "System.Boolean")], &[("kda", &["System.Boolean"], "System.Void")]));
        let mut c = cat(json!({"symbols": [
            {"id": "ui.button", "summary": "s", "match": {"name": "Button", "methods": [{"argc": 1, "inherited": true}]},
             "members": {"interactable": {"field": {"type": "System.Boolean", "inherited": true}},
                         "local_only": {"field": {"type": "System.Boolean"}}}}
        ]}));
        let r = resolve(&c, &s);
        let Failure::Members { errors, .. } = &r.failed["ui.button"] else { panic!("{:?}", r.failed) };
        assert_eq!(errors.keys().collect::<Vec<_>>(), ["local_only"], "a declared-only search must not see the parent");
        c.symbols[0].members.remove("local_only");
        let r = resolve(&c, &s);
        let m = &r.ok["ui.button"].members["interactable"];
        assert_eq!((s.classes[m.class].name.as_str(), member_name(&s, m).as_str()), ("Selectable", "<uhg>k__BackingField"));
    }

    #[test]
    fn ambiguity_and_member_errors_are_failures() {
        let c = cat(json!({"symbols": [
            {"id": "a", "summary": "s", "match": {"obfuscated": true, "parent": "System.Object", "fields": [{"type": "System.String"}], "instance_fields": [1, 1]}},
            {"id": "b", "summary": "s", "match": {"name": "kq"}, "members": {"name": {"field": {"type": "System.String"}}}},
            {"id": "c", "summary": "s", "match": {"name": "Nope"}},
            {"id": "d", "summary": "s", "match": {"obfuscated": true, "fields": [{"type": "@c"}]}}
        ]}));
        let r = resolve(&c, &snap());
        assert_eq!(r.failed["a"], Failure::Ambiguous { candidates: vec!["xy".into(), "xw".into()] });
        assert!(matches!(&r.failed["b"], Failure::Members { errors, .. } if errors["name"].starts_with("2 matches")));
        assert_eq!(r.failed["c"], Failure::NotFound);
        assert_eq!(r.failed["d"], Failure::Dependency { id: "c".into() });
    }

    #[test]
    fn cycles_fail_instead_of_looping() {
        let c = cat(json!({"symbols": [
            {"id": "x", "summary": "s", "match": {"fields": [{"type": "@y"}]}},
            {"id": "y", "summary": "s", "match": {"fields": [{"type": "@x"}]}}
        ]}));
        let r = resolve(&c, &snap());
        assert!(r.ok.is_empty() && r.failed.len() == 2);
    }

    #[test]
    fn members_may_refer_to_symbols_that_refer_back() {
        // zz holds kq[]; kq's members refer to zz's class while zz's class rule refers to kq.
        let c = cat(json!({"symbols": [
            {"id": "reg", "summary": "s", "match": {"fields": [{"type": "@cmd[]"}]}},
            {"id": "cmd", "summary": "s", "match": {"obfuscated": true, "methods": [{"name": "Run"}]},
             "members": {"run": {"method": {"name": "Run"}}}},
            {"id": "x", "summary": "s", "match": {"name": "kq"}, "members": {"r": {"method": {"params": ["System.String[]"]}}}},
            {"id": "y", "summary": "s", "match": {"name": "zz"}, "members": {"cm": {"field": {"type": "@x[]"}}}}
        ]}));
        let r = resolve(&c, &snap());
        assert!(r.failed.is_empty(), "{:?}", r.failed);
        let mut s = snap();
        s.classes[1].fields.push(FieldInfo { name: "bk".into(), ty: "zz".into(), ..Default::default() });
        let c = cat(json!({"symbols": [
            {"id": "a", "summary": "s", "match": {"name": "zz"}, "members": {"cmds": {"field": {"type": "@b[]"}}}},
            {"id": "b", "summary": "s", "match": {"name": "kq", "fields": [{"type": "@a"}]}, "members": {"back": {"field": {"type": "@a"}}}}
        ]}));
        let r = resolve(&c, &s);
        assert!(r.failed.is_empty(), "{:?}", r.failed);
        assert_eq!(member_name(&s, &r.ok["a"].members["cmds"]), "cm");
    }

    #[test]
    fn lint_catches_mistakes() {
        let c = cat(json!({"symbols": [
            {"id": "Bad Id", "summary": "", "match": {}},
            {"id": "ok", "summary": "s", "match": {"fields": [{"type": "@missing[]"}]}, "members": {"m": {"method": {}}}}
        ]}));
        let e = lint(&c);
        assert!(e.iter().any(|x| x.contains("lowercase")));
        assert!(e.iter().any(|x| x.contains("missing summary")));
        assert!(e.iter().any(|x| x.contains("match anything")));
        assert!(e.iter().any(|x| x.contains("@missing")));
        assert!(e.iter().any(|x| x.contains("ok.m")));
    }

    #[test]
    fn diff_reports_renames_and_breaks() {
        let c = cat(json!({"symbols": [
            {"id": "cmd", "summary": "s", "match": {"obfuscated": true, "methods": [{"name": "Run"}]}},
            {"id": "imp", "summary": "s", "match": {"name": "MapRuntimeImporter"}}
        ]}));
        let old = snap();
        let mut new = snap();
        new.classes[1].name = "pd".into();
        new.classes[1].full_name = "pd".into();
        new.classes.remove(0);
        let d = diff(&c, &old, &new);
        assert_eq!(d[0].change, "renamed");
        assert_eq!(d[0].new.as_deref(), Some("pd"));
        assert_eq!(d[1].change, "broke");
    }

    #[test]
    fn built_in_catalogue_is_valid() {
        let c: Catalogue = serde_json::from_str(crate::symbols::CATALOGUE_JSON).expect("symbols.json parses");
        assert_eq!(lint(&c), Vec::<String>::new());
    }
}
