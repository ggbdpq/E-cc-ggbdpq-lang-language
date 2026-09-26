using Lang.Core;

namespace Lang.Syntax;

// —— 运算符（三组） ——

/// <summary>一元运算符：`- ! &`。</summary>
public enum UnaryOp { Neg, Not, Ref }

/// <summary>二元运算符（18 个；契约文本「17 个」为笔误，与 TS ast.ts 全列表一致）。</summary>
public enum BinaryOp
{
    Eq, Ne, Lt, Le, Gt, Ge,
    Add, Sub, Mul, Div, Rem,
    BitAnd, BitOr, BitXor, Shl, Shr,
    AndAnd, OrOr,
}

/// <summary>赋值运算符（5 个）：`= += -= *= /=`。</summary>
public enum AssignOp { Assign, AddAssign, SubAssign, MulAssign, DivAssign }

// —— 共用小节点 ——

/// <summary>参数 / struct 字段 / 方法接收者共用的 `name: 类型` 形状。</summary>
public sealed record Param(Span Sp, string Name, TypeEx? Typ)
{
    public Span Span => Sp;
}

/// <summary>结构体字面量字段：`name: 值`。</summary>
public sealed record FieldLit(Span Sp, string Name, Expr Value)
{
    public Span Span => Sp;
}

/// <summary>映射字面量键值对：`key: val`。</summary>
public sealed record MapItem(Span Sp, Expr Key, Expr Value)
{
    public Span Span => Sp;
}

/// <summary>match 臂：`模式 => 表达式`（TS 证据里 span 为零值，契约强制超集：一律真实 span）。</summary>
public sealed record Arm(Span Sp, Pat Pat, Expr Body)
{
    public Span Span => Sp;
}

/// <summary>enum case：`Name(载荷类型...)`，载荷按位置。</summary>
public sealed record EnumCase(Span Sp, string Name, IReadOnlyList<TypeEx> Payloads)
{
    public Span Span => Sp;
}

// —— 表达式（18 变体） ——

/// <summary>
/// 表达式。AST 形态决定（A1 冻结登记）：abstract record + 嵌套 sealed record 子型
/// （`Expr.IntLit`），节点 span 统一暴露为 <see cref="Span"/>（起于首记号、止于末记号之后）；
/// 位置参数命名 Sp 以避开基类抽象属性；结构相等由 record 提供。
/// 穷尽分派用 switch expression（无 default 兜底）。
/// </summary>
public abstract record Expr
{
    public abstract Span Span { get; }

    /// <summary>整数字面量（i64，类型恒 int）。</summary>
    public sealed record IntLit(Span Sp, long Value) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>浮点字面量（f64，类型恒 float）。</summary>
    public sealed record FloatLit(Span Sp, double Value) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>字符串字面量（解码后的值；不可变，按码点索引）。</summary>
    public sealed record StrLit(Span Sp, string Value) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>布尔字面量。</summary>
    public sealed record BoolLit(Span Sp, bool Value) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>标识符引用。</summary>
    public sealed record Ident(Span Sp, string Name) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>一元 `- ! &`。</summary>
    public sealed record Unary(Span Sp, UnaryOp Op, Expr Operand) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>二元运算。</summary>
    public sealed record Binary(Span Sp, BinaryOp Op, Expr Lhs, Expr Rhs) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>区间 `lo..hi`（半开）。</summary>
    public sealed record Range(Span Sp, Expr Lo, Expr Hi) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>调用 `callee(args)`。</summary>
    public sealed record Call(Span Sp, Expr Callee, IReadOnlyList<Expr> Args) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>索引 `obj[idx]`。</summary>
    public sealed record Index(Span Sp, Expr Obj, Expr Idx) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>切片 `obj[lo:hi]`（lo/hi 必填，v0 无开区间切片）。</summary>
    public sealed record Slice(Span Sp, Expr Obj, Expr Lo, Expr Hi) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>字段访问 `obj.name`。</summary>
    public sealed record Field(Span Sp, Expr Obj, string Name) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>`e?` —— Result 传播。</summary>
    public sealed record Try(Span Sp, Expr Operand) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>结构体字面量 `Type{...}`。</summary>
    public sealed record StructLit(Span Sp, string TypeName, IReadOnlyList<FieldLit> Fields) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>映射字面量 `{k: v, ...}`。</summary>
    public sealed record MapLit(Span Sp, IReadOnlyList<MapItem> Items) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>数组字面量 `[a, b, ...]`。</summary>
    public sealed record ArrLit(Span Sp, IReadOnlyList<Expr> Items) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>match 表达式（PARSE-005）。</summary>
    public sealed record Match(Span Sp, Expr Scrutinee, IReadOnlyList<Arm> Arms) : Expr
    {
        public override Span Span => Sp;
    }

    /// <summary>匿名函数表达式；name 恒为空串的 FnDecl 形状展开（fnlit type_params 必须保留）。</summary>
    public sealed record FnLit(Span Sp, IReadOnlyList<string> TypeParams, IReadOnlyList<Param> Params,
        TypeEx? Ret, IReadOnlyList<Stmt> Body) : Expr
    {
        public override Span Span => Sp;
    }
}

// —— 模式（4 变体，PARSE-005） ——

