# A2-semantic 工作状态

> 由该 Agent 自行维护；协调人每轮汇总。格式：完成块 / 进行中 / 下一步 / 阻塞 / 已冻结接口 / SPEC-GAP。

## 完成块

**首批交付（2026-09-26，第二波重跑）全部完成**：`src/Lang.Types/` + `src/Lang.Semantic/` 全量语义核，与 lang-rs A2 冻结面 1:1 对齐。

- **Lang.Types**（`TypeData.cs` + `TypeTables.cs` + `Ops.cs`）：
  - `TypeData` 全量 21 变体（TYPE-KINDS-001）：Invalid/Int/Float/Bool/Str/Error/Unit/Range/Any/Unknown/Never + Slice/Map/Ref/Chan/Fn/Struct/Enum/Option/Result/TypeParam；abstract record + 嵌套 sealed record（A1 冻结形态），`Fn` 参数表定制逐元素结构相等供 intern 去重。
  - `TypeTables` arena intern（单例直命中 + 结构去重）+ 名义 struct/enum def 表（只增不减，def 为可变类支持注册后回填字段/载荷）。
  - 预载面与 A4 冻结对齐：`error`=StructDefId(0)（字段 `msg: string`）、`Option`=EnumDefId(0)（Some=0/None=1）、`Result`=EnumDefId(1)（Ok=0/Err=1）；单例 TypeId 0..=10 固定序。
  - `Ops`：结构同一（名义按 def id、type_param 擦除互同）、可赋值性（宽松位 any/unknown/invalid/type_param 双向放行；容器/代数协变、fn 参数逆变）、`TypeStr`（与证据逐字一致，unit 返回省略 `: ()`）、成员查找（struct 字段 + `error.msg` + `&T` 解引用）、调用兼容（Compatible/Arity/ArgMismatch/NotCallable）、泛型替换 `Substitute`、迭代协议 `IterableElem`（map 迭代产出键）。
- **Lang.Semantic**（`Symbols.cs`/`Builtin.cs`/`Anno.cs`/`Resolver.cs`/`Checker.cs`/`LangSemantic.cs`）：
  - Resolver：值/类型双作用域栈 + 构造器全局命名空间 + SymbolArena；多遍扫描（收集类型 → 顶层 def 表注册 → 收集 fn/extern/method → 顶层绑定依序 → 函数体最后走）与 interp 顺序语义一致（顶层前向引用全局即 E2002）。NAME-* 十条全覆盖：E2001/E2002/E2003/E2004/E2005/E2006/E2007/E2008/E2010（E2009 空号保留）。跨文件合并 = 直接输入合并后的 stmts（NAME-MERGE-010）。AST 节点键用引用身份比较器（`RefComparer`，等价 lang-rs 指针键，禁用 record 结构相等）。
  - Anno：注解 → TypeId 双通道（quiet：声明收集期不报错；emit：检查器路径发 Option arity E3001、Result arity E3001、第二参数非 error E3009，均 line 0 无锚，E3009 后仍返回 Result 形防 E3006 连锁——证据同款）。
  - Checker：E3001..E3014、E4001..E4003 全部发射（锚定与 case.yaml 逐项核对）；`?` 传播（E3005/E3008 + **赋值顶层 `?` 走 InferTry**，按 ADR-0005 放行）；E3006（Result 函数 return 形态 + Ok/Err 位置约束）；match 四分类 + enum/代数型穷尽性（E4001 列缺失 case）、E4002/E4003 混用；泛型擦除面（type_param 双向放行、arity 照查，用户泛型 struct/enum 静默容忍）。**块内 let/循环变量/臂绑定类型写当前块作用域**（lang-rs RS-012 结论，不写全局层）。
  - builtin：20 内建名面与 A4 冻结一致；`http`/`HttpResp` 不预载（A4 冻结面无此项，见 SPEC-GAP）。
