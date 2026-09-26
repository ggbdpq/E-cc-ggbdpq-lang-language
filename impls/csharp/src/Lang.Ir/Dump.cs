using System.Globalization;
using System.Text;
using Lang.Core;

namespace Lang.Ir;

/// <summary>
/// IR 确定性文本 dump（调试视图）。
/// <b>这是调试视图，不是跨版本契约</b>：格式随 IR 演进可调整，不承诺不同工具版本间
/// 逐字节稳定；对外稳定文本面以 conformance/CLI 契约为准。在同一工具版本内确定性成立：
/// 同一 <see cref="IrProgram"/> 两次 dump 逐字节相等（表序遍历、无哈希迭代序、浮点最短往返、
/// 字符串确定性转义）。本视图<b>不含 span</b>（specs/source-span.md §2：IR 为精简执行视图，
/// 定位回 HIR 取），也不打印 TypeId（语义表下标对调试无用且引入跨表依赖）。
/// 格式速览（每行一个元素；块行缩进 2、指令行缩进 4、声明子行缩进 2）：
/// <code>
/// entry init=&lt;funcId|-&gt;
/// entry main=&lt;funcId|-&gt;
/// struct &lt;i&gt; "&lt;name&gt;" &lt;类型参数&gt; fields=&lt;n&gt;
///   field "&lt;name&gt;"
/// enum &lt;i&gt; "&lt;name&gt;" &lt;类型参数&gt; cases=&lt;n&gt;
///   case &lt;ci&gt; "&lt;name&gt;" payloads=&lt;n&gt;
/// global &lt;i&gt; "&lt;name&gt;" &lt;mut|imm&gt;
/// func &lt;i&gt; "&lt;name&gt;" captures=&lt;c&gt; params=&lt;p&gt; ret=&lt;0|1&gt;
/// func &lt;i&gt; "&lt;name&gt;" extern="&lt;host&gt;"          // extern 函数无块
///   block &lt;b&gt;:
///     &lt;指令/终接助记符&gt;
/// </code>
/// </summary>
public static class IrDumper
{
    /// <summary>dump 整个程序（确定性文本；格式见类型文档）。</summary>
    public static string DumpProgram(IrProgram prog)
    {
        var outp = new StringBuilder();
        EntryLine(outp, "init", prog.Init);
        EntryLine(outp, "main", prog.Main);
        for (int i = 0; i < prog.Structs.Count; i++)
        {
            var s = prog.Structs[i];
            outp.Append("struct ").Append(i).Append(' ').Append(Quote(s.Name))
                .Append(TypeParams(s.TypeParams))
                .Append(" fields=").Append(s.Fields.Count).Append('\n');
            foreach (var f in s.Fields)
            {
                outp.Append("  field ").Append(Quote(f.Name)).Append('\n');
            }
        }
        for (int i = 0; i < prog.Enums.Count; i++)
        {
            var e = prog.Enums[i];
            outp.Append("enum ").Append(i).Append(' ').Append(Quote(e.Name))
                .Append(TypeParams(e.TypeParams))
                .Append(" cases=").Append(e.Cases.Count).Append('\n');
            for (int ci = 0; ci < e.Cases.Count; ci++)
            {
                var c = e.Cases[ci];
                outp.Append("  case ").Append(ci).Append(' ').Append(Quote(c.Name))
                    .Append(" payloads=").Append(c.PayloadTypes.Count).Append('\n');
            }
        }
        for (int i = 0; i < prog.Globals.Count; i++)
        {
            var g = prog.Globals[i];
            outp.Append("global ").Append(i).Append(' ').Append(Quote(g.Name)).Append(' ')
                .Append(g.Mutable ? "mut" : "imm").Append('\n');
        }
        for (int i = 0; i < prog.Functions.Count; i++)
        {
            DumpFunctionInto(outp, i, prog.Functions[i]);
        }
        return outp.ToString();
    }

