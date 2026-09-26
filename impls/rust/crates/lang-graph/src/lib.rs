//! lang-graph —— 语义图：inspect.callers/callees/impact/references/dependency 查询面
//! （specs/agent-protocol.md）的公共结构。
//!
//! 节点身份 = [`lang_core::SymbolId`]（resolver 分配、refactor/inspect 消费）；
//! 模块级节点（Module/File）沿用同一编号空间。
//!
//! 确定性纪律（接口契约 §5）：全部容器 BTree，公开迭代序固定——节点按 SymbolId 升序、
//! 边按 (from, kind 声明序, to) 升序，禁 HashMap 迭代序外泄。
#![forbid(unsafe_code)]

pub mod fill;

use std::collections::{BTreeMap, BTreeSet};

use lang_core::SymbolId;

/// 语义图节点类别（冻结枚举，追加只能走协调人集成轮）。
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
pub enum NodeKind {
    Module,
    File,
    Function,
    Variable,
    Parameter,
    Struct,
    Field,
    Method,
    Enum,
    Variant,
    Type,
}

impl NodeKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            NodeKind::Module => "module",
            NodeKind::File => "file",
            NodeKind::Function => "function",
            NodeKind::Variable => "variable",
            NodeKind::Parameter => "parameter",
            NodeKind::Struct => "struct",
            NodeKind::Field => "field",
            NodeKind::Method => "method",
            NodeKind::Enum => "enum",
            NodeKind::Variant => "variant",
            NodeKind::Type => "type",
        }
    }
}

/// 语义图边类别（冻结枚举；Ord 推导 = 声明序 = 边排序的 kind 次序）。
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
pub enum EdgeKind {
    Defines,
    References,
    Calls,
    Imports,
    Returns,
    Reads,
    Writes,
    Contains,
    DependsOn,
    Spawn,
}

impl EdgeKind {
    pub fn as_str(&self) -> &'static str {
        match self {
            EdgeKind::Defines => "defines",
            EdgeKind::References => "references",
            EdgeKind::Calls => "calls",
            EdgeKind::Imports => "imports",
            EdgeKind::Returns => "returns",
            EdgeKind::Reads => "reads",
            EdgeKind::Writes => "writes",
            EdgeKind::Contains => "contains",
            EdgeKind::DependsOn => "depends_on",
            EdgeKind::Spawn => "spawn",
        }
    }
}

/// 图节点：身份 + 类别 + 名称。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct GraphNode {
    pub id: SymbolId,
    pub kind: NodeKind,
    pub name: String,
}

/// 有向边（语义方向：from → to，如 caller --calls→ callee）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct GraphEdge {
    pub from: SymbolId,
    pub kind: EdgeKind,
    pub to: SymbolId,
}

/// 图构建错误。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum GraphError {
    /// 节点重复登记。
    DuplicateNode(SymbolId),
    /// 边端点未登记（语义图必须良构：先节点后边）。
    UnknownNode(SymbolId),
}

impl std::fmt::Display for GraphError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            GraphError::DuplicateNode(id) => write!(f, "图节点重复登记: {id}"),
            GraphError::UnknownNode(id) => write!(f, "边端点未登记: {id}"),
        }
    }
}

impl std::error::Error for GraphError {}

/// 确定性语义图容器。
///
/// - 节点存 BTreeMap<SymbolId, _>，`nodes()` 按 id 升序；
/// - 边存 BTreeSet<GraphEdge>，`edges()` 按 (from, kind, to) 升序且天然去重。
#[derive(Clone, Debug, Default)]
pub struct Graph {
    nodes: BTreeMap<SymbolId, GraphNode>,
    edges: BTreeSet<GraphEdge>,
}

impl Graph {
    pub fn new() -> Self {
        Self::default()
    }

