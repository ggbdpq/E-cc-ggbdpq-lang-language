using Lang.Contract;
using Lang.Core;
using Lang.Graph;
using Lang.Refactor;
using Lang.Semantic;
using Lang.Syntax;
using Lang.Types;
using LangVerify = Lang.Verify.EffectFacts;

namespace Lang.Cli;

/// <summary>
/// <c>inspect symbols|refs|type|effects|callers|callees|impact [dir] [--json]</c>。
/// 最小实现面（契约 §2 的本波子集，对照 lang-rs inspect.rs 同构）：
/// - symbols：非预载符号全列（SymbolId 升序 = canonical 序）；
/// - refs：walk + symbol_of_ref 事实（read/write/call/type 四类）；
/// - type：--file/--line/--column → 最内层包含该点的表达式节点；
/// - effects：Lang.Verify EffectFacts（AST 事实 → CallGraph 不动点）；
/// - callers/callees/impact：Lang.Graph 图（沿 calls/spawn 边的闭包，--depth 限深）。
/// </summary>
internal static class Inspect
{
    private sealed class InspectArgs
    {
        public List<string> Positionals = [];
        public string? File;
        public uint? Line;
        public uint? Column;
        public int? Depth;
        public bool Json;
    }

    private static InspectArgs ParseArgs(IReadOnlyList<string> rest)
    {
        var outArgs = new InspectArgs();
        string[] flagsWithValue = ["--file", "--line", "--column", "--depth", "--symbol"];
        var i = 0;
        while (i < rest.Count)
        {
            var a = rest[i];
            if (a == "--json")
            {
                outArgs.Json = true;
            }
            else if (flagsWithValue.Contains(a) && i + 1 < rest.Count)
            {
                var v = rest[i + 1];
                switch (a)
                {
                    case "--file":
                        outArgs.File = v;
                        break;
                    case "--line":
                        outArgs.Line = uint.TryParse(v, out var ln) ? ln : null;
                        break;
                    case "--column":
                        outArgs.Column = uint.TryParse(v, out var col) ? col : null;
                        break;
                    case "--depth":
                        outArgs.Depth = int.TryParse(v, out var d) ? d : null;
                        break;
                }

                i++; // 跳过旗标值
            }
            else if (!a.StartsWith('-'))
            {
                outArgs.Positionals.Add(a);
            }

            i++;
        }

        return outArgs;
    }

    internal static int CmdInspect(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            Out.PrintJson(Out.ErrorEnvelopeOf(
                "inspect", "缺少子命令：symbols|refs|type|effects|callers|callees|impact"));
            return 2;
        }

        var sub = args[0];
        var kind = $"inspect.{sub}";
        var a = ParseArgs(args.Skip(1).ToList());
        // 位置参数：refs/callers/callees/impact 取 <sym> [dir]，其余取 [dir]
        var takesSym = sub is "refs" or "references" or "callers" or "callees" or "impact";
        string? sym;
        string dir;
        if (takesSym)
        {
            sym = a.Positionals.Count > 0 ? a.Positionals[0] : null;
            dir = a.Positionals.Count > 1 ? a.Positionals[1] : ".";
        }
        else
        {
            sym = null;
            dir = a.Positionals.Count > 0 ? a.Positionals[0] : ".";
        }

        var load = Loader.LoadModule(dir, includeTests: false);
        Loader.CliModule module;
        switch (load)
        {
            case Loader.LoadResult.LoadError loadErr:
                return ReportError(kind, $"装载失败: {loadErr.Message}", a.Json, 1);
            case Loader.LoadResult.ParseError parse:
                return ReportError(kind, $"模块存在 {parse.Diagnostics.Count} 条解析诊断，语义面不可查询", a.Json, 1);
            case Loader.LoadResult.Ok ok:
                module = ok.Module;
                break;
            default:
                throw new InvalidOperationException("LoadResult 变体未穷尽（内部不变量）");
        }

        if (module.Analysis.HasErrors())
        {
            return ReportError(
                kind, $"模块存在 {module.Analysis.Diags.Count} 条诊断，语义面不可查询", a.Json, 1);
        }

