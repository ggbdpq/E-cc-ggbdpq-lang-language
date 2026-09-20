"""→Go 转译后端（gen.go 语义等价）：Core IR → 自包含可编译 Go 源码。

名字解析、类型推断、语义校验已在 semantic/types 完成；本层只做
lang-go 类型 → Go 类型映射与 any 边界的收窄插桩（langGoTo）。
`?` 传播（Try）仅出现在 let/return/赋值顶层，gen_try 在语句上下文直接发射语句。
prelude 文本为产物内嵌运行时（行为是跨实现硬契约，见资源文件 go_prelude.go.txt）。
"""

from __future__ import annotations

import os
from typing import Optional

from ..errors import LangError
from ..ir import nodes as I
from ..typesys.model import TK, Type, result_ok

_PRELUDE_PATH = os.path.join(os.path.dirname(__file__), "go_prelude.go.txt")

with open(_PRELUDE_PATH, "r", encoding="utf-8") as _f:
    PRELUDE = _f.read()

_BUILTIN_MAP = {
    "println": "langGoPrintln", "print": "langGoPrint", "len": "langGoLen",
    "str": "langGoStr", "int": "langGoInt", "float": "langGoFloat",
    "keys": "langGoKeys", "values": "langGoValues", "has": "langGoHas",
    "push": "langGoPush", "assert": "langGoAssert", "err": "langGoErr",
    "bytes": "langGoBytes", "char": "langGoChar",
    "makeChan": "langGoNewChan", "send": "langGoSend", "recv": "langGoRecv",
    "jsonParse": "langGoJSONParse", "jsonStringify": "langGoJSONStringify",
    "isNone": "langGoIsNone",
}

# 部分内建在 →Go prelude 里的实际返回形态（装箱 any）
_BUILTIN_RET = {
    "bytes": "*LangGoArr[any]",
    "keys": "*LangGoArr[any]",
    "values": "*LangGoArr[any]",
    "recv": "LangGoRes[any]",
    "makeChan": "chan any",
    "jsonParse": "any",
}


def go_type_of(t: Optional[Type]) -> str:
    """lang-go 类型 → Go 类型（映射，非推断）。"""
    if t is None:
        return "any"
    k = t.kind
    if k == TK.INT:
        return "int64"
    if k == TK.FLOAT:
        return "float64"
    if k == TK.STRING:
        return "string"
    if k == TK.BOOL:
        return "bool"
    if k == TK.ERROR:
        return "error"
    if k == TK.CHAN:
        return "chan any"  # v0 装箱
    if k == TK.SLICE:
        return f"*LangGoArr[{go_type_of(t.elem)}]"
    if k == TK.MAP:
        return "*LangGoMap"
    if k == TK.REF:
        return "*" + go_type_of(t.elem)
    if k == TK.OPTION:
        return f"LangGoOpt[{go_type_of(t.elem)}]"
    if k == TK.RESULT:
        return f"LangGoRes[{go_type_of(result_ok(t))}]"
    if k == TK.ENUM:
        return "LangGoEnum"
    if k == TK.STRUCT:
        return t.sym.name if t.sym is not None else "any"
    if k == TK.RANGE:
        return "langGoRange"
    if k == TK.TYPE_ARG:
        return t.name if t.name else "any"
    if k == TK.FN:
        ps = ", ".join(go_type_of(p) for p in t.params)
        sig = f"func({ps})"
        if t.ret is not None and t.ret.kind != TK.UNIT:
            sig += " " + go_type_of(t.ret)
        return sig
    return "any"


def _is_any(t: Optional[Type]) -> bool:
    return t is None or t.kind in (TK.ANY, TK.UNKNOWN, TK.INVALID, TK.TYPE_ARG)


def _go_quote(s: str) -> str:
    """Go 字符串字面量（生成侧）。"""
    out = ['"']
    for ch in s:
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif ch == "\n":
            out.append("\\n")
        elif ch == "\t":
            out.append("\\t")
        elif ch == "\r":
            out.append("\\r")
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def _fmt_float(f: float) -> str:
    """Go strconv.FormatFloat 'g' -1 的生成侧文本（复用 interp 的位级验证实现）。"""
    from ..interp.values import format_g
    return format_g(f)


