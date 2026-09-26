//! 内建注册表与调用口（specs/runtime.md 输出面 / CHAN 面）。
//!
//! 注册表锁定内建**名字面**（name + 元数 + 类别）；执行分两类：
//! - 运行时自有：`println/print`（输出面）、chan 三件套、纯值内建
//!   （`len/str/int/float/keys/values/has/push/assert/err/bytes/char/isNone`），
//!   由 `Machine::call_builtin` 实现；
//! - 宿主类（`Host`）：`jsonParse/jsonStringify/http.get` 等——具体格式化与
//!   IO 规则由 A5/解释器波次实现，运行时只保证名字注册与调用路由口。
//!
//! 输出格式化细节（float 安全区、%q、JSON 字节级契约）是 A5 的领地：
//! 运行时经 [`ValueFormatter`] 钩子取得格式化结果，不自带格式化器。

use crate::error::{RtError, RtErrorKind, RtResult};
use crate::machine::Machine;
use crate::value::Value;

/// 内建身份（注册表下标 newtype）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct BuiltinId(pub u32);

impl From<BuiltinId> for usize {
    fn from(v: BuiltinId) -> usize {
        v.0 as usize
    }
}

impl std::fmt::Display for BuiltinId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// 内建类别（决定调用路由与元数检查）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum BuiltinKind {
    /// `println(args...)`：空格分隔 + 换行（RUNTIME-OUTPUT-003）。
    Println,
    /// `print(args...)`：空格分隔，无换行。
    Print,
    /// `len(x)`：str 码点数 / 数组 / 映射 / 区间。
    Len,
    /// `str(v)`：值的顶层文本形。
    Str,
    /// `int(v)`。
    Int,
    /// `float(v)`。
    Float,
    /// `keys(m)`：插入序键数组。
    Keys,
    /// `values(m)`：插入序值数组。
    Values,
    /// `has(m, k)`：合法键探测。
    Has,
    /// `push(arr, v...)`：原位追加。
    Push,
    /// `assert(cond, msg?)`。
    Assert,
    /// `err(msg)`：构造 `error{msg}` struct。
    Err,
    /// `bytes(s)`：UTF-8 字节数组。
    Bytes,
    /// `char(n)`：码点 → 单字符字符串（非法码点 → U+FFFD）。
    Char,
    /// `makeChan()`。
    MakeChan,
    /// `send(ch, v)`。
    Send,
    /// `recv(ch)`。
    Recv,
    /// `isNone(v)`。
    IsNone,
    /// 宿主实现（jsonParse/jsonStringify/http.get 等）：调用口存在，实现归宿主。
    Host,
}

/// 内建注册项：名字 + 类别 + 元数区间（min..=max；None 表示变长上限）。
#[derive(Clone, Copy, Debug)]
struct Entry {
    name: &'static str,
    kind: BuiltinKind,
    min_args: u32,
    max_args: Option<u32>,
}

const fn e(name: &'static str, kind: BuiltinKind, min: u32, max: Option<u32>) -> Entry {
    Entry {
        name,
        kind,
        min_args: min,
        max_args: max,
    }
}

/// 内建注册表：名字面冻结（与运行时证据 CORE_BUILTINS + stdlib 对齐）。
#[derive(Debug, Default)]
pub struct BuiltinRegistry {
    by_name: std::collections::HashMap<&'static str, BuiltinId>,
    entries: Vec<Entry>,
}

impl BuiltinRegistry {
    /// 空表。
    pub fn new() -> BuiltinRegistry {
        BuiltinRegistry::default()
    }

