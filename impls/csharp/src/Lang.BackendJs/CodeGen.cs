using System.Globalization;
using System.Text;
using Lang.Core;
using Lang.Ir;

namespace Lang.BackendJs;

/// <summary>生成失败（内部错误：未知内建名等；非用户诊断）。</summary>
public sealed record GenError(string Message);

/// <summary>
/// Core IR → JS ESM 生成器（<c>.mjs</c> 形态、prelude 内联、无外部 import）。
/// 发射架构与 lang-rs <c>lang_backend_js::codegen</c>（集成轮修复后最新结论）同构：
/// <list type="bullet">
/// <item><b>函数</b>：每 IR 函数发射 <c>function lf{n}_{名}(...as)</c>；捕获/参数槽 =
/// <c>as[i]</c>，普通局部 = 函数顶部 <c>var l{i} = undefined;</c>（JS var 函数作用域）。</item>
/// <item><b>基本块 → 状态机</b>：JS 无 goto，CFG 发射为 <c>let __pc</c> +
/// <c>while(true) switch(__pc)</c>：每块 = 一个 <c>case k: { ... }</c>，终接改写
/// <c>__pc</c> 后 <c>break</c>（break 绑定 switch，即块间跳转）。</item>
/// <item><b>同步确定子集</b>：spawn = 直接调用（实参逐个 <c>langGoCopy</c> 快照，
/// TASK-SPAWN-002）；chan = 数组队列（CHAN-MAP-005）；映射键类型前缀防撞键
/// （int 1 与 string "1" 不撞——lang-rs 修过的参考实现 bug 直接规避）。</item>
/// <item><b>写链地址组件</b>发射 Ref 读形 <c>langGoFieldRef/langGoIndexRef</c>
/// （B1 牵出的写链缺口教训）；ArrayPush 发射 <c>langGoPushArr</c>（拼接语义，B2 教训）。</item>
/// <item><b>确定性</b>：同 IR 两次生成逐字节相等（测试锁定）。</item>
/// </list>
/// </summary>
public static class CodeGen
{
    /// <summary>Core IR → 自包含可执行 ES Module（<c>node &lt;file&gt;.mjs</c> 直接可用）。
    /// 受控错误不抛异常：失败时 <paramref name="source"/> 为 null、<paramref name="error"/> 带因。</summary>
    public static bool TryGenerate(IrProgram prog, out string? source, out GenError? error)
    {
        var gen = new JsGen(prog);
        var text = gen.EmitProgram();
        error = gen.Error;
        source = error is null ? text : null;
        return error is null;
    }

    /// <summary>标识符消毒（同 →Go 规则：按码点迭代，非字母数字/下划线 → <c>_</c>）。
    /// 两程序集同构不共享（沿 lang-rs 决策），此为 Go 侧同形副本。</summary>
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

    private static string FnName(int idx, string name) => $"lf{idx}_{SanitizeIdent(name)}";

    private static string GlobalName(int idx, string name) => $"g{idx}_{SanitizeIdent(name)}";

    /// <summary>JS 源码字符串字面量（JSON 转义面，无 HTML 转义——源码字面量解析后内容不变）。</summary>
    public static string JsStrLit(string s)
    {
        var outp = new StringBuilder(s.Length + 2);
        outp.Append('"');
        int i = 0;
        while (i < s.Length)
        {
            int cp = CodePointAt(s, i, out int size);
            i += size;
            switch (cp)
            {
                case 0x22: outp.Append("\\\""); break;
                case 0x5c: outp.Append("\\\\"); break;
                case 0x08: outp.Append("\\b"); break;
                case 0x0c: outp.Append("\\f"); break;
                case 0x0a: outp.Append("\\n"); break;
                case 0x0d: outp.Append("\\r"); break;
                case 0x09: outp.Append("\\t"); break;
                default:
                    if (cp < 0x20)
                    {
                        outp.Append("\\u").Append(cp.ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (size == 2)
                    {
                        // astral 码点：原样追加代理对两半
                        outp.Append(s[i - 2]).Append(s[i - 1]);
                    }
                    else
                    {
                        outp.Append((char)cp);
                    }

                    break;
            }
        }

        outp.Append('"');
        return outp.ToString();
    }

    /// <summary>s[i] 起的码点：代理对合成 astral 码点，否则取单个 char（与 Go 侧 Fmt 同形副本）。</summary>
    private static int CodePointAt(string s, int i, out int size)
    {
        char c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            size = 2;
            return 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00);
        }

        size = 1;
        return c;
    }

