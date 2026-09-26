using System.Collections.Generic;
using System.Linq;
using Lang.Core;

namespace Lang.Hir;

/// <summary>
/// Typed HIR 节点模型（语义事实层，第三层）：AST 降糖、已解析、已标注类型的语义树。
/// HIR 消除的语法糖（与 Go/TS/Rust 参考实现一致）：
/// 裸构造器引用 → <see cref="HirExpr.Ctor"/>（0 参）、<c>obj.m(args)</c> → <see cref="HirExpr.MethodCall"/>、
/// <c>go f(args)</c> → <see cref="HirStmt.Spawn"/>、<c>?</c> → <see cref="HirExpr.Try"/>（checker 已约束位置）。
/// 本层不携带类型结构（只有 <see cref="Lang.Core.TypeId"/> 句柄）——类型格由 Lang.Types 拥有。
/// </summary>
public sealed record HirProg(
    IReadOnlyList<HirStructDecl> Structs,
    IReadOnlyList<HirEnumDecl> Enums,
    /// <summary>顶层 let/global 绑定，按声明序。</summary>
    IReadOnlyList<HirLet> Globals,
    /// <summary>函数/方法/extern 声明（方法 Recv 非空）。</summary>
    IReadOnlyList<HirFn> Fns,
    HirFn? Main)
{
    public bool Equals(HirProg? other) =>
        other is not null && Seq.Eq(Structs, other.Structs) && Seq.Eq(Enums, other.Enums)
        && Seq.Eq(Globals, other.Globals) && Seq.Eq(Fns, other.Fns) && Seq.OptEq(Main, other.Main);

    public override int GetHashCode() => HashCode.Combine(
        Seq.Hash(Structs), Seq.Hash(Enums), Seq.Hash(Globals), Seq.Hash(Fns), Main);
}

/// <summary>函数/方法/extern 声明。</summary>
public sealed record HirFn(
    NodeInfo Meta,
    string Name,
    /// <summary>值接收者（方法非空）。</summary>
    HirParam? Recv,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<HirParam> Params,
    /// <summary>无注解（void）时为 null：决定 →Go 是否写返回类型。</summary>
    TypeId? Ret,
    IReadOnlyList<HirStmt> Body,
    bool IsExtern,
    /// <summary>extern 宿主名（<c>extern "go"</c> 的宿主侧）。</summary>
    string ExternHost)
{
    public bool Equals(HirFn? other) =>
        other is not null && Meta.Equals(other.Meta) && Name == other.Name && Seq.OptEq(Recv, other.Recv)
        && Seq.Eq(TypeParams, other.TypeParams) && Seq.Eq(Params, other.Params) && Ret.Equals(other.Ret)
        && Seq.Eq(Body, other.Body) && IsExtern == other.IsExtern && ExternHost == other.ExternHost;

    public override int GetHashCode() => HashCode.Combine(
        Meta, Name, Recv, Seq.Hash(TypeParams), Seq.Hash(Params), Ret, Seq.Hash(Body), HashCode.Combine(IsExtern, ExternHost));
}

/// <summary>参数/接收者。</summary>
public sealed record HirParam(string Name, TypeId? TypeId);

/// <summary>struct 声明。</summary>
public sealed record HirStructDecl(
    NodeInfo Meta, string Name, IReadOnlyList<string> TypeParams, IReadOnlyList<HirFieldDecl> Fields)
{
    public bool Equals(HirStructDecl? other) =>
        other is not null && Meta.Equals(other.Meta) && Name == other.Name
        && Seq.Eq(TypeParams, other.TypeParams) && Seq.Eq(Fields, other.Fields);

    public override int GetHashCode() => HashCode.Combine(Meta, Name, Seq.Hash(TypeParams), Seq.Hash(Fields));
}

public sealed record HirFieldDecl(string Name, TypeId? TypeId);