    /// 核心注册表：语言标准内建全量名字面。
    pub fn core() -> BuiltinRegistry {
        let mut r = BuiltinRegistry::new();
        for entry in [
            e("println", BuiltinKind::Println, 0, None),
            e("print", BuiltinKind::Print, 0, None),
            e("len", BuiltinKind::Len, 1, Some(1)),
            e("str", BuiltinKind::Str, 1, Some(1)),
            e("int", BuiltinKind::Int, 1, Some(1)),
            e("float", BuiltinKind::Float, 1, Some(1)),
            e("keys", BuiltinKind::Keys, 1, Some(1)),
            e("values", BuiltinKind::Values, 1, Some(1)),
            e("has", BuiltinKind::Has, 2, Some(2)),
            e("push", BuiltinKind::Push, 2, None),
            e("assert", BuiltinKind::Assert, 1, Some(2)),
            e("err", BuiltinKind::Err, 1, Some(1)),
            e("bytes", BuiltinKind::Bytes, 1, Some(1)),
            e("char", BuiltinKind::Char, 1, Some(1)),
            e("makeChan", BuiltinKind::MakeChan, 0, Some(0)),
            e("send", BuiltinKind::Send, 2, Some(2)),
            e("recv", BuiltinKind::Recv, 1, Some(1)),
            e("isNone", BuiltinKind::IsNone, 1, Some(1)),
            e("jsonParse", BuiltinKind::Host, 1, Some(1)),
            e("jsonStringify", BuiltinKind::Host, 1, Some(1)),
        ] {
            r.register_entry(entry);
        }
        r
    }

    fn register_entry(&mut self, entry: Entry) -> BuiltinId {
        let id = BuiltinId(self.entries.len() as u32);
        self.entries.push(entry);
        self.by_name.insert(entry.name, id);
        id
    }

    /// 登记宿主内建（名字锁定，执行由宿主接手）。
    pub fn register_host(&mut self, name: &'static str) -> BuiltinId {
        self.register_entry(e(name, BuiltinKind::Host, 0, None))
    }

    /// 名字解析（静态解析结论的运行时余量；CALL-RESOLVE-001）。
    pub fn lookup(&self, name: &str) -> Option<BuiltinId> {
        self.by_name.get(name).copied()
    }

    pub fn kind(&self, id: BuiltinId) -> RtResult<BuiltinKind> {
        Ok(self.entry(id)?.kind)
    }

    pub fn name(&self, id: BuiltinId) -> RtResult<&'static str> {
        Ok(self.entry(id)?.name)
    }

    /// 元数检查（内建实参不符为运行时错误；用户函数静态 E3003）。
    pub(crate) fn check_arity(&self, id: BuiltinId, args: &[Value]) -> RtResult<()> {
        let entry = self.entry(id)?;
        let n = args.len() as u32;
        let ok = n >= entry.min_args && entry.max_args.map(|m| n <= m).unwrap_or(true);
        if ok {
            return Ok(());
        }
        let want = match (entry.min_args, entry.max_args) {
            (a, Some(b)) if a == b => format!("{} 个参数", a),
            (a, Some(b)) => format!("{}~{} 个参数", a, b),
            (a, None) => format!("至少 {} 个参数", a),
        };
        Err(RtError::new(
            RtErrorKind::TypeCast,
            format!("{} 需要{}，得到 {} 个", entry.name, want, n),
        ))
    }

    fn entry(&self, id: BuiltinId) -> RtResult<&Entry> {
        self.entries
            .get(usize::from(id))
            .ok_or_else(|| RtError::internal(format!("BuiltinId({}) 越界", usize::from(id))))
    }
}

/// 输出汇：`println`/`print` 的落点（进程 stdout 或测试缓冲）。
pub trait OutputSink {
    /// 原样写出（不自动换行）。
    fn write(&mut self, s: &str);
}

/// 进程 stdout 输出汇。
#[derive(Clone, Copy, Debug, Default)]
pub struct StdoutSink;

impl OutputSink for StdoutSink {
    fn write(&mut self, s: &str) {
        use std::io::Write;
        let mut out = std::io::stdout().lock();
        let _ = out.write_all(s.as_bytes());
        let _ = out.flush();
    }
}

/// 值格式化钩子：输出面细节（float 安全区、%q 引号、struct/enum 形）
/// 由 A5/解释器波次提供实现；运行时只消费结果。
///
/// `top=true` 时字符串顶层不带引号（RUNTIME-OUTPUT-003）。
pub trait ValueFormatter {
    fn fmt(&self, machine: &Machine, v: &Value, top: bool) -> String;
}

/// 把实参格式化为空格分隔的输出行体（println/print 共用）。
pub fn fmt_args(machine: &Machine, fmt: &dyn ValueFormatter, args: &[Value]) -> String {
    args.iter()
        .map(|v| fmt.fmt(machine, v, true))
        .collect::<Vec<_>>()
        .join(" ")
}

