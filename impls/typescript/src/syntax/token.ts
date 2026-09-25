// 记号类别与记号（specs/lexical-grammar.md LEX-001..008）。
// TokenKind 用字符串字面量联合表达，kindName 与 Go kindName 表逐一对应
//（解析错误消息按 Go 文本逐字复刻，属契约面）。

export type TokenKind =
  | "eof"
  | "newline"
  | "ident"
  | "int"
  | "float"
  | "string"
  | "underscore"
  | "let"
  | "var"
  | "fn"
  | "return"
  | "if"
  | "else"
  | "while"
  | "for"
  | "in"
  | "match"
  | "struct"
  | "enum"
  | "true"
  | "false"
  | "break"
  | "continue"
  | "go"
  | "chan"
  | "import"
  | "extern"
  | "interface"
  | "+"
  | "-"
  | "*"
  | "/"
  | "%"
  | "="
  | "=="
  | "!="
  | "<"
  | "<="
  | ">"
  | ">="
  | "&&"
  | "||"
  | "!"
  | "&"
  | "|"
  | "^"
  | "<<"
  | ">>"
  | "+="
  | "-="
  | "*="
  | "/="
  | "?"
  | "."
  | ".."
  | ","
  | ":"
  | "=>"
  | "("
  | ")"
  | "{"
  | "}"
  | "["
  | "]";

export interface Token {
  readonly kind: TokenKind;
  /** 字面量文本：字符串记号为解码后的值，整数为十进制规范文本（Q28） */
  readonly lit: string;
  /** 1 基行号 */
  readonly line: number;
  /** 码点偏移 [start, end) */
  readonly start: number;
  readonly end: number;
}

export function kindName(k: TokenKind): string {
  switch (k) {
    case "eof":
      return "文件尾";
    case "newline":
      return "换行";
    case "ident":
      return "标识符";
    case "int":
      return "整数";
    case "float":
      return "浮点数";
    case "string":
      return "字符串";
    case "underscore":
      return "_";
    case "let":
      return "let";
    case "var":
      return "var";
    case "fn":
      return "fn";
    case "return":
      return "return";
    case "if":
      return "if";
    case "else":
      return "else";
    case "while":
      return "while";
    case "for":
      return "for";
    case "in":
      return "in";
    case "match":
      return "match";
    case "struct":
      return "struct";
    case "enum":
      return "enum";
    case "true":
      return "true";
    case "false":
      return "false";
    case "break":
      return "break";
    case "continue":
      return "continue";
    case "go":
      return "go";
    case "chan":
      return "chan";
    case "import":
      return "import";
    case "extern":
      return "extern";
    case "interface":
      return "interface";
    default:
      return k; // 运算符/标点的展示名即其字面
  }
}

export const keywords: Readonly<Record<string, TokenKind>> = {
  let: "let",
  var: "var",
  fn: "fn",
  return: "return",
  if: "if",
  else: "else",
  while: "while",
  for: "for",
  in: "in",
  match: "match",
  struct: "struct",
  enum: "enum",
  true: "true",
  false: "false",
  break: "break",
  continue: "continue",
  go: "go",
  chan: "chan",
  import: "import",
  extern: "extern",
  interface: "interface",
};

/** 该类记号之后换行视为续行（LEX-008） */
export function isCont(k: TokenKind): boolean {
  switch (k) {
    case "+":
    case "-":
    case "*":
    case "/":
    case "%":
    case "=":
    case "==":
    case "!=":
    case "<":
    case "<=":
    case ">":
    case ">=":
    case "&&":
    case "||":
    case ",":
    case ":":
    case "=>":
    case ".":
    case "..":
    case "(":
    case "[":
    case "{":
      return true;
    default:
      return false;
  }
}
