using Lang.Core;
using Lang.Syntax;

namespace Lang.Refactor;

/// <summary>
/// AST 走查（预序）：rename/safe-remove 的引用面事实来源。
/// 行为证据：impls/rust/crates/lang-refactor/src/walk.rs。
///
/// <para>单一递归走查覆盖全部 15 语句 / 18 表达式 / 4 模式变体（含 FnLit 体内语句、
/// match 臂与构造子模式、extern 内联签名），访问顺序 = 源码出现序（确定性）。</para>
/// </summary>
public static class AstWalk
{
    /// <summary>预序走查：先访节点自身，再递归子节点（回调可空）。</summary>
    public static void Walk(IReadOnlyList<Stmt> stmts, Action<Stmt>? onStmt, Action<Expr>? onExpr, Action<Pat>? onPat)
    {
        foreach (var s in stmts)
        {
            onStmt?.Invoke(s);
            switch (s)
            {
                case Stmt.Let let:
                    WalkExpr(let.Init, onExpr, onPat);
                    break;
                case Stmt.Assign a:
                    WalkExpr(a.Target, onExpr, onPat);
                    WalkExpr(a.Value, onExpr, onPat);
                    break;
                case Stmt.ExprStmt es:
                    WalkExpr(es.Expr, onExpr, onPat);
                    break;
                case Stmt.Return r:
                    if (r.Value is not null)
                    {
                        WalkExpr(r.Value, onExpr, onPat);
                    }
                    break;
                case Stmt.If i:
                    Walk(i.ThenBody, onStmt, onExpr, onPat);
                    if (i.ElseBody is not null)
                    {
                        Walk(i.ElseBody, onStmt, onExpr, onPat);
                    }
                    break;
                case Stmt.While w:
                    Walk(w.Body, onStmt, onExpr, onPat);
                    break;
                case Stmt.ForIn f:
                    Walk(f.Body, onStmt, onExpr, onPat);
                    break;
                case Stmt.FnDecl d:
                    Walk(d.Body, onStmt, onExpr, onPat);
                    break;
                case Stmt.Extern ex:
                    Walk(ex.Fns, onStmt, onExpr, onPat);
                    break;
                case Stmt.Go go:
                    WalkExpr(go.Call, onExpr, onPat);
                    break;
                case Stmt.Break or Stmt.Continue or Stmt.StructDecl or Stmt.EnumDecl or Stmt.Import:
                    break;
            }
        }
    }

    private static void WalkExpr(Expr e, Action<Expr>? onExpr, Action<Pat>? onPat)
    {
        onExpr?.Invoke(e);
        switch (e)
        {
            case Expr.Unary u:
                WalkExpr(u.Operand, onExpr, onPat);
                break;
            case Expr.Binary b:
                WalkExpr(b.Lhs, onExpr, onPat);
                WalkExpr(b.Rhs, onExpr, onPat);
                break;
            case Expr.Range r:
                WalkExpr(r.Lo, onExpr, onPat);
                WalkExpr(r.Hi, onExpr, onPat);
                break;
            case Expr.Call c:
                WalkExpr(c.Callee, onExpr, onPat);
                foreach (var a in c.Args)
                {
                    WalkExpr(a, onExpr, onPat);
                }
                break;
            case Expr.Index ix:
                WalkExpr(ix.Obj, onExpr, onPat);
                WalkExpr(ix.Idx, onExpr, onPat);
                break;
            case Expr.Slice sl:
                WalkExpr(sl.Obj, onExpr, onPat);
                WalkExpr(sl.Lo, onExpr, onPat);
                WalkExpr(sl.Hi, onExpr, onPat);
                break;
            case Expr.Field f:
                WalkExpr(f.Obj, onExpr, onPat);
                break;
            case Expr.Try t:
                WalkExpr(t.Operand, onExpr, onPat);
                break;
            case Expr.StructLit lit:
                foreach (var fl in lit.Fields)
                {
                    WalkExpr(fl.Value, onExpr, onPat);
                }
                break;
            case Expr.MapLit ml:
                foreach (var it in ml.Items)
                {
                    WalkExpr(it.Key, onExpr, onPat);
                    WalkExpr(it.Value, onExpr, onPat);
                }
                break;
            case Expr.ArrLit al:
                foreach (var it in al.Items)
                {
                    WalkExpr(it, onExpr, onPat);
                }
                break;
            case Expr.Match m:
                WalkExpr(m.Scrutinee, onExpr, onPat);
                foreach (var arm in m.Arms)
                {
                    WalkPat(arm.Pat, onPat);
                    WalkExpr(arm.Body, onExpr, onPat);
                }
                break;
            case Expr.FnLit fn:
                Walk(fn.Body, null, onExpr, onPat);
                break;
            case Expr.Ident or Expr.IntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit:
                break;
        }
    }

    private static void WalkPat(Pat p, Action<Pat>? onPat)
    {
        onPat?.Invoke(p);
        if (p is Pat.Case c)
        {
            foreach (var sp in c.Subs)
            {
                WalkPat(sp, onPat);
            }
        }
    }

    /// <summary>目标符号的全部引用点（Ident/StructLit 引用；出现序）。</summary>
    public static List<Expr> RefsOf(RefactorInput input, SymbolId target)
    {
        var refs = new List<Expr>();
        Walk(input.Merged, null,
            e => { if (input.Analysis.SymbolOfRef(e) == target) refs.Add(e); }, null);
        return refs;
    }

