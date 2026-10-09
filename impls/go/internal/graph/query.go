package graph

// query.go：图查询——callers / callees / references / impact / dependency。
// BFS 访问集防环；impact 区分 direct 与 transitive，每条证据带位置（保守证据集，
// 不宣称完整证明）。depth < 0 表示不限层。

import (
	"lang-go/internal/syntax"
	"sort"
)

// ImpactItem 一条影响证据.
type ImpactItem struct {
	From       int          // 影响源符号（引用/调用它的一方）
	Kind       EdgeKind     // 证据类别
	Span       *syntax.Span // 引用/调用位置
	Transitive bool         // 是否传递影响（经由调用链）
}

// FileDep 文件级依赖.
type FileDep struct {
	Module    string
	Imports   []string
	DependsOn []string
}

// Callers 反向调用 BFS：谁（直接/传递）调用 id.
func (g *Graph) Callers(id, depth int) []ImpactItem {
	return g.callBFS(id, true, depth)
}

// Callees 正向调用 BFS：id（直接/传递）调用谁.
func (g *Graph) Callees(id, depth int) []ImpactItem {
	return g.callBFS(id, false, depth)
}

func (g *Graph) callBFS(id int, reverse bool, depth int) []ImpactItem {
	visited := map[int]bool{id: true}
	level := []int{id}

	var out []ImpactItem

	d := 0
	for len(level) > 0 && (depth < 0 || d < depth) {
		var next []int

		for _, cur := range level {
			var edges []Edge
			if reverse {
				edges = g.In(cur)
			} else {
				edges = g.Out(cur)
			}

			for _, e := range edges {
				if e.Kind != EdgeCall {
					continue
				}

				other := e.From
				if !reverse {
					other = e.To
				}

				if visited[other] {
					continue
				}

				visited[other] = true
				next = append(next, other)
				out = append(out, ImpactItem{From: other, Transitive: d+1 > 1})
			}
		}

		level = next
		d++
	}

	sortImpactItems(out)

	return out
}

func sortImpactItems(items []ImpactItem) {
	sort.Slice(items, func(i, j int) bool { return items[i].From < items[j].From })
}

// Impact 对符号的影响分析：
//   - Direct：直接引用/写入/模式/类型/调用它的一切符号（含位置证据）
//   - Transitive：经调用链传递受影响的符号（效果沿调用图传播 → 传递调用方即效果关注集）
func (g *Graph) Impact(id, depth int) Impact {
	imp := Impact{}
	if n, ok := g.Nodes[id]; ok {
		imp.Symbol = n
	}

	seen := map[int]bool{id: true}

	// 直接影响：全部入边
	for _, e := range g.In(id) {
		if e.From == 0 {
			continue
		}

		imp.Direct = append(imp.Direct, ImpactItem{From: e.From, Kind: e.Kind, Span: e.Span})
		if e.Kind == EdgeCall && !seen[e.From] {
			seen[e.From] = true
			imp.EffectWatch = append(imp.EffectWatch, e.From)
		}
	}

	// 传递影响：沿调用边反向逐层
	level := []int{}

	for _, e := range g.In(id) {
		if e.Kind == EdgeCall && e.From > 0 && !seen[e.From] {
			// 理论不可达（上面已标记），防御性保留
		}
	}
	// 直接调用方作为第一层种子
	for _, e := range g.In(id) {
		if e.Kind == EdgeCall && e.From > 0 {
			level = append(level, e.From)
		}
	}

	d := 1
	for len(level) > 0 && (depth < 0 || d < depth) {
		var next []int

		for _, cur := range level {
			for _, e := range g.In(cur) {
				if e.From == 0 || seen[e.From] {
					continue
				}

				seen[e.From] = true
				next = append(next, e.From)
				imp.EffectWatch = append(imp.EffectWatch, e.From)
				it := ImpactItem{From: e.From, Kind: e.Kind, Transitive: true}
				imp.Transitive = appendUniqueItem(imp.Transitive, it)
			}
		}

		level = next
		d++
	}

	// 模块集合
	mods := map[string]bool{}

	for _, it := range append(append([]ImpactItem(nil), imp.Direct...), imp.Transitive...) {
		if n, ok := g.Nodes[it.From]; ok && n.Module != "" {
			mods[n.Module] = true
		}
	}

	imp.Modules = sortedKeys(mods)
	sortImpactItems(imp.Direct)
	sort.Slice(imp.Transitive, func(i, j int) bool { return imp.Transitive[i].From < imp.Transitive[j].From })
	sort.Slice(imp.EffectWatch, func(i, j int) bool { return imp.EffectWatch[i] < imp.EffectWatch[j] })

	return imp
}

// Impact 影响分析结果.
type Impact struct {
	Symbol      *Node
	Direct      []ImpactItem // 直接影响（全部入边类别）
	Transitive  []ImpactItem // 传递调用影响
	EffectWatch []int        // 效果变化关注集（传递调用方 SymbolID）
	Modules     []string     // 涉及文件
}

func appendUniqueItem(items []ImpactItem, it ImpactItem) []ImpactItem {
	for _, x := range items {
		if x.From == it.From && x.Kind == it.Kind {
			return items
		}
	}

	return append(items, it)
}

func sortedKeys(m map[string]bool) []string {
	var out []string
	for k := range m {
		out = append(out, k)
	}

	sort.Strings(out)

	return out
}

// FileDep 文件级依赖查询.
func (g *Graph) FileDep(module string) (FileDep, bool) {
	if len(g.Files) == 0 {
		// 空 Files（如从纯源码构建）——仅当声明过 imports 才有
		if imports, ok := g.FileImports[module]; ok {
			return FileDep{Module: module, Imports: imports}, true
		}

		return FileDep{}, false
	}

	if imports, ok := g.FileImports[module]; ok || g.hasFile(module) {
		dep := FileDep{Module: module}

		dep.Imports = append([]string(nil), imports...)

		set := map[string]bool{}
		for _, d := range g.FileDependsOn[module] {
			if !set[d] {
				set[d] = true
				dep.DependsOn = append(dep.DependsOn, d)
			}
		}

		if dep.DependsOn == nil {
			dep.DependsOn = []string{}
		}

		return dep, true
	}

	return FileDep{}, false
}

func (g *Graph) hasFile(module string) bool {
	for _, f := range g.Files {
		if f == module {
			return true
		}
	}

	return false
}
