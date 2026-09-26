using System.Globalization;
using System.Text;
using Lang.Core;
using Lang.Ir;

namespace Lang.BackendGo;

/// <summary>生成失败（内部错误：未知内建名等；非用户诊断）。</summary>
public sealed record GenError(string Message);

/// <summary>
/// Core IR → Go 源码生成器（单文件、<c>package main</c>、prelude 内联）。
/// 发射架构与 lang-rs <c>lang_backend_go::codegen</c>（集成轮修复后最新结论）同构：
/// <list type="bullet">
/// <item><b>值域</b>：全动态 <c>any</c>（IR 无类型表，TypeId 不可解）；每函数
/// <c>lf{n}_{名}(as ...any) any</c> 统一变参，捕获/参数槽 = <c>as[i]</c>、局部 = <c>l{i}</c>。</item>
/// <item><b>控制流</b>：label <c>L{k}</c> + 花括号块 + goto（只给被引用块发 label——Go 视
/// 未用 label 为编译错误）；每块编译为 Go 意义上的终结语句，无 missing-return。</item>
/// <item><b>操作数栈</b>：符号串栈内联（IR 无 Dup，每值至多消费一次）；Try/IterNext
/// 物化临时 <c>_t{n}</c>。has_ret=false 的调用按语句发射（lang-rs E2E 揪出的真 bug 教训）。</item>
/// <item><b>值语义</b>：用户 struct 以 <c>*S{n}_{名}</c> 指针承载，绑定/传参/返回/容器写入点
/// 显式 <c>langGoCopy</c>；ArrayPush 发射 <c>langGoPushArr</c>（拼接语义，B2 教训）、
/// RangeNew 操作数经 <c>langGoInt</c> 断言（B3 教训）、写链地址组件发射 Ref 读形
/// <c>langGoFieldRef/langGoIndexRef</c>（B1 牵出的写链缺口教训）。</item>
/// <item><b>确定性</b>：发射顺序 = 声明序/块序/指令序，无哈希序遍历；同 IR 两次生成
/// 逐字节相等（测试锁定）。</item>
/// </list>
/// </summary>
public static class CodeGen
{
    /// <summary>Core IR → 自包含可编译 Go 源码（<c>go build &lt;file&gt;</c> 直接可用）。
    /// 受控错误不抛异常：失败时 <paramref name="source"/> 为 null、<paramref name="error"/> 带因。</summary>
    public static bool TryGenerate(IrProgram prog, out string? source, out GenError? error)
    {
        var gen = new GoGen(prog);
        var text = gen.EmitProgram();
        error = gen.Error;
        source = error is null ? text : null;
        return error is null;
    }

    /// <summary>Go 关键字（字段发射名须避开）。</summary>
    private static readonly HashSet<string> GoKeywords = new()
    {
        "break", "case", "chan", "const", "continue", "default", "defer", "else",
        "fallthrough", "for", "func", "go", "goto", "if", "import", "interface",
        "map", "package", "range", "return", "select", "struct", "switch", "type", "var",
    };

    /// <summary>标识符消毒：非字母数字/下划线的码点 → <c>_</c>（按码点迭代，astral 不拆）。</summary>
    public static string SanitizeIdent(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var r in name.EnumerateRunes())
        {
            if (r.Value == '_' || IsAlphanumeric(r))
            {
                sb.Append(r.ToString());
            }
            else
            {
                sb.Append('_');
            }
        }