    /// <summary>
    /// 最短舍入数字提取（与 Go 侧 Fmt.ShortestDigits 同形副本；返回无符号数字串与
    /// 小数点位置 dp，满足 <c>value = 0.digits × 10^dp</c>）。
    /// </summary>
    private static (string Digits, int Dp) ShortestDigits(double a)
    {
        string s = a.ToString(CultureInfo.InvariantCulture);
        string digits;
        int dp;
        int eIdx = s.IndexOf('E');
        if (eIdx >= 0)
        {
            // C# E 形尾数恒为「点前恰一位」：value = 0.digits × 10^(exp+1)
            string mant = s[..eIdx];
            int exp = int.Parse(s[(eIdx + 1)..], CultureInfo.InvariantCulture);
            int dot = mant.IndexOf('.');
            digits = dot >= 0 ? mant.Remove(dot, 1) : mant;
            dp = exp + 1;
        }
        else
        {
            int dot = s.IndexOf('.');
            if (dot >= 0)
            {
                digits = s.Remove(dot, 1);
                dp = dot;
            }
            else
            {
                digits = s;
                dp = s.Length;
            }
        }

        int last = digits.Length;
        while (last > 1 && digits[last - 1] == '0')
        {
            last--;
        }

        digits = digits[..last];
        int start = 0;
        while (start < digits.Length - 1 && digits[start] == '0')
        {
            start++;
        }

        if (start > 0)
        {
            digits = digits[start..];
            dp -= start;
        }

        return (digits, dp);
    }

