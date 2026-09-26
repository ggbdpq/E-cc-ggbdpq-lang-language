//! 声明名字 token 定位：在声明 span 的词元流内找名字 token 的精确区间。
//!
//! 与 TS 证据（refactor.ts 的 spanTokens/scanNameAfterFn）同构：AST span 给
//! 搜索范围，词元流给精确 token 区间；**名字身份仍以 SymbolId 为准**，这里
//! 只做「声明里名字写在哪儿」的定位。码点度量全程（SourceText/token span）。

use lang_core::Span;
use lang_syntax::{Token, TokenKind};

use crate::input::{RefactorInput, SourceFile};

/// 文件内某 span 区间的非换行词元（绝对码点 span，已按文件词元流过滤）。
pub fn tokens_in<'a>(file: &'a SourceFile, span: &Span) -> Vec<&'a Token> {
    file.tokens
        .iter()
        .filter(|t| {
            t.span.start.offset >= span.start.offset && t.span.end.offset <= span.end.offset
        })
        .filter(|t| !matches!(t.kind, TokenKind::Eof | TokenKind::Newline))
        .collect()
}

/// 按符号在文件中的定位：文件词元流 + 符号声明 span → 名字 token span。
pub fn decl_name_span(input: &RefactorInput, target_span: &Span, has_recv: bool) -> Option<Span> {
    let file = input.sources.get(&target_span.source)?;
    let toks = tokens_in(file, target_span);
    let mut i = toks.iter().position(|t| t.kind == TokenKind::Fn)?;
    i += 1;
    if has_recv {
        // fn (r Recv).name：跳过平衡的接收者括号组，再越过 `.` 链
        while i < toks.len() && toks[i].kind != TokenKind::LParen {
            i += 1;
        }
        if i >= toks.len() {
            return None;
        }
        let mut depth = 1usize;
        i += 1;
        while i < toks.len() && depth > 0 {
            match toks[i].kind {
                TokenKind::LParen | TokenKind::LBracket => depth += 1,
                TokenKind::RParen | TokenKind::RBracket => depth -= 1,
                _ => {}
            }
            if depth > 0 {
                i += 1;
            }
        }
        i += 1; // 越过收括号
        while i < toks.len() && toks[i].kind == TokenKind::Dot {
            i += 1;
        }
    }
    let t = toks.get(i)?;
    if t.kind != TokenKind::Ident {
        return None;
    }
    Some(t.span)
}

/// Let 语句的绑定名 span（首个标识符词元）。
pub fn first_ident_span(input: &RefactorInput, span: &Span) -> Option<Span> {
    let file = input.sources.get(&span.source)?;
    tokens_in(file, span)
        .into_iter()
        .find(|t| t.kind == TokenKind::Ident)
        .map(|t| t.span)
}

/// ForIn 语句的循环变量名 span（`for` 后的标识符）。
pub fn forin_var_span(input: &RefactorInput, span: &Span) -> Option<Span> {
    let file = input.sources.get(&span.source)?;
    let toks = tokens_in(file, span);
    let i = toks.iter().position(|t| t.kind == TokenKind::For)? + 1;
    let t = toks.get(i)?;
    if t.kind != TokenKind::Ident {
        return None;
    }
    Some(t.span)
}

/// 参数组的名字 token span：decl span 内第 `seg` 个参数段的首个标识符。
///
/// 顶层括号组按源码序编号：接收者组在前（若有），形参组随后；`_` 段照计
/// （调用方传入的 seg 已按含 `_` 的位序计算）。返回类型里的 `fn(...)` 注解
/// 组位于形参组之后，不影响取组。
pub fn param_ident_span(
    input: &RefactorInput,
    decl_span: &Span,
    has_recv: bool,
    seg: usize,
) -> Option<Span> {
    let file = input.sources.get(&decl_span.source)?;
    let toks = tokens_in(file, decl_span);
    // 收集顶层括号组（depth 1 的括号对在词元流中的下标区间）
    let mut groups: Vec<(usize, usize)> = Vec::new();
    let mut i = 0usize;
    while i < toks.len() {
        if toks[i].kind == TokenKind::LParen {
            let mut depth = 1usize;
            let mut j = i + 1;
            while j < toks.len() && depth > 0 {
                match toks[j].kind {
                    TokenKind::LParen | TokenKind::LBracket => depth += 1,
                    TokenKind::RParen | TokenKind::RBracket => depth -= 1,
                    _ => {}
                }
                j += 1;
            }
            groups.push((i, j - 1));
            i = j;
        } else {
            i += 1;
        }
    }
    let gi = if has_recv { 1 } else { 0 };
    let (lp, rp) = *groups.get(gi)?;
    seg_ident(&toks, lp, rp, seg)
}

/// 在第 `lp` 个词元（LParen）起的括号组（止于 `rp`）内找第 `seg` 个逗号段
/// 的首个标识符。
fn seg_ident(toks: &[&Token], lp: usize, rp: usize, seg: usize) -> Option<Span> {
    let mut depth = 1usize;
    let mut i = lp + 1;
    let mut seg_idx = 0usize;
    let mut start = i;
    while i <= rp {
        match toks[i].kind {
            TokenKind::LParen | TokenKind::LBracket => depth += 1,
            TokenKind::RParen | TokenKind::RBracket => {
                depth -= 1;
                if depth == 0 {
                    break;
                }
            }
            TokenKind::Comma if depth == 1 => {
                if seg_idx == seg {
                    return first_ident(&toks[start..i]);
                }
                seg_idx += 1;
                start = i + 1;
            }
            _ => {}
        }
        i += 1;
    }
    if seg_idx == seg && start <= i {
        return first_ident(&toks[start..i]);
    }
    None
}

fn first_ident(toks: &[&Token]) -> Option<Span> {
    toks.iter()
        .find(|t| t.kind == TokenKind::Ident)
        .map(|t| t.span)
}

/// 字段/方法访问的名字 token span：span 内最后一个标识符词元（`obj.name`）。
pub fn last_ident_span(input: &RefactorInput, span: &Span) -> Option<Span> {
    let file = input.sources.get(&span.source)?;
    tokens_in(file, span)
        .into_iter()
        .rev()
        .find(|t| t.kind == TokenKind::Ident)
        .map(|t| t.span)
}

/// 码点区间取切片（UTF-8 安全；越界收敛到文末）。
pub fn cp_slice(src: &str, start: u32, end: u32) -> &str {
    let a = cp_to_byte(src, start);
    let b = cp_to_byte(src, end.max(start));
    &src[a..b]
}

/// 码点偏移 → 字节偏移。
pub fn cp_to_byte(src: &str, cp: u32) -> usize {
    src.char_indices()
        .nth(cp as usize)
        .map(|(b, _)| b)
        .unwrap_or(src.len())
}
