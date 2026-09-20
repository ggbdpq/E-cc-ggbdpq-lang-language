"""Core IR 树遍历解释器（v0.9 Semantic Integrity；go/internal/irinterp 语义等价）。

定位（specs/README 真值优先级）：AST 解释器（lang_py.interp）是参考语义 Oracle；
本机器消费与 →Go/→JS 后端相同的 Core IR，用于四路差分验证——证明「IR 语义 = AST 语义」。
职责边界：不做名字解析、类型推断或语义校验（结论已固化在 IR 节点上）。
值模型复用 interp 的值类型与格式化（RUNTIME-OUTPUT-003 禁止第二份实现）；
内建经 Interp.call_builtin 复用同一套实现。预载 Option/Result/error 在本包重建
（字段与 interp.prelude 同名同序），判定一律走 Def.Name/Case 序。
"""

from __future__ import annotations

import io
from typing import IO, Optional

from ..errors import (LangRuntimeError, SignalBreak, SignalContinue,
                      SignalPropagateErr, SignalReturn)
from ..interp.interp import Interp as HostInterp
from ..interp.values import (ArrVal, EnumDef, EnumVal, MapVal, PtrVal, RangeVal,
                             StructDef, StructVal, UNIT, UnitVal, copy_value,
                             fmt_val, i64, i64_div, i64_mod, truthy)
from ..ir import nodes as I
from ..ir.nodes import OP_AND, OP_BIT_OR, OP_MINUS, OP_MINUS_A, OP_NOT, OP_PLUS, \
    OP_PLUS_A, OP_PERCENT, OP_SHL, OP_SHR, OP_SLASH, OP_SLASH_A, OP_STAR, \
    OP_STAR_A, OP_XOR


class _Frame:
    """局部槽位帧。槽位为单元素 list（写槽/读格）。"""
    __slots__ = ("vars", "fns", "parent")

    def __init__(self, parent: Optional["_Frame"]) -> None:
        self.vars: dict[int, list] = {}
        self.fns: dict[str, I.Fn] = {}
        self.parent = parent

    def slot(self, l: I.Local) -> Optional[list]:
        cur: Optional[_Frame] = self
        while cur is not None:
            c = cur.vars.get(id(l))
            if c is not None:
                return c
            cur = cur.parent
        return None

    def lookup_fn(self, name: str) -> Optional[I.Fn]:
        cur: Optional[_Frame] = self
        while cur is not None:
            fn = cur.fns.get(name)
            if fn is not None:
                return fn
            cur = cur.parent
        return None


class _FnVal:
    """函数值：顶层 fn / 匿名 FnLit 二选一。"""
    __slots__ = ("fn", "lit", "env", "name")

    def __init__(self, fn: Optional[I.Fn] = None, lit: Optional[I.FnLit] = None,
                 env: Optional[_Frame] = None, name: str = "") -> None:
        self.fn = fn
        self.lit = lit
        self.env = env
        self.name = name


class _BuiltinVal:
    __slots__ = ("name",)

    def __init__(self, name: str) -> None:
        self.name = name


def _deref(v):
    if isinstance(v, PtrVal):
        return v.cell[0]
    return v


