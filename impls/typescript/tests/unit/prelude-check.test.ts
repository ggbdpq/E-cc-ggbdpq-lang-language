import { expect, it } from "vitest";
import { jsPrelude } from "../../src/backends/jsprelude.js";

it("prelude float formatter matches Go %v", () => {
  expect(jsPrelude).toContain("exec(v.toExponential())");
  const fn = new Function(jsPrelude + "; return langGoFloatStr;")();
  expect(fn(123456789.5)).toBe("1.234567895e+08");
  expect(fn(1234567)).toBe("1.234567e+06");
  expect(fn(123456)).toBe("123456");
  expect(fn(1000000)).toBe("1e+06");
  expect(fn(999999)).toBe("999999");
  expect(fn(1e21)).toBe("1e+21");
  expect(fn(1e20)).toBe("1e+20");
  expect(fn(0.00001)).toBe("1e-05");
  expect(fn(0.0001)).toBe("0.0001");
  expect(fn(0.1)).toBe("0.1");
  expect(fn(2.5)).toBe("2.5");
  expect(fn(-0)).toBe("-0");
});

it("prelude JSON stringify keeps bigint precision", () => {
  const mk = new Function(jsPrelude + "; return langGoNewMap;")();
  const str = new Function(jsPrelude + "; return langGoJSONStringify;")();
  const map = mk(["n", 9223372036854775807n]);
  expect(str(map)).toBe('{"n":9223372036854775807}');
});

it("prelude langGoInt saturates and zeroes NaN (RS-006)", () => {
  const int = new Function(jsPrelude + "; return langGoInt;")();
  // 期望值手算（TYPE-CONV-005 v1.0.1）：NaN → 0；溢出饱和；向零截断
  expect(int(Number.NaN)).toBe(0n);
  expect(int(9.3e18)).toBe(9223372036854775807n);
  expect(int(1e300)).toBe(9223372036854775807n);
  expect(int(Infinity)).toBe(9223372036854775807n);
  expect(int(-9.3e18)).toBe(-9223372036854775808n);
  expect(int(-1e300)).toBe(-9223372036854775808n);
  expect(int(-Infinity)).toBe(-9223372036854775808n);
  expect(int(0.5)).toBe(0n);
  expect(int(-0.5)).toBe(0n);
  expect(int(2.9)).toBe(2n);
  expect(int(9223372036854775807n)).toBe(9223372036854775807n);
});

it("prelude jsonStringify rejects non-string map keys (RS-003)", () => {
  const mk = new Function(jsPrelude + "; return langGoNewMap;")();
  const str = new Function(jsPrelude + "; return langGoJSONStringify;")();
  const map = mk([1n, "x"]);
  expect(() => str(map)).toThrow("JSON 对象键须为 string，得到 int");
});