    /// 登记节点；同 id 重复登记报错（防调用方静默改写已发布身份）。
    pub fn add_node(
        &mut self,
        id: SymbolId,
        kind: NodeKind,
        name: impl Into<String>,
    ) -> Result<(), GraphError> {
        let node = GraphNode {
            id,
            kind,
            name: name.into(),
        };
        if self.nodes.insert(id, node).is_some() {
            return Err(GraphError::DuplicateNode(id));
        }
        Ok(())
    }

    /// 加边；端点必须已登记，重复边幂等（集合语义）。
    pub fn add_edge(&mut self, edge: GraphEdge) -> Result<(), GraphError> {
        if !self.nodes.contains_key(&edge.from) {
            return Err(GraphError::UnknownNode(edge.from));
        }
        if !self.nodes.contains_key(&edge.to) {
            return Err(GraphError::UnknownNode(edge.to));
        }
        self.edges.insert(edge);
        Ok(())
    }

    pub fn node(&self, id: SymbolId) -> Option<&GraphNode> {
        self.nodes.get(&id)
    }

    /// 全部节点，SymbolId 升序（canonical 序）。
    pub fn nodes(&self) -> impl Iterator<Item = &GraphNode> {
        self.nodes.values()
    }

    /// 全部边，(from, kind, to) 升序（canonical 序）。
    pub fn edges(&self) -> impl Iterator<Item = &GraphEdge> {
        self.edges.iter()
    }

    /// 以 from 为起点的边（kind 声明序 → to 升序；inspect.callees/impact 的直接集）。
    pub fn edges_from(&self, from: SymbolId) -> impl Iterator<Item = &GraphEdge> {
        self.edges
            .range(from_edge_lower(from)..from_edge_upper(from))
    }

    /// 以 to 为终点的边（inspect.callers 的直接入边集）。
    pub fn edges_to(&self, to: SymbolId) -> impl Iterator<Item = &GraphEdge> {
        self.edges.iter().filter(move |e| e.to == to)
    }

    pub fn node_count(&self) -> usize {
        self.nodes.len()
    }

    pub fn edge_count(&self) -> usize {
        self.edges.len()
    }
}

/// SymbolId(u32) → 边空间下界键（from 固定，kind/to 取最小）。
fn from_edge_lower(from: SymbolId) -> GraphEdge {
    GraphEdge {
        from,
        kind: EdgeKind::Defines,
        to: SymbolId(u32::MIN),
    }
}

