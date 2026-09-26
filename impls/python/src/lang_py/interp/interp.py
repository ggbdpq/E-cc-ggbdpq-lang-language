"""AST 树遍历解释器（参考语义 Oracle，interp.go 语义等价）。

- 值模型见 values.py；控制流信号经异常承载（SignalReturn/Break/Continue/PropagateErr），
  在函数/循环边界消化，绝不穿越 CLI。
- 求值顺序严格左到右；赋值地址组件先于右值（EVAL-ASSIGN-003）。
- go 语句为同步确定子集（TASK-SPAWN-002）。
- REPL 场景 EvalChunk 环境持久；test 经 CallByName。
"""

from __future__ import annotations

from typing import IO, Optional

from ..errors import (LangRuntimeError, SignalBreak, SignalContinue,
                      SignalPropagateErr, SignalReturn)
from ..syntax import ast_nodes as A
from ..syntax.tokens import K
from .builtins import register_builtins
from .stdlib import register_stdlib
from .values import (ArrVal, Builtin, Closure, EnumDef, EnumVal, MapVal,
                     PtrVal, RangeVal, StructDef, StructVal, UNIT, compare,
                     copy_value, equals, fmt_val, i64, i64_div, i64_mod,
                     truthy, type_name)


class Cell:
    __slots__ = ("val", "mutable")

    def __init__(self, val, mutable: bool) -> None:
        self.val = val
        self.mutable = mutable


class Env:
    __slots__ = ("vars", "parent")

    def __init__(self, parent: Optional["Env"]) -> None:
        self.vars: dict[str, Cell] = {}
        self.parent = parent

    def define(self, name: str, v, mutable: bool) -> None:
        if name in self.vars:
            raise LangRuntimeError(f"{name} 在此作用域重复定义")
        self.vars[name] = Cell(v, mutable)

    def redefine(self, name: str, v) -> None:
        self.vars[name] = Cell(v, False)

    def lookup(self, name: str) -> Optional[Cell]:
        env: Optional[Env] = self
        while env is not None:
            c = env.vars.get(name)
            if c is not None:
                return c
            env = env.parent
        return None


def _deref(v):
    if isinstance(v, PtrVal):
        return v.cell[0]
    return v


class CtorInfo:
    __slots__ = ("def_", "idx", "arity")

    def __init__(self, def_: EnumDef, idx: int, arity: int) -> None:
        self.def_ = def_
        self.idx = idx
        self.arity = arity


class Addr:
    """预求的赋值目标地址（cell / obj+name / obj+idx）。"""
    __slots__ = ("cell", "vname", "obj", "name", "idx", "line")

    def __init__(self, cell: Optional[Cell] = None, vname: str = "",
                 obj=None, name: str = "", idx=None, line: int = 0) -> None:
        self.cell = cell
        self.vname = vname
        self.obj = obj
        self.name = name
        self.idx = idx
        self.line = line