class Machine:
    """IR 解释器。"""

    def __init__(self, out: IO) -> None:
        self.out = out
        self.host = HostInterp(io.StringIO())  # 内建值构造上下文（CallBuiltin 复用）
        self.fns: dict[str, I.Fn] = {}
        self.methods: dict[str, I.Fn] = {}  # structName.method → fn
        self.ctors: dict[str, tuple[EnumDef, int]] = {}
        self.structs: dict[str, StructDef] = {}
        self.enums: dict[str, EnumDef] = {}
        self.globals: Optional[_Frame] = None
        self.global_slots: dict[int, list] = {}  # Symbol.id → 槽
        # 预载代数类型与 error（RESULT-PRELUDE-001；与 interp.prelude 同名同序）
        opt = EnumDef("Option", ["Some", "None"])
        res = EnumDef("Result", ["Ok", "Err"])
        self.enums["Option"] = opt
        self.enums["Result"] = res
        self.ctors["Some"] = (opt, 0)
        self.ctors["None"] = (opt, 1)
        self.ctors["Ok"] = (res, 0)
        self.ctors["Err"] = (res, 1)
        self.structs["error"] = StructDef("error", ["msg"])

    # ———— 入口 ————
    def run(self, p: I.Prog) -> None:
        """执行程序：声明注册 → 全局初始化（MODULE-INIT-005 合并序）→ 调 main。"""
        self.globals = _Frame(None)
        for sd in p.structs:
            self.structs[sd.name] = StructDef(sd.name, [f.name for f in sd.fields])
        for ed in p.enums:
            edef = EnumDef(ed.name, [c.name for c in ed.cases])
            self.enums[ed.name] = edef
            for i, c in enumerate(ed.cases):
                self.ctors[c.name] = (edef, i)
        for fn in p.fns:
            if fn.recv is not None:
                recv_name = fn.recv.typ.sym.name if fn.recv.typ is not None and fn.recv.typ.sym else "unknown"
                self.methods[f"{recv_name}.{fn.name}"] = fn
                continue
            self.fns[fn.name] = fn
        for g in p.globals:
            v = self.eval(g.init, self.globals)
            cell = [copy_value(v)]
            self.globals.vars[id(g.local)] = cell
            if g.local.sym is not None:
                self.global_slots[g.local.sym.id] = cell
        if p.main is not None:
            self.call_fn(p.main, None, [])

    # ———— 槽位 ————
    def _global_slot(self, l: I.Local) -> Optional[list]:
        if l.sym is not None:
            return self.global_slots.get(l.sym.id)
        return None

    def _slot_for(self, l: I.Local, f: _Frame) -> Optional[list]:
        p = f.slot(l)
        if p is None:
            return self._global_slot(l)
        return p

    # ———— 语句 ————
    def _exec_stmts(self, stmts: list[I.Stmt], f: _Frame) -> None:
        for st in stmts:
            self._exec(st, f)

    def _exec(self, st: I.Stmt, f: _Frame) -> None:
        if isinstance(st, I.Let):
            v = self.eval(st.init, f)
            f.vars[id(st.local)] = [copy_value(v)]
        elif isinstance(st, I.Assign):
            self._exec_assign(st, f)
        elif isinstance(st, I.Eval):
            self.eval(st.x, f)
        elif isinstance(st, I.Return):
            v = UNIT
            if st.val is not None:
                v = copy_value(self.eval(st.val, f))
            raise SignalReturn(v)
        elif isinstance(st, I.If):
            if truthy(self.eval(st.cond, f)):
                self._exec_stmts(st.then, _Frame(f))
            else:
                self._exec_stmts(st.els, _Frame(f))
        elif isinstance(st, I.While):
            while truthy(self.eval(st.cond, f)):
                try:
                    self._exec_stmts(st.body, _Frame(f))
                except SignalBreak:
                    break
                except SignalContinue:
                    continue
        elif isinstance(st, I.ForIn):
            self._exec_for_in(st, f)
        elif isinstance(st, I.Break):
            raise SignalBreak()
        elif isinstance(st, I.Continue):
            raise SignalContinue()
        elif isinstance(st, I.Spawn):
            # TASK-SPAWN-002 同步确定子集：实参就地求值后同步执行完
            args = [self.eval(a, f) for a in st.args]
            fn = self.fns.get(st.fn.name) if st.fn is not None else None
            if fn is None:
                raise LangRuntimeError(f"go 的目标未注册: {st.fn.name if st.fn else '?'}")
            self.call_fn(fn, None, args)
        elif isinstance(st, I.FnDef):
            f.fns[st.fn.name] = st.fn
        else:
            raise LangRuntimeError(f"irinterp: 未支持的语句 {type(st).__name__}")

    def _exec_for_in(self, st: I.ForIn, f: _Frame) -> None:
        it = self.eval(st.iter, f)
        if st.kind == I.IterKind.SLICE:
            if not isinstance(it, ArrVal):
                raise LangRuntimeError("该类型不可迭代")
            items = list(it.items)
        elif st.kind == I.IterKind.MAP:
            if not isinstance(it, MapVal):
                raise LangRuntimeError("该类型不可迭代")
            items = it.keys_snapshot()
        elif st.kind == I.IterKind.STRING:
            if not isinstance(it, str):
                raise LangRuntimeError("该类型不可迭代")
            items = list(it)
        elif st.kind == I.IterKind.RANGE:
            if not isinstance(it, RangeVal):
                raise LangRuntimeError("该类型不可迭代")
            items = list(range(it.lo, it.hi))
        else:
            raise LangRuntimeError("该类型不可迭代")
        for item in items:
            body = _Frame(f)
            body.vars[id(st.var)] = [item]
            try:
                self._exec_stmts(st.body, body)
            except SignalContinue:
                continue
            except SignalBreak:
                break

    # ———— 赋值（EVAL-ASSIGN-003：地址组件先于右值）————
    def _eval_slot(self, tgt: I.Expr, f: _Frame) -> tuple:
        if isinstance(tgt, I.LocalGet):
            p = self._slot_for(tgt.local, f)
            if p is None:
                raise LangRuntimeError(f"未定义变量 {tgt.local.name}")
            return ("local", p)
        if isinstance(tgt, I.Field):
            ov = _deref(self.eval(tgt.obj, f))
            if not isinstance(ov, StructVal):
                raise LangRuntimeError("该值不是 struct")
            for i, fname in enumerate(ov.def_.fields):
                if fname == tgt.name:
                    return ("field", ov, i)
            raise LangRuntimeError(f"{ov.def_.name} 没有字段 {tgt.name}")
        if isinstance(tgt, I.Index):
            ov = _deref(self.eval(tgt.obj, f))
            iv = self.eval(tgt.idx, f)
            if isinstance(ov, ArrVal):
                return ("arr", ov, self._int_index(iv, len(ov.items)))
            if isinstance(ov, MapVal):
                if type(iv) not in (int, str, bool):
                    raise LangRuntimeError("映射键须为 int/string/bool")
                return ("map", ov, iv)
            raise LangRuntimeError("该类型不可索引赋值")
        raise LangRuntimeError("赋值目标不合法")

    def _slot_read(self, slot) -> object:
        kind = slot[0]
        if kind == "local":
            return slot[1][0]
        if kind == "field":
            return slot[1].fields[slot[2]]
        if kind == "arr":
            return slot[1].items[slot[2]]
        # map：缺键读 unit（与参考一致）
        v = slot[1].get(slot[2])
        return UNIT if v is None else v

    def _slot_write(self, slot, v) -> None:
        kind = slot[0]
        if kind == "local":
            slot[1][0] = v
        elif kind == "field":
            slot[1].fields[slot[2]] = v
        elif kind == "arr":
            slot[1].items[slot[2]] = v
        else:
            slot[1].set(slot[2], v)

    def _exec_assign(self, st: I.Assign, f: _Frame) -> None:
        slot = self._eval_slot(st.target, f)
        v = self.eval(st.val, f)
        if st.op == I.OP_ASSIGN:
            self._slot_write(slot, copy_value(v))
            return
        # 复合赋值：读旧值 → 运算 → 写回；目标读出数组且为 += 时原位拼接
        if st.op == OP_PLUS_A and isinstance(self._slot_read(slot), ArrVal):
            add = v
            if not isinstance(add, ArrVal):
                raise LangRuntimeError(f"数组 += 需要数组，得到 {_type_name(v)}")
            self._slot_read(slot).items.extend(add.items)
            return
        old = self._slot_read(slot)
        combined = self._binary(_bin_op_of(st.op), old, v)
        self._slot_write(slot, combined)

    # ———— 表达式 ————
    def eval(self, e: I.Expr, f: _Frame):
        if isinstance(e, I.Const):
            if e.kind == I.ConstKind.INT:
                return e.i64
            if e.kind == I.ConstKind.FLOAT:
                return e.f64
            if e.kind == I.ConstKind.STR:
                return e.s
            return e.b
        if isinstance(e, I.LocalGet):
            p = self._slot_for(e.local, f)
            if p is None:
                raise LangRuntimeError(f"未定义变量 {e.local.name}")
            return p[0]
        if isinstance(e, I.FnGet):
            fn = self.fns.get(e.name)
            if fn is None:
                raise LangRuntimeError(f"未注册函数 {e.name}")
            return _FnVal(fn=fn, name=e.name)
        if isinstance(e, I.BuiltinGet):
            return _BuiltinVal(e.name)
        if isinstance(e, I.Ctor):
            args = [self.eval(a, f) for a in e.args]
            ci = self.ctors.get(e.sym.name)
            if ci is None:
                raise LangRuntimeError(f"未知构造器 {e.sym.name}")
            return EnumVal(ci[0], ci[1], args)
        if isinstance(e, I.Call):
            return self._eval_call(e, f)
        if isinstance(e, I.Unary):
            v = self.eval(e.x, f)
            if e.op == OP_MINUS:
                if type(v) is int:
                    return i64(-v)
                if type(v) is float:
                    return -v
                raise LangRuntimeError("一元 - 不适用于该类型")
            if e.op == OP_NOT:
                return not truthy(v)
            if e.op == "&":
                # &x（VALUE-REF-003）：仅 LocalGet 有稳定槽位
                if isinstance(e.x, I.LocalGet):
                    p = f.slot(e.x.local)
                    if p is not None:
                        return PtrVal(p)
                raise LangRuntimeError("& 仅可作用于变量")
            raise LangRuntimeError(f"irinterp: 未支持的一元运算 {e.op}")
        if isinstance(e, I.Binary):
            lv = self.eval(e.l, f)
            if e.op == I.OP_ANDAND:
                if not truthy(lv):
                    return False
                return truthy(self.eval(e.r, f))
            if e.op == I.OP_OROR:
                if truthy(lv):
                    return True
                return truthy(self.eval(e.r, f))
            rv = self.eval(e.r, f)
            return self._binary(e.op, lv, rv)
        if isinstance(e, I.Range):
            lo = self.eval(e.lo, f)
            hi = self.eval(e.hi, f)
            if type(lo) is not int or type(hi) is not int:
                raise LangRuntimeError("区间两端须为 int")
            return RangeVal(lo, hi)
        if isinstance(e, I.Index):
            ov = _deref(self.eval(e.obj, f))
            iv = self.eval(e.idx, f)
            if isinstance(ov, ArrVal):
                return ov.items[self._int_index(iv, len(ov.items))]
            if isinstance(ov, MapVal):
                if not ov.has(iv):
                    raise LangRuntimeError(f"映射无键 {fmt_val(iv, True)}（用 has() 探测）")
                return ov.get(iv)
            if isinstance(ov, str):
                return ov[self._int_index(iv, len(ov))]
            raise LangRuntimeError("该类型不可索引")
        if isinstance(e, I.Slice):
            ov = _deref(self.eval(e.obj, f))
            lo = self.eval(e.lo, f)
            hi = self.eval(e.hi, f)
            if type(lo) is not int or type(hi) is not int:
                raise LangRuntimeError("切片端点须为 int")
            if isinstance(ov, ArrVal):
                if lo < 0 or hi > len(ov.items) or lo > hi:
                    raise LangRuntimeError(f"切片区间 [{lo},{hi}) 越界（长度 {len(ov.items)}）")
                return ArrVal(ov.items[lo:hi])
            if isinstance(ov, str):
                if lo < 0 or hi > len(ov) or lo > hi:
                    raise LangRuntimeError(f"切片区间 [{lo},{hi}) 越界（长度 {len(ov)}）")
                return ov[lo:hi]
            raise LangRuntimeError("该类型不可切片")
        if isinstance(e, I.Field):
            ov = _deref(self.eval(e.obj, f))
            if not isinstance(ov, StructVal):
                raise LangRuntimeError("该值不是 struct")
            for i, fname in enumerate(ov.def_.fields):
                if fname == e.name:
                    return ov.fields[i]
            raise LangRuntimeError(f"{ov.def_.name} 没有字段 {e.name}")
        if isinstance(e, I.Try):
            v = self.eval(e.x, f)
            if isinstance(v, EnumVal) and v.def_.name == "Result" and v.case == 0:
                return v.payloads[0]
            # Err(e)：整个 Err 枚举值传播出当前函数（RESULT-TRY-003 边界合成返回）
            raise SignalPropagateErr(v)
        if isinstance(e, I.StructLit):
            name = e.struct.name if e.struct is not None else ""
            sdef = self.structs.get(name)
            if sdef is None:
                raise LangRuntimeError(f"未知 struct {name}")
            vals: dict[str, object] = {}
            for fv in e.fields:
                vals[fv.name] = self.eval(fv.val, f)
            fields = [vals[fname] for fname in sdef.fields]
            return StructVal(sdef, fields)
        if isinstance(e, I.MapLit):
            mm = MapVal()
            for k_e, v_e in zip(e.keys, e.vals):
                k = self.eval(k_e, f)
                v = self.eval(v_e, f)
                mm.set(k, v)
            return mm
        if isinstance(e, I.SliceLit):
            return ArrVal([self.eval(el, f) for el in e.elems])
        if isinstance(e, I.Match):
            return self._eval_match(e, f)
        if isinstance(e, I.FnLit):
            return _FnVal(lit=e, env=f)
        raise LangRuntimeError(f"irinterp: 未支持的表达式 {type(e).__name__}")

    # ———— 调用 ————
    def _eval_call(self, x: I.Call, f: _Frame):
        if x.builtin:
            args = [self.eval(a, f) for a in x.args]
            return self._call_builtin(x.builtin, args)
        if x.method is not None:
            recv = _deref(self.eval(x.recv, f))
            if not isinstance(recv, StructVal):
                raise LangRuntimeError("该值不是 struct")
            fn = self.methods.get(f"{recv.def_.name}.{x.method.name}")
            if fn is None:
                raise LangRuntimeError(f"{recv.def_.name} 没有方法 {x.method.name}")
            args = [self.eval(a, f) for a in x.args]
            return self.call_fn(fn, recv, args)
        if x.fn is not None:
            args = [self.eval(a, f) for a in x.args]
            fn = self.fns.get(x.fn.name)
            if fn is None:
                nested = f.lookup_fn(x.fn.name)
                if nested is not None:
                    return self.call_fn(nested, None, args)
                raise LangRuntimeError(f"未注册函数 {x.fn.name}")
            return self.call_fn(fn, None, args)
        cv = self.eval(x.callee, f)
        args = [self.eval(a, f) for a in x.args]
        if isinstance(cv, _FnVal):
            return self._call_fn_val(cv, args)
        if isinstance(cv, _BuiltinVal):
            return self._call_builtin(cv.name, args)
        raise LangRuntimeError("该值不可调用")

    def _call_builtin(self, name: str, args: list):
        if name == "println":
            self.out.write(self._fmt_args(args) + "\n")
            return UNIT
        if name == "print":
            self.out.write(self._fmt_args(args))
            return UNIT
        v, found = self.host.call_builtin(name, args)
        if not found:
            raise LangRuntimeError(f"irinterp: 未支持的内建 {name}")
        return v

    def call_fn(self, fn: I.Fn, recv: Optional[object], args: list):
        if fn.extern:
            raise LangRuntimeError("extern 函数仅转译可用")
        f = _Frame(self.globals)
        if fn.recv is not None:
            f.vars[id(fn.recv)] = [copy_value(recv)]
        for i, p in enumerate(fn.params):
            v = args[i] if i < len(args) else UNIT
            f.vars[id(p)] = [copy_value(v)]
        return self._exec_body(fn.body, f)

    def _call_fn_val(self, fv: _FnVal, args: list):
        if fv.fn is not None:
            return self.call_fn(fv.fn, None, args)
        if fv.lit is not None:
            f = _Frame(fv.env)
            # VALUE-CAPTURE-004 按引用捕获：Locals 中已存在于捕获链的槽位直接复用
            for l in fv.lit.locals:
                p = fv.env.slot(l) if fv.env is not None else None
                if p is not None:
                    f.vars[id(l)] = p
            for i, p in enumerate(fv.lit.params):
                v = args[i] if i < len(args) else UNIT
                f.vars[id(p)] = [copy_value(v)]
            return self._exec_body(fv.lit.body, f)
        raise LangRuntimeError("空函数值不可调用")

    def _exec_body(self, body: list[I.Stmt], f: _Frame):
        try:
            self._exec_stmts(body, f)
        except SignalReturn as ret:
            return ret.value
        except SignalPropagateErr as prop:
            return prop.value
        except (SignalBreak, SignalContinue) as sig:
            raise LangRuntimeError(str(sig)) from None
        return UNIT

    # ———— match ————
    def _eval_match(self, x: I.Match, f: _Frame):
        scrut = self.eval(x.scrut, f)
        for arm in x.arms:
            bind = _Frame(f)
            if self._match_pat(arm.pat, scrut, bind):
                return self.eval(arm.body, bind)
        raise LangRuntimeError("match 无匹配分支")

    def _match_pat(self, p: I.Pat, v, bind: _Frame) -> bool:
        if isinstance(p, I.WildPat):
            return True
        if isinstance(p, I.BindPat):
            if p.local is not None:
                bind.vars[id(p.local)] = [v]
            return True
        if isinstance(p, I.LitPat):
            lv = self.eval(p.val, bind)
            return _eq_values(lv, v)
        if isinstance(p, I.CtorPat):
            if not isinstance(v, EnumVal):
                return False
            if v.def_.cases[v.case] != (p.sym.name if p.sym is not None else ""):
                return False
            for i, sub in enumerate(p.subs):
                if i >= len(v.payloads):
                    return False
                if not self._match_pat(sub, v.payloads[i], bind):
                    return False
            return True
        return False

    # ———— 运算 ————
    def _binary(self, op: str, lv, rv):
        if op in (I.OP_EQ,):
            return _eq_values(lv, rv)
        if op in (I.OP_NEQ,):
            return not _eq_values(lv, rv)
        if op in (I.OP_LT, I.OP_LE, I.OP_GT, I.OP_GE):
            return self._compare(op, lv, rv)
        return self._arith(op, lv, rv)

    def _arith(self, op: str, lv, rv):
        tl, tr = type(lv), type(rv)
        if tl is int and tr is int:
            if op in (OP_PLUS, OP_PLUS_A):
                return i64(lv + rv)
            if op in (OP_MINUS, OP_MINUS_A):
                return i64(lv - rv)
            if op in (OP_STAR, OP_STAR_A):
                return i64(lv * rv)
            if op in (OP_SLASH, OP_SLASH_A):
                if rv == 0:
                    raise LangRuntimeError("整数除零")
                return i64(i64_div(lv, rv))
            if op == OP_PERCENT:
                if rv == 0:
                    raise LangRuntimeError("整数模零")
                return i64(i64_mod(lv, rv))
            if op == OP_AND:
                return i64(lv & rv)
            if op == OP_BIT_OR:
                return i64(lv | rv)
            if op == OP_XOR:
                return i64(lv ^ rv)
            if op == OP_SHL:
                return i64(lv << rv)
            if op == OP_SHR:
                return lv >> rv
        elif tl is float and tr is float:
            if op in (OP_PLUS, OP_PLUS_A):
                return lv + rv
            if op in (OP_MINUS, OP_MINUS_A):
                return lv - rv
            if op in (OP_STAR, OP_STAR_A):
                return lv * rv
            if op in (OP_SLASH, OP_SLASH_A):
                return lv / rv
        elif tl is str and tr is str:
            if op in (OP_PLUS, OP_PLUS_A):
                return lv + rv
        else:
            if (tl is int) != (tr is int) and (tl is int or tr is int) and (tl is float or tr is float):
                raise LangRuntimeError("int 与 float 不可混合运算（无隐式转换）")
        raise LangRuntimeError("运算不适用于该类型")

    def _compare(self, op: str, lv, rv):
        if type(lv) is not type(rv) or type(lv) not in (int, float, str):
            raise LangRuntimeError("该类型不可比较")
        if op == I.OP_LT:
            return lv < rv
        if op == I.OP_LE:
            return lv <= rv
        if op == I.OP_GT:
            return lv > rv
        return lv >= rv

    # ———— 辅助 ————
    def _fmt_args(self, args: list) -> str:
        return " ".join(fmt_val(a, True) for a in args)

    def _int_index(self, v, length: int) -> int:
        if type(v) is not int:
            raise LangRuntimeError("索引须为 int")
        if v < 0 or v >= length:
            raise LangRuntimeError(f"索引 {v} 越界（长度 {length}）")
        return v


def _eq_values(a, b) -> bool:
    ta, tb = type(a), type(b)
    if ta in (int, float, bool, str):
        return ta is tb and a == b
    if ta is EnumVal:
        if tb is not EnumVal or a.def_.name != b.def_.name or a.case != b.case \
                or len(a.payloads) != len(b.payloads):
            return False
        return all(_eq_values(x, y) for x, y in zip(a.payloads, b.payloads))
    return False


def _bin_op_of(op: str) -> str:
    return {
        OP_PLUS_A: OP_PLUS,
        OP_MINUS_A: OP_MINUS,
        OP_STAR_A: OP_STAR,
        OP_SLASH_A: OP_SLASH,
    }.get(op, op)


def _type_name(v) -> str:
    t = type(v)
    if t is int:
        return "int"
    if t is float:
        return "float"
    if t is bool:
        return "bool"
    if t is str:
        return "string"
    if t is ArrVal:
        return "数组"
    if t is MapVal:
        return "映射"
    if t is StructVal:
        return "struct"
    if t is EnumVal:
        return "enum"
    return "未知值"
