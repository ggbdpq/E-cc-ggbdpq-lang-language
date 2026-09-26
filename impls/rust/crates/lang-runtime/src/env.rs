//! 变量环境 arena：cell 语义 + 闭包按引用捕获（VALUE-CAPTURE-004）。
//!
//! 每个变量是一个 cell `{ value, mutable }`，环境链经父指针解析；闭包保存
//! **定义处环境的 EnvId**——cell 在 arena 中共享，定义作用域与闭包体读写同一
//! cell，这就是「按引用捕获」的全部机制。循环体每迭代新建子环境
//! （运行时证据：TS/Go 均按迭代建 Env），闭包捕获的循环变量互不串扰。

use indexmap::IndexMap;

use crate::error::{RtError, RtErrorKind, RtResult};
use crate::value::Value;

/// 环境身份（arena 下标 newtype）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct EnvId(pub u32);

impl From<EnvId> for usize {
    fn from(v: EnvId) -> usize {
        v.0 as usize
    }
}

impl std::fmt::Display for EnvId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// 变量 cell：值 + 可变性（`var` 可写、`let` 不可写；捕获语义随 cell）。
#[derive(Clone, Debug, PartialEq)]
pub struct Cell {
    pub value: Value,
    pub mutable: bool,
}

/// 环境对象：父链 + 有序变量表。
#[derive(Debug, Default)]
pub struct EnvObj {
    parent: Option<EnvId>,
    vars: IndexMap<String, Cell>,
}

/// 环境 arena。
#[derive(Debug, Default)]
pub struct EnvArena {
    envs: Vec<EnvObj>,
}

impl EnvArena {
    pub fn new() -> EnvArena {
        EnvArena { envs: Vec::new() }
    }

    /// 根环境（全局）。
    pub fn root(&mut self) -> EnvId {
        self.child_of(None)
    }

    /// 子环境（块/函数调用帧/循环每迭代）。
    pub fn child(&mut self, parent: EnvId) -> EnvId {
        self.child_of(Some(parent))
    }

    fn child_of(&mut self, parent: Option<EnvId>) -> EnvId {
        let id = EnvId(self.envs.len() as u32);
        self.envs.push(EnvObj {
            parent,
            vars: IndexMap::new(),
        });
        id
    }

    fn env(&self, id: EnvId) -> RtResult<&EnvObj> {
        self.envs
            .get(usize::from(id))
            .ok_or_else(|| RtError::internal(format!("EnvId({}) 越界", usize::from(id))))
    }

    fn env_mut(&mut self, id: EnvId) -> RtResult<&mut EnvObj> {
        let n = self.envs.len();
        self.envs.get_mut(usize::from(id)).ok_or_else(|| {
            RtError::internal(format!("EnvId({}) 越界（共 {} 环境）", usize::from(id), n))
        })
    }

    /// 在指定环境定义变量，返回槽位号。重复定义 → `RtErrorKind::Scope`
    /// （正常程序被静态拒绝，此处兜底）。
    pub fn define(&mut self, env: EnvId, name: &str, value: Value, mutable: bool) -> RtResult<u32> {
        let obj = self.env_mut(env)?;
        if obj.vars.contains_key(name) {
            return Err(RtError::new(
                RtErrorKind::Scope,
                format!("{} 在此作用域重复定义", name),
            ));
        }
        let slot = obj.vars.len() as u32;
        obj.vars.insert(name.to_string(), Cell { value, mutable });
        Ok(slot)
    }

    /// 沿父链解析变量名，返回 **属主** 环境与槽位（按引用捕获的关键：
    /// 拿到属主 cell 才能共享读写）。
    pub fn lookup(&self, env: EnvId, name: &str) -> RtResult<Option<(EnvId, u32)>> {
        let mut cur = Some(env);
        while let Some(id) = cur {
            let obj = self.env(id)?;
            if let Some((slot, _key, _cell)) = obj.vars.get_full(name) {
                return Ok(Some((id, slot as u32)));
            }
            cur = obj.parent;
        }
        Ok(None)
    }