/// 内建调用错误：类型不符统一走 `RtErrorKind::TypeCast`（消息含期望类型）。
pub(crate) fn type_err(name: &str, want: &str, got: &Value) -> RtError {
    RtError::new(
        RtErrorKind::TypeCast,
        format!("{} 需要{}，得到 {}", name, want, got.type_hint()),
    )
}

/// 运行时自有内建的执行分发（`Machine::call_builtin` 的主体）。
/// `Host` 类别返回 `UnsupportedHost`——宿主应在路由前拦截。
pub(crate) fn dispatch(
    machine: &mut Machine,
    out: &mut dyn OutputSink,
    fmt: &dyn ValueFormatter,
    id: BuiltinId,
    args: Vec<Value>,
) -> RtResult<Value> {
    // 先取出 Copy 的类别/名字，避免与后续可变操作借用冲突
    let (kind, name) = {
        let reg = &machine.builtins;
        (reg.kind(id)?, reg.name(id)?)
    };
    machine.builtins.check_arity(id, &args)?;
    match kind {
        BuiltinKind::Host => Err(RtError::new(
            RtErrorKind::UnsupportedHost,
            format!("内建 {} 由宿主实现（当前调用口不承接）", name),
        )),
        BuiltinKind::Println => {
            let line = fmt_args(machine, fmt, &args);
            out.write(&line);
            out.write("\n");
            Ok(Value::Unit)
        }
        BuiltinKind::Print => {
            let line = fmt_args(machine, fmt, &args);
            out.write(&line);
            Ok(Value::Unit)
        }
        BuiltinKind::Len => builtin_len(machine, args),
        BuiltinKind::Str => Ok(Value::str(fmt.fmt(machine, &args[0], true))),
        BuiltinKind::Int => builtin_int(&args[0]),
        BuiltinKind::Float => builtin_float(&args[0]),
        BuiltinKind::Keys => {
            let m = expect_map("keys", &args[0])?;
            Ok(machine.heap.alloc_slice(machine.heap.map_keys(m)?))
        }
        BuiltinKind::Values => {
            let m = expect_map("values", &args[0])?;
            Ok(machine.heap.alloc_slice(machine.heap.map_values(m)?))
        }
        BuiltinKind::Has => {
            let m = expect_map("has", &args[0])?;
            let key = crate::value::MapKey::from_value(&args[1])
                .ok_or_else(|| type_err("has", "int/string/bool 键", &args[1]))?;
            Ok(Value::Bool(machine.heap.map_has(m, &key)?))
        }
        BuiltinKind::Push => {
            let m = expect_slice("push", &args[0])?;
            for v in &args[1..] {
                machine.heap.slice_push(m, v.clone())?;
            }
            Ok(args[0].clone())
        }
        BuiltinKind::Assert => {
            let cond = args[0]
                .truthy()
                .map_err(|_| type_err("assert", "bool 条件", &args[0]))?;
            if cond {
                Ok(Value::Unit)
            } else {
                let msg = if args.len() == 2 {
                    format!("assert 失败: {}", fmt.fmt(machine, &args[1], true))
                } else {
                    "assert 失败".to_string()
                };
                Err(RtError::new(RtErrorKind::Assert, msg))
            }
        }
        BuiltinKind::Err => match &args[0] {
            Value::Str(msg) => Ok(machine.error_value(msg.clone())),
            other => Err(type_err("err", "string", other)),
        },
        BuiltinKind::Bytes => match &args[0] {
            Value::Str(s) => Ok(machine
                .heap
                .alloc_slice(s.as_bytes().iter().map(|&b| Value::Int(b as i64)).collect())),
            other => Err(type_err("bytes", "string", other)),
        },
        BuiltinKind::Char => match &args[0] {
            // Go string(rune(n))：非法码点/代理区 → U+FFFD（证据行为）
            Value::Int(n) => {
                let cp = if *n < 0 || *n > 0x10_FFFF || (0xD800..=0xDFFF).contains(n) {
                    0xFFFDu32
                } else {
                    *n as u32
                };
                let c = char::from_u32(cp).unwrap_or('\u{FFFD}');
                Ok(Value::str(c.to_string()))
            }
            other => Err(type_err("char", "int", other)),
        },
        BuiltinKind::MakeChan => Ok(machine.make_chan_value()),
        BuiltinKind::Send => {
            let ch = expect_chan(&args[0])?;
            machine.chans.send(ch, args[1].clone())?;
            Ok(Value::Unit)
        }
        BuiltinKind::Recv => {
            let ch = expect_chan(&args[0])?;
            match machine.chans.try_recv(ch)? {
                crate::chan::RecvOutcome::Value(v) => Ok(machine.ok_value(v)),
                crate::chan::RecvOutcome::Empty => {
                    // CHAN-RECV-003：空且无并发 send 方 → Err("channel 空") 值
                    Ok(machine.err_value(Value::str(crate::chan::CHAN_EMPTY_MSG)))
                }
            }
        }
        BuiltinKind::IsNone => {
            // 证据口径：Option 的 None case → true；非 enum 值 → false
            let is_none = match &args[0] {
                Value::Enum { def, case, .. } => *def == machine.defs.option_def() && *case == 1,
                _ => false,
            };
            Ok(Value::Bool(is_none))
        }
    }
}

