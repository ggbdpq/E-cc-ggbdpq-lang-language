//! jsonStringify 序列化规则（RUNTIME-JSON-002 跨实现硬契约）。
//!
//! 行为证据：`impls/typescript/src/interp/value.ts` 的 `goJSONString`/`goJSONFloat` +
//! `impls/typescript/src/interp/builtin.ts` 的 `marshalJSON`（Go `encoding/json` 默认
//! HTML 转义镜像）。期望值全部手算。
//!
//! 已知实现分歧登记于 SPEC-GAP-RS-002/-003（→JS prelude 浮点区间、非字符串键），
//! 本层以解释器语义为准，不照抄 prelude 分歧。

use crate::fmt::Node;

/// 序列化失败（运行时面：RUNTIME-FAIL-001，进程以非零码终止）。
/// 消息文本与参考实现对齐，供上层原样输出。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct JsonError(pub String);

/// lang 值 → JSON 文本。
///
/// - 映射键按**码点序**输出（Rust `str` 序 = UTF-8 字节序 = 码点序）
/// - None/unit → `null`
/// - int 全域十进制直出（不经浮点，i64 全精度）
/// - float 按 Go `encoding/json` 形：abs ∈ [1e-6, 1e21) 定点，否则 `e` 形（负指数去前导零）
/// - 字符串 HTML 转义开启（`< > &` → `\u003c \u003e \u0026`）
/// - 数组元素 `,` 连接无空格；映射 `{k:v,...}` 无空格
pub fn stringify(v: &Node) -> Result<String, JsonError> {
    match v {
        Node::Unit | Node::Option(None) => Ok("null".to_string()),
        Node::Bool(b) => Ok(if *b { "true" } else { "false" }.to_string()),
        Node::Int(i) => Ok(i.to_string()),
        Node::Float(f) => json_float(*f),
        Node::Str(s) => Ok(json_string(s)),
        Node::Arr(items) => {
            let mut out = String::from("[");
            for (i, it) in items.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                out.push_str(&stringify(it)?);
            }
            out.push(']');
            Ok(out)
        }
        Node::Map(kvs) => {
            // 键必须全为 string（解释器语义）；按码点序输出
            let mut keys: Vec<(&str, &Node)> = Vec::with_capacity(kvs.len());
            for (k, val) in kvs {
                match k {
                    Node::Str(s) => keys.push((s, val)),
                    other => {
                        return Err(JsonError(format!(
                            "JSON 对象键须为 string，得到 {}",
                            type_name(other)
                        )));
                    }
                }
            }
            keys.sort_by(|a, b| a.0.cmp(b.0));
            let mut out = String::from("{");
            for (i, (k, val)) in keys.iter().enumerate() {
                if i > 0 {
                    out.push(',');
                }
                out.push_str(&json_string(k));
                out.push(':');
                out.push_str(&stringify(val)?);
            }
            out.push('}');
            Ok(out)
        }
        other => Err(JsonError(format!("{} 不可 JSON 序列化", node_tag(other)))),
    }
}

