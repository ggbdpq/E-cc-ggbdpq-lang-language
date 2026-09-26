//! 语法器（specs/grammar.md PARSE-001..006）：recursive descent + Pratt 表达式。
//!
//! 错误消息与 Go 参考实现逐字一致（经 TS parser 证据复刻），一律以
//! `lang_core::Diagnostic` E1001 返回，禁止 panic；首个错误即停（无恢复）。
//! 控制头禁止裸 struct 字面量（PARSE-006）由 `no_struct` 计数器落实。

use lang_core::{Diagnostic, DiagnosticCode, SourceId, SourceText, Span};

use crate::ast::{
    Arm, AssignOp, BinaryOp, EnumCase, Expr, FieldLit, MapItem, Param, Pat, Stmt, TypeEx, UnaryOp,
};
use crate::lexer::lex;
use crate::token::{Token, TokenKind, kind_name};

/// Pratt 优先级（PARSE-004，低 → 高；0 = 非二元运算符）。
fn prec_of(k: TokenKind) -> u8 {
    match k {
        TokenKind::OrOr => 1,
        TokenKind::AndAnd => 2,
        TokenKind::EqEq | TokenKind::Ne => 3,
        TokenKind::Lt | TokenKind::Le | TokenKind::Gt | TokenKind::Ge => 4,
        TokenKind::DotDot => 5,
        TokenKind::Plus | TokenKind::Minus | TokenKind::Or | TokenKind::Xor => 6,
        TokenKind::Star
        | TokenKind::Slash
        | TokenKind::Percent
        | TokenKind::And
        | TokenKind::Shl
        | TokenKind::Shr => 7,
        _ => 0,
    }
}

fn binary_op(k: TokenKind) -> Option<BinaryOp> {
    match k {
        TokenKind::EqEq => Some(BinaryOp::Eq),
        TokenKind::Ne => Some(BinaryOp::Ne),
        TokenKind::Lt => Some(BinaryOp::Lt),
        TokenKind::Le => Some(BinaryOp::Le),
        TokenKind::Gt => Some(BinaryOp::Gt),
        TokenKind::Ge => Some(BinaryOp::Ge),
        TokenKind::Plus => Some(BinaryOp::Add),
        TokenKind::Minus => Some(BinaryOp::Sub),
        TokenKind::Star => Some(BinaryOp::Mul),
        TokenKind::Slash => Some(BinaryOp::Div),
        TokenKind::Percent => Some(BinaryOp::Rem),
        TokenKind::And => Some(BinaryOp::BitAnd),
        TokenKind::Or => Some(BinaryOp::BitOr),
        TokenKind::Xor => Some(BinaryOp::BitXor),
        TokenKind::Shl => Some(BinaryOp::Shl),
        TokenKind::Shr => Some(BinaryOp::Shr),
        TokenKind::AndAnd => Some(BinaryOp::AndAnd),
        TokenKind::OrOr => Some(BinaryOp::OrOr),
        _ => None,
    }
}

/// 复刻 Go `unicode.IsUpper(rune(lit[0]))`：取 UTF-8 首字节并按其码点判 Lu
/// （TS parser firstByteIsUpper 同构：首字节 ≥0x80 时按 Latin-1 字符判大写）。
fn first_byte_is_upper(lit: &str) -> bool {
    let Some(&b) = lit.as_bytes().first() else {
        return false;
    };
    if b.is_ascii_uppercase() {
        return true;
    }
    if b >= 0x80 {
        return char::from_u32(u32::from(b)).is_some_and(char::is_uppercase);
    }
    false
}

/// 语法器状态。
struct Parser {
    toks: Vec<Token>,
    i: usize,
    source: SourceId,
    /// 末次消费记号的 span 终点（span_of 用；skip_nl 不更新）。
    prev_end: Span,
    /// >0 时处于控制头位置，禁止裸 struct 字面量（PARSE-006）。
    no_struct: u32,
}

impl Parser {
    fn new(toks: Vec<Token>, source: SourceId) -> Self {
        Self {
            toks,
            i: 0,
            source,
            prev_end: Span::point(source, lang_core::Position::new(0, 1, 1)),
            no_struct: 0,
        }
    }

    fn cur(&self) -> &Token {
        &self.toks[self.i]
    }

    /// 消费当前记号；停在 EOF 不再前进（与 TS next 一致）。
    fn next(&mut self) -> Token {
        let t = self.toks[self.i].clone();
        self.prev_end = t.span;
        if self.i < self.toks.len() - 1 {
            self.i += 1;
        }
        t
    }

    fn at(&self, k: TokenKind) -> bool {
        self.toks[self.i].kind == k
    }

    fn skip_nl(&mut self) {
        while self.at(TokenKind::Newline) {
            self.i += 1;
        }
    }

    fn accept(&mut self, k: TokenKind) -> bool {
        if self.at(k) {
            self.next();
            true
        } else {
            false
        }
    }

    /// `起点记号 → 末次消费记号` 的 span。
    fn span_of(&self, start: &Token) -> Span {
        Span::new(self.source, start.span.start, self.prev_end.end)
    }

    /// 从某起点 Position 到末次消费记号的 span（Arm/EnumCase/TypeEx 等用）。
    fn span_from(&self, start: Span) -> Span {
        Span::new(self.source, start.start, self.prev_end.end)
    }

    fn err_at(&self, t: &Token, msg: impl Into<String>) -> Diagnostic {
        Diagnostic::new(
            DiagnosticCode::E1001,
            msg,
            Some(Diagnostic::at_line(self.source, t.span.start.line)),
        )
    }

    fn expect(&mut self, k: TokenKind) -> Result<Token, Diagnostic> {
        if self.at(k) {
            return Ok(self.next());
        }
        let t = self.cur().clone();
        Err(self.err_at(
            &t,
            format!("期望 {}，得到 {}", kind_name(k), kind_name(t.kind)),
        ))
    }

    fn expect_name(&mut self) -> Result<String, Diagnostic> {
        if self.at(TokenKind::Ident) {
            return Ok(self.next().lit);
        }
        if self.at(TokenKind::Underscore) {
            self.next();
            return Ok("_".to_string());
        }
        let t = self.cur().clone();
        Err(self.err_at(&t, format!("期望标识符，得到 {}", kind_name(t.kind))))
    }

    // —— 顶层 ——

    fn parse_top(&mut self) -> Result<Stmt, Diagnostic> {
        let t = self.cur().clone();
        // 保留字在顶层使用即诊断（LEX-004 系）
        match t.kind {
            TokenKind::Chan => {
                return Err(self.err_at(&t, "保留字 chan 尚未启用（规划于 M3+）"));
            }
            TokenKind::Interface => {
                return Err(self.err_at(&t, "保留字 interface 尚未启用（规划于 M3+）"));
            }
            TokenKind::Import => {
                self.next();
                let path = self.expect(TokenKind::Str)?;
                return Ok(Stmt::Import {
                    span: self.span_of(&t),
                    path: path.lit,
                });
            }
            TokenKind::Extern => return self.parse_extern(),
            _ => {}
        }
        self.parse_stmt()
    }