    /// <summary>目标符号的声明语句锚（Let/ForIn/FnDecl，任何深度；出现序）。</summary>
    public static List<Stmt> DeclStmtsOf(RefactorInput input, SymbolId target)
    {
        var decls = new List<Stmt>();
        Walk(input.Merged,
            s => { if (input.Analysis.SymbolOfDecl(s) == target) decls.Add(s); }, null, null);
        return decls;
    }

    /// <summary>目标符号的绑定模式点（match 绑定；出现序）。</summary>
    public static List<Pat> PatBindingsOf(RefactorInput input, SymbolId target)
    {
        var pats = new List<Pat>();
        Walk(input.Merged, null, null,
            p => { if (input.Analysis.SymbolOfPat(p) == target) pats.Add(p); });
        return pats;
    }

    /// <summary>目标符号的引用点及其<b>所属函数</b>（参数改名定位用；出现序去重）。</summary>
    public static List<SymbolId?> RefOwnersOf(RefactorInput input, SymbolId target)
    {
        var owners = new List<SymbolId?>();
        WalkOwned(input, input.Merged, null, (e, owner) =>
        {
            if (input.Analysis.SymbolOfRef(e) == target && !owners.Contains(owner))
            {
                owners.Add(owner);
            }
        });
        return owners;
    }

    /// <summary>带所属函数上下文的表达式走查（owner = 最近一层 FnDecl 的声明符号；
    /// 嵌套 fn 切换上下文；FnLit 不切换——匿名函数不是符号）。</summary>
    public static void WalkOwned(RefactorInput input, IReadOnlyList<Stmt> stmts,
        SymbolId? owner, Action<Expr, SymbolId?> f)
    {
        foreach (var s in stmts)
        {
            switch (s)
            {
                case Stmt.Let let:
                    WalkExprOwned(input, let.Init, owner, f);
                    break;
                case Stmt.Assign a:
                    WalkExprOwned(input, a.Target, owner, f);
                    WalkExprOwned(input, a.Value, owner, f);
                    break;
                case Stmt.ExprStmt es:
                    WalkExprOwned(input, es.Expr, owner, f);
                    break;
                case Stmt.Return { Value: not null } r:
                    WalkExprOwned(input, r.Value, owner, f);
                    break;
                case Stmt.If i:
                    WalkExprOwned(input, i.Cond, owner, f);
                    WalkOwned(input, i.ThenBody, owner, f);
                    if (i.ElseBody is not null)
                    {
                        WalkOwned(input, i.ElseBody, owner, f);
                    }
                    break;
                case Stmt.While w:
                    WalkExprOwned(input, w.Cond, owner, f);
                    WalkOwned(input, w.Body, owner, f);
                    break;
                case Stmt.ForIn fi:
                    WalkExprOwned(input, fi.Iter, owner, f);
                    WalkOwned(input, fi.Body, owner, f);
                    break;
                case Stmt.FnDecl d:
                    var next = input.Analysis.SymbolOfDecl(s);
                    WalkOwned(input, d.Body, next, f);
                    break;
                case Stmt.Extern ex:
                    WalkOwned(input, ex.Fns, owner, f);
                    break;
                case Stmt.Go go:
                    WalkExprOwned(input, go.Call, owner, f);
                    break;
                default:
                    break;
            }
        }
    }

    private static void WalkExprOwned(RefactorInput input, Expr e, SymbolId? owner, Action<Expr, SymbolId?> f)
    {
        f(e, owner);
        switch (e)
        {
            case Expr.Unary u:
                WalkExprOwned(input, u.Operand, owner, f);
                break;
            case Expr.Binary b:
                WalkExprOwned(input, b.Lhs, owner, f);
                WalkExprOwned(input, b.Rhs, owner, f);
                break;
            case Expr.Range r:
                WalkExprOwned(input, r.Lo, owner, f);
                WalkExprOwned(input, r.Hi, owner, f);
                break;
            case Expr.Call c:
                WalkExprOwned(input, c.Callee, owner, f);
                foreach (var a in c.Args)
                {
                    WalkExprOwned(input, a, owner, f);
                }
                break;
            case Expr.Index ix:
                WalkExprOwned(input, ix.Obj, owner, f);
                WalkExprOwned(input, ix.Idx, owner, f);
                break;
            case Expr.Slice sl:
                WalkExprOwned(input, sl.Obj, owner, f);
                WalkExprOwned(input, sl.Lo, owner, f);
                WalkExprOwned(input, sl.Hi, owner, f);
                break;
            case Expr.Field fl:
                WalkExprOwned(input, fl.Obj, owner, f);
                break;
            case Expr.Try t:
                WalkExprOwned(input, t.Operand, owner, f);
                break;
            case Expr.StructLit lit:
                foreach (var field in lit.Fields)
                {
                    WalkExprOwned(input, field.Value, owner, f);
                }
                break;
            case Expr.MapLit ml:
                foreach (var it in ml.Items)
                {
                    WalkExprOwned(input, it.Key, owner, f);
                    WalkExprOwned(input, it.Value, owner, f);
                }
                break;
            case Expr.ArrLit al:
                foreach (var it in al.Items)
                {
                    WalkExprOwned(input, it, owner, f);
                }
                break;
            case Expr.Match m:
                WalkExprOwned(input, m.Scrutinee, owner, f);
                foreach (var arm in m.Arms)
                {
                    WalkExprOwned(input, arm.Body, owner, f);
                }
                break;
            case Expr.FnLit fn:
                WalkOwned(input, fn.Body, owner, f);
                break;
            default:
                break;
        }
    }
}
