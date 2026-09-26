//! Effects fixpoint 引擎（specs/runtime.md EFFECT-001 + specs/agent-protocol.md §3 Effect）。
//!
//! 效果是**元数据**，不是约束系统：无效果类型注解、无静态效果检查。
//! 本引擎只做「直接效果 → 调用图 → 不动点」三步；输入是泛型调用图
//! （节点 = FunctionId + 函数名，边 = 调用/go 目标），本波不接语义核——
//! 直接效果从 IR 的提取（println→IO、go 语句→SPAWN 等）由 IR 接线方调用
//! [`builtin_direct_effect`] 与 `set_direct` 完成。
//!
//! 传播语义（保守）：caller 的效果集 ⊇ 全部被调函数的效果集；
//! `go f()` 产生直接 SPAWN，并对 f 的调用边照常传播（并发效果的保守近似）。
//! 格是 {9 个效果} 的幂集，单调增长必有不动点——递归/互递归/纯环都收敛。

use std::collections::{BTreeMap, BTreeSet};

use lang_core::FunctionId;

/// 效果枚举（契约顺序 = [`Effect::ALL`] 声明序：PURE,IO,FS,NET,TIME,RANDOM,SPAWN,CHANNEL,FFI）。
///
/// FS/TIME/RANDOM 为保留枚举——当前语言无生产者（specs/runtime.md EFFECT-001）。
#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Debug)]
pub enum Effect {
    Pure,
    Io,
    Fs,
    Net,
    Time,
    Random,
    Spawn,
    Channel,
    Ffi,
}

impl Effect {
    /// 固定契约顺序（输出去重按此序；禁 alphabet/HashMap 序）。
    pub const ALL: [Effect; 9] = [
        Effect::Pure,
        Effect::Io,
        Effect::Fs,
        Effect::Net,
        Effect::Time,
        Effect::Random,
        Effect::Spawn,
        Effect::Channel,
        Effect::Ffi,
    ];

    pub fn as_str(&self) -> &'static str {
        match self {
            Effect::Pure => "PURE",
            Effect::Io => "IO",
            Effect::Fs => "FS",
            Effect::Net => "NET",
            Effect::Time => "TIME",
            Effect::Random => "RANDOM",
            Effect::Spawn => "SPAWN",
            Effect::Channel => "CHANNEL",
            Effect::Ffi => "FFI",
        }
    }
}

/// 内建调用名 → 直接效果（specs/agent-protocol.md §3 推导规则；未列出的内建视为 PURE）。
///
/// extern 声明（→FFI）与 go 语句（→SPAWN）来自 IR 结构而非调用名，由 IR 接线方直接 set_direct。
pub fn builtin_direct_effect(builtin: &str) -> Option<Effect> {
    match builtin {
        "println" | "print" => Some(Effect::Io),
        "http.get" => Some(Effect::Net),
        "makeChan" | "send" | "recv" => Some(Effect::Channel),
        _ => None,
    }
}

/// 单个函数的效果查询条目（effects 数组按契约序去重；无效果时为 `[PURE]`）。
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EffectEntry {
    pub id: FunctionId,
    pub name: String,
    pub effects: Vec<Effect>,
}

/// 泛型调用图 + 直接效果输入。
///
/// 节点必须先 `add_fn` 登记才能挂效果/边；全部容器用 BTree，求解输出按
/// FunctionId 升序，迭代序确定（接口契约 §5：禁 HashMap 迭代序外泄）。
#[derive(Clone, Debug, Default)]
pub struct CallGraph {
    names: BTreeMap<FunctionId, String>,
    direct: BTreeMap<FunctionId, BTreeSet<Effect>>,
    /// caller → 被调集合（普通调用与 go 目标共用；spawn 的直接 SPAWN 由调用方另 set_direct）。
    edges: BTreeMap<FunctionId, BTreeSet<FunctionId>>,
}

impl CallGraph {
    pub fn new() -> Self {
        Self::default()
    }

    /// 登记函数节点；重复登记报错（防调用方把同名函数当同一节点静默合并）。
    pub fn add_fn(&mut self, id: FunctionId, name: impl Into<String>) -> Result<(), EffectsError> {
        if self.names.insert(id, name.into()).is_some() {
            return Err(EffectsError::DuplicateFn(id));
        }
        Ok(())
    }