    /// `extern "host" { fn 签名（无函数体）... }`。
    fn parse_extern(&mut self) -> Result<Stmt, Diagnostic> {
        let t = self.next(); // extern
        if !self.at(TokenKind::Str) {
            return Err(self.err_at(&t, "extern 后须是宿主名字符串，如 extern \"go\""));
        }
        let host = self.next().lit;
        self.expect(TokenKind::LBrace)?;
        let mut fns = Vec::new();
        loop {
            self.skip_nl();
            if self.accept(TokenKind::RBrace) {
                return Ok(Stmt::Extern {
                    span: self.span_of(&t),
                    host,
                    fns,
                });
            }
            if self.at(TokenKind::Eof) {
                return Err(self.err_at(self.cur(), "extern 块未闭合"));
            }
            let fn_tok = self.expect(TokenKind::Fn)?;
            let name = self.expect_name()?;
            let type_params = self.parse_type_params()?;
            let params = self.parse_params()?;
            let ret = if self.accept(TokenKind::Colon) {
                Some(self.parse_type()?)
            } else {
                None
            };
            fns.push(Stmt::FnDecl {
                span: self.span_of(&fn_tok),
                name,
                recv: None,
                type_params,
                params,
                ret,
                body: Vec::new(),
            });
        }
    }

    // —— 语句 ——

    fn parse_stmt(&mut self) -> Result<Stmt, Diagnostic> {
        let t = self.cur().clone();
        match t.kind {
            TokenKind::Let | TokenKind::Var => {
                self.next();
                let name = self.expect_name()?;
                let typ = if self.accept(TokenKind::Colon) {
                    Some(self.parse_type()?)
                } else {
                    None
                };
                self.expect(TokenKind::Eq)?;
                let init = self.parse_expr(0)?;
                Ok(Stmt::Let {
                    span: self.span_of(&t),
                    name,
                    mutable: t.kind == TokenKind::Var,
                    typ,
                    init,
                })
            }

            TokenKind::Return => {
                self.next();
                let value = if !self.at(TokenKind::Newline)
                    && !self.at(TokenKind::RBrace)
                    && !self.at(TokenKind::Eof)
                {
                    Some(self.parse_expr(0)?)
                } else {
                    None
                };
                Ok(Stmt::Return {
                    span: self.span_of(&t),
                    value,
                })
            }

            TokenKind::If => self.parse_if(),

            TokenKind::While => {
                self.next();
                self.no_struct += 1;
                let cond = self.parse_expr(0);
                self.no_struct -= 1;
                let cond = cond?;
                let body = self.parse_block()?;
                Ok(Stmt::While {
                    span: self.span_of(&t),
                    cond,
                    body,
                })
            }

            TokenKind::For => {
                self.next();
                let variable = self.expect_name()?;
                self.expect(TokenKind::In)?;
                self.no_struct += 1;
                let iter = self.parse_expr(0);
                self.no_struct -= 1;
                let iter = iter?;
                let body = self.parse_block()?;
                Ok(Stmt::ForIn {
                    span: self.span_of(&t),
                    variable,
                    iter,
                    body,
                })
            }

            TokenKind::Go => {
                self.next();
                let call = self.parse_expr(0)?;
                if !matches!(call, Expr::Call { .. }) {
                    return Err(self.err_at(&t, "go 的目标须是函数调用"));
                }
                Ok(Stmt::Go {
                    span: self.span_of(&t),
                    call,
                })
            }

            TokenKind::Break => {
                self.next();
                Ok(Stmt::Break {
                    span: self.span_of(&t),
                })
            }

            TokenKind::Continue => {
                self.next();
                Ok(Stmt::Continue {
                    span: self.span_of(&t),
                })
            }

            TokenKind::Fn => self.parse_fn_decl(),
            TokenKind::Struct => self.parse_struct(),
            TokenKind::Enum => self.parse_enum(),

            _ => {
                let e = self.parse_expr(0)?;
                if self.accept(TokenKind::Eq) {
                    check_target(&e)?;
                    let v = self.parse_expr(0)?;
                    return Ok(Stmt::Assign {
                        span: self.span_of(&t),
                        target: e,
                        op: AssignOp::Assign,
                        value: v,
                    });
                }
                let compound = match self.cur().kind {
                    TokenKind::PlusEq => Some(AssignOp::AddAssign),
                    TokenKind::MinusEq => Some(AssignOp::SubAssign),
                    TokenKind::StarEq => Some(AssignOp::MulAssign),
                    TokenKind::SlashEq => Some(AssignOp::DivAssign),
                    _ => None,
                };
                if let Some(op) = compound {
                    self.next();
                    check_target(&e)?;
                    let v = self.parse_expr(0)?;
                    return Ok(Stmt::Assign {
                        span: self.span_of(&t),
                        target: e,
                        op,
                        value: v,
                    });
                }
                Ok(Stmt::ExprStmt {
                    span: self.span_of(&t),
                    expr: e,
                })
            }
        }
    }

    /// `if`（含 else if 展开；Sp 末次重算覆盖 else 分支，与 TS 两次 spanOf 等价）。
    fn parse_if(&mut self) -> Result<Stmt, Diagnostic> {
        let t = self.next(); // if
        self.no_struct += 1;
        let cond = self.parse_expr(0);
        self.no_struct -= 1;
        let cond = cond?;
        let then_body = self.parse_block()?;
        let mut else_body = None;
        self.skip_nl();
        if self.accept(TokenKind::Else) {
            if self.at(TokenKind::If) {
                else_body = Some(vec![self.parse_if()?]);
            } else {
                else_body = Some(self.parse_block()?);
            }
        }
        Ok(Stmt::If {
            span: self.span_of(&t),
            cond,
            then_body,
            else_body,
        })
    }

    fn parse_block(&mut self) -> Result<Vec<Stmt>, Diagnostic> {
        self.expect(TokenKind::LBrace)?;
        let mut stmts = Vec::new();
        loop {
            self.skip_nl();
            if self.accept(TokenKind::RBrace) {
                return Ok(stmts);
            }
            if self.at(TokenKind::Eof) {
                return Err(self.err_at(self.cur(), "块未闭合，缺少 }"));
            }
            stmts.push(self.parse_stmt()?);
        }
    }

    // —— 声明 ——

