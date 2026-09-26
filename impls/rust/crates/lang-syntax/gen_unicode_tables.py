# 一次性生成器：产出 src/unicode_tables.rs（生成数据，运行后可删）。
# L = Lu/Ll/Lt/Lm/Lo 闭区间表（Go unicode.IsLetter / TS \p{L} 同源）
# Nd = 十进制数字闭区间表（Go unicode.IsDigit / TS \p{Nd} 同源）
import unicodedata

OUT = r"E:\ggbdpq\gh\language\impls\rust\crates\lang-syntax\src\unicode_tables.rs"


def ranges(cps):
    out = []
    s = p = cps[0]
    for c in cps[1:]:
        if c == p + 1:
            p = c
        else:
            out.append((s, p))
            s = p = c
    out.append((s, p))
    return out


cats = {}
for cp in range(0x110000):
    cat = unicodedata.category(chr(cp))
    if cat.startswith("L") or cat == "Nd":
        cats.setdefault(cat, []).append(cp)

letters = sorted(sum((v for k, v in cats.items() if k.startswith("L")), []))
lr = ranges(letters)
nr = ranges(cats["Nd"])


def emit(name, rs):
    lines = [f"pub(crate) const {name}: &[(u32, u32)] = &["]
    for i in range(0, len(rs), 3):
        lines.append("    " + " ".join(f"(0x{a:X}, 0x{b:X})," for a, b in rs[i : i + 3]))
    lines.append("];")
    return "\n".join(lines)


header = f"""// Unicode 码点性质表（生成数据，勿手改）。
//
// 由 Python 3.14 unicodedata（Unicode {unicodedata.unidata_version}）全平面扫描生成：
// L  = 一般类别 Lu/Ll/Lt/Lm/Lo（与 Go unicode.IsLetter、TS 正则属性 \\p{{L}} 同源）；
// Nd = 十进制数字类别（与 Go unicode.IsDigit、TS 正则属性 \\p{{Nd}} 同源）。
// Rust std 的 is_alphabetic/is_numeric 走 Alphabetic/N 派生性质，比 L*/Nd 宽
//（含 Nl/No/Mn 等），会让上标数字、组合记号混入标识符——这里用精确闭区间表 + 二分查找。
// 再生成：遍历 U+0000..=U+10FFFF 取 category，相邻码点合并为闭区间。

#![allow(clippy::all)]

"""

with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    f.write(header)
    f.write(emit("LETTER_RANGES", lr) + "\n\n")
    f.write(emit("ND_RANGES", nr) + "\n")

print("L ranges", len(lr), "Nd ranges", len(nr))
