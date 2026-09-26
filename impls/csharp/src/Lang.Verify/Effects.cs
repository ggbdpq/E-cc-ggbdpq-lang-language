using Lang.Contract;
using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Types;

namespace Lang.Verify;

/// <summary>
/// Effect 段事实提取：合并 AST + 语义分析 → <see cref="CallGraph"/> → 不动点。
/// 行为证据：impls/rust/crates/lang-verify/src/effects.rs。
///
/// <para>事实源（SymbolId 面，禁文本搜索）：</para>
/// <list type="bullet">
/// <item>节点 = 全部非预载 fn/method 符号（extern 含 FFI 直接效果）；</item>
/// <item>直接效果 = 内建调用名（<see cref="Effects.BuiltinDirectEffect"/>：println/print→IO、
/// makeChan/send/recv→CHANNEL）+ go 语句→SPAWN + extern→FFI；</item>
/// <item>边 = 直接调用（symbol_of_ref 解析到 fn/method 符号）+ go 目标 + 方法调用
/// （接收者静态类型 → struct.methods 表；类型未知即保守缺边）；</item>
/// <item>函数字面量效果归属定义所在函数（保守近似，与契约推导规则一致）；</item>
/// <item>顶层非声明语句的调用效果归属合成 init 节点（懒登记）。</item>
/// </list>
///
/// <para>HIR→IR lowering 未接线前，本提取是直接效果的保守超集：IR 结构形状
/// （go/extern）在 AST 上同样可得，内建名面与 IR 接线方用同一
/// <see cref="Effects.BuiltinDirectEffect"/> 单一事实源。</para>
/// </summary>
public static class EffectFacts
{
    /// <summary>合成 init 节点的函数身份（与符号编号空间不撞：符号 id 为顺序小整数）。</summary>
    private static readonly FunctionId InitFid = new(uint.MaxValue);

    /// <summary>从一次分析构建调用图并求解。失败 = 引擎良构性被破坏（内部不变量）。</summary>
    public static ExecResult<IReadOnlyList<EffectEntry>> Solve(
        IReadOnlyList<Stmt> stmts, Analysis analysis)
    {
        var cg = new CallGraph();
        try
        {
            // 1) 节点：全部非预载 fn/method；extern 挂 FFI
            foreach (var s in analysis.Symbols.Items)
            {
                if (s.IsBuiltin || s.Kind is not (SymbolKind.Fn or SymbolKind.Method))
                {
                    continue;
                }
                var fid = new FunctionId(s.Id.Value);
                cg.AddFn(fid, s.Name);
                if (s.IsExtern)
                {
                    cg.SetDirect(fid, Effect.Ffi);
                }
            }

            // 2) 走查：直接效果 + 调用/go 边
            var w = new Walk(analysis, cg);
            foreach (var s in stmts)
            {
                switch (s)
                {
                    case Stmt.FnDecl fn:
                        {
                            var owner = analysis.SymbolOfDecl(s) is { } sym
                                ? new FunctionId(sym.Value)
                                : (FunctionId?)null;
                            foreach (var b in fn.Body)
                            {
                                w.WalkStmt(b, owner);
                            }
                            break;
                        }
                    case Stmt.StructDecl or Stmt.EnumDecl or Stmt.Import or Stmt.Extern:
                        // FFI 直接效果已在节点登记时挂；声明无调用事实
                        break;
                    default:
                        // 顶层副作用归属合成 init（懒登记）
                        var init = w.EnsureInit();
                        w.WalkStmt(s, init);
                        break;
                }
            }
            return ExecResult<IReadOnlyList<EffectEntry>>.Ok(cg.Solve());
        }
        catch (EffectsGraphException e)
        {
            return ExecResult<IReadOnlyList<EffectEntry>>.Err(
                ExecError.Fail($"效果图良构性: {e.Message}"));
        }
    }

    private sealed class Walk(Analysis analysis, CallGraph cg)
    {
        private FunctionId? _init;

        /// <summary>懒登记合成 init 节点（顶层副作用的效果归属单位）。</summary>
        public FunctionId EnsureInit()
        {
            if (_init is { } f)
            {
                return f;
            }
            if (!cg.HasFn(InitFid))
            {
                cg.AddFn(InitFid, "init");
            }
            _init = InitFid;
            return InitFid;
        }