    fn parse_fn_decl(&mut self) -> Result<Stmt, Diagnostic> {
        let t = self.next(); // fn
        let mut recv = None;
        if self.accept(TokenKind::LParen) {
            let name_pos = self.cur().span;
            let rname = self.expect_name()?;
            if self.at(TokenKind::And) {
                return Err(self.err_at(self.cur(), "v0 暂不支持指针接收者"));
            }
            let rtyp = self.parse_type()?;
            self.expect(TokenKind::RParen)?;
            self.expect(TokenKind::Dot)?;
            recv = Some(Param {
                span: self.span_from(name_pos),
                name: rname,
                typ: Some(rtyp),
            });
        }
        let name = self.expect_name()?;
        self.parse_func_rest(&t, recv, name)
    }

    fn parse_func_rest(
        &mut self,
        start_tok: &Token,
        recv: Option<Param>,
        name: String,
    ) -> Result<Stmt, Diagnostic> {
        let type_params = self.parse_type_params()?;
        let params = self.parse_params()?;
        let ret = if self.accept(TokenKind::Colon) {
            Some(self.parse_type()?)
        } else {
            None
        };
        let body = self.parse_block()?;
        Ok(Stmt::FnDecl {
            span: self.span_of(start_tok),
            name,
            recv,
            type_params,
            params,
            ret,
            body,
        })
    }

    fn parse_type_params(&mut self) -> Result<Vec<String>, Diagnostic> {
        if !self.accept(TokenKind::LBracket) {
            return Ok(Vec::new());
        }
        let mut tps = Vec::new();
        loop {
            tps.push(self.expect_name()?);
            if !self.accept(TokenKind::Comma) {
                break;
            }
        }
        self.expect(TokenKind::RBracket)?;
        Ok(tps)
    }

    fn parse_params(&mut self) -> Result<Vec<Param>, Diagnostic> {
        self.expect(TokenKind::LParen)?;
        let mut params = Vec::new();
        while !self.at(TokenKind::RParen) {
            let name_pos = self.cur().span;
            let name = self.expect_name()?;
            let typ = if self.accept(TokenKind::Colon) {
                Some(self.parse_type()?)
            } else {
                None
            };
            params.push(Param {
                span: self.span_from(name_pos),
                name,
                typ,
            });
            if !self.accept(TokenKind::Comma) {
                break;
            }
        }
        self.expect(TokenKind::RParen)?;
        Ok(params)
    }

    fn parse_struct(&mut self) -> Result<Stmt, Diagnostic> {
        let t = self.next(); // struct
        let name = self.expect_name()?;
        let type_params = self.parse_type_params()?;
        self.expect(TokenKind::LBrace)?;
        let mut fields = Vec::new();
        loop {
            self.skip_nl();
            if self.accept(TokenKind::RBrace) {
                break;
            }
            if self.at(TokenKind::Eof) {
                return Err(self.err_at(self.cur(), "struct 未闭合"));
            }
            let name_pos = self.cur().span;
            let fname = self.expect_name()?;
            self.expect(TokenKind::Colon)?;
            let typ = self.parse_type()?;
            fields.push(Param {
                span: self.span_from(name_pos),
                name: fname,
                typ: Some(typ),
            });
            self.skip_nl();
            self.accept(TokenKind::Comma);
        }
        Ok(Stmt::StructDecl {
            span: self.span_of(&t),
            name,
            type_params,
            fields,
        })
    }

    fn parse_enum(&mut self) -> Result<Stmt, Diagnostic> {
        let t = self.next(); // enum
        let name = self.expect_name()?;
        let type_params = self.parse_type_params()?;
        self.expect(TokenKind::LBrace)?;
        let mut cases = Vec::new();
        loop {
            self.skip_nl();
            if self.accept(TokenKind::RBrace) {
                break;
            }
            if self.at(TokenKind::Eof) {
                return Err(self.err_at(self.cur(), "enum 未闭合"));
            }
            let case_pos = self.cur().span;
            let cn = self.expect_name()?;
            let mut payloads = Vec::new();
            if self.accept(TokenKind::LParen) {
                while !self.at(TokenKind::RParen) {
                    payloads.push(self.parse_type()?);
                    if !self.accept(TokenKind::Comma) {
                        break;
                    }
                }
                self.expect(TokenKind::RParen)?;
            }
            cases.push(EnumCase {
                span: self.span_from(case_pos),
                name: cn,
                payloads,
            });
            self.skip_nl();
            self.accept(TokenKind::Comma);
        }
        Ok(Stmt::EnumDecl {
            span: self.span_of(&t),
            name,
            type_params,
            cases,
        })
    }

    // —— 类型注解 ——

    fn parse_type(&mut self) -> Result<TypeEx, Diagnostic> {
        let t = self.cur().clone();
        match t.kind {
            TokenKind::Chan => {
                self.next();
                if !self.accept(TokenKind::LBracket) {
                    return Err(self.err_at(&t, "chan 需要 elem 类型参数，如 chan[int]"));
                }
                let elem = self.parse_type()?;
                self.expect(TokenKind::RBracket)?;
                Ok(TypeEx::Named {
                    span: self.span_of(&t),
                    name: "chan".to_string(),
                    args: vec![elem],
                })
            }
            TokenKind::And => {
                self.next();
                let elem = self.parse_type()?;
                Ok(TypeEx::Ptr {
                    span: self.span_of(&t),
                    elem: Box::new(elem),
                })
            }
            TokenKind::Fn => {
                self.next();
                self.expect(TokenKind::LParen)?;
                let mut params = Vec::new();
                while !self.at(TokenKind::RParen) {
                    params.push(self.parse_type()?);
                    if !self.accept(TokenKind::Comma) {
                        break;
                    }
                }
                self.expect(TokenKind::RParen)?;
                let ret = if self.accept(TokenKind::Colon) {
                    Some(Box::new(self.parse_type()?))
                } else {
                    None
                };
                Ok(TypeEx::Fn {
                    span: self.span_of(&t),
                    params,
                    ret,
                })
            }
            TokenKind::LBracket => {
                self.next();
                let elem = self.parse_type()?;
                self.expect(TokenKind::RBracket)?;
                Ok(TypeEx::Arr {
                    span: self.span_of(&t),
                    elem: Box::new(elem),
                })
            }
            TokenKind::Ident => {
                self.next();
                if t.lit == "map" && self.at(TokenKind::LBracket) {
                    self.next();
                    let k = self.parse_type()?;
                    self.expect(TokenKind::RBracket)?;
                    let v = self.parse_type()?;
                    return Ok(TypeEx::Map {
                        span: self.span_of(&t),
                        key: Box::new(k),
                        val: Box::new(v),
                    });
                }
                let mut args = Vec::new();
                if self.accept(TokenKind::LBracket) {
                    loop {
                        args.push(self.parse_type()?);
                        if !self.accept(TokenKind::Comma) {
                            break;
                        }
                    }
                    self.expect(TokenKind::RBracket)?;
                }
                Ok(TypeEx::Named {
                    span: self.span_of(&t),
                    name: t.lit,
                    args,
                })
            }
            _ => Err(self.err_at(&t, format!("期望类型，得到 {}", kind_name(t.kind)))),
        }
    }