        return sb.ToString();
    }

    /// <summary>Unicode 字母/数字（对齐 rust char::is_alphanumeric：L* + Nd/Nl/No）。</summary>
    private static bool IsAlphanumeric(Rune r) => Rune.GetUnicodeCategory(r) switch
    {
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
        _ => false,
    };

    /// <summary>用户函数发射名：下标保证唯一，前缀避开 prelude/关键字。</summary>
    private static string FnName(int idx, string name) => $"lf{idx}_{SanitizeIdent(name)}";

    /// <summary>用户 struct 发射类型名。</summary>
    private static string StructTypeName(int idx, string name) => $"S{idx}_{SanitizeIdent(name)}";

    /// <summary>全局变量发射名。</summary>
    private static string GlobalName(int idx, string name) => $"g{idx}_{SanitizeIdent(name)}";

    /// <summary>struct 字段的 Go 字段名：消毒 + 关键字/数字开头加 <c>X</c> 前缀
    /// （动态访问经 <c>LangGoField(原名)</c> 接口，不依赖此形）。</summary>
    private static string GoFieldName(string name)
    {
        var s = SanitizeIdent(name);
        bool needsPrefix = s.Length == 0
            || (s[0] >= '0' && s[0] <= '9')
            || GoKeywords.Contains(s);
        return needsPrefix ? "X" + s : s;
    }

    /// <summary>float 常量的 Go 字面量（最短科学计数恒为合法 Go 浮点字面量；
    /// 特殊值经 math 包，规避无 <c>-0.0</c> 常量的语义歧义）。</summary>
    public static string GoFloatLit(double f)
    {
        if (double.IsNaN(f))
        {
            return "math.NaN()";
        }

        if (double.IsPositiveInfinity(f))
        {
            return "math.Inf(1)";
        }

        if (double.IsNegativeInfinity(f))
        {
            return "math.Inf(-1)";
        }

        if (f == 0.0)
        {
            return double.IsNegative(f) ? "math.Copysign(0, -1)" : "float64(0)";
        }

        var (digits, dp) = Fmt.ShortestDigits(Math.Abs(f));
        int exp = dp - 1;
        string mant = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "");
        string sign = double.IsNegative(f) ? "-" : "";
        return $"float64({sign}{mant}e{exp.ToString(CultureInfo.InvariantCulture)})";
    }

    /// <summary>函数内被任何跳转/分支/分派/迭代耗尽/Try 失败引用的块号集合。</summary>
    private static HashSet<int> ReferencedBlocks(IrFunction f)
    {
        var refs = new HashSet<int>();
        void Note(BlockId b) => refs.Add((int)b.Value);
        foreach (var block in f.Blocks)
        {
            foreach (var inst in block.Insts)
            {
                switch (inst)
                {
                    case Inst.IterNext it:
                        Note(it.Done);
                        break;
                    case Inst.Try t:
                        Note(t.Fail);
                        break;
                }
            }

            switch (block.Term)
            {
                case Terminator.Goto g:
                    Note(g.Target);
                    break;
                case Terminator.Branch br:
                    Note(br.IfTrue);
                    Note(br.IfFalse);
                    break;
                case Terminator.SwitchInt si:
                    foreach (var (_, target) in si.Arms)
                    {
                        Note(target);
                    }

                    if (si.Default is { } sid)
                    {
                        Note(sid);
                    }

                    break;
                case Terminator.SwitchEnum se:
                    foreach (var target in se.Arms)
                    {
                        Note(target);
                    }

                    if (se.Default is { } sed)
                    {
                        Note(sed);
                    }

                    break;
                case Terminator.Return:
                case Terminator.Unreachable:
                    break;
            }
        }

        return refs;
    }

    /// <summary>
    /// 标记作为赋值写链地址组件的 <see cref="Inst.FieldGet"/>/<see cref="Inst.IndexGet"/>
    /// （发射为 Ref 读形——不落副本，最终 set 在底层存储上写穿；与机器 Place 溯源
    /// 操作数同语义。普通读保持「读出皆副本」VALUE-ELEM-009）。
    /// 链形（lowering 约定，块内连续指令）：<c>[LocalGet|GlobalGet,
    /// (FieldGet | LocalGet + IndexGet)*]</c> 后随 <c>(LocalGet(val),)</c> +
    /// <c>FieldSet|IndexSet|ArrayPush</c>。回溯不匹配链形时什么都不标（保守回退
    /// 普通读形，hand-built IR 安全）。
    /// </summary>
    private static HashSet<int> MarkRefReads(IReadOnlyList<Inst> insts)
    {
        var marked = new HashSet<int>();
        for (int i = 0; i < insts.Count; i++)
        {
            // obj 操作数产生者相对 set 指令的位置（跳过 val（与 idx）的 LocalGet）
            int? objAt = insts[i] switch
            {
                Inst.FieldSet or Inst.ArrayPush => i - 2,
                Inst.IndexSet => i - 3,
                _ => null,
            };
            if (objAt is not { } j || j < 0)
            {
                continue;
            }

            while (true)
            {
                switch (InstAt(insts, j))
                {
                    case Inst.FieldGet:
                        marked.Add(j);
                        j -= 1;
                        break;
                    case Inst.IndexGet:
                        marked.Add(j);
                        // idx 产生者 = 紧邻的 LocalGet(idx_temp)
                        if (j >= 1 && InstAt(insts, j - 1) is Inst.LocalGet)
                        {
                            j -= 2;
                            break;
                        }

                        return marked;
                    default:
                        return marked; // 根（LocalGet/GlobalGet）= 链头，停
                }

                if (j < 0)
                {
                    break;
                }
            }
        }

        return marked;
    }

    private static Inst? InstAt(IReadOnlyList<Inst> insts, int i) =>
        i >= 0 && i < insts.Count ? insts[i] : null;

    /// <summary>发射器主体（每程序一个；确定性由发射顺序保证）。</summary>
    private sealed class GoGen(IrProgram prog)
    {
        private readonly IrProgram _prog = prog;
        private readonly StringBuilder _out = new();
        private readonly List<string> _stack = new();
        private int _indent;
        private int _tmp;
        private int _fixedSlots;
        private bool _spawns;
        private HashSet<int> _refReads = [];

        /// <summary>首个生成错误（null = 成功）。</summary>
        public GenError? Error { get; private set; }

        private void Fail(string msg) => Error ??= new GenError(msg);

        private void W(string line)
        {
            for (int i = 0; i < _indent; i++)
            {
                _out.Append('\t');
            }

            _out.Append(line).Append('\n');
        }

        private string NewTmp()
        {
            _tmp++;
            return $"_t{_tmp}";
        }

        private string NewIterTmp()
        {
            _tmp++;
            return $"_it{_tmp}";
        }

        private void Push(string expr) => _stack.Add(expr);

        private string Pop()
        {
            int last = _stack.Count - 1;
            if (last < 0)
            {
                return "panic(\"栈下溢\")";
            }

            var v = _stack[last];
            _stack.RemoveAt(last);
            return v;
        }

        /// <summary>弹 <paramref name="n"/> 个并按<b>压入序</b>返回（弹序逆序）。</summary>
        private List<string> PopN(int n)
        {
            var outp = new List<string>(new string[n]);
            for (int i = n - 1; i >= 0; i--)
            {
                outp[i] = Pop();
            }

            return outp;
        }

        /// <summary>局部槽位引用：捕获/参数槽 = <c>as[i]</c>（闭包调用约定把 Env 前置），
        /// 普通局部 = 预声明变量 <c>l{i}</c>。</summary>
        private string SlotRef(LocalId id)
        {
            int i = (int)id.Value;
            return i < _fixedSlots ? $"as[{i}]" : $"l{i}";
        }

        // ———— 程序 ————

        public string EmitProgram()
        {
            _out.Append(Prelude.GoPrelude.TrimEnd('\n'));
            _out.Append('\n');
            for (int i = 0; i < _prog.Structs.Count; i++)
            {
                EmitStruct(i, _prog.Structs[i]);
            }

            for (int i = 0; i < _prog.Globals.Count; i++)
            {
                W($"var {GlobalName(i, _prog.Globals[i].Name)} any");
            }

            for (int i = 0; i < _prog.Functions.Count; i++)
            {
                EmitFunction(i, _prog.Functions[i]);
            }

            EmitMain();
            return _out.ToString();
        }

        private void EmitStruct(int idx, StructDecl s)
        {
            var gname = StructTypeName(idx, s.Name);
            W($"type {gname} struct {{");
            _indent++;
            foreach (var f in s.Fields)
            {
                W($"{GoFieldName(f.Name)} any");
            }

            _indent--;
            W("}");
            W("");

            // 值语义拷贝（langGoCopy 的 struct 分派点）
            W($"func (v *{gname}) langGoClone() any {{");
            _indent++;
            W("n := *v");
            foreach (var f in s.Fields)
            {
                var fname = GoFieldName(f.Name);
                W($"n.{fname} = langGoCopy(v.{fname})");
            }

            W("return &n");
            _indent--;
            W("}");

            // 打印（镜像解释器 fmtVal struct 分支：声明序、嵌套形）
            var parts = s.Fields
                .Select(f => $"{Fmt.QuoteGo(f.Name)} + \": \" + langGoFmt(v.{GoFieldName(f.Name)}, false)")
                .ToList();
            W($"func (v *{gname}) langGoString(top bool) string {{");
            _indent++;
            W($"return {Fmt.QuoteGo(s.Name)} + \"{{\" + strings.Join([]string{{{string.Join(", ", parts)}}}, \", \") + \"}}\"");
            _indent--;
            W("}");

            // 动态字段读写（FieldGet/FieldSet 经接口分派）
            W($"func (v *{gname}) LangGoField(name string) any {{");
            _indent++;
            W("switch name {");
            foreach (var f in s.Fields)
            {
                W($"case {Fmt.QuoteGo(f.Name)}:");
                _indent++;
                W($"return v.{GoFieldName(f.Name)}");
                _indent--;
            }

            W("}");
            W($"panic({Fmt.QuoteGo(s.Name)} + \" 没有字段 \" + name)");
            _indent--;
            W("}");

            W($"func (v *{gname}) LangGoSetField(name string, val any) {{");
            _indent++;
            W("switch name {");
            foreach (var f in s.Fields)
            {
                var fname = GoFieldName(f.Name);
                W($"case {Fmt.QuoteGo(f.Name)}:");
                _indent++;
                W($"v.{fname} = langGoCopy(val)");
                W("return");
                _indent--;
            }

            W("}");
            W($"panic({Fmt.QuoteGo(s.Name)} + \" 没有字段 \" + name)");
            _indent--;
            W("}");

            W($"func (v *{gname}) LangGoTypeName() string {{ return {Fmt.QuoteGo(s.Name)} }}");
            W("");
        }

        private void EmitFunction(int idx, IrFunction f)
        {
            _tmp = 0;
            _spawns = false;
            _fixedSlots = f.FixedSlots;
            var name = FnName(idx, f.Name);
            if (f.ExternHost is not null)
            {
                // extern 宿主函数：独立产物无宿主可接，执行到即内部错误
                W($"// extern 宿主函数 {Fmt.QuoteGo(f.Name)}（{f.ExternHost}）：独立产物未链接宿主实现");
                W($"func {name}(as ...any) any {{");
                _indent++;
                var host = f.ExternHost.Replace("\\", "\\\\").Replace("\"", "\\\"");
                W($"panic(\"extern 宿主函数未链接: {host}\")");
                _indent--;
                W("}");
                W("");
                return;
            }

            W($"func {name}(as ...any) any {{");
            _indent++;
            for (int i = _fixedSlots; i < f.Locals.Count; i++)
            {
                W($"var l{i} any");
                W($"_ = l{i}");
            }

            // 只给被跳转引用的块发 label（入口块与死块无 label；
            // Go 把「label defined and not used」当编译错误）
            var referenced = ReferencedBlocks(f);
            for (int bi = 0; bi < f.Blocks.Count; bi++)
            {
                var block = f.Blocks[bi];
                if (referenced.Contains(bi))
                {
                    W($"L{bi}:");
                }

                W("{");
                _indent++;
                _stack.Clear();
                _refReads = MarkRefReads(block.Insts);
                for (int ii = 0; ii < block.Insts.Count; ii++)
                {
                    EmitInst(ii, block.Insts[ii]);
                    if (Error is not null)
                    {
                        return;
                    }
                }

                EmitTerminator(block.Term);
                _indent--;
                W("}");
            }

            _indent--;
            W("}");
            W("");
        }

        private void EmitMain()
        {
            W("func main() {");
            _indent++;
            if (_prog.Init is { } init)
            {
                W($"{FnName((int)init.Value, _prog.Functions[(int)init.Value].Name)}()");
            }

            if (_prog.Main is { } main)
            {
                W($"{FnName((int)main.Value, _prog.Functions[(int)main.Value].Name)}()");
            }

            W("langGoWaitAll()");
            _indent--;
            W("}");
        }

        // ———— 指令 ————

        private void EmitInst(int pos, Inst inst)
        {
            switch (inst)
            {
                case Inst.ConstI64 c:
                    Push($"int64({c.Value.ToString(CultureInfo.InvariantCulture)})");
                    break;
                case Inst.ConstF64 c:
                    Push(GoFloatLit(c.Value));
                    break;
                case Inst.ConstBool c:
                    Push(c.Value ? "true" : "false");
                    break;
                case Inst.ConstStr c:
                    Push(Fmt.QuoteGo(c.Value));
                    break;

                case Inst.LocalGet g:
                    Push(SlotRef(g.Local));
                    break;
                case Inst.LocalSet s:
                    {
                        var v = Pop();
                        var r = SlotRef(s.Local);
                        W($"{r} = langGoCopy({v})");
                        break;
                    }

                case Inst.GlobalGet g:
                    Push(GlobalName((int)g.Global.Value, _prog.Globals[(int)g.Global.Value].Name));
                    break;
                case Inst.GlobalSet s:
                    {
                        var v = Pop();
                        var name = GlobalName((int)s.Global.Value, _prog.Globals[(int)s.Global.Value].Name);
                        W($"{name} = langGoCopy({v})");
                        break;
                    }

                case Inst.Unary u:
                    {
                        var v = Pop();
                        Push(u.Op switch
                        {
                            Unop.Neg => $"langGoNeg({v})",
                            Unop.Not => $"langGoNot({v})",
                            _ => throw new InvalidOperationException("Unop 变体未穷尽"),
                        });
                        break;
                    }

                case Inst.Binary b:
                    {
                        var r = Pop();
                        var l = Pop();
                        Push(EmitBinop(b.Op, l, r));
                        break;
                    }

                case Inst.Pop:
                    W($"_ = {Pop()}");
                    break;

                case Inst.Call call:
                    {
                        var target = _prog.Functions[(int)call.Func.Value];
                        var args = PopN(target.Sig.ParamCount);
                        var name = FnName((int)call.Func.Value, target.Name);
                        var text = $"{name}({string.Join(", ", args)})";
                        // out = 目标 has_ret：有值压栈，无值为语句（调用本身即效果）
                        if (target.Sig.HasRet)
                        {
                            Push(text);
                        }
                        else
                        {
                            W(text);
                        }

                        break;
                    }

                case Inst.CallIndirect ci:
                    {
                        var args = PopN(ci.Argc);
                        var callee = Pop();
                        var text = $"langGoCallClosure({callee}{PrependArgs(args)})";
                        if (ci.HasRet)
                        {
                            Push(text);
                        }
                        else
                        {
                            W(text);
                        }

                        break;
                    }

                case Inst.CallBuiltin cb:
                    {
                        var args = PopN(cb.Argc);
                        if (!TryEmitBuiltin(cb.Name, args, out var text))
                        {
                            Fail(text!);
                            return;
                        }

                        if (cb.HasRet)
                        {
                            Push(text!);
                        }
                        else
                        {
                            W(text!);
                        }

                        break;
                    }

                case Inst.Spawn sp:
                    {
                        var args = PopN(sp.Argc);
                        _spawns = true;
                        var target = _prog.Functions[(int)sp.Func.Value];
                        var name = FnName((int)sp.Func.Value, target.Name);
                        W($"langGoGo(func(sa ...any) {{ {name}(sa...) }}, []any{{{string.Join(", ", args)}}})");
                        break;
                    }

                case Inst.ClosureNew cl:
                    {
                        var caps = PopN(cl.Captures);
                        var env = caps.Count == 0 ? "nil" : $"[]any{{{string.Join(", ", caps)}}}";
                        var tf = _prog.Functions[(int)cl.Func.Value];
                        Push($"&langGoClosure{{Fn: {FnName((int)cl.Func.Value, tf.Name)}, Env: {env}, Name: {Fmt.QuoteGo(tf.Name)}}}");
                        break;
                    }

                case Inst.IndexGet:
                    {
                        var idx = Pop();
                        var obj = Pop();
                        if (_refReads.Contains(pos))
                        {
                            // 写链地址组件：Ref 读形（不落副本，最终 set 写穿）
                            Push($"langGoIndexRef({obj}, {idx})");
                        }
                        else
                        {
                            Push($"langGoIndex({obj}, {idx})");
                        }

                        break;
                    }

                case Inst.IndexSet:
                    {
                        var val = Pop();
                        var idx = Pop();
                        var obj = Pop();
                        W($"langGoSetIndex({obj}, {idx}, {val})");
                        break;
                    }

                case Inst.SliceGet:
                    {
                        var hi = Pop();
                        var lo = Pop();
                        var obj = Pop();
                        Push($"langGoSlice({obj}, {lo}, {hi})");
                        break;
                    }

                case Inst.FieldGet fg:
                    {
                        var obj = Pop();
                        if (_refReads.Contains(pos))
                        {
                            // 写链地址组件：Ref 读形（不落副本，最终 set 写穿）
                            Push($"langGoFieldRef({obj}, {Fmt.QuoteGo(fg.Field)})");
                        }
                        else
                        {
                            Push($"langGoField({obj}, {Fmt.QuoteGo(fg.Field)})");
                        }

                        break;
                    }

                case Inst.FieldSet fs:
                    {
                        var val = Pop();
                        var obj = Pop();
                        W($"langGoSetField({obj}, {Fmt.QuoteGo(fs.Field)}, {val})");
                        break;
                    }

                case Inst.ArrayPush:
                    // 数组 +=：rhs 为数组 → 原位拼接（IrMachine ArrayPush 同语义；
                    // 单元素追加是 push 内建，走 CallBuiltin langGoPush）
                    {
                        var val = Pop();
                        var arr = Pop();
                        W($"langGoPushArr({arr}, {val})");
                        break;
                    }

                case Inst.ArrayNew an:
                    {
                        var items = PopN(an.Count);
                        Push(items.Count == 0
                            ? "&LangGoArr{}"
                            : $"&LangGoArr{{Items: []any{{{string.Join(", ", items)}}}}}");
                        break;
                    }

                case Inst.MapNew mn:
                    {
                        var flat = PopN(mn.Count * 2);
                        var kvs = flat.Chunk(2)
                            .Select(p => $"langGoKV{{{p[0]}, {p[1]}}}")
                            .ToList();
                        Push($"langGoNewMap({string.Join(", ", kvs)})");
                        break;
                    }

                case Inst.StructNew sn:
                    {
                        var decl = _prog.Structs[(int)sn.Struct.Value];
                        var fields = PopN(decl.Fields.Count);
                        var inits = decl.Fields.Zip(fields)
                            .Select(pair => $"{GoFieldName(pair.First.Name)}: {pair.Second}")
                            .ToList();
                        Push($"&{StructTypeName((int)sn.Struct.Value, decl.Name)}{{{string.Join(", ", inits)}}}");
                        break;
                    }

                case Inst.EnumNew en:
                    {
                        var decl = _prog.Enums[(int)en.Enum.Value];
                        var kase = decl.Cases[en.Case];
                        var payloads = PopN(kase.PayloadTypes.Count);
                        Push($"langGoEnumVal({Fmt.QuoteGo(decl.Name)}, {Fmt.QuoteGo(kase.Name)}, {string.Join(", ", payloads)})");
                        break;
                    }

                case Inst.RangeNew:
                    // 操作数经临时槽（any）入 struct 字面量须断言 int64
                    //（对齐 SwitchInt 等整型操作数的 langGoInt 处理；lang-rs B3 教训）
                    {
                        var hi = Pop();
                        var lo = Pop();
                        Push($"langGoRange{{Lo: langGoInt({lo}), Hi: langGoInt({hi})}}");
                        break;
                    }

                case Inst.BoxNew:
                    Push($"langGoBox({Pop()})");
                    break;
                case Inst.Deref:
                    Push($"langGoDeref({Pop()})");
                    break;
                case Inst.RefSet:
                    {
                        var val = Pop();
                        var r = Pop();
                        W($"langGoRefSet({r}, {val})");
                        break;
                    }

                case Inst.EnumPayloadGet ep:
                    Push($"langGoEnumPayload({Pop()}, {ep.Index.ToString(CultureInfo.InvariantCulture)})");
                    break;

                case Inst.IterNew:
                    Push($"langGoIterNew({Pop()})");
                    break;
                case Inst.IterNext itn:
                    {
                        var v = Pop();
                        var it = NewIterTmp();
                        W($"{it} := {v}.(*langGoIter)");
                        W($"if {it}.I >= int64(len({it}.Items)) {{");
                        _indent++;
                        W($"goto L{(int)itn.Done.Value}");
                        _indent--;
                        W("}");
                        var e = NewTmp();
                        W($"{e} := {it}.Items[{it}.I]");
                        W($"{it}.I++");
                        Push($"langGoCopy({e})");
                        break;
                    }

                case Inst.ChanNew:
                    Push("langGoNewChan()");
                    break;
                case Inst.ChanSend:
                    {
                        var val = Pop();
                        var ch = Pop();
                        W($"langGoSend({ch}, {val})");
                        break;
                    }

                case Inst.ChanRecv:
                    Push($"langGoRecv({Pop()})");
                    break;

                case Inst.Try t:
                    {
                        var v = Pop();
                        var tmp = NewTmp();
                        W($"{tmp} := {v}");
                        W($"if langGoIsErr({tmp}) {{");
                        _indent++;
                        var slot = SlotRef(t.Err);
                        W($"{slot} = langGoTryErrOf({tmp})");
                        W($"goto L{(int)t.Fail.Value}");
                        _indent--;
                        W("}");
                        Push($"langGoTryOk({tmp})");
                        break;
                    }

                default:
                    throw new InvalidOperationException("Inst 变体未穷尽（内部不变量）");
            }
        }

        // ———— 终接 ————

        private void EmitTerminator(Terminator term)
        {
            switch (term)
            {
                case Terminator.Goto g:
                    W($"goto L{(int)g.Target.Value}");
                    break;
                case Terminator.Branch br:
                    {
                        var c = Pop();
                        if (br.IfTrue.Equals(br.IfFalse))
                        {
                            W($"goto L{(int)br.IfTrue.Value}");
                        }
                        else
                        {
                            W($"if langGoTruthy({c}) {{");
                            _indent++;
                            W($"goto L{(int)br.IfTrue.Value}");
                            _indent--;
                            W("}");
                            W($"goto L{(int)br.IfFalse.Value}");
                        }

                        break;
                    }

                case Terminator.SwitchInt si:
                    {
                        var v = Pop();
                        W($"switch langGoInt({v}) {{");
                        foreach (var (val, target) in si.Arms)
                        {
                            W($"case {val.ToString(CultureInfo.InvariantCulture)}:");
                            _indent++;
                            W($"goto L{(int)target.Value}");
                            _indent--;
                        }

                        EmitSwitchTail(si.Default);
                        W("}");
                        break;
                    }

                case Terminator.SwitchEnum se:
                    {
                        var v = Pop();
                        var decl = _prog.Enums[(int)se.Enum.Value];
                        W($"switch langGoEnumTag({v}) {{");
                        for (int i = 0; i < decl.Cases.Count && i < se.Arms.Count; i++)
                        {
                            W($"case {Fmt.QuoteGo(decl.Cases[i].Name)}:");
                            _indent++;
                            W($"goto L{(int)se.Arms[i].Value}");
                            _indent--;
                        }

                        EmitSwitchTail(se.Default);
                        W("}");
                        break;
                    }

                case Terminator.Return ret:
                    // 函数边界 join：含 go 语句的函数每个返回点先等自身 Task（TASK-DETERMINISM-003）
                    if (_spawns)
                    {
                        W("langGoWaitAll()");
                    }

                    if (ret.HasValue)
                    {
                        W($"return langGoCopy({Pop()})");
                    }
                    else
                    {
                        W("return nil");
                    }

                    break;
                case Terminator.Unreachable:
                    W("panic(\"不可达\")");
                    break;
                default:
                    throw new InvalidOperationException("Terminator 变体未穷尽（内部不变量）");
            }
        }

        /// <summary>分派 switch 的收尾：default 或运行时无臂命中错误（RUNTIME-MATCH-001）。</summary>
        private void EmitSwitchTail(BlockId? def)
        {
            W("default:");
            _indent++;
            W(def is { } d ? $"goto L{(int)d.Value}" : "panic(\"match 无匹配分支\")");
            _indent--;
        }
    }

    /// <summary>调用实参拼接：<c>, a, b</c>（无实参为空串）。</summary>
    private static string PrependArgs(List<string> args) =>
        args.Count == 0 ? "" : ", " + string.Join(", ", args);

    private static string EmitBinop(Binop op, string l, string r) => op switch
    {
        Binop.Eq => $"langGoEq({l}, {r})",
        Binop.Ne => $"!langGoEq({l}, {r})",
        Binop.Lt => $"langGoCompare({l}, {r}) < 0",
        Binop.Le => $"langGoCompare({l}, {r}) <= 0",
        Binop.Gt => $"langGoCompare({l}, {r}) > 0",
        Binop.Ge => $"langGoCompare({l}, {r}) >= 0",
        Binop.Add => $"langGoArith(\"+\", {l}, {r})",
        Binop.Sub => $"langGoArith(\"-\", {l}, {r})",
        Binop.Mul => $"langGoArith(\"*\", {l}, {r})",
        Binop.Div => $"langGoArith(\"/\", {l}, {r})",
        Binop.Rem => $"langGoArith(\"%\", {l}, {r})",
        Binop.BitAnd => $"langGoBits(\"&\", {l}, {r})",
        Binop.BitOr => $"langGoBits(\"|\", {l}, {r})",
        Binop.BitXor => $"langGoBits(\"^\", {l}, {r})",
        Binop.Shl => $"langGoBits(\"<<\", {l}, {r})",
        Binop.Shr => $"langGoBits(\">>\", {l}, {r})",
        _ => throw new InvalidOperationException("Binop 变体未穷尽（内部不变量）"),
    };

    /// <summary>内建名 → prelude 运行时调用（实参个数由 IR argc 保证，未知名 = 内部错误）。</summary>
    private static bool TryEmitBuiltin(string name, List<string> args, out string? call)
    {
        var join = string.Join(", ", args);
        call = name switch
        {
            "println" => $"langGoPrintln({join})",
            "print" => $"langGoPrint({join})",
            "len" => $"langGoLen({join})",
            "str" => $"langGoStr({join})",
            "int" => $"langGoInt({join})",
            "float" => $"langGoFloat({join})",
            "keys" => $"langGoKeys({join})",
            "values" => $"langGoValues({join})",
            "has" => $"langGoHas({join})",
            "push" => $"langGoPush({join})",
            "assert" => $"langGoAssert({join})",
            "err" => $"langGoErr({join})",
            "bytes" => $"langGoBytes({join})",
            "char" => $"langGoChar({join})",
            "jsonParse" => $"langGoJSONParse({join})",
            "jsonStringify" => $"langGoJSONStringify({join})",
            "isNone" => $"langGoIsNone({join})",
            _ => null,
        };
        if (call is null)
        {
            call = $"未知内建 {name}";
            return false;
        }

        return true;
    }
}
