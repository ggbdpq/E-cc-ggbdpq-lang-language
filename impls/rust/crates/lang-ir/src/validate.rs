//! IR Validator：线性扫描结构校验。
//!
//! 这里的失败全部是 **内部错误**（lowering/手写 IR 的不变量破坏），
//! 不是用户诊断——用户源码问题在 semantic 期已被拒绝。
//!
//! 校验面（栈式 + 基本块模型的结构不变量）：
//! 1. 引用合法：`LocalId`/`GlobalId`/`FunctionId`/`BlockId`/`StructId`/`EnumId`
//!    全部落在对应表内；`EnumNew.case_id` 落在 case 数内。
//! 2. 签名一致：`Call`/`Spawn` 的实参个数 = 目标 `param_count`；
//!    `ClosureNew.captures` = 目标 `capture_count`。
//! 3. 块终接：每个块恰一个显式终接（结构上由 `Block.term` 保证）；
//!    非 extern 函数至少一个块；extern 函数不得有块。
//! 4. 栈效应：块入口栈深 0；逐指令弹压平衡（下溢即错）；终接处栈深
//!    必须恰为终接消费量；终接后归零。SwitchEnum arms 长度 = case 数。

use lang_core::ids::FunctionId;

use crate::ids::BlockId;
use crate::model::{Inst, IrFunction, IrProgram, Terminator};

/// 校验失败（内部错误形态；定位到函数/块/指令序号）。
#[derive(Clone, PartialEq, Eq, Debug)]
pub struct IrError {
    pub function: FunctionId,
    pub block: Option<BlockId>,
    /// 块内指令下标；终接指令以 `None` 表示（定位 `block` 即可）。
    pub inst: Option<usize>,
    pub message: String,
}

impl std::fmt::Display for IrError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "IR 内部错误 fn#{}", self.function)?;
        if let Some(b) = self.block {
            write!(f, " block#{}", b)?;
        }
        if let Some(i) = self.inst {
            write!(f, " inst#{}", i)?;
        }
        write!(f, ": {}", self.message)
    }
}

impl std::error::Error for IrError {}

/// 校验整个程序。合法返回 `Ok(())`。
pub fn validate(prog: &IrProgram) -> Result<(), IrError> {
    for (fi, func) in prog.functions.iter().enumerate() {
        validate_function(prog, FunctionId(fi as u32), func)?;
    }
    Ok(())
}

fn err(
    function: FunctionId,
    block: Option<BlockId>,
    inst: Option<usize>,
    message: String,
) -> IrError {
    IrError {
        function,
        block,
        inst,
        message,
    }
}

fn validate_function(prog: &IrProgram, fid: FunctionId, func: &IrFunction) -> Result<(), IrError> {
    // extern 函数：只有声明，无块。
    if func.extern_host.is_some() {
        if !func.blocks.is_empty() {
            return Err(err(fid, None, None, "extern 函数不得有函数体".into()));
        }
        return Ok(());
    }
    if func.blocks.is_empty() {
        return Err(err(fid, None, None, "函数没有基本块（缺入口块）".into()));
    }
    // 捕获槽 + 参数必须落在局部表内。
    if func.locals.len() < func.fixed_slots() {
        return Err(err(
            fid,
            None,
            None,
            format!(
                "局部表 {} 个槽位 < 捕获 {} + 参数 {}",
                func.locals.len(),
                func.capture_count,
                func.sig.param_count
            ),
        ));
    }

    for (bi, block) in func.blocks.iter().enumerate() {
        validate_block(prog, func, fid, BlockId(bi as u32), block)?;
    }
    Ok(())
}

