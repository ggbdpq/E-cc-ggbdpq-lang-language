namespace Lang.Hir;

/// <summary>归一算符（语义层封闭集合；A1 的 token 在 lowering 映射到此，不依赖 token 形态）。</summary>
public enum UnaryOp
{
    Neg,
    Not,
    /// <summary><c>&amp;x</c>（仅变量，E3012；语义见 VALUE-REF-003）。</summary>
    Ref,
}

/// <summary>二元算符 16 种。<c>&amp;&amp;</c>/<c>||</c> 不在此列——它们是 <see cref="HirExpr.AndThen"/>/<see cref="HirExpr.OrElse"/>（EVAL-SHORTCUT-006）。</summary>
public enum BinaryOp
{
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

/// <summary>赋值算符 5 种。复合赋值求值序见 EVAL-ASSIGN-003。</summary>
public enum AssignOp
{
    Assign,
    AddAssign,
    SubAssign,
    MulAssign,
    DivAssign,
}

/// <summary>match 四分类（MATCH-KIND-001；混用形态由 checker 拒绝）。</summary>
public enum MatchKind
{
    Literal,
    Enum,
    Result,
    Option,
}

/// <summary>字面量类别（kind 区分有效载荷字段；与 TS/Go 参考实现同构）。</summary>
public enum LitKind
{
    Int,
    Float,
    Str,
    Bool,
}
