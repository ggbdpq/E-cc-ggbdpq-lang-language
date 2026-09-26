//! 输出面：`ValueFormatter` 桥 + 宿主内建（jsonParse/jsonStringify）。
//!
//! 打印与 JSON 规则**零漂移复用** A5 已冻结的纯函数
//! （`lang_backend_go::fmt`：float = Go `strconv.FormatFloat('g',-1,64)` 镜像、
//! 嵌套字符串 = strconv.Quote 镜像；`lang_backend_go::json`：RUNTIME-JSON-002
//! 字节契约）。本模块只负责 `Value → Node` 的桥接与宿主内建路由。
//!
//! Node 形状之外的可打印值（chan / 内建函数值）在进入桥之前截获：
//! chan 打印 `?`、内建打印 `<内建 名>`（TS 证据口径；conformance 语料
//! 不打印这两类值，嵌套形态差异不可达）。

use lang_backend_go::fmt::Node;
use lang_runtime::{Machine, RtError, RtErrorKind, RtResult, Value, ValueFormatter};

/// 解释器输出面格式化器（零字段；全部经参数 `&Machine` 读堆/定义表）。
#[derive(Clone, Copy, Debug, Default)]
pub struct InterpFmt;

impl ValueFormatter for InterpFmt {
    fn fmt(&self, machine: &Machine, v: &Value, top: bool) -> String {
        match v {
            Value::Chan(_) => "?".to_string(),
            Value::Builtin(id) => {
                let name = machine
                    .builtins
                    .name(*id)
                    .map(str::to_string)
                    .unwrap_or_else(|_| "未知".to_string());
                format!("<内建 {name}>")
            }
            _ => lang_backend_go::fmt::fmt_value(&value_to_node(machine, v), top),
        }
    }
}

/// `Value → Node`（A5 抽象打印值树）：struct/enum 字段名与 case 名取自
/// 定义表；容器按插入序展开；引用打印为穿透后的 `&<嵌套形>`。
pub fn value_to_node(m: &Machine, v: &Value) -> Node {
    match v {
        Value::Int(i) => Node::Int(*i),
        Value::Float(f) => Node::Float(*f),
        Value::Bool(b) => Node::Bool(*b),
        Value::Str(s) => Node::Str(s.to_string()),
        Value::Unit => Node::Unit,
        Value::Range { lo, hi } => Node::Range(*lo, *hi),
        Value::Slice(id) => {
            let items = m
                .heap
                .slice(*id)
                .map(|xs| xs.iter().map(|it| value_to_node(m, it)).collect())
                .unwrap_or_default();
            Node::Arr(items)
        }
        Value::Map(id) => {
            let kvs = m
                .heap
                .map(*id)
                .map(|obj| {
                    obj.iter()
                        .map(|(k, v)| (value_to_node(m, k), value_to_node(m, v)))
                        .collect()
                })
                .unwrap_or_default();
            Node::Map(kvs)
        }
        Value::Struct(sv) => {
            let Ok(def) = m.defs.struct_def(sv.def) else {
                return Node::Unit;
            };
            let name = def.name.to_string();
            let fields = def
                .fields
                .iter()
                .zip(sv.fields.iter())
                .map(|(n, v)| (n.clone(), value_to_node(m, v)))
                .collect();
            Node::Struct { name, fields }
        }
        Value::Enum {
            def,
            case,
            payloads,
        } => {
            if *def == m.defs.option_def() {
                let inner = payloads.first().map(|p| Box::new(value_to_node(m, p)));
                return Node::Option(inner);
            }
            if *def == m.defs.result_def() {
                let inner = payloads.first().map(|p| Box::new(value_to_node(m, p)));
                return match (*case, inner) {
                    (0, Some(p)) => Node::Result(Ok(p)),
                    (_, Some(p)) => Node::Result(Err(p)),
                    (_, None) => Node::Result(Err(Box::new(Node::Unit))),
                };
            }
            let tag = m.enum_case_name(*def, *case).unwrap_or_default();
            let nodes: Vec<Node> = payloads.iter().map(|p| value_to_node(m, p)).collect();
            Node::Enum {
                tag,
                payloads: nodes,
            }
        }
        Value::Closure { func, .. } => {
            let name = m.fn_name(*func);
            if name.is_empty() {
                Node::Fn(None)
            } else {
                Node::Fn(Some(name.to_string()))
            }
        }
        Value::Ref(p) => match m.place_load_resolved(p) {
            Ok(inner) => Node::Ptr(Box::new(value_to_node(m, &inner))),
            Err(_) => Node::Str("?".to_string()),
        },
        // Chan/Builtin 在 ValueFormatter 入口已截获；此处仅为完备性兜底
        Value::Chan(_) | Value::Builtin(_) => Node::Str("?".to_string()),
    }
}