fn validate_block(
    prog: &IrProgram,
    func: &IrFunction,
    fid: FunctionId,
    bid: BlockId,
    block: &crate::model::Block,
) -> Result<(), IrError> {
    let n_blocks = func.blocks.len();
    let mut depth: usize = 0;

    for (ii, inst) in block.insts.iter().enumerate() {
        let loc = (fid, Some(bid), Some(ii));
        match inst {
            Inst::ConstI64(_)
            | Inst::ConstF64(_)
            | Inst::ConstBool(_)
            | Inst::ConstStr(_)
            | Inst::ChanNew => {
                depth += 1;
            }
            Inst::LocalGet(local) => {
                check_local(func, loc, *local)?;
                depth += 1;
            }
            Inst::LocalSet(local) => {
                check_local(func, loc, *local)?;
                pop(&mut depth, 1, loc, "LocalSet")?;
            }
            Inst::GlobalGet(global) => {
                let n = prog.globals.len();
                if usize::from(*global) >= n {
                    return Err(bad_ref(
                        loc,
                        format!("GlobalId {} 越界（共 {n} 个全局）", global),
                    ));
                }
                depth += 1;
            }
            Inst::GlobalSet(global) => {
                let n = prog.globals.len();
                if usize::from(*global) >= n {
                    return Err(bad_ref(
                        loc,
                        format!("GlobalId {} 越界（共 {n} 个全局）", global),
                    ));
                }
                pop(&mut depth, 1, loc, "GlobalSet")?;
            }
            Inst::Unary(_) => pop_push(&mut depth, 1, 1, loc, "Unary")?,
            Inst::Binary(_) => pop_push(&mut depth, 2, 1, loc, "Binary")?,
            Inst::Pop => pop(&mut depth, 1, loc, "Pop")?,

            Inst::Call(target) => {
                let sig = check_function(prog, loc, *target)?;
                pop(&mut depth, usize::from(sig.param_count), loc, "Call")?;
                if sig.has_ret {
                    depth += 1;
                }
            }
            Inst::CallIndirect { argc, has_ret } => {
                // 先压闭包值再压实参。
                pop(&mut depth, usize::from(*argc) + 1, loc, "CallIndirect")?;
                if *has_ret {
                    depth += 1;
                }
            }
            Inst::CallBuiltin { argc, has_ret, .. } => {
                pop(&mut depth, usize::from(*argc), loc, "CallBuiltin")?;
                if *has_ret {
                    depth += 1;
                }
            }
            Inst::Spawn { func: target, argc } => {
                let sig = check_function(prog, loc, *target)?;
                if *argc != sig.param_count {
                    return Err(bad_ref(
                        loc,
                        format!(
                            "Spawn 实参 {argc} 个 != 目标 fn#{} 参数 {} 个",
                            target, sig.param_count
                        ),
                    ));
                }
                pop(&mut depth, usize::from(*argc), loc, "Spawn")?;
            }
            Inst::ClosureNew {
                func: target,
                captures,
            } => {
                let callee = lookup_function(prog, loc, *target)?;
                if *captures != callee.capture_count {
                    return Err(bad_ref(
                        loc,
                        format!(
                            "ClosureNew 捕获 {captures} 个 != 目标 fn#{} capture_count {}",
                            target, callee.capture_count
                        ),
                    ));
                }
                pop(&mut depth, usize::from(*captures), loc, "ClosureNew")?;
                depth += 1;
            }

            Inst::IndexGet => pop_push(&mut depth, 2, 1, loc, "IndexGet")?,
            Inst::IndexSet => pop(&mut depth, 3, loc, "IndexSet")?,
            Inst::SliceGet => pop_push(&mut depth, 3, 1, loc, "SliceGet")?,
            Inst::FieldGet { .. } => pop_push(&mut depth, 1, 1, loc, "FieldGet")?,
            Inst::FieldSet { .. } => pop(&mut depth, 2, loc, "FieldSet")?,
            Inst::ArrayPush => pop(&mut depth, 2, loc, "ArrayPush")?,

            Inst::ArrayNew { count } => {
                pop(&mut depth, *count as usize, loc, "ArrayNew")?;
                depth += 1;
            }
            Inst::MapNew { count } => {
                pop(&mut depth, 2 * *count as usize, loc, "MapNew")?;
                depth += 1;
            }
            Inst::StructNew { struct_id } => {
                let n = prog.structs.len();
                let si = usize::from(*struct_id);
                if si >= n {
                    return Err(bad_ref(
                        loc,
                        format!("StructId {struct_id} 越界（共 {n} 个 struct）"),
                    ));
                }
                let field_count = prog.structs[si].fields.len();
                pop(&mut depth, field_count, loc, "StructNew")?;
                depth += 1;
            }
            Inst::EnumNew {
                enum_id,
                case_id: case,
            } => {
                let n = prog.enums.len();
                let ei = usize::from(*enum_id);
                if ei >= n {
                    return Err(bad_ref(
                        loc,
                        format!("EnumId {enum_id} 越界（共 {n} 个 enum）"),
                    ));
                }
                let cases = &prog.enums[ei].cases;
                let ci = usize::from(*case);
                if ci >= cases.len() {
                    return Err(bad_ref(
                        loc,
                        format!(
                            "case {case} 越界（enum {} 共 {} 个 case）",
                            enum_id,
                            cases.len()
                        ),
                    ));
                }
                pop(&mut depth, cases[ci].payload_types.len(), loc, "EnumNew")?;
                depth += 1;
            }
            Inst::RangeNew => pop_push(&mut depth, 2, 1, loc, "RangeNew")?,

            Inst::BoxNew => pop_push(&mut depth, 1, 1, loc, "BoxNew")?,
            Inst::Deref => pop_push(&mut depth, 1, 1, loc, "Deref")?,
            Inst::RefSet => pop(&mut depth, 2, loc, "RefSet")?,
            Inst::EnumPayloadGet { .. } => pop_push(&mut depth, 1, 1, loc, "EnumPayloadGet")?,

            Inst::IterNew => pop_push(&mut depth, 1, 1, loc, "IterNew")?,
            Inst::IterNext { done } => {
                check_block(func, loc, *done, n_blocks)?;
                pop_push(&mut depth, 1, 1, loc, "IterNext")?;
            }

            Inst::ChanSend => pop(&mut depth, 2, loc, "ChanSend")?,
            Inst::ChanRecv => pop_push(&mut depth, 1, 1, loc, "ChanRecv")?,

            Inst::Try {
                err: err_local,
                fail,
            } => {
                check_local(func, loc, *err_local)?;
                check_block(func, loc, *fail, n_blocks)?;
                pop_push(&mut depth, 1, 1, loc, "Try")?;
            }
        }
    }

    // 终接：消费量必须恰为当前栈深，消费后归零。
    let loc = (fid, Some(bid), None);
    let (consumes, targets): (usize, Vec<BlockId>) = match &block.term {
        Terminator::Goto { target } => (0, vec![*target]),
        Terminator::Branch { if_true, if_false } => (1, vec![*if_true, *if_false]),
        Terminator::SwitchInt { arms, default } => {
            let mut ts = arms.iter().map(|(_, b)| *b).collect::<Vec<_>>();
            ts.extend(default);
            (1, ts)
        }
        Terminator::SwitchEnum {
            enum_id,
            arms,
            default,
        } => {
            let n = prog.enums.len();
            let ei = usize::from(*enum_id);
            if ei >= n {
                return Err(bad_ref(
                    loc,
                    format!("EnumId {enum_id} 越界（共 {n} 个 enum）"),
                ));
            }
            let case_count = prog.enums[ei].cases.len();
            if arms.len() != case_count {
                return Err(bad_ref(
                    loc,
                    format!(
                        "SwitchEnum arms {} 个 != enum {} case {} 个",
                        arms.len(),
                        enum_id,
                        case_count
                    ),
                ));
            }
            let mut ts = arms.clone();
            ts.extend(*default);
            (1, ts)
        }
        Terminator::Return { has_value } => (usize::from(*has_value), vec![]),
        Terminator::Unreachable => (0, vec![]),
    };
    if depth != consumes {
        return Err(err(
            fid,
            Some(bid),
            None,
            format!("终接处栈深 {depth} != 终接消费 {consumes}（块必须以平衡栈收尾）"),
        ));
    }
    for t in targets {
        check_block(func, loc, t, n_blocks)?;
    }
    Ok(())
}