    /// 挂一条直接效果（可多次调用累积）。
    pub fn set_direct(&mut self, id: FunctionId, effect: Effect) -> Result<(), EffectsError> {
        self.node_mut(id)?.insert(effect);
        Ok(())
    }

    /// 由内建调用名推导并挂直接效果；非效果内建（视为 PURE）静默忽略。
    pub fn set_builtin_call(&mut self, id: FunctionId, builtin: &str) -> Result<(), EffectsError> {
        if let Some(e) = builtin_direct_effect(builtin) {
            self.set_direct(id, e)?;
        }
        Ok(())
    }

    /// 加一条 caller → callee 边（go 目标同用）；端点必须已登记，重复边幂等。
    pub fn add_call(&mut self, caller: FunctionId, callee: FunctionId) -> Result<(), EffectsError> {
        if !self.names.contains_key(&callee) {
            return Err(EffectsError::UnknownFn(callee));
        }
        if !self.names.contains_key(&caller) {
            return Err(EffectsError::UnknownFn(caller));
        }
        self.edges.entry(caller).or_default().insert(callee);
        Ok(())
    }

    pub fn has_fn(&self, id: FunctionId) -> bool {
        self.names.contains_key(&id)
    }

    /// 不动点求解：直接效果沿调用边传播到收敛；结果按 FunctionId 升序。
    pub fn solve(&self) -> Vec<EffectEntry> {
        // 初始 = 直接效果；eff 值单调增长，格有限 → 必收敛。
        let mut eff: BTreeMap<FunctionId, BTreeSet<Effect>> = self.direct.clone();
        loop {
            // 先按只读快照收集每个 caller 的新增效果，再统一写入（避开借用冲突）。
            let mut updates: Vec<(FunctionId, Vec<Effect>)> = Vec::new();
            for (caller, callees) in &self.edges {
                let mut gained: BTreeSet<Effect> = BTreeSet::new();
                for callee in callees {
                    if let Some(callee_eff) = eff.get(callee) {
                        gained.extend(callee_eff.iter().copied());
                    }
                }
                let new: Vec<Effect> = match eff.get(caller) {
                    Some(cur) => gained.difference(cur).copied().collect(),
                    None => gained.iter().copied().collect(),
                };
                if !new.is_empty() {
                    updates.push((*caller, new));
                }
            }
            if updates.is_empty() {
                break;
            }
            for (caller, effects) in updates {
                eff.entry(caller).or_default().extend(effects);
            }
        }
        self.names
            .iter()
            .map(|(id, name)| EffectEntry {
                id: *id,
                name: name.clone(),
                effects: ordered(&eff.get(id).cloned().unwrap_or_default()),
            })
            .collect()
    }

    fn node_mut(&mut self, id: FunctionId) -> Result<&mut BTreeSet<Effect>, EffectsError> {
        if !self.names.contains_key(&id) {
            return Err(EffectsError::UnknownFn(id));
        }
        Ok(self.direct.entry(id).or_default())
    }
}

/// 效果集 → 契约输出序：去重按 ALL 声明序；空集输出 `[PURE]`。
fn ordered(set: &BTreeSet<Effect>) -> Vec<Effect> {
    if set.is_empty() {
        return vec![Effect::Pure];
    }
    Effect::ALL
        .iter()
        .copied()
        .filter(|e| set.contains(e))
        .collect()
}

/// 效果图输入错误（节点未登记 / 重复登记）。
#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum EffectsError {
    DuplicateFn(FunctionId),
    UnknownFn(FunctionId),
}

impl std::fmt::Display for EffectsError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            EffectsError::DuplicateFn(id) => write!(f, "函数节点重复登记: {id}"),
            EffectsError::UnknownFn(id) => write!(f, "函数节点未登记: {id}"),
        }
    }
}

impl std::error::Error for EffectsError {}

#[cfg(test)]
mod tests {
    use super::*;

    fn id(n: u32) -> FunctionId {
        FunctionId(n)
    }

