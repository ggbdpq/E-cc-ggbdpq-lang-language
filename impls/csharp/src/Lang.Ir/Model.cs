using System.Collections.Generic;
using Lang.Core;

namespace Lang.Ir;

/// <summary>一元算符。<c>&amp;</c> 不在此列：取址经装箱纪律表达（装箱槽持 Ref，读经 Deref/写经 RefSet）。</summary>
public enum Unop
{
    Neg,
    Not,
}

/// <summary>二元算符 16 种。<c>&amp;&amp;</c>/<c>||</c> 不在此列（降为 <see cref="Terminator.Branch"/> 控制流，EVAL-SHORTCUT-006）。</summary>
public enum Binop
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

/// <summary>
/// Core IR 模型（第四层）：HIR 降级而成的后端中立执行表示。与 lang-rs 指令集 1:1（同名同栈效应）。
/// 形状决策（冻结，见 docs/lang-cs/status/A3-hir-ir.md 登记）：
/// - <b>arena/index 图</b>：函数表/块表/局部表全部扁平表 + newtype 索引，无自引用树。
/// - <b>不含 span</b>（specs/source-span.md §2：IR 为精简执行视图，定位回 HIR 取）。
/// - <b>栈式 + 基本块</b>：每函数划分为块；块内指令操作操作数栈，块以显式终接
///   （<see cref="Terminator"/>）收尾。块入口栈深恒 0、终接后归零，validator 可全静态
///   线性校验栈效应（见 Validator）。
/// - <b>显式化</b>：求值顺序即指令线性序（callee 先、实参左到右）；<c>&amp;&amp;</c>/<c>||</c>/
///   break/continue 降为 <see cref="Terminator.Branch"/>/<see cref="Terminator.Goto"/>；
///   <c>?</c> 为 <see cref="Inst.Try"/>；chan send/recv/spawn 为专指令（非内建）。
/// - <b>引用与捕获</b>：被 <c>&amp;</c> 取址或被闭包捕获的绑定按装箱纪律存储——槽位持 Ref 值
///   （<see cref="Inst.BoxNew"/> 创建；循环变量每轮重绑 = 每轮新 Box），读经 <see cref="Inst.Deref"/>、
///   写经 <see cref="Inst.RefSet"/>（VALUE-REF-003/VALUE-CAPTURE-004）。闭包捕获以 Ref 值进
///   <see cref="Inst.ClosureNew"/>，在被调函数局部表前 CaptureCount 个槽位落地（按引用共享）。
/// - <b>带 Place 溯源的操作数（IR 侧值面契约，A4 解释器同构实现）</b>：操作数栈条目为
///   <c>StackVal { Value, Place? }</c>——常量压入不带溯源；<see cref="Inst.LocalGet"/>/
///   <see cref="Inst.GlobalGet"/> 压入的条目带 <c>Place.Var(env, slot)</c> 溯源；
///   <see cref="Inst.FieldGet"/>/<see cref="Inst.IndexGet"/> 消费带溯源的栈条目时扩展链
///   （<c>Place.Field(base, index)</c> 等），产出值+溯源；<see cref="Inst.FieldSet"/>/
///   <see cref="Inst.IndexSet"/>/<see cref="Inst.ArrayPush"/>/<see cref="Inst.RefSet"/>
///   要求目标条目带溯源，写穿透到原槽/容器（别名可见）。IR 侧义务（lowering 冻结纪律，
///   lang-rs B1 教训）：赋值目标的地址组件一律经 LocalGet/GlobalGet 直压 + FieldGet/IndexGet
///   链上栈，<b>禁止落临时槽深拷贝</b>（EVAL-ASSIGN-003 地址组件先于右值且保 place 身份）；
///   右值与多块形态操作数才落临时槽。
/// </summary>
public sealed record IrProgram(
    IReadOnlyList<StructDecl> Structs,
    IReadOnlyList<EnumDecl> Enums,
    /// <summary>全局变量表（按声明序；初始化写在 <c>init</c> 函数体内，声明序 GlobalSet）。</summary>
    IReadOnlyList<GlobalDecl> Globals,
    /// <summary>函数表；<see cref="Lang.Core.FunctionId"/> 即下标。含方法与 extern。</summary>
    IReadOnlyList<IrFunction> Functions,
    /// <summary>全局初始化函数（运行 main 前执行一次；无全局时可缺省）。</summary>
    FunctionId? Init,
    /// <summary>顶层 main（可缺省）。</summary>
    FunctionId? Main)
{
    public bool Equals(IrProgram? other) =>
        other is not null && Seq.Eq(Structs, other.Structs) && Seq.Eq(Enums, other.Enums)
        && Seq.Eq(Globals, other.Globals) && Seq.Eq(Functions, other.Functions)
        && Init.Equals(other.Init) && Main.Equals(other.Main);

    public override int GetHashCode() => HashCode.Combine(
        Seq.Hash(Structs), Seq.Hash(Enums), Seq.Hash(Globals), Seq.Hash(Functions), Init, Main);
}