- **测试**（`tests/Lang.Types.Tests/` + `tests/Lang.Semantic.Tests/`，xunit）：**92 通过 / 0 失败**（Types 11 + Semantic 81）。
  - Semantic 81 = 对抗专项 27（lang-rs adversarial 17 条逐条移植 + checker 单元 6 + astral 对抗 2 + Analysis 查询视图/ResolveAnnotation 2）+ NAME-* 十码专项 22（含遮蔽合法/var 重绑/字段可写/checked=false 管线切断/E1001-only 入口面）+ **conformance/diagnostics 30 个 check 类 case 夹具（逐 case 读 case.yaml 的 code/severity/line 多重集合比对 + ruleId 逐项核对，30/30 通过；case 数=30 锁定）**。
  - astral 对抗轴：每个测试类至少 1 例（🦀𝕏𠀀 的类型参数名 intern/type_str、诊断行锚穿越 astral 内容不偏移、astral 字符串后行号正确性）。
- **质量门**：`dotnet test`（本域 92/92 绿，Lang.Core.Tests 12/12 亦绿）；`dotnet build LangCs.slnx -c Release -warnaserror` **全解决方案 0 警告 0 错误**（开工时 Lang.Contract 有一个预存编译错，收工时 A6 侧已修复，复验通过）；4 个本域项目 `dotnet format --verify-no-changes` 干净。两个测试项目已按惯例许可 `dotnet sln LangCs.slnx add` 登记。零 unsafe/async/throw 表达用户错误；Dictionary 仅用于查名（不外泄迭代序，公开遍历一律 List/排序）。

## 进行中

（无）

## 下一步

- A3 HIR 消费 `Analysis`（TypeOf/MatchKindOf/SymbolOfRef/SymbolOfPat/SymbolOfDecl/FnType + `LangSemantic.ResolveAnnotation`）。
- 用户泛型 struct/enum 如需硬拒绝，等协调人裁决诊断码（见 SPEC-GAP-CS-001）。

## 阻塞

（无）

## 观察提示（非本域，不阻塞）

- `Lang.Core.Tests` 当前 2 红（`EndOfTextReturnsLengthCp`/`OutOfRangeFloors`，coretests 期望与 `SourceText.CharIndexToCp` 的文末边界新行为不一致）——属协调人领地（Lang.Core 及其测试），A1 status 的 SPEC-GAP-1 已登记同一区域；本域 92 测试不依赖该边界，未受影响。

## 已冻结接口登记

> 均在 `Lang.Types` / `Lang.Semantic` 命名空间；消费方按此对接，改签名走协调人集成轮。
> 与 lang-rs A2 冻结登记（docs/lang-rs/status/A2-semantic.md）逐条对齐；Rust snake_case → C# PascalCase。

