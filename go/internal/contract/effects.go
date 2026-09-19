package contract

// effects.go：Effect Metadata 从 Core IR 推导（v0.4）。
// 禁止源码字符串搜索：直接效果来自 IR 节点（内建调用/extern/spawn），
// 间接效果沿调用图传播（fixpoint）。函数字面量的效果归属其定义所在函数（保守近似）。
//
// 枚举（固定顺序输出）：PURE IO FS NET TIME RANDOM SPAWN CHANNEL FFI
// 其中 FS/TIME/RANDOM 为保留值——当前语言无生产者（见 specs/agent-protocol.md）。

import (
	"lang-go/internal/ir"
	"lang-go/internal/semantic"
)

// Effect 常量（契约枚举）
const (
	EffPure    = "PURE"
	EffIO      = "IO"
	EffFS      = "FS"
	EffNET     = "NET"
	EffTIME    = "TIME"
	EffRANDOM  = "RANDOM"
	EffSPAWN   = "SPAWN"
	EffCHANNEL = "CHANNEL"
	EffFFI     = "FFI"
)

// effectOrder 固定输出顺序
var effectOrder = []string{EffPure, EffIO, EffFS, EffNET, EffTIME, EffRANDOM, EffSPAWN, EffCHANNEL, EffFFI}

// builtinEffects 内建直接效果（未列出的内建视为 PURE）
var builtinEffects = map[string]string{
	"println":  EffIO,
	"print":    EffIO,
	"http.get": EffNET,
	"makeChan": EffCHANNEL,
	"send":     EffCHANNEL,
	"recv":     EffCHANNEL,
}

// fnEffects 单个函数的直接效果 + 被调符号集（传播用）
type fnDirect struct {
	self    map[string]bool // 直接效果
	callees []*ir.Fn        // Call{Fn} / Spawn 目标
}

