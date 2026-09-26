//! 双解释器共用的算术/比较/复合赋值运算（i64 回绕 = Go int64 语义）。
//!
//! AST 解释器与 IR 解释器共用同一份运算实现——差分的价值在**遍历与求值序**，
//! 不在运算规则本身；运算规则漂移会同时污染两条执行路径。

use lang_runtime::{RtError, RtErrorKind, RtResult, Value};
use lang_syntax::{AssignOp, BinaryOp};

/// IR Binop ↔ AST BinaryOp 的公共运算键（不含 `&&`/`||`——那在两条路径里
/// 分别降为短路控制流）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Op {
    Eq,
    Ne,
    Lt,
    Le,
    Gt,
    Ge,
    Add,
    Sub,
    Mul,
    Div,
    Rem,
    BitAnd,
    BitOr,
    BitXor,
    Shl,
    Shr,
}

impl From<BinaryOp> for Op {
    fn from(op: BinaryOp) -> Op {
        match op {
            BinaryOp::Eq => Op::Eq,
            BinaryOp::Ne => Op::Ne,
            BinaryOp::Lt => Op::Lt,
            BinaryOp::Le => Op::Le,
            BinaryOp::Gt => Op::Gt,
            BinaryOp::Ge => Op::Ge,
            BinaryOp::Add => Op::Add,
            BinaryOp::Sub => Op::Sub,
            BinaryOp::Mul => Op::Mul,
            BinaryOp::Div => Op::Div,
            BinaryOp::Rem => Op::Rem,
            BinaryOp::BitAnd => Op::BitAnd,
            BinaryOp::BitOr => Op::BitOr,
            BinaryOp::BitXor => Op::BitXor,
            BinaryOp::Shl => Op::Shl,
            BinaryOp::Shr => Op::Shr,
            // 短路运算符不会到达这里（两条路径都在此前分派）
            BinaryOp::AndAnd | BinaryOp::OrOr => Op::Eq,
        }
    }
}

/// 二元运算（`==`/`!=`/关系/位/算术；无隐式转换，RUNTIME-EQ-003 / ARITH-002）。
pub fn binary(op: Op, l: &Value, r: &Value) -> RtResult<Value> {
    match op {
        Op::Eq | Op::Ne => {
            let eq = Value::equals(l, r)?;
            Ok(Value::Bool(if op == Op::Eq { eq } else { !eq }))
        }
        Op::Lt | Op::Le | Op::Gt | Op::Ge => {
            // float 关系比较按 IEEE 运算符直落：NaN 参与任何比较一律 false
            // （TYPE-CONV-005 v1.0.1，ADR-0006/RS-007）。NaN 无全序，若走
            // compare 会被兜成 Equal，把 `<=`/`>=` 错判为 true。
            if let (Value::Float(x), Value::Float(y)) = (l, r) {
                let b = match op {
                    Op::Lt => x < y,
                    Op::Le => x <= y,
                    Op::Gt => x > y,
                    _ => x >= y,
                };
                return Ok(Value::Bool(b));
            }
            let Some(cmp) = Value::compare(l, r) else {
                return Err(RtError::new(
                    RtErrorKind::TypeCast,
                    format!(
                        "{} 与 {} 不可比较大小（无隐式转换）",
                        l.type_hint(),
                        r.type_hint()
                    ),
                ));
            };
            let b = match op {
                Op::Lt => cmp.is_lt(),
                Op::Le => cmp.is_le(),
                Op::Gt => cmp.is_gt(),
                _ => cmp.is_ge(),
            };
            Ok(Value::Bool(b))
        }
        Op::BitAnd | Op::BitOr | Op::BitXor | Op::Shl | Op::Shr => {
            let (Value::Int(a), Value::Int(b)) = (l, r) else {
                return Err(RtError::new(
                    RtErrorKind::TypeCast,
                    format!(
                        "{} 与 {} 不可位运算（须为 int）",
                        l.type_hint(),
                        r.type_hint()
                    ),
                ));
            };
            if matches!(op, Op::Shl | Op::Shr) && !(*b >= 0 && *b < 64) {
                return Err(RtError::new(
                    RtErrorKind::Arith,
                    format!("移位位数须在 0..63，得到 {b}"),
                ));
            }
            Ok(Value::Int(match op {
                Op::BitAnd => a & b,
                Op::BitOr => a | b,
                Op::BitXor => a ^ b,
                Op::Shl => a.wrapping_shl(*b as u32),
                _ => a.wrapping_shr(*b as u32),
            }))
        }
        _ => arith(op, l, r),
    }
}