    /// <summary>dump 单个函数（不含程序头；index 写进 func 行）。</summary>
    public static string DumpFunction(IrFunction func, FunctionId index)
    {
        var outp = new StringBuilder();
        DumpFunctionInto(outp, (int)index.Value, func);
        return outp.ToString();
    }

    private static void EntryLine(StringBuilder outp, string label, FunctionId? fid)
    {
        outp.Append("entry ").Append(label).Append('=')
            .Append(fid is { } f ? f.Value.ToString(CultureInfo.InvariantCulture) : "-").Append('\n');
    }

    private static string TypeParams(IReadOnlyList<string> typeParams)
    {
        if (typeParams.Count == 0)
        {
            return string.Empty;
        }
        // 类型参数是源码标识符（无引号/控制字符），原样拼接。
        return " <" + string.Join(", ", typeParams) + ">";
    }

    private static void DumpFunctionInto(StringBuilder outp, int index, IrFunction func)
    {
        if (func.ExternHost is { } host)
        {
            outp.Append("func ").Append(index).Append(' ').Append(Quote(func.Name))
                .Append(" extern=").Append(Quote(host)).Append('\n');
            return;
        }
        outp.Append("func ").Append(index).Append(' ').Append(Quote(func.Name))
            .Append(" captures=").Append(func.CaptureCount)
            .Append(" params=").Append(func.Sig.ParamCount)
            .Append(" ret=").Append(func.Sig.HasRet ? '1' : '0').Append('\n');
        for (int bi = 0; bi < func.Blocks.Count; bi++)
        {
            outp.Append("  block ").Append(bi).Append(":\n");
            foreach (var inst in func.Blocks[bi].Insts)
            {
                outp.Append("    ").Append(DumpInst(inst)).Append('\n');
            }
            outp.Append("    ").Append(DumpTerm(func.Blocks[bi].Term)).Append('\n');
        }
    }

    /// <summary>声明名加引号（标识符无引号/控制字符，原样；与 lang-rs dump 同形）。</summary>
    private static string Quote(string s) => "\"" + s + "\"";

    /// <summary>字符串字面量转义（引号/反斜杠/控制字符；astral 码点按 UTF-16 对原样保留，确定性）。</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u{").Append(((int)ch).ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>浮点最短往返文本（.NET "R"）；整值补 .0（rust {:?} 同形），非有限值用 rust 拼写。</summary>
    private static string FormatF64(double v)
    {
        if (double.IsNaN(v))
        {
            return "NaN";
        }
        if (double.IsPositiveInfinity(v))
        {
            return "inf";
        }
        if (double.IsNegativeInfinity(v))
        {
            return "-inf";
        }
        var s = v.ToString("R", CultureInfo.InvariantCulture);
        if (s.IndexOfAny(new[] { '.', 'e', 'E' }) < 0)
        {
            s += ".0";
        }
        return s;
    }