/// <summary>enum 声明。</summary>
public sealed record HirEnumDecl(
    NodeInfo Meta, string Name, IReadOnlyList<string> TypeParams, IReadOnlyList<HirEnumCase> Cases)
{
    public bool Equals(HirEnumDecl? other) =>
        other is not null && Meta.Equals(other.Meta) && Name == other.Name
        && Seq.Eq(TypeParams, other.TypeParams) && Seq.Eq(Cases, other.Cases);

    public override int GetHashCode() => HashCode.Combine(Meta, Name, Seq.Hash(TypeParams), Seq.Hash(Cases));
}

public sealed record HirEnumCase(
    /// <summary>case 符号（NAME-CTOR-005 全局唯一身份）。</summary>
    SymbolId? Sym,
    string Name,
    /// <summary>case 载荷按声明序。</summary>
    IReadOnlyList<TypeId?> Payloads)
{
    public bool Equals(HirEnumCase? other) =>
        other is not null && Sym.Equals(other.Sym) && Name == other.Name && Seq.Eq(Payloads, other.Payloads);

    public override int GetHashCode() => HashCode.Combine(Sym, Name, Seq.Hash(Payloads));
}

/// <summary>语句（10 变体；穷尽分派用 switch expression，禁 default 兜底吞新变体）。</summary>
public abstract record HirStmt
{
    /// <summary><c>let</c>/顶层 global 绑定语句：载荷统一命名 Value（参数名禁与嵌套类型同名，CS8866）。</summary>
    public sealed record Let(HirLet Value) : HirStmt;

    /// <summary>赋值/复合赋值。IR 侧把复合赋值分解为读-算-写（见 Lang.Ir）。</summary>
    public sealed record Assign(HirAssign Value) : HirStmt;

    /// <summary>表达式语句：值被丢弃。</summary>
    public sealed record Eval(NodeInfo Meta, HirExpr Expr) : HirStmt;

    public sealed record Return(NodeInfo Meta, HirExpr? Val) : HirStmt;

    public sealed record If(NodeInfo Meta, HirExpr Cond, IReadOnlyList<HirStmt> Then, IReadOnlyList<HirStmt> Els) : HirStmt
    {
        public bool Equals(If? other) =>
            other is not null && Meta.Equals(other.Meta) && Cond.Equals(other.Cond)
            && Seq.Eq(Then, other.Then) && Seq.Eq(Els, other.Els);

        public override int GetHashCode() => HashCode.Combine(Meta, Cond, Seq.Hash(Then), Seq.Hash(Els));
    }

    public sealed record While(NodeInfo Meta, HirExpr Cond, IReadOnlyList<HirStmt> Body) : HirStmt
    {
        public bool Equals(While? other) =>
            other is not null && Meta.Equals(other.Meta) && Cond.Equals(other.Cond) && Seq.Eq(Body, other.Body);

        public override int GetHashCode() => HashCode.Combine(Meta, Cond, Seq.Hash(Body));
    }

    /// <summary>for-in（CTRL-FORIN-003：循环变量每轮重新绑定）。</summary>
    public sealed record ForIn(NodeInfo Meta, string Variable, TypeId? VarType, HirExpr Iter, IReadOnlyList<HirStmt> Body) : HirStmt
    {
        public bool Equals(ForIn? other) =>
            other is not null && Meta.Equals(other.Meta) && Variable == other.Variable && VarType.Equals(other.VarType)
            && Iter.Equals(other.Iter) && Seq.Eq(Body, other.Body);

        public override int GetHashCode() => HashCode.Combine(Meta, Variable, VarType, Iter, Seq.Hash(Body));
    }

    public sealed record Break(NodeInfo Meta) : HirStmt;

    public sealed record Continue(NodeInfo Meta) : HirStmt;

