"""Effect 与 Semantic Graph 的契约 JSON 包装（contract/effects.go + graph.go 语义等价）。"""

from __future__ import annotations

from typing import Optional

from .model import KIND_INSPECT_CALLEES, KIND_INSPECT_CALLERS, KIND_INSPECT_DEPENDENCY, \
    KIND_INSPECT_EFFECTS, KIND_INSPECT_IMPACT, envelope, span_json


def effects_json(p) -> list[dict]:
    """推导全部函数效果（按 SymbolID 排序，效果按固定序去重）。"""
    from ..graph.effects import derive_effects
    out = []
    for e in derive_effects(p):
        out.append({"symbolId": e.symbol_id, "name": e.name, "effects": e.effects})
    return out


def effects_envelope(p) -> dict:
    return envelope(KIND_INSPECT_EFFECTS, effects=effects_json(p))


def graph_node_json(n) -> dict:
    return {
        "symbolId": n.id,
        "kind": n.kind,
        "name": n.name,
        "module": n.module,
        "type": n.type,
        "effects": n.effects,
        "span": span_json(n.span),
    }


def _impact_item_json(g, it) -> dict:
    name = kind = module = ""
    n = g.nodes.get(it.from_id)
    if n is not None:
        name, kind, module = n.name, n.kind, n.module
    out = {
        "symbolId": it.from_id,
        "name": name,
        "kind": kind,
        "transitive": it.transitive,
    }
    if module:
        out["module"] = module
    sp = span_json(it.span) if it.span else None
    if sp is not None:
        out["span"] = sp
    return out


def callers_envelope(g, items) -> dict:
    return envelope(KIND_INSPECT_CALLERS, callers=[_impact_item_json(g, it) for it in items])


def callees_envelope(g, items) -> dict:
    return envelope(KIND_INSPECT_CALLEES, callees=[_impact_item_json(g, it) for it in items])


def impact_envelope(imp) -> dict:
    out = {
        "symbol": graph_node_json(imp.symbol) if imp.symbol is not None else {
            "symbolId": 0, "kind": "", "name": "", "module": ""},
        "direct": [],
        "transitive": [],
        "effectWatch": list(imp.effect_watch),
        "modules": list(imp.modules),
        "note": "保守证据集：列出可能受影响的符号与位置，不构成完整影响证明",
    }
    for it in imp.direct:
        item = {"symbolId": it.from_id, "kind": it.kind, "transitive": it.transitive}
        sp = span_json(it.span) if it.span else None
        if sp is not None:
            item["span"] = sp
        out["direct"].append(item)
    for it in imp.transitive:
        out["transitive"].append({
            "symbolId": it.from_id, "kind": it.kind, "transitive": it.transitive,
            **({"span": span_json(it.span)} if it.span and span_json(it.span) else {})})
    if not out["effectWatch"]:
        out["effectWatch"] = []
    return envelope(KIND_INSPECT_IMPACT, impact=out)


def graph_dep_envelope(dep) -> dict:
    return envelope(KIND_INSPECT_DEPENDENCY, dependency={
        "module": dep.module,
        "imports": list(dep.imports),
        "dependsOn": list(dep.depends_on),
    })