/// SymbolId(u32) → 边空间上界开区间哨兵（from+1 的最小键）。
fn from_edge_upper(from: SymbolId) -> GraphEdge {
    GraphEdge {
        from: SymbolId(from.0.saturating_add(1)),
        kind: EdgeKind::Defines,
        to: SymbolId(u32::MIN),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sid(n: u32) -> SymbolId {
        SymbolId(n)
    }

    fn edge(from: u32, kind: EdgeKind, to: u32) -> GraphEdge {
        GraphEdge {
            from: sid(from),
            kind,
            to: sid(to),
        }
    }

    #[test]
    fn node_edge_strings_frozen() {
        // 冻结字符串形（契约面：inspect JSON 会用到）
        assert_eq!(NodeKind::Module.as_str(), "module");
        assert_eq!(NodeKind::Variant.as_str(), "variant");
        assert_eq!(EdgeKind::DependsOn.as_str(), "depends_on");
        assert_eq!(EdgeKind::Spawn.as_str(), "spawn");
    }

    /// 同一图、不同登记顺序 → 公开迭代逐项相等（禁 HashMap 序外泄）。
    #[test]
    fn iteration_is_insertion_order_independent() {
        let build = |scrambled: bool| {
            let mut g = Graph::new();
            let ids: [(u32, NodeKind, &str); 5] = [
                (1, NodeKind::Function, "main"),
                (2, NodeKind::Function, "helper"),
                (3, NodeKind::Struct, "Point"),
                (4, NodeKind::Field, "x"),
                (5, NodeKind::Module, "lib"),
            ];
            let order: Vec<usize> = if scrambled {
                vec![3, 0, 4, 1, 2]
            } else {
                vec![0, 1, 2, 3, 4]
            };
            for i in order {
                let (id, kind, name) = ids[i];
                g.add_node(sid(id), kind, name).unwrap();
            }
            let es = [
                edge(1, EdgeKind::Calls, 2),
                edge(1, EdgeKind::References, 3),
                edge(3, EdgeKind::Contains, 4),
                edge(5, EdgeKind::Contains, 3),
                edge(1, EdgeKind::Spawn, 2),
            ];
            let order_e: Vec<usize> = if scrambled {
                vec![4, 2, 0, 3, 1]
            } else {
                vec![0, 1, 2, 3, 4]
            };
            for i in order_e {
                g.add_edge(es[i]).unwrap();
            }
            let nodes: Vec<(u32, &'static str, String)> = g
                .nodes()
                .map(|n| (n.id.0, n.kind.as_str(), n.name.clone()))
                .collect();
            let edges: Vec<(u32, &'static str, u32)> = g
                .edges()
                .map(|e| (e.from.0, e.kind.as_str(), e.to.0))
                .collect();
            (nodes, edges)
        };
        let (plain_nodes, plain_edges) = build(false);
        let (scrambled_nodes, scrambled_edges) = build(true);
        assert_eq!(plain_nodes, scrambled_nodes);
        assert_eq!(plain_edges, scrambled_edges);
        // canonical 序的形状：节点 id 升序；边 (from, kind 声明序, to)
        assert_eq!(
            plain_nodes.iter().map(|n| n.0).collect::<Vec<_>>(),
            vec![1, 2, 3, 4, 5]
        );
        assert_eq!(
            plain_edges,
            vec![
                (1, "references", 3),
                (1, "calls", 2),
                (1, "spawn", 2),
                (3, "contains", 4),
                (5, "contains", 3),
            ]
        );
    }

    /// 重复边幂等、重复节点/悬边报错。
    #[test]
    fn edge_set_semantics_and_validation() {
        let mut g = Graph::new();
        g.add_node(sid(1), NodeKind::Function, "a").unwrap();
        g.add_node(sid(2), NodeKind::Function, "b").unwrap();
        g.add_edge(edge(1, EdgeKind::Calls, 2)).unwrap();
        g.add_edge(edge(1, EdgeKind::Calls, 2)).unwrap();
        assert_eq!(g.edge_count(), 1);
        assert_eq!(
            g.add_node(sid(1), NodeKind::Variable, "dup"),
            Err(GraphError::DuplicateNode(sid(1)))
        );
        assert_eq!(
            g.add_edge(edge(1, EdgeKind::Calls, 9)),
            Err(GraphError::UnknownNode(sid(9)))
        );
        assert_eq!(
            g.add_edge(edge(9, EdgeKind::Calls, 1)),
            Err(GraphError::UnknownNode(sid(9)))
        );
    }

    /// callers（edges_to）/ callees（edges_from）直接集，按 canonical 序给出。
    #[test]
    fn direct_in_out_edges() {
        let mut g = Graph::new();
        for (id, name) in [(1u32, "main"), (2, "log"), (3, "fetch"), (4, "sink")] {
            g.add_node(sid(id), NodeKind::Function, name).unwrap();
        }
        g.add_edge(edge(1, EdgeKind::Calls, 2)).unwrap();
        g.add_edge(edge(1, EdgeKind::Calls, 3)).unwrap();
        g.add_edge(edge(4, EdgeKind::Calls, 2)).unwrap();
        g.add_edge(edge(1, EdgeKind::References, 2)).unwrap();

        let callees: Vec<(u32, &str)> = g
            .edges_from(sid(1))
            .map(|e| (e.to.0, e.kind.as_str()))
            .collect();
        assert_eq!(callees, vec![(2, "references"), (2, "calls"), (3, "calls")]);

        let callers: Vec<u32> = g.edges_to(sid(2)).map(|e| e.from.0).collect();
        assert_eq!(callers, vec![1, 1, 4]);
    }
}