class GoGen:
    def __init__(self, p: I.Prog) -> None:
        self.p = p
        self.buf: list[str] = []
        self.indent = 0
        self.structs = {s.name: s for s in p.structs}
        self.case_enum: dict[str, str] = {}
        for e in p.enums:
            for c in e.cases:
                self.case_enum[c.name] = e.name
        self.fns: dict[int, I.Fn] = {}  # Symbol.id → IR Fn
        self.tmp_n = 0
        self.cur_fn: Optional[I.Fn] = None
        self.match_ret: Optional[Type] = None  # 表达式位置 match 的结果类型
        self.local_go: dict[int, str] = {}  # id(Local) → 实际 Go 类型
        self._register_fns()

    def _register_fns(self) -> None:
        for f in self.p.fns:
            if f.sym is not None:
                self.fns[f.sym.id] = f
            self._walk_fn_defs(f.body)

    def _walk_fn_defs(self, stmts: list[I.Stmt]) -> None:
        for s in stmts:
            if isinstance(s, I.FnDef):
                if s.fn.sym is not None:
                    self.fns[s.fn.sym.id] = s.fn
                self._walk_fn_defs(s.fn.body)

    # ———— 基础 ————
    def err(self, msg: str) -> LangError:
        return LangError("→Go 转译失败: " + msg)

    def new_tmp(self) -> str:
        self.tmp_n += 1
        return f"_t{self.tmp_n}"

    def w(self, line: str) -> None:
        self.buf.append("\t" * self.indent + line + "\n")

    # ———— 生成侧 Go 类型 ————
    def gt(self, e: I.Expr) -> str:
        """局部表达式的「生成侧 Go 类型」（语义类型 ≠ Go 运行时形态）。"""
        if isinstance(e, I.LocalGet):
            s = self.local_go.get(id(e.local))
            return s if s is not None else go_type_of(e.local.typ)
        if isinstance(e, (I.Index, I.Slice)):
            return "any"  # langGoIndex/langGoSlice 返回 interface
        if isinstance(e, I.Field):
            if _is_any(e.obj.typ):
                return "any"  # langGoField
            if e.obj.typ is not None and e.obj.typ.kind == TK.ERROR:
                return "string"  # langGoErrMsg
        if isinstance(e, I.Call):
            if e.builtin and e.builtin in _BUILTIN_RET:
                return _BUILTIN_RET[e.builtin]
            if e.fn is not None:
                fi = self.fns.get(e.fn.id)
                if fi is not None:
                    return go_type_of(fi.ret) if fi.ret is not None else "any"
        if isinstance(e, I.Ctor):
            return go_type_of(e.typ)
        if isinstance(e, I.Match):
            return self.match_ret_type(e)
        if isinstance(e, I.Binary):
            if e.op == I.OP_PLUS:
                li, ri = self.gt(e.l), self.gt(e.r)
                if li == "string" or ri == "string":
                    return "string"
                if li == "any" or ri == "any":
                    return "any"
                return li
            if e.op in (I.OP_ANDAND, I.OP_OROR):
                return "bool"
        if isinstance(e, I.Unary):
            if e.op == I.OP_NOT:
                return "bool"
        return go_type_of(e.typ)

    def match_ret_type(self, m: I.Match) -> str:
        ret = go_type_of(m.typ)
        for arm in m.arms:
            if go_type_of(arm.body.typ) != ret:
                return "any"
        return ret

    def wrap_arg(self, target: Optional[Type], e: I.Expr, expr: str) -> str:
        """目标具体而右侧是动态 any → 需要 langGoTo 收窄。"""
        if target is not None and not _is_any(target) and self.gt(e) == "any":
            return f"langGoTo[{go_type_of(target)}]({expr})"
        return expr

    # ———— 程序 ————
    def emit_program(self) -> None:
        self.buf.append(PRELUDE)
        for s in self.p.structs:
            parts = [f"{f.name} {go_type_of(f.typ)}" for f in s.fields]
            fmt_parts = [f'{_go_quote(f.name)} + ": " + langGoFmt(v.{f.name}, false)' for f in s.fields]
            self.w(f"type {s.name} struct {{")
            for fl in parts:
                self.w(fl)
            self.w("}")
            self.w("")
            self.w(f"func (v {s.name}) langGoString(top bool) string {{")
            self.w(f'return {_go_quote(s.name)} + "{{" + strings.Join([]string{{{", ".join(fmt_parts)}}}, ", ") + "}}"')
            self.w("}")
            self.w(f"func (v {s.name}) LangGoField(name string) any {{")
            self.w("switch name {")
            for f in s.fields:
                self.w(f'case {_go_quote(f.name)}:')
                self.w(f"return v.{f.name}")
            self.w("}")
            self.w(f'panic({_go_quote(s.name)} + " 没有字段 " + name)')
            self.w("}")
            self.w("")
        for en in self.p.enums:
            for c in en.cases:
                ps = [f"p{i} {go_type_of(pt)}" for i, pt in enumerate(c.payloads)]
                vals = [f"p{i}" for i in range(len(c.payloads))]
                vals_expr = "nil"
                if vals:
                    vals_expr = "[]any{" + ", ".join(vals) + "}"
                self.w(f"func le_{en.name}_{c.name}({', '.join(ps)}) LangGoEnum "
                       f"{{ return LangGoEnum{{{_go_quote(c.name)}, {vals_expr}}} }}")
                self.w("")
        for gl in self.p.globals:
            # 全局绑定：Go 包级只有 var（无 wrap 收窄，与参考实现一致）
            expr = self.expr(gl.init)
            self.w(f"var {gl.local.name} = {expr}")
            self.w("")
        for f in self.p.fns:
            self.emit_func(f)
        if self.p.main is not None:
            self.w("func main() {")
            self.w("\tlf_main()")
            self.w("\tlangGoWaitAll()")
            self.w("}")
        else:
            self.w("func main() {}")

    def go_name(self, f: I.Fn) -> str:
        if f.recv is not None:
            return f.name  # 原生 Go 方法
        return "lf_" + f.name

    # ———— 函数 ————
    def emit_func(self, f: I.Fn) -> None:
        generic = ""
        if f.type_params:
            gs = ", ".join(f"{tp} any" for tp in f.type_params)
            generic = f"[{gs}]"
        recv_prefix = ""
        if f.recv is not None:
            recv_prefix = f"({f.recv.name} {go_type_of(f.recv.typ)}) "
            self.local_go[id(f.recv)] = go_type_of(f.recv.typ)
        ps = []
        for p in f.params:
            ps.append(f"{p.name} {go_type_of(p.typ)}")
            self.local_go[id(p)] = go_type_of(p.typ)
        ret_decl = ""
        if f.ret is not None:
            ret_decl = " " + go_type_of(f.ret)
        self.w(f"func {recv_prefix}{self.go_name(f)}{generic}({', '.join(ps)}){ret_decl} {{")
        saved_fn = self.cur_fn
        self.cur_fn = f
        self.emit_body(f.body)
        # 函数体内有 go 语句：函数边界 join
        if self.body_has_go(f.body):
            self.w("\tlangGoWaitAll()")
        self.cur_fn = saved_fn
        self.w("}")
        self.w("")

    @staticmethod
    def body_has_go(stmts: list[I.Stmt]) -> bool:
        for st in stmts:
            if isinstance(st, I.Spawn):
                return True
            if isinstance(st, I.If):
                if GoGen.body_has_go(st.then) or GoGen.body_has_go(st.els):
                    return True
            if isinstance(st, I.While) and GoGen.body_has_go(st.body):
                return True
            if isinstance(st, I.ForIn) and GoGen.body_has_go(st.body):
                return True
        return False

    def emit_body(self, stmts: list[I.Stmt]) -> None:
        for st in stmts:
            self.emit_stmt(st)

    # ———— 语句 ————
    def emit_stmt(self, s: I.Stmt) -> None:
        if isinstance(s, I.Let):
            self.emit_let(s)
        elif isinstance(s, I.Assign):
            self.emit_assign(s)
        elif isinstance(s, I.Eval):
            if isinstance(s.x, I.Match):
                self.emit_match_stmt(s.x)
                return
            expr = self.expr(s.x)
            if self.is_void_call(s.x):
                self.w(expr)
            else:
                self.w("_ = " + expr)
        elif isinstance(s, I.Return):
            self.emit_return(s)
        elif isinstance(s, I.If):
            cond = self.cond_expr(s.cond)
            self.w(f"if {cond} {{")
            self.emit_block(s.then)
            if s.els:
                self.w("} else {")
                self.emit_block(s.els)
            self.w("}")
        elif isinstance(s, I.While):
            cond = self.cond_expr(s.cond)
            self.w(f"for {cond} {{")
            self.emit_block(s.body)
            self.w("}")
        elif isinstance(s, I.ForIn):
            self.emit_for_in(s)
        elif isinstance(s, I.Break):
            self.w("break")
        elif isinstance(s, I.Continue):
            self.w("continue")
        elif isinstance(s, I.Spawn):
            fi = self.fns.get(s.fn.id) if s.fn is not None else None
            if fi is None:
                raise self.err(f"内部错误：go 目标未注册 {s.fn.name if s.fn else '?'}")
            args = []
            for i, a in enumerate(s.args):
                arg = self.expr(a)
                if i < len(fi.params):
                    arg = self.wrap_arg(fi.params[i].typ, a, arg)
                args.append(arg)
            self.w(f"langGoGo(func() {{ {self.go_name(fi)}({', '.join(args)}) }})")
        elif isinstance(s, I.FnDef):
            self.emit_func(s.fn)
        else:
            raise self.err("→Go 暂不支持该语句")

    def is_void_call(self, e: I.Expr) -> bool:
        if not isinstance(e, I.Call):
            return False
        if e.fn is not None:
            fi = self.fns.get(e.fn.id)
            return fi is not None and fi.ret is None
        if e.method is not None:
            fi = self.fns.get(e.method.id)
            return fi is not None and fi.ret is None
        if e.builtin:
            return e.builtin in ("println", "print", "push", "assert", "send")
        return False

    def emit_let(self, st: I.Let) -> None:
        name = st.local.name
        if isinstance(st.init, I.Try):
            tmp = self.gen_try(st.init)
            if name != "_":
                # 收窄源是 ? 操作数的 Result T
                bind_type = "any"
                rt = st.init.x.typ
                if rt is not None and rt.kind == TK.RESULT:
                    bind_type = go_type_of(result_ok(rt))
                rhs = f"{tmp}.v"
                if bind_type != "any":
                    rhs = f"langGoTo[{bind_type}]({rhs})"
                self.w(f"{name} := {rhs}")
            return
        expr = self.expr(st.init)
        if name == "_":
            self.w("_ = " + expr)
            return
        bind_type = go_type_of(st.local.typ)
        bind_go = self.gt(st.init)
        # 空数组字面量继承注解类型：var out: [U] = []
        if isinstance(st.init, I.SliceLit) and not st.init.elems and bind_type.startswith("*LangGoArr["):
            expr = "&" + bind_type[1:] + "{}"
            bind_go = bind_type
        else:
            if bind_type != "any" and self.gt(st.init) == "any":
                bind_go = bind_type  # 有注解（或收窄目标）→ 局部 Go 类型即注解
            expr = self.wrap_arg(st.local.typ, st.init, expr)
        self.local_go[id(st.local)] = bind_go
        self.w(f"{name} := {expr}")
        # lang-go 无"局部绑定必须使用"规则，Go 有——统一消 unused 编译错
        self.w(f"_ = {name}")
        # 函数值绑定注册参数/返回类型，调用点据此收窄
        if isinstance(st.init, I.FnLit) and st.local.sym is not None:
            self.fns[st.local.sym.id] = I.Fn(name=name, sym=st.local.sym, recv=None,
                                             type_params=[], params=list(st.init.params),
                                             ret=st.init.ret, locals=[], body=[])

    def emit_assign(self, st: I.Assign) -> None:
        if st.op != I.OP_ASSIGN:
            self.emit_compound_assign(st)
            return
        tgt = st.target
        if isinstance(tgt, I.LocalGet):
            if isinstance(st.val, I.Try):
                tmp = self.gen_try(st.val)
                self.w(f"{tgt.local.name} = {tmp}.v")
                return
            expr = self.wrap_arg(tgt.local.typ, st.val, self.expr(st.val))
            self.w(f"{tgt.local.name} = {expr}")
        elif isinstance(tgt, I.Field):
            expr = self.expr(st.val)
            if isinstance(tgt.obj, I.Index):
                # 容器元素字段链 xs[i].f = v：读改写（VALUE-ELEM-009）
                cobj = self.expr(tgt.obj.obj)
                cidx = self.expr(tgt.obj.idx)
                elem_t = go_type_of(tgt.obj.typ)
                tmp = self.new_tmp()
                self.w(f"{tmp} := langGoIndex[{elem_t}]({cobj}, {cidx})")
                self.w(f"{tmp}.{tgt.name} = {expr}")
                self.w(f"langGoSetIndex({cobj}, {cidx}, {tmp})")
                return
            obj = self.expr(tgt.obj)
            self.w(f"{obj}.{tgt.name} = {expr}")
        elif isinstance(tgt, I.Index):
            obj = self.expr(tgt.obj)
            idx = self.expr(tgt.idx)
            expr = self.expr(st.val)
            self.w(f"langGoSetIndex({obj}, {idx}, {expr})")
        else:
            raise self.err("赋值目标不合法")

    def emit_compound_assign(self, st: I.Assign) -> None:
        op = st.op
        tgt = st.target
        if isinstance(tgt, I.LocalGet):
            expr = self.expr(st.val)
            lt = go_type_of(tgt.local.typ)
            if st.op == I.OP_PLUS_A and lt.startswith("*LangGoArr["):
                self.w(f"{tgt.local.name} = langGoPushFrom({tgt.local.name}, {expr})")
                return
            # any 右值（如 recv(ch)? 解包）按目标类型收窄
            expr = self.wrap_arg(tgt.local.typ, st.val, expr)
            self.w(f"{tgt.local.name} {op} {expr}")
        elif isinstance(tgt, I.Field):
            obj = self.expr(tgt.obj)
            expr = self.expr(st.val)
            self.w(f"{obj}.{tgt.name} {op} {expr}")
        elif isinstance(tgt, I.Index):
            obj = self.expr(tgt.obj)
            idx = self.expr(tgt.idx)
            expr = self.expr(st.val)
            self.w(f"langGoAddIndex({obj}, {idx}, {expr}, {_go_quote(op)})")
        else:
            raise self.err("赋值目标不合法")

    def emit_return(self, st: I.Return) -> None:
        if st.val is None:
            self.w("return")
            return
        if isinstance(st.val, I.Try):
            tmp = self.gen_try(st.val)
            self.w(f"return {tmp}.v")
            return
        if self.cur_fn is not None and self.cur_fn.ret is not None and self.cur_fn.ret.kind == TK.RESULT:
            # Result 函数的 return 必为 Ok(...)/Err(...)（checker 已约束）
            ctor = st.val
            if not isinstance(ctor, I.Ctor) or ctor.sym.name not in ("Ok", "Err") or len(ctor.args) != 1:
                raise self.err("内部错误：Result return 形态异常")
            t = go_type_of(result_ok(self.cur_fn.ret))
            expr = self.expr(ctor.args[0])
            if ctor.sym.name == "Ok":
                self.w(f"return LangGoOk[{t}]({expr})")
            else:
                self.w(f"return LangGoResErr[{t}]({expr})")
            return
        if self.cur_fn is None or self.cur_fn.ret is None:
            raise self.err("void 函数不应返回值")
        expr = self.expr(st.val)
        rt = go_type_of(self.cur_fn.ret)
        if rt != "any" and self.gt(st.val) == "any":
            expr = f"langGoTo[{rt}]({expr})"  # 返回值按签名收窄
        self.w(f"return {expr}")

    def gen_try(self, t: I.Try) -> str:
        """展开 ? 传播；返回持有 Result 的临时变量名。"""
        if self.cur_fn is None or self.cur_fn.ret is None or self.cur_fn.ret.kind != TK.RESULT:
            raise self.err("带 ? 的函数须注解为 Result[T, error]")
        tmp = self.new_tmp()
        expr = self.expr(t.x)
        if self.gt(t.x) == "any":
            expr = f"langGoTo[LangGoRes[any]]({expr})"  # 装箱 Result 先收窄再解包
        tt = go_type_of(result_ok(self.cur_fn.ret))
        self.w(f"{tmp} := {expr}")
        self.w(f"if !{tmp}.ok {{")
        self.indent += 1
        self.w(f"return LangGoResErr[{tt}]({tmp}.e)")
        self.indent -= 1
        self.w("}")
        return tmp

    def cond_expr(self, e: I.Expr) -> str:
        expr = self.expr(e)
        if self.gt(e) != "bool":
            expr = f"langGoTruthy({expr})"
        return expr

    def emit_block(self, stmts: list[I.Stmt]) -> None:
        self.indent += 1
        self.emit_body(stmts)
        self.indent -= 1

    def emit_for_in(self, st: I.ForIn) -> None:
        if st.kind == I.IterKind.RANGE:
            self.local_go[id(st.var)] = "int64"
            # 区间经绑定（let r = a..b; for i in r）与字面量两条路径
            if isinstance(st.iter, I.Range):
                lo = self.expr(st.iter.lo)
                hi = self.expr(st.iter.hi)
                self.w(f"for {st.var.name} := {lo}; {st.var.name} < {hi}; {st.var.name}++ {{")
                self.emit_block(st.body)
                self.w("}")
                return
            rv = self.expr(st.iter)
            tmp = self.new_tmp()
            self.w(f"{tmp} := {rv}")
            self.w(f"for {st.var.name} := {tmp}.Lo; {st.var.name} < {tmp}.Hi; {st.var.name}++ {{")
            self.emit_block(st.body)
            self.w("}")
            return
        tmp = self.new_tmp()
        it = self.expr(st.iter)
        name = st.var.name
        iter_go = self.gt(st.iter)
        if st.kind == I.IterKind.SLICE and iter_go != "any":
            self.local_go[id(st.var)] = iter_go[len("*LangGoArr["):-1]
            self.w(f"{tmp} := {it}")
            self.w(f"for _, {name} := range {tmp}.Items {{")
            self.emit_block(st.body)
            self.w("}")
        elif st.kind == I.IterKind.STRING:
            # lang-go 语义：按码点迭代，绑定单字符字符串
            self.local_go[id(st.var)] = "string"
            self.w(f"for _, _r{tmp} := range []rune({it}) {{")
            self.indent += 1
            self.w(f"{name} := string(_r{tmp})")
            self.emit_block(st.body)
            self.indent -= 1
            self.w("}")
        else:
            # 动态值（JSON 结果/映射键/any 局部）：运行时分派迭代（langGoIter 快照）
            self.local_go[id(st.var)] = "any"
            self.w(f"{tmp} := langGoIter({it})")
            self.w(f"for _, {name} := range {tmp}.Items {{")
            self.emit_block(st.body)
            self.w("}")

    # ———— match ————
    def emit_match_stmt(self, m: I.Match) -> None:
        tmp = self.new_tmp()
        scrut = self.expr(m.scrut)
        scrut = self.coerce_algebraic(scrut, m)
        self.w(f"{tmp} := {scrut}")
        if m.kind == I.MatchKind.RESULT:
            ok_arm, err_arm = split_algebraic(m, "Ok", "Err")
            self.w(f"if {tmp}.ok {{")
            self.emit_result_arm_block(ok_arm, tmp, ".v")
            self.w("} else {")
            self.emit_result_arm_block(err_arm, tmp, ".e")
            self.w("}")
        elif m.kind == I.MatchKind.OPTION:
            some_arm, none_arm = split_algebraic(m, "Some", "None")
            self.w(f"if {tmp}.has {{")
            self.emit_result_arm_block(some_arm, tmp, ".v")
            if none_arm is not None:
                self.w("} else {")
                self.indent += 1
                self.emit_arm_stmt(none_arm)
                self.indent -= 1
            self.w("}")
        elif m.kind == I.MatchKind.ENUM:
            self.w(f"switch {tmp}.Tag {{")
            self.indent += 1
            for arm in m.arms:
                if isinstance(arm.pat, I.CtorPat):
                    self.w(f'case {_go_quote(arm.pat.sym.name)}:')
                    self.indent += 1
                    self.bind_enum_payloads(arm.pat, tmp)
                    self.emit_arm_stmt(arm)
                    self.indent -= 1
                else:
                    self.w("default:")
                    self.indent += 1
                    if isinstance(arm.pat, I.BindPat):
                        self.w(f"{arm.pat.name} := {tmp}")
                        self.w(f"_ = {arm.pat.name}")
                    self.emit_arm_stmt(arm)
                    self.indent -= 1
            self.indent -= 1
            self.w("}")
        else:
            self.emit_literal_match_stmt(m, tmp)

    def coerce_algebraic(self, scrut: str, m: I.Match) -> str:
        """装箱的 Result/Option（如 http.get 返回 any）在解包前收窄。"""
        if not _is_any(m.scrut.typ):
            return scrut
        if m.kind == I.MatchKind.RESULT:
            return f"langGoTo[LangGoRes[any]]({scrut})"
        if m.kind == I.MatchKind.OPTION:
            return f"langGoTo[LangGoOpt[any]]({scrut})"
        return scrut

    def emit_result_arm_block(self, arm: Optional[I.Arm], tmp: str, field: str) -> None:
        self.indent += 1
        if arm is not None:
            name = arm_bind_name(arm)
            if name != "_":
                self.w(f"{name} := {tmp}{field}")
                self.w(f"_ = {name}")  # 臂体未用时消 Go unused
            self.emit_arm_stmt(arm)
        self.indent -= 1

    def bind_enum_payloads(self, p: I.CtorPat, tmp: str) -> None:
        for j, sp in enumerate(p.subs):
            if isinstance(sp, I.BindPat):
                pt = "any"
                if sp.typ is not None and not _is_any(sp.typ):
                    pt = go_type_of(sp.typ)
                self.w(f"{sp.name} := langGoTo[{pt}]({tmp}.Vals[{j}])")

    def emit_arm_stmt(self, arm: I.Arm) -> None:
        if isinstance(arm.body, I.Match):
            self.emit_match_stmt(arm.body)
            return
        expr = self.expr(arm.body)
        if self.is_void_call(arm.body):
            self.w(expr)
        else:
            self.w("_ = " + expr)

    def emit_literal_match_stmt(self, m: I.Match, tmp: str) -> None:
        for i, arm in enumerate(m.arms):
            if isinstance(arm.pat, I.LitPat):
                kw = "if" if i == 0 else "} else if"
                lit = self.expr(arm.pat.val)
                self.w(f"{kw} langGoEq({tmp}, {lit}) {{")
                self.emit_block([I.Eval(x=arm.body)])
            elif isinstance(arm.pat, I.BindPat):
                self.w("} else {")
                self.indent += 1
                self.w(f"{arm.pat.name} := {tmp}")
                self.w(f"_ = {arm.pat.name}")
                self.emit_arm_stmt(arm)
                self.indent -= 1
            elif isinstance(arm.pat, I.WildPat):
                self.w("} else {")
                self.emit_block([I.Eval(x=arm.body)])
        self.w("}")

    # ———— 表达式 ————
    def operand(self, e: I.Expr) -> str:
        s = self.expr(e)
        if isinstance(e, (I.Binary, I.Unary)):
            return f"({s})"  # 子式是运算时强制加括号（优先级差异防御）
        return s

    def expr(self, e: I.Expr) -> str:
        if isinstance(e, I.Const):
            if e.kind == I.ConstKind.INT:
                return f"int64({e.i64})"
            if e.kind == I.ConstKind.FLOAT:
                return f"float64({_fmt_float(e.f64)})"
            if e.kind == I.ConstKind.STR:
                return _go_quote(e.s)
            return "true" if e.b else "false"
        if isinstance(e, I.LocalGet):
            return e.local.name
        if isinstance(e, I.FnGet):
            fi = self.fns.get(e.sym.id) if e.sym is not None else None
            if fi is not None:
                return self.go_name(fi)
            return "lf_" + e.name
        if isinstance(e, I.BuiltinGet):
            raise self.err(f"内建 {e.name} 须直接调用")
        if isinstance(e, I.Ctor):
            return self.emit_ctor(e)
        if isinstance(e, I.Call):
            return self.emit_call(e)
        if isinstance(e, I.Unary):
            inner = self.operand(e.x)
            if e.op == I.OP_NEG:
                return f"-{inner}"
            if e.op == I.OP_NOT:
                return f"!{inner}"
            if e.op == I.OP_REF:
                return f"&{inner}"
            raise self.err("未知一元运算")
        if isinstance(e, I.Binary):
            return self.emit_binary(e)
        if isinstance(e, I.Range):
            return f"langGoRange{{Lo: {self.expr(e.lo)}, Hi: {self.expr(e.hi)}}}"
        if isinstance(e, I.Index):
            obj = self.expr(e.obj)
            idx = self.expr(e.idx)
            # 显式类型实参：调用点按元素类型特化
            return f"langGoIndex[{go_type_of(e.typ)}]({obj}, {idx})"
        if isinstance(e, I.Slice):
            obj = self.expr(e.obj)
            lo = self.expr(e.lo)
            hi = self.expr(e.hi)
            return f"langGoSlice({obj}, {lo}, {hi})"
        if isinstance(e, I.Field):
            obj = self.expr(e.obj)
            if not _is_any(e.obj.typ) and e.obj.typ is not None and e.obj.typ.kind == TK.ERROR:
                if e.name == "msg":
                    return f"langGoErrMsg({obj})"
                raise self.err("error 只有 msg 字段")
            if self.gt(e) == "any":
                return f"langGoField({obj}, {_go_quote(e.name)})"  # 装箱值经接口分派
            return f"{obj}.{e.name}"
        if isinstance(e, I.Try):
            raise self.err("? 只能出现在 let/return/赋值的顶层（→Go 约束）")
        if isinstance(e, I.StructLit):
            name = e.struct.name if e.struct is not None else ""
            sd = self.structs.get(name)
            fs = []
            for i, f in enumerate(e.fields):
                v = self.expr(f.val)
                if sd is not None and i < len(sd.fields):
                    v = self.wrap_arg(sd.fields[i].typ, f.val, v)
                fs.append(f"{f.name}: {v}")
            return f"{name}{{{', '.join(fs)}}}"
        if isinstance(e, I.MapLit):
            kvs = []
            for k_e, v_e in zip(e.keys, e.vals):
                kvs.append(f"langGoKV{{{self.expr(k_e)}, {self.expr(v_e)}}}")
            return f"langGoNewMap({', '.join(kvs)})"
        if isinstance(e, I.SliceLit):
            elem = "any"
            if e.typ is not None and e.typ.kind == TK.SLICE and not _is_any(e.typ.elem):
                elem = go_type_of(e.typ.elem)
            items = []
            for el in e.elems:
                v = self.expr(el)
                if e.typ is not None and e.typ.elem is not None and not _is_any(e.typ.elem) and self.gt(el) == "any":
                    v = f"langGoTo[{go_type_of(e.typ.elem)}]({v})"
                items.append(v)
            if not items:
                return f"&LangGoArr[{elem}]{{}}"
            return f"&LangGoArr[{elem}]{{Items: []{elem}{{{', '.join(items)}}}}}"
        if isinstance(e, I.Match):
            return self.expr_match(e)
        if isinstance(e, I.FnLit):
            return self.emit_fn_lit(e)
        raise self.err("→Go 暂不支持该表达式")

    def emit_ctor(self, x: I.Ctor) -> str:
        if x.sym.name == "None":
            # None 无载荷无法靠推断：类型实参取当前函数返回类型的元素
            # （泛型函数=类型参数 T；非泛型 Option[int] → int64），与签名一致
            tp = "any"
            if self.cur_fn is not None:
                if len(self.cur_fn.type_params) == 1:
                    tp = self.cur_fn.type_params[0]
                elif (self.cur_fn.ret is not None and self.cur_fn.ret.kind == TK.OPTION
                      and self.cur_fn.ret.elem is not None and not _is_any(self.cur_fn.ret.elem)):
                    tp = go_type_of(self.cur_fn.ret.elem)
            return f"LangGoNone[{tp}]()"
        if x.sym.name == "Some":
            if len(x.args) != 1:
                raise self.err("Some 需要 1 个参数")
            return f"LangGoSome({self.expr(x.args[0])})"
        if x.sym.name in ("Ok", "Err"):
            raise self.err("Ok/Err 只能出现在 Result 函数的 return（→Go 约束）")
        en = self.case_enum.get(x.sym.name, "")
        args = [self.expr(a) for a in x.args]
        return f"le_{en}_{x.sym.name}({', '.join(args)})"

    def emit_binary(self, x: I.Binary) -> str:
        l = self.operand(x.l)
        r = self.operand(x.r)
        if x.op == I.OP_ANDAND:
            return f"{l} && {r}"
        if x.op == I.OP_OROR:
            return f"{l} || {r}"
        if x.op == I.OP_EQ:
            return f"langGoEq({l}, {r})"
        if x.op == I.OP_NEQ:
            return f"!langGoEq({l}, {r})"
        if x.op in (I.OP_LT, I.OP_LE, I.OP_GT, I.OP_GE):
            cmp = f"langGoCompare({l}, {r})"  # 排序比较统一走 langGoCompare
            if x.op == I.OP_LT:
                return f"{cmp} < 0"
            if x.op == I.OP_LE:
                return f"{cmp} <= 0"
            if x.op == I.OP_GT:
                return f"{cmp} > 0"
            return f"{cmp} >= 0"
        if x.op == I.OP_PLUS:
            li, ri = self.gt(x.l), self.gt(x.r)
            if li == "any" or ri == "any":
                if li == "string" or ri == "string":
                    return f"langGoStrVal({l}) + langGoStrVal({r})"
                return f"langGoInt({l}) + langGoInt({r})"
        if x.op in (I.OP_MINUS, I.OP_STAR, I.OP_SLASH, I.OP_PERCENT,
                    I.OP_AND, I.OP_BIT_OR, I.OP_XOR, I.OP_SHL, I.OP_SHR):
            if self.gt(x.l) == "any":
                l = f"langGoInt({l})"
            if self.gt(x.r) == "any":
                r = f"langGoInt({r})"
        return f"{l} {x.op} {r}"

    def emit_call(self, x: I.Call) -> str:
        if x.builtin:
            if x.builtin == "http.get":
                args = [self.expr(a) for a in x.args]
                return f"langGoHTTP.get({', '.join(args)})"
            bf = _BUILTIN_MAP.get(x.builtin)
            if bf is None:
                raise self.err(f"未知内建 {x.builtin}")
            return self.emit_builtin_call(x, bf)
        if x.fn is not None:
            fi = self.fns.get(x.fn.id)
            if fi is None:
                raise self.err(f"内部错误：fn 未注册 {x.fn.name}")
            args = []
            for i, a in enumerate(x.args):
                target = fi.params[i].typ if i < len(fi.params) else None
                arg = self.expr_arg(a, target)
                if i < len(fi.params):
                    arg = self.wrap_arg(fi.params[i].typ, a, arg)
                args.append(arg)
            return f"{self.go_name(fi)}({', '.join(args)})"
        if x.method is not None:
            obj = self.expr(x.recv)
            args = [self.expr(a) for a in x.args]
            return f"{obj}.{x.method.name}({', '.join(args)})"
        # 函数值（含 fnlit 绑定：按注册的参数类型收窄实参）
        if isinstance(x.callee, I.LocalGet) and x.callee.local.sym is not None:
            fi = self.fns.get(x.callee.local.sym.id)
            if fi is not None:
                args = []
                for i, a in enumerate(x.args):
                    arg = self.expr(a)
                    if i < len(fi.params):
                        arg = self.wrap_arg(fi.params[i].typ, a, arg)
                    args.append(arg)
                return f"{x.callee.local.name}({', '.join(args)})"
        callee = self.expr(x.callee)
        args = [self.expr(a) for a in x.args]
        return f"{callee}({', '.join(args)})"

    def emit_builtin_call(self, x: I.Call, bf: str) -> str:
        if bf == "langGoAssert":
            if len(x.args) < 1 or len(x.args) > 2:
                raise self.err("assert 需要 1~2 个参数")
            cond = self.expr(x.args[0])
            if self.gt(x.args[0]) != "bool":
                cond = f"langGoTruthy({cond})"
            if len(x.args) == 2:
                msg = self.expr(x.args[1])
                return f"langGoAssert({cond}, {msg})"
            return f"langGoAssert({cond})"
        if bf == "langGoPush":
            if len(x.args) < 2:
                raise self.err("push 需要至少 2 个参数")
            arr = self.expr(x.args[0])
            at = go_type_of(x.args[0].typ)
            if not at.startswith("*LangGoArr["):
                raise self.err("push 目标类型未知（→Go 需可推断的数组）")
            elem = at[len("*LangGoArr["):-1]
            vs = []
            for a in x.args[1:]:
                v = self.expr(a)
                if elem != "any" and self.gt(a) == "any":
                    v = f"langGoTo[{elem}]({v})"  # 带元素类型的数组在 push 边界收窄
                vs.append(v)
            return f"{arr}.Push({', '.join(vs)})"
        args = [self.expr(a) for a in x.args]
        return f"{bf}({', '.join(args)})"

    def expr_arg(self, e: I.Expr, target: Optional[Type]) -> str:
        """实参生成：None 无载荷无法推断，按调用方期望的 Option 元素类型特化。"""
        if (isinstance(e, I.Ctor) and e.sym.name == "None" and target is not None
                and target.kind == TK.OPTION and target.elem is not None
                and not _is_any(target.elem)):
            return f"LangGoNone[{go_type_of(target.elem)}]()"
        return self.expr(e)

    # ———— 表达式位置 match ————
    def expr_match(self, m: I.Match) -> str:
        """独立缓冲渲染成立即调用的函数字面量。"""
        ret_type = self.match_ret_type(m)
        saved_match_ret = self.match_ret
        self.match_ret = m.typ
        saved_buf = self.buf
        saved_indent = self.indent
        self.buf = []
        self.indent = 0
        tmp = self.new_tmp()
        try:
            scrut = self.expr(m.scrut)
            scrut = self.coerce_algebraic(scrut, m)
            self.w(f"func() {ret_type} {{")
            self.indent += 1
            self.w(f"{tmp} := {scrut}")
            if m.kind == I.MatchKind.RESULT:
                ok_arm, err_arm = split_algebraic(m, "Ok", "Err")
                self.w(f"if {tmp}.ok {{")
                self.emit_result_arm_return(ok_arm, tmp, ".v")
                self.w("} else {")
                self.emit_result_arm_return(err_arm, tmp, ".e")
                self.w("}")
            elif m.kind == I.MatchKind.OPTION:
                some_arm, none_arm = split_algebraic(m, "Some", "None")
                self.w(f"if {tmp}.has {{")
                self.emit_result_arm_return(some_arm, tmp, ".v")
                if none_arm is not None:
                    self.w("} else {")
                    self.emit_result_arm_return(none_arm, tmp, ".e")
                self.w("}")
            elif m.kind == I.MatchKind.ENUM:
                self.w(f"switch {tmp}.Tag {{")
                self.indent += 1
                has_default = False
                for arm in m.arms:
                    if isinstance(arm.pat, I.CtorPat):
                        self.w(f'case {_go_quote(arm.pat.sym.name)}:')
                        self.indent += 1
                        self.bind_enum_payloads(arm.pat, tmp)
                        self.emit_arm_return(arm)
                        self.indent -= 1
                    else:
                        has_default = True
                        self.w("default:")
                        self.indent += 1
                        if isinstance(arm.pat, I.BindPat):
                            self.w(f"{arm.pat.name} := {tmp}")
                            self.w(f"_ = {arm.pat.name}")
                        self.emit_arm_return(arm)
                        self.indent -= 1
                if not has_default:
                    # 编译期穷尽检查已保证不可达；仅为满足 Go 的 missing return
                    self.w("default:")
                    self.indent += 1
                    self.w('panic("match 无匹配分支")')
                    self.indent -= 1
                self.indent -= 1
                self.w("}")
            else:
                self.emit_literal_match_return(m, tmp)
            self.indent -= 1
            self.w("}()")
            out = "".join(self.buf)
        except Exception:
            self.buf = saved_buf
            self.indent = saved_indent
            self.match_ret = saved_match_ret
            raise
        # 恢复外层缓冲
        self.buf = saved_buf
        self.indent = saved_indent
        self.match_ret = saved_match_ret
        # 表达式片段可能在求值期间向「外层语句位置」发语句（当前实现不会）；
        # 若片段内含语句级展开，已随片段渲染
        return out.rstrip("\n")

    def emit_result_arm_return(self, arm: Optional[I.Arm], tmp: str, field: str) -> None:
        if arm is None:
            return
        self.indent += 1
        name = arm_bind_name(arm)
        if name != "_":
            self.w(f"{name} := {tmp}{field}")
            self.w(f"_ = {name}")
        self.emit_arm_return(arm)
        self.indent -= 1

    def wrap_to(self, expr: str, body: I.Expr, target: Optional[Type]) -> str:
        """any 臂体收窄到 match 的结果类型（Go 需要一致返回类型）。"""
        if target is not None and not _is_any(target) and self.gt(body) == "any":
            return f"langGoTo[{go_type_of(target)}]({expr})"
        return expr

    def emit_literal_match_return(self, m: I.Match, tmp: str) -> None:
        for i, arm in enumerate(m.arms):
            if isinstance(arm.pat, I.LitPat):
                kw = "if" if i == 0 else "} else if"
                lit = self.expr(arm.pat.val)
                self.w(f"{kw} langGoEq({tmp}, {lit}) {{")
                self.indent += 1
                self.emit_arm_return(arm)
                self.indent -= 1
            elif isinstance(arm.pat, I.BindPat):
                self.w("} else {")
                self.indent += 1
                self.w(f"{arm.pat.name} := {tmp}")
                self.w(f"_ = {arm.pat.name}")
                self.emit_arm_return(arm)
                self.indent -= 1
            elif isinstance(arm.pat, I.WildPat):
                self.w("} else {")
                self.indent += 1
                self.emit_arm_return(arm)
                self.indent -= 1
        self.w("}")

    def emit_arm_return(self, arm: I.Arm) -> None:
        expr = self.expr(arm.body)
        expr = self.wrap_to(expr, arm.body, self.match_ret)
        self.w(f"return {expr}")

    # ———— 函数字面量 ————
    def emit_fn_lit(self, x: I.FnLit) -> str:
        ps = [f"{p.name} {go_type_of(p.typ)}" for p in x.params]
        ret_decl = ""
        if x.ret is not None:
            ret_decl = " " + go_type_of(x.ret)
        saved_fn = self.cur_fn
        self.cur_fn = I.Fn(name="匿名函数", sym=None, recv=None, type_params=[],
                           params=[], ret=x.ret, locals=[], body=[])
        saved_buf = self.buf
        saved_indent = self.indent
        self.buf = []
        self.indent = 0
        try:
            self.w(f"func({', '.join(ps)}){ret_decl} {{")
            for st in x.body:
                self.emit_stmt(st)
            self.w("}")
            out = "".join(self.buf)
        finally:
            self.buf = saved_buf
            self.indent = saved_indent
            self.cur_fn = saved_fn
        # 内嵌进实参表时不能带尾随换行
        return out.rstrip("\n")


def split_algebraic(m: I.Match, pos_name: str, neg_name: str):
    pos = neg = None
    for arm in m.arms:
        if isinstance(arm.pat, I.CtorPat):
            if arm.pat.sym.name == pos_name:
                pos = arm
            elif arm.pat.sym.name == neg_name:
                neg = arm
    return pos, neg


def arm_bind_name(arm: I.Arm) -> str:
    p = arm.pat
    if isinstance(p, I.CtorPat) and len(p.subs) == 1 and isinstance(p.subs[0], I.BindPat):
        return p.subs[0].name
    return "_"


def transpile(p: I.Prog) -> str:
    """IR → Go 源码（自包含，package main）。"""
    g = GoGen(p)
    g.emit_program()
    return "".join(g.buf)
