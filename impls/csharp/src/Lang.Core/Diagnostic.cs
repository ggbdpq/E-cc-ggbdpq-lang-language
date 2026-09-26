namespace Lang.Core;

/// <summary>严重级。v0.4 仅 error 实际发射，warning 位保留。</summary>
public enum Severity { Error, Warning }

/// <summary>语义规则身份（specs/rules.md 全量；书写形经 RuleText() 取得，一经发布不可改义）。</summary>
public enum RuleId
{
    LexAll, Lex001, Lex002, Lex003, Lex004, Lex005, Lex006, Lex007, Lex008,
    NameScope001, NameShadow002, NameDup003, NameUndef004, NameCtor005,
    NameRecv006, NameMut007, NamePlace008, NameNest009, NameMerge010,
    TypeKinds001, TypeAny002, TypeAssign001, TypeStruct003, TypeFn004,
    TypeConv005, TypeAnn001, TypeAnn006, TypeMember007, TypeIndex008,
    GenDecl001, GenInfer002, GenSubst003,
    CallResolve001, CallArg002, CallClosure003,
    CtrlIf001, CtrlWhile002, CtrlForin003, CtrlReturn004,
    MatchKind001, MatchExhaustive002, MatchBind003, MatchExpr004,
    ResultPrelude001, ResultNull002, ResultTry003, ResultReturn004, ResultChan005, ResultMatch006,
    ValueCopy001, ValueShare002, ValueRef003, ValueCapture004, ValueImmut005,
    ValueMap006, ValueTaskshare007, ValueUnit008, ValueElem009,
    EvalOrder001, EvalCall002, EvalAssign003, EvalLiteral004, EvalIndex005,
    EvalShortcut001, EvalShortcut006, EvalGo007,
    ModuleRoot001, ModuleImport002, ModuleMerge003, ModuleCycle004, ModuleInit005, ModuleVis006, ModuleTest007,
    TaskModel001, TaskSpawn001, TaskSpawn002, TaskDeterminism003,
    ChanCreate001, ChanSend002, ChanRecv003, ChanClose004, ChanMap005,
    RuntimeFail001, RuntimeChan001, RuntimeChan002, RuntimeEq001, RuntimeArith002, RuntimeEq003,
    RuntimeIndex001, RuntimeJson001, RuntimeJson002, RuntimeMatch001, RuntimeAssert004,
    RuntimeOutput003, RuntimeOutput004, RuntimeType005,
    Ffi001,
}