    /// 读 cell 值（clone；标量廉价，容器共享索引）。
    pub fn get(&self, env: EnvId, slot: u32) -> RtResult<Value> {
        Ok(self.cell(env, slot)?.value.clone())
    }

    /// 函数声明面：插入或覆盖（顶层/局部 `fn` 声明语句可重复执行，后者覆盖
    /// 前者——与证据 `env.redefine` 一致；绑定量恒不可变）。
    pub fn redefine(&mut self, env: EnvId, name: &str, value: Value) -> RtResult<u32> {
        let obj = self.env_mut(env)?;
        if let Some((slot, _, cell)) = obj.vars.get_full_mut(name) {
            cell.value = value;
            cell.mutable = false;
            return Ok(slot as u32);
        }
        let slot = obj.vars.len() as u32;
        obj.vars.insert(
            name.to_string(),
            Cell {
                value,
                mutable: false,
            },
        );
        Ok(slot)
    }

    /// 字段写回路径专用：把「含字段更新的 struct 值」写回 base 槽位。
    ///
    /// 这不是对绑定的重新赋值（E2005 是静态面）：绑定不可重绑 ≠ 值的字段
    /// 不可变——`let p = P{..}; p.x = 1` 更新的是 p 所持值的字段（conformance
    /// struct-field-write / nested-struct-copy 的行为真值）。variable 级
    /// 可变性检查只在 [`EnvArena::set`]（重绑定路径）执行。
    pub fn set_field_writeback(&mut self, env: EnvId, slot: u32, value: Value) -> RtResult<()> {
        let obj = self.env_mut(env)?;
        let cell = obj
            .vars
            .get_index_mut(slot as usize)
            .map(|(_, c)| c)
            .ok_or_else(|| {
                RtError::internal(format!("EnvId({}) 槽位 {} 越界", usize::from(env), slot))
            })?;
        cell.value = value;
        Ok(())
    }

    /// 写 cell 值：`let` cell 拒绝写入（`RtErrorKind::ImmutableAssign`）。
    pub fn set(&mut self, env: EnvId, slot: u32, value: Value) -> RtResult<()> {
        let obj = self.env_mut(env)?;
        let cell = obj
            .vars
            .get_index_mut(slot as usize)
            .map(|(_, c)| c)
            .ok_or_else(|| {
                RtError::internal(format!("EnvId({}) 槽位 {} 越界", usize::from(env), slot))
            })?;
        if !cell.mutable {
            return Err(RtError::new(
                RtErrorKind::ImmutableAssign,
                "由 let 绑定，不可重新赋值",
            ));
        }
        cell.value = value;
        Ok(())
    }

    /// cell 只读访问。
    pub fn cell(&self, env: EnvId, slot: u32) -> RtResult<&Cell> {
        let obj = self.env(env)?;
        obj.vars
            .get_index(slot as usize)
            .map(|(_, c)| c)
            .ok_or_else(|| {
                RtError::internal(format!("EnvId({}) 槽位 {} 越界", usize::from(env), slot))
            })
    }