/// <summary>struct 声明。</summary>
public sealed record StructDecl(
    string Name,
    IReadOnlyList<string> TypeParams,
    /// <summary>字段按声明序（StructNew 按此序弹栈）。</summary>
    IReadOnlyList<FieldDecl> Fields)
{
    public bool Equals(StructDecl? other) =>
        other is not null && Name == other.Name && Seq.Eq(TypeParams, other.TypeParams) && Seq.Eq(Fields, other.Fields);

    public override int GetHashCode() => HashCode.Combine(Name, Seq.Hash(TypeParams), Seq.Hash(Fields));
}

public sealed record FieldDecl(string Name, TypeId? TypeId);

/// <summary>enum 声明。</summary>
public sealed record EnumDecl(
    string Name,
    IReadOnlyList<string> TypeParams,
    /// <summary>case 按声明序（SwitchEnum arms 与此平行；EnumNew 按此序弹载荷）。</summary>
    IReadOnlyList<EnumCase> Cases)
{
    public bool Equals(EnumDecl? other) =>
        other is not null && Name == other.Name && Seq.Eq(TypeParams, other.TypeParams) && Seq.Eq(Cases, other.Cases);

    public override int GetHashCode() => HashCode.Combine(Name, Seq.Hash(TypeParams), Seq.Hash(Cases));
}

public sealed record EnumCase(string Name, IReadOnlyList<TypeId?> PayloadTypes)
{
    public bool Equals(EnumCase? other) =>
        other is not null && Name == other.Name && Seq.Eq(PayloadTypes, other.PayloadTypes);

    public override int GetHashCode() => HashCode.Combine(Name, Seq.Hash(PayloadTypes));
}

/// <summary>全局变量声明。</summary>
public sealed record GlobalDecl(string Name, bool Mutable, TypeId? TypeId);

/// <summary>局部槽位声明。</summary>
public sealed record LocalDecl(string Name, bool Mutable, TypeId? TypeId);

/// <summary>函数签名要素（栈效应校验所需的最小集）。</summary>
public readonly record struct FnSig(int ParamCount, bool HasRet);

