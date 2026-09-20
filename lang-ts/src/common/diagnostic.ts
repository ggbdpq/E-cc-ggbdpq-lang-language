// 统一诊断形态（E1xxx 解析 / E2xxx 名字解析 / E3xxx-E4xxx 类型与模式）。
// code + severity + 定位结构是契约；message 文本按 Go 参考实现复刻。

export interface Diagnostic {
  readonly code: string;
  readonly msg: string;
  readonly line: number;
}

export const diag = (code: string, msg: string, line: number): Diagnostic => ({ code, msg, line });

export const formatDiag = (d: Diagnostic): string => `第${d.line}行: [${d.code}] ${d.msg}`;

/** 多诊断聚合（首个诊断即 Error() 文本，与 Go ErrorList 一致） */
export const formatDiags = (diags: readonly Diagnostic[]): string =>
  diags.map(formatDiag).join("\n");

// —— 稳定码常量（specs/diagnostics.md 全集） ——

export const E1001 = "E1001";
export const E2001 = "E2001";
export const E2002 = "E2002";
export const E2003 = "E2003";
export const E2004 = "E2004";
export const E2005 = "E2005";
export const E2006 = "E2006";
export const E2007 = "E2007";
export const E2008 = "E2008";
export const E2010 = "E2010";
export const E3001 = "E3001";
export const E3002 = "E3002";
export const E3003 = "E3003";
export const E3004 = "E3004";
export const E3005 = "E3005";
export const E3006 = "E3006";
export const E3007 = "E3007";
export const E3008 = "E3008";
export const E3009 = "E3009";
export const E3010 = "E3010";
export const E3011 = "E3011";
export const E3012 = "E3012";
export const E3013 = "E3013";
export const E3014 = "E3014";
export const E4001 = "E4001";
export const E4002 = "E4002";
export const E4003 = "E4003";