    // —— 表达式（Pratt） ——

    fn parse_expr(&mut self, min_prec: u8) -> Result<Expr, Diagnostic> {
        let mut left = self.parse_unary()?;
        loop {
            let t = self.cur().clone();
            let prec = prec_of(t.kind);
            if prec == 0 || prec < min_prec {
                return Ok(left);
            }
            self.next();
            let right = self.parse_expr(prec + 1)?;
            let span = self.span_of(&t);
            left = if t.kind == TokenKind::DotDot {
                Expr::Range {
                    span,
                    lo: Box::new(left),
                    hi: Box::new(right),
                }
            } else {
                // prec_of 已保证是二元运算符
                let Some(op) = binary_op(t.kind) else {
                    return Err(self.err_at(&t, "内部错误：优先级表与二元运算符表不一致"));
                };
                Expr::Binary {
                    span,
                    op,
                    lhs: Box::new(left),
                    rhs: Box::new(right),
                }
            };
        }
    }

    fn parse_unary(&mut self) -> Result<Expr, Diagnostic> {
        let t = self.cur().clone();
        let op = match t.kind {
            TokenKind::Minus => UnaryOp::Neg,
            TokenKind::Not => UnaryOp::Not,
            TokenKind::And => UnaryOp::Ref,
            _ => return self.parse_postfix(),
        };
        self.next();
        let x = self.parse_unary()?;
        Ok(Expr::Unary {
            span: self.span_of(&t),
            op,
            operand: Box::new(x),
        })
    }

    fn parse_postfix(&mut self) -> Result<Expr, Diagnostic> {
        let mut x = self.parse_primary()?;
        loop {
            let t = self.cur().clone();
            match t.kind {
                TokenKind::LParen => {
                    self.next();
                    let mut args = Vec::new();
                    loop {
                        self.skip_nl();
                        if self.at(TokenKind::RParen) {
                            break;
                        }
                        args.push(self.parse_expr(0)?);
                        self.skip_nl();
                        if !self.accept(TokenKind::Comma) {
                            break;
                        }
                    }
                    self.skip_nl();
                    self.expect(TokenKind::RParen)?;
                    x = Expr::Call {
                        span: self.span_of(&t),
                        callee: Box::new(x),
                        args,
                    };
                }
                TokenKind::LBracket => {
                    self.next();
                    self.skip_nl();
                    let lo = self.parse_expr(0)?;
                    if self.accept(TokenKind::Colon) {
                        if self.at(TokenKind::RBracket) {
                            return Err(
                                self.err_at(self.cur(), "切片需要上界（v0 不支持开区间切片）")
                            );
                        }
                        let hi = self.parse_expr(0)?;
                        self.skip_nl();
                        self.expect(TokenKind::RBracket)?;
                        x = Expr::Slice {
                            span: self.span_of(&t),
                            obj: Box::new(x),
                            lo: Box::new(lo),
                            hi: Box::new(hi),
                        };
                    } else {
                        self.skip_nl();
                        self.expect(TokenKind::RBracket)?;
                        x = Expr::Index {
                            span: self.span_of(&t),
                            obj: Box::new(x),
                            idx: Box::new(lo),
                        };
                    }
                }
                TokenKind::Dot => {
                    self.next();
                    let name = self.expect(TokenKind::Ident)?;
                    x = Expr::Field {
                        span: self.span_of(&t),
                        obj: Box::new(x),
                        name: name.lit,
                    };
                }
                TokenKind::Question => {
                    self.next();
                    x = Expr::Try {
                        span: self.span_of(&t),
                        operand: Box::new(x),
                    };
                }
                _ => return Ok(x),
            }
        }
    }

    fn parse_primary(&mut self) -> Result<Expr, Diagnostic> {
        let t = self.cur().clone();
        match t.kind {
            TokenKind::Int => {
                self.next();
                // Go ParseInt(s, 10, 64)：语法非法与溢出统一报「整数溢出」
                match t.lit.parse::<i64>() {
                    Ok(v) => Ok(Expr::IntLit {
                        span: self.span_of(&t),
                        value: v,
                    }),
                    Err(_) => Err(self.err_at(&t, format!("整数溢出: {}", t.lit))),
                }
            }
            TokenKind::Float => {
                self.next();
                if !is_plain_float(&t.lit) {
                    return Err(self.err_at(&t, format!("非法浮点数: {}", t.lit)));
                }
                match t.lit.parse::<f64>() {
                    Ok(v) if v.is_finite() => Ok(Expr::FloatLit {
                        span: self.span_of(&t),
                        value: v,
                    }),
                    _ => Err(self.err_at(&t, format!("非法浮点数: {}", t.lit))),
                }
            }
            TokenKind::Str => {
                self.next();
                Ok(Expr::StrLit {
                    span: self.span_of(&t),
                    value: t.lit.clone(),
                })
            }
            TokenKind::True | TokenKind::False => {
                self.next();
                Ok(Expr::BoolLit {
                    span: self.span_of(&t),
                    value: t.kind == TokenKind::True,
                })
            }
            TokenKind::Ident => {
                self.next();
                if self.at(TokenKind::LBrace) && self.no_struct == 0 {
                    return self.parse_struct_lit(&t);
                }
                Ok(Expr::Ident {
                    span: self.span_of(&t),
                    name: t.lit,
                })
            }
            TokenKind::LParen => {
                self.next();
                let saved = self.no_struct; // 括号内恢复 struct 字面量
                self.no_struct = 0;
                let x = self.parse_expr(0);
                self.no_struct = saved;
                let x = x?;
                self.skip_nl();
                self.expect(TokenKind::RParen)?;
                Ok(x)
            }
            TokenKind::LBracket => {
                self.next();
                let mut items = Vec::new();
                loop {
                    self.skip_nl();
                    if self.at(TokenKind::RBracket) {
                        break;
                    }
                    items.push(self.parse_expr(0)?);
                    self.skip_nl();
                    if !self.accept(TokenKind::Comma) {
                        break;
                    }
                }
                self.skip_nl();
                self.expect(TokenKind::RBracket)?;
                Ok(Expr::ArrLit {
                    span: self.span_of(&t),
                    items,
                })
            }
            TokenKind::LBrace => {
                self.next();
                let mut items = Vec::new();
                loop {
                    self.skip_nl();
                    if self.accept(TokenKind::RBrace) {
                        break;
                    }
                    if self.at(TokenKind::Eof) {
                        return Err(self.err_at(self.cur(), "映射字面量未闭合"));
                    }
                    let key_pos = self.cur().span;
                    let key = self.parse_expr(0)?;
                    self.expect(TokenKind::Colon)?;
                    let value = self.parse_expr(0)?;
                    items.push(MapItem {
                        span: self.span_from(key_pos),
                        key,
                        value,
                    });
                    self.skip_nl();
                    self.accept(TokenKind::Comma);
                }
                Ok(Expr::MapLit {
                    span: self.span_of(&t),
                    items,
                })
            }
            TokenKind::Fn => {
                self.next();
                let f = self.parse_func_rest(&t, None, String::new())?;
                let Stmt::FnDecl {
                    type_params,
                    params,
                    ret,
                    body,
                    ..
                } = f
                else {
                    return Err(self.err_at(&t, "内部错误：函数字面量形状不符"));
                };
                Ok(Expr::FnLit {
                    span: self.span_of(&t),
                    type_params,
                    params,
                    ret,
                    body,
                })
            }
            TokenKind::Match => self.parse_match(),
            TokenKind::Eof => Err(self.err_at(&t, "表达式意外结束")),
            _ => Err(self.err_at(&t, format!("意外的记号 {}", kind_name(t.kind)))),
        }
    }

