using Lang.Core;
using Lang.Semantic;
using Lang.Syntax;

namespace Lang.Graph;

/// <summary>
/// Analysis → Graph 填充器：把一次语义分析的符号/引用事实落成语义图。
/// 行为证据：impls/rust/crates/lang-graph/src/fill.rs（1:1 对齐，C# 参数符号 span
/// 形态差异见 bind_params 注）。
///
/// <para>边语义（与冻结 <see cref="EdgeKind"/> 一一对应）：</para>
/// <list type="bullet">
/// <item><see cref="EdgeKind.Defines"/>：模块 → 顶层声明（fn/method/绑定/struct/enum/extern）；</item>
/// <item><see cref="EdgeKind.Contains"/>：struct → 方法、fn → 参数/内部绑定/嵌套 fn、enum → 构造器；</item>
/// <item><see cref="EdgeKind.Calls"/>：Call 的 callee Ident（go 语句除外）；<see cref="EdgeKind.Spawn"/>：go 语句目标；</item>
/// <item><see cref="EdgeKind.Writes"/>：赋值目标 Ident；<see cref="EdgeKind.Reads"/>：读位置 Ident；</item>
/// <item><see cref="EdgeKind.References"/>：StructLit 类型引用 + match 构造模式引用；</item>
/// <item><see cref="EdgeKind.Imports"/>：模块 → 被导入文件节点（<see cref="NodeKind.File"/>）；</item>
/// <item><see cref="EdgeKind.Returns"/>：fn/method → 返回注解指向的用户 struct/enum 符号。</item>
/// </list>
///
/// <para>全部事实取自 <see cref="Analysis"/> 的符号表与引用映射（SymbolId 事实，禁文本搜索）；
/// 指向预载符号（println/Some/None/Ok/Err…）的引用不落边（预载符号不建节点，保守缺边）。
/// 节点身份：arena 内符号沿用 <see cref="SymbolId"/>；模块/File 节点为合成 id，自
/// <c>symbols.Count</c> 起连续分配（与 arena 编号空间不撞，确定性）。
/// 已知缺口（与 rust 同步登记）：方法调用边依赖接收者静态类型，等检查器类型面
/// 在图上开放后补——直接调用/go/读/写/类型引用已全覆盖。</para>
/// </summary>
public static class GraphFill
{
    /// <summary>引用位置分类（决定 Ident 落哪类边）。</summary>
    private enum Pos { Read, Call, Write, Spawn }

