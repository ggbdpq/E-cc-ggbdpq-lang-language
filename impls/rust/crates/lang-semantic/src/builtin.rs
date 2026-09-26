//! 内建面：函数名单（resolver 预载）+ 返回类型特化（checker 消费）。
//!
//! 名单与 lang-runtime 冻结的 20 个内建一致（docs/lang-rs/status/A4-runtime.md）；
//! TS/Go 证据里的 `http` 服务值不在 A4 冻结面，本实现不预载。

use lang_types::{TypeData, TypeId, TypeTables};

/// 内建函数名单（与 interp/gen 三方证据一致；顺序即注册序）。
pub const BUILTIN_FNS: [&str; 20] = [
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
];

/// 预载 struct 名（RESULT-PRELUDE-001 的 error；HttpResp 属宿主服务面不预载）。
pub const PRELUDE_STRUCTS: [&str; 1] = ["error"];

/// 预载构造器（与 A4 prelude case 序号一致）。
pub const PRELUDE_CTORS: [(&str, &str, u32, usize); 4] = [
    ("Some", "Option", 0, 1),
    ("None", "Option", 1, 0),
    ("Ok", "Result", 0, 1),
    ("Err", "Result", 1, 1),
];

/// 内建调用返回类型（参数不检查该位；返回类型依实参特化）。
#[must_use]
pub fn builtin_return(t: &mut TypeTables, name: &str, args: &[TypeId]) -> TypeId {
    match name {
        "println" | "print" | "assert" | "send" => TypeTables::UNIT,
        "len" | "int" => TypeTables::INT,
        "float" => TypeTables::FLOAT,
        "str" | "char" | "jsonStringify" => TypeTables::STR,
        "keys" | "values" => t.intern(TypeData::Slice(TypeTables::UNKNOWN)),
        "has" | "isNone" => TypeTables::BOOL,
        "push" => args.first().copied().unwrap_or(TypeTables::UNKNOWN),
        "err" => TypeTables::ERROR,
        "bytes" => t.intern(TypeData::Slice(TypeTables::INT)),
        "jsonParse" => TypeTables::ANY,
        "makeChan" => t.intern(TypeData::Chan(TypeTables::UNKNOWN)),
        "recv" => {
            let ok = match args.first().map(|a| t.data(*a)) {
                Some(TypeData::Chan(elem)) => *elem,
                _ => TypeTables::UNKNOWN,
            };
            t.intern(TypeData::Result {
                ok,
                err: TypeTables::ERROR,
            })
        }
        _ => TypeTables::UNKNOWN,
    }
}

/// 是否内建函数名。
#[must_use]
pub fn is_builtin_fn(name: &str) -> bool {
    BUILTIN_FNS.contains(&name)
}
