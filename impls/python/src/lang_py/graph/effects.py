"""效果元数据推导（graph/effects.go 语义等价）。

直接效果来自 IR 节点（内建调用/extern/spawn），间接效果沿调用图 fixpoint 传播。
函数字面量的效果归属其定义所在函数（保守近似）。
枚举固定顺序：PURE IO FS NET TIME RANDOM SPAWN CHANNEL FFI。
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional

from ..ir import nodes as I

EFF_PURE = "PURE"
EFF_IO = "IO"
EFF_FS = "FS"
EFF_NET = "NET"
EFF_TIME = "TIME"
EFF_RANDOM = "RANDOM"
EFF_SPAWN = "SPAWN"
EFF_CHANNEL = "CHANNEL"
EFF_FFI = "FFI"

EFFECT_ORDER = [EFF_PURE, EFF_IO, EFF_FS, EFF_NET, EFF_TIME, EFF_RANDOM, EFF_SPAWN, EFF_CHANNEL, EFF_FFI]

# 内建直接效果（未列出的内建视为 PURE）
BUILTIN_EFFECTS = {
    "println": EFF_IO,
    "print": EFF_IO,
    "http.get": EFF_NET,
    "makeChan": EFF_CHANNEL,
    "send": EFF_CHANNEL,
    "recv": EFF_CHANNEL,
}


@dataclass(eq=False)
class EffectEntry:
    symbol_id: int
    name: str
    effects: list[str] = field(default_factory=list)


class _Unit:
    __slots__ = ("self_effects", "callees")

    def __init__(self) -> None:
        self.self_effects: set[str] = set()
        self.callees: list[I.Fn] = []


def derive_effects(p: I.Prog) -> list[EffectEntry]:
    """效果推导：每个顶层 fn/方法/嵌套 fn 各一条，按 SymbolID 排序。"""
    units: list[I.Fn] = []
    unit_set: set[int] = set()

    def walk_fns(stmts: list[I.Stmt]) -> None:
        for s in stmts:
            if isinstance(s, I.FnDef):
                if id(s.fn) not in unit_set:
                    unit_set.add(id(s.fn))
                    units.append(s.fn)
                walk_fns(s.fn.body)

    for f in p.fns:
        if id(f) not in unit_set:
            unit_set.add(id(f))
            units.append(f)
        walk_fns(f.body)

    direct: dict[int, _Unit] = {id(f): _Unit() for f in units}
    sym_to_unit: dict[int, I.Fn] = {}  # 与参考一致：当前不填充（效果传播不生效的既有行为）
    cur: list[I.Fn] = []  # 单元素游标

    def add_direct(name: str) -> None:
        eff = BUILTIN_EFFECTS.get(name)
        if eff is not None:
            direct[id(cur[0])].self_effects.add(eff)

    def visit_expr(e: I.Expr) -> None:
        if isinstance(e, I.Call):
            if e.builtin == "http.get":
                direct[id(cur[0])].self_effects.add(EFF_NET)
            elif e.builtin:
                add_direct(e.builtin)
            elif e.fn is not None:
                u = sym_to_unit.get(e.fn.id)
                if u is not None:
                    direct[id(cur[0])].callees.append(u)
            if e.recv is not None:
                visit_expr(e.recv)
            if e.callee is not None:
                visit_expr(e.callee)
            for a in e.args:
                visit_expr(a)
        elif isinstance(e, I.Ctor):
            for a in e.args:
                visit_expr(a)
        elif isinstance(e, I.Unary):
            visit_expr(e.x)
        elif isinstance(e, I.Binary):
            visit_expr(e.l)
            visit_expr(e.r)
        elif isinstance(e, I.Range):
            visit_expr(e.lo)
            visit_expr(e.hi)
        elif isinstance(e, I.Index):
            visit_expr(e.obj)
            visit_expr(e.idx)
        elif isinstance(e, I.Slice):
            visit_expr(e.obj)
            visit_expr(e.lo)
            visit_expr(e.hi)
        elif isinstance(e, I.Field):
            visit_expr(e.obj)
        elif isinstance(e, I.Try):
            visit_expr(e.x)
        elif isinstance(e, I.StructLit):
            for f in e.fields:
                visit_expr(f.val)
        elif isinstance(e, I.MapLit):
            for k in e.keys:
                visit_expr(k)
            for v in e.vals:
                visit_expr(v)
        elif isinstance(e, I.SliceLit):
            for el in e.elems:
                visit_expr(el)
        elif isinstance(e, I.Match):
            visit_expr(e.scrut)
            for arm in e.arms:
                visit_expr(arm.body)
        elif isinstance(e, I.FnLit):
            # 保守：字面量体的效果归当前单元（闭包定义处）
            for st in e.body:
                visit_stmts([st])

    def visit_stmts(list_: list[I.Stmt]) -> None:
        for s in list_:
            if isinstance(s, I.Let):
                visit_expr(s.init)
            elif isinstance(s, I.Assign):
                visit_expr(s.target)
                visit_expr(s.val)
            elif isinstance(s, I.Eval):
                visit_expr(s.x)
            elif isinstance(s, I.Return):
                if s.val is not None:
                    visit_expr(s.val)
            elif isinstance(s, I.If):
                visit_expr(s.cond)
                visit_stmts(s.then)
                visit_stmts(s.els)
            elif isinstance(s, I.While):
                visit_expr(s.cond)
                visit_stmts(s.body)
            elif isinstance(s, I.ForIn):
                visit_expr(s.iter)
                visit_stmts(s.body)
            elif isinstance(s, I.Spawn):
                direct[id(cur[0])].self_effects.add(EFF_SPAWN)
                if s.fn is not None:
                    u = sym_to_unit.get(s.fn.id)
                    if u is not None:
                        direct[id(cur[0])].callees.append(u)
                for a in s.args:
                    visit_expr(a)
            elif isinstance(s, I.FnDef):
                pass  # 嵌套 fn 是独立单元，效果不并入外层

    for f in units:
        cur = [f]
        visit_stmts(f.body)

    for f in units:
        if f.extern:
            direct[id(f)].self_effects.add(EFF_FFI)

    # fixpoint 传播
    changed = True
    while changed:
        changed = False
        for f in units:
            d = direct[id(f)]
            for c in d.callees:
                if c is None or c is f:
                    continue
                cd = direct.get(id(c))
                if cd is None:
                    continue
                for eff in cd.self_effects:
                    if eff not in d.self_effects:
                        d.self_effects.add(eff)
                        changed = True

    # 输出（SymbolID 排序；预载/无名跳过）
    out: list[EffectEntry] = []
    seen: set[int] = set()
    for f in units:
        if id(f) in seen or f.sym is None or f.sym.builtin_mark:
            continue
        seen.add(id(f))
        out.append(EffectEntry(symbol_id=f.sym.id, name=f.name,
                               effects=ordered_effects(direct[id(f)].self_effects)))
    out.sort(key=lambda e: e.symbol_id)
    return out


def ordered_effects(set_: set[str]) -> list[str]:
    if not set_:
        return [EFF_PURE]
    return [e for e in EFFECT_ORDER if e in set_]