    fn parse_struct_lit(&mut self, start_tok: &Token) -> Result<Expr, Diagnostic> {
        let name = start_tok.lit.clone();
        self.next(); // {
        let mut fields = Vec::new();
        loop {
            self.skip_nl();
            if self.accept(TokenKind::RBrace) {
                break;
            }
            if self.at(TokenKind::Eof) {
                return Err(self.err_at(self.cur(), "struct 字面量未闭合"));
            }
            let fname = self.expect(TokenKind::Ident)?;
            self.expect(TokenKind::Colon)?;
            let value = self.parse_expr(0)?;
            fields.push(FieldLit {
                span: Span::new(self.source, fname.span.start, self.prev_end.end),
                name: fname.lit,
                value,
            });
            self.skip_nl();
            self.accept(TokenKind::Comma);
        }
        Ok(Expr::StructLit {
            span: self.span_of(start_tok),
            type_name: name,
            fields,
        })
    }

    fn parse_match(&mut self) -> Result<Expr, Diagnostic> {
        let t = self.next(); // match
        self.no_struct += 1;
        let scrut = self.parse_expr(0);
        self.no_struct -= 1;
        let scrut = scrut?;
        self.expect(TokenKind::LBrace)?;
        let mut arms = Vec::new();
        loop {
            self.skip_nl();
            if self.accept(TokenKind::RBrace) {
                break;
            }
            if self.at(TokenKind::Eof) {
                return Err(self.err_at(self.cur(), "match 未闭合"));
            }
            let arm_pos = self.cur().span;
            let pat = self.parse_pattern()?;
            self.expect(TokenKind::FatArrow)?;
            let body = self.parse_expr(0)?;
            arms.push(Arm {
                span: self.span_from(arm_pos),
                pat,
                body,
            });
            if self.at(TokenKind::Newline) || self.at(TokenKind::RBrace) {
                continue;
            }
            return Err(self.err_at(self.cur(), "match 分支之间需要换行"));
        }
        Ok(Expr::Match {
            span: self.span_of(&t),
            scrutinee: Box::new(scrut),
            arms,
        })
    }

    fn parse_pattern(&mut self) -> Result<Pat, Diagnostic> {
        let t = self.cur().clone();
        match t.kind {
            TokenKind::Underscore => {
                self.next();
                Ok(Pat::Wild {
                    span: self.span_of(&t),
                })
            }
            TokenKind::Int
            | TokenKind::Float
            | TokenKind::Str
            | TokenKind::True
            | TokenKind::False => {
                let e = self.parse_primary()?;
                Ok(Pat::Lit {
                    span: self.span_of(&t),
                    value: e,
                })
            }
            TokenKind::Ident => {
                self.next();
                if self.accept(TokenKind::LParen) {
                    let mut subs = Vec::new();
                    while !self.at(TokenKind::RParen) {
                        subs.push(self.parse_pattern()?);
                        if !self.accept(TokenKind::Comma) {
                            break;
                        }
                    }
                    self.expect(TokenKind::RParen)?;
                    return Ok(Pat::Case {
                        span: self.span_of(&t),
                        name: t.lit,
                        subs,
                    });
                }
                // 大写开头按 case 引用，小写为绑定（PARSE-005）
                if first_byte_is_upper(&t.lit) {
                    Ok(Pat::Case {
                        span: self.span_of(&t),
                        name: t.lit,
                        subs: Vec::new(),
                    })
                } else {
                    Ok(Pat::Bind {
                        span: self.span_of(&t),
                        name: t.lit,
                    })
                }
            }
            _ => Err(self.err_at(&t, format!("非法模式，得到 {}", kind_name(t.kind)))),
        }
    }

    /// 整个源文件 / REPL 片段 → 语句序列。
    fn parse_program(&mut self) -> Result<Vec<Stmt>, Diagnostic> {
        let mut prog = Vec::new();
        loop {
            self.skip_nl();
            if self.at(TokenKind::Eof) {
                return Ok(prog);
            }
            prog.push(self.parse_top()?);
        }
    }
}

/// 赋值目标只能是变量、字段链或索引。
fn check_target(e: &Expr) -> Result<(), Diagnostic> {
    match e {
        Expr::Ident { .. } => Ok(()),
        Expr::Field { obj, .. } | Expr::Index { obj, .. } => check_target(obj),
        _ => Err(Diagnostic::new(
            DiagnosticCode::E1001,
            "赋值目标不合法",
            Some(Diagnostic::at_line(e.span().source, e.span().start.line)),
        )),
    }
}

/// 浮点字面量形状校验：`[0-9]+\.[0-9]+`（TS parser 正则同构）。
fn is_plain_float(lit: &str) -> bool {
    let Some((int_part, frac_part)) = lit.split_once('.') else {
        return false;
    };
    !int_part.is_empty()
        && !frac_part.is_empty()
        && int_part.bytes().all(|b| b.is_ascii_digit())
        && frac_part.bytes().all(|b| b.is_ascii_digit())
}

/// 解析整个源文件 / REPL 片段（词法 + 语法），错误以 E1001 诊断返回。
pub fn parse(text: &SourceText, source: SourceId) -> Result<Vec<Stmt>, Diagnostic> {
    let toks = lex(text, source)?;
    parse_tokens(toks, source)
}