    /// <summary>float 常量的 JS 字面量（最短科学计数恒为合法 JS 数值字面量）。</summary>
    public static string JsFloatLit(double f)
    {
        if (double.IsNaN(f))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(f))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(f))
        {
            return "-Infinity";
        }

        if (f == 0.0)
        {
            return double.IsNegative(f) ? "-0" : "0";
        }

        var (digits, dp) = ShortestDigits(Math.Abs(f));
        int exp = dp - 1;
        string mant = digits[..1] + (digits.Length > 1 ? "." + digits[1..] : "");
        string sign = double.IsNegative(f) ? "-" : "";
        return $"{sign}{mant}e{exp.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// 标记作为赋值写链地址组件的 <see cref="Inst.FieldGet"/>/<see cref="Inst.IndexGet"/>
    /// （发射为 Ref 读形——不落副本，最终 set 在底层存储上写穿；与机器 Place 溯源
    /// 操作数同语义。普通读保持「读出皆副本」VALUE-ELEM-009）。回溯不匹配链形时
    /// 什么都不标（保守回退普通读形，hand-built IR 安全）。与 Go 侧同形副本。
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
                Inst? cur = j >= 0 && j < insts.Count ? insts[j] : null;
                switch (cur)
                {
                    case Inst.FieldGet:
                        marked.Add(j);
                        j -= 1;
                        break;
                    case Inst.IndexGet:
                        marked.Add(j);
                        // idx 产生者 = 紧邻的 LocalGet(idx_temp)
                        if (j >= 1 && insts[j - 1] is Inst.LocalGet)
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

    /// <summary>发射器主体（每程序一个；确定性由发射顺序保证）。</summary>
    private sealed class JsGen(IrProgram prog)
    {
        private readonly IrProgram _prog = prog;
        private readonly StringBuilder _out = new();
        private readonly List<string> _stack = new();
        private int _indent;
        private int _tmp;
        private int _fixedSlots;
        private HashSet<int> _refReads = [];

        /// <summary>首个生成错误（null = 成功）。</summary>
        public GenError? Error { get; private set; }

        private void Fail(string msg) => Error ??= new GenError(msg);

        private void W(string line)
        {
            for (int i = 0; i < _indent; i++)
            {
                _out.Append("  ");
            }

            _out.Append(line).Append('\n');
        }

        private string NewTmp()
        {
            _tmp++;
            return $"_t{_tmp}";
        }

        private void Push(string expr) => _stack.Add(expr);

        private string Pop()
        {
            int last = _stack.Count - 1;
            if (last < 0)
            {
                return "/* 栈下溢 */ undefined";
            }

            var v = _stack[last];
            _stack.RemoveAt(last);
            return v;
        }

        private List<string> PopN(int n)
        {
            var outp = new List<string>(new string[n]);
            for (int i = n - 1; i >= 0; i--)
            {
                outp[i] = Pop();
            }

            return outp;
        }

        private string SlotRef(LocalId id)
        {
            int i = (int)id.Value;
            return i < _fixedSlots ? $"as[{i}]" : $"l{i}";
        }

        // ———— 程序 ————

        public string EmitProgram()
        {
            _out.Append(Prelude.JsPrelude.TrimEnd('\n'));
            _out.Append('\n');
            for (int i = 0; i < _prog.Globals.Count; i++)
            {
                W($"var {GlobalName(i, _prog.Globals[i].Name)} = undefined;");
            }

            if (_prog.Globals.Count > 0)
            {
                W("");
            }

            for (int i = 0; i < _prog.Functions.Count; i++)
            {
                EmitFunction(i, _prog.Functions[i]);
                if (Error is not null)
                {
                    return _out.ToString();
                }
            }

            W("// —— 程序入口 ——");
            if (_prog.Init is { } init)
            {
                W($"{FnName((int)init.Value, _prog.Functions[(int)init.Value].Name)}();");
            }

            if (_prog.Main is { } main)
            {
                W($"{FnName((int)main.Value, _prog.Functions[(int)main.Value].Name)}();");
            }

            return _out.ToString();
        }

        private void EmitFunction(int idx, IrFunction f)
        {
            _tmp = 0;
            _fixedSlots = f.FixedSlots;
            var name = FnName(idx, f.Name);
            if (f.ExternHost is not null)
            {
                W($"// extern 宿主函数 {JsStrLit(f.Name)}：独立产物未链接宿主实现");
                W($"function {name}(...as) {{");
                _indent++;
                var host = f.ExternHost.Replace("\\", "\\\\").Replace("\"", "\\\"");
                W($"throw new Error(\"extern 宿主函数未链接: {host}\");");
                _indent--;
                W("}");
                W("");
                return;
            }

            W($"function {name}(...as) {{");
            _indent++;
            for (int i = _fixedSlots; i < f.Locals.Count; i++)
            {
                W($"var l{i} = undefined;");
            }

            W("let __pc = 0;");
            W("while (true) {");
            _indent++;
            W("switch (__pc) {");
            _indent++;
            for (int bi = 0; bi < f.Blocks.Count; bi++)
            {
                var block = f.Blocks[bi];
                W($"case {bi.ToString(CultureInfo.InvariantCulture)}: {{");
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
            _indent--;
            W("}");
            _indent--;
            W("}");
            W("");
        }

        // ———— 指令 ————

        private void EmitInst(int pos, Inst inst)
        {
            switch (inst)
            {
                case Inst.ConstI64 c:
                    Push(c.Value < 0
                        ? $"({c.Value.ToString(CultureInfo.InvariantCulture)}n)"
                        : $"{c.Value.ToString(CultureInfo.InvariantCulture)}n");
                    break;
                case Inst.ConstF64 c:
                    Push(JsFloatLit(c.Value));
                    break;
                case Inst.ConstBool c:
                    Push(c.Value ? "true" : "false");
                    break;
                case Inst.ConstStr c:
                    Push(JsStrLit(c.Value));
                    break;

                case Inst.LocalGet g:
                    Push(SlotRef(g.Local));
                    break;
                case Inst.LocalSet s:
                    {
                        var v = Pop();
                        var r = SlotRef(s.Local);
                        W($"{r} = langGoCopy({v});");
                        break;
                    }

                case Inst.GlobalGet g:
                    Push(GlobalName((int)g.Global.Value, _prog.Globals[(int)g.Global.Value].Name));
                    break;
                case Inst.GlobalSet s:
                    {
                        var v = Pop();
                        var name = GlobalName((int)s.Global.Value, _prog.Globals[(int)s.Global.Value].Name);
                        W($"{name} = langGoCopy({v});");
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
                    W($"{Pop()};");
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
                            W($"{text};");
                        }

                        break;
                    }

                case Inst.CallIndirect ci:
                    {
                        var args = PopN(ci.Argc);
                        var callee = Pop();
                        var text = $"langGoCallClosure({callee}{JoinArgs(args)})";
                        if (ci.HasRet)
                        {
                            Push(text);
                        }
                        else
                        {
                            W($"{text};");
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
                            W($"{text};");
                        }

                        break;
                    }

                case Inst.Spawn sp:
                    {
                        // 同步确定子集：spawn 等价直接调用，实参快照逐个拷贝（TASK-SPAWN-002）
                        var args = PopN(sp.Argc);
                        var target = _prog.Functions[(int)sp.Func.Value];
                        var name = FnName((int)sp.Func.Value, target.Name);
                        var copied = args.Select(a => $"langGoCopy({a})").ToList();
                        W($"{name}({string.Join(", ", copied)});");
                        break;
                    }

                case Inst.ClosureNew cl:
                    {
                        var caps = PopN(cl.Captures);
                        var env = $"[{string.Join(", ", caps)}]";
                        var tf = _prog.Functions[(int)cl.Func.Value];
                        Push($"{{ __langGoClosure: true, fn: {FnName((int)cl.Func.Value, tf.Name)}, env: {env}, name: {JsStrLit(tf.Name)} }}");
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
                        W($"langGoSetIndex({obj}, {idx}, {val});");
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
                            Push($"langGoFieldRef({obj}, {JsStrLit(fg.Field)})");
                        }
                        else
                        {
                            Push($"langGoField({obj}, {JsStrLit(fg.Field)})");
                        }

                        break;
                    }

                case Inst.FieldSet fs:
                    {
                        var val = Pop();
                        var obj = Pop();
                        W($"langGoSetField({obj}, {JsStrLit(fs.Field)}, {val});");
                        break;
                    }

                case Inst.ArrayPush:
                    // 数组 +=：rhs 为数组 → 原位拼接（IrMachine ArrayPush 同语义；
                    // 单元素追加是 push 内建，走 CallBuiltin langGoPush）
                    {
                        var val = Pop();
                        var arr = Pop();
                        W($"langGoPushArr({arr}, {val});");
                        break;
                    }

                case Inst.ArrayNew an:
                    {
                        var items = PopN(an.Count);
                        Push($"[{string.Join(", ", items)}]");
                        break;
                    }

                case Inst.MapNew mn:
                    {
                        var flat = PopN(mn.Count * 2);
                        var kvs = flat.Chunk(2)
                            .Select(p => $"{{ K: {p[0]}, V: {p[1]} }}")
                            .ToList();
                        Push($"langGoNewMap({string.Join(", ", kvs)})");
                        break;
                    }

                case Inst.StructNew sn:
                    {
                        var decl = _prog.Structs[(int)sn.Struct.Value];
                        var fields = PopN(decl.Fields.Count);
                        var parts = new List<string> { $"__langGoStruct: {JsStrLit(decl.Name)}" };
                        foreach (var (f, v) in decl.Fields.Zip(fields))
                        {
                            parts.Add($"{JsStrLit(f.Name)}: langGoCopy({v})");
                        }

                        Push($"{{ {string.Join(", ", parts)} }}");
                        break;
                    }

                case Inst.EnumNew en:
                    {
                        var decl = _prog.Enums[(int)en.Enum.Value];
                        var kase = decl.Cases[en.Case];
                        var payloads = PopN(kase.PayloadTypes.Count);
                        Push($"langGoEnumVal({JsStrLit(decl.Name)}, {JsStrLit(kase.Name)}, {string.Join(", ", payloads)})");
                        break;
                    }

                case Inst.RangeNew:
                    {
                        var hi = Pop();
                        var lo = Pop();
                        Push($"{{ __langGoRange: true, Lo: {lo}, Hi: {hi} }}");
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
                        W($"langGoRefSet({r}, {val});");
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
                        var it = NewTmp();
                        W($"const {it} = {v};");
                        W($"if ({it}.I >= {it}.Items.length) {{ __pc = {(int)itn.Done.Value}; break; }}");
                        var e = NewTmp();
                        W($"const {e} = langGoCopy({it}.Items[{it}.I]);");
                        W($"{it}.I++;");
                        Push(e);
                        break;
                    }

                case Inst.ChanNew:
                    Push("langGoNewChan()");
                    break;
                case Inst.ChanSend:
                    {
                        var val = Pop();
                        var ch = Pop();
                        W($"langGoSend({ch}, {val});");
                        break;
                    }

                case Inst.ChanRecv:
                    Push($"langGoRecv({Pop()})");
                    break;

                case Inst.Try t:
                    {
                        var v = Pop();
                        var tmp = NewTmp();
                        W($"const {tmp} = {v};");
                        W($"if (langGoIsErr({tmp})) {{ {SlotRef(t.Err)} = langGoTryErrOf({tmp}); __pc = {(int)t.Fail.Value}; break; }}");
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
                    W($"__pc = {(int)g.Target.Value}; break;");
                    break;
                case Terminator.Branch br:
                    {
                        var c = Pop();
                        if (br.IfTrue.Equals(br.IfFalse))
                        {
                            W($"__pc = {(int)br.IfTrue.Value}; break;");
                        }
                        else
                        {
                            W($"if (langGoTruthy({c})) {{ __pc = {(int)br.IfTrue.Value}; break; }}");
                            W($"__pc = {(int)br.IfFalse.Value}; break;");
                        }

                        break;
                    }

                case Terminator.SwitchInt si:
                    {
                        var v = Pop();
                        var t = NewTmp();
                        W($"const {t} = langGoInt({v});");
                        foreach (var (val, target) in si.Arms)
                        {
                            W($"if ({t} === {val.ToString(CultureInfo.InvariantCulture)}n) {{ __pc = {(int)target.Value}; break; }}");
                        }

                        EmitSwitchTail(si.Default);
                        break;
                    }

                case Terminator.SwitchEnum se:
                    {
                        var v = Pop();
                        var decl = _prog.Enums[(int)se.Enum.Value];
                        var t = NewTmp();
                        W($"const {t} = langGoEnumTag({v});");
                        for (int i = 0; i < decl.Cases.Count && i < se.Arms.Count; i++)
                        {
                            W($"if ({t} === {JsStrLit(decl.Cases[i].Name)}) {{ __pc = {(int)se.Arms[i].Value}; break; }}");
                        }

                        EmitSwitchTail(se.Default);
                        break;
                    }

                case Terminator.Return ret:
                    if (ret.HasValue)
                    {
                        W($"return langGoCopy({Pop()});");
                    }
                    else
                    {
                        W("return;");
                    }

                    break;
                case Terminator.Unreachable:
                    W("throw new Error(\"不可达\");");
                    break;
                default:
                    throw new InvalidOperationException("Terminator 变体未穷尽（内部不变量）");
            }
        }

        private void EmitSwitchTail(BlockId? def) =>
            W(def is { } d ? $"__pc = {(int)d.Value}; break;" : "throw new Error(\"match 无匹配分支\");");
    }

    private static string JoinArgs(List<string> args) =>
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