// ———— 宿主内建（Host 类别在 Machine::call_builtin 路由前拦截） ————

/// 宿主内建分发：jsonParse / jsonStringify（RUNTIME-JSON-001/002）。
/// 其余 Host 名（如 http.get）报「仅转译可用/宿主未提供」。
pub fn host_builtin(machine: &mut Machine, name: &str, args: Vec<Value>) -> RtResult<Value> {
    match name {
        "jsonParse" => json_parse(machine, args),
        "jsonStringify" => json_stringify(machine, args),
        other => Err(RtError::new(
            RtErrorKind::UnsupportedHost,
            format!("内建 {other} 由宿主实现（当前解释器未提供）"),
        )),
    }
}

fn arity1(name: &str, args: &[Value]) -> RtResult<()> {
    if args.len() == 1 {
        return Ok(());
    }
    Err(RtError::new(
        RtErrorKind::TypeCast,
        format!("{} 需要 1 个参数，得到 {} 个", name, args.len()),
    ))
}

/// `jsonParse(s)`：两阶段——serde_json 校验合法性，再按 json.Number 语义
/// 提取（整数优先 int64；对象键按码点序载入；null → None；对象 → 映射）。
fn json_parse(machine: &mut Machine, args: Vec<Value>) -> RtResult<Value> {
    arity1("jsonParse", &args)?;
    let Value::Str(s) = &args[0] else {
        return Err(RtError::new(
            RtErrorKind::TypeCast,
            format!("jsonParse 需要字符串，得到 {}", args[0].type_hint()),
        ));
    };
    let raw: serde_json::Value = serde_json::from_str(s).map_err(|e| {
        let detail = if e.is_eof() {
            "unexpected end of JSON input"
        } else {
            "invalid character looking for beginning of value"
        };
        RtError::new(RtErrorKind::JsonParse, format!("JSON 解析失败: {detail}"))
    })?;
    json_to_value(machine, &raw)
}

fn json_to_value(machine: &mut Machine, raw: &serde_json::Value) -> RtResult<Value> {
    match raw {
        serde_json::Value::Null => Ok(machine.none_value()),
        serde_json::Value::Bool(b) => Ok(Value::Bool(*b)),
        // json.Number 语义：整数优先 int64；越 i64 域按浮点（证据口径）
        serde_json::Value::Number(n) => {
            if let Some(i) = n.as_i64() {
                Ok(Value::Int(i))
            } else if let Some(u) = n.as_u64() {
                Ok(Value::Float(u as f64))
            } else {
                match n.as_f64() {
                    Some(f) => Ok(Value::Float(f)),
                    None => Err(RtError::internal("JSON 数字无法表示为 f64")),
                }
            }
        }
        serde_json::Value::String(s) => Ok(Value::str(s.as_str())),
        serde_json::Value::Array(items) => {
            let mut vals = Vec::with_capacity(items.len());
            for it in items {
                vals.push(json_to_value(machine, it)?);
            }
            Ok(machine.alloc_slice_value(vals))
        }
        serde_json::Value::Object(map) => {
            // 键按码点序载入（RUNTIME-JSON-002；serde_json Map 的迭代序受
            // preserve_order feature 统一影响，此处显式排序保证确定）
            let mut keys: Vec<&String> = map.keys().collect();
            keys.sort();
            let mval = machine.alloc_map_value();
            let Value::Map(mid) = &mval else {
                return Err(RtError::internal("新分配映射缺索引"));
            };
            for k in keys {
                let v = json_to_value(machine, &map[k])?;
                machine.heap.map_insert(*mid, &Value::str(k.as_str()), v)?;
            }
            Ok(mval)
        }
    }
}