    /// <summary>从一次分析填充语义图。stmts 必须与 analysis 消费的是同一棵 AST
    /// （查询视图按节点引用取事实，跨树返回 null）。指向未登记节点的边跳过
    /// （保守缺边，不虚构事实）；节点重复登记等良构性破坏以 GraphException 报告。</summary>
    public static Graph FromAnalysis(IReadOnlyList<Stmt> stmts, Analysis analysis, string moduleName)
    {
        var g = new Graph();

        // 1) 符号节点：全部非预载符号入图
        foreach (var s in analysis.Symbols.Items)
        {
            if (s.IsBuiltin)
            {
                continue;
            }
            g.AddNode(s.Id, s.Kind switch
            {
                SymbolKind.Fn => NodeKind.Function,
                SymbolKind.Method => NodeKind.Method,
                SymbolKind.Var => NodeKind.Variable,
                SymbolKind.Param => NodeKind.Parameter,
                SymbolKind.Struct => NodeKind.Struct,
                SymbolKind.Enum => NodeKind.Enum,
                SymbolKind.Ctor => NodeKind.Variant,
                _ => throw new InvalidOperationException($"未登记的 SymbolKind: {s.Kind}"),
            }, s.Name);
        }

        // 2) 合成节点：模块 + 被导入文件
        var moduleId = new SymbolId((uint)analysis.Symbols.Count);
        g.AddNode(moduleId, NodeKind.Module, moduleName);
        var fileSeq = (uint)(analysis.Symbols.Count + 1);
        var fileIds = new List<(string Path, SymbolId Id)>();

        // 3) 顶层 defines/imports/returns/contains（struct→method、enum→ctor）
        foreach (var s in stmts)
        {
            switch (s)
            {
                case Stmt.FnDecl d:
                    {
                        if (analysis.SymbolOfDecl(s) is not { } sym)
                        {
                            continue;
                        }
                        Connect(g, moduleId, EdgeKind.Defines, sym);
                        // returns：返回注解指向的用户 struct/enum
                        var retName = analysis.Symbols.Get(sym).TypeEx is { } t ? NamedRoot(t) : null;
                        if (retName is not null && FindTypeNamed(analysis, retName) is { } target)
                        {
                            Connect(g, sym, EdgeKind.Returns, target);
                        }
                        // 方法挂 struct（struct 符号的 methods 表是唯一事实源）
                        if (d.Recv is not null)
                        {
                            foreach (var st in analysis.Symbols.Items)
                            {
                                if (st.Kind != SymbolKind.Struct || st.IsBuiltin)
                                {
                                    continue;
                                }
                                if (st.Methods.Values.Any(m => m == sym))
                                {
                                    Connect(g, st.Id, EdgeKind.Contains, sym);
                                    break;
                                }
                            }
                        }
                        BindParams(g, analysis, d, sym);
                        break;
                    }
                case Stmt.Let:
                    if (analysis.SymbolOfDecl(s) is { } letSym)
                    {
                        Connect(g, moduleId, EdgeKind.Defines, letSym);
                    }
                    break;
                case Stmt.StructDecl sd:
                    if (SymbolAt(analysis, SymbolKind.Struct, sd.Span.Source, sd.Span.Start.Offset) is { } stSym)
                    {
                        Connect(g, moduleId, EdgeKind.Defines, stSym);
                    }
                    break;
                case Stmt.EnumDecl ed:
                    {
                        if (SymbolAt(analysis, SymbolKind.Enum, ed.Span.Source, ed.Span.Start.Offset) is not { } enSym)
                        {
                            continue;
                        }
                        Connect(g, moduleId, EdgeKind.Defines, enSym);
                        foreach (var c in ed.Cases)
                        {
                            if (SymbolAt(analysis, SymbolKind.Ctor, c.Span.Source, c.Span.Start.Offset) is { } ctor)
                            {
                                Connect(g, enSym, EdgeKind.Contains, ctor);
                            }
                        }
                        break;
                    }
                case Stmt.Extern ex:
                    foreach (var f in ex.Fns)
                    {
                        if (f is Stmt.FnDecl fd
                            && SymbolAt(analysis, SymbolKind.Fn, fd.Span.Source, fd.Span.Start.Offset) is { } fnSym)
                        {
                            Connect(g, moduleId, EdgeKind.Defines, fnSym);
                        }
                    }
                    break;
                case Stmt.Import im:
                    {
                        SymbolId fid;
                        var found = fileIds.FirstOrDefault(p => p.Path == im.Path);
                        if (found.Path == im.Path)
                        {
                            fid = found.Id;
                        }
                        else
                        {
                            fid = new SymbolId(fileSeq++);
                            g.AddNode(fid, NodeKind.File, im.Path);
                            fileIds.Add((im.Path, fid));
                        }
                        Connect(g, moduleId, EdgeKind.Imports, fid);
                        break;
                    }
            }
        }

        // 4) 引用/调用/写/go 边（携带所属函数上下文的深度走查）
        var ctx = new Ctx(g, analysis, null, moduleId);
        foreach (var s in stmts)
        {
            if (s is Stmt.FnDecl top)
            {
                // 顶层 FnDecl 的 defines 已落；这里只补体内的引用边
                var sym = analysis.SymbolOfDecl(s);
                var saved = ctx.Owner;
                ctx.Owner = sym;
                foreach (var b in top.Body)
                {
                    ctx.WalkStmt(b);
                }
                ctx.Owner = saved;
            }
            else
            {
                ctx.WalkStmt(s);
            }
        }
        return g;
    }

    /// <summary>fn → 参数的 contains 边。C# 事实形态：参数符号 span 起于名字 token
    /// （rust 证据里参数符号 span 与 FnDecl 全 span 同值——对齐口径改为「AST 参数节点
    /// span 锚」：Param.Span.Start 即名字 token 起点，精确且无嵌套 fn 冲突）。
    /// 同名参数本身即 E2001，不产生干净程序。</summary>
    private static void BindParams(Graph g, Analysis analysis, Stmt.FnDecl d, SymbolId fnSym)
    {
        var named = new List<Param>();
        if (d.Recv is { } r && r.Name != "_")
        {
            named.Add(r);
        }
        named.AddRange(d.Params.Where(p => p.Name != "_"));
        foreach (var p in named)
        {
            var found = FindParam(analysis, p.Span.Source, p.Span.Start.Offset, p.Name);
            if (found is { } pid)
            {
                Connect(g, fnSym, EdgeKind.Contains, pid);
            }
        }
    }

    private sealed class Ctx(Graph g, Analysis analysis, SymbolId? owner, SymbolId moduleId)
    {
        public SymbolId? Owner { get; set; } = owner;

        private SymbolId From => Owner ?? moduleId;

