package contract

// effects.go：Effect 契约负载——推导在 internal/graph（v0.6 起唯一实现），此处仅转换。

import (
	"lang-go/internal/graph"
	"lang-go/internal/ir"
)

// Effect 函数效果元数据.
type Effect struct {
	SymbolID int      `json:"symbolId"`
	Name     string   `json:"name"`
	Effects  []string `json:"effects"`
}

// Effect 枚举值（契约词汇；FS/TIME/RANDOM 为保留值，当前语言无生产者）.
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

// Effects 推导全部函数效果（按 SymbolID 排序，效果按固定序去重）.
func Effects(p *ir.Prog) []Effect {
	ge := graph.Effects(p)
	out := make([]Effect, 0, len(ge))
	for _, e := range ge {
		effs := e.Effects
		if effs == nil {
			effs = []string{}
		}
		out = append(out, Effect{SymbolID: e.SymbolID, Name: e.Name, Effects: effs})
	}
	return out
}
