// 诊断码 → Semantic Rule ID 映射（specs/rules.md 的 lang-ts 侧实现）。
// 契约数据：一经发布不可改义；新码追加时同步 specs/rules.md 与 go/internal/contract/rules.go。

export const CODE_TO_RULE: Readonly<Record<string, string>> = {
  E1001: "LEX-ALL",
  E2001: "NAME-DUP-003",
  E2002: "NAME-UNDEF-004",
  E2003: "NAME-CTOR-005",
  E2004: "NAME-RECV-006",
  E2005: "NAME-MUT-007",
  E2006: "NAME-CTOR-005",
  E2007: "NAME-PLACE-008",
  E2008: "NAME-PLACE-008",
  E2010: "NAME-NEST-009",
  E3001: "TYPE-ASSIGN-001",
  E3002: "TYPE-ASSIGN-001",
  E3003: "CALL-ARG-002",
  E3004: "TYPE-MEMBER-007",
  E3005: "RESULT-TRY-003",
  E3006: "RESULT-RETURN-004",
  E3007: "TYPE-ANN-006",
  E3008: "RESULT-TRY-003",
  E3009: "GEN-SUBST-003",
  E3010: "CTRL-FORIN-003",
  E3011: "TYPE-INDEX-008",
  E3012: "VALUE-REF-003",
  E3013: "TYPE-INDEX-008",
  E3014: "TYPE-INDEX-008",
  E4001: "MATCH-EXHAUSTIVE-002",
  E4002: "MATCH-KIND-001",
  E4003: "MATCH-KIND-001",
};

export const ruleOf = (code: string): string => CODE_TO_RULE[code] ?? "";