        private void RefEdge(Expr e, Pos pos)
        {
            if (analysis.SymbolOfRef(e) is not { } sym)
            {
                return;
            }
            var kind = pos switch
            {
                Pos.Read => EdgeKind.Reads,
                Pos.Call => EdgeKind.Calls,
                Pos.Write => EdgeKind.Writes,
                Pos.Spawn => EdgeKind.Spawn,
                _ => throw new InvalidOperationException("Pos 未穷尽"),
            };
            Connect(g, From, kind, sym);
        }

        public void WalkStmt(Stmt s)
        {
            switch (s)
            {
                case Stmt.Let letStmt:
                    WalkExpr(letStmt.Init, Pos.Read);
                    if (analysis.SymbolOfDecl(s) is { } letSym && Owner is { } letOwner)
                    {
                        Connect(g, letOwner, EdgeKind.Contains, letSym);
                    }
                    break;
                case Stmt.Assign a:
                    AssignTarget(a.Target);
                    WalkExpr(a.Value, Pos.Read);
                    break;
                case Stmt.ExprStmt es:
                    WalkExpr(es.Expr, Pos.Read);
                    break;
                case Stmt.Return ret:
                    if (ret.Value is not null)
                    {
                        WalkExpr(ret.Value, Pos.Read);
                    }
                    break;
                case Stmt.If i:
                    WalkExpr(i.Cond, Pos.Read);
                    WalkBody(i.ThenBody);
                    if (i.ElseBody is not null)
                    {
                        WalkBody(i.ElseBody);
                    }
                    break;
                case Stmt.While w:
                    WalkExpr(w.Cond, Pos.Read);
                    WalkBody(w.Body);
                    break;
                case Stmt.ForIn f:
                    WalkExpr(f.Iter, Pos.Read);
                    if (analysis.SymbolOfDecl(s) is { } forSym && Owner is { } forOwner)
                    {
                        Connect(g, forOwner, EdgeKind.Contains, forSym);
                    }
                    WalkBody(f.Body);
                    break;
                case Stmt.Break or Stmt.Continue:
                    break;
                case Stmt.FnDecl d:
                    {
                        // 嵌套 fn：定义/contains + 切换所属上下文
                        var fnSym = analysis.SymbolOfDecl(s);
                        if (Owner is { } fnOwner && fnSym is { } nestedSym)
                        {
                            Connect(g, fnOwner, EdgeKind.Contains, nestedSym);
                        }
                        if (fnSym is { } s2)
                        {
                            BindParams(g, analysis, d, s2);
                        }
                        var saved = Owner;
                        Owner = fnSym;
                        WalkBody(d.Body);
                        Owner = saved;
                        break;
                    }
                case Stmt.StructDecl or Stmt.EnumDecl or Stmt.Import:
                    break;
                case Stmt.Extern:
                    break; // extern 无函数体，无引用事实
                case Stmt.Go go:
                    if (go.Call is Expr.Call call)
                    {
                        if (call.Callee is Expr.Ident)
                        {
                            WalkExpr(call.Callee, Pos.Spawn);
                        }
                        foreach (var arg in call.Args)
                        {
                            WalkExpr(arg, Pos.Read);
                        }
                    }
                    break;
            }
        }

        private void WalkBody(IReadOnlyList<Stmt> stmts)
        {
            foreach (var b in stmts)
            {
                WalkStmt(b);
            }
        }

        private void AssignTarget(Expr t)
        {
            switch (t)
            {
                case Expr.Ident:
                    WalkExpr(t, Pos.Write);
                    break;
                case Expr.Field f:
                    AssignTarget(f.Obj);
                    break;
                case Expr.Index ix:
                    AssignTarget(ix.Obj);
                    WalkExpr(ix.Idx, Pos.Read);
                    break;
                default:
                    WalkExpr(t, Pos.Read);
                    break;
            }
        }