public static class RuleIdText
{
    public static string RuleText(this RuleId r) => r switch
    {
        RuleId.LexAll => "LEX-ALL",
        >= RuleId.Lex001 and <= RuleId.Lex008 => $"LEX-{(int)r:000}",
        RuleId.NameScope001 => "NAME-SCOPE-001",
        RuleId.NameShadow002 => "NAME-SHADOW-002",
        RuleId.NameDup003 => "NAME-DUP-003",
        RuleId.NameUndef004 => "NAME-UNDEF-004",
        RuleId.NameCtor005 => "NAME-CTOR-005",
        RuleId.NameRecv006 => "NAME-RECV-006",
        RuleId.NameMut007 => "NAME-MUT-007",
        RuleId.NamePlace008 => "NAME-PLACE-008",
        RuleId.NameNest009 => "NAME-NEST-009",
        RuleId.NameMerge010 => "NAME-MERGE-010",
        RuleId.TypeKinds001 => "TYPE-KINDS-001",
        RuleId.TypeAny002 => "TYPE-ANY-002",
        RuleId.TypeAssign001 => "TYPE-ASSIGN-001",
        RuleId.TypeStruct003 => "TYPE-STRUCT-003",
        RuleId.TypeFn004 => "TYPE-FN-004",
        RuleId.TypeConv005 => "TYPE-CONV-005",
        RuleId.TypeAnn001 => "TYPE-ANN-001",
        RuleId.TypeAnn006 => "TYPE-ANN-006",
        RuleId.TypeMember007 => "TYPE-MEMBER-007",
        RuleId.TypeIndex008 => "TYPE-INDEX-008",
        RuleId.GenDecl001 => "GEN-DECL-001",
        RuleId.GenInfer002 => "GEN-INFER-002",
        RuleId.GenSubst003 => "GEN-SUBST-003",
        RuleId.CallResolve001 => "CALL-RESOLVE-001",
        RuleId.CallArg002 => "CALL-ARG-002",
        RuleId.CallClosure003 => "CALL-CLOSURE-003",
        RuleId.CtrlIf001 => "CTRL-IF-001",
        RuleId.CtrlWhile002 => "CTRL-WHILE-002",
        RuleId.CtrlForin003 => "CTRL-FORIN-003",
        RuleId.CtrlReturn004 => "CTRL-RETURN-004",
        RuleId.MatchKind001 => "MATCH-KIND-001",
        RuleId.MatchExhaustive002 => "MATCH-EXHAUSTIVE-002",
        RuleId.MatchBind003 => "MATCH-BIND-003",
        RuleId.MatchExpr004 => "MATCH-EXPR-004",
        RuleId.ResultPrelude001 => "RESULT-PRELUDE-001",
        RuleId.ResultNull002 => "RESULT-NULL-002",
        RuleId.ResultTry003 => "RESULT-TRY-003",
        RuleId.ResultReturn004 => "RESULT-RETURN-004",
        RuleId.ResultChan005 => "RESULT-CHAN-005",
        RuleId.ResultMatch006 => "RESULT-MATCH-006",
        RuleId.ValueCopy001 => "VALUE-COPY-001",
        RuleId.ValueShare002 => "VALUE-SHARE-002",
        RuleId.ValueRef003 => "VALUE-REF-003",
        RuleId.ValueCapture004 => "VALUE-CAPTURE-004",
        RuleId.ValueImmut005 => "VALUE-IMMUT-005",
        RuleId.ValueMap006 => "VALUE-MAP-006",
        RuleId.ValueTaskshare007 => "VALUE-TASKSHARE-007",
        RuleId.ValueUnit008 => "VALUE-UNIT-008",
        RuleId.ValueElem009 => "VALUE-ELEM-009",
        RuleId.EvalOrder001 => "EVAL-ORDER-001",
        RuleId.EvalCall002 => "EVAL-CALL-002",
        RuleId.EvalAssign003 => "EVAL-ASSIGN-003",
        RuleId.EvalLiteral004 => "EVAL-LITERAL-004",
        RuleId.EvalIndex005 => "EVAL-INDEX-005",
        RuleId.EvalShortcut001 => "EVAL-SHORTCUT-001",
        RuleId.EvalShortcut006 => "EVAL-SHORTCUT-006",
        RuleId.EvalGo007 => "EVAL-GO-007",
        RuleId.ModuleRoot001 => "MODULE-ROOT-001",
        RuleId.ModuleImport002 => "MODULE-IMPORT-002",
        RuleId.ModuleMerge003 => "MODULE-MERGE-003",
        RuleId.ModuleCycle004 => "MODULE-CYCLE-004",
        RuleId.ModuleInit005 => "MODULE-INIT-005",
        RuleId.ModuleVis006 => "MODULE-VIS-006",
        RuleId.ModuleTest007 => "MODULE-TEST-007",
        RuleId.TaskModel001 => "TASK-MODEL-001",
        RuleId.TaskSpawn001 => "TASK-SPAWN-001",
        RuleId.TaskSpawn002 => "TASK-SPAWN-002",
        RuleId.TaskDeterminism003 => "TASK-DETERMINISM-003",
        RuleId.ChanCreate001 => "CHAN-CREATE-001",
        RuleId.ChanSend002 => "CHAN-SEND-002",
        RuleId.ChanRecv003 => "CHAN-RECV-003",
        RuleId.ChanClose004 => "CHAN-CLOSE-004",
        RuleId.ChanMap005 => "CHAN-MAP-005",
        RuleId.RuntimeFail001 => "RUNTIME-FAIL-001",
        RuleId.RuntimeChan001 => "RUNTIME-CHAN-001",
        RuleId.RuntimeChan002 => "RUNTIME-CHAN-002",
        RuleId.RuntimeEq001 => "RUNTIME-EQ-001",
        RuleId.RuntimeArith002 => "RUNTIME-ARITH-002",
        RuleId.RuntimeEq003 => "RUNTIME-EQ-003",
        RuleId.RuntimeIndex001 => "RUNTIME-INDEX-001",
        RuleId.RuntimeJson001 => "RUNTIME-JSON-001",
        RuleId.RuntimeJson002 => "RUNTIME-JSON-002",
        RuleId.RuntimeMatch001 => "RUNTIME-MATCH-001",
        RuleId.RuntimeAssert004 => "RUNTIME-ASSERT-004",
        RuleId.RuntimeOutput003 => "RUNTIME-OUTPUT-003",
        RuleId.RuntimeOutput004 => "RUNTIME-OUTPUT-004",
        RuleId.RuntimeType005 => "RUNTIME-TYPE-005",
        RuleId.Ffi001 => "FFI-001",
        _ => throw new InvalidOperationException($"未登记的 RuleId: {r}"),
    };
}