// Effects 效果推导：每个顶层 fn/方法/嵌套 fn 各一条，按 SymbolID 排序
func Effects(p *ir.Prog) []Effect {
	// 收集函数单元
	units := map[*ir.Fn]bool{}
	var order []*ir.Fn
	var walkFns func(list []ir.Stmt)
	walkFns = func(list []ir.Stmt) {
		for _, s := range list {
			if fd, ok := s.(*ir.FnDef); ok {
				units[fd.Fn] = true
				order = append(order, fd.Fn)
				walkFns(fd.Fn.Body)
			}
		}
	}
	for _, f := range p.Fns {
		units[f] = true
		order = append(order, f)
		walkFns(f.Body)
	}

	direct := map[*ir.Fn]*fnDirect{}
	symToUnit := map[*semantic.Symbol]*ir.Fn{}
	for _, f := range order {
		direct[f] = &fnDirect{self: map[string]bool{}}
	}
	type semanticSymbol = struct{}
	_ = symToUnit
	// 函数字面量：效果归属定义所在函数（当前单元）
	var cur *ir.Fn
	var visitStmts func(list []ir.Stmt)
	var visitExpr func(e ir.Expr)

	addDirect := func(name string) {
		if n, ok := builtinEffects[name]; ok {
			direct[cur].self[n] = true
		}
	}

	visitExpr = func(e ir.Expr) {
		switch x := e.(type) {
		case *ir.Call:
			switch {
			case x.Builtin == "http.get":
				direct[cur].self[EffNET] = true
			case x.Builtin != "":
				addDirect(x.Builtin)
			case x.Fn != nil:
				if u, ok := symToUnit[x.Fn]; ok {
					direct[cur].callees = append(direct[cur].callees, u)
				}
			}
			if x.Recv != nil {
				visitExpr(x.Recv)
			}
			if x.Callee != nil {
				visitExpr(x.Callee)
			}
			for _, a := range x.Args {
				visitExpr(a)
			}
		case *ir.Ctor:
			for _, a := range x.Args {
				visitExpr(a)
			}
		case *ir.Unary:
			visitExpr(x.X)
		case *ir.Binary:
			visitExpr(x.L)
			visitExpr(x.R)
		case *ir.Range:
			visitExpr(x.Lo)
			visitExpr(x.Hi)
		case *ir.Index:
			visitExpr(x.Obj)
			visitExpr(x.Idx)
		case *ir.Slice:
			visitExpr(x.Obj)
			visitExpr(x.Lo)
			visitExpr(x.Hi)
		case *ir.Field:
			visitExpr(x.Obj)
		case *ir.Try:
			visitExpr(x.X)
		case *ir.StructLit:
			for _, f := range x.Fields {
				visitExpr(f.Val)
			}
		case *ir.MapLit:
			for i := range x.Keys {
				visitExpr(x.Keys[i])
				visitExpr(x.Vals[i])
			}
		case *ir.SliceLit:
			for _, el := range x.Elems {
				visitExpr(el)
			}
		case *ir.Match:
			visitExpr(x.Scrut)
			for _, arm := range x.Arms {
				visitExpr(arm.Body)
			}
		case *ir.FnLit:
			// 保守：字面量体的效果归当前单元（闭包定义处）
			saved := cur
			for _, st := range x.Body {
				visitStmts([]ir.Stmt{st})
			}
			cur = saved
		}
	}
	visitStmts = func(list []ir.Stmt) {
		for _, s := range list {
			switch x := s.(type) {
			case *ir.Let:
				visitExpr(x.Init)
			case *ir.Assign:
				visitExpr(x.Target)
				visitExpr(x.Val)
			case *ir.Eval:
				visitExpr(x.X)
			case *ir.Return:
				if x.Val != nil {
					visitExpr(x.Val)
				}
			case *ir.If:
				visitExpr(x.Cond)
				visitStmts(x.Then)
				visitStmts(x.Else)
			case *ir.While:
				visitExpr(x.Cond)
				visitStmts(x.Body)
			case *ir.ForIn:
				visitExpr(x.Iter)
				visitStmts(x.Body)
			case *ir.Spawn:
				direct[cur].self[EffSPAWN] = true
				if x.Fn != nil {
					if u, ok := symToUnit[x.Fn]; ok {
						direct[cur].callees = append(direct[cur].callees, u)
					}
				}
				for _, a := range x.Args {
					visitExpr(a)
				}
			case *ir.FnDef:
				// 嵌套 fn 是独立单元，已在 order 中；其效果不并入外层
			}
		}
	}
	for _, f := range order {
		cur = f
		visitStmts(f.Body)
	}

	// extern：直接效果 FFI（extern fn 是独立单元）
	for _, f := range order {
		if f.Extern {
			direct[f].self[EffFFI] = true
		}
	}

	// fixpoint 传播
	for changed := true; changed; {
		changed = false
		for _, f := range order {
			d := direct[f]
			for _, c := range d.callees {
				if c == nil || c == f {
					continue
				}
				cd := direct[c]
				if cd == nil {
					continue
				}
				for eff := range cd.self {
					if !d.self[eff] {
						d.self[eff] = true
						changed = true
					}
				}
			}
		}
	}

	// 输出（SymbolID 排序；预载/无名跳过）
	var out []Effect
	seen := map[*ir.Fn]bool{}
	for _, f := range order {
		if seen[f] || f.Sym == nil || f.Sym.BuiltinMark {
			continue
		}
		seen[f] = true
		out = append(out, Effect{
			SymbolID: int(f.Sym.ID),
			Name:     f.Name,
			Effects:  orderedEffects(direct[f].self),
		})
	}
	sortByID(out)
	return out
}

func sortByID(out []Effect) {
	for i := 1; i < len(out); i++ {
		for j := i; j > 0 && out[j].SymbolID < out[j-1].SymbolID; j-- {
			out[j], out[j-1] = out[j-1], out[j]
		}
	}
}

func orderedEffects(set map[string]bool) []string {
	if len(set) == 0 {
		return []string{EffPure}
	}
	out := make([]string, 0, len(set))
	for _, e := range effectOrder {
		if set[e] {
			out = append(out, e)
		}
	}
	return out
}