/// Go `encoding/json` 字符串编码镜像（HTML 转义开启）：
/// `" \` → 转义；`< > &` → `\u003c/\u003e/\u0026`；`\b \f \n \r \t` 短形；
/// 其余 < 0x20 → `\u00XX`（小写）；其余码点直出（含 DEL、U+2028——Go json 不转义）。
pub fn json_string(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '<' => out.push_str("\\u003c"),
            '>' => out.push_str("\\u003e"),
            '&' => out.push_str("\\u0026"),
            '\u{08}' => out.push_str("\\b"),
            '\u{0c}' => out.push_str("\\f"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            c if (c as u32) < 0x20 => out.push_str(&format!("\\u{:04x}", c as u32)),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// Go `encoding/json` 浮点编码镜像：
/// abs != 0 且 (abs < 1e-6 或 abs >= 1e21) → `d[.ddd]e±X` 形（负指数去前导零，
/// 正指数恒 >= 21 天然两位）；否则最短定点。NaN/Inf 非法。
/// `-0.0` → `"0"`（解释器语义；与 Go json 的 `-0` 分歧记 SPEC-GAP-RS-002）。
pub fn json_float(f: f64) -> Result<String, JsonError> {
    if f.is_nan() {
        return Err(JsonError(
            "JSON 序列化失败: json: unsupported value: NaN".into(),
        ));
    }
    if f.is_infinite() {
        let name = if f > 0.0 { "+Inf" } else { "-Inf" };
        return Err(JsonError(format!(
            "JSON 序列化失败: json: unsupported value: {name}"
        )));
    }
    let a = f.abs();
    let body = if a != 0.0 && (a < 1e-6 || a >= 1e21) {
        let (digits, exp) = crate::fmt::shortest_digits(a);
        let mut mant = String::with_capacity(digits.len() + 1);
        mant.push_str(&digits[..1]);
        if digits.len() > 1 {
            mant.push('.');
            mant.push_str(&digits[1..]);
        }
        let sign = if exp < 0 { '-' } else { '+' };
        format!("{mant}e{sign}{}", exp.abs())
    } else {
        let (digits, exp) = crate::fmt::shortest_digits(a);
        crate::fmt::fixed_digits(&digits, exp)
    };
    if f < 0.0 {
        Ok(format!("-{body}"))
    } else {
        Ok(body)
    }
}

/// 错误消息用的类型名（对齐解释器 typeName）。
fn type_name(v: &Node) -> String {
    match v {
        Node::Unit => "()".into(),
        Node::Int(_) => "int".into(),
        Node::Float(_) => "float".into(),
        Node::Bool(_) => "bool".into(),
        Node::Str(_) => "string".into(),
        Node::Fn(_) => "函数".into(),
        Node::Range(..) => "区间".into(),
        Node::Ptr(_) => "引用".into(),
        Node::Arr(_) => "数组".into(),
        Node::Map(_) => "映射".into(),
        Node::Struct { name, .. } => name.clone(),
        Node::Enum { .. } => "enum".into(),
        Node::Option(_) => "Option".into(),
        Node::Result(_) => "Result".into(),
        Node::Error { .. } => "error".into(),
    }
}

/// 序列化拒绝消息里的值名：enum 用 case 名，其余用类型名。
fn node_tag(v: &Node) -> String {
    match v {
        Node::Enum { tag, .. } => tag.clone(),
        Node::Option(Some(_)) => "Some".into(),
        Node::Result(Ok(_)) => "Ok".into(),
        Node::Result(Err(_)) => "Err".into(),
        other => type_name(other),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn null_forms() {
        assert_eq!(stringify(&Node::Unit).unwrap(), "null");
        assert_eq!(stringify(&Node::Option(None)).unwrap(), "null");
    }

    #[test]
    fn scalars() {
        assert_eq!(stringify(&Node::Bool(true)).unwrap(), "true");
        assert_eq!(stringify(&Node::Bool(false)).unwrap(), "false");
        assert_eq!(stringify(&Node::Str("hi".into())).unwrap(), "\"hi\"");
    }

    #[test]
    fn int64_full_precision() {
        // 大整数不经浮点：2^53+1 若走 f64 会丢成 ...992
        assert_eq!(
            stringify(&Node::Int(9007199254740993)).unwrap(),
            "9007199254740993"
        );
        assert_eq!(
            stringify(&Node::Int(i64::MIN)).unwrap(),
            "-9223372036854775808"
        );
        assert_eq!(
            stringify(&Node::Int(i64::MAX)).unwrap(),
            "9223372036854775807"
        );
    }

    #[test]
    fn float_encoding_thresholds() {
        // Go encoding/json：abs ∈ [1e-6, 1e21) 定点，否则 e 形
        assert_eq!(stringify(&Node::Float(123.456)).unwrap(), "123.456");
        assert_eq!(stringify(&Node::Float(1e8)).unwrap(), "100000000"); // prelude 分歧案例（SPEC-GAP-RS-002）
        assert_eq!(
            stringify(&Node::Float(1e20)).unwrap(),
            "100000000000000000000"
        );
        assert_eq!(stringify(&Node::Float(1e21)).unwrap(), "1e+21");
        assert_eq!(stringify(&Node::Float(1e100)).unwrap(), "1e+100");
        assert_eq!(stringify(&Node::Float(1e-6)).unwrap(), "0.000001"); // 端点归定点
        assert_eq!(stringify(&Node::Float(9.9e-7)).unwrap(), "9.9e-7"); // 负指数去前导零
        assert_eq!(stringify(&Node::Float(1e-7)).unwrap(), "1e-7");
        assert_eq!(stringify(&Node::Float(0.0)).unwrap(), "0");
        assert_eq!(stringify(&Node::Float(-0.0)).unwrap(), "0"); // interp 语义（SPEC-GAP-RS-002）
        assert_eq!(stringify(&Node::Float(-2.5)).unwrap(), "-2.5");
    }

    #[test]
    fn float_specials_rejected() {
        assert_eq!(
            stringify(&Node::Float(f64::NAN)).unwrap_err(),
            JsonError("JSON 序列化失败: json: unsupported value: NaN".into())
        );
        assert_eq!(
            stringify(&Node::Float(f64::INFINITY)).unwrap_err(),
            JsonError("JSON 序列化失败: json: unsupported value: +Inf".into())
        );
        assert_eq!(
            stringify(&Node::Float(f64::NEG_INFINITY)).unwrap_err(),
            JsonError("JSON 序列化失败: json: unsupported value: -Inf".into())
        );
    }

    #[test]
    fn string_html_escaping_and_controls() {
        assert_eq!(
            stringify(&Node::Str("a<b>&c".into())).unwrap(),
            "\"a\\u003cb\\u003e\\u0026c\""
        );
        assert_eq!(
            stringify(&Node::Str("q\"q\\s".into())).unwrap(),
            "\"q\\\"q\\\\s\""
        );
        assert_eq!(stringify(&Node::Str("\n\t".into())).unwrap(), "\"\\n\\t\"");
        assert_eq!(
            stringify(&Node::Str("\u{1}".into())).unwrap(),
            "\"\\u0001\""
        );
        // DEL 与 U+2028 直出（Go json 不转义；区别于 strconv.Quote）
        assert_eq!(
            stringify(&Node::Str("\u{7f}".into())).unwrap(),
            "\"\u{7f}\""
        );
        assert_eq!(
            stringify(&Node::Str("\u{2028}".into())).unwrap(),
            "\"\u{2028}\""
        );
    }

    #[test]
    fn map_keys_sorted_by_codepoint() {
        // 插入序 z,a,~,Z,\u{10000} → 码点序 Z(5A) a(61) b(62)... ~(7E) \u{10000}(10000)
        let m = Node::Map(vec![
            (Node::Str("z".into()), Node::Int(1)),
            (Node::Str("a".into()), Node::Int(2)),
            (Node::Str("~".into()), Node::Int(3)),
            (Node::Str("Z".into()), Node::Int(4)),
            (Node::Str("\u{10000}".into()), Node::Int(5)),
        ]);
        assert_eq!(
            stringify(&m).unwrap(),
            "{\"Z\":4,\"a\":2,\"z\":1,\"~\":3,\"\u{10000}\":5}"
        );
        // 嵌套映射同样排序
        let inner = Node::Map(vec![
            (Node::Str("b".into()), Node::Int(1)),
            (Node::Str("a".into()), Node::Int(2)),
        ]);
        let outer = Node::Map(vec![(Node::Str("k".into()), inner)]);
        assert_eq!(stringify(&outer).unwrap(), "{\"k\":{\"a\":2,\"b\":1}}");
    }

    #[test]
    fn map_non_string_key_rejected() {
        let m = Node::Map(vec![(Node::Int(1), Node::Str("x".into()))]);
        assert_eq!(
            stringify(&m).unwrap_err(),
            JsonError("JSON 对象键须为 string，得到 int".into())
        );
    }

    #[test]
    fn arrays_and_nested_null() {
        let a = Node::Arr(vec![
            Node::Int(1),
            Node::Str("a".into()),
            Node::Option(None),
        ]);
        assert_eq!(stringify(&a).unwrap(), "[1,\"a\",null]");
    }

    #[test]
    fn non_serializable_rejected_with_tag() {
        assert_eq!(
            stringify(&Node::Option(Some(Box::new(Node::Int(1))))).unwrap_err(),
            JsonError("Some 不可 JSON 序列化".into())
        );
        assert_eq!(
            stringify(&Node::Result(Err(Box::new(Node::Str("e".into()))))).unwrap_err(),
            JsonError("Err 不可 JSON 序列化".into())
        );
        assert_eq!(
            stringify(&Node::Enum {
                tag: "Red".into(),
                payloads: vec![]
            })
            .unwrap_err(),
            JsonError("Red 不可 JSON 序列化".into())
        );
        assert_eq!(
            stringify(&Node::Range(1, 2)).unwrap_err(),
            JsonError("区间 不可 JSON 序列化".into())
        );
        let st = Node::Struct {
            name: "Point".into(),
            fields: vec![("x".into(), Node::Int(1))],
        };
        assert_eq!(
            stringify(&st).unwrap_err(),
            JsonError("Point 不可 JSON 序列化".into())
        );
    }
}
