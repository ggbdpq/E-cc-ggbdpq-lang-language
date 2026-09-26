//! →JS 后端的输出格式化规则层（RUNTIME-OUTPUT-003 / RUNTIME-OUTPUT-004）。
//!
//! 行为证据：`impls/typescript/src/backends/jsprelude.ts` 的 `langGoFmt`/
//! `langGoFloatStr`（该函数带 FIX 注释：按 Go `strconv.FormatFloat('g',-1,64)`
//! 对齐，**不**采用 `String(v)`）与 `impls/typescript/src/interp/value.ts` 的
//! `fmtVal`。期望值全部手算。
//!
//! 与 →Go 后端的语义分歧点：嵌套字符串转义采用 JSON.stringify 语义（见
//! [`quote_json`]），与 →Go 的 `strconv.Quote` 语义在 U+2028/2029、DEL、
//! NBSP 等码点上有字节级分歧——登记为 SPEC-GAP-RS-001，本层按各自目标语义实现。

/// 抽象打印值树：IR 冻结前 fmt 层的自有形状（与 lang-backend-go 的 `fmt::Node` 同构；
/// 不可共享 crate，两后端各自持有）。映射打印保持**插入序**（Vec）。
#[derive(Debug, Clone, PartialEq)]
pub enum Node {
    Unit,
    Int(i64),
    Float(f64),
    Bool(bool),
    Str(String),
    /// 用户/匿名函数；None = 匿名
    Fn(Option<String>),
    /// 半开区间 lo..hi
    Range(i64, i64),
    /// 引用（&v），打印为 `&<嵌套形>`
    Ptr(Box<Node>),
    Arr(Vec<Node>),
    /// 键值对按插入序；键只能是 int/string/bool
    Map(Vec<(Node, Node)>),
    /// 字段按声明序
    Struct {
        name: String,
        fields: Vec<(String, Node)>,
    },
    /// 用户 enum：case 名 + 载荷
    Enum {
        tag: String,
        payloads: Vec<Node>,
    },
    /// Option[T]：None 打印 `None`，Some 打印 `Some(<嵌套形>)`
    Option(Option<Box<Node>>),
    /// Result[T, error]：`Ok(v)` / `Err(e)`
    Result(Result<Box<Node>, Box<Node>>),
    /// struct error { msg: string }，打印 `error{msg: "<JSON 引号形>"}`
    Error {
        msg: String,
    },
}

/// 值格式化（`langGoFmt` 镜像）。`top=true` 为顶层（println/str()）：字符串不带引号；
/// `top=false` 为嵌套：字符串按 JSON.stringify 语义带引号。
pub fn fmt_value(v: &Node, top: bool) -> String {
    match v {
        Node::Unit => "()".to_string(),
        Node::Int(i) => i.to_string(), // BigInt.toString(10) 全域十进制
        Node::Float(f) => fmt_float(*f),
        Node::Bool(b) => if *b { "true" } else { "false" }.to_string(),
        Node::Str(s) => {
            if top {
                s.clone()
            } else {
                quote_json(s)
            }
        }
        Node::Fn(Some(name)) => format!("<fn {name}>"),
        Node::Fn(None) => "<fn 匿名>".to_string(),
        Node::Range(lo, hi) => format!("{lo}..{hi}"),
        Node::Ptr(inner) => format!("&{}", fmt_value(inner, false)),
        Node::Arr(items) => {
            let parts: Vec<String> = items.iter().map(|it| fmt_value(it, false)).collect();
            format!("[{}]", parts.join(", "))
        }
        Node::Map(kvs) => {
            let parts: Vec<String> = kvs
                .iter()
                .map(|(k, v)| format!("{}: {}", fmt_value(k, false), fmt_value(v, false)))
                .collect();
            format!("{{{}}}", parts.join(", "))
        }
        Node::Struct { name, fields } => {
            let parts: Vec<String> = fields
                .iter()
                .map(|(f, v)| format!("{f}: {}", fmt_value(v, false)))
                .collect();
            format!("{name}{{{}}}", parts.join(", "))
        }
        Node::Enum { tag, payloads } => {
            if payloads.is_empty() {
                return tag.clone();
            }
            let parts: Vec<String> = payloads.iter().map(|p| fmt_value(p, false)).collect();
            format!("{tag}({})", parts.join(", "))
        }
        Node::Option(Some(inner)) => format!("Some({})", fmt_value(inner, false)),
        Node::Option(None) => "None".to_string(),
        Node::Result(Ok(inner)) => format!("Ok({})", fmt_value(inner, false)),
        Node::Result(Err(inner)) => format!("Err({})", fmt_value(inner, false)),
        Node::Error { msg } => format!("error{{msg: {}}}", quote_json(msg)),
    }
}

/// println/print 的实参格式化：顶层形 + 空格分隔（RUNTIME-OUTPUT-003）。
pub fn fmt_args(args: &[Node]) -> String {
    args.iter()
        .map(|a| fmt_value(a, true))
        .collect::<Vec<_>>()
        .join(" ")
}