/// <summary>稳定诊断码（27 码全集；E2009 空号保留）。</summary>
public enum DiagnosticCode
{
    E1001,
    E2001, E2002, E2003, E2004, E2005, E2006, E2007, E2008, E2010,
    E3001, E3002, E3003, E3004, E3005, E3006, E3007, E3008, E3009,
    E3010, E3011, E3012, E3013, E3014,
    E4001, E4002, E4003,
}

public static class DiagnosticCodeText
{
    public static string CodeText(this DiagnosticCode c) => $"E{(int)c switch
    {
        0 => 1001,
        var n when n <= 8 => 2000 + n,
        9 => 2010,
        var n when n <= 23 => 3000 + (n - 9),
        var n => 4000 + (n - 23),
    }}";

    /// <summary>码 ↔ Rule 映射（specs/rules.md 27 行，契约级，测试逐行锁定）。</summary>
    public static RuleId Rule(this DiagnosticCode c) => c switch
    {
        DiagnosticCode.E1001 => RuleId.LexAll,
        DiagnosticCode.E2001 => RuleId.NameDup003,
        DiagnosticCode.E2002 => RuleId.NameUndef004,
        DiagnosticCode.E2003 => RuleId.NameCtor005,
        DiagnosticCode.E2004 => RuleId.NameRecv006,
        DiagnosticCode.E2005 => RuleId.NameMut007,
        DiagnosticCode.E2006 => RuleId.NameCtor005,
        DiagnosticCode.E2007 => RuleId.NamePlace008,
        DiagnosticCode.E2008 => RuleId.NamePlace008,
        DiagnosticCode.E2010 => RuleId.NameNest009,
        DiagnosticCode.E3001 => RuleId.TypeAssign001,
        DiagnosticCode.E3002 => RuleId.TypeAssign001,
        DiagnosticCode.E3003 => RuleId.CallArg002,
        DiagnosticCode.E3004 => RuleId.TypeMember007,
        DiagnosticCode.E3005 => RuleId.ResultTry003,
        DiagnosticCode.E3006 => RuleId.ResultReturn004,
        DiagnosticCode.E3007 => RuleId.TypeAnn006,
        DiagnosticCode.E3008 => RuleId.ResultTry003,
        DiagnosticCode.E3009 => RuleId.GenSubst003,
        DiagnosticCode.E3010 => RuleId.CtrlForin003,
        DiagnosticCode.E3011 => RuleId.TypeIndex008,
        DiagnosticCode.E3012 => RuleId.ValueRef003,
        DiagnosticCode.E3013 => RuleId.TypeIndex008,
        DiagnosticCode.E3014 => RuleId.TypeIndex008,
        DiagnosticCode.E4001 => RuleId.MatchExhaustive002,
        DiagnosticCode.E4002 => RuleId.MatchKind001,
        DiagnosticCode.E4003 => RuleId.MatchKind001,
        _ => throw new InvalidOperationException($"未登记的 DiagnosticCode: {c}"),
    };
}

/// <summary>诊断结构（结构即契约；JSON 形态由 Lang.Contract 落）。</summary>
public sealed record Diagnostic
{
    public required DiagnosticCode Code { get; init; }
    public required RuleId RuleId { get; init; }
    public required Severity Severity { get; init; }
    public required string Message { get; init; }
    /// <summary>v0.4 行级：line 正确、column 恒 1、offset 恒 0（未填）。</summary>
    public Span? PrimarySpan { get; init; }
    public IReadOnlyList<Span> RelatedSpans { get; init; } = Array.Empty<Span>();
    public string Help { get; init; } = "";

    /// <summary>以 code 构造：severity 固定 error、ruleId 由码推导（不可由调用方改写）。</summary>
    public static Diagnostic New(DiagnosticCode code, string message, Span? primarySpan = null) => new()
    {
        Code = code,
        RuleId = code.Rule(),
        Severity = Severity.Error,
        Message = message,
        PrimarySpan = primarySpan,
    };

    /// <summary>行级锚（v0.4 形态）：定位到某行第 1 列的单点空 span。</summary>
    public static Span AtLine(SourceId source, uint line)
    {
        var pos = new Position(0, line, 1);
        return new Span(source, pos, pos);
    }
}