fn expect_slice(name: &str, v: &Value) -> RtResult<crate::heap::ValueId> {
    match v {
        Value::Slice(id) => Ok(*id),
        other => Err(type_err(name, "数组", other)),
    }
}

fn expect_map(name: &str, v: &Value) -> RtResult<crate::heap::ValueId> {
    match v {
        Value::Map(id) => Ok(*id),
        other => Err(type_err(name, "映射", other)),
    }
}

fn expect_chan(v: &Value) -> RtResult<crate::chan::ChannelId> {
    match v {
        Value::Chan(id) => Ok(*id),
        other => Err(type_err("channel 操作", "chan", other)),
    }
}

fn builtin_len(machine: &Machine, args: Vec<Value>) -> RtResult<Value> {
    let n = match &args[0] {
        // lexical-grammar.md：len() 对字符串返回码点数
        Value::Str(s) => s.chars().count() as i64,
        Value::Slice(id) => machine.heap.slice_len(*id)? as i64,
        Value::Map(id) => machine.heap.map_len(*id)? as i64,
        // 证据口径：hi <= lo 的区间长度为 0（半开区间）
        Value::Range { lo, hi } => {
            if hi > lo {
                hi - lo
            } else {
                0
            }
        }
        other => return Err(type_err("len", "string/数组/映射/区间", other)),
    };
    Ok(Value::Int(n))
}

fn builtin_int(v: &Value) -> RtResult<Value> {
    match v {
        Value::Int(_) => Ok(v.clone()),
        // Go int64(f)：向零截断；越界/非有限为平台定义——按 amd64 取 MinInt64（证据口径）
        Value::Float(f) => {
            const I64_AS_F64: f64 = 9_223_372_036_854_775_808.0;
            if !f.is_finite() || f.abs() >= I64_AS_F64 {
                return Ok(Value::Int(i64::MIN));
            }
            Ok(Value::Int(f.trunc() as i64))
        }
        Value::Str(s) => {
            let trimmed = s.trim();
            match trimmed.parse::<i64>() {
                Ok(n) => Ok(Value::Int(n)),
                Err(_) => Err(RtError::new(
                    RtErrorKind::TypeCast,
                    format!("无法把 {:?} 转为 int", trimmed),
                )),
            }
        }
        other => Err(type_err("int()", "int/float/string", other)),
    }
}

fn builtin_float(v: &Value) -> RtResult<Value> {
    match v {
        Value::Int(i) => Ok(Value::Float(*i as f64)),
        Value::Float(_) => Ok(v.clone()),
        Value::Str(s) => {
            let trimmed = s.trim();
            match trimmed.parse::<f64>() {
                Ok(f) => Ok(Value::Float(f)),
                Err(_) => Err(RtError::new(
                    RtErrorKind::TypeCast,
                    format!("无法把 {:?} 转为 float", trimmed),
                )),
            }
        }
        other => Err(type_err("float()", "int/float/string", other)),
    }
}