        private void WalkExpr(Expr e, Pos pos)
        {
            switch (e)
            {
                case Expr.Ident:
                    RefEdge(e, pos);
                    break;
                case Expr.Unary u:
                    WalkExpr(u.Operand, Pos.Read);
                    break;
                case Expr.Binary b:
                    WalkExpr(b.Lhs, Pos.Read);
                    WalkExpr(b.Rhs, Pos.Read);
                    break;
                case Expr.Range r:
                    WalkExpr(r.Lo, Pos.Read);
                    WalkExpr(r.Hi, Pos.Read);
                    break;
                case Expr.Call c:
                    WalkExpr(c.Callee, Pos.Call);
                    foreach (var a in c.Args)
                    {
                        WalkExpr(a, Pos.Read);
                    }
                    break;
                case Expr.Index ix:
                    WalkExpr(ix.Obj, Pos.Read);
                    WalkExpr(ix.Idx, Pos.Read);
                    break;
                case Expr.Slice sl:
                    WalkExpr(sl.Obj, Pos.Read);
                    WalkExpr(sl.Lo, Pos.Read);
                    WalkExpr(sl.Hi, Pos.Read);
                    break;
                case Expr.Field f:
                    WalkExpr(f.Obj, Pos.Read);
                    break;
                case Expr.Try t:
                    WalkExpr(t.Operand, Pos.Read);
                    break;
                case Expr.StructLit lit:
                    // 类型位置引用
                    if (analysis.SymbolOfRef(e) is { } litSym)
                    {
                        Connect(g, From, EdgeKind.References, litSym);
                    }
                    foreach (var fl in lit.Fields)
                    {
                        WalkExpr(fl.Value, Pos.Read);
                    }
                    break;
                case Expr.MapLit ml:
                    foreach (var it in ml.Items)
                    {
                        WalkExpr(it.Key, Pos.Read);
                        WalkExpr(it.Value, Pos.Read);
                    }
                    break;
                case Expr.ArrLit al:
                    foreach (var it in al.Items)
                    {
                        WalkExpr(it, Pos.Read);
                    }
                    break;
                case Expr.Match m:
                    WalkExpr(m.Scrutinee, Pos.Read);
                    foreach (var arm in m.Arms)
                    {
                        WalkPat(arm.Pat);
                        WalkExpr(arm.Body, Pos.Read);
                    }
                    break;
                case Expr.FnLit fn:
                    // FnLit 自身不是符号；体内语句照常按当前所属函数落边
                    WalkBody(fn.Body);
                    break;
                case Expr.IntLit or Expr.FloatLit or Expr.StrLit or Expr.BoolLit:
                    break;
            }
        }

        private void WalkPat(Pat p)
        {
            switch (p)
            {
                case Pat.Wild or Pat.Lit:
                    break;
                case Pat.Bind:
                    if (analysis.SymbolOfPat(p) is { } bindSym && Owner is { } bindOwner)
                    {
                        Connect(g, bindOwner, EdgeKind.Contains, bindSym);
                    }
                    break;
                case Pat.Case c:
                    // 构造模式引用
                    if (analysis.SymbolOfPat(p) is { } caseSym)
                    {
                        Connect(g, From, EdgeKind.References, caseSym);
                    }
                    foreach (var sp in c.Subs)
                    {
                        WalkPat(sp);
                    }
                    break;
            }
        }
    }

    /// <summary>加边；任一端点未登记（预载符号不建节点）则跳过。</summary>
    private static void Connect(Graph g, SymbolId from, EdgeKind kind, SymbolId to)
    {
        if (g.Node(from) is null || g.Node(to) is null)
        {
            return;
        }
        g.AddEdge(new GraphEdge(from, kind, to));
    }

    /// <summary>按（source, 起点码点, 名字）定位声明符号（struct/enum/extern fn 的声明锚；
    /// resolver 不为这些语句发 decl_syms，span 同值即同一声明的结构事实）。</summary>
    private static SymbolId? SymbolAt(Analysis analysis, SymbolKind kind, SourceId source, uint offset)
    {
        foreach (var s in analysis.Symbols.Items)
        {
            if (s.Kind == kind && !s.IsBuiltin
                && s.Span.Source == source && s.Span.Start.Offset == offset)
            {
                return s.Id;
            }
        }
        return null;
    }

    /// <summary>按（source, 名字 token 起点码点）定位参数符号（fn → 参数 contains 边的锚）。</summary>
    private static SymbolId? FindParam(Analysis analysis, SourceId source, uint offset, string name)
    {
        foreach (var s in analysis.Symbols.Items)
        {
            if (s.Kind == SymbolKind.Param && !s.IsBuiltin
                && s.Span.Source == source && s.Span.Start.Offset == offset && s.Name == name)
            {
                return s.Id;
            }
        }
        return null;
    }

    /// <summary>用户 struct/enum 符号（按名；类型命名空间顶层唯一，重名即 E2001 脏程序）。</summary>
    private static SymbolId? FindTypeNamed(Analysis analysis, string name)
    {
        foreach (var s in analysis.Symbols.Items)
        {
            if (!s.IsBuiltin && (s.Kind == SymbolKind.Struct || s.Kind == SymbolKind.Enum)
                && s.Name == name)
            {
                return s.Id;
            }
        }
        return null;
    }

    /// <summary>命名类型的根名（`Option[int]` → "Option"；用于 returns 边判定）。</summary>
    private static string? NamedRoot(TypeEx t) =>
        t is TypeEx.Named n ? n.Name : null;
}
