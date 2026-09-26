//! 诊断契约（specs/diagnostics.md + specs/rules.md）。
//!
//! code + severity + 定位结构 + ruleId 都是跨实现契约：码只能追加、不可改义、不可复用。
//! v0.4 全部为 error 级；primarySpan 为行级（line 正确、column 恒 1、offset 未填即 0）。

use crate::ids::SourceId;
use crate::span::{Position, Span};

/// 严重级。v0.4 仅 error 实际发射，warning 位保留。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum Severity {
    Error,
    Warning,
}

impl Severity {
    pub fn as_str(&self) -> &'static str {
        match self {
            Severity::Error => "error",
            Severity::Warning => "warning",
        }
    }
}

/// 语义规则身份（specs/rules.md 全量；一经发布不可改义）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum RuleId {
    LexAll,
    Lex001,
    Lex002,
    Lex003,
    Lex004,
    Lex005,
    Lex006,
    Lex007,
    Lex008,
    NameScope001,
    NameShadow002,
    NameDup003,
    NameUndef004,
    NameCtor005,
    NameRecv006,
    NameMut007,
    NamePlace008,
    NameNest009,
    NameMerge010,
    TypeKinds001,
    TypeAny002,
    TypeAssign001,
    TypeStruct003,
    TypeFn004,
    TypeConv005,
    TypeAnn001,
    TypeAnn006,
    TypeMember007,
    TypeIndex008,
    GenDecl001,
    GenInfer002,
    GenSubst003,
    CallResolve001,
    CallArg002,
    CallClosure003,
    CtrlIf001,
    CtrlWhile002,
    CtrlForin003,
    CtrlReturn004,
    MatchKind001,
    MatchExhaustive002,
    MatchBind003,
    MatchExpr004,
    ResultPrelude001,
    ResultNull002,
    ResultTry003,
    ResultReturn004,
    ResultChan005,
    ResultMatch006,
    ValueCopy001,
    ValueShare002,
    ValueRef003,
    ValueCapture004,
    ValueImmut005,
    ValueMap006,
    ValueTaskshare007,
    ValueUnit008,
    ValueElem009,
    EvalOrder001,
    EvalCall002,
    EvalAssign003,
    EvalLiteral004,
    EvalIndex005,
    EvalShortcut001,
    EvalShortcut006,
    EvalGo007,
    ModuleRoot001,
    ModuleImport002,
    ModuleMerge003,
    ModuleCycle004,
    ModuleInit005,
    ModuleVis006,
    ModuleTest007,
    TaskModel001,
    TaskSpawn001,
    TaskSpawn002,
    TaskDeterminism003,
    ChanCreate001,
    ChanSend002,
    ChanRecv003,
    ChanClose004,
    ChanMap005,
    RuntimeFail001,
    RuntimeChan001,
    RuntimeChan002,
    RuntimeEq001,
    RuntimeArith002,
    RuntimeEq003,
    RuntimeIndex001,
    RuntimeJson001,
    RuntimeJson002,
    RuntimeMatch001,
    RuntimeAssert004,
    RuntimeOutput003,
    RuntimeOutput004,
    RuntimeType005,
    Ffi001,
}

macro_rules! rule_strs {
    ($($v:ident => $s:literal),* $(,)?) => {
        impl RuleId {
            /// 规范书写形（如 "NAME-SHADOW-002"）。
            pub fn as_str(&self) -> &'static str {
                match self {
                    $(RuleId::$v => $s,)*
                }
            }
        }
    };
}