/// <summary>
/// 函数/方法/extern/闭包体的 IR。locals 布局 =
/// <c>[捕获槽][参数槽][局部/临时]</c>（按分配序）；extern 函数必须无块。
/// </summary>
public sealed record IrFunction(
    string Name,
    /// <summary>闭包捕获槽个数：<c>locals[0..CaptureCount)</c> 为捕获槽（按引用），
    /// 其后为参数，再后是普通局部/临时。</summary>
    int CaptureCount,
    FnSig Sig,
    IReadOnlyList<LocalDecl> Locals,
    /// <summary>基本块；块 0 为入口。extern 函数必须为空。</summary>
    IReadOnlyList<Block> Blocks,
    /// <summary>extern 宿主函数名；非 extern 为 null。</summary>
    string? ExternHost)
{
    /// <summary>捕获槽 + 参数所需的槽位下界（validator 用于查 locals 表长度）。</summary>
    public int FixedSlots => CaptureCount + Sig.ParamCount;

    public bool Equals(IrFunction? other) =>
        other is not null && Name == other.Name && CaptureCount == other.CaptureCount
        && Sig.Equals(other.Sig) && Seq.Eq(Locals, other.Locals) && Seq.Eq(Blocks, other.Blocks)
        && ExternHost == other.ExternHost;

    public override int GetHashCode() => HashCode.Combine(
        Name, CaptureCount, Sig, Seq.Hash(Locals), Seq.Hash(Blocks), ExternHost);
}

/// <summary>基本块：线性指令 + 显式终接。入口栈深 0，终接后栈深 0（validator 强制不变量）。</summary>
public sealed record Block(IReadOnlyList<Inst> Insts, Terminator Term)
{
    public bool Equals(Block? other) =>
        other is not null && Seq.Eq(Insts, other.Insts) && Term.Equals(other.Term);

    public override int GetHashCode() => HashCode.Combine(Seq.Hash(Insts), Term);
}

/// <summary>
/// 栈式指令（37 变体；穷尽分派用 switch expression，禁 default 兜底吞新变体）。
/// 注释给出「操作数栈弹压序」（弹序 = 压序逆序）与栈效应 <c>(in → out)</c>。
/// </summary>
public abstract record Inst
{
    // —— 常量（0 → 1）——

    /// <summary>int 字面量（i64 回绕语义）。</summary>
    public sealed record ConstI64(long Value) : Inst;

    public sealed record ConstF64(double Value) : Inst;

    public sealed record ConstBool(bool Value) : Inst;

    public sealed record ConstStr(string Value) : Inst;

    // —— 局部/全局读写 ——

    /// <summary>读局部槽位（0 → 1；操作数带 Place.Var 溯源）。</summary>
    public sealed record LocalGet(LocalId Local) : Inst;

    /// <summary>写局部槽位（1 → 0）。</summary>
    public sealed record LocalSet(LocalId Local) : Inst;

    /// <summary>读全局（0 → 1；操作数带 Place.Var(root, slot) 溯源）。</summary>
    public sealed record GlobalGet(GlobalId Global) : Inst;

    /// <summary>写全局（1 → 0）。</summary>
    public sealed record GlobalSet(GlobalId Global) : Inst;

    // —— 运算 ——

    /// <summary>一元（1 → 1）。</summary>
    public sealed record Unary(Unop Op) : Inst;

    /// <summary>二元：先压左后压右，弹右弹左（2 → 1）。</summary>
    public sealed record Binary(Binop Op) : Inst;

    /// <summary>丢弃栈顶（1 → 0；表达式语句结果落栈用）。</summary>
    public sealed record Pop() : Inst;

    // —— 调用 ——

    /// <summary>直接调用：实参按左到右压栈（in = 目标参数个数，out = 目标 HasRet）。</summary>
    public sealed record Call(FunctionId Func) : Inst;

    /// <summary>闭包值调用：先压闭包值再压实参（in = Argc + 1，out = 调用点声明 HasRet；
    /// 静态类型保证与闭包签名一致，运行时不符为内部错误）。</summary>
    public sealed record CallIndirect(int Argc, bool HasRet) : Inst;

    /// <summary>内建调用：实参左到右压栈（in = Argc，out = 调用点声明 HasRet）。
    /// 内建身份为名（push/println/…；makeChan/send/recv 已升为专指令）。
    /// 名不做名单硬校验——宿主可扩展内建（如 http.get），效果提取未知名缺省视为纯。</summary>
    public sealed record CallBuiltin(string Name, int Argc, bool HasRet) : Inst;

