"""→JS 转译后端（genjs.go 语义等价）：Core IR → 自包含可执行 ES Module。

目标平台映射：int=BigInt（64 位语义精确）、全函数 async（await-everything 吸收染色）、
struct 值语义经 langGoCopy 三时机（绑定/传参/返回）。go 语句为同步立即执行
（单线程确定子集）。prelude 见资源文件 js_prelude.mjs.txt。
"""

from __future__ import annotations

import os
from typing import Optional

from ..errors import LangError
from ..ir import nodes as I
from ..typesys.model import TK

_PRELUDE_PATH = os.path.join(os.path.dirname(__file__), "js_prelude.mjs.txt")

with open(_PRELUDE_PATH, "r", encoding="utf-8") as _f:
    JS_PRELUDE = _f.read()

_BUILTINS = {
    "println": "langGoPrintln", "print": "langGoPrint", "len": "langGoLen",
    "str": "langGoStr", "int": "langGoInt", "float": "langGoFloat",
    "keys": "langGoKeys", "values": "langGoValues", "has": "langGoHas",
    "push": "langGoPush", "assert": "langGoAssert", "err": "langGoErr",
    "bytes": "langGoBytes", "char": "langGoChar",
    "jsonParse": "langGoJSONParse", "jsonStringify": "langGoJSONStringify",
    "isNone": "langGoIsNone", "makeChan": "langGoNewChan",
    "send": "langGoChanSend", "recv": "langGoChanRecv",
}


def _js_quote(s: str) -> str:
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