rule_strs! {
    LexAll => "LEX-ALL",
    Lex001 => "LEX-001", Lex002 => "LEX-002", Lex003 => "LEX-003", Lex004 => "LEX-004",
    Lex005 => "LEX-005", Lex006 => "LEX-006", Lex007 => "LEX-007", Lex008 => "LEX-008",
    NameScope001 => "NAME-SCOPE-001", NameShadow002 => "NAME-SHADOW-002",
    NameDup003 => "NAME-DUP-003", NameUndef004 => "NAME-UNDEF-004",
    NameCtor005 => "NAME-CTOR-005", NameRecv006 => "NAME-RECV-006",
    NameMut007 => "NAME-MUT-007", NamePlace008 => "NAME-PLACE-008",
    NameNest009 => "NAME-NEST-009", NameMerge010 => "NAME-MERGE-010",
    TypeKinds001 => "TYPE-KINDS-001", TypeAny002 => "TYPE-ANY-002",
    TypeAssign001 => "TYPE-ASSIGN-001", TypeStruct003 => "TYPE-STRUCT-003",
    TypeFn004 => "TYPE-FN-004", TypeConv005 => "TYPE-CONV-005",
    TypeAnn001 => "TYPE-ANN-001", TypeAnn006 => "TYPE-ANN-006",
    TypeMember007 => "TYPE-MEMBER-007", TypeIndex008 => "TYPE-INDEX-008",
    GenDecl001 => "GEN-DECL-001", GenInfer002 => "GEN-INFER-002", GenSubst003 => "GEN-SUBST-003",
    CallResolve001 => "CALL-RESOLVE-001", CallArg002 => "CALL-ARG-002",
    CallClosure003 => "CALL-CLOSURE-003",
    CtrlIf001 => "CTRL-IF-001", CtrlWhile002 => "CTRL-WHILE-002",
    CtrlForin003 => "CTRL-FORIN-003", CtrlReturn004 => "CTRL-RETURN-004",
    MatchKind001 => "MATCH-KIND-001", MatchExhaustive002 => "MATCH-EXHAUSTIVE-002",
    MatchBind003 => "MATCH-BIND-003", MatchExpr004 => "MATCH-EXPR-004",
    ResultPrelude001 => "RESULT-PRELUDE-001", ResultNull002 => "RESULT-NULL-002",
    ResultTry003 => "RESULT-TRY-003", ResultReturn004 => "RESULT-RETURN-004",
    ResultChan005 => "RESULT-CHAN-005", ResultMatch006 => "RESULT-MATCH-006",
    ValueCopy001 => "VALUE-COPY-001", ValueShare002 => "VALUE-SHARE-002",
    ValueRef003 => "VALUE-REF-003", ValueCapture004 => "VALUE-CAPTURE-004",
    ValueImmut005 => "VALUE-IMMUT-005", ValueMap006 => "VALUE-MAP-006",
    ValueTaskshare007 => "VALUE-TASKSHARE-007", ValueUnit008 => "VALUE-UNIT-008",
    ValueElem009 => "VALUE-ELEM-009",
    EvalOrder001 => "EVAL-ORDER-001", EvalCall002 => "EVAL-CALL-002",
    EvalAssign003 => "EVAL-ASSIGN-003", EvalLiteral004 => "EVAL-LITERAL-004",
    EvalIndex005 => "EVAL-INDEX-005", EvalShortcut001 => "EVAL-SHORTCUT-001",
    EvalShortcut006 => "EVAL-SHORTCUT-006", EvalGo007 => "EVAL-GO-007",
    ModuleRoot001 => "MODULE-ROOT-001", ModuleImport002 => "MODULE-IMPORT-002",
    ModuleMerge003 => "MODULE-MERGE-003", ModuleCycle004 => "MODULE-CYCLE-004",
    ModuleInit005 => "MODULE-INIT-005", ModuleVis006 => "MODULE-VIS-006",
    ModuleTest007 => "MODULE-TEST-007",
    TaskModel001 => "TASK-MODEL-001", TaskSpawn001 => "TASK-SPAWN-001",
    TaskSpawn002 => "TASK-SPAWN-002", TaskDeterminism003 => "TASK-DETERMINISM-003",
    ChanCreate001 => "CHAN-CREATE-001", ChanSend002 => "CHAN-SEND-002",
    ChanRecv003 => "CHAN-RECV-003", ChanClose004 => "CHAN-CLOSE-004",
    ChanMap005 => "CHAN-MAP-005",
    RuntimeFail001 => "RUNTIME-FAIL-001", RuntimeChan001 => "RUNTIME-CHAN-001",
    RuntimeChan002 => "RUNTIME-CHAN-002", RuntimeEq001 => "RUNTIME-EQ-001",
    RuntimeArith002 => "RUNTIME-ARITH-002", RuntimeEq003 => "RUNTIME-EQ-003",
    RuntimeIndex001 => "RUNTIME-INDEX-001", RuntimeJson001 => "RUNTIME-JSON-001",
    RuntimeJson002 => "RUNTIME-JSON-002", RuntimeMatch001 => "RUNTIME-MATCH-001",
    RuntimeAssert004 => "RUNTIME-ASSERT-004", RuntimeOutput003 => "RUNTIME-OUTPUT-003",
    RuntimeOutput004 => "RUNTIME-OUTPUT-004", RuntimeType005 => "RUNTIME-TYPE-005",
    Ffi001 => "FFI-001",
}

