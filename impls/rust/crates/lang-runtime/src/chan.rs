//! channel 表（CHAN-CREATE-001 ~ CHAN-CLOSE-004，冻结面）。
//!
//! - 缓冲 1024（语言常量）；`send` 不阻塞，满 → RUNTIME-CHAN-001；
//! - `recv` FIFO；同步确定子集下空且无并发 send 方 = 程序死锁面，
//!   语言层表现为 `Err("channel 空")` 值（CHAN-RECV-003 / RUNTIME-CHAN-002）；
//! - 无 close/选择/超时；channel 生命周期 = 程序生命周期。
//!
//! 载荷按值语义传递（VALUE-TASKSHARE-007）：入队即 clone 快照
//! （struct 拷贝、容器共享索引——与其他传值路径一致）。

use std::collections::VecDeque;

use crate::error::{RtError, RtErrorKind, RtResult};
use crate::value::Value;

/// channel 缓冲容量（CHAN-CREATE-001 语言常量，v0.9 冻结）。
pub const CHAN_CAP: usize = 1024;

/// recv 空渠道时 `Err` 载荷的规范文本（CHAN-RECV-003 逐字冻结——
/// 它是程序可打印的值，不是内部消息）。
pub const CHAN_EMPTY_MSG: &str = "channel 空";

/// channel 身份（ChannelTable 下标 newtype）。
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct ChannelId(pub u32);

impl From<ChannelId> for usize {
    fn from(v: ChannelId) -> usize {
        v.0 as usize
    }
}

impl std::fmt::Display for ChannelId {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}

/// recv 结果：有值 / 空（空 = 同步子集死锁面，由上层构造 `Err(CHAN_EMPTY_MSG)` 值）。
#[derive(Clone, Debug, PartialEq)]
pub enum RecvOutcome {
    /// FIFO 出队的值。
    Value(Value),
    /// 空（无人 send）。
    Empty,
}

/// 单条 channel：FIFO 缓冲。
#[derive(Debug, Default)]
pub struct Channel {
    buf: VecDeque<Value>,
}

/// channel 表。
#[derive(Debug, Default)]
pub struct ChannelTable {
    chans: Vec<Channel>,
}

impl ChannelTable {
    pub fn new() -> ChannelTable {
        ChannelTable { chans: Vec::new() }
    }

    /// `makeChan()`：缓冲 1024 的 channel。
    pub fn make(&mut self) -> ChannelId {
        let id = ChannelId(self.chans.len() as u32);
        self.chans.push(Channel {
            buf: VecDeque::with_capacity(CHAN_CAP),
        });
        id
    }

    fn chan(&self, id: ChannelId) -> RtResult<&Channel> {
        self.chans
            .get(usize::from(id))
            .ok_or_else(|| RtError::internal(format!("ChannelId({}) 越界", usize::from(id))))
    }

    fn chan_mut(&mut self, id: ChannelId) -> RtResult<&mut Channel> {
        let n = self.chans.len();
        self.chans.get_mut(usize::from(id)).ok_or_else(|| {
            RtError::internal(format!(
                "ChannelId({}) 越界（共 {} 条）",
                usize::from(id),
                n
            ))
        })
    }

    /// `send(ch, v)`：不阻塞；满 → `RtErrorKind::ChanFull`（RUNTIME-CHAN-001）。
    /// 载荷入队即快照（clone）。
    pub fn send(&mut self, id: ChannelId, v: Value) -> RtResult<()> {
        let chan = self.chan_mut(id)?;
        if chan.buf.len() >= CHAN_CAP {
            return Err(RtError::new(
                RtErrorKind::ChanFull,
                format!("向已满的 channel 发送（容量 {}）", CHAN_CAP),
            ));
        }
        chan.buf.push_back(v);
        Ok(())
    }

    /// `recv(ch)` 的存储面：FIFO 出队；空 → `RecvOutcome::Empty`。
    pub fn try_recv(&mut self, id: ChannelId) -> RtResult<RecvOutcome> {
        match self.chan_mut(id)?.buf.pop_front() {
            Some(v) => Ok(RecvOutcome::Value(v)),
            None => Ok(RecvOutcome::Empty),
        }
    }