/// 解析已切好的记号流（供上层复用词法结果）。
///
/// 契约：`toks` 末尾应有 `lex()` 追加的 EOF 记号；空流按空文件处理（补一个
/// 合成 EOF，保证游标不变量，绝不 panic）。
pub fn parse_tokens(mut toks: Vec<Token>, source: SourceId) -> Result<Vec<Stmt>, Diagnostic> {
    if toks.is_empty() {
        let at = lang_core::Position::new(0, 1, 1);
        toks.push(Token {
            kind: TokenKind::Eof,
            lit: String::new(),
            span: Span::new(source, at, at),
        });
    }
    Parser::new(toks, source).parse_program()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::ast::Expr;

    fn parse_ok(src: &str) -> Vec<Stmt> {
        let text = SourceText::new("t.lang-go", src.to_string());
        parse(&text, SourceId(0)).unwrap_or_default()
    }

    fn parse_err(src: &str) -> Diagnostic {
        let text = SourceText::new("t.lang-go", src.to_string());
        parse(&text, SourceId(0)).expect_err("应为解析错误")
    }

    /// 取第一个语句为表达式（表达式语句）。
    fn first_expr(src: &str) -> Expr {
        match &parse_ok(src)[0] {
            Stmt::ExprStmt { expr, .. } => expr.clone(),
            other => panic!("应为表达式语句，得到 {other:?}"),
        }
    }

    #[test]
    fn precedence_mul_over_add() {
        // 2 + 3 * 4 → Binary(+, 2, Binary(*, 3, 4))
        let e = first_expr("2 + 3 * 4");
        let Expr::Binary { op, lhs, rhs, .. } = &e else {
            panic!("应为二元")
        };
        assert_eq!(*op, BinaryOp::Add);
        assert!(matches!(**lhs, Expr::IntLit { value: 2, .. }));
        assert!(matches!(
            &**rhs,
            Expr::Binary {
                op: BinaryOp::Mul,
                ..
            }
        ));
    }

    #[test]
    fn precedence_same_level_bitor_add_is_left_assoc() {
        // PARSE-004：| 与 + 同在第 6 级，左结合 → (1 | 2) + 3。
        // conformance 交叉验证：precedence-bit-vs-arith 期望 stdout「6」=(1|2)+3。
        let e = first_expr("1 | 2 + 3");
        let Expr::Binary { op, lhs, rhs, .. } = &e else {
            panic!("应为二元")
        };
        assert_eq!(*op, BinaryOp::Add);
        let Expr::Binary { op: inner, .. } = &**lhs else {
            panic!("应为二元")
        };
        assert_eq!(*inner, BinaryOp::BitOr);
        assert!(matches!(&**rhs, Expr::IntLit { value: 3, .. }));
    }

    #[test]
    fn precedence_shl_above_add() {
        // PARSE-004：<< 在第 7 级（高于 + 第 6 级）→ (1 << 2) + 1。
        // conformance 交叉验证：同 case 期望 stdout「5」=(1<<2)+1。
        let e = first_expr("1 << 2 + 1");
        let Expr::Binary { op, lhs, rhs, .. } = &e else {
            panic!("应为二元")
        };
        assert_eq!(*op, BinaryOp::Add);
        let Expr::Binary { op: inner, .. } = &**lhs else {
            panic!("应为二元")
        };
        assert_eq!(*inner, BinaryOp::Shl);
        assert!(matches!(&**rhs, Expr::IntLit { value: 1, .. }));
    }

    #[test]
    fn parens_override_precedence_and_logic_chain() {
        let e = first_expr("(2 + 3) * 4");
        let Expr::Binary { op, lhs, .. } = &e else {
            panic!("应为二元")
        };
        assert_eq!(*op, BinaryOp::Mul);
        assert!(matches!(
            &**lhs,
            Expr::Binary {
                op: BinaryOp::Add,
                ..
            }
        ));

        // a || b && c → ||(a, &&(b, c))（&& 优先级更高）
        let e = first_expr("a || b && c");
        let Expr::Binary { op: o1, rhs, .. } = &e else {
            panic!("应为二元")
        };
        assert_eq!(*o1, BinaryOp::OrOr);
        let Expr::Binary { op: o2, .. } = &**rhs else {
            panic!("应为二元")
        };
        assert_eq!(*o2, BinaryOp::AndAnd);
    }

    #[test]
    fn binary_ops_are_left_associative() {
        // 10 - 3 - 2 → (- (- 10 3) 2)
        let e = first_expr("10 - 3 - 2");
        let Expr::Binary { lhs, .. } = &e else {
            panic!("应为二元")
        };
        assert!(matches!(
            &**lhs,
            Expr::Binary {
                op: BinaryOp::Sub,
                ..
            }
        ));
    }

    #[test]
    fn range_is_level_five_not_float() {
        let e = first_expr("1..3");
        assert!(matches!(
            &e,
            Expr::Range { lo, hi, .. }
                if matches!(&**lo, Expr::IntLit { value: 1, .. })
                    && matches!(&**hi, Expr::IntLit { value: 3, .. })
        ));
    }

    #[test]
    fn unary_nested_negation() {
        // -(-5) → Unary(-, Unary(-, 5))；-5 - -3 → Binary(-, u(-5), u(-3))
        let e = first_expr("-(-5)");
        let Expr::Unary { operand, .. } = &e else {
            panic!("应为一元")
        };
        assert!(matches!(&**operand, Expr::Unary { .. }));

        let e = first_expr("-5 - -3");
        let Expr::Binary { op, lhs, rhs, .. } = &e else {
            panic!("应为二元")
        };
        assert_eq!(*op, BinaryOp::Sub);
        assert!(matches!(&**lhs, Expr::Unary { .. }));
        assert!(matches!(&**rhs, Expr::Unary { .. }));
    }

    #[test]
    fn postfix_chains_call_index_slice_field_try() {
        let e = first_expr("a[1:2].b(x)?");
        // Try(Call(Field(Slice(Ident a)), [x]))
        let Expr::Try { operand, .. } = &e else {
            panic!("应为 try")
        };
        let Expr::Call { callee, args, .. } = &**operand else {
            panic!("应为 call")
        };
        assert_eq!(args.len(), 1);
        let Expr::Field { obj, name, .. } = &**callee else {
            panic!("应为 field")
        };
        assert_eq!(name, "b");
        let Expr::Slice { obj, lo, hi, .. } = &**obj else {
            panic!("应为 slice")
        };
        assert!(matches!(&**lo, Expr::IntLit { value: 1, .. }));
        assert!(matches!(&**hi, Expr::IntLit { value: 2, .. }));
        let Expr::Ident { name, .. } = &**obj else {
            panic!("应为 ident")
        };
        assert_eq!(name, "a");
    }

    #[test]
    fn literals_and_collections() {
        let e = first_expr("0xFF");
        assert!(matches!(&e, Expr::IntLit { value: 255, .. }));

        let e = first_expr("[1, 2, 3]");
        let Expr::ArrLit { items, .. } = &e else {
            panic!("应为数组")
        };
        assert_eq!(items.len(), 3);

        let e = first_expr("{\"a\": 1, \"b\": 2}");
        let Expr::MapLit { items, .. } = &e else {
            panic!("应为映射")
        };
        assert_eq!(items.len(), 2);

        let e = first_expr("\"中\\n文\"");
        let Expr::StrLit { value, .. } = &e else {
            panic!("应为字符串")
        };
        assert_eq!(value, "中\n文");
    }

    #[test]
    fn struct_literal_parses_fields() {
        let e = first_expr("Point{x: 1, y: 2}");
        let Expr::StructLit {
            type_name, fields, ..
        } = &e
        else {
            panic!("应为 struct 字面量")
        };
        assert_eq!(type_name, "Point");
        assert_eq!(fields.len(), 2);
        assert_eq!(fields[0].name, "x");
        assert!(matches!(&fields[0].value, Expr::IntLit { value: 1, .. }));
    }

    #[test]
    fn match_expression_arms_and_patterns() {
        let src = "match v {\n    1 => \"一\"\n    _ => \"多\"\n}";
        let e = first_expr(src);
        let Expr::Match {
            scrutinee, arms, ..
        } = &e
        else {
            panic!("应为 match")
        };
        assert!(matches!(&**scrutinee, Expr::Ident { name, .. } if name == "v"));
        assert_eq!(arms.len(), 2);
        assert!(matches!(&arms[0].pat, Pat::Lit { .. }));
        assert!(matches!(&arms[1].pat, Pat::Wild { .. }));

        // 绑定（小写）vs 构造（大写）vs 构造带子模式
        let src = "match r {\n    Ok(v) => v\n    Err(e) => 0\n    Nil => 0\n    other => 1\n}";
        let e = first_expr(src);
        let Expr::Match { arms, .. } = &e else {
            panic!("应为 match")
        };
        assert!(
            matches!(&arms[0].pat, Pat::Case { name, subs, .. } if name == "Ok" && subs.len() == 1)
        );
        assert!(matches!(&arms[1].pat, Pat::Case { name, .. } if name == "Err"));
        assert!(matches!(&arms[2].pat, Pat::Case { name, .. } if name == "Nil"));
        assert!(matches!(&arms[3].pat, Pat::Bind { name, .. } if name == "other"));
    }

    #[test]
    fn fn_literal_with_capture_body() {
        // fn 字面量须处于表达式位置（语句首的 fn 走函数声明）
        let src = "let f = fn(x: int): int {\n    return x + 1\n}";
        let prog = parse_ok(src);
        let Stmt::Let { init, .. } = &prog[0] else {
            panic!("应为 let")
        };
        let Expr::FnLit {
            params, ret, body, ..
        } = init
        else {
            panic!("应为 fn 字面量")
        };
        assert_eq!(params.len(), 1);
        assert!(ret.is_some());
        assert_eq!(body.len(), 1);
    }

    #[test]
    fn statements_full_set() {
        let src = "\
let a = 1
var b = 2
b += 3
a.b = 4
c[0] = 5
if a > 0 {
    return a
} else if a < 0 {
    return -a
} else {
    return 0
}
while b > 0 {
    b -= 1
    if b == 1 { break }
    continue
}
for x in [1, 2] {
    println(x)
}
go worker(1)
";
        let prog = parse_ok(src);
        assert!(matches!(&prog[0], Stmt::Let { mutable: false, .. }));
        assert!(matches!(&prog[1], Stmt::Let { mutable: true, .. }));
        let Stmt::Assign { op, target, .. } = &prog[2] else {
            panic!("应为赋值")
        };
        assert_eq!(*op, AssignOp::AddAssign);
        assert!(matches!(target, Expr::Ident { .. }));
        let Stmt::Assign { target, .. } = &prog[3] else {
            panic!("应为赋值")
        };
        assert!(matches!(target, Expr::Field { .. }));
        let Stmt::Assign { target, .. } = &prog[4] else {
            panic!("应为赋值")
        };
        assert!(matches!(target, Expr::Index { .. }));
        assert!(matches!(
            &prog[5],
            Stmt::If {
                else_body: Some(_),
                ..
            }
        ));
        assert!(matches!(&prog[6], Stmt::While { .. }));
        assert!(matches!(&prog[7], Stmt::ForIn { variable, .. } if variable == "x"));
        assert!(matches!(&prog[8], Stmt::Go { .. }));
    }

    #[test]
    fn bare_return_is_none() {
        let src = "fn f() {\n    return\n}";
        let prog = parse_ok(src);
        let Stmt::FnDecl { body, .. } = &prog[0] else {
            panic!("应为函数")
        };
        let Stmt::Return { value, .. } = &body[0] else {
            panic!("应为 return")
        };
        assert!(value.is_none());
    }

    #[test]
    fn declarations_fn_method_generic_struct_enum() {
        let src = "\
fn add(a: int, b: int): int {
    return a + b
}
fn (r Rect).area(): int {
    return r.w * r.h
}
fn id[T](x: T): T {
    return x
}
struct Point[T] {
    x: T
    y: T
}
enum Shape {
    Circle(int)
    Rect(int, int)
    Unit
}
";
        let prog = parse_ok(src);
        let Stmt::FnDecl {
            name, params, ret, ..
        } = &prog[0]
        else {
            panic!("应为 fn")
        };
        assert_eq!(name, "add");
        assert_eq!(params.len(), 2);
        assert!(ret.is_some());

        let Stmt::FnDecl { name, recv, .. } = &prog[1] else {
            panic!("应为方法")
        };
        assert_eq!(name, "area");
        let recv = recv.as_ref().expect("接收者应存在");
        assert_eq!(recv.name, "r");

        let Stmt::FnDecl {
            name, type_params, ..
        } = &prog[2]
        else {
            panic!("应为泛型 fn")
        };
        assert_eq!(name, "id");
        assert_eq!(type_params, &vec!["T".to_string()]);

        let Stmt::StructDecl {
            name,
            type_params,
            fields,
            ..
        } = &prog[3]
        else {
            panic!("应为 struct")
        };
        assert_eq!(name, "Point");
        assert_eq!(type_params.len(), 1);
        assert_eq!(fields.len(), 2);

        let Stmt::EnumDecl { name, cases, .. } = &prog[4] else {
            panic!("应为 enum")
        };
        assert_eq!(name, "Shape");
        assert_eq!(cases.len(), 3);
        assert_eq!(cases[0].payloads.len(), 1);
        assert_eq!(cases[1].payloads.len(), 2);
        assert!(cases[2].payloads.is_empty());
    }

    #[test]
    fn import_and_extern() {
        let src = "import \"./md5\"\n";
        let prog = parse_ok(src);
        let Stmt::Import { path, .. } = &prog[0] else {
            panic!("应为 import")
        };
        assert_eq!(path, "./md5");

        let src = "extern \"go\" {\n    fn abs(x: int): int\n}\n";
        let prog = parse_ok(src);
        let Stmt::Extern { host, fns, .. } = &prog[0] else {
            panic!("应为 extern")
        };
        assert_eq!(host, "go");
        assert_eq!(fns.len(), 1);
        assert!(matches!(&fns[0], Stmt::FnDecl { body, .. } if body.is_empty()));
    }

    #[test]
    fn nodes_carry_code_point_spans_with_cjk() {
        // let(0..3) sp 消(4)息(5) sp =(6) sp "(9)中(10)文(11)"(12)：全长 13 码点
        let src = "let 消息 = \"中文\"";
        let prog = parse_ok(src);
        let stmt = &prog[0];
        assert_eq!(stmt.span().start.offset, 0);
        assert_eq!(stmt.span().end.offset, 13);
        let Stmt::Let { init, .. } = stmt else {
            panic!("应为 let")
        };
        let Expr::StrLit { span, value } = init else {
            panic!("应为字符串")
        };
        assert_eq!(value, "中文");
        assert_eq!(span.start.offset, 9);
        assert_eq!(span.end.offset, 13); // 引号 2 + 2 码点 = 4 码点（非 6 字节）
    }

    #[test]
    fn arm_and_case_spans_are_real_not_zero() {
        let src = "match v {\n    1 => x\n}";
        let e = first_expr(src);
        let Expr::Match { arms, .. } = &e else {
            panic!("应为 match")
        };
        // 证据实现里 Arm.Sp 是零值；契约要求每节点带 span
        let a = &arms[0];
        assert!(a.span.start.offset > 0);
        assert!(a.span.end.offset > a.span.start.offset);
        assert_eq!(a.pat.span().start.line, 2);
    }

    #[test]
    fn control_head_bans_bare_struct_literal_parse006() {
        // 裸 struct 字面量在 if 头部被拒
        let d = parse_err("fn main() {\n    if Point{x: 1}.x > 0 {\n        println(1)\n    }\n}");
        assert_eq!(d.code, DiagnosticCode::E1001);
        // 加括号即合法
        let prog =
            parse_ok("fn main() {\n    if (Point{x: 1}).x > 0 {\n        println(1)\n    }\n}");
        assert_eq!(prog.len(), 1);
    }

    #[test]
    fn struct_literal_allowed_outside_control_head() {
        let prog = parse_ok("let p = Point{x: 1}\nprintln(p.x)");
        assert_eq!(prog.len(), 2);
    }

    #[test]
    fn reserved_words_rejected_at_top_level() {
        let d = parse_err("interface = 1");
        assert_eq!(d.message, "保留字 interface 尚未启用（规划于 M3+）");
        let d = parse_err("chan = 1");
        assert_eq!(d.message, "保留字 chan 尚未启用（规划于 M3+）");
        // 但 chan[int] 类型注解合法（类型位置）
        let prog = parse_ok("fn f(c: chan[int]) {}");
        let Stmt::FnDecl { params, .. } = &prog[0] else {
            panic!("应为 fn")
        };
        let Some(t) = params[0].typ.as_ref() else {
            panic!("参数应有类型")
        };
        assert!(matches!(t, TypeEx::Named { name, args, .. } if name == "chan" && args.len() == 1));
    }

    #[test]
    fn error_messages_follow_contract_text() {
        assert_eq!(parse_err("let = 1").message, "期望标识符，得到 =");
        assert_eq!(parse_err("fn main() {").message, "块未闭合，缺少 }");
        assert_eq!(
            parse_err("999999999999999999999999").message,
            "整数溢出: 999999999999999999999999"
        );
        assert_eq!(parse_err("let x =").message, "表达式意外结束");
        // `1.` 的 '.' 被 postfix 当字段访问消费，随后在文件尾找字段名
        assert_eq!(parse_err("let x = 1.").message, "期望 标识符，得到 文件尾");
        assert_eq!(
            parse_err("match x { 1 => 1 2 => 2 }").message,
            "match 分支之间需要换行"
        );
    }

    #[test]
    fn int_overflow_and_non_ascii_digits_unified_message() {
        // Go ParseInt 同源：语法非法与溢出统一报「整数溢出」
        assert_eq!(
            parse_err("１２３").message, // 全角数字（Nd），词法收进、parser 拒绝
            "整数溢出: １２３"
        );
    }

    #[test]
    fn assignment_target_check() {
        let d = parse_err("1 = 2");
        assert_eq!(d.message, "赋值目标不合法");
        let d = parse_err("f() = 2");
        assert_eq!(d.message, "赋值目标不合法");
        // 链式字段/索引目标合法
        assert!(parse_ok("a.b[0].c = 1").len() == 1);
    }

    #[test]
    fn slice_needs_upper_bound() {
        assert_eq!(
            parse_err("a[1:]").message,
            "切片需要上界（v0 不支持开区间切片）"
        );
    }

    #[test]
    fn go_requires_call() {
        assert_eq!(parse_err("go worker").message, "go 的目标须是函数调用");
    }

    #[test]
    fn multiline_collections_and_call_args_survive() {
        // LEX-008 续行的端到端：行尾逗号 / 开括号使换行存活
        let src = "\
fn add(a: int, b: int, c: int): int {
    return a + b + c
}
fn main() {
    println(add(
        1,
        2,
        3,
    ))
    let m = {
        \"k\": [
            1,
            2,
        ]
    }
    println(m)
}
";
        let prog = parse_ok(src);
        assert_eq!(prog.len(), 2);
    }

    #[test]
    fn pointer_receiver_rejected() {
        // 名字消费后才检查 &（与 TS 证据一致）
        assert_eq!(
            parse_err("fn (r &Rect).area(): int {\n    return 0\n}").message,
            "v0 暂不支持指针接收者"
        );
    }

    #[test]
    fn diagnostic_shape_is_e1001_with_line_anchor() {
        let d = parse_err("let = 1");
        assert_eq!(d.code, DiagnosticCode::E1001);
        assert_eq!(d.code.as_str(), "E1001");
        assert_eq!(d.rule_id.as_str(), "LEX-ALL");
        assert_eq!(d.severity, lang_core::Severity::Error);
        let s = d.primary_span.expect("应带行级锚");
        assert_eq!(s.start.line, 1);
        assert_eq!(s.start.column, 1); // v0.4 行级契约
        assert_eq!(s.start.offset, 0);
    }

    #[test]
    fn error_line_points_at_offending_line() {
        let d = parse_err("let a = 1\nlet = 2\n");
        assert_eq!(d.primary_span.expect("锚").start.line, 2);
    }

    #[test]
    fn empty_token_stream_is_treated_as_empty_file() {
        // pub API 防御：空记号流不 panic，按空程序处理
        let prog = parse_tokens(Vec::new(), SourceId(0)).unwrap_or_default();
        assert!(prog.is_empty());
    }
}