/// 稳定诊断码（27 码全集；E2009 空号保留）。
#[derive(Clone, Copy, PartialEq, Eq, Hash, Debug)]
pub enum DiagnosticCode {
    E1001,
    E2001,
    E2002,
    E2003,
    E2004,
    E2005,
    E2006,
    E2007,
    E2008,
    E2010,
    E3001,
    E3002,
    E3003,
    E3004,
    E3005,
    E3006,
    E3007,
    E3008,
    E3009,
    E3010,
    E3011,
    E3012,
    E3013,
    E3014,
    E4001,
    E4002,
    E4003,
}

impl DiagnosticCode {
    pub fn as_str(&self) -> &'static str {
        match self {
            DiagnosticCode::E1001 => "E1001",
            DiagnosticCode::E2001 => "E2001",
            DiagnosticCode::E2002 => "E2002",
            DiagnosticCode::E2003 => "E2003",
            DiagnosticCode::E2004 => "E2004",
            DiagnosticCode::E2005 => "E2005",
            DiagnosticCode::E2006 => "E2006",
            DiagnosticCode::E2007 => "E2007",
            DiagnosticCode::E2008 => "E2008",
            DiagnosticCode::E2010 => "E2010",
            DiagnosticCode::E3001 => "E3001",
            DiagnosticCode::E3002 => "E3002",
            DiagnosticCode::E3003 => "E3003",
            DiagnosticCode::E3004 => "E3004",
            DiagnosticCode::E3005 => "E3005",
            DiagnosticCode::E3006 => "E3006",
            DiagnosticCode::E3007 => "E3007",
            DiagnosticCode::E3008 => "E3008",
            DiagnosticCode::E3009 => "E3009",
            DiagnosticCode::E3010 => "E3010",
            DiagnosticCode::E3011 => "E3011",
            DiagnosticCode::E3012 => "E3012",
            DiagnosticCode::E3013 => "E3013",
            DiagnosticCode::E3014 => "E3014",
            DiagnosticCode::E4001 => "E4001",
            DiagnosticCode::E4002 => "E4002",
            DiagnosticCode::E4003 => "E4003",
        }
    }

    /// 码 ↔ Rule 映射（specs/rules.md 27 行，契约级，测试逐行锁定）。
    pub fn rule(&self) -> RuleId {
        match self {
            DiagnosticCode::E1001 => RuleId::LexAll,
            DiagnosticCode::E2001 => RuleId::NameDup003,
            DiagnosticCode::E2002 => RuleId::NameUndef004,
            DiagnosticCode::E2003 => RuleId::NameCtor005,
            DiagnosticCode::E2004 => RuleId::NameRecv006,
            DiagnosticCode::E2005 => RuleId::NameMut007,
            DiagnosticCode::E2006 => RuleId::NameCtor005,
            DiagnosticCode::E2007 => RuleId::NamePlace008,
            DiagnosticCode::E2008 => RuleId::NamePlace008,
            DiagnosticCode::E2010 => RuleId::NameNest009,
            DiagnosticCode::E3001 => RuleId::TypeAssign001,
            DiagnosticCode::E3002 => RuleId::TypeAssign001,
            DiagnosticCode::E3003 => RuleId::CallArg002,
            DiagnosticCode::E3004 => RuleId::TypeMember007,
            DiagnosticCode::E3005 => RuleId::ResultTry003,
            DiagnosticCode::E3006 => RuleId::ResultReturn004,
            DiagnosticCode::E3007 => RuleId::TypeAnn006,
            DiagnosticCode::E3008 => RuleId::ResultTry003,
            DiagnosticCode::E3009 => RuleId::GenSubst003,
            DiagnosticCode::E3010 => RuleId::CtrlForin003,
            DiagnosticCode::E3011 => RuleId::TypeIndex008,
            DiagnosticCode::E3012 => RuleId::ValueRef003,
            DiagnosticCode::E3013 => RuleId::TypeIndex008,
            DiagnosticCode::E3014 => RuleId::TypeIndex008,
            DiagnosticCode::E4001 => RuleId::MatchExhaustive002,
            DiagnosticCode::E4002 => RuleId::MatchKind001,
            DiagnosticCode::E4003 => RuleId::MatchKind001,
        }
    }
}