    /// <summary><c>go f(args)</c>：实参在 go 语句处立即求值压栈；本指令启动 Task，返回值丢弃
    /// （in = Argc，out = 0；Argc 由 validator 对照目标签名，EVAL-GO-007/TASK-SPAWN-002）。</summary>
    public sealed record Spawn(FunctionId Func, int Argc) : Inst;

    /// <summary>创建闭包：弹 Captures 个 Ref 值作为捕获环境（按引用捕获，VALUE-CAPTURE-004；
    /// 按 captures 升序压栈，被调捕获槽 i 接第 i 次压入的 Ref）。
    /// Captures 必须等于目标函数 CaptureCount（validator 强制）。</summary>
    public sealed record ClosureNew(FunctionId Func, int Captures) : Inst;

    // —— 索引/切片/字段 ——

    /// <summary><c>a[i]</c> 读：先压 obj 后压 idx（2 → 1；EVAL-INDEX-005）。</summary>
    public sealed record IndexGet() : Inst;

    /// <summary><c>a[i] = v</c> 写回容器：压序 obj, idx, val（3 → 0；别名可见，VALUE-SHARE-002）。</summary>
    public sealed record IndexSet() : Inst;

    /// <summary><c>a[lo:hi]</c>：压序 obj, lo, hi（3 → 1）。</summary>
    public sealed record SliceGet() : Inst;

    /// <summary><c>obj.f</c> 读（1 → 1；字段以名寻址，后端需名映射）。</summary>
    public sealed record FieldGet(string Field) : Inst;

    /// <summary><c>obj.f = v</c>：压序 obj, val（2 → 0；冻结合同，写经 place 溯源穿透）。</summary>
    public sealed record FieldSet(string Field) : Inst;

    /// <summary>数组 <c>+=</c> 原位 push：压序 arr, val（2 → 0；别名可见，VALUE-SHARE-002）。</summary>
    public sealed record ArrayPush() : Inst;

    // —— 构造（构造类指令 pop_n 栈序 = 冻结合同）——

    /// <summary>数组字面量：元素左到右压栈（in = Count，out = 1）。</summary>
    public sealed record ArrayNew(int Count) : Inst;

    /// <summary>映射字面量：键值对左到右压栈（压序 k1,v1,k2,v2,…；in = 2*Count，out = 1；
    /// 插入序 = 压入对序，VALUE-MAP-006）。</summary>
    public sealed record MapNew(int Count) : Inst;

    /// <summary>struct 字面量：字段值按声明序压栈（in = 声明字段数，out = 1）。</summary>
    public sealed record StructNew(StructId Struct) : Inst;

    /// <summary>enum 构造：载荷按 case 声明序压栈（in = case 载荷数，out = 1）。</summary>
    public sealed record EnumNew(EnumId Enum, int Case) : Inst;

    /// <summary>区间 <c>lo..hi</c>：压序 lo, hi（2 → 1；半开）。</summary>
    public sealed record RangeNew() : Inst;

    // —— 引用/装箱 ——

    /// <summary>新建 Ref 装入初值（1 → 1）。装箱纪律见模型文档。</summary>
    public sealed record BoxNew() : Inst;

    /// <summary>读穿透：弹一值，Ref 则压入指称副本，非 Ref 原样回压（1 → 1）。</summary>
    public sealed record Deref() : Inst;

    /// <summary>写穿透：压序 Ref, val（2 → 0；所有别名可见）。</summary>
    public sealed record RefSet() : Inst;

    // —— match 分派取载荷 ——

    /// <summary>取 enum 值第 Index 个载荷：弹 enum 值，压载荷副本（1 → 1）。</summary>
    public sealed record EnumPayloadGet(int Index) : Inst;

    // —— for-in 迭代协议（显式化，CTRL-FORIN-003）——

    /// <summary>由可迭代对象建迭代器（1 → 1；数组索引序/映射插入序/字符串码点序/区间半开）。</summary>
    public sealed record IterNew() : Inst;

