# A2-semantic 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

**首批交付（2026-09-25，第二波重跑）全部完成**：`impls/rust/crates/lang-types/` + `impls/rust/crates/lang-semantic/` 全量语义核。

- **lang-types**（`model.rs` + `ops.rs`）：
  - `TypeData` 全量类别（TYPE-KINDS-001）：Invalid/Int/Float/Bool/Str/Error/Unit/Range/Any/Unknown/Never + Slice/Map/Ref/Chan/Fn/Struct/Enum/Option/Result/TypeParam；`TypeTables` arena intern（结构去重）+ 名义 struct/enum def 表（只增不减）。
  - 预载面与 A4 冻结对齐：`error`=StructDefId(0)（字段 `msg: string`）、`Option`=EnumDefId(0)（Some=0/None=1）、`Result`=EnumDefId(1)（Ok=0/Err=1）。
  - `ops.rs`：结构同一（名义按 def id、type_param 擦除互同）、可赋值性（宽松位 any/unknown/invalid/type_param 双向放行；容器/代数协变、fn 参数逆变）、`type_str`（与证据逐字一致）、成员查找（struct 字段 + `error.msg` + `&T` 解引用）、调用兼容（Arity/ArgMismatch/NotCallable）、泛型替换 `substitute`、迭代协议 `iterable_elem`（map 迭代产出键）。
- **lang-semantic**（`symbols.rs`/`builtin.rs`/`resolver.rs`/`anno.rs`/`checker.rs`/`lib.rs`）：
  - Resolver：值/类型双作用域栈 + 构造器全局命名空间 + SymbolArena；四遍扫描（收集类型 → 顶层 def 表注册 → 收集 fn/extern/method → 顶层绑定依序 → 函数体最后走）与 interp 顺序语义一致（前向引用全局即 E2002）。NAME-* 十条全覆盖：E2001/E2002/E2003/E2004/E2005/E2006/E2007/E2008/E2010（E2009 空号保留）。跨文件合并 = 直接输入合并后的 stmts（NAME-MERGE-010）。
  - anno：注解 → TypeId 双通道（quiet：声明收集期不报错；emit：检查器路径发 Option arity E3001、Result arity E3001、第二参数非 error E3009，均 line 0 无锚，E3009 后仍返回 Result 形防 E3006 连锁——证据同款）。
  - Checker：E3001..E3014、E4001..E4003 全部发射（锚定与 case.yaml 逐项核对）；`?` 传播（E3005/E3008）、Result 函数 return 形态（E3006 + Ok/Err 位置约束）、match 四分类 + enum/代数型穷尽性（E4001 列缺失 case）、E4002/E4003 混用；泛型擦除面（type_param 双向放行、arity 照查）。产物：每表达式/绑定/符号类型 + MatchKind，经 `Analysis` 查询视图暴露（HIR/IR 消费）。
  - builtin：20 内建名面与 A4 冻结一致；`http`/`HttpResp` 不预载（A4 冻结面无此项，见 SPEC-GAP-002）。
- **测试**：`cargo test -p lang-types -p lang-semantic` **37 通过 / 0 失败**（lang-types 10 + semantic lib 6 + 对抗专项 20 + conformance 夹具 runner 1）；**conformance/diagnostics 30 个 check 类 case 30/30 通过**（期望直接读 case.yaml 的 code/severity/line，多重集合比对，ruleId 逐项核对）。`cargo clippy -p 两 crate --all-targets -- -D warnings` 干净；`cargo fmt --check` 通过；`cargo check --workspace` + `cargo test --workspace` 341 测试全绿（兄弟 crate 零破坏）。`#![forbid(unsafe_code)]` 保持；生产代码零 unwrap/expect/panic（仅 resolver 一处 `unreachable!` 内部不变量：make_fn_symbol 只接受 FnDecl，两调用点均模式保证）。

## 进行中

（无）

## 下一步

- lang-hir/lowering 消费 `Analysis`（type_of/match_kind/symbol 查询 + `resolve_annotation`）。
- 用户泛型 struct/enum 如需硬拒绝，等协调人裁决诊断码（见 SPEC-GAP-RS-013）。

## 阻塞

（无）

## 已冻结接口登记

> 登记后改签名 = 破坏契约，走协调人集成轮。

