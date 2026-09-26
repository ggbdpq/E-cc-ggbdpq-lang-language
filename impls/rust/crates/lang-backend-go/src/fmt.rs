//! →Go 后端的输出格式化规则层（RUNTIME-OUTPUT-003 / RUNTIME-OUTPUT-004）。
//!
//! 行为证据：`impls/typescript/src/interp/value.ts` 的 `fmtVal`/`goFormatFloat`/`goQuote`
//! 与 `impls/typescript/src/backends/goprelude.ts` 的 `langGoFmt`（Go `strconv` 镜像）。
//! 期望值全部手算，不回读实现输出（防同义反复）。
//!
//! 与 →JS 后端唯一的语义分歧点：嵌套字符串转义（本 crate 用 Go `strconv.Quote` 语义，
//! 见 [`quote_go`]；→JS 用 JSON.stringify 语义，见 SPEC-GAP-RS-001）。

/// 抽象打印值树：IR 冻结前 fmt 层的自有形状（后端把 IR 常量映射到它）。
///
/// 形状逐一对齐解释器 `fmtVal` 的分派分支：标量、容器、struct/enum、
/// Option/Result、error、区间、函数、指针。映射打印保持**插入序**（Vec）。
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
    /// struct error { msg: string }，打印 `error{msg: "<quote>"}`
    Error {
        msg: String,
    },
}