        public void WalkStmt(Stmt s, FunctionId? owner)
        {
            switch (s)
            {
                case Stmt.Let let:
                    WalkExpr(let.Init, owner);
                    break;
                case Stmt.Assign a:
                    WalkExpr(a.Target, owner);
                    WalkExpr(a.Value, owner);
                    break;
                case Stmt.ExprStmt es:
                    WalkExpr(es.Expr, owner);
                    break;
                case Stmt.Return ret:
                    if (ret.Value is not null)
                    {
                        WalkExpr(ret.Value, owner);
                    }
                    break;
                case Stmt.If i:
                    WalkExpr(i.Cond, owner);
                    WalkBody(i.ThenBody, owner);
                    if (i.ElseBody is not null)
                    {
                        WalkBody(i.ElseBody, owner);
                    }
                    break;
                case Stmt.While w:
                    WalkExpr(w.Cond, owner);
                    WalkBody(w.Body, owner);
                    break;
                case Stmt.ForIn f:
                    WalkExpr(f.Iter, owner);
                    WalkBody(f.Body, owner);
                    break;
                case Stmt.Break or Stmt.Continue:
                    break;
                case Stmt.FnDecl d:
                    // 嵌套 fn：自有节点为效果归属单位
                    var nested = analysis.SymbolOfDecl(s) is { } sym
                        ? new FunctionId(sym.Value)
                        : (FunctionId?)null;
                    WalkBody(d.Body, nested);
                    break;
                case Stmt.StructDecl or Stmt.EnumDecl or Stmt.Import or Stmt.Extern:
                    break;
                case Stmt.Go go:
                    // go 语句：SPAWN 直接效果 + go 目标照常沿边传播（契约推导规则）
                    if (owner is { } o)
                    {
                        cg.SetDirect(o, Effect.Spawn);
                        if (go.Call is Expr.Call call)
                        {
                            Callee(call.Callee, o);
                            foreach (var arg in call.Args)
                            {
                                WalkExpr(arg, o);
                            }
                        }
                    }
                    break;
            }
        }

        private void WalkBody(IReadOnlyList<Stmt> stmts, FunctionId? owner)
        {
            foreach (var b in stmts)
            {
                WalkStmt(b, owner);
            }
        }

        private void WalkExpr(Expr e, FunctionId? owner)
        {
            switch (e)
            {
                case Expr.Call c:
                    if (owner is { } o)
                    {
                        Callee(c.Callee, o);
                    }
                    foreach (var a in c.Args)
                    {
                        WalkExpr(a, owner);
                    }
                    break;
                case Expr.Unary u:
                    WalkExpr(u.Operand, owner);
                    break;
                case Expr.Binary b:
                    WalkExpr(b.Lhs, owner);
                    WalkExpr(b.Rhs, owner);
                    break;
                case Expr.Range r:
                    WalkExpr(r.Lo, owner);
                    WalkExpr(r.Hi, owner);
                    break;
                case Expr.Index ix:
                    WalkExpr(ix.Obj, owner);
                    WalkExpr(ix.Idx, owner);
                    break;
                case Expr.Slice sl:
                    WalkExpr(sl.Obj, owner);
                    WalkExpr(sl.Lo, owner);
                    WalkExpr(sl.Hi, owner);
                    break;
                case Expr.Field f:
                    WalkExpr(f.Obj, owner);
                    break;
                case Expr.Try t:
                    WalkExpr(t.Operand, owner);
                    break;
                case Expr.StructLit lit:
                    foreach (var fl in lit.Fields)
                    {
                        WalkExpr(fl.Value, owner);
                    }
                    break;
                case Expr.MapLit ml:
                    foreach (var it in ml.Items)
                    {
                        WalkExpr(it.Key, owner);
                        WalkExpr(it.Value, owner);
                    }
                    break;
                case Expr.ArrLit al:
                    foreach (var it in al.Items)
                    {
                        WalkExpr(it, owner);
                    }
                    break;
                case Expr.Match m:
                    WalkExpr(m.Scrutinee, owner);
                    foreach (var arm in m.Arms)
                    {
                        // 模式（绑定/字面量/构造）不含调用，无效果事实，只走臂体
                        WalkExpr(arm.Body, owner);
                    }
                    break;
                case Expr.FnLit fn:
                    // 函数字面量：体内效果归属定义所在函数（保守近似）
                    WalkBody(fn.Body, owner);
                    break;
                case Expr.Ident or Expr.IntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit:
                    break;
            }
        }

        /// <summary>调用目标解析：直接调用（fn/method 符号）或内建直接效果；
        /// 方法调用经接收者静态类型 → struct.methods（未知类型保守缺边）。</summary>
        private void Callee(Expr callee, FunctionId owner)
        {
            switch (callee)
            {
                case Expr.Ident ident:
                    if (analysis.SymbolOfRef(callee) is { } sym)
                    {
                        var s = analysis.Symbols.Get(sym);
                        if (!s.IsBuiltin && s.Kind is SymbolKind.Fn or SymbolKind.Method)
                        {
                            cg.AddCall(owner, new FunctionId(sym.Value));
                            return;
                        }
                    }
                    cg.SetBuiltinCall(owner, ident.Name);
                    break;
                case Expr.Field field:
                    if (MethodOwner(field.Obj, field.Name) is { } target)
                    {
                        cg.AddCall(owner, new FunctionId(target.Value));
                    }
                    break;
                // 间接调用（索引/闭包值）：效果不可静态解析，保守缺边
                default:
                    break;
            }
        }

        /// <summary>接收者类型 + 方法名 → 方法符号（TypeId → struct def → struct 符号 methods 表）。</summary>
        private SymbolId? MethodOwner(Expr obj, string method)
        {
            var t = analysis.TypeOf(obj);
            if (t is null || analysis.Types.Data(t.Value) is not TypeData.Struct def)
            {
                return null;
            }
            foreach (var s in analysis.Symbols.Items)
            {
                if (!s.IsBuiltin && s.Kind == SymbolKind.Struct
                    && s.Nominal is NominalRef.Struct sr && sr.Def == def.Def)
                {
                    return analysis.Symbols.Get(s.Id).Methods.TryGetValue(method, out var m)
                        ? m
                        : null;
                }
            }
            return null;
        }
    }
}