- 冻结：`lang_types::TypeData`：enum{Invalid,Int,Float,Bool,Str,Error,Unit,Range,Any,Unknown,Never,Slice(TypeId),Map{key,val},Ref(TypeId),Chan(TypeId),Fn{params,ret},Struct(StructDefId),Enum(EnumDefId),Option(TypeId),Result{ok,err},TypeParam{name}}；TypeId(0..=10) 为预载单例（Invalid..Never 固定序）。
- 冻结：`lang_types::TypeTables`：new/default（含预载 def）/intern/data/register_struct/register_enum/struct_def/enum_def/struct_def_mut/enum_def_mut；`StructDefId(pub u32)`/`EnumDefId(pub u32)`；预载常量 `ERROR_DEF=StructDefId(0)`、`OPTION_DEF=EnumDefId(0)`、`RESULT_DEF=EnumDefId(1)`、`ERROR_MSG_FIELD="msg"`。
- 冻结：`lang_types::ops`：equal/assignable/type_str/is_numeric/is_concrete/is_lax/deref/result_ok/result_err/option_elem/lookup_field(FieldLookup{Found,Missing,NotStruct,Lax})/check_call(CallCompat{Compatible,Arity,ArgMismatch,NotCallable})/substitute/iterable_elem。
- 冻结：`lang_semantic::analyze_stmts(stmts: &[Stmt], source: SourceId) -> Analysis`（输入为装载器合并后的语句序列）与 `analyze_source(text: &SourceText, source: SourceId) -> Analysis`（parse 失败 → 仅 E1001 诊断）；resolver 有诊断时 checker 不运行（`Analysis.checked=false`），与证据管线一致。
- 冻结：`lang_semantic::Analysis`：pub 字段 `diags: Vec<Diagnostic>`/`types: TypeTables`/`symbols: SymbolArena`/`source: SourceId`/`checked: bool`；查询视图 `has_errors/type_of(&Expr)/pat_type(&Pat)/symbol_type(SymbolId)/fn_type(SymbolId)/match_kind(&Expr)/symbol_of_ref(&Expr)/symbol_of_pat(&Pat)/symbol_of_decl(&Stmt)`（指针键查询须传入同一次分析的 AST 引用，跨树返回 None）。
- 冻结：`lang_semantic::MatchKind`：enum{Literal,Enum,Result,Option}（MATCH-KIND-001 结论，HIR/IR/后端直接消费）。
- 冻结：`lang_semantic::resolve_annotation(&mut Analysis, &TypeEx, tparams: &[String]) -> TypeId`（HIR 构建复用的静默注解解析）。
- 冻结：`lang_semantic::symbols`：`SymbolKind{Fn,Method,Var,Param,Struct,Enum,Ctor}`（kind_name 中文契约串）、`Symbol`（owned 注解快照：type_ex/param_annos/param_names/recv_name/arity/type_params/owner_name/case_index/payload_annos/fields/field_annos/methods/nominal/is_extern/is_builtin）、`SymbolArena`（get/get_mut/len/iter/first_type_named）、`NominalRef{Struct,Enum}`。
- 冻结：`lang_semantic::builtin`：`BUILTIN_FNS` 20 名单（与 A4 冻结一致）、`PRELUDE_STRUCTS=["error"]`、`PRELUDE_CTORS`（Some=Option/0/1、None=Option/1/0、Ok=Result/0/1、Err=Result/1/1）、`builtin_return(&mut TypeTables, name, args) -> TypeId`。

## SPEC-GAP / 契约疑义（已登记 docs/lang-rs/spec-gaps.md RS-011..013，等协调人裁决，均按现状实现并在测试锁定）

1. **SPEC-GAP-RS-011：赋值顶层 `?`**——RESULT-TRY-003 明文允许 `?` 出现在「let/return/赋值的顶层」，但 TS/Go 检查器在赋值路径走 `infer`（误发 E3005）；而 Go interp 的 `?` 是通用表达式求值（`case *syntax.TryEx` 在 eval 内），运行时赋值顶层 `?` 可用。本实现按 spec 放行（赋值值顶层的 Try 走 inferTry），`try_at_assign_top_level_is_allowed` 测试锁定。若裁决从证据，改 `Checker::assign` 一处。
2. **SPEC-GAP-RS-012：块作用域类型写入**——证据检查器把块内 let/循环变量/臂绑定类型写进「全局层」（Go 历史行为，TS 注释明说如实复刻），会产生跨块类型泄漏（如 if 块内 `let q: string` 泄漏到块外同名绑定）。本实现按 NAME-SCOPE-001/MATCH-BIND-003 写入当前块作用域（词法作用域语义），诊断面在 30/30 夹具上与证据一致。若裁决从证据，改 `Checker::{let_stmt, stmt(ForIn), infer_match}` 三处写入点。
3. **SPEC-GAP-RS-013：用户泛型 struct/enum 无诊断码约定**——GEN-DECL-001 说「用户泛型 struct/enum v0.9 冻结不支持（预载 Option/Result 除外）」，但 27 码表没有对应码，证据实现静默容忍（类型参数按擦除占位参与检查）。本实现同口径：声明不报错，`T` 解析为 TypeParam 擦除占位。如需硬拒绝请裁决补码。
4. **http 服务面缺位**：TS 证据预载 `http` 值 + `HttpResp` struct（checker/resolver 特判 `http.get`），但 A4 冻结的 runtime 内建清单无此项。本实现对齐 A4（不预载）；若后续冻结宿主服务面，只需在 `builtin.rs` 名单 + `infer_method_call` 加回特判。
5. **E3007/E3009 发射位与证据完全一致**（E3007 在 fn 声明行、E3009 line 0），含证据的既有怪癖：`fnSignature` 与 `declareParams`、以及每个调用点的 `checkArgsVsDecl` 会重复解析同一参数注解——坏 Option/Result arity 注解可能多次发码。为诊断序逐字对齐保留该行为。