class JsGen:
    def __init__(self, p: I.Prog) -> None:
        self.p = p
        self.buf: list[str] = []
        self.indent = 0
        self.structs = {s.name: s for s in p.structs}
        self.case_enum: dict[str, str] = {}
        for e in p.enums:
            for c in e.cases:
                self.case_enum[c.name] = e.name
        self.fns: dict[int, I.Fn] = {}
        self.tmp_n = 0
        self.cur_fn: Optional[I.Fn] = None
        for f in p.fns:
            if f.sym is not None:
                self.fns[f.sym.id] = f
            self._collect_nested(f.body)

    def _collect_nested(self, stmts: list[I.Stmt]) -> None:
        for s in stmts:
            if isinstance(s, I.FnDef):
                if s.fn.sym is not None:
                    self.fns[s.fn.sym.id] = s.fn
                self._collect_nested(s.fn.body)

    def err(self, msg: str) -> LangError:
        return LangError("→JS 转译失败: " + msg)

    def new_tmp(self) -> str:
        self.tmp_n += 1
        return f"_t{self.tmp_n}"

    def w(self, line: str) -> None:
        self.buf.append("  " * self.indent + line + "\n")

    def fn_name(self, f: I.Fn) -> str:
        if f.recv is not None:
            recv_name = "unknown"
            if f.recv.typ is not None and f.recv.typ.kind == TK.STRUCT and f.recv.typ.sym is not None:
                recv_name = f.recv.typ.sym.name
            return f"lf_{recv_name}_{f.name}"
        return f"lf_{f.name}"

    # ———— 程序 ————
    def emit_program(self) -> None:
        self.buf.append(JS_PRELUDE)
        for en in self.p.enums:
            for c in en.cases:
                params = [f"p{i}" for i in range(len(c.payloads))]
                vals = "[" + ", ".join(params) + "]" if params else "[]"
                self.buf.append(
                    f"function le_{en.name}_{c.name}({', '.join(params)}) "
                    f"{{ return langGoEnumVal({_js_quote(c.name)}, {vals}); }}\n\n")
        for gl in self.p.globals:
            expr = self.expr(gl.init)
            self.buf.append(f"let {gl.local.name} = langGoCopy({expr});\n\n")
        for f in self.p.fns:
            self.emit_func(f)
        if self.p.main is not None:
            self.buf.append("await lf_main();\n")

    # ———— 函数 ————
    def emit_func(self, f: I.Fn) -> None:
        self.cur_fn = f
        ps = []
        if f.recv is not None:
            ps.append(f.recv.name)
        for p in f.params:
            ps.append(p.name)
        self.buf.append(f"async function {self.fn_name(f)}({', '.join(ps)}) {{\n")
        # 值语义：参数进入函数体即拷贝
        self.indent = 1
        if f.recv is not None:
            self.buf.append(f"  {f.recv.name} = langGoCopy({f.recv.name});\n")
        for p in f.params:
            self.buf.append(f"  {p.name} = langGoCopy({p.name});\n")
        self.emit_body(f.body)
        self.indent = 0
        self.cur_fn = None
        self.buf.append("}\n\n")

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
                expr = self.expr(s.x)
                self.w(f"await {expr};")
                return
            expr = self.expr(s.x)
            self.w(expr + ";")
        elif isinstance(s, I.Return):
            self.emit_return(s)
        elif isinstance(s, I.If):
            cond = self.expr(s.cond)
            self.w(f"if ({cond}) {{")
            self.emit_block(s.then)
            if s.els:
                self.w("} else {")
                self.emit_block(s.els)
            self.w("}")
        elif isinstance(s, I.While):
            cond = self.expr(s.cond)
            self.w(f"while ({cond}) {{")
            self.emit_block(s.body)
            self.w("}")
        elif isinstance(s, I.ForIn):
            self.emit_for_in(s)
        elif isinstance(s, I.Break):
            self.w("break")
        elif isinstance(s, I.Continue):
            self.w("continue")
        elif isinstance(s, I.Spawn):
            # →JS 单线程：go 同步立即执行（顺序确定）
            fi = self.fns.get(s.fn.id) if s.fn is not None else None
            if fi is None:
                raise self.err(f"内部错误：go 目标未注册 {s.fn.name if s.fn else '?'}")
            args = [f"langGoCopy({self.expr(a)})" for a in s.args]
            self.w(f"await {self.fn_name(fi)}({', '.join(args)});")
        elif isinstance(s, I.FnDef):
            # 嵌套 fn：JS 提升，递归可用（独立缓冲渲染）
            fn = s.fn
            saved_fn = self.cur_fn
            self.cur_fn = fn
            ps = []
            if fn.recv is not None:
                ps.append(fn.recv.name)
            for p in fn.params:
                ps.append(p.name)
            saved_buf = self.buf
            saved_indent = self.indent
            self.buf = []
            self.indent = 0
            self.buf.append(f"async function {self.fn_name(fn)}({', '.join(ps)}) {{\n")
            self.indent = 1
            if fn.recv is not None:
                self.buf.append(f"  {fn.recv.name} = langGoCopy({fn.recv.name});\n")
            for p in fn.params:
                self.buf.append(f"  {p.name} = langGoCopy({p.name});\n")
            self.emit_body(fn.body)
            self.buf.append("}\n")
            self.cur_fn = saved_fn
            self.indent = saved_indent
            out = "".join(self.buf)
            self.buf = saved_buf
            self.w(out.rstrip("\n"))
        else:
            raise self.err("→JS 暂不支持该语句")

    def emit_let(self, st: I.Let) -> None:
        name = st.local.name
        if isinstance(st.init, I.Try):
            tmp = self.gen_try(st.init)
            if name != "_":
                self.w(f"let {name} = langGoCopy({tmp}.v);")
            return
        expr = self.expr(st.init)
        if name == "_":
            self.w(expr + ";")
            return
        if _is_ref(st.init):
            self.w(f"let {name} = {expr};")  # & 引用赋值：不拷贝
        else:
            self.w(f"let {name} = langGoCopy(await {expr});")

    def emit_assign(self, st: I.Assign) -> None:
        if st.op != I.OP_ASSIGN:
            self.emit_compound_assign(st)
            return
        tgt = st.target
        if isinstance(tgt, I.LocalGet):
            if isinstance(st.val, I.Try):
                tmp = self.gen_try(st.val)
                self.w(f"{tgt.local.name} = langGoCopy({tmp}.v);")
                return
            expr = self.expr(st.val)
            if _is_ref(st.val):
                self.w(f"{tgt.local.name} = {expr};")
            else:
                self.w(f"{tgt.local.name} = langGoCopy(await {expr});")
        elif isinstance(tgt, I.Field):
            obj = self.expr(tgt.obj)
            expr = self.expr(st.val)
            self.w(f"{obj}.{tgt.name} = langGoCopy(await {expr});")
        elif isinstance(tgt, I.Index):
            obj = self.expr(tgt.obj)
            idx = self.expr(tgt.idx)
            expr = self.expr(st.val)
            self.w(f"langGoSetIndex({obj}, {idx}, langGoCopy(await {expr}));")
        else:
            raise self.err("赋值目标不合法")

    def emit_compound_assign(self, st: I.Assign) -> None:
        op = st.op
        tgt = st.target
        if isinstance(tgt, I.LocalGet):
            expr = self.expr(st.val)
            if st.op == I.OP_PLUS_A:
                # 数组 += 是元素拼接；数值/字符串原生 += 对 BigInt/number/string 均正确
                self.w(f"if (Array.isArray({tgt.local.name})) "
                       f"{{ {tgt.local.name}.push(...langGoIter({expr})) }} "
                       f"else {{ {tgt.local.name} {op} {expr} }}")
                return
            self.w(f"{tgt.local.name} {op} {expr};")
        elif isinstance(tgt, I.Field):
            obj = self.expr(tgt.obj)
            expr = self.expr(st.val)
            self.w(f"{obj}.{tgt.name} {op} {expr};")
        elif isinstance(tgt, I.Index):
            obj = self.expr(tgt.obj)
            idx = self.expr(tgt.idx)
            expr = self.expr(st.val)
            self.w(f"langGoAddIndex({obj}, {idx}, {expr}, {_js_quote(op)})")
        else:
            raise self.err("赋值目标不合法")

    def emit_return(self, st: I.Return) -> None:
        if st.val is None:
            self.w("return;")
            return
        if isinstance(st.val, I.Try):
            tmp = self.gen_try(st.val)
            self.w(f"return langGoCopy({tmp}.v);")
            return
        if self.cur_fn is not None and self.cur_fn.ret is not None and self.cur_fn.ret.kind == TK.RESULT:
            # Result 函数的 return 必为 Ok(...)/Err(...)（checker 已约束）
            ctor = st.val
            if not isinstance(ctor, I.Ctor) or ctor.sym.name not in ("Ok", "Err") or len(ctor.args) != 1:
                raise self.err("内部错误：Result return 形态异常")
            expr = self.expr(ctor.args[0])
            if ctor.sym.name == "Ok":
                self.w(f"return {{ __langGoRes: true, ok: true, v: langGoCopy(await {expr}), e: undefined }};")
            else:
                self.w(f"return {{ __langGoRes: true, ok: false, v: undefined, e: langGoCopy({expr}) }};")
            return
        expr = self.expr(st.val)
        self.w(f"return langGoCopy(await {expr});")

    def gen_try(self, t: I.Try) -> str:
        if self.cur_fn is None or self.cur_fn.ret is None or self.cur_fn.ret.kind != TK.RESULT:
            raise self.err("带 ? 的函数须注解为 Result[T, error]")
        tmp = self.new_tmp()
        expr = self.expr(t.x)
        self.w(f"const {tmp} = await {expr};")
        self.w(f"if (!{tmp}.ok) {{")
        self.indent += 1
        self.w(f"return {{ __langGoRes: true, ok: false, v: undefined, e: {tmp}.e }};")
        self.indent -= 1
        self.w("}")
        return tmp

    def emit_for_in(self, st: I.ForIn) -> None:
        name = st.var.name
        if st.kind == I.IterKind.RANGE:
            # 区间字面量与经绑定变量两条路径（for-in 区间半开）
            if isinstance(st.iter, I.Range):
                lo = self.expr(st.iter.lo)
                hi = self.expr(st.iter.hi)
                self.w(f"for (let {name} = {lo}; {name} < {hi}; {name}++) {{")
                self.emit_block_typed(st.body, name)
                self.w("}")
                return
            rv = self.expr(st.iter)
            tmp = self.new_tmp()
            self.w(f"const {tmp} = {rv};")
            self.w(f"for (let {name} = {tmp}.lo; {name} < {tmp}.hi; {name}++) {{")
            self.emit_block_typed(st.body, name)
            self.w("}")
            return
        it = self.expr(st.iter)
        self.w(f"for (let {name} of langGoIter({it})) {{")
        self.emit_block_typed(st.body, name)
        self.w("}")

    def emit_block_typed(self, stmts: list[I.Stmt], name: str) -> None:
        self.indent += 1
        self.w(f"{name} = langGoCopy({name});")
        self.emit_body(stmts)
        self.indent -= 1

    def emit_block(self, stmts: list[I.Stmt]) -> None:
        self.indent += 1
        self.emit_body(stmts)
        self.indent -= 1

    # ———— match ————
    def emit_match_stmt(self, m: I.Match) -> None:
        tmp = self.new_tmp()
        scrut = self.expr(m.scrut)
        self.w(f"const {tmp} = {scrut};")
        if m.kind == I.MatchKind.RESULT:
            ok_arm, err_arm = split_algebraic(m, "Ok", "Err")
            self.w(f"if ({tmp}.ok) {{")
            self.indent += 1
            if ok_arm is not None:
                self.bind_arm_value(ok_arm, f"{tmp}.v")
                self.emit_arm_stmt(ok_arm)
            self.indent -= 1
            self.w("} else {")
            self.indent += 1
            if err_arm is not None:
                self.bind_arm_value(err_arm, f"{tmp}.e")
                self.emit_arm_stmt(err_arm)
            self.indent -= 1
            self.w("}")
        elif m.kind == I.MatchKind.OPTION:
            some_arm, none_arm = split_algebraic(m, "Some", "None")
            self.w(f"if ({tmp}.has) {{")
            self.indent += 1
            if some_arm is not None:
                self.bind_arm_value(some_arm, f"{tmp}.v")
                self.emit_arm_stmt(some_arm)
            self.indent -= 1
            if none_arm is not None:
                self.w("} else {")
                self.indent += 1
                self.emit_arm_stmt(none_arm)
                self.indent -= 1
            self.w("}")
        elif m.kind == I.MatchKind.ENUM:
            self.w(f"switch ({tmp}.tag) {{")
            self.indent += 1
            has_default = False
            for arm in m.arms:
                if isinstance(arm.pat, I.CtorPat):
                    self.w(f'case {_js_quote(arm.pat.sym.name)}: {{')
                    self.indent += 1
                    self.bind_enum_payloads(arm.pat, tmp)
                    self.emit_arm_stmt(arm)
                    self.indent -= 1
                    self.w("}")
                else:
                    has_default = True
                    self.w("default: {")
                    self.indent += 1
                    if isinstance(arm.pat, I.BindPat):
                        self.w(f"const {arm.pat.name} = {tmp};")
                    self.emit_arm_stmt(arm)
                    self.indent -= 1
                    self.w("}")
            if not has_default:
                self.w('default: throw new Error("match 无匹配分支");')
            self.indent -= 1
            self.w("}")
        else:
            self.emit_literal_match_stmt(m, tmp)

    def bind_arm_value(self, arm: I.Arm, value_expr: str) -> None:
        name = arm_bind_name(arm)
        if name != "_":
            self.w(f"const {name} = langGoCopy({value_expr});")

    def bind_enum_payloads(self, p: I.CtorPat, tmp: str) -> None:
        for j, sp in enumerate(p.subs):
            if isinstance(sp, I.BindPat):
                self.w(f"const {sp.name} = langGoCopy({tmp}.vals[{j}]);")

    def emit_arm_stmt(self, arm: I.Arm) -> None:
        expr = self.expr(arm.body)
        self.w(expr + ";")

    def emit_literal_match_stmt(self, m: I.Match, tmp: str) -> None:
        for i, arm in enumerate(m.arms):
            if isinstance(arm.pat, I.LitPat):
                kw = "if" if i == 0 else "} else if"
                lit = self.expr(arm.pat.val)
                self.w(f"{kw} (langGoEq({tmp}, {lit})) {{")
                self.emit_block([I.Eval(x=arm.body)])
            elif isinstance(arm.pat, I.BindPat):
                self.w("} else {")
                self.indent += 1
                self.w(f"const {arm.pat.name} = {tmp};")
                self.emit_arm_stmt(arm)
                self.indent -= 1
            elif isinstance(arm.pat, I.WildPat):
                self.w("} else {")
                self.emit_block([I.Eval(x=arm.body)])
        self.w("}")

    # ———— 表达式 ————
    def expr(self, e: I.Expr) -> str:
        if isinstance(e, I.Const):
            if e.kind == I.ConstKind.INT:
                return f"{e.i64}n"
            if e.kind == I.ConstKind.FLOAT:
                from ..interp.values import format_g
                return format_g(e.f64)
            if e.kind == I.ConstKind.STR:
                return _js_quote(e.s)
            return "true" if e.b else "false"
        if isinstance(e, I.LocalGet):
            return e.local.name
        if isinstance(e, I.FnGet):
            fi = self.fns.get(e.sym.id) if e.sym is not None else None
            if fi is not None:
                return self.fn_name(fi)
            return f"lf_{e.name}"
        if isinstance(e, I.BuiltinGet):
            raise self.err(f"内建 {e.name} 须直接调用")
        if isinstance(e, I.Ctor):
            return self.emit_ctor(e)
        if isinstance(e, I.Call):
            return self.emit_call(e)
        if isinstance(e, I.Unary):
            inner = self.expr(e.x)
            if e.op == I.OP_NEG:
                return f"-{inner}"
            if e.op == I.OP_NOT:
                return f"!{inner}"
            if e.op == I.OP_REF:
                # JS 对象赋值即共享引用，与 lang-go 的 & 语义天然对齐
                return inner
            raise self.err("未知一元运算")
        if isinstance(e, I.Binary):
            return self.emit_binary(e)
        if isinstance(e, I.Range):
            lo = self.expr(e.lo)
            hi = self.expr(e.hi)
            return f"{{ __langGoRange: true, lo: {lo}, hi: {hi} }}"
        if isinstance(e, I.Index):
            obj = self.expr(e.obj)
            idx = self.expr(e.idx)
            return f"langGoIndex({obj}, {idx})"
        if isinstance(e, I.Slice):
            obj = self.expr(e.obj)
            lo = self.expr(e.lo)
            hi = self.expr(e.hi)
            return f"langGoSlice({obj}, {lo}, {hi})"
        if isinstance(e, I.Field):
            obj = self.expr(e.obj)
            return f"{obj}.{e.name}"
        if isinstance(e, I.Try):
            raise self.err("? 只能出现在 let/return/赋值的顶层（→JS 约束）")
        if isinstance(e, I.StructLit):
            name = e.struct.name if e.struct is not None else ""
            fs = [f"__langGoStruct: {_js_quote(name)}"]
            for f in e.fields:
                fs.append(f"{f.name}: langGoCopy({self.expr(f.val)})")
            return "{ " + ", ".join(fs) + " }"
        if isinstance(e, I.MapLit):
            kvs = []
            for k_e, v_e in zip(e.keys, e.vals):
                kvs.append(f"[{self.expr(k_e)}, langGoCopy({self.expr(v_e)})]")
            return f"langGoNewMap({', '.join(kvs)})"
        if isinstance(e, I.SliceLit):
            items = [f"langGoCopy({self.expr(el)})" for el in e.elems]
            return "[" + ", ".join(items) + "]"
        if isinstance(e, I.Match):
            return self.expr_match(e)
        if isinstance(e, I.FnLit):
            return self.emit_fn_lit(e)
        raise self.err("→JS 暂不支持该表达式")

    def emit_ctor(self, x: I.Ctor) -> str:
        if x.sym.name == "None":
            return "{ __langGoOpt: true, has: false, v: undefined }"
        if x.sym.name == "Some":
            if len(x.args) != 1:
                raise self.err("Some 需要 1 个参数")
            return f"{{ __langGoOpt: true, has: true, v: langGoCopy({self.expr(x.args[0])}) }}"
        if x.sym.name in ("Ok", "Err"):
            raise self.err("Ok/Err 只能出现在 Result 函数的 return（→JS 约束）")
        en = self.case_enum.get(x.sym.name, "")
        args = [f"langGoCopy({self.expr(a)})" for a in x.args]
        return f"le_{en}_{x.sym.name}({', '.join(args)})"

    def emit_binary(self, x: I.Binary) -> str:
        l = self.expr(x.l)
        r = self.expr(x.r)
        if x.op == I.OP_ANDAND:
            return f"({l} && {r})"
        if x.op == I.OP_OROR:
            return f"({l} || {r})"
        if x.op == I.OP_EQ:
            return f"langGoEq({l}, {r})"
        if x.op == I.OP_NEQ:
            return f"!langGoEq({l}, {r})"
        if x.op == I.OP_SLASH:
            return f"langGoDiv({l}, {r})"
        return f"({l} {x.op} {r})"

    def emit_call(self, x: I.Call) -> str:
        if x.builtin:
            if x.builtin == "http.get":
                args = [self.expr(a) for a in x.args]
                return f"langGoHTTP.get({', '.join(args)})"
            bf = _BUILTINS.get(x.builtin)
            if bf is None:
                raise self.err(f"未知内建 {x.builtin}")
            args = []
            for a in x.args:
                arg = self.expr(a)
                if x.builtin == "push":
                    arg = f"langGoCopy({arg})"
                args.append(arg)
            return f"{bf}({', '.join(args)})"
        if x.method is not None:
            obj = self.expr(x.recv)
            fi = self.fns.get(x.method.id)
            if fi is None:
                raise self.err(f"内部错误：方法未注册 {x.method.name}")
            args = [f"langGoCopy({self.expr(a)})" for a in x.args]
            return f"await {self.fn_name(fi)}({obj}, {', '.join(args)})"
        if x.fn is not None:
            fi = self.fns.get(x.fn.id)
            if fi is None:
                raise self.err(f"内部错误：fn 未注册 {x.fn.name}")
            args = [f"langGoCopy({self.expr(a)})" for a in x.args]
            return f"await {self.fn_name(fi)}({', '.join(args)})"
        # 函数值（fnlit 绑定直通；字段函数值经 obj.f）
        if isinstance(x.callee, I.LocalGet):
            callee = x.callee.local.name
        else:
            callee = self.expr(x.callee)
        args = [self.expr(a) for a in x.args]
        return f"await {callee}({', '.join(args)})"

    # ———— 表达式位置 match ————
    def expr_match(self, m: I.Match) -> str:
        """立即调用的 async 函数（片段自带 await）。"""
        saved_buf = self.buf
        saved_indent = self.indent
        self.buf = []
        self.indent = 1
        tmp = self.new_tmp()
        try:
            scrut = self.expr(m.scrut)
            self.w("(async () => {")
            self.indent += 1
            self.w(f"const {tmp} = {scrut};")
            if m.kind == I.MatchKind.RESULT:
                ok_arm, err_arm = split_algebraic(m, "Ok", "Err")
                self.w(f"if ({tmp}.ok) {{")
                self.indent += 1
                if ok_arm is not None:
                    self.bind_arm_value(ok_arm, f"{tmp}.v")
                    self.emit_arm_return(ok_arm)
                self.indent -= 1
                self.w("} else {")
                self.indent += 1
                if err_arm is not None:
                    self.bind_arm_value(err_arm, f"{tmp}.e")
                    self.emit_arm_return(err_arm)
                self.indent -= 1
                self.w("}")
            elif m.kind == I.MatchKind.OPTION:
                some_arm, none_arm = split_algebraic(m, "Some", "None")
                self.w(f"if ({tmp}.has) {{")
                self.indent += 1
                if some_arm is not None:
                    self.bind_arm_value(some_arm, f"{tmp}.v")
                    self.emit_arm_return(some_arm)
                self.indent -= 1
                if none_arm is not None:
                    self.w("} else {")
                    self.indent += 1
                    self.emit_arm_return(none_arm)
                    self.indent -= 1
                    self.w("}")
                else:
                    self.w("}")
            elif m.kind == I.MatchKind.ENUM:
                self.w(f"switch ({tmp}.tag) {{")
                self.indent += 1
                for arm in m.arms:
                    if isinstance(arm.pat, I.CtorPat):
                        self.w(f'case {_js_quote(arm.pat.sym.name)}: {{')
                        self.indent += 1
                        self.bind_enum_payloads(arm.pat, tmp)
                        self.emit_arm_return(arm)
                        self.indent -= 1
                        self.w("}")
                    else:
                        self.w("default: {")
                        self.indent += 1
                        if isinstance(arm.pat, I.BindPat):
                            self.w(f"const {arm.pat.name} = {tmp};")
                        self.emit_arm_return(arm)
                        self.indent -= 1
                        self.w("}")
                self.indent -= 1
                self.w("}")
            else:
                self.emit_literal_match_return(m, tmp)
            self.indent -= 1
            self.w("})()")
            out = "".join(self.buf)
        finally:
            self.buf = saved_buf
            self.indent = saved_indent
        # 片段自带 await：match 可出现在任意嵌套表达式位置
        return "await " + out

    def emit_literal_match_return(self, m: I.Match, tmp: str) -> None:
        for i, arm in enumerate(m.arms):
            if isinstance(arm.pat, I.LitPat):
                kw = "if" if i == 0 else "} else if"
                lit = self.expr(arm.pat.val)
                self.w(f"{kw} (langGoEq({tmp}, {lit})) {{")
                self.indent += 1
                self.emit_arm_return(arm)
                self.indent -= 1
            elif isinstance(arm.pat, I.BindPat):
                self.w("} else {")
                self.indent += 1
                self.w(f"const {arm.pat.name} = {tmp};")
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
        self.w(f"return langGoCopy(await {expr});")

    # ———— 函数字面量 ————
    def emit_fn_lit(self, x: I.FnLit) -> str:
        ps = [p.name for p in x.params]
        saved_fn = self.cur_fn
        self.cur_fn = I.Fn(name="匿名函数", sym=None, recv=None, type_params=[],
                           params=[], ret=x.ret, locals=[], body=[])
        saved_buf = self.buf
        saved_indent = self.indent
        self.buf = []
        self.indent = 0
        try:
            self.w(f"async function({', '.join(ps)}) {{")
            self.indent += 1
            for p in x.params:
                self.w(f"{p.name} = langGoCopy({p.name});")
            for st in x.body:
                self.emit_stmt(st)
            self.w("}")
            out = "".join(self.buf)
        finally:
            self.buf = saved_buf
            self.indent = saved_indent
            self.cur_fn = saved_fn
        return out.rstrip("\n")


def _is_ref(e: I.Expr) -> bool:
    return isinstance(e, I.Unary) and e.op == I.OP_REF


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


def transpile_js(p: I.Prog) -> str:
    """IR → JS 源码（自包含 ESM）。"""
    g = JsGen(p)
    g.emit_program()
    return "".join(g.buf)