    /// <summary><c>go f(args)</c>：目标已被 resolver 约束为顶层 fn（EVAL-GO-007/TASK-SPAWN-002）。</summary>
    public sealed record Spawn(NodeInfo Meta, SymbolId? Func, IReadOnlyList<HirExpr> Args) : HirStmt
    {
        public bool Equals(Spawn? other) =>
            other is not null && Meta.Equals(other.Meta) && Func.Equals(other.Func) && Seq.Eq(Args, other.Args);

        public override int GetHashCode() => HashCode.Combine(Meta, Func, Seq.Hash(Args));
    }

    /// <summary>函数体内嵌套 fn 声明。</summary>
    public sealed record FnStmt(NodeInfo Meta, HirFn Func) : HirStmt;
}

/// <summary><c>let</c>/顶层 global 绑定。</summary>
public sealed record HirLet(NodeInfo Meta, string Name, bool Mutable, HirExpr Init);

public sealed record HirAssign(NodeInfo Meta, HirExpr Target, AssignOp Op, HirExpr Rhs);

/// <summary>表达式（20 变体；穷尽分派用 switch expression，禁 default 兜底吞新变体）。</summary>
public abstract record HirExpr
{
    public sealed record Lit(HirLit Value) : HirExpr;

    /// <summary>局部/全局绑定读取（写目标复用此节点）。</summary>
    public sealed record LocalRef(NodeInfo Meta, string Name) : HirExpr;

    /// <summary>顶层 fn 引用（一等函数值）。</summary>
    public sealed record FnRef(NodeInfo Meta, string Name) : HirExpr;

    /// <summary>内建引用（println/len/…）。</summary>
    public sealed record BuiltinRef(NodeInfo Meta, string Name) : HirExpr;

    /// <summary>构造器调用（裸零参引用已脱糖为 0 参调用）。</summary>
    public sealed record Ctor(NodeInfo Meta, SymbolId? CtorSym, IReadOnlyList<HirExpr> Args) : HirExpr
    {
        public bool Equals(Ctor? other) =>
            other is not null && Meta.Equals(other.Meta) && CtorSym.Equals(other.CtorSym) && Seq.Eq(Args, other.Args);

        public override int GetHashCode() => HashCode.Combine(Meta, CtorSym, Seq.Hash(Args));
    }

    /// <summary>用户 fn（Func 符号）/ 内建（Builtin 名）/ 表达式 callee（Callee 节点）三选一（CALL-RESOLVE-001）。</summary>
    public sealed record Call(NodeInfo Meta, SymbolId? Func, string? Builtin, HirExpr? Callee, IReadOnlyList<HirExpr> Args) : HirExpr
    {
        public bool Equals(Call? other) =>
            other is not null && Meta.Equals(other.Meta) && Func.Equals(other.Func) && Builtin == other.Builtin
            && Seq.OptEq(Callee, other.Callee) && Seq.Eq(Args, other.Args);

        public override int GetHashCode() => HashCode.Combine(Meta, Func, Builtin, Callee, Seq.Hash(Args));
    }

    /// <summary>方法调用（脱糖自 <c>obj.m(args)</c>）；Method 为空时是字段函数值间接调用。</summary>
    public sealed record MethodCall(
        NodeInfo Meta, HirExpr Recv, SymbolId? Method,
        /// <summary>字段函数值间接调用的字段名（Method 非空时为 null）。</summary>
        string? FieldName, IReadOnlyList<HirExpr> Args,
        /// <summary>方法名（或字段名）token 区间，供 rename 消费。</summary>
        Span NameSpan) : HirExpr
    {
        public bool Equals(MethodCall? other) =>
            other is not null && Meta.Equals(other.Meta) && Recv.Equals(other.Recv) && Method.Equals(other.Method)
            && FieldName == other.FieldName && Seq.Eq(Args, other.Args) && NameSpan.Equals(other.NameSpan);

        public override int GetHashCode() => HashCode.Combine(Meta, Recv, Method, FieldName, Seq.Hash(Args), NameSpan);
    }

    public sealed record Unary(NodeInfo Meta, UnaryOp Op, HirExpr Expr) : HirExpr;