/// float 文本形：Go `strconv.FormatFloat(f, 'g', -1, 64)` 等价
/// （jsprelude `langGoFloatStr` 带 FIX 注释声明的目标语义；非 `String(v)`）。
///
/// 规则：NaN → `NaN`；±Inf → `+Inf`/`-Inf`；±0 → `0`/`-0`；最短舍入数字；
/// 十进制指数 < -4 或 >= 6 → `d.ddde±XX`（指数至少两位、恒带符号），否则定点无尾零。
pub fn fmt_float(f: f64) -> String {
    if f.is_nan() {
        return "NaN".to_string();
    }
    if f.is_infinite() {
        return if f > 0.0 { "+Inf" } else { "-Inf" }.to_string();
    }
    let (digits, exp) = shortest_digits(f.abs());
    // 条件按 Go strconv 源码逐字镜像（exp < -4 || exp >= 6 走 %e），不改为 range 形
    #[allow(clippy::manual_range_contains)]
    let body = if exp < -4 || exp >= 6 {
        let mut mant = String::with_capacity(digits.len() + 1);
        mant.push_str(&digits[..1]);
        if digits.len() > 1 {
            mant.push('.');
            mant.push_str(&digits[1..]);
        }
        let sign = if exp < 0 { '-' } else { '+' };
        format!("{mant}e{sign}{:02}", exp.abs())
    } else {
        fixed_digits(&digits, exp)
    };
    if f.is_sign_negative() {
        format!("-{body}")
    } else {
        body
    }
}

/// 嵌套字符串引号：JSON.stringify 语义（jsprelude `langGoFmt` 的字符串分支）。
///
/// 仅转义 `"` `\` 与 < 0x20 控制字符（`\b \f \n \r \t` 短形，其余 `\u00XX` 小写）；
/// 其余码点直出——U+2028/2029、DEL(0x7F)、NBSP 均**不**转义。
/// 与 →Go 的 `strconv.Quote` 分歧登记 SPEC-GAP-RS-001。
pub fn quote_json(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        let cp = c as u32;
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            '\u{08}' => out.push_str("\\b"),
            '\u{0c}' => out.push_str("\\f"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\t' => out.push_str("\\t"),
            _ if cp < 0x20 => out.push_str(&format!("\\u{cp:04x}")),
            c => out.push(c),
        }
    }
    out.push('"');
    out
}

/// 最短舍入数字提取（与 lang-backend-go 同法：Rust LowerExp 无精度输出即最短表示）。
pub(crate) fn shortest_digits(a: f64) -> (String, i32) {
    debug_assert!(a.is_finite() && a >= 0.0);
    let s = format!("{a:e}");
    let Some((mant, exp_str)) = s.split_once('e') else {
        return (s, 0); // 不可达：LowerExp 输出恒含 'e'
    };
    let exp: i32 = exp_str.parse().unwrap_or(0);
    let digits: String = mant.chars().filter(|c| c.is_ascii_digit()).collect();
    let trimmed = digits.trim_end_matches('0');
    let digits = if trimmed.is_empty() { "0" } else { trimmed };
    (digits.to_string(), exp)
}