    /// 对抗验证重点 Effects 节 1/2：direct effect 与纯函数缺省 PURE。
    #[test]
    fn direct_effect_and_default_pure() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "show").unwrap();
        g.add_fn(id(2), "add").unwrap();
        g.set_builtin_call(id(1), "println").unwrap();
        let out = g.solve();
        assert_eq!(
            out,
            vec![
                EffectEntry {
                    id: id(1),
                    name: "show".into(),
                    effects: vec![Effect::Io]
                },
                EffectEntry {
                    id: id(2),
                    name: "add".into(),
                    effects: vec![Effect::Pure]
                },
            ]
        );
    }

    /// 对抗验证重点 Effects 节 3：1 级传播。
    #[test]
    fn one_level_propagation() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "main").unwrap();
        g.add_fn(id(2), "helper").unwrap();
        g.set_builtin_call(id(2), "println").unwrap();
        g.add_call(id(1), id(2)).unwrap();
        let out = g.solve();
        assert_eq!(out[0].effects, vec![Effect::Io], "main 传播获得 IO");
        assert_eq!(out[1].effects, vec![Effect::Io]);
    }

    /// 对抗验证重点 Effects 节 4：3 级传播（a→b→c→IO 全链获得）。
    #[test]
    fn three_level_propagation() {
        let mut g = CallGraph::new();
        for (n, name) in [(1u32, "a"), (2, "b"), (3, "c")] {
            g.add_fn(id(n), name).unwrap();
        }
        g.set_builtin_call(id(3), "http.get").unwrap();
        g.add_call(id(1), id(2)).unwrap();
        g.add_call(id(2), id(3)).unwrap();
        for e in g.solve() {
            assert_eq!(e.effects, vec![Effect::Net], "{} 应传播获得 NET", e.name);
        }
    }

    /// 对抗验证重点 Effects 节 5：自递归收敛（单调格保证不动点）。
    #[test]
    fn recursion_converges() {
        let mut g = CallGraph::new();
        g.add_fn(id(10), "fact").unwrap();
        g.set_builtin_call(id(10), "println").unwrap();
        g.add_call(id(10), id(10)).unwrap();
        let out = g.solve();
        assert_eq!(out.len(), 1);
        assert_eq!(out[0].effects, vec![Effect::Io]);
    }

    /// 对抗验证重点 Effects 节 6：互递归——效果经环双向传播。
    #[test]
    fn mutual_recursion_shares_effects() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "is_even").unwrap();
        g.add_fn(id(2), "is_odd").unwrap();
        g.set_builtin_call(id(2), "recv").unwrap();
        g.add_call(id(1), id(2)).unwrap();
        g.add_call(id(2), id(1)).unwrap();
        for e in g.solve() {
            assert_eq!(
                e.effects,
                vec![Effect::Channel],
                "{} 应获得 CHANNEL",
                e.name
            );
        }
    }

    /// 对抗验证重点 Effects 节 7：go 语句 = 直接 SPAWN + 被调效果保守并入。
    #[test]
    fn spawn_gives_spawn_plus_callee_effects() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "main").unwrap();
        g.add_fn(id(2), "worker").unwrap();
        g.set_builtin_call(id(2), "println").unwrap();
        // go worker()：直接 SPAWN + 调用边（保守传播）
        g.set_direct(id(1), Effect::Spawn).unwrap();
        g.add_call(id(1), id(2)).unwrap();
        let out = g.solve();
        // 契约序 IO < SPAWN：main 为 ["IO","SPAWN"]
        assert_eq!(out[0].effects, vec![Effect::Io, Effect::Spawn]);
        assert_eq!(out[1].effects, vec![Effect::Io]);
    }

    /// 对抗验证重点 Effects 节 8：channel 三内建全部落在 CHANNEL。
    #[test]
    fn channel_builtins() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "produce").unwrap();
        g.set_builtin_call(id(1), "makeChan").unwrap();
        g.set_builtin_call(id(1), "send").unwrap();
        g.set_builtin_call(id(1), "recv").unwrap();
        let out = g.solve();
        assert_eq!(out[0].effects, vec![Effect::Channel]);
    }

    /// 对抗验证重点 Effects 节 9：纯环——互调无效果，双方保持 PURE。
    #[test]
    fn pure_cycle_stays_pure() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "ping").unwrap();
        g.add_fn(id(2), "pong").unwrap();
        g.add_call(id(1), id(2)).unwrap();
        g.add_call(id(2), id(1)).unwrap();
        for e in g.solve() {
            assert_eq!(
                e.effects,
                vec![Effect::Pure],
                "{} 纯环不得凭空长效果",
                e.name
            );
        }
    }

    /// 输出去重按契约序，与插入顺序无关（禁迭代序外泄）。
    #[test]
    fn effects_emitted_in_contract_order() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "m").unwrap();
        g.set_direct(id(1), Effect::Ffi).unwrap();
        g.set_direct(id(1), Effect::Io).unwrap();
        g.set_direct(id(1), Effect::Spawn).unwrap();
        let out = g.solve();
        assert_eq!(out[0].effects, vec![Effect::Io, Effect::Spawn, Effect::Ffi]);
    }

    /// 同一内容、不同登记顺序 → 求解输出逐项相等（确定性）。
    #[test]
    fn solve_is_insertion_order_independent() {
        let build = |reversed: bool| {
            let mut g = CallGraph::new();
            let order: Vec<u32> = if reversed {
                vec![3, 2, 1]
            } else {
                vec![1, 2, 3]
            };
            for n in order {
                g.add_fn(id(n), format!("f{n}")).unwrap();
            }
            g.set_builtin_call(id(3), "println").unwrap();
            g.add_call(id(1), id(2)).unwrap();
            g.add_call(id(2), id(3)).unwrap();
            g.solve()
        };
        assert_eq!(build(false), build(true));
    }

    /// 混合传播：多个 callee 的效果并集进入 caller。
    #[test]
    fn caller_unions_all_callee_effects() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "main").unwrap();
        g.add_fn(id(2), "log").unwrap();
        g.add_fn(id(3), "fetch").unwrap();
        g.set_builtin_call(id(2), "println").unwrap();
        g.set_builtin_call(id(3), "http.get").unwrap();
        g.add_call(id(1), id(2)).unwrap();
        g.add_call(id(1), id(3)).unwrap();
        let out = g.solve();
        assert_eq!(out[0].effects, vec![Effect::Io, Effect::Net]);
    }

    /// extern 形状的 FFI 由调用方 set_direct（IR 接线点）；内建映射表锁定。
    #[test]
    fn builtin_mapping_table() {
        assert_eq!(builtin_direct_effect("println"), Some(Effect::Io));
        assert_eq!(builtin_direct_effect("print"), Some(Effect::Io));
        assert_eq!(builtin_direct_effect("http.get"), Some(Effect::Net));
        assert_eq!(builtin_direct_effect("makeChan"), Some(Effect::Channel));
        assert_eq!(builtin_direct_effect("send"), Some(Effect::Channel));
        assert_eq!(builtin_direct_effect("recv"), Some(Effect::Channel));
        assert_eq!(builtin_direct_effect("len"), None);
        // extern 声明不是调用名：IR 接线方对 extern fn 直接挂 FFI
        let mut g = CallGraph::new();
        g.add_fn(id(1), "hostWrite").unwrap();
        g.set_direct(id(1), Effect::Ffi).unwrap();
        assert_eq!(g.solve()[0].effects, vec![Effect::Ffi]);
    }

    /// 未登记/重复登记被拒绝——泛型输入的良构性由构造期保证。
    #[test]
    fn rejects_unregistered_and_duplicate_nodes() {
        let mut g = CallGraph::new();
        g.add_fn(id(1), "a").unwrap();
        assert_eq!(g.add_fn(id(1), "a2"), Err(EffectsError::DuplicateFn(id(1))));
        assert_eq!(
            g.add_call(id(1), id(2)),
            Err(EffectsError::UnknownFn(id(2)))
        );
        assert_eq!(
            g.add_call(id(9), id(1)),
            Err(EffectsError::UnknownFn(id(9)))
        );
        assert_eq!(
            g.set_direct(id(9), Effect::Io),
            Err(EffectsError::UnknownFn(id(9)))
        );
    }
}