    private static string DumpInst(Inst inst) => inst switch
    {
        Inst.ConstI64 v => $"const.i64 {v.Value}",
        Inst.ConstF64 v => $"const.f64 {FormatF64(v.Value)}",
        Inst.ConstBool v => $"const.bool {(v.Value ? "true" : "false")}",
        Inst.ConstStr v => $"const.str {Escape(v.Value)}",
        Inst.LocalGet v => $"local.get {v.Local.Value}",
        Inst.LocalSet v => $"local.set {v.Local.Value}",
        Inst.GlobalGet v => $"global.get {v.Global.Value}",
        Inst.GlobalSet v => $"global.set {v.Global.Value}",
        Inst.Unary v => $"unary.{(v.Op == Unop.Neg ? "neg" : "not")}",
        Inst.Binary v => $"binary.{BinopName(v.Op)}",
        Inst.Pop => "pop",
        Inst.Call v => $"call {v.Func.Value}",
        Inst.CallIndirect v => $"call.indirect argc={v.Argc} ret={(v.HasRet ? '1' : '0')}",
        Inst.CallBuiltin v => $"call.builtin {Escape(v.Name)} argc={v.Argc} ret={(v.HasRet ? '1' : '0')}",
        Inst.Spawn v => $"spawn {v.Func.Value} argc={v.Argc}",
        Inst.ClosureNew v => $"closure.new {v.Func.Value} captures={v.Captures}",
        Inst.IndexGet => "index.get",
        Inst.IndexSet => "index.set",
        Inst.SliceGet => "slice.get",
        Inst.FieldGet v => $"field.get {Escape(v.Field)}",
        Inst.FieldSet v => $"field.set {Escape(v.Field)}",
        Inst.ArrayPush => "array.push",
        Inst.ArrayNew v => $"array.new {v.Count}",
        Inst.MapNew v => $"map.new {v.Count}",
        Inst.StructNew v => $"struct.new {v.Struct.Value}",
        Inst.EnumNew v => $"enum.new {v.Enum.Value} {v.Case}",
        Inst.RangeNew => "range.new",
        Inst.BoxNew => "box.new",
        Inst.Deref => "deref",
        Inst.RefSet => "ref.set",
        Inst.EnumPayloadGet v => $"enum.payload.get {v.Index}",
        Inst.IterNew => "iter.new",
        Inst.IterNext v => $"iter.next {v.Done.Value}",
        Inst.ChanNew => "chan.new",
        Inst.ChanSend => "chan.send",
        Inst.ChanRecv => "chan.recv",
        Inst.Try v => $"try err={v.Err.Value} fail={v.Fail.Value}",
        _ => throw new InvalidOperationException("IR 指令变体未穷尽（dump 必须覆盖全部指令）"),
    };

    private static string DumpTerm(Terminator term) => term switch
    {
        Terminator.Goto v => $"goto {v.Target.Value}",
        Terminator.Branch v => $"branch {v.IfTrue.Value} {v.IfFalse.Value}",
        Terminator.SwitchInt v => RenderSwitch("switch.int", v.Arms, v.Default),
        Terminator.SwitchEnum v => RenderSwitch($"switch.enum {v.Enum.Value}", v.Arms, v.Default),
        Terminator.Return v => $"return {(v.HasValue ? '1' : '0')}",
        Terminator.Unreachable => "unreachable",
        _ => throw new InvalidOperationException("IR 终接变体未穷尽（dump 必须覆盖全部终接）"),
    };

    private static string RenderSwitch(string head, IReadOnlyList<(long Value, BlockId Target)> arms, BlockId? @default)
    {
        var s = new StringBuilder(head);
        foreach (var (value, target) in arms)
        {
            s.Append(' ').Append(value).Append("->").Append(target.Value);
        }
        WriteDefault(s, @default);
        return s.ToString();
    }

    private static string RenderSwitch(string head, IReadOnlyList<BlockId> arms, BlockId? @default)
    {
        var s = new StringBuilder(head);
        for (int ci = 0; ci < arms.Count; ci++)
        {
            s.Append(' ').Append(ci).Append("->").Append(arms[ci].Value);
        }
        WriteDefault(s, @default);
        return s.ToString();
    }

    private static void WriteDefault(StringBuilder s, BlockId? @default)
    {
        if (@default is { } b)
        {
            s.Append(" default=").Append(b.Value);
        }
        else
        {
            s.Append(" default=-");
        }
    }

    private static string BinopName(Binop op) => op switch
    {
        Binop.Eq => "eq",
        Binop.Ne => "ne",
        Binop.Lt => "lt",
        Binop.Le => "le",
        Binop.Gt => "gt",
        Binop.Ge => "ge",
        Binop.Add => "add",
        Binop.Sub => "sub",
        Binop.Mul => "mul",
        Binop.Div => "div",
        Binop.Rem => "rem",
        Binop.BitAnd => "bit.and",
        Binop.BitOr => "bit.or",
        Binop.BitXor => "bit.xor",
        Binop.Shl => "shl",
        Binop.Shr => "shr",
        _ => throw new InvalidOperationException("Binop 变体未穷尽"),
    };
}