        switch (sub)
        {
            case "symbols":
                return InspectSymbols(module, kind, a.Json);
            case "refs" or "references":
                return InspectRefs(module, kind, sym, a.Json);
            case "type":
                return InspectType(module, kind, a);
            case "effects":
                return InspectEffects(module, kind, a.Json);
            case "callers" or "callees" or "impact":
                return InspectGraph(module, sub, kind, sym, a.Depth, a.Json);
            default:
                Out.PrintJson(Out.ErrorEnvelopeOf(kind, $"未知子命令 {sub}"));
                return 2;
        }
    }

    private static int ReportError(string kind, string msg, bool asJson, int code)
    {
        if (asJson)
        {
            Out.PrintJson(Out.ErrorEnvelopeOf(kind, msg));
        }
        else
        {
            Console.Error.WriteLine(msg);
        }

        return code;
    }

    /// <summary>契约 kind 字符串（specs §3 Symbol）。</summary>
    private static string ContractKind(SymbolKind kind) => kind switch
    {
        SymbolKind.Fn => "function",
        SymbolKind.Method => "method",
        SymbolKind.Var => "variable",
        SymbolKind.Param => "parameter",
        SymbolKind.Struct => "struct",
        SymbolKind.Enum => "enum",
        SymbolKind.Ctor => "constructor",
        _ => throw new InvalidOperationException("SymbolKind 变体未穷尽（内部不变量）"),
    };

    private static SpanDto SpanJson(Span span, SourceNames files) => SpanDto.Of(span, files);

    private static string TypeStrOf(Loader.CliModule module, TypeId? ty) =>
        ty is { } t ? Lang.Types.Ops.TypeStr(module.Analysis.Types, t) : "unknown";

    /// <summary>符号类型串：fn/method 走 FnType（函数签名登记表），其余走 SymbolType；
    /// 两表皆无 → "unknown"（诚实缺省）。</summary>
    private static string SymbolTypeStr(Loader.CliModule module, Symbol s) =>
        s.Kind is SymbolKind.Fn or SymbolKind.Method
            ? TypeStrOf(module, module.Analysis.FnType(s.Id) ?? module.Analysis.SymbolType(s.Id))
            : TypeStrOf(module, module.Analysis.SymbolType(s.Id));

    /// <summary>符号参数解析（协议 &lt;sym&gt;：数字 id 或名字；名字多义拒绝）。</summary>
    private static (SymbolId? Id, string? Error) ResolveSymbol(Loader.CliModule module, string? arg)
    {
        if (arg is null)
        {
            return (null, "缺少符号参数（<sym>：数字 ID 或名字）");
        }

        if (arg.All(char.IsAsciiDigit))
        {
            if (!uint.TryParse(arg, out var n)
                || n >= (uint)module.Analysis.Symbols.Count
                || module.Analysis.Symbols.Get(new SymbolId(n)).IsBuiltin)
            {
                return (null, $"未知符号 {arg}");
            }

            return (new SymbolId(n), null);
        }

        var hits = module.Analysis.Symbols.Items
            .Where(s => !s.IsBuiltin && s.Name == arg)
            .Select(s => s.Id).ToList();
        return hits.Count switch
        {
            1 => (hits[0], null),
            0 => (null, $"未知符号 {arg}"),
            _ => (null, $"符号名 {arg} 对应多个符号，请用数字 ID"),
        };
    }

    // ———— symbols ————

    private static int InspectSymbols(Loader.CliModule module, string kind, bool asJson)
    {
        var symbols = new List<Out.SymbolDto>();
        foreach (var s in module.Analysis.Symbols.Items)
        {
            if (s.IsBuiltin)
            {
                continue;
            }

            symbols.Add(new Out.SymbolDto
            {
                SymbolId = s.Id.Value,
                Name = s.Name,
                Kind = ContractKind(s.Kind),
                Definition = SpanJson(s.Span, module.Names),
                Type = SymbolTypeStr(module, s),
                Arity = s.Arity,
                Module = module.Names.FileOf(s.Span.Source),
            });
        }

        if (asJson)
        {
            Out.PrintJsonSoft(new Out.SymbolsEnvelope { Kind = kind, Symbols = symbols });
        }
        else
        {
            foreach (var s in symbols)
            {
                Console.WriteLine($"{s.SymbolId}\t{s.Kind}\t{s.Name}\t{s.Type}");
            }

            Console.WriteLine($"{symbols.Count} 个符号");
        }

        return 0;
    }

    // ———— refs ————

    /// <summary>调用位置 Ident span 集（全树；ref 分类用）。</summary>
    private static HashSet<(uint, uint)> CallSites(Loader.CliModule module)
    {
        var calls = new HashSet<(uint, uint)>();
        AstWalk.Walk(module.Merged, null, e =>
        {
            if (e is Expr.Call { Callee: Expr.Ident ident })
            {
                calls.Add((ident.Span.Source.Value, ident.Span.Start.Offset));
            }
        }, null);
        return calls;
    }

    /// <summary>目标符号的全部引用点（read/write/call/type 分类；file+offset 序）。</summary>
    private static List<(Span Span, string Kind)> ClassifyRefs(Loader.CliModule module, SymbolId target)
    {
        // 赋值目标 Ident（全树收集。lang-rs inspect.rs 最小实现只扫顶层 Assign——
        // 体内写点会被降级 classified 为 read；此处按 Analysis 事实补全，登记为有意分歧）
        var writes = new HashSet<(uint, uint)>();
        AstWalk.Walk(module.Merged,
            s =>
            {
                if (s is Stmt.Assign { Target: Expr.Ident ident })
                {
                    writes.Add((ident.Span.Source.Value, ident.Span.Start.Offset));
                }
            }, null, null);

        var calls = CallSites(module);
        var refs = new List<Expr>();
        AstWalk.Walk(module.Merged, null,
            e => { if (module.Analysis.SymbolOfRef(e) == target) refs.Add(e); }, null);
        var outRefs = refs.Select(e =>
        {
            var span = e.Span;
            var key = (span.Source.Value, span.Start.Offset);
            var k = e switch
            {
                Expr.Ident when calls.Contains(key) => "call",
                Expr.Ident when writes.Contains(key) => "write",
                Expr.Ident => "read",
                Expr.StructLit => "type",
                _ => "read",
            };
            return (span, k);
        }).ToList();
        outRefs.Sort((x, y) =>
        {
            var bySource = x.span.Source.Value.CompareTo(y.span.Source.Value);
            return bySource != 0 ? bySource : x.span.Start.Offset.CompareTo(y.span.Start.Offset);
        });
        return outRefs;
    }

    private static int InspectRefs(Loader.CliModule module, string kind, string? sym, bool asJson)
    {
        var (target, err) = ResolveSymbol(module, sym);
        if (err is not null)
        {
            return ReportError(kind, err, asJson, 1);
        }

        var refs = ClassifyRefs(module, target!.Value);
        if (asJson)
        {
            Out.PrintJsonSoft(new Out.ReferencesEnvelope
            {
                Kind = kind,
                References = [.. refs.Select(r => new Out.ReferenceDto
                {
                    SymbolId = target.Value.Value,
                    Span = SpanJson(r.Span, module.Names),
                    ReferenceKind = r.Kind,
                })],
            });
        }
        else
        {
            foreach (var (span, k) in refs)
            {
                Console.WriteLine($"{module.Names.FileOf(span.Source)}:{span.Start.Line}:{span.Start.Column}\t{k}");
            }

            Console.WriteLine($"{refs.Count} 处引用");
        }

        return 0;
    }

    // ———— type ————

    /// <summary>(line, column) 1 基 → 0 基码点 offset（列越界取行尾；行越界取文末）。</summary>
    internal static uint? OffsetAt(string text, uint line, uint column)
    {
        if (line == 0)
        {
            return 0;
        }

        uint curLine = 1;
        uint col = 1;
        uint offset = 0;
        foreach (var ch in text)
        {
            if (curLine == line)
            {
                if (col >= column)
                {
                    return offset;
                }

                if (ch == '\n')
                {
                    return offset; // 行尾兜底
                }
            }

            if (ch == '\n')
            {
                curLine++;
                col = 1;
            }
            else
            {
                col++;
            }

            offset++;
        }

        return curLine == line ? offset : null;
    }

    /// <summary>最内层包含该点的表达式（span 最短优先；同长取先序）。</summary>
    private static (Expr E, Span Span)? InnermostExprAt(Loader.CliModule module, SourceId sid, uint offset)
    {
        Expr? best = null;
        var bestSpan = default(Span);
        AstWalk.Walk(module.Merged, null, e =>
        {
            var span = e.Span;
            if (span.Source != sid)
            {
                return;
            }

            var inside = span.Start.Offset <= offset
                && (offset < span.End.Offset
                    || (span.Start.Offset == span.End.Offset && offset == span.Start.Offset));
            if (!inside)
            {
                return;
            }

            var shorter = best is null
                || span.End.Offset - span.Start.Offset < bestSpan.End.Offset - bestSpan.Start.Offset;
            if (shorter)
            {
                best = e;
                bestSpan = span;
            }
        }, null);
        return best is null ? null : (best, bestSpan);
    }

    private static string ExprKindName(Expr e) => e switch
    {
        Expr.IntLit => "int_lit",
        Expr.FloatLit => "float_lit",
        Expr.StrLit => "str_lit",
        Expr.BoolLit => "bool_lit",
        Expr.Ident => "ident",
        Expr.Unary => "unary",
        Expr.Binary => "binary",
        Expr.Range => "range",
        Expr.Call => "call",
        Expr.Index => "index",
        Expr.Slice => "slice",
        Expr.Field => "field",
        Expr.Try => "try",
        Expr.StructLit => "struct_lit",
        Expr.MapLit => "map_lit",
        Expr.ArrLit => "array_lit",
        Expr.Match => "match",
        Expr.FnLit => "fn_lit",
        _ => throw new InvalidOperationException("Expr 变体未穷尽（内部不变量）"),
    };

    private static int InspectType(Loader.CliModule module, string kind, InspectArgs a)
    {
        if (a.File is not { } file || a.Line is not { } line || a.Column is not { } column)
        {
            return ReportError(kind, "缺少 --file/--line/--column 参数", a.Json, 2);
        }

        var unit = module.Files.FirstOrDefault(f => f.RelPath == file);
        if (unit is null)
        {
            return ReportError(kind, $"未知文件 {file}", a.Json, 1);
        }

        var offset = OffsetAt(unit.Text, line, column);
        if (offset is null)
        {
            return ReportError(kind, $"位置越界 {file}:{line}:{column}", a.Json, 1);
        }

        var hit = InnermostExprAt(module, unit.Sid, offset.Value);
        if (hit is null)
        {
            return ReportError(kind, "未命中表达式节点", a.Json, 1);
        }

        var (e, span) = hit.Value;
        var ty = TypeStrOf(module, module.Analysis.TypeOf(e));
        string? name = e is Expr.Ident { Name: var n } ? n : null;
        if (a.Json)
        {
            Out.PrintJsonSoft(new Out.TypeNodesEnvelope
            {
                Kind = kind,
                Nodes =
                [
                    new Out.TypeNodeDto
                    {
                        Kind = ExprKindName(e), Name = name, Type = ty, Span = SpanJson(span, module.Names),
                    },
                ],
            });
        }
        else
        {
            Console.WriteLine($"{ExprKindName(e)}\t{name ?? "-"}\t{ty}");
        }

        return 0;
    }

    // ———— effects ————

    private static int InspectEffects(Loader.CliModule module, string kind, bool asJson)
    {
        var solved = LangVerify.Solve(module.Merged, module.Analysis);
        if (solved.Error is not null)
        {
            return ReportError(kind, $"效果求解失败: {solved.Error.Message}", asJson, 1);
        }

        var entries = solved.Value!;
        if (asJson)
        {
            Out.PrintJsonSoft(new Out.EffectsEnvelope
            {
                Kind = kind,
                Effects = [.. entries.Select(e => new Out.EffectDto
                {
                    SymbolId = e.Id.Value,
                    Name = e.Name,
                    Effects = [.. e.Effects.Select(eff => eff.AsStr())],
                })],
            });
        }
        else
        {
            foreach (var e in entries)
            {
                Console.WriteLine($"{e.Name}\t{string.Join(",", e.Effects.Select(eff => eff.AsStr()))}");
            }

            Console.WriteLine($"{entries.Count} 个函数");
        }

        return 0;
    }

    // ———— callers / callees / impact ————

    /// <summary>calls/spawn 直接邻居（升序去重）。</summary>
    private static List<SymbolId> Neighbors(Lang.Graph.Graph graph, SymbolId id, bool outgoing)
    {
        IEnumerable<SymbolId> seq = outgoing
            ? graph.EdgesFrom(id)
                .Where(e => e.Kind is EdgeKind.Calls or EdgeKind.Spawn).Select(e => e.To)
            : graph.EdgesTo(id)
                .Where(e => e.Kind is EdgeKind.Calls or EdgeKind.Spawn).Select(e => e.From);
        return seq.Distinct().OrderBy(x => x.Value).ToList();
    }

    /// <summary>沿 calls/spawn 边的闭包（depth null = 不限；不含起点）。</summary>
    private static List<SymbolId> Closure(Lang.Graph.Graph graph, SymbolId start, bool outgoing, int? depth)
    {
        var seen = new SortedSet<uint>();
        var frontier = new List<SymbolId> { start };
        var d = 0;
        while (frontier.Count > 0 && (depth is null || d < depth))
        {
            var next = new List<SymbolId>();
            foreach (var id in frontier)
            {
                foreach (var t in Neighbors(graph, id, outgoing))
                {
                    if (t.Value != start.Value && seen.Add(t.Value))
                    {
                        next.Add(t);
                    }
                }
            }

            frontier = next;
            d++;
        }

        return seen.Select(v => new SymbolId(v)).ToList();
    }

    private static Out.GraphNodeDto NodeJson(Lang.Graph.Graph graph, SymbolId id)
    {
        var node = graph.Node(id);
        return node is null
            ? new Out.GraphNodeDto { SymbolId = id.Value }
            : new Out.GraphNodeDto { SymbolId = id.Value, Name = node.Name, Kind = node.Kind.AsStr() };
    }

    private static List<string> NamesOf(Lang.Graph.Graph graph, IEnumerable<SymbolId> ids) =>
        ids.Select(id => graph.Node(id)?.Name ?? "").ToList();

    /// <summary>rust `{:?}` 的 Vec&lt;String&gt; 文本形（调试输出）。</summary>
    private static string DebugList(List<string> xs) => $"[{string.Join(", ", xs.Select(x => $"\"{x}\""))}]";

    private static int InspectGraph(Loader.CliModule module, string sub, string kind, string? sym,
        int? depth, bool asJson)
    {
        Lang.Graph.Graph graph;
        try
        {
            graph = GraphFill.FromAnalysis(module.Merged, module.Analysis, sub);
        }
        catch (GraphException e)
        {
            return ReportError(kind, $"图构建失败: {e.Message}", asJson, 1);
        }

        var (target, err) = ResolveSymbol(module, sym);
        if (err is not null)
        {
            return ReportError(kind, err, asJson, 1);
        }

        switch (sub)
        {
            case "callers":
                {
                    var ids = Closure(graph, target!.Value, outgoing: false, depth);
                    if (asJson)
                    {
                        Out.PrintJsonSoft(new Out.CallersEnvelope
                        {
                            Kind = kind,
                            Callers = [.. ids.Select(id => NodeJson(graph, id))],
                        });
                    }
                    else
                    {
                        foreach (var n in NamesOf(graph, ids))
                        {
                            Console.WriteLine(n);
                        }

                        Console.WriteLine($"{ids.Count} 个调用方");
                    }

                    return 0;
                }

            case "callees":
                {
                    var ids = Closure(graph, target!.Value, outgoing: true, depth);
                    if (asJson)
                    {
                        Out.PrintJsonSoft(new Out.CalleesEnvelope
                        {
                            Kind = kind,
                            Callees = [.. ids.Select(id => NodeJson(graph, id))],
                        });
                    }
                    else
                    {
                        foreach (var n in NamesOf(graph, ids))
                        {
                            Console.WriteLine(n);
                        }

                        Console.WriteLine($"{ids.Count} 个被调方");
                    }

                    return 0;
                }

            default:
                {
                    var direct = Neighbors(graph, target!.Value, outgoing: false);
                    var transitive = Closure(graph, target.Value, outgoing: false, null);
                    if (asJson)
                    {
                        Out.PrintJsonSoft(new Out.ImpactEnvelope
                        {
                            Kind = kind,
                            Impact = new Out.ImpactDto
                            {
                                Symbol = NodeJson(graph, target.Value),
                                Direct = [.. direct.Select(id => NodeJson(graph, id))],
                                Transitive = [.. transitive.Select(id => NodeJson(graph, id))],
                            },
                        });
                    }
                    else
                    {
                        Console.WriteLine($"direct: {DebugList(NamesOf(graph, direct))}");
                        Console.WriteLine($"transitive: {DebugList(NamesOf(graph, transitive))}");
                    }

                    return 0;
                }
        }
    }
}