class Interp:
    """树遍历解释器。REPL 场景可反复 EvalChunk，环境持久。"""

    def __init__(self, out: IO) -> None:
        self.out = out
        self.globals = Env(None)
        self.structs: dict[str, StructDef] = {}
        self.enums: dict[str, EnumDef] = {}
        self.ctors: dict[str, CtorInfo] = {}
        self._prelude()

    def errf(self, line: int, msg: str) -> LangRuntimeError:
        return LangRuntimeError(f"第{line}行: {msg}")

    def write(self, s: str) -> None:
        self.out.write(s)

    # ———— 预载 ————
    def _prelude(self) -> None:
        opt_def = EnumDef("Option", ["Some", "None"])
        res_def = EnumDef("Result", ["Ok", "Err"])
        self.enums["Option"] = opt_def
        self.enums["Result"] = res_def
        self.ctors["Some"] = CtorInfo(opt_def, 0, 1)
        self.ctors["None"] = CtorInfo(opt_def, 1, 0)
        self.ctors["Ok"] = CtorInfo(res_def, 0, 1)
        self.ctors["Err"] = CtorInfo(res_def, 1, 1)
        self.structs["error"] = StructDef("error", ["msg"])

        def reg(name, fn):
            self.globals.redefine(name, Builtin(name, fn))

        register_builtins(reg)
        build_http = register_stdlib(reg, self.structs, self.enums)
        self.globals.redefine("http", build_http())

    # ———— 入口 ————
    def run(self, prog: list[A.Stmt]) -> None:
        """执行整个程序；定义了 main 则随后调用。"""
        self.exec_stmts(prog, self.globals)
        c = self.globals.lookup("main")
        if c is not None and isinstance(c.val, Closure):
            self.call_closure(c.val, [])

    def eval_chunk(self, stmts: list[A.Stmt]) -> tuple[object, bool]:
        """执行 REPL 片段，返回 (最后一个表达式语句的值, 是否有)。"""
        last = None
        has = False
        for s in stmts:
            if isinstance(s, A.ExprStmt):
                last = self.eval(s.x, self.globals)
                has = True
                continue
            self.exec_stmt(s, self.globals)
        return last, has

    def call_builtin(self, name: str, args: list) -> tuple[object, bool]:
        """调用预载内建（IR 解释器等 IR 层执行器复用同一套内建实现）。
        返回 (值, found)；found=False 表示未注册该名字。"""
        c = self.globals.lookup(name)
        if c is None:
            return None, False
        b = c.val
        if not isinstance(b, Builtin):
            return None, False
        return b.fn(self, args), True

    def call_by_name(self, name: str) -> None:
        c = self.globals.lookup(name)
        if c is None:
            raise LangRuntimeError(f"未定义 {name}")
        if not isinstance(c.val, Closure):
            raise LangRuntimeError(f"{name} 不是函数")
        self.call_closure(c.val, [])

    # ———— 语句 ————
    def exec_stmts(self, stmts: list[A.Stmt], env: Env) -> None:
        for s in stmts:
            self.exec_stmt(s, env)

    def exec_stmt(self, s: A.Stmt, env: Env) -> None:
        if isinstance(s, A.LetStmt):
            v = self.eval(s.init, env)
            if s.name == "_":
                return
            env.define(s.name, copy_value(v), s.mutable)
        elif isinstance(s, A.AssignStmt):
            self.assign(s, env)
        elif isinstance(s, A.ExprStmt):
            self.eval(s.x, env)
        elif isinstance(s, A.ReturnStmt):
            v = UNIT
            if s.val is not None:
                v = copy_value(self.eval(s.val, env))
            raise SignalReturn(v)
        elif isinstance(s, A.IfStmt):
            if truthy(self.eval(s.cond, env)):
                self.exec_stmts(s.then, Env(env))
            elif s.els is not None:
                self.exec_stmts(s.els, Env(env))
        elif isinstance(s, A.WhileStmt):
            while truthy(self.eval(s.cond, env)):
                self.exec_loop_body(s.body, Env(env))
        elif isinstance(s, A.ForInStmt):
            self.exec_for_in(s, env)
        elif isinstance(s, A.BreakStmt):
            raise SignalBreak()
        elif isinstance(s, A.ContinueStmt):
            raise SignalContinue()
        elif isinstance(s, A.FnDecl):
            self.decl_fn(s, env)
        elif isinstance(s, A.StructDecl):
            self.decl_struct(s)
        elif isinstance(s, A.EnumDecl):
            self.decl_enum(s)
        elif isinstance(s, A.ExternDecl):
            # extern 声明：解释器一律注册为"仅转译可用"桩
            for fn in s.fns:
                name = fn.name
                host = s.host

                def stub(it, args, _name=name, _host=host):
                    raise LangRuntimeError(
                        f"extern 函数 {_name} 仅转译可用（宿主 {_host}）")
                self.globals.redefine(name, Builtin(name, stub))
        elif isinstance(s, A.ImportStmt):
            raise self.errf(s.line, "import 由装载器在合并前展开（不应执行到这里）")
        elif isinstance(s, A.GoStmt):
            # 单线程解释器：go 同步立即执行（语义子集，输出确定）
            self.eval_call(s.call, env)
        else:
            raise self.errf(s.line, "未知语句")

    def exec_loop_body(self, body: list[A.Stmt], env: Env) -> None:
        try:
            self.exec_stmts(body, env)
        except (SignalBreak, SignalContinue):
            pass

    def exec_for_in(self, st: A.ForInStmt, env: Env) -> None:
        it = self.eval(st.iter, env)
        if isinstance(it, ArrVal):
            items = list(it.items)
        elif isinstance(it, MapVal):
            items = it.keys_snapshot()
        elif isinstance(it, str):
            items = list(it)
        elif isinstance(it, RangeVal):
            items = list(range(it.lo, it.hi))
        else:
            raise self.errf(st.line, f"{type_name(it)} 不可迭代")
        for item in items:
            benv = Env(env)
            if st.var != "_":
                benv.define(st.var, copy_value(item), False)
            self.exec_loop_body(st.body, benv)

    def decl_fn(self, st: A.FnDecl, env: Env) -> None:
        if st.recv is not None:
            nt = st.recv.typ
            if not isinstance(nt, A.NamedType):
                raise self.errf(st.line, "方法接收者须为具名 struct 类型")
            sdef = self.structs.get(nt.name)
            if sdef is None:
                raise self.errf(st.line, f"为未知 struct {nt.name} 定义方法")
            sdef.methods[st.name] = Closure(st.name, st, env)
            return
        env.redefine(st.name, Closure(st.name, st, env))

    def decl_struct(self, st: A.StructDecl) -> None:
        self.register_struct(st.name, [f.name for f in st.fields], st.line)

    def decl_enum(self, st: A.EnumDecl) -> None:
        cases = [c.name for c in st.cases]
        edef = self.register_enum(st.name, cases, st.line)
        for i, c in enumerate(st.cases):
            self.ctors[c.name] = CtorInfo(edef, i, len(c.payloads))

    def register_struct(self, name: str, fields: list[str], line: int) -> StructDef:
        if name in self.structs:
            raise self.errf(line, f"struct {name} 重复定义")
        sdef = StructDef(name, fields)
        self.structs[name] = sdef
        return sdef

    def register_enum(self, name: str, cases: list[str], line: int) -> EnumDef:
        if name in self.enums:
            raise self.errf(line, f"enum {name} 重复定义")
        for c in cases:
            if c in self.ctors:
                raise self.errf(line, f"构造器 {c} 重名")
            if self.globals.lookup(c) is not None:
                raise self.errf(line, f"case {c} 与内建名冲突")
        edef = EnumDef(name, cases)
        self.enums[name] = edef
        return edef

    # ———— 赋值（EVAL-ASSIGN-003：地址组件先于右值）————
    def assign(self, st: A.AssignStmt, env: Env) -> None:
        addr = self.eval_assign_addr(st.target, env)
        v = copy_value(self.eval(st.val, env))
        if st.op != K.ASSIGN:
            self.assign_compound(st, addr, v, env)
            return
        self.write_addr(addr, v)

    def eval_assign_addr(self, tgt: A.Expr, env: Env) -> Addr:
        if isinstance(tgt, A.Ident):
            c = env.lookup(tgt.name)
            if c is None:
                raise self.errf(tgt.line, f"未定义变量 {tgt.name}")
            return Addr(cell=c, vname=tgt.name, line=tgt.line)
        if isinstance(tgt, A.FieldEx):
            ov = _deref(self.eval(tgt.obj, env))
            if not isinstance(ov, StructVal):
                raise self.errf(tgt.line, f"只能给 struct 字段赋值，得到 {type_name(ov)}")
            return Addr(obj=ov, name=tgt.name, line=tgt.line)
        if isinstance(tgt, A.IndexEx):
            ov = _deref(self.eval(tgt.obj, env))
            iv = self.eval(tgt.idx, env)
            return Addr(obj=ov, idx=iv, line=tgt.line)
        raise LangRuntimeError("赋值目标不合法")

    def write_addr(self, a: Addr, v) -> None:
        if a.cell is not None:
            if not a.cell.mutable:
                raise self.errf(a.line, f"{a.vname} 由 let 绑定，不可重新赋值")
            a.cell.val = v
            return
        container = a.obj
        if isinstance(container, StructVal):
            idx = -1
            for i, f in enumerate(container.def_.fields):
                if f == a.name:
                    idx = i
                    break
            if idx < 0:
                raise self.errf(a.line, f"{container.def_.name} 没有字段 {a.name}")
            container.fields[idx] = v
        elif isinstance(container, ArrVal):
            i = self.int_index(a.idx, len(container.items), a.line)
            container.items[i] = v
        elif isinstance(container, MapVal):
            container.set(a.idx, v)
        elif isinstance(container, str):
            raise self.errf(a.line, "字符串不可变")
        else:
            raise self.errf(a.line, f"{type_name(container)} 不可索引赋值")

    def read_addr(self, a: Addr):
        if a.cell is not None:
            return a.cell.val
        container = a.obj
        if isinstance(container, StructVal):
            for i, f in enumerate(container.def_.fields):
                if f == a.name:
                    return container.fields[i]
            raise self.errf(a.line, f"{container.def_.name} 没有字段 {a.name}")
        if isinstance(container, ArrVal):
            i = self.int_index(a.idx, len(container.items), a.line)
            return container.items[i]
        if isinstance(container, MapVal):
            if container.has(a.idx):
                return container.get(a.idx)
            raise self.errf(a.line, f"映射无键 {fmt_val(a.idx, True)}（用 has() 探测）")
        if isinstance(container, str):
            return container
        raise self.errf(a.line, f"{type_name(container)} 不可索引读取")

    def assign_compound(self, st: A.AssignStmt, addr: Addr, v, env: Env) -> None:
        if st.op == K.PLUS_ASSIGN:
            cur = _deref(self.read_addr(addr))
            if isinstance(cur, ArrVal):
                if not isinstance(v, ArrVal):
                    raise self.errf(st.line, f"数组 += 需要数组，得到 {type_name(v)}")
                cur.items.extend(v.items)
                return
        old = _deref(self.read_addr(addr))
        combined = self.apply_op(st.op, old, v, st.line)
        self.write_addr(addr, combined)

    def apply_op(self, op: K, old, v, line: int):
        to, tv = type(old), type(v)
        if op == K.PLUS_ASSIGN:
            if to is int and tv is int:
                return i64(old + v)
            if to is float and tv is float:
                return old + v
            if to is str and tv is str:
                return old + v
        elif op == K.MINUS_ASSIGN:
            if to is int and tv is int:
                return i64(old - v)
            if to is float and tv is float:
                return old - v
        elif op == K.STAR_ASSIGN:
            if to is int and tv is int:
                return i64(old * v)
            if to is float and tv is float:
                return old * v
        elif op == K.SLASH_ASSIGN:
            if to is int and tv is int:
                if v == 0:
                    raise self.errf(line, "整数除零")
                return i64(i64_div(old, v))
            if to is float and tv is float:
                if v == 0.0:
                    raise self.errf(line, "浮点除零")
                return old / v
        raise self.errf(line, f"{type_name(old)} 与 {type_name(v)} 不可做该运算（无隐式转换）")

    def int_index(self, v, length: int, line: int) -> int:
        if type(v) is not int:
            raise self.errf(line, f"索引须为 int，得到 {type_name(v)}")
        if v < 0 or v >= length:
            raise self.errf(line, f"索引 {v} 越界（长度 {length}）")
        return v

    # ———— 表达式 ————
    def eval(self, e: A.Expr, env: Env):
        if isinstance(e, A.IntLit):
            return e.val
        if isinstance(e, A.FloatLit):
            return e.val
        if isinstance(e, A.StrLit):
            return e.val
        if isinstance(e, A.BoolLit):
            return e.val
        if isinstance(e, A.Ident):
            return self.eval_ident(e, env)
        if isinstance(e, A.Unary):
            return self.eval_unary(e, env)
        if isinstance(e, A.Binary):
            return self.eval_binary(e, env)
        if isinstance(e, A.RangeEx):
            lo = self.eval(e.lo, env)
            hi = self.eval(e.hi, env)
            if type(lo) is not int or type(hi) is not int:
                raise self.errf(e.line, "区间两端须为 int")
            return RangeVal(lo, hi)
        if isinstance(e, A.CallEx):
            return self.eval_call(e, env)
        if isinstance(e, A.IndexEx):
            return self.eval_index(e, env)
        if isinstance(e, A.SliceEx):
            return self.eval_slice(e, env)
        if isinstance(e, A.FieldEx):
            return self.eval_field(e, env)
        if isinstance(e, A.TryEx):
            return self.eval_try(e, env)
        if isinstance(e, A.StructLit):
            return self.eval_struct_lit(e, env)
        if isinstance(e, A.MapLit):
            return self.eval_map_lit(e, env)
        if isinstance(e, A.ArrLit):
            return ArrVal([copy_value(self.eval(el, env)) for el in e.items])
        if isinstance(e, A.MatchEx):
            return self.eval_match(e, env)
        if isinstance(e, A.FnLit):
            decl = A.FnDecl("", None, e.type_params, e.params, e.ret, e.body, e.line, e.span)
            return Closure("", decl, env)
        raise self.errf(e.line, "未知表达式")

    def eval_ident(self, x: A.Ident, env: Env):
        c = env.lookup(x.name)
        if c is not None:
            return c.val
        ctor = self.ctors.get(x.name)
        if ctor is not None:
            if ctor.arity == 0:
                # 零参构造器允许裸写（None 这类）
                return EnumVal(ctor.def_, ctor.idx, [])
            raise self.errf(x.line, f"构造器 {x.name} 须直接调用")
        raise self.errf(x.line, f"未定义 {x.name}")

    def eval_unary(self, x: A.Unary, env: Env):
        v = self.eval(x.x, env)
        if x.op == K.MINUS:
            if type(v) is int:
                return i64(-v)
            if type(v) is float:
                return -v
            raise self.errf(x.line, f"- 不能用于 {type_name(v)}")
        if x.op == K.NOT:
            return not truthy(v)
        if x.op == K.AMP:
            if not isinstance(x.x, A.Ident):
                raise self.errf(x.line, "v0 只能对变量取引用")
            c = env.lookup(x.x.name)
            if c is None:
                raise self.errf(x.line, f"未定义 {x.x.name}")
            return PtrVal([c.val])
        raise self.errf(x.line, "未知一元运算")

    def eval_binary(self, x: A.Binary, env: Env):
        if x.op == K.AND:
            if not truthy(self.eval(x.l, env)):
                return False
            return truthy(self.eval(x.r, env))
        if x.op == K.OR:
            if truthy(self.eval(x.l, env)):
                return True
            return truthy(self.eval(x.r, env))

        l = self.eval(x.l, env)
        r = self.eval(x.r, env)

        if x.op in (K.EQ, K.NEQ):
            eq = equals(l, r)
            return (not eq) if x.op == K.NEQ else eq
        if x.op in (K.AMP, K.BIT_OR, K.XOR, K.SHL, K.SHR):
            if type(l) is not int or type(r) is not int:
                raise self.errf(x.line, f"{type_name(l)} 与 {type_name(r)} 不可位运算（须为 int）")
            if x.op == K.AMP:
                return i64(l & r)
            if x.op == K.BIT_OR:
                return i64(l | r)
            if x.op == K.XOR:
                return i64(l ^ r)
            if r < 0 or r >= 64:
                raise self.errf(x.line, f"移位位数须在 0..63，得到 {r}")
            if x.op == K.SHL:
                return i64(l << r)
            return l >> r  # 算术移位（补码），i64 域内闭合
        if x.op in (K.LT, K.LE, K.GT, K.GE):
            # float 对按 IEEE 运算符直落：NaN 参与任何比较一律 false
            # （TYPE-CONV-005 v1.0.1，ADR-0006/RS-007）。NaN 无全序，
            # 走 compare 会被兜成 0，把 `<=`/`>=` 错判为 true。
            if type(l) is float and type(r) is float:
                if x.op == K.LT:
                    return l < r
                if x.op == K.LE:
                    return l <= r
                if x.op == K.GT:
                    return l > r
                return l >= r
            cmp = compare(l, r)
            if cmp is None:
                raise self.errf(x.line,
                                f"{type_name(l)} 与 {type_name(r)} 不可比较大小（无隐式转换）")
            if x.op == K.LT:
                return cmp < 0
            if x.op == K.LE:
                return cmp <= 0
            if x.op == K.GT:
                return cmp > 0
            return cmp >= 0

        tl, tr = type(l), type(r)
        if tl is int:
            if tr is not int:
                raise self.errf(x.line,
                                f"{type_name(l)} 与 {type_name(r)} 不可比较（无隐式转换）")
            return self.arith(x, l, r)
        if tl is float:
            if tr is not float:
                raise self.errf(x.line,
                                f"{type_name(l)} 与 {type_name(r)} 不可比较（无隐式转换）")
            if x.op == K.PLUS:
                return l + r
            if x.op == K.MINUS:
                return l - r
            if x.op == K.STAR:
                return l * r
            if x.op == K.SLASH:
                if r == 0.0:
                    raise self.errf(x.line, "浮点除零")
                return l / r
            raise self.errf(x.line, "% 不能用于 float")
        if tl is str:
            if tr is not str or x.op != K.PLUS:
                raise self.errf(x.line,
                                f"{type_name(l)} 与 {type_name(r)} 不可比较（无隐式转换）")
            return l + r
        raise self.errf(x.line, f"{type_name(l)} 不支持运算 {x.op}")

    def arith(self, x: A.Binary, a: int, b: int):
        if x.op == K.PLUS:
            return i64(a + b)
        if x.op == K.MINUS:
            return i64(a - b)
        if x.op == K.STAR:
            return i64(a * b)
        if x.op == K.SLASH:
            if b == 0:
                raise self.errf(x.line, "整数除零")
            return i64(i64_div(a, b))
        if x.op == K.PERCENT:
            if b == 0:
                raise self.errf(x.line, "整数模零")
            return i64(i64_mod(a, b))
        raise self.errf(x.line, "未知算术运算")

    def eval_index(self, x: A.IndexEx, env: Env):
        ov = _deref(self.eval(x.obj, env))
        iv = self.eval(x.idx, env)
        if isinstance(ov, ArrVal):
            return ov.items[self.int_index(iv, len(ov.items), x.line)]
        if isinstance(ov, MapVal):
            if not ov.has(iv):
                raise self.errf(x.line, f"映射无键 {fmt_val(iv, True)}（用 has() 探测）")
            return ov.get(iv)
        if isinstance(ov, str):
            return ov[self.int_index(iv, len(ov), x.line)]
        raise self.errf(x.line, f"{type_name(ov)} 不可索引")

    def eval_slice(self, x: A.SliceEx, env: Env):
        ov = _deref(self.eval(x.obj, env))
        lov = self.eval(x.lo, env)
        hiv = self.eval(x.hi, env)

        def bounds(n: int) -> tuple[int, int]:
            if type(lov) is not int or type(hiv) is not int:
                raise self.errf(x.line, "切片端点须为 int")
            if lov < 0 or hiv < lov or hiv > n:
                raise self.errf(x.line, f"切片 [{lov}:{hiv}] 越界（长度 {n}）")
            return lov, hiv

        if isinstance(ov, ArrVal):
            lo, hi = bounds(len(ov.items))
            return ArrVal(ov.items[lo:hi])
        if isinstance(ov, str):
            lo, hi = bounds(len(ov))
            return ov[lo:hi]
        raise self.errf(x.line, f"{type_name(ov)} 不可切片")

    def eval_field(self, x: A.FieldEx, env: Env):
        ov = _deref(self.eval(x.obj, env))
        if not isinstance(ov, StructVal):
            raise self.errf(x.line, f"{x.name} 没有字段（{type_name(ov)} 不是 struct）")
        for i, f in enumerate(ov.def_.fields):
            if f == x.name:
                return ov.fields[i]
        raise self.errf(x.line, f"{ov.def_.name} 没有字段 {x.name}")

    def eval_try(self, x: A.TryEx, env: Env):
        v = self.eval(x.x, env)
        if not isinstance(v, EnumVal) or v.def_.name != "Result":
            raise self.errf(x.line, f"? 的操作数须为 Result，得到 {type_name(v)}")
        if v.case == 0:  # Ok
            return v.payloads[0]
        raise SignalPropagateErr(v.payloads[0])

    def eval_struct_lit(self, x: A.StructLit, env: Env):
        sdef = self.structs.get(x.type_name)
        if sdef is None:
            raise self.errf(x.line, f"未知 struct {x.type_name}")
        if len(x.fields) != len(sdef.fields):
            raise self.errf(x.line,
                            f"{sdef.name} 需要 {len(sdef.fields)} 个字段，得到 {len(x.fields)} 个")
        fields: list = []
        for i, f in enumerate(x.fields):
            if f.name != sdef.fields[i]:
                raise self.errf(x.line,
                                f"{sdef.name} 第 {i + 1} 个字段应为 {sdef.fields[i]}，得到 {f.name}")
            fields.append(copy_value(self.eval(f.val, env)))
        return StructVal(sdef, fields)

    def eval_map_lit(self, x: A.MapLit, env: Env):
        m = MapVal()
        for item in x.items:
            k = self.eval(item.key, env)
            v = self.eval(item.val, env)
            m.set(k, copy_value(v))
        return m

    def eval_match(self, x: A.MatchEx, env: Env):
        scrut = self.eval(x.scrut, env)
        is_enum = isinstance(scrut, EnumVal)
        covered: set[int] = set()
        for arm in x.arms:
            penv = Env(env)
            if self.match_pat(scrut, arm.pat, penv):
                return self.eval(arm.body, penv)
            if is_enum and isinstance(arm.pat, A.CasePat):
                idx = scrut.def_.case_index(arm.pat.name)
                if idx >= 0:
                    covered.add(idx)
        if is_enum:
            missing = [c for i, c in enumerate(scrut.def_.cases) if i not in covered]
            if missing:
                raise self.errf(x.line, "match 不穷尽，缺少: " + ", ".join(missing))
        raise self.errf(x.line, "match 无匹配分支")

    def match_pat(self, v, pat: A.Pat, env: Env) -> bool:
        if isinstance(pat, A.WildPat):
            return True
        if isinstance(pat, A.BindPat):
            env.define(pat.name, v, False)
            return True
        if isinstance(pat, A.LitPat):
            lv = self.eval(pat.val, env)
            try:
                return equals(v, lv)
            except LangRuntimeError:
                return False
        if isinstance(pat, A.CasePat):
            if not isinstance(v, EnumVal):
                return False
            idx = v.def_.case_index(pat.name)
            if idx < 0 or idx != v.case:
                return False
            if len(pat.subs) != len(v.payloads):
                raise self.errf(pat.line,
                                f"case {pat.name} 需要 {len(v.payloads)} 个子模式，得到 {len(pat.subs)} 个")
            for sp, pv in zip(pat.subs, v.payloads):
                if not self.match_pat(pv, sp, env):
                    return False
            return True
        return False

    # ———— 调用 ————
    def eval_call(self, x: A.CallEx, env: Env):
        # 构造器调用（Some/None/Ok/Err 与用户 enum case）
        if isinstance(x.callee, A.Ident):
            ctor = self.ctors.get(x.callee.name)
            if ctor is not None:
                if len(x.args) != ctor.arity:
                    raise self.errf(x.line,
                                    f"{x.callee.name} 需要 {ctor.arity} 个参数，得到 {len(x.args)} 个")
                payloads = [copy_value(self.eval(a, env)) for a in x.args]
                return EnumVal(ctor.def_, ctor.idx, payloads)

        # 方法调用：Callee 为 FieldEx 且对象是 struct
        if isinstance(x.callee, A.FieldEx):
            fe = x.callee
            ov = _deref(self.eval(fe.obj, env))
            if isinstance(ov, StructVal):
                cl = ov.def_.methods.get(fe.name)
                if cl is not None:
                    args = self.eval_args(x.args, env)
                    return self.call_method(cl, ov, args)
            # 字段存函数值的情况落到通用路径

        cv = self.eval(x.callee, env)
        args = self.eval_args(x.args, env)
        if isinstance(cv, Closure):
            return self.call_closure(cv, args)
        if isinstance(cv, Builtin):
            return cv.fn(self, args)
        raise self.errf(x.line, f"{type_name(cv)} 不可调用")

    def eval_args(self, exprs: list[A.Expr], env: Env) -> list:
        return [copy_value(self.eval(a, env)) for a in exprs]

    def call_closure(self, cl: Closure, args: list):
        name = cl.name or "匿名函数"
        if len(args) != len(cl.decl.params):
            raise LangRuntimeError(f"{name} 需要 {len(cl.decl.params)} 个参数，得到 {len(args)} 个")
        fenv = Env(cl.env)
        for p, a in zip(cl.decl.params, args):
            fenv.define(p.name, a, False)
        return self.run_body(cl, fenv)

    def call_method(self, cl: Closure, recv, args: list):
        name = cl.name
        if len(args) != len(cl.decl.params):
            raise LangRuntimeError(f"{name} 需要 {len(cl.decl.params)} 个参数，得到 {len(args)} 个")
        fenv = Env(cl.env)
        fenv.define(cl.decl.recv.name, copy_value(recv), False)
        for p, a in zip(cl.decl.params, args):
            fenv.define(p.name, a, False)
        return self.run_body(cl, fenv)

    def run_body(self, cl: Closure, fenv: Env):
        try:
            self.exec_stmts(cl.decl.body, fenv)
        except SignalReturn as ret:
            return ret.value
        except SignalPropagateErr as prop:
            # ? 传播在边界合成该函数的 Err 返回值
            return EnumVal(self.enums["Result"], 1, [prop.value])
        except (SignalBreak, SignalContinue) as sig:
            raise LangRuntimeError(str(sig)) from None
        return UNIT