- 冻结：`Lang.Types.TypeData`：abstract record + 嵌套 sealed record 21 变体{Invalid, Int, Float, Bool, Str, Error, Unit, Range, Any, Unknown, Never, Slice(TypeId Elem), Map(TypeId Key, TypeId Val), Ref(TypeId Elem), Chan(TypeId Elem), Fn(IReadOnlyList&lt;TypeId&gt; Params, TypeId Ret), Struct(StructDefId), Enum(EnumDefId), Option(TypeId Elem), Result(TypeId Ok, TypeId Err), TypeParam(string Name)}；`TypeId(0..=10)` 为预载单例（Invalid..Never 固定序，`TypeTables` 静态字段）。
- 冻结：`Lang.Types.TypeTables`：`new()`（含预载 def）/`Intern(TypeData)`/`Data(TypeId)`/`RegisterStruct`/`RegisterEnum`/`GetStruct`/`GetEnum`（def 类字段可直接回填，对应 Rust struct_def_mut/enum_def_mut）；`readonly record struct StructDefId(uint)`/`EnumDefId(uint)`；预载常量 `TypeTables.ErrorDef=StructDefId(0)`、`OptionDef=EnumDefId(0)`、`ResultDef=EnumDefId(1)`、`ErrorMsgField="msg"`。
- 冻结：`Lang.Types.Ops`：`Equal/Assignable/TypeStr/IsNumeric/IsConcrete/IsLax/Deref/ResultOk/ResultErr/OptionElem/LookupField(→FieldLookup{Found,Missing,NotStruct,Lax})/CheckCall(→CallCompat{Compatible,Arity,ArgMismatch,NotCallable})/Substitute/IterableElem`（FieldLookup/CallCompat 为 abstract record 层次，record 相等可直接断言）。
- 冻结：`Lang.Semantic.LangSemantic`：`static Analysis Analyze(IReadOnlyList<Stmt> stmts, SourceId source)`（输入为装载器合并后的语句序列）与 `static Analysis AnalyzeSource(SourceText, SourceId)`（parse 失败 → 仅 E1001 诊断，`Checked=false`）；resolver 有诊断时 checker 不运行（`Analysis.Checked=false`），与证据管线一致。`static TypeId ResolveAnnotation(Analysis, TypeEx, IReadOnlyList<string> tparams)`（HIR 构建复用的静默注解解析）。
- 冻结：`Lang.Semantic.Analysis`：`Diags/Types/Symbols/Source/Checked` + `HasErrors()` + 查询视图 `TypeOf(Expr)/PatType(Pat)/SymbolType(SymbolId)/FnType(SymbolId)/MatchKindOf(Expr)/SymbolOfRef(Expr)/SymbolOfPat(Pat)/SymbolOfDecl(Stmt)`（引用键查询须传入同一次分析的 AST 节点，跨树返回 null）。
- 冻结：`Lang.Semantic.MatchKind`：enum{Literal, Enum, Result, Option}（MATCH-KIND-001 结论，HIR/IR/后端直接消费）。
- 冻结：`Lang.Semantic.symbols`：`SymbolKind{Fn, Method, Var, Param, Struct, Enum, Ctor}` + `KindName()` 中文契约串；`Symbol`（owned 注解快照：TypeEx/ParamAnnos/ParamNames/RecvName/Arity/TypeParams/OwnerName/CaseIndex/PayloadAnnos/Fields/FieldAnnos/Methods(SortedDictionary 保序)/Nominal/IsExtern/IsBuiltin）；`SymbolArena`（Get/Count/Items 声明序遍历/FirstTypeNamed）；`NominalRef{Struct, Enum}`。
- 冻结：`Lang.Semantic.Builtin`：`BuiltinFns` 20 名单（与 A4 冻结一致）、`PreludeStructs=["error"]`、`PreludeCtors`（Some=Option/0/1、None=Option/1/0、Ok=Result/0/1、Err=Result/1/1）、`BuiltinReturn(TypeTables, name, args)`、`IsBuiltinFn(name)`。

## SPEC-GAP / 契约疑义

1. **SPEC-GAP-CS-001（继承 lang-rs RS-013）：用户泛型 struct/enum 无诊断码约定**——GEN-DECL-001 说「用户泛型 struct/enum v0.9 冻结不支持（预载 Option/Result 除外）」，但 27 码表没有对应码，证据实现静默容忍（类型参数按擦除占位参与检查）。本实现同口径：声明不报错，`T` 解析为 TypeParam 擦除占位。如需硬拒绝请裁决补码。
2. **http 服务面缺位**（继承 lang-rs 同款处理）：TS 证据预载 `http` 值 + `HttpResp` struct（checker/resolver 特判 `http.get`），但 A4 冻结的 runtime 内建清单无此项。本实现对齐 A4（不预载）；若后续冻结宿主服务面，只需在 `Builtin.cs` 名单 + `InferMethodCall` 加回特判。
3. **E3007/E3009 发射位与证据完全一致**（E3007 在 fn 声明行、E3009 line 0），含证据的既有怪癖：`FnSignatureOfDecl` 与 `DeclareParams`、以及每个调用点的 `CheckArgsVsSymbol` 会重复解析同一参数注解——坏 Option/Result arity 注解可能多次发码。为诊断序逐字对齐保留该行为。
4. **ADR-0005 已落地**：赋值顶层 `?` 走 `InferTry`（`TryAtAssignTopLevelIsAllowed` 测试锁定），非语句顶层（表达式内部）仍发 E3005；SPEC-GAP-RS-011 在本实现中即按已接受 ADR 实现，无遗留分歧。