    public sealed record Binary(NodeInfo Meta, BinaryOp Op, HirExpr Lhs, HirExpr Rhs) : HirExpr;

    /// <summary><c>&amp;&amp;</c>（短路：左 false 不求值右，EVAL-SHORTCUT-006）。</summary>
    public sealed record AndThen(NodeInfo Meta, HirExpr Lhs, HirExpr Rhs) : HirExpr;

    /// <summary><c>||</c>（短路：左 true 不求值右）。</summary>
    public sealed record OrElse(NodeInfo Meta, HirExpr Lhs, HirExpr Rhs) : HirExpr;

    /// <summary>区间 <c>lo..hi</c>（半开）。</summary>
    public sealed record Range(NodeInfo Meta, HirExpr Lo, HirExpr Hi) : HirExpr;

    /// <summary><c>a[i]</c>（EVAL-INDEX-005：先 obj 后 idx）。</summary>
    public sealed record Index(NodeInfo Meta, HirExpr Obj, HirExpr Idx) : HirExpr;

    /// <summary><c>a[lo:hi]</c>。</summary>
    public sealed record Slice(NodeInfo Meta, HirExpr Obj, HirExpr Lo, HirExpr Hi) : HirExpr;

    public sealed record Field(NodeInfo Meta, HirExpr Obj, string Name) : HirExpr;

    /// <summary><c>?</c> 传播（checker 已约束：操作数为 Result、位置合法、宿主函数返回 Result；RESULT-TRY-003）。</summary>
    public sealed record Try(NodeInfo Meta, HirExpr Expr) : HirExpr;

    public sealed record StructLit(NodeInfo Meta, SymbolId? StructSym, IReadOnlyList<HirStructField> Fields) : HirExpr
    {
        public bool Equals(StructLit? other) =>
            other is not null && Meta.Equals(other.Meta) && StructSym.Equals(other.StructSym) && Seq.Eq(Fields, other.Fields);

        public override int GetHashCode() => HashCode.Combine(Meta, StructSym, Seq.Hash(Fields));
    }

    /// <summary>映射字面量：键值对按源码序（= 插入序，VALUE-MAP-006）。</summary>
    public sealed record MapLit(NodeInfo Meta, IReadOnlyList<HirMapItem> Items) : HirExpr
    {
        public bool Equals(MapLit? other) =>
            other is not null && Meta.Equals(other.Meta) && Seq.Eq(Items, other.Items);

        public override int GetHashCode() => HashCode.Combine(Meta, Seq.Hash(Items));
    }

    public sealed record ArrayLit(NodeInfo Meta, IReadOnlyList<HirExpr> Elems) : HirExpr
    {
        public bool Equals(ArrayLit? other) =>
            other is not null && Meta.Equals(other.Meta) && Seq.Eq(Elems, other.Elems);

        public override int GetHashCode() => HashCode.Combine(Meta, Seq.Hash(Elems));
    }

    /// <summary>match 是表达式（MATCH-EXPR-004）；kind 四分类见 <see cref="MatchKind"/>。</summary>
    public sealed record Match(NodeInfo Meta, HirExpr Scrutinee, MatchKind Kind, IReadOnlyList<HirMatchArm> Arms) : HirExpr
    {
        public bool Equals(Match? other) =>
            other is not null && Meta.Equals(other.Meta) && Scrutinee.Equals(other.Scrutinee)
            && Kind == other.Kind && Seq.Eq(Arms, other.Arms);

        public override int GetHashCode() => HashCode.Combine(Meta, Scrutinee, Kind, Seq.Hash(Arms));
    }