/// `jsonStringify(v)`：`Value → Node` 后走 A5 冻结的字节契约
/// （键码点序、HTML 转义、int64 直出、None/unit → null）。
fn json_stringify(machine: &mut Machine, args: Vec<Value>) -> RtResult<Value> {
    arity1("jsonStringify", &args)?;
    let node = value_to_node(machine, &args[0]);
    match lang_backend_go::json::stringify(&node) {
        Ok(text) => Ok(Value::str(text)),
        Err(e) => Err(RtError::new(RtErrorKind::TypeCast, e.0)),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use lang_runtime::StructVal;

    /// 手算期望（RUNTIME-OUTPUT-003/004）：桥接层不重新发明格式化，
    /// 这里锁定 Value→Node 桥的形状正确性。
    #[test]
    fn fmt_scalar_and_containers() {
        let mut m = Machine::new();
        let fmt = InterpFmt;
        assert_eq!(fmt.fmt(&m, &Value::Int(-5), true), "-5");
        assert_eq!(fmt.fmt(&m, &Value::Float(1.0), true), "1");
        assert_eq!(fmt.fmt(&m, &Value::Float(2.5), true), "2.5");
        assert_eq!(fmt.fmt(&m, &Value::Float(123456.0), true), "123456");
        assert_eq!(fmt.fmt(&m, &Value::Bool(false), true), "false");
        assert_eq!(fmt.fmt(&m, &Value::str("hi"), true), "hi");
        // 嵌套字符串带引号（strconv.Quote 语义）
        let arr = m.alloc_slice_value(vec![Value::Int(1), Value::str("s")]);
        assert_eq!(fmt.fmt(&m, &arr, true), "[1, \"s\"]");
        // 映射插入序
        let map = m.alloc_map_value();
        let Value::Map(mid) = &map else { panic!() };
        m.heap
            .map_insert(*mid, &Value::str("z"), Value::Int(26))
            .unwrap();
        m.heap
            .map_insert(*mid, &Value::str("a"), Value::Int(1))
            .unwrap();
        // 字符串键嵌套形带引号（conformance map-keys-values 真值 {"b": 2, "a": 1}）
        assert_eq!(fmt.fmt(&m, &map, true), "{\"z\": 26, \"a\": 1}");
    }

    #[test]
    fn fmt_struct_enum_option_result_error() {
        let mut m = Machine::new();
        let fmt = InterpFmt;
        let p2 = m.defs.register_struct("P2", vec!["v".to_string()]);
        let s = Value::Struct(StructVal {
            def: p2,
            fields: vec![Value::str("boom")],
        });
        assert_eq!(fmt.fmt(&m, &s, true), "P2{v: \"boom\"}");
        assert_eq!(fmt.fmt(&m, &m.some_value(Value::Int(3)), true), "Some(3)");
        assert_eq!(fmt.fmt(&m, &m.none_value(), true), "None");
        assert_eq!(fmt.fmt(&m, &m.ok_value(Value::Int(1)), true), "Ok(1)");
        let err = m.error_value("deep".into());
        assert_eq!(
            fmt.fmt(&m, &m.err_value(err), true),
            "Err(error{msg: \"deep\"})"
        );
        // 嵌套 Option
        let nested = m.some_value(m.none_value());
        assert_eq!(fmt.fmt(&m, &nested, true), "Some(None)");
    }

    #[test]
    fn host_json_parse_and_stringify() {
        let mut m = Machine::new();
        let parsed =
            host_builtin(&mut m, "jsonParse", vec![Value::str("[1, \"s\", false]")]).unwrap();
        assert_eq!(InterpFmt.fmt(&m, &parsed, true), "[1, \"s\", false]");
        // null → None；数字整/浮分型
        let parsed = host_builtin(
            &mut m,
            "jsonParse",
            vec![Value::str("{\"k\": [1, 2.5, null]}")],
        )
        .unwrap();
        assert_eq!(InterpFmt.fmt(&m, &parsed, true), "{\"k\": [1, 2.5, None]}");
        // 序列化：键码点序 + None → null
        let map = m.alloc_map_value();
        let Value::Map(mid) = &map else { panic!() };
        m.heap
            .map_insert(*mid, &Value::str("b"), Value::Int(2))
            .unwrap();
        m.heap
            .map_insert(*mid, &Value::str("a"), m.none_value())
            .unwrap();
        let out = host_builtin(&mut m, "jsonStringify", vec![map]).unwrap();
        assert_eq!(out, Value::str("{\"a\":null,\"b\":2}"));
        // 非法 JSON → JsonParse 分类
        let err = host_builtin(&mut m, "jsonParse", vec![Value::str("{")]).unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::JsonParse);
    }
}