    /// <summary>迭代下一步：弹迭代器，压下一元素（1 → 1）；耗尽则跳 Done。
    /// 迭代器本体驻留局部槽（不跨块压栈——块入口栈深恒 0）；
    /// 循环变量每轮重绑由 lowering 逐轮 BoxNew 保证。</summary>
    public sealed record IterNext(BlockId Done) : Inst;

    // —— chan（专指令，非内建）——

    /// <summary><c>makeChan()</c>：缓冲 1024（语言常量，无操作数）（0 → 1；CHAN-CREATE-001）。</summary>
    public sealed record ChanNew() : Inst;

    /// <summary><c>send(ch, v)</c>：压序 ch, val（2 → 0；恒产 ()，满为运行时错误，CHAN-SEND-002）。</summary>
    public sealed record ChanSend() : Inst;

    /// <summary><c>recv(ch)</c>：弹 ch，压 Result[T,error]（1 → 1；FIFO；空 = Err，CHAN-RECV-003）。</summary>
    public sealed record ChanRecv() : Inst;

    // —— `?` 传播（RESULT-TRY-003）——

    /// <summary>弹 Result 值：Ok(v) 压 v 继续走本块（1 → 1）；
    /// Err(e) 将 e 存入 Err 槽位并跳 Fail 块（fail 块入口栈深 0，
    /// 从 err 槽读取错误值合成 Err 返回——函数边界传播）。</summary>
    public sealed record Try(LocalId Err, BlockId Fail) : Inst;
}

/// <summary>
/// 块终接指令（6 变体）：消费栈顶后块结束，栈深归零；每个块恰一个。
/// 穷尽分派用 switch expression，禁 default 兜底吞新变体。
/// </summary>
public abstract record Terminator
{
    /// <summary>无条件跳转（0 → 0；break/continue 的降形）。</summary>
    public sealed record Goto(BlockId Target) : Terminator;

    /// <summary>条件分支：弹 bool（1 → 0）。</summary>
    public sealed record Branch(BlockId IfTrue, BlockId IfFalse) : Terminator;

    /// <summary>字面量 int match 分派：弹 int（1 → 0）；arms 依源码臂序，首个命中即跳。</summary>
    public sealed record SwitchInt(IReadOnlyList<(long Value, BlockId Target)> Arms, BlockId? Default) : Terminator
    {
        public bool Equals(SwitchInt? other) =>
            other is not null && Seq.Eq(Arms, other.Arms) && Default.Equals(other.Default);

        public override int GetHashCode() => HashCode.Combine(Seq.Hash(Arms), Default);
    }

    /// <summary>
    /// enum/Option/Result match 分派：弹 enum 值（1 → 0）；
    /// Arms 与 enum case 声明序平行（长度必须相等，validator 强制）；
    /// 穷尽 match 可省 Default；无臂命中且无 Default 为运行时错误（RUNTIME-MATCH-001）。
    /// </summary>
    public sealed record SwitchEnum(EnumId Enum, IReadOnlyList<BlockId> Arms, BlockId? Default) : Terminator
    {
        public bool Equals(SwitchEnum? other) =>
            other is not null && Enum.Equals(other.Enum) && Seq.Eq(Arms, other.Arms) && Default.Equals(other.Default);

        public override int GetHashCode() => HashCode.Combine(Enum, Seq.Hash(Arms), Default);
    }

    /// <summary>函数返回（in = HasValue；无值即返回 ()）。</summary>
    public sealed record Return(bool HasValue) : Terminator;

    /// <summary>不可达锚（穷尽 match 兜底；执行到即内部错误）。</summary>
    public sealed record Unreachable() : Terminator;
}

// 注：字符串/bool 字面量 match 分派不设专用 SwitchStr/SwitchBool——
// 字符串臂经 `Binary(Eq)` 链 + Branch 降级（被测值先入临时槽），bool 同理。

