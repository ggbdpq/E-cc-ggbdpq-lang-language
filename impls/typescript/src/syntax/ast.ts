// AST（specs/grammar.md PARSE-001..006）。
// 判别联合 + readonly 模型；节点带 line（起始记号 1 基行号）与 span（码点度量）。
// Span.file 由装载器在解析时传入（模块根相对路径，/ 分隔）。

import type { Span } from "./span.js";

// —— 表达式 ——

export interface IntLit {
  readonly kind: "int_lit";
  /** int64，宿主以 bigint 承载（JS number 不足 53 位即失真） */
  readonly value: bigint;
  readonly line: number;
  readonly span: Span;
}
export interface FloatLit {
  readonly kind: "float_lit";
  readonly value: number;
  readonly line: number;
  readonly span: Span;
}
export interface StrLit {
  readonly kind: "str_lit";
  readonly value: string;
  readonly line: number;
  readonly span: Span;
}
export interface BoolLit {
  readonly kind: "bool_lit";
  readonly value: boolean;
  readonly line: number;
  readonly span: Span;
}
export interface Ident {
  readonly kind: "ident";
  readonly name: string;
  readonly line: number;
  readonly span: Span;
}
export interface UnaryEx {
  readonly kind: "unary";
  readonly op: UnaryOp;
  readonly x: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface BinaryEx {
  readonly kind: "binary";
  readonly op: BinaryOp;
  readonly l: Expr;
  readonly r: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface RangeEx {
  readonly kind: "range";
  readonly lo: Expr;
  readonly hi: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface CallEx {
  readonly kind: "call";
  readonly callee: Expr;
  readonly args: readonly Expr[];
  readonly line: number;
  readonly span: Span;
}
export interface IndexEx {
  readonly kind: "index";
  readonly obj: Expr;
  readonly idx: Expr;
  readonly line: number;
  readonly span: Span;
}
/** a[lo:hi]，lo/hi 必填 */
export interface SliceEx {
  readonly kind: "slice";
  readonly obj: Expr;
  readonly lo: Expr;
  readonly hi: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface FieldEx {
  readonly kind: "field";
  readonly obj: Expr;
  readonly name: string;
  readonly line: number;
  readonly span: Span;
}
/** e? —— Result 传播 */
export interface TryEx {
  readonly kind: "try";
  readonly x: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface FieldLit {
  readonly name: string;
  readonly val: Expr;
}
export interface StructLit {
  readonly kind: "struct_lit";
  readonly typeName: string;
  readonly fields: readonly FieldLit[];
  readonly line: number;
  readonly span: Span;
}
export interface MapItem {
  readonly key: Expr;
  readonly val: Expr;
}
export interface MapLit {
  readonly kind: "map_lit";
  readonly items: readonly MapItem[];
  readonly line: number;
  readonly span: Span;
}
export interface ArrLit {
  readonly kind: "arr_lit";
  readonly items: readonly Expr[];
  readonly line: number;
  readonly span: Span;
}
export interface Arm {
  readonly pat: Pat;
  readonly body: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface MatchEx {
  readonly kind: "match";
  readonly scrut: Expr;
  readonly arms: readonly Arm[];
  readonly line: number;
  readonly span: Span;
}
/** 匿名函数表达式 */
export interface FnLit {
  readonly kind: "fn_lit";
  readonly typeParams: readonly string[];
  readonly params: readonly Param[];
  readonly ret: TypeEx | undefined;
  readonly body: readonly Stmt[];
  readonly line: number;
  readonly span: Span;
}

export type UnaryOp = "-" | "!" | "&";

export type BinaryOp =
  | "=="
  | "!="
  | "<"
  | "<="
  | ">"
  | ">="
  | "+"
  | "-"
  | "*"
  | "/"
  | "%"
  | "&"
  | "|"
  | "^"
  | "<<"
  | ">>"
  | "&&"
  | "||";

export type Expr =
  | IntLit
  | FloatLit
  | StrLit
  | BoolLit
  | Ident
  | UnaryEx
  | BinaryEx
  | RangeEx
  | CallEx
  | IndexEx
  | SliceEx
  | FieldEx
  | TryEx
  | StructLit
  | MapLit
  | ArrLit
  | MatchEx
  | FnLit;

// —— 模式 ——

export interface WildPat {
  readonly kind: "wild_pat";
  readonly line: number;
  readonly span: Span;
}
export interface BindPat {
  readonly kind: "bind_pat";
  readonly name: string;
  readonly line: number;
  readonly span: Span;
}
/** 字面量模式（Int/Float/Str/Bool） */
export interface LitPat {
  readonly kind: "lit_pat";
  readonly val: Expr;
  readonly line: number;
  readonly span: Span;
}
/** 枚举构造模式，subs 为载荷子模式 */
export interface CasePat {
  readonly kind: "case_pat";
  readonly name: string;
  readonly subs: readonly Pat[];
  readonly line: number;
  readonly span: Span;
}

export type Pat = WildPat | BindPat | LitPat | CasePat;

// —— 语句与声明 ——

export interface Param {
  readonly name: string;
  readonly typ: TypeEx | undefined;
}

export interface LetStmt {
  readonly kind: "let_stmt";
  readonly name: string;
  readonly mutable: boolean;
  readonly typ: TypeEx | undefined;
  readonly init: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface AssignStmt {
  readonly kind: "assign";
  readonly target: Expr;
  readonly val: Expr;
  /** "=" 或复合赋值 "+=" "-=" "*=" "/=" */
  readonly op: AssignOp;
  readonly line: number;
  readonly span: Span;
}
export interface ExprStmt {
  readonly kind: "expr_stmt";
  readonly x: Expr;
  readonly line: number;
  readonly span: Span;
}
export interface ReturnStmt {
  readonly kind: "return";
  /** 可空：bare return */
  readonly val: Expr | undefined;
  readonly line: number;
  readonly span: Span;
}
export interface IfStmt {
  readonly kind: "if";
  readonly cond: Expr;
  readonly then: readonly Stmt[];
  /** 可空；else if 展开为单元素块 */
  readonly els: readonly Stmt[] | undefined;
  readonly line: number;
  readonly span: Span;
}
export interface WhileStmt {
  readonly kind: "while";
  readonly cond: Expr;
  readonly body: readonly Stmt[];
  readonly line: number;
  readonly span: Span;
}
export interface ForInStmt {
  readonly kind: "for_in";
  readonly variable: string;
  readonly iter: Expr;
  readonly body: readonly Stmt[];
  readonly line: number;
  readonly span: Span;
}
export interface BreakStmt {
  readonly kind: "break";
  readonly line: number;
  readonly span: Span;
}
export interface ContinueStmt {
  readonly kind: "continue";
  readonly line: number;
  readonly span: Span;
}
export interface FnDecl {
  readonly kind: "fn_decl";
  readonly name: string;
  /** 值接收者；无接收者方法为 undefined */
  readonly recv: Param | undefined;
  readonly typeParams: readonly string[];
  readonly params: readonly Param[];
  readonly ret: TypeEx | undefined;
  readonly body: readonly Stmt[];
  readonly line: number;
  readonly span: Span;
}
export interface StructDecl {
  readonly kind: "struct_decl";
  readonly name: string;
  readonly typeParams: readonly string[];
  readonly fields: readonly Param[];
  readonly line: number;
  readonly span: Span;
}
export interface EnumCase {
  readonly name: string;
  readonly payloads: readonly TypeEx[];
  readonly line: number;
  readonly span: Span;
}
export interface EnumDecl {
  readonly kind: "enum_decl";
  readonly name: string;
  readonly typeParams: readonly string[];
  readonly cases: readonly EnumCase[];
  readonly line: number;
  readonly span: Span;
}
/** go <调用>：后台任务（TASK-SPAWN-002） */
export interface GoStmt {
  readonly kind: "go";
  readonly call: CallEx;
  readonly line: number;
  readonly span: Span;
}
/** import "./md5"（装载器在合并前展开，解释器不会执行到） */
export interface ImportStmt {
  readonly kind: "import";
  readonly path: string;
  readonly line: number;
  readonly span: Span;
}
/** extern "host" { fn 签名 } 声明块：宿主函数手写签名 */
export interface ExternDecl {
  readonly kind: "extern";
  readonly host: string;
  readonly fns: readonly FnDecl[];
  readonly line: number;
  readonly span: Span;
}

export type AssignOp = "=" | "+=" | "-=" | "*=" | "/=";

export type Stmt =
  | LetStmt
  | AssignStmt
  | ExprStmt
  | ReturnStmt
  | IfStmt
  | WhileStmt
  | ForInStmt
  | BreakStmt
  | ContinueStmt
  | FnDecl
  | StructDecl
  | EnumDecl
  | GoStmt
  | ImportStmt
  | ExternDecl;

// —— 类型注解 ——

export interface NamedType {
  readonly kind: "named_type";
  readonly name: string;
  readonly args: readonly TypeEx[];
}
export interface ArrType {
  readonly kind: "arr_type";
  readonly elem: TypeEx;
}
export interface MapType {
  readonly kind: "map_type";
  readonly key: TypeEx;
  readonly val: TypeEx;
}
export interface PtrType {
  readonly kind: "ptr_type";
  readonly elem: TypeEx;
}
/** 函数类型：fn(int, string): bool */
export interface FnType {
  readonly kind: "fn_type";
  readonly params: readonly TypeEx[];
  readonly ret: TypeEx | undefined;
}

export type TypeEx = NamedType | ArrType | MapType | PtrType | FnType;