/// <summary>模式：通配 / 绑定 / 字面量 / 构造。</summary>
public abstract record Pat
{
    public abstract Span Span { get; }

    /// <summary>`_` 通配。</summary>
    public sealed record Wild(Span Sp) : Pat
    {
        public override Span Span => Sp;
    }

    /// <summary>小写标识符绑定，捕获整个值。</summary>
    public sealed record Bind(Span Sp, string Name) : Pat
    {
        public override Span Span => Sp;
    }

    /// <summary>字面量模式（int/float/string/bool，`==` 语义比较）。</summary>
    public sealed record Lit(Span Sp, Expr Value) : Pat
    {
        public override Span Span => Sp;
    }

    /// <summary>构造模式：大写标识符，可带按位置的载荷子模式。</summary>
    public sealed record Case(Span Sp, string Name, IReadOnlyList<Pat> Subs) : Pat
    {
        public override Span Span => Sp;
    }
}

// —— 类型注解（5 变体） ——

/// <summary>类型注解。</summary>
public abstract record TypeEx
{
    public abstract Span Span { get; }

    /// <summary>命名类型，可带类型实参（`T`、`chan[int]`、`Result[int, string]`）。</summary>
    public sealed record Named(Span Sp, string Name, IReadOnlyList<TypeEx> Args) : TypeEx
    {
        public override Span Span => Sp;
    }

    /// <summary>数组类型 `[T]`。</summary>
    public sealed record Arr(Span Sp, TypeEx Elem) : TypeEx
    {
        public override Span Span => Sp;
    }

    /// <summary>映射类型 `map[K]V`。</summary>
    public sealed record Map(Span Sp, TypeEx Key, TypeEx Val) : TypeEx
    {
        public override Span Span => Sp;
    }

    /// <summary>指针类型 `&T`。</summary>
    public sealed record Ptr(Span Sp, TypeEx Elem) : TypeEx
    {
        public override Span Span => Sp;
    }

    /// <summary>函数类型 `fn(A, B): R`。</summary>
    public sealed record Fn(Span Sp, IReadOnlyList<TypeEx> Params, TypeEx? Ret) : TypeEx
    {
        public override Span Span => Sp;
    }
}

// —— 语句 / 顶层声明（15 变体） ——

/// <summary>语句 / 顶层声明。</summary>
public abstract record Stmt
{
    public abstract Span Span { get; }

    /// <summary>`let x: T = e`（不可重绑）/ `var x = e`（可重绑）。</summary>
    public sealed record Let(Span Sp, string Name, bool Mutable, TypeEx? Typ, Expr Init) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>赋值 / 复合赋值：目标只能是变量、字段链或索引。</summary>
    public sealed record Assign(Span Sp, Expr Target, AssignOp Op, Expr Value) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>表达式语句（求值后丢弃）。</summary>
    public sealed record ExprStmt(Span Sp, Expr Expr) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`return` / `return e`（bare return 为 unit）。</summary>
    public sealed record Return(Span Sp, Expr? Value) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`if c {} else if c2 {} else {}`；else if 展开为单元素块。</summary>
    public sealed record If(Span Sp, Expr Cond, IReadOnlyList<Stmt> ThenBody,
        IReadOnlyList<Stmt>? ElseBody) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`while c {}`。</summary>
    public sealed record While(Span Sp, Expr Cond, IReadOnlyList<Stmt> Body) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`for x in iter {}`。</summary>
    public sealed record ForIn(Span Sp, string Variable, Expr Iter, IReadOnlyList<Stmt> Body) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`break`。</summary>
    public sealed record Break(Span Sp) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`continue`。</summary>
    public sealed record Continue(Span Sp) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>函数 / 方法声明：`fn name[T](参数): 返回 { 体 }`、`fn (r Recv).name(...)`。</summary>
    public sealed record FnDecl(Span Sp, string Name, Param? Recv, IReadOnlyList<string> TypeParams,
        IReadOnlyList<Param> Params, TypeEx? Ret, IReadOnlyList<Stmt> Body) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>struct 声明：字段逐行 `name: 类型`。</summary>
    public sealed record StructDecl(Span Sp, string Name, IReadOnlyList<string> TypeParams,
        IReadOnlyList<Param> Fields) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>enum 声明：case 逐行，载荷按位置。</summary>
    public sealed record EnumDecl(Span Sp, string Name, IReadOnlyList<string> TypeParams,
        IReadOnlyList<EnumCase> Cases) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`go &lt;具名函数调用&gt;`（TASK-SPAWN-002；实参在 go 语句处求值）。</summary>
    public sealed record Go(Span Sp, Expr Call) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>`import "./md5"`。</summary>
    public sealed record Import(Span Sp, string Path) : Stmt
    {
        public override Span Span => Sp;
    }

    /// <summary>
    /// `extern "host" { fn 签名... }`：宿主函数手写签名（无函数体）。
    /// Fns 每项恒为 <see cref="FnDecl"/>（Body 为空；TS 证据同构复用 FnDecl）。
    /// </summary>
    public sealed record Extern(Span Sp, string Host, IReadOnlyList<Stmt> Fns) : Stmt
    {
        public override Span Span => Sp;
    }
}
