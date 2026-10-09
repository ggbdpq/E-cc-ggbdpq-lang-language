package contract

// graph.go：Semantic Graph 的契约 JSON 包装（v0.6）。
// 数据源 internal/graph（semantic/types/HIR/IR 推导）；此处只做协议形态转换。

import (
	"lang-go/internal/graph"
	"lang-go/internal/syntax"
)

// GraphNode 图节点（Symbol 粒度）.
type GraphNode struct {
	SymbolID int      `json:"symbolId"`
	Kind     string   `json:"kind"`
	Name     string   `json:"name"`
	Module   string   `json:"module"`
	Type     string   `json:"type,omitempty"`
	Effects  []string `json:"effects,omitempty"`
	Span     *Span    `json:"span,omitempty"`
}

// ImpactItemJSON 影响证据.
type ImpactItemJSON struct {
	SymbolID   int    `json:"symbolId"`
	Name       string `json:"name"`
	Kind       string `json:"kind"`
	Module     string `json:"module,omitempty"`
	Transitive bool   `json:"transitive"`
	Span       *Span  `json:"span,omitempty"`
}

// ImpactJSON 影响分析负载.
type ImpactJSON struct {
	Symbol      GraphNode        `json:"symbol"`
	Direct      []ImpactItemJSON `json:"direct"`
	Transitive  []ImpactItemJSON `json:"transitive"`
	EffectWatch []int            `json:"effectWatch"`
	Modules     []string         `json:"modules"`
	Note        string           `json:"note"`
}

// FileDepJSON 文件级依赖负载.
type FileDepJSON struct {
	Module    string   `json:"module"`
	Imports   []string `json:"imports"`
	DependsOn []string `json:"dependsOn"`
}

// CallersEnvelope inspect callers --json.
func CallersEnvelope(g *graph.Graph, items []graph.ImpactItem) Envelope {
	return Envelope{
		SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectCallers,
		Callers: impactItemsJSON(g, items),
	}
}

// CalleesEnvelope inspect callees --json.
func CalleesEnvelope(g *graph.Graph, items []graph.ImpactItem) Envelope {
	return Envelope{
		SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectCallees,
		Callees: impactItemsJSON(g, items),
	}
}

// ImpactEnvelope inspect impact --json.
func ImpactEnvelope(imp graph.Impact) Envelope {
	out := ImpactJSON{
		Direct:      []ImpactItemJSON{},
		Transitive:  []ImpactItemJSON{},
		EffectWatch: []int{},
		Modules:     imp.Modules,
		Note:        "保守证据集：列出可能受影响的符号与位置，不构成完整影响证明",
	}
	if imp.Symbol != nil {
		out.Symbol = nodeJSON(*imp.Symbol)
	}
	for _, it := range imp.Direct {
		out.Direct = append(out.Direct, impactItemJSON(it))
	}
	for _, it := range imp.Transitive {
		out.Transitive = append(out.Transitive, impactItemJSON(it))
	}
	out.EffectWatch = append([]int(nil), imp.EffectWatch...)
	if out.EffectWatch == nil {
		out.EffectWatch = []int{}
	}
	return Envelope{SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectImpact, Impact: &out}
}

// GraphNodeJSON 图节点 → 契约节点形态.
func GraphNodeJSON(n graph.Node) GraphNode {
	effs := n.Effects
	if effs == nil {
		effs = []string{}
	}
	sp := n.Span
	return GraphNode{
		SymbolID: n.ID,
		Kind:     n.Kind,
		Name:     n.Name,
		Module:   n.Module,
		Type:     n.Type,
		Effects:  effs,
		Span:     SpanJSON(sp),
	}
}

func nodeJSON(n graph.Node) GraphNode { return GraphNodeJSON(n) }

func impactItemJSON(it graph.ImpactItem) ImpactItemJSON {
	return ImpactItemJSON{
		SymbolID:   it.From,
		Kind:       string(it.Kind),
		Transitive: it.Transitive,
		Span:       spanPtr(it.Span),
	}
}

func impactItemsJSON(g *graph.Graph, items []graph.ImpactItem) []ImpactItemJSON {
	out := make([]ImpactItemJSON, 0, len(items))
	for _, it := range items {
		name, kind, module := "", "", ""
		if n, ok := g.Nodes[it.From]; ok {
			name, kind, module = n.Name, n.Kind, n.Module
		}
		out = append(out, ImpactItemJSON{
			SymbolID:   it.From,
			Name:       name,
			Kind:       kind,
			Module:     module,
			Transitive: it.Transitive,
			Span:       spanPtr(it.Span),
		})
	}
	return out
}

func spanPtr(sp *syntax.Span) *Span {
	if sp == nil {
		return nil
	}
	return SpanJSON(*sp)
}

// GraphDepJSON 文件依赖负载.
func GraphDepJSON(dep graph.FileDep) Envelope {
	imports := dep.Imports
	if imports == nil {
		imports = []string{}
	}
	dependsOn := dep.DependsOn
	if dependsOn == nil {
		dependsOn = []string{}
	}
	return Envelope{
		SchemaVersion: SchemaVersion, ToolVersion: ToolVersion, Kind: KindInspectDependency,
		Dependency: &FileDepJSON{Module: dep.Module, Imports: imports, DependsOn: dependsOn},
	}
}
