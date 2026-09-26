//! rename：SymbolId 目标 + Analysis 引用集 → TextEdit 集（specs §7）。
//!
//! 事实来源（禁 Regex/文本搜索改名）：
//! - 定义位 = 声明语句锚（`decl_syms`）+ 词元流定位名字 token（[`crate::locate`]）；
//! - 引用位 = `refs`（Ident 读/写/调用/go 目标）与 StructLit 类型引用；
//! - 方法调用点 = `Call{callee: Field}` + 检查器接收者静态类型 →
//!   struct 符号的 `methods` 表（SymbolId 事实）；接收者类型未知即拒绝
//!   （不冒险改名，绝不半改）。
//!
//! 拒绝语义（specs §7）：struct/enum/构造器列 P1 显式拒绝；新名与已有
//! 非预载符号同名即保守冲突拒绝；解析有诊断（引用集不完整）拒绝。

use lang_core::SymbolId;
use lang_edit::{Precondition, TextEdit, WorkspaceEdit};
use lang_semantic::{NominalRef, Symbol, SymbolKind};
use lang_syntax::{Expr, Stmt};
use lang_types::TypeData;

use crate::input::RefactorInput;
use crate::locate::{
    decl_name_span, first_ident_span, forin_var_span, last_ident_span, param_ident_span,
};
use crate::walk::{decl_stmts_of, pat_bindings_of, ref_owners_of, refs_of, walk, walk_owned};

/// rename 拒绝（每条都是显式证据，不含糊）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RenameRefusal {
    Unknown(String),
    UnsupportedKind(String),
    InvalidName(String),
    Conflict(String),
    /// 解析有诊断：引用集不完整，改名不安全。
    Dirty(String),
    NoDeclSite(String),
    /// 方法接收者类型未知：存在无法确认归属的同名调用点。
    UntypedReceiver(String),
}