/// 值格式化（`fmtVal` 镜像）。`top=true` 为顶层（println/str()）：字符串不带引号；
/// `top=false` 为嵌套：字符串带引号。
pub fn fmt_value(v: &Node, top: bool) -> String {
    match v {
        Node::Unit => "()".to_string(),
        Node::Int(i) => i.to_string(),
        Node::Float(f) => fmt_float(*f),
        Node::Bool(b) => if *b { "true" } else { "false" }.to_string(),
        Node::Str(s) => {
            if top {
                s.clone()
            } else {
                quote_go(s)
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
        Node::Error { msg } => format!("error{{msg: {}}}", quote_go(msg)),
    }
}

/// println/print 的实参格式化：顶层形 + 空格分隔（RUNTIME-OUTPUT-003）。
pub fn fmt_args(args: &[Node]) -> String {
    args.iter()
        .map(|a| fmt_value(a, true))
        .collect::<Vec<_>>()
        .join(" ")
}

/// float 文本形：Go `strconv.FormatFloat(f, 'g', -1, 64)` 的镜像。
///
/// 规则（手算 + RUNTIME-OUTPUT-004 安全区 [1e-4, 1e6) 契约对齐）：
/// - NaN → `NaN`；+Inf/-Inf → `+Inf`/`-Inf`；±0 → `0`/`-0`
/// - 最短舍入数字；科学计数切换：十进制指数 < -4 或 >= 6 → `d.ddde±XX`（指数至少两位、恒带符号）
/// - 否则定点；无尾零（`1.0` → `1`）
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
        // %e 形：d.ddde±XX（指数至少两位）
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

/// Go `strconv.Quote` 的镜像（嵌套字符串引号）。
///
/// - `"` → `\"`，`\` → `\\`
/// - ASCII 控制字符：`\a \b \f \n \r \t \v`，其余 `\xNN`（小写十六进制）
/// - U+2028/U+2029 与非打印 Unicode → `\uNNNN`（>0xFFFF 用 `\UNNNNNNNN`）
/// - 打印 Unicode（L/M/N/P/S 类 + ASCII 空格，含中文/emoji/组合记号）直出
///
/// 注：Go `unicode.IsPrint` 的完整类别表需要 Unicode 数据；此处按 std 能力近似——
/// 已知非打印区（分隔符 Zs/Zl/Zp、格式符 Cf、私用区 Co）硬编码，未分配码点按打印处理
/// （conformance 语料不使用未分配码点，分歧不可达）。
pub fn quote_go(s: &str) -> String {
    let mut out = String::with_capacity(s.len() + 2);
    out.push('"');
    for c in s.chars() {
        let cp = c as u32;
        if cp < 0x80 {
            match c {
                '"' => out.push_str("\\\""),
                '\\' => out.push_str("\\\\"),
                '\u{07}' => out.push_str("\\a"),
                '\u{08}' => out.push_str("\\b"),
                '\u{0c}' => out.push_str("\\f"),
                '\n' => out.push_str("\\n"),
                '\r' => out.push_str("\\r"),
                '\t' => out.push_str("\\t"),
                '\u{0b}' => out.push_str("\\v"),
                _ if (0x20..0x7f).contains(&cp) => out.push(c),
                _ => out.push_str(&format!("\\x{cp:02x}")),
            }
            continue;
        }
        if cp == 0x2028 || cp == 0x2029 || !is_print_unicode(cp) {
            if cp > 0xFFFF {
                out.push_str(&format!("\\U{cp:08x}"));
            } else {
                out.push_str(&format!("\\u{cp:04x}"));
            }
            continue;
        }
        out.push(c);
    }
    out.push('"');
    out
}

/// 最短舍入数字提取：`format!("{a:e}")`（Rust 的 LowerExp 无精度输出即最短舍入表示，
/// 形如 `1.2345e-7`，指数无 `+` 号、无前导零）。返回（无符号十进制数字串、十进制指数），
/// 满足 `value = 0.<digits> × 10^(exp+1)`。
pub(crate) fn shortest_digits(a: f64) -> (String, i32) {
    debug_assert!(a.is_finite() && a >= 0.0);
    let s = format!("{a:e}");
    let Some((mant, exp_str)) = s.split_once('e') else {
        return (s, 0); // 不可达：LowerExp 输出恒含 'e'
    };
    let exp: i32 = exp_str.parse().unwrap_or(0);
    let digits: String = mant.chars().filter(|c| c.is_ascii_digit()).collect();
    // Rust 最短表示不应带尾零；防御性去除（对齐参考实现 shortestDigits 的 trim）
    let trimmed = digits.trim_end_matches('0');
    let digits = if trimmed.is_empty() { "0" } else { trimmed };
    (digits.to_string(), exp)
}

/// 定点形（`%f`）：不补尾零——最短数字本身不含多余零。
pub(crate) fn fixed_digits(digits: &str, exp: i32) -> String {
    let dp = exp + 1; // 小数点位置（digits 前 dp 位在整数侧）
    if dp <= 0 {
        format!("0.{}{digits}", "0".repeat((-dp) as usize))
    } else if dp as usize >= digits.len() {
        format!("{digits}{}", "0".repeat(dp as usize - digits.len()))
    } else {
        format!("{}.{}", &digits[..dp as usize], &digits[dp as usize..])
    }
}

/// Go `unicode.IsPrint` 近似：L/M/N/P/S + ASCII 空格为打印。
/// std 无类别表，硬编码非打印区：分隔符 Zs/Zl/Zp、格式符 Cf、私用区 Co、
/// 非字符；其余未分配码点按打印处理（conformance 语料不使用未分配码点，
/// 分歧不可达）。
fn is_print_unicode(cp: u32) -> bool {
    if cp == 0x20 {
        return true;
    }
    // Zs（除 0x20）：U+00A0, U+1680, U+2000..=U+200A, U+202F, U+205F, U+3000
    if cp == 0xA0
        || cp == 0x1680
        || (0x2000..=0x200A).contains(&cp)
        || cp == 0x202F
        || cp == 0x205F
        || cp == 0x3000
    {
        return false;
    }
    // Cf 格式符（常见区）：U+00AD, U+0600..=U+0605, U+061C, U+06DD, U+070F,
    // U+180E, U+200B..=U+200F, U+202A..=U+202E, U+2060..=U+2064, U+2066..=U+206F,
    // U+FEFF, U+FFF9..=U+FFFB
    if cp == 0xAD
        || (0x600..=0x605).contains(&cp)
        || cp == 0x61C
        || cp == 0x6DD
        || cp == 0x70F
        || cp == 0x180E
        || (0x200B..=0x200F).contains(&cp)
        || (0x202A..=0x202E).contains(&cp)
        || (0x2060..=0x2064).contains(&cp)
        || (0x2066..=0x206F).contains(&cp)
        || cp == 0xFEFF
        || (0xFFF9..=0xFFFB).contains(&cp)
    {
        return false;
    }
    // Co 私用区
    if (0xE000..=0xF8FF).contains(&cp)
        || (0xF0000..=0xFFFFD).contains(&cp)
        || (0x100000..=0x10FFFD).contains(&cp)
    {
        return false;
    }
    // 非字符（保证永不分配，Go IsPrint = false）：U+FDD0..=U+FDEF 与各平面末位 FFFE/FFFF
    if (0xFDD0..=0xFDEF).contains(&cp) || (cp & 0xFFFE) == 0xFFFE {
        return false;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    // —— float（对照 goprelude langGoFmt = strconv.FormatFloat('g',-1,64)，手算真值）——

    #[test]
    fn float_no_trailing_zero_and_safe_zone() {
        assert_eq!(fmt_float(1.0), "1");
        assert_eq!(fmt_float(3.0), "3");
        assert_eq!(fmt_float(100.0), "100");
        assert_eq!(fmt_float(0.1), "0.1");
        assert_eq!(fmt_float(-2.5), "-2.5");
        // 安全区端点：|x| ∈ [1e-4, 1e6) 为跨实现契约（RUNTIME-OUTPUT-004）
        assert_eq!(fmt_float(1e-4), "0.0001"); // exp=-4 → 定点
        assert_eq!(fmt_float(0.25), "0.25");
        assert_eq!(fmt_float(999999.0), "999999");
        assert_eq!(fmt_float(1e6), "1e+06"); // exp=6 → 科学计数（安全区开区间右端）
    }

    #[test]
    fn float_scientific_switch() {
        // 指数 < -4 或 >= 6 走 %e，指数至少两位、恒带符号
        assert_eq!(fmt_float(1e-5), "1e-05");
        assert_eq!(fmt_float(1.5e-7), "1.5e-07");
        assert_eq!(fmt_float(1e21), "1e+21");
        assert_eq!(fmt_float(1e100), "1e+100");
        assert_eq!(fmt_float(1234567.8), "1.2345678e+06");
        // 边界内保持定点
        assert_eq!(fmt_float(123456.789), "123456.789");
        assert_eq!(fmt_float(0.5), "0.5");
    }

    #[test]
    fn float_specials() {
        assert_eq!(fmt_float(0.0), "0");
        assert_eq!(fmt_float(-0.0), "-0");
        assert_eq!(fmt_float(f64::NAN), "NaN");
        assert_eq!(fmt_float(f64::INFINITY), "+Inf");
        assert_eq!(fmt_float(f64::NEG_INFINITY), "-Inf");
    }

    #[test]
    fn float_shortest_round_trip_digits() {
        // 1/3 的最短舍入表示（手算：f64 最近值为 0.333333333333333314829616256247...，
        // 最短往返小数 = 0.3333333333333333）
        assert_eq!(fmt_float(1.0 / 3.0), "0.3333333333333333");
        // 0.1+0.2 的最短往返
        assert_eq!(fmt_float(0.1 + 0.2), "0.30000000000000004");
        assert_eq!(fmt_float(5e-324), "5e-324"); // 最小子正规
        assert_eq!(fmt_float(f64::MAX), "1.7976931348623157e+308");
    }

    // —— 嵌套字符串 quote（strconv.Quote 语义，手算真值）——

    #[test]
    fn quote_plain_and_simple_escapes() {
        assert_eq!(quote_go("hi"), "\"hi\"");
        assert_eq!(quote_go("a\"b"), "\"a\\\"b\"");
        assert_eq!(quote_go("a\\b"), "\"a\\\\b\"");
        assert_eq!(quote_go("l\nm\tr "), "\"l\\nm\\tr \"");
    }

    #[test]
    fn quote_control_chars() {
        assert_eq!(quote_go("\u{7}"), "\"\\a\"");
        assert_eq!(quote_go("\u{8}"), "\"\\b\"");
        assert_eq!(quote_go("\u{b}"), "\"\\v\"");
        assert_eq!(quote_go("\u{c}"), "\"\\f\"");
        assert_eq!(quote_go("\u{1}"), "\"\\x01\"");
        assert_eq!(quote_go("\u{1f}"), "\"\\x1f\"");
        assert_eq!(quote_go("\u{7f}"), "\"\\x7f\""); // DEL → \xNN
    }

    #[test]
    fn quote_unicode_passthrough_policy() {
        // 打印 Unicode 直出：中文、emoji（So 类）、组合记号（Mn 类）
        assert_eq!(quote_go("中文"), "\"中文\"");
        assert_eq!(quote_go("e\u{301}"), "\"e\u{301}\"");
        assert_eq!(quote_go("\u{1f600}"), "\"\u{1f600}\"");
        // 非打印转义
        assert_eq!(quote_go("\u{2028}"), "\"\\u2028\""); // Zl
        assert_eq!(quote_go("\u{a0}"), "\"\\u00a0\""); // NBSP（Zs）
        assert_eq!(quote_go("\u{200d}"), "\"\\u200d\""); // ZWJ（Cf）
        assert_eq!(quote_go("\u{e000}"), "\"\\ue000\""); // 私用区
        assert_eq!(quote_go("\u{10ffff}"), "\"\\U0010ffff\""); // >0xFFFF 用 \U
    }

    // —— 标量与容器规范打印形（fmtVal 分派镜像）——

    #[test]
    fn int_full_range_decimal() {
        assert_eq!(
            fmt_value(&Node::Int(i64::MIN), true),
            "-9223372036854775808"
        );
        assert_eq!(fmt_value(&Node::Int(i64::MAX), true), "9223372036854775807");
        assert_eq!(fmt_value(&Node::Int(0), true), "0");
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
            fields: vec![
                ("x".into(), Node::Int(1)),
                ("y".into(), Node::Float(2.5)),
                ("label".into(), Node::Str("a".into())),
            ],
        };
        assert_eq!(fmt_value(&st, true), "Point{x: 1, y: 2.5, label: \"a\"}");

        let range = Node::Range(1, 5);
        assert_eq!(fmt_value(&range, true), "1..5");
    }

    #[test]
    fn map_prints_insertion_order() {
        // 插入序 b, 1, true——与键的任何排序序不同
        let m = Node::Map(vec![
            (Node::Str("b".into()), Node::Int(2)),
            (Node::Int(1), Node::Str("x".into())),
            (Node::Bool(true), Node::Unit),
        ]);
        assert_eq!(fmt_value(&m, true), "{\"b\": 2, 1: \"x\", true: ()}");
        // 重设同键不新增（调用方维护；fmt 层只按给定序打印）
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
        // 嵌套：数组里的 Option 里的字符串 → 嵌套形（带引号）
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