/// 加减乘除模：int 回绕（二补码）、float IEEE；除零/模零为运行时错误。
pub fn arith(op: Op, l: &Value, r: &Value) -> RtResult<Value> {
    if let (Value::Int(a), Value::Int(b)) = (l, r) {
        return Ok(Value::Int(match op {
            Op::Add => a.wrapping_add(*b),
            Op::Sub => a.wrapping_sub(*b),
            Op::Mul => a.wrapping_mul(*b),
            Op::Div => {
                if *b == 0 {
                    return Err(RtError::new(RtErrorKind::Arith, "整数除零"));
                }
                a.wrapping_div(*b)
            }
            Op::Rem => {
                if *b == 0 {
                    return Err(RtError::new(RtErrorKind::Arith, "整数模零"));
                }
                a.wrapping_rem(*b)
            }
            _ => {
                return Err(RtError::new(
                    RtErrorKind::TypeCast,
                    format!("{} 不支持该运算", l.type_hint()),
                ));
            }
        }));
    }
    if let (Value::Float(a), Value::Float(b)) = (l, r) {
        return Ok(Value::Float(match op {
            Op::Add => a + b,
            Op::Sub => a - b,
            Op::Mul => a * b,
            Op::Div => {
                if *b == 0.0 {
                    return Err(RtError::new(RtErrorKind::Arith, "浮点除零"));
                }
                a / b
            }
            _ => {
                return Err(RtError::new(
                    RtErrorKind::TypeCast,
                    "% 不能用于 float".to_string(),
                ));
            }
        }));
    }
    if let (Value::Str(a), Value::Str(b)) = (l, r) {
        if op == Op::Add {
            let mut s = String::with_capacity(a.len() + b.len());
            s.push_str(a);
            s.push_str(b);
            return Ok(Value::str(s));
        }
    }
    Err(RtError::new(
        RtErrorKind::TypeCast,
        format!(
            "{} 与 {} 不可做该运算（无隐式转换）",
            l.type_hint(),
            r.type_hint()
        ),
    ))
}

/// 复合赋值的运算段（`+=` `-=` `*=` `/=`；数组 `+=` 的原位扩展由调用方先行处理）。
pub fn compound(op: CompoundOp, old: &Value, rhs: &Value) -> RtResult<Value> {
    match op {
        CompoundOp::Add => arith(Op::Add, old, rhs),
        CompoundOp::Sub => arith(Op::Sub, old, rhs),
        CompoundOp::Mul => arith(Op::Mul, old, rhs),
        CompoundOp::Div => arith(Op::Div, old, rhs),
    }
}

/// 复合赋值运算键（AST AssignOp / lowering 读算写分解共用）。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CompoundOp {
    Add,
    Sub,
    Mul,
    Div,
}

impl From<AssignOp> for CompoundOp {
    fn from(op: AssignOp) -> CompoundOp {
        match op {
            AssignOp::AddAssign => CompoundOp::Add,
            AssignOp::SubAssign => CompoundOp::Sub,
            AssignOp::MulAssign => CompoundOp::Mul,
            AssignOp::DivAssign => CompoundOp::Div,
            // 纯赋值不会到达运算段（两条路径都在此前分派）
            AssignOp::Assign => CompoundOp::Add,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn b(op: Op, l: &Value, r: &Value) -> bool {
        match binary(op, l, r).unwrap() {
            Value::Bool(x) => x,
            other => panic!("期望 bool，得到 {other:?}"),
        }
    }

    fn nan() -> Value {
        Value::Float(f64::NAN)
    }

    /// NaN 参与任何比较一律 false（TYPE-CONV-005 v1.0.1，ADR-0006/RS-007）：
    /// 关系四运算含反向与 NaN 对 NaN 全 false；`==` 为 false、`!=` 恒 true。
    #[test]
    fn nan_comparisons_all_false() {
        let one = Value::Float(1.0);
        assert!(!b(Op::Lt, &nan(), &one));
        assert!(!b(Op::Le, &nan(), &one));
        assert!(!b(Op::Gt, &nan(), &one));
        assert!(!b(Op::Ge, &nan(), &one));
        // 反向同样 false
        assert!(!b(Op::Lt, &one, &nan()));
        assert!(!b(Op::Ge, &one, &nan()));
        // NaN 对 NaN
        assert!(!b(Op::Lt, &nan(), &nan()));
        assert!(!b(Op::Le, &nan(), &nan()));
        assert!(!b(Op::Gt, &nan(), &nan()));
        assert!(!b(Op::Ge, &nan(), &nan()));
        // == 为 false / != 恒 true（含自身对自身）
        assert!(!b(Op::Eq, &nan(), &nan()));
        assert!(b(Op::Ne, &nan(), &nan()));
        assert!(!b(Op::Eq, &nan(), &one));
        assert!(b(Op::Ne, &nan(), &one));
        // 非 NaN 关系语义不受影响
        assert!(b(Op::Le, &one, &one));
        assert!(b(Op::Ge, &one, &one));
    }
}