/// 诊断结构（结构即契约；JSON 形态由 lang-contract 落）。
#[derive(Clone, PartialEq, Eq, Hash, Debug)]
pub struct Diagnostic {
    pub code: DiagnosticCode,
    pub rule_id: RuleId,
    pub severity: Severity,
    pub message: String,
    /// v0.4 行级：line 正确、column 恒 1、offset 未填（0）。
    pub primary_span: Option<Span>,
    pub related_spans: Vec<Span>,
    pub help: String,
}

impl Diagnostic {
    /// 以 code 构造：severity 固定 error、ruleId 由码推导（两者不可由调用方改写）。
    pub fn new(
        code: DiagnosticCode,
        message: impl Into<String>,
        primary_span: Option<Span>,
    ) -> Self {
        Self {
            code,
            rule_id: code.rule(),
            severity: Severity::Error,
            message: message.into(),
            primary_span,
            related_spans: Vec::new(),
            help: String::new(),
        }
    }

    /// 行级锚（v0.4 形态）：定位到某行第 1 列的单点空 span。
    pub fn at_line(source: SourceId, line: u32) -> Span {
        let pos = Position::new(0, line, 1);
        Span {
            source,
            start: pos,
            end: pos,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn code_strings_verbatim() {
        assert_eq!(DiagnosticCode::E1001.as_str(), "E1001");
        assert_eq!(DiagnosticCode::E2010.as_str(), "E2010");
        assert_eq!(DiagnosticCode::E4003.as_str(), "E4003");
    }

    #[test]
    fn code_to_rule_mapping_samples() {
        // rules.md 表逐行抽验：一码多规则、多码一规则、段位归属都覆盖
        assert_eq!(DiagnosticCode::E1001.rule(), RuleId::LexAll);
        assert_eq!(DiagnosticCode::E2002.rule(), RuleId::NameUndef004);
        assert_eq!(DiagnosticCode::E2006.rule(), RuleId::NameCtor005);
        assert_eq!(DiagnosticCode::E2007.rule(), RuleId::NamePlace008);
        assert_eq!(DiagnosticCode::E2008.rule(), RuleId::NamePlace008);
        assert_eq!(DiagnosticCode::E3009.rule(), RuleId::GenSubst003);
        assert_eq!(DiagnosticCode::E3012.rule(), RuleId::ValueRef003);
        assert_eq!(DiagnosticCode::E3014.rule(), RuleId::TypeIndex008);
        assert_eq!(DiagnosticCode::E4001.rule(), RuleId::MatchExhaustive002);
        assert_eq!(DiagnosticCode::E4003.rule(), RuleId::MatchKind001);
    }

    #[test]
    fn new_derives_rule_and_pins_error() {
        let d = Diagnostic::new(DiagnosticCode::E2002, "未定义 nope", None);
        assert_eq!(d.rule_id, RuleId::NameUndef004);
        assert_eq!(d.severity, Severity::Error);
        assert_eq!(d.severity.as_str(), "error");
        assert!(d.related_spans.is_empty());
    }

    #[test]
    fn line_anchor_is_empty_point() {
        let s = Diagnostic::at_line(SourceId(0), 7);
        assert_eq!(s.start, Position::new(0, 7, 1));
        assert_eq!(s.end, s.start);
    }

    #[test]
    fn rule_canonical_strings() {
        assert_eq!(RuleId::NameShadow002.as_str(), "NAME-SHADOW-002");
        assert_eq!(RuleId::ValueTaskshare007.as_str(), "VALUE-TASKSHARE-007");
        assert_eq!(RuleId::Ffi001.as_str(), "FFI-001");
    }
}