impl std::fmt::Display for RenameRefusal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RenameRefusal::Unknown(m)
            | RenameRefusal::UnsupportedKind(m)
            | RenameRefusal::InvalidName(m)
            | RenameRefusal::Conflict(m)
            | RenameRefusal::Dirty(m)
            | RenameRefusal::NoDeclSite(m)
            | RenameRefusal::UntypedReceiver(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for RenameRefusal {}

/// 新名非法字符/保留名名单（逐字对齐 TS 证据 refactor.ts reservedNames）。
const RESERVED: &[&str] = &[
    "let",
    "var",
    "fn",
    "return",
    "if",
    "else",
    "while",
    "for",
    "in",
    "match",
    "struct",
    "enum",
    "true",
    "false",
    "break",
    "continue",
    "go",
    "chan",
    "import",
    "extern",
    "interface",
    "println",
    "print",
    "len",
    "str",
    "int",
    "float",
    "keys",
    "values",
    "has",
    "push",
    "assert",
    "err",
    "bytes",
    "char",
    "jsonParse",
    "jsonStringify",
    "isNone",
    "makeChan",
    "send",
    "recv",
    "Some",
    "None",
    "Ok",
    "Err",
    "http",
];

/// 校验新名（空名/非法字符/保留名拒绝）。
pub fn validate_new_name(name: &str) -> Result<(), RenameRefusal> {
    if name.is_empty() {
        return Err(RenameRefusal::InvalidName("新名字为空".to_string()));
    }
    for (i, r) in name.chars().enumerate() {
        let ok = r == '_'
            || r.is_ascii_lowercase()
            || r.is_ascii_uppercase()
            || (i > 0 && r.is_ascii_digit());
        if !ok {
            return Err(RenameRefusal::InvalidName(format!(
                "新名字含非法字符 {r:?}"
            )));
        }
    }
    if RESERVED.contains(&name) {
        return Err(RenameRefusal::InvalidName(format!(
            "新名字 {name} 与关键字/保留字/内建名冲突"
        )));
    }
    Ok(())
}

/// 计划一次 rename（dry-run）：返回跨文件 WorkspaceEdit，不落盘。
pub fn rename(
    input: &RefactorInput,
    target: SymbolId,
    new_name: &str,
) -> Result<WorkspaceEdit, RenameRefusal> {
    validate_new_name(new_name)?;
    if (target.0 as usize) >= input.analysis.symbols.len() {
        return Err(RenameRefusal::Unknown(format!("未知符号 ID {target}")));
    }
    let sym = input.analysis.symbols.get(target);
    if sym.is_builtin {
        return Err(RenameRefusal::Unknown(format!(
            "符号 {target} 是预载符号，不可 rename"
        )));
    }
    if sym.name == "_" {
        return Err(RenameRefusal::InvalidName(
            "占位符 _ 不可 rename".to_string(),
        ));
    }
    match sym.kind {
        SymbolKind::Struct | SymbolKind::Enum | SymbolKind::Ctor => {
            return Err(RenameRefusal::UnsupportedKind(format!(
                "暂不支持对 {} 的 rename（列 P1）",
                sym.kind.kind_name()
            )));
        }
        SymbolKind::Fn | SymbolKind::Method | SymbolKind::Var | SymbolKind::Param => {}
    }
    if !input.analysis.checked {
        return Err(RenameRefusal::Dirty(
            "模块存在解析诊断，引用集不完整，拒绝 rename".to_string(),
        ));
    }
    // 保守冲突检测：其他非预载符号占用新名即拒绝
    for s in input.analysis.symbols.iter() {
        if s.is_builtin || s.id == target {
            continue;
        }
        if s.name == new_name {
            return Err(RenameRefusal::Conflict(format!(
                "新名字 {new_name} 与已有符号冲突（{} #{}）",
                s.name, s.id
            )));
        }
    }

    let file_of = |span: &lang_core::Span| input.path_of(span).to_string();
    let edit_at = |file_of: &dyn Fn(&lang_core::Span) -> String,
                   sp: &lang_core::Span,
                   new_name: &str| TextEdit {
        file: file_of(sp),
        start_offset: sp.start.offset,
        end_offset: sp.end.offset,
        replacement: new_name.to_string(),
    };

    let mut edits: Vec<TextEdit> = Vec::new();

    // —— 定义位 ——
    match sym.kind {
        SymbolKind::Fn | SymbolKind::Method => {
            let decls = decl_stmts_of(input, target);
            let mut found = false;
            for d in &decls {
                if let Stmt::FnDecl { span, recv, .. } = d {
                    if let Some(name_sp) = decl_name_span(input, span, recv.is_some()) {
                        edits.push(edit_at(&file_of, &name_sp, new_name));
                        found = true;
                    }
                }
            }
            if !found {
                return Err(RenameRefusal::NoDeclSite(format!(
                    "未找到 {} 的声明节点",
                    sym.name
                )));
            }
        }
        SymbolKind::Var => {
            // let/var 绑定、for 循环变量、match 绑定模式
            let mut found = false;
            for d in decl_stmts_of(input, target) {
                let name_sp = match d {
                    Stmt::Let { span, .. } => first_ident_span(input, span),
                    Stmt::ForIn { span, .. } => forin_var_span(input, span),
                    _ => None,
                };
                if let Some(name_sp) = name_sp {
                    edits.push(edit_at(&file_of, &name_sp, new_name));
                    found = true;
                }
            }
            for p in pat_bindings_of(input, target) {
                let sp = p.span();
                edits.push(edit_at(&file_of, &sp, new_name));
                found = true;
            }
            if !found {
                return Err(RenameRefusal::NoDeclSite(format!(
                    "未找到绑定 {} 的声明",
                    sym.name
                )));
            }
        }
        SymbolKind::Param => {
            let (decl_span, has_recv, seg) = locate_param(input, target, sym)?;
            let Some(name_sp) = param_ident_span(input, &decl_span, has_recv, seg) else {
                return Err(RenameRefusal::NoDeclSite(
                    "未在声明中定位到参数名".to_string(),
                ));
            };
            edits.push(edit_at(&file_of, &name_sp, new_name));
        }
        SymbolKind::Struct | SymbolKind::Enum | SymbolKind::Ctor => {}
    }

    // —— 引用位：全部 Ident 引用（读/写/调用/go 目标）——
    for e in refs_of(input, target) {
        let sp = e.span();
        edits.push(edit_at(&file_of, &sp, new_name));
    }

    // —— 方法调用点：obj.name(...) 经接收者类型解析到本方法 ——
    if sym.kind == SymbolKind::Method {
        collect_method_call_sites(input, target, &sym.name, new_name, &file_of, &mut edits)?;
    }

    // 去重（同文件同区间）+ 按文件降序、区间起点降序输出
    edits.sort_by(|a, b| {
        b.file
            .cmp(&a.file)
            .then(b.start_offset.cmp(&a.start_offset))
    });
    edits.dedup_by(|a, b| a.file == b.file && a.start_offset == b.start_offset);

    // precondition：每个涉及文件一条（对当前内容取哈希）
    let mut files: Vec<String> = edits.iter().map(|e| e.file.clone()).collect();
    files.sort();
    files.dedup();
    let current = input.current();
    let preconditions = files
        .into_iter()
        .map(|f| {
            let text = current.get(&f).cloned().unwrap_or_default();
            Precondition {
                file: f,
                file_hash: lang_edit::sha256_hex(text.as_bytes()),
            }
        })
        .collect();
    Ok(WorkspaceEdit {
        edits,
        preconditions,
    })
}

/// 定位参数的声明：所属 fn 声明 span + 是否带接收者 + 段位（含接收者位）。
///
/// 所属 fn 判定：引用点的所属上下文（参数只在属主函数体内被引用）；
/// 从未被引用时退化为「全程序唯一含同名参数的 fn 声明」（多于一个即拒绝，
/// 与保守冲突纪律一致）。
fn locate_param(
    input: &RefactorInput,
    target: SymbolId,
    sym: &Symbol,
) -> Result<(lang_core::Span, bool, usize), RenameRefusal> {
    let mut owner: Option<SymbolId> = None;
    for o in ref_owners_of(input, target) {
        if o.is_some() {
            owner = o;
            break;
        }
    }
    let (decl_span, has_recv) = match owner {
        Some(o) => decl_stmts_of(input, o)
            .into_iter()
            .find_map(|d| match d {
                Stmt::FnDecl { span, recv, .. } => Some((*span, recv.is_some())),
                _ => None,
            })
            .ok_or_else(|| RenameRefusal::NoDeclSite("未找到参数所属函数的声明节点".to_string()))?,
        None => {
            let mut hits: Vec<(lang_core::Span, bool)> = Vec::new();
            each_decl(input, &mut |span, recv, names| {
                if names.iter().any(|n| n == &sym.name) {
                    hits.push((*span, recv));
                }
            });
            if hits.len() != 1 {
                return Err(RenameRefusal::NoDeclSite(format!(
                    "参数 {} 从未被引用且无法唯一定位所属函数",
                    sym.name
                )));
            }
            hits[0]
        }
    };
    // 段位：声明内同名参数位序（干净程序内唯一；同名多个本身即 E2001 脏程序）
    let mut seg: Option<usize> = None;
    each_decl(input, &mut |span, _recv, names| {
        if *span == decl_span && seg.is_none() {
            seg = names.iter().position(|n| n == &sym.name);
        }
    });
    let Some(seg) = seg else {
        return Err(RenameRefusal::NoDeclSite(format!(
            "符号 {target} 不在声明参数表内"
        )));
    };
    Ok((decl_span, has_recv, seg))
}

/// 全程序 FnDecl 走查（span, 是否带 recv, 参数名列表——接收者在首位）。
fn each_decl<F: FnMut(&lang_core::Span, bool, &[String])>(input: &RefactorInput, f: &mut F) {
    walk(
        &input.merged,
        &mut |s| {
            if let Stmt::FnDecl {
                span, recv, params, ..
            } = s
            {
                let mut names: Vec<String> = Vec::new();
                if let Some(r) = recv {
                    names.push(r.name.clone());
                }
                for p in params {
                    names.push(p.name.clone());
                }
                f(span, recv.is_some(), &names);
            }
        },
        &mut |_| {},
        &mut |_| {},
    );
}

/// 方法调用点收集：`obj.name(args)` 的 name 位置。
///
/// 判定链：Field 名相同（AST 结构事实）→ 接收者表达式静态类型 → struct def →
/// struct 符号 methods 表命中目标 → 改名点。接收者类型缺失（None/Any/Unknown/
/// Invalid）的同名点存在时拒绝（保守，不半改）。
fn collect_method_call_sites(
    input: &RefactorInput,
    target: SymbolId,
    old_name: &str,
    new_name: &str,
    file_of: &dyn Fn(&lang_core::Span) -> String,
    edits: &mut Vec<TextEdit>,
) -> Result<(), RenameRefusal> {
    let mut untyped: Option<lang_core::Span> = None;
    walk_owned(input, &input.merged, None, &mut |e, _owner| {
        let Expr::Call { callee, .. } = e else { return };
        let Expr::Field {
            obj,
            name,
            span: field_span,
        } = callee.as_ref()
        else {
            return;
        };
        if name != old_name {
            return;
        }
        let Some(nsp) = last_ident_span(input, field_span) else {
            return;
        };
        let ty = input.analysis.type_of(obj);
        let owned = ty.and_then(|t| method_owner_of(input, t, old_name)) == Some(target);
        if owned {
            edits.push(TextEdit {
                file: file_of(&nsp),
                start_offset: nsp.start.offset,
                end_offset: nsp.end.offset,
                replacement: new_name.to_string(),
            });
        } else {
            let unknown = match ty.map(|t| input.analysis.types.data(t)) {
                None => true,
                Some(TypeData::Any) | Some(TypeData::Unknown) | Some(TypeData::Invalid) => true,
                Some(_) => false,
            };
            if unknown && untyped.is_none() {
                untyped = Some(nsp);
            }
        }
    });
    if let Some(sp) = untyped {
        return Err(RenameRefusal::UntypedReceiver(format!(
            "第 {} 行存在同名方法调用点，但接收者类型未知，无法确认归属，拒绝 rename",
            sp.start.line
        )));
    }
    Ok(())
}

/// 接收者类型 + 方法名 → 方法符号（TypeId → struct def → struct 符号 methods 表）。
fn method_owner_of(input: &RefactorInput, t: lang_core::TypeId, method: &str) -> Option<SymbolId> {
    let TypeData::Struct(def) = input.analysis.types.data(t) else {
        return None;
    };
    let sid = input
        .analysis
        .symbols
        .iter()
        .find(|s| {
            !s.is_builtin
                && s.kind == SymbolKind::Struct
                && s.nominal == Some(NominalRef::Struct(*def))
        })
        .map(|s| s.id)?;
    input.analysis.symbols.get(sid).methods.get(method).copied()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::input::RefactorInput;
    use lang_edit::apply_in_memory;

    fn input(src: &str) -> RefactorInput {
        RefactorInput::from_files(vec![("main.lang-go".to_string(), src.to_string())]).unwrap()
    }

    fn by_name(input: &RefactorInput, name: &str) -> SymbolId {
        input.resolve_target(name).unwrap()
    }

    fn apply(input: &RefactorInput, ws: &WorkspaceEdit) -> String {
        let out = apply_in_memory(ws, &input.current()).unwrap();
        out["main.lang-go"].clone()
    }

    const SRC: &str = "\
fn add(a: int, b: int): int {
    return a + b
}
fn twice(x: int): int {
    return add(x, x)
}
fn main() {
    let s = add(1, 2)
    println(s)
}
";

    #[test]
    fn rename_fn_rewrites_decl_and_all_call_sites() {
        let inp = input(SRC);
        let ws = rename(&inp, by_name(&inp, "add"), "sum").unwrap();
        // 定义位 + 两处调用点
        assert_eq!(ws.edits.len(), 3);
        let out = apply(&inp, &ws);
        assert!(out.contains("fn sum(a: int, b: int): int"));
        assert!(!out.contains("return add_not"));
        assert!(out.contains("sum(x, x)"));
        assert!(out.contains("let s = sum(1, 2)"));
        assert!(!out.contains("add("));
    }

    #[test]
    fn rename_var_covers_read_and_write() {
        let src = "fn main() {\n    var n = 1\n    n = n + 1\n    println(n)\n}\n";
        let inp = input(src);
        let ws = rename(&inp, by_name(&inp, "n"), "acc").unwrap();
        let out = apply(&inp, &ws);
        assert!(out.contains("var acc = 1"));
        assert!(out.contains("acc = acc + 1"));
        assert!(out.contains("println(acc)"));
    }

    #[test]
    fn rename_param_rewrites_decl_and_body_refs() {
        let src = "fn f(alpha: int): int {\n    return alpha + 1\n}\n";
        let inp = input(src);
        let ws = rename(&inp, by_name(&inp, "alpha"), "base").unwrap();
        let out = apply(&inp, &ws);
        assert!(out.contains("fn f(base: int): int"));
        assert!(out.contains("return base + 1"));
    }

    #[test]
    fn rename_method_covers_call_site_via_receiver_type() {
        let src = "struct P {\n    x: int\n}\nfn (p P).len2(): int {\n    return p.x * p.x\n}\nfn main() {\n    let q = P { x: 3 }\n    println(q.len2())\n}\n";
        let inp = input(src);
        let ws = rename(&inp, by_name(&inp, "len2"), "area").unwrap();
        let out = apply(&inp, &ws);
        assert!(out.contains("fn (p P).area(): int"));
        assert!(out.contains("q.area()"));
        assert!(!out.contains("len2"));
    }

    #[test]
    fn rename_match_binding_works() {
        // match 臂的绑定变量（Pat::Bind 符号）改名
        let src = "enum E {\n    A\n}\nfn main() {\n    let v = match A {\n        A => 7\n    }\n    println(v)\n}\n";
        let inp = input(src);
        let ws = rename(&inp, by_name(&inp, "v"), "answer").unwrap();
        let out = apply(&inp, &ws);
        assert!(out.contains("let answer = match A"));
        assert!(out.contains("println(answer)"));
    }

    #[test]
    fn struct_rename_refused() {
        let src = "struct P {\n    x: int\n}\nfn main() {}\n";
        let inp = input(src);
        let err = rename(&inp, by_name(&inp, "P"), "Q").unwrap_err();
        assert!(matches!(err, RenameRefusal::UnsupportedKind(_)));
    }

    #[test]
    fn conflict_with_existing_name_refused() {
        let inp = input(SRC);
        let err = rename(&inp, by_name(&inp, "add"), "twice").unwrap_err();
        assert!(matches!(err, RenameRefusal::Conflict(_)));
    }

    #[test]
    fn invalid_and_reserved_names_refused() {
        let inp = input(SRC);
        let t = by_name(&inp, "add");
        assert!(matches!(
            rename(&inp, t, "1bad").unwrap_err(),
            RenameRefusal::InvalidName(_)
        ));
        assert!(matches!(
            rename(&inp, t, "fn").unwrap_err(),
            RenameRefusal::InvalidName(_)
        ));
        assert!(matches!(
            rename(&inp, t, "println").unwrap_err(),
            RenameRefusal::InvalidName(_)
        ));
        assert!(matches!(
            rename(&inp, t, "").unwrap_err(),
            RenameRefusal::InvalidName(_)
        ));
    }

    #[test]
    fn untyped_receiver_refuses_conservatively() {
        // 无注解参数作接收者 → 同名调用点类型未知 → 拒绝（不半改）
        let src = "struct P {\n    x: int\n}\nfn (p P).m(): int {\n    return p.x\n}\nfn probe(q) {\n    return q.m()\n}\nfn main() {\n    let z = P { x: 3 }\n    println(z.m())\n    println(probe(z))\n}\n";
        let inp = input(src);
        let err = rename(&inp, by_name(&inp, "m"), "n").unwrap_err();
        assert!(matches!(err, RenameRefusal::UntypedReceiver(_)));
    }

    #[test]
    fn resolve_target_by_name_and_id() {
        let inp = input(SRC);
        let add = by_name(&inp, "add");
        let by_num = inp.resolve_target(&add.0.to_string()).unwrap();
        assert_eq!(by_num, add);
        // 预载符号 id（0..20）不可指名
        assert!(inp.resolve_target("2").is_err());
        assert!(inp.resolve_target("999").is_err());
        assert!(inp.resolve_target("").is_err());
    }
}