    /// 当前缓冲长度（测试/诊断用）。
    pub fn len(&self, id: ChannelId) -> RtResult<usize> {
        Ok(self.chan(id)?.buf.len())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    // —— CHAN-RECV-003 FIFO / 对抗重点：single producer FIFO ——

    #[test]
    fn single_producer_fifo() {
        // 手算：send 1、2、3 → recv 依次得 1、2、3（先进先出）
        let mut chans = ChannelTable::new();
        let ch = chans.make();
        chans.send(ch, Value::Int(1)).unwrap();
        chans.send(ch, Value::Int(2)).unwrap();
        chans.send(ch, Value::Int(3)).unwrap();
        assert_eq!(chans.len(ch).unwrap(), 3);
        assert_eq!(
            chans.try_recv(ch).unwrap(),
            RecvOutcome::Value(Value::Int(1))
        );
        assert_eq!(
            chans.try_recv(ch).unwrap(),
            RecvOutcome::Value(Value::Int(2))
        );
        assert_eq!(
            chans.try_recv(ch).unwrap(),
            RecvOutcome::Value(Value::Int(3))
        );
        assert_eq!(chans.try_recv(ch).unwrap(), RecvOutcome::Empty);
    }

    #[test]
    fn send_payload_semantics_follow_value_rules() {
        // VALUE-TASKSHARE-007：channel 载荷按值语义复制/共享——
        // struct 入队为独立拷贝；容器入队共享底层（clone 索引）
        let mut chans = ChannelTable::new();
        let mut heap = crate::heap::HeapArena::new();
        let arr = heap.alloc_slice(vec![Value::Int(1)]);
        let ch = chans.make();

        // 容器载荷：send 后对堆的 push 对队列中的载荷可见（共享）
        chans.send(ch, arr.clone()).unwrap();
        heap.slice_push(crate::heap::ValueId(0), Value::Int(2))
            .unwrap();
        match chans.try_recv(ch).unwrap() {
            RecvOutcome::Value(Value::Slice(sid)) => {
                assert_eq!(heap.slice_len(sid).unwrap(), 2);
            }
            other => panic!("期望 slice 载荷，得到 {:?}", other),
        }

        // struct 载荷：入队后改源不影响队列中的副本（拷贝）
        let p = crate::value::StructVal {
            def: crate::defs::StructDefId(0),
            fields: vec![Value::Int(1)],
        };
        chans.send(ch, Value::Struct(p.clone())).unwrap();
        let mut p2 = p.clone();
        p2.fields[0] = Value::Int(99);
        match chans.try_recv(ch).unwrap() {
            RecvOutcome::Value(v) => assert_eq!(v, Value::Struct(p)),
            other => panic!("期望 struct 载荷，得到 {:?}", other),
        }
    }

    // —— RUNTIME-CHAN-001 缓冲 1024 ——

    #[test]
    fn send_full_at_capacity() {
        let mut chans = ChannelTable::new();
        let ch = chans.make();
        for i in 0..CHAN_CAP as i64 {
            chans.send(ch, Value::Int(i)).unwrap();
        }
        assert_eq!(chans.len(ch).unwrap(), CHAN_CAP);
        let err = chans.send(ch, Value::Int(-1)).unwrap_err();
        assert_eq!(err.kind(), RtErrorKind::ChanFull);
        // 满后原缓冲不动：recv 仍从 0 起 FIFO
        assert_eq!(
            chans.try_recv(ch).unwrap(),
            RecvOutcome::Value(Value::Int(0))
        );
    }

    // —— CHAN-RECV-003 空渠道 ——

    #[test]
    fn recv_empty_reports_empty_outcome() {
        let mut chans = ChannelTable::new();
        let ch = chans.make();
        assert_eq!(chans.try_recv(ch).unwrap(), RecvOutcome::Empty);
        // 规范文本逐字冻结（程序可 println 的 Err 载荷）
        assert_eq!(CHAN_EMPTY_MSG, "channel 空");
    }
}
