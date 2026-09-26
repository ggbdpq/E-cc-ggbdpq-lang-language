using Lang.Runtime;
using Lang.Syntax;

namespace Lang.Interp;

/// <summary>
/// 双解释器共用的运算键（AST BinaryOp / IR Binop 的公共面；不含 <c>&amp;&amp;</c>/<c>||</c>
/// ——那在两条路径里分别降为短路控制流）。AST 解释器与 IR 解释器共用同一份运算实现
/// ——差分的价值在<b>遍历与求值序</b>，不在运算规则本身；运算规则漂移会同时污染两条
/// 执行路径（lang-rs ops.rs 同构）。
/// </summary>
internal enum Op
{
    Eq, Ne, Lt, Le, Gt, Ge,
    Add, Sub, Mul, Div, Rem,
    BitAnd, BitOr, BitXor, Shl, Shr,
}

/// <summary>复合赋值运算键（AST AssignOp / lowering 读算写分解共用）。</summary>
internal enum CompoundOp
{
    Add,
    Sub,
    Mul,
    Div,
}

/// <summary>二元/复合运算（i64 回绕 = Go int64 语义；无隐式转换，RUNTIME-EQ-003 / RUNTIME-ARITH-002）。</summary>
internal static class Ops
{
    /// <summary>AST BinaryOp → 公共运算键。短路算符不会到达这里（两条路径都在此前分派）。</summary>
    public static Op Of(BinaryOp op) => op switch
    {
        BinaryOp.Eq => Op.Eq,
        BinaryOp.Ne => Op.Ne,
        BinaryOp.Lt => Op.Lt,
        BinaryOp.Le => Op.Le,
        BinaryOp.Gt => Op.Gt,
        BinaryOp.Ge => Op.Ge,
        BinaryOp.Add => Op.Add,
        BinaryOp.Sub => Op.Sub,
        BinaryOp.Mul => Op.Mul,
        BinaryOp.Div => Op.Div,
        BinaryOp.Rem => Op.Rem,
        BinaryOp.BitAnd => Op.BitAnd,
        BinaryOp.BitOr => Op.BitOr,
        BinaryOp.BitXor => Op.BitXor,
        BinaryOp.Shl => Op.Shl,
        BinaryOp.Shr => Op.Shr,
        BinaryOp.AndAnd or BinaryOp.OrOr => Op.Eq,
        _ => throw new InvalidOperationException("未知 BinaryOp（内部不变量）"),
    };

    /// <summary>IR Binop → 公共运算键（枚举成员序两语言 1:1）。</summary>
    public static Op Of(Lang.Ir.Binop op) => op switch
    {
        Lang.Ir.Binop.Eq => Op.Eq,
        Lang.Ir.Binop.Ne => Op.Ne,
        Lang.Ir.Binop.Lt => Op.Lt,
        Lang.Ir.Binop.Le => Op.Le,
        Lang.Ir.Binop.Gt => Op.Gt,
        Lang.Ir.Binop.Ge => Op.Ge,
        Lang.Ir.Binop.Add => Op.Add,
        Lang.Ir.Binop.Sub => Op.Sub,
        Lang.Ir.Binop.Mul => Op.Mul,
        Lang.Ir.Binop.Div => Op.Div,
        Lang.Ir.Binop.Rem => Op.Rem,
        Lang.Ir.Binop.BitAnd => Op.BitAnd,
        Lang.Ir.Binop.BitOr => Op.BitOr,
        Lang.Ir.Binop.BitXor => Op.BitXor,
        Lang.Ir.Binop.Shl => Op.Shl,
        Lang.Ir.Binop.Shr => Op.Shr,
        _ => throw new InvalidOperationException("未知 IR Binop（内部不变量）"),
    };

    /// <summary>AST AssignOp → 复合运算键。纯赋值不会到达运算段（两条路径都在此前分派）。</summary>
    public static CompoundOp Of(AssignOp op) => op switch
    {
        AssignOp.AddAssign => CompoundOp.Add,
        AssignOp.SubAssign => CompoundOp.Sub,
        AssignOp.MulAssign => CompoundOp.Mul,
        AssignOp.DivAssign => CompoundOp.Div,
        AssignOp.Assign => CompoundOp.Add,
        _ => throw new InvalidOperationException("未知 AssignOp（内部不变量）"),
    };