/// 定点形（`%f`）：不补尾零。
pub(crate) fn fixed_digits(digits: &str, exp: i32) -> String {
    let dp = exp + 1;
    if dp <= 0 {
        format!("0.{}{digits}", "0".repeat((-dp) as usize))
    } else if dp as usize >= digits.len() {
        format!("{digits}{}", "0".repeat(dp as usize - digits.len()))
    } else {
        format!("{}.{}", &digits[..dp as usize], &digits[dp as usize..])
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // —— float（langGoFloatStr 目标语义 = Go 'g'，手算真值，与 go 后端同矩阵）——

    #[test]
    fn float_no_trailing_zero_and_safe_zone() {
        assert_eq!(fmt_float(1.0), "1");
        assert_eq!(fmt_float(0.1), "0.1");
        assert_eq!(fmt_float(-2.5), "-2.5");
        assert_eq!(fmt_float(1e-4), "0.0001");
        assert_eq!(fmt_float(999999.0), "999999");
        assert_eq!(fmt_float(1e6), "1e+06");
    }

    #[test]
    fn float_scientific_switch() {
        assert_eq!(fmt_float(1e-5), "1e-05");
        assert_eq!(fmt_float(1.5e-7), "1.5e-07");
        assert_eq!(fmt_float(1e21), "1e+21");
        assert_eq!(fmt_float(1e100), "1e+100");
        assert_eq!(fmt_float(1234567.8), "1.2345678e+06");
        assert_eq!(fmt_float(123456.789), "123456.789");
    }

    #[test]
    fn float_specials() {
        assert_eq!(fmt_float(0.0), "0");
        assert_eq!(fmt_float(-0.0), "-0");
        assert_eq!(fmt_float(f64::NAN), "NaN");
        assert_eq!(fmt_float(f64::INFINITY), "+Inf");
        assert_eq!(fmt_float(f64::NEG_INFINITY), "-Inf");
        assert_eq!(fmt_float(5e-324), "5e-324");
        assert_eq!(fmt_float(f64::MAX), "1.7976931348623157e+308");
    }

    // —— 嵌套字符串 quote（JSON.stringify 语义；与 go 后端分歧点逐一手算锁定）——

    #[test]
    fn quote_json_basic_escapes() {
        assert_eq!(quote_json("hi"), "\"hi\"");
        assert_eq!(quote_json("a\"b\\c"), "\"a\\\"b\\\\c\"");
        assert_eq!(quote_json("d\u{1}e"), "\"d\\u0001e\"");
        assert_eq!(quote_json("x\ny\tz\r"), "\"x\\ny\\tz\\r\"");
    }

    #[test]
    fn quote_json_unicode_raw_policy() {
        // 与 strconv.Quote 的分歧面：以下码点 JSON.stringify 直出（go 后端转义）
        assert_eq!(quote_json("\u{2028}"), "\"\u{2028}\"");
        assert_eq!(quote_json("\u{7f}"), "\"\u{7f}\"");
        assert_eq!(quote_json("\u{a0}"), "\"\u{a0}\"");
        // 打印 Unicode 直出（两后端一致）
        assert_eq!(quote_json("中文"), "\"中文\"");
        assert_eq!(quote_json("\u{1f600}"), "\"\u{1f600}\"");
    }

    // —— 标量与容器规范打印形（langGoFmt 分派镜像）——

    #[test]
    fn int_full_range_decimal() {
        assert_eq!(
            fmt_value(&Node::Int(i64::MIN), true),
            "-9223372036854775808"
        );
        assert_eq!(fmt_value(&Node::Int(i64::MAX), true), "9223372036854775807");
    }

    #[test]
    fn top_string_unquoted_nested_quoted() {
        let s = Node::Str("a b".into());
        assert_eq!(fmt_value(&s, true), "a b");
        assert_eq!(fmt_value(&s, false), "\"a b\"");
    }

    #[test]
    fn container_shapes() {
        let arr = Node::Arr(vec![Node::Int(1), Node::Str("a".into()), Node::Float(2.5)]);
        assert_eq!(fmt_value(&arr, true), "[1, \"a\", 2.5]");

        let st = Node::Struct {
            name: "Point".into(),
            fields: vec![("x".into(), Node::Int(1)), ("y".into(), Node::Float(2.5))],
        };
        assert_eq!(fmt_value(&st, true), "Point{x: 1, y: 2.5}");
        assert_eq!(fmt_value(&Node::Range(-3, 4), true), "-3..4");
    }

    #[test]
    fn map_prints_insertion_order() {
        let m = Node::Map(vec![
            (Node::Str("b".into()), Node::Int(2)),
            (Node::Int(1), Node::Str("x".into())),
            (Node::Bool(true), Node::Unit),
        ]);
        assert_eq!(fmt_value(&m, true), "{\"b\": 2, 1: \"x\", true: ()}");
    }

    #[test]
    fn enum_opt_res_error_shapes() {
        let red = Node::Enum {
            tag: "Red".into(),
            payloads: vec![],
        };
        assert_eq!(fmt_value(&red, true), "Red");
        let pair = Node::Enum {
            tag: "Pair".into(),
            payloads: vec![Node::Int(1), Node::Str("a".into())],
        };
        assert_eq!(fmt_value(&pair, true), "Pair(1, \"a\")");
        assert_eq!(
            fmt_value(&Node::Option(Some(Box::new(Node::Int(1)))), true),
            "Some(1)"
        );
        assert_eq!(fmt_value(&Node::Option(None), true), "None");
        assert_eq!(
            fmt_value(&Node::Result(Ok(Box::new(Node::Str("a".into())))), true),
            "Ok(\"a\")"
        );
        assert_eq!(
            fmt_value(&Node::Result(Err(Box::new(Node::Int(3)))), true),
            "Err(3)"
        );
        // error 的 msg 用 JSON 引号形（jsprelude langGoFmt 的 __langGoErr 分支）
        assert_eq!(
            fmt_value(
                &Node::Error {
                    msg: "boom\n".into()
                },
                true
            ),
            "error{msg: \"boom\\n\"}"
        );
    }

    #[test]
    fn fn_ptr_and_nested_composition() {
        assert_eq!(fmt_value(&Node::Fn(Some("main".into())), true), "<fn main>");
        assert_eq!(fmt_value(&Node::Fn(None), true), "<fn 匿名>");
        assert_eq!(fmt_value(&Node::Ptr(Box::new(Node::Int(5))), true), "&5");
        let nested = Node::Arr(vec![Node::Option(Some(Box::new(Node::Str("x".into()))))]);
        assert_eq!(fmt_value(&nested, true), "[Some(\"x\")]");
    }

    #[test]
    fn println_args_space_joined() {
        let args = vec![Node::Str("a b".into()), Node::Int(1), Node::Bool(false)];
        assert_eq!(fmt_args(&args), "a b 1 false");
        assert_eq!(fmt_args(&[]), "");
    }
}