    /// <summary>匿名函数（闭包）。捕获集由语义期给出（VALUE-CAPTURE-004 按引用）；TypeParams 必须保留（lang-rs 教训）。</summary>
    public sealed record FnLit(
        NodeInfo Meta, IReadOnlyList<string> TypeParams, IReadOnlyList<HirParam> Params,
        TypeId? Ret, IReadOnlyList<HirStmt> Body,
        /// <summary>按引用捕获的绑定名（定义处作用域名）。</summary>
        IReadOnlyList<string> Captures) : HirExpr
    {
        public bool Equals(FnLit? other) =>
            other is not null && Meta.Equals(other.Meta) && Seq.Eq(TypeParams, other.TypeParams)
            && Seq.Eq(Params, other.Params) && Ret.Equals(other.Ret) && Seq.Eq(Body, other.Body)
            && Seq.Eq(Captures, other.Captures);

        public override int GetHashCode() => HashCode.Combine(
            Meta, Seq.Hash(TypeParams), Seq.Hash(Params), Ret, Seq.Hash(Body), Seq.Hash(Captures));
    }
}

/// <summary>struct 字面量字段（Name=Value 对，按源码序）。</summary>
public sealed record HirStructField(string Name, HirExpr Value);

/// <summary>字面量（Kind 区分有效载荷字段；int 存 i64、string 存解码后值）。</summary>
public sealed record HirLit(NodeInfo Meta, LitKind Kind, long I64, double F64, string S, bool B);

public sealed record HirMapItem(HirExpr Key, HirExpr Val);

public sealed record HirMatchArm(HirPat Pat, HirExpr Body);

/// <summary>模式（4 变体；构造臂子模式按位置解构载荷，MATCH-BIND-003）。</summary>
public abstract record HirPat
{
    public sealed record Wild() : HirPat;

    /// <summary>单一绑定名捕获载荷。</summary>
    public sealed record Bind(string Name, TypeId? TypeId) : HirPat;

    /// <summary>字面量臂（int/string/bool）。</summary>
    public sealed record Lit(HirLit Value) : HirPat;

    /// <summary>构造器臂（enum/Option/Result），子模式按位置解构载荷。</summary>
    public sealed record Ctor(SymbolId? CtorSym, IReadOnlyList<HirPat> Subs) : HirPat
    {
        public bool Equals(Ctor? other) =>
            other is not null && CtorSym.Equals(other.CtorSym) && Seq.Eq(Subs, other.Subs);

        public override int GetHashCode() => HashCode.Combine(CtorSym, Seq.Hash(Subs));
    }
}

/// <summary>表达式语义类型句柄（无类型节点为 null）——读取节点类型结论，禁止重新推断。</summary>
public static class HirFacts
{
    public static TypeId? TypeOf(HirExpr expr) => expr switch
    {
        HirExpr.Lit l => l.Value.Meta.TypeId,
        HirExpr.LocalRef e => e.Meta.TypeId,
        HirExpr.FnRef e => e.Meta.TypeId,
        HirExpr.BuiltinRef e => e.Meta.TypeId,
        HirExpr.Ctor e => e.Meta.TypeId,
        HirExpr.Call e => e.Meta.TypeId,
        HirExpr.MethodCall e => e.Meta.TypeId,
        HirExpr.Unary e => e.Meta.TypeId,
        HirExpr.Binary e => e.Meta.TypeId,
        HirExpr.AndThen e => e.Meta.TypeId,
        HirExpr.OrElse e => e.Meta.TypeId,
        HirExpr.Range e => e.Meta.TypeId,
        HirExpr.Index e => e.Meta.TypeId,
        HirExpr.Slice e => e.Meta.TypeId,
        HirExpr.Field e => e.Meta.TypeId,
        HirExpr.Try e => e.Meta.TypeId,
        HirExpr.StructLit e => e.Meta.TypeId,
        HirExpr.MapLit e => e.Meta.TypeId,
        HirExpr.ArrayLit e => e.Meta.TypeId,
        HirExpr.Match e => e.Meta.TypeId,
        HirExpr.FnLit e => e.Meta.TypeId,
        _ => throw new InvalidOperationException("HIR 表达式变体未穷尽（禁 default 兜底，新变体必须显式登记）"),
    };
}