type Loc = (FunctionId, Option<BlockId>, Option<usize>);

fn lookup_function(prog: &IrProgram, loc: Loc, fid: FunctionId) -> Result<&IrFunction, IrError> {
    prog.functions.get(usize::from(fid)).ok_or_else(|| {
        bad_ref(
            loc,
            format!(
                "FunctionId {fid} 越界（共 {} 个函数）",
                prog.functions.len()
            ),
        )
    })
}

fn check_function(
    prog: &IrProgram,
    loc: Loc,
    fid: FunctionId,
) -> Result<crate::model::FnSig, IrError> {
    lookup_function(prog, loc, fid).map(|f| f.sig)
}

fn check_local(func: &IrFunction, loc: Loc, local: lang_core::ids::LocalId) -> Result<(), IrError> {
    let n = func.locals.len();
    if usize::from(local) >= n {
        return Err(bad_ref(
            loc,
            format!("LocalId {} 越界（共 {n} 个局部）", local),
        ));
    }
    Ok(())
}

fn check_block(func: &IrFunction, loc: Loc, bid: BlockId, n_blocks: usize) -> Result<(), IrError> {
    let _ = func;
    if usize::from(bid) >= n_blocks {
        return Err(bad_ref(
            loc,
            format!("BlockId {bid} 越界（共 {n_blocks} 个块）"),
        ));
    }
    Ok(())
}

fn pop(depth: &mut usize, n: usize, loc: Loc, what: &str) -> Result<(), IrError> {
    if *depth < n {
        return Err(err(
            loc.0,
            loc.1,
            loc.2,
            format!("{what} 栈下溢：需要 {n} 个操作数，只有 {depth}"),
        ));
    }
    *depth -= n;
    Ok(())
}

fn pop_push(
    depth: &mut usize,
    pop_n: usize,
    push_n: usize,
    loc: Loc,
    what: &str,
) -> Result<(), IrError> {
    pop(depth, pop_n, loc, what)?;
    *depth += push_n;
    Ok(())
}

fn bad_ref(loc: Loc, message: String) -> IrError {
    err(loc.0, loc.1, loc.2, message)
}