    /// 环境的父链（调试/作用域检查用）。
    pub fn parent(&self, env: EnvId) -> RtResult<Option<EnvId>> {
        Ok(self.env(env)?.parent)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// int cell 读值（测试辅助）。
    fn read_int(envs: &EnvArena, env: EnvId, slot: u32) -> i64 {
        match envs.get(env, slot).unwrap() {
            Value::Int(v) => v,
            other => panic!("期望 int，得到 {:?}", other),
        }
    }

    // —— VALUE-CAPTURE-004 / conformance: closure-capture-by-reference ——

    #[test]
    fn capture_shares_owner_cell() {
        // var x = 1；闭包捕获定义处环境 → x = x*10 后定义处同见（conformance：10 10）
        let mut envs = EnvArena::new();
        let main = envs.root();
        let slot = envs.define(main, "x", Value::Int(1), true).unwrap();

        // 闭包捕获 = 记录定义处 EnvId；名字解析返回属主 cell
        let (owner, owner_slot) = envs.lookup(main, "x").unwrap().unwrap();
        assert_eq!(owner, main);
        assert_eq!(owner_slot, slot);

        // 闭包体写 x = x * 10 → 定义处读：10（共享可见）
        let v = read_int(&envs, main, slot);
        envs.set(main, slot, Value::Int(v * 10)).unwrap();
        assert_eq!(read_int(&envs, main, slot), 10);

        // 定义处写 x = 5 → 闭包体再写 x = x * 10 → 同 cell 得 50（conformance 第二行）
        envs.set(main, slot, Value::Int(5)).unwrap();
        let v = read_int(&envs, main, slot);
        envs.set(main, slot, Value::Int(v * 10)).unwrap();
        assert_eq!(read_int(&envs, main, slot), 50);
    }

    #[test]
    fn loop_capture_fresh_cell_per_iteration() {
        // for 循环每迭代新子环境：迭代内闭包捕获的 i 互不串扰
        // 手算：迭代0 定义 i=0，迭代1 定义 i=1；迭代0 闭包读 i 恒为 0
        let mut envs = EnvArena::new();
        let main = envs.root();
        let iter0 = envs.child(main);
        let iter1 = envs.child(main);
        let s0 = envs.define(iter0, "i", Value::Int(0), false).unwrap();
        let s1 = envs.define(iter1, "i", Value::Int(1), true).unwrap();

        assert_eq!(read_int(&envs, iter0, s0), 0);
        // 迭代 1 的 i 变化不影响迭代 0 的捕获
        envs.set(iter1, s1, Value::Int(99)).unwrap();
        assert_eq!(read_int(&envs, iter0, s0), 0);
        assert_eq!(read_int(&envs, iter1, s1), 99);
    }

    #[test]
    fn let_cell_is_immutable_var_cell_is_mutable() {
        let mut envs = EnvArena::new();
        let root = envs.root();
        let lv = envs.define(root, "a", Value::Int(1), false).unwrap();
        let vv = envs.define(root, "b", Value::Int(1), true).unwrap();
        assert_eq!(
            envs.set(root, lv, Value::Int(2)).unwrap_err().kind(),
            RtErrorKind::ImmutableAssign
        );
        envs.set(root, vv, Value::Int(2)).unwrap();
        assert_eq!(read_int(&envs, root, vv), 2);
    }

    #[test]
    fn lookup_walks_parent_chain_and_returns_owner() {
        let mut envs = EnvArena::new();
        let root = envs.root();
        let mid = envs.child(root);
        let leaf = envs.child(mid);
        let rs = envs.define(root, "g", Value::Int(1), true).unwrap();
        let ms = envs.define(mid, "m", Value::Int(2), true).unwrap();
        let _ = ms;
        // 叶子解析全局变量 → 属主是根
        assert_eq!(envs.lookup(leaf, "g").unwrap(), Some((root, rs)));
        // 就近遮蔽：叶子层重定义后解析到叶子（对抗重点：shadowed capture）
        let ls = envs.define(leaf, "g", Value::Int(3), true).unwrap();
        assert_eq!(envs.lookup(leaf, "g").unwrap(), Some((leaf, ls)));
        assert_eq!(envs.lookup(leaf, "不存在").unwrap(), None);
    }

    #[test]
    fn duplicate_define_rejected() {
        let mut envs = EnvArena::new();
        let root = envs.root();
        envs.define(root, "x", Value::Unit, true).unwrap();
        assert_eq!(
            envs.define(root, "x", Value::Unit, true)
                .unwrap_err()
                .kind(),
            RtErrorKind::Scope
        );
    }
}