    /// <summary>二元运算（<c>==</c>/<c>!=</c>/关系/位/算术）。</summary>
    public static RtResult<Value> Binary(Op op, Value l, Value r)
    {
        switch (op)
        {
            case Op.Eq or Op.Ne:
                {
                    var eq = Value.EqualsLang(l, r);
                    if (!eq.IsOk)
                    {
                        return RtResult<Value>.Fail(eq.Error!);
                    }

                    return RtResult<Value>.Ok(new Value.Bool(op == Op.Eq ? eq.Value : !eq.Value));
                }

            case Op.Lt or Op.Le or Op.Gt or Op.Ge:
                {
                    var cmp = Value.Compare(l, r);
                    if (cmp is null)
                    {
                        return RtResult<Value>.Fail(new RtError(
                            RtErrorKind.TypeCast,
                            $"{l.TypeHint()} 与 {r.TypeHint()} 不可比较大小（无隐式转换）"));
                    }

                    var b = op switch
                    {
                        Op.Lt => cmp < 0,
                        Op.Le => cmp <= 0,
                        Op.Gt => cmp > 0,
                        _ => cmp >= 0,
                    };
                    return RtResult<Value>.Ok(new Value.Bool(b));
                }

            case Op.BitAnd or Op.BitOr or Op.BitXor or Op.Shl or Op.Shr:
                {
                    if (l is not Value.Int a || r is not Value.Int b2)
                    {
                        return RtResult<Value>.Fail(new RtError(
                            RtErrorKind.TypeCast,
                            $"{l.TypeHint()} 与 {r.TypeHint()} 不可位运算（须为 int）"));
                    }

                    if ((op == Op.Shl || op == Op.Shr) && b2.V is < 0 or >= 64)
                    {
                        return RtResult<Value>.Fail(new RtError(
                            RtErrorKind.Arith, $"移位位数须在 0..63，得到 {b2.V}"));
                    }

                    unchecked
                    {
                        var v = op switch
                        {
                            Op.BitAnd => a.V & b2.V,
                            Op.BitOr => a.V | b2.V,
                            Op.BitXor => a.V ^ b2.V,
                            Op.Shl => a.V << (int)b2.V,
                            _ => a.V >> (int)b2.V,
                        };
                        return RtResult<Value>.Ok(new Value.Int(v));
                    }
                }
        }

        return Arith(op, l, r);
    }

    /// <summary>加减乘除模：int 回绕（二补码）、float IEEE；除零/模零为运行时错误。
    /// C# 整数除零/long.MinValue ÷ -1 抛异常，须先拦（Rust wrapping 语义在 unchecked 下等价）。</summary>
    private static RtResult<Value> Arith(Op op, Value l, Value r)
    {
        if (l is Value.Int li && r is Value.Int ri)
        {
            if ((op == Op.Div || op == Op.Rem) && ri.V == 0)
            {
                return RtResult<Value>.Fail(new RtError(
                    RtErrorKind.Arith, op == Op.Div ? "整数除零" : "整数模零"));
            }

            unchecked
            {
                // MinValue ÷/-1 的回绕特判（unchecked 下 C# 仍对它抛 OverflowException）
                var v = op switch
                {
                    Op.Add => li.V + ri.V,
                    Op.Sub => li.V - ri.V,
                    Op.Mul => li.V * ri.V,
                    Op.Div => li.V == long.MinValue && ri.V == -1 ? li.V : li.V / ri.V,
                    Op.Rem => li.V == long.MinValue && ri.V == -1 ? 0 : li.V % ri.V,
                    _ => 0L,
                };
                return RtResult<Value>.Ok(new Value.Int(v));
            }
        }

        if (l is Value.Float lf && r is Value.Float rf)
        {
            if (op == Op.Div && rf.V == 0.0)
            {
                return RtResult<Value>.Fail(new RtError(RtErrorKind.Arith, "浮点除零"));
            }

            if (op == Op.Rem)
            {
                return RtResult<Value>.Fail(new RtError(RtErrorKind.TypeCast, "% 不能用于 float"));
            }

            var v = op switch
            {
                Op.Add => lf.V + rf.V,
                Op.Sub => lf.V - rf.V,
                Op.Mul => lf.V * rf.V,
                Op.Div => lf.V / rf.V,
                _ => 0.0,
            };
            return RtResult<Value>.Ok(new Value.Float(v));
        }

        if (l is Value.Str ls && r is Value.Str rs && op == Op.Add)
        {
            return RtResult<Value>.Ok(new Value.Str(ls.V + rs.V));
        }

        return RtResult<Value>.Fail(new RtError(
            RtErrorKind.TypeCast,
            $"{l.TypeHint()} 与 {r.TypeHint()} 不可做该运算（无隐式转换）"));
    }

    /// <summary>复合赋值的运算段（<c>+=</c> <c>-=</c> <c>*=</c> <c>/=</c>；数组 <c>+=</c> 的
    /// 原位扩展由调用方先行处理）。</summary>
    public static RtResult<Value> Compound(CompoundOp op, Value old, Value rhs)
    {
        var binop = op switch
        {
            CompoundOp.Add => Op.Add,
            CompoundOp.Sub => Op.Sub,
            CompoundOp.Mul => Op.Mul,
            _ => Op.Div,
        };
        return Binary(binop, old, rhs);
    }
}
