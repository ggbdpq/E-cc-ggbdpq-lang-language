package interp_test

// 测试缝：ling 源码进，stdout/错误出。这是用户可见行为，不测内部结构。

import (
	"bytes"
	"strings"
	"testing"

	"ling/internal/interp"
	"ling/internal/syntax"
)

func runSrc(t *testing.T, src string) (string, error) {
	t.Helper()
	prog, err := syntax.Parse(src)
	if err != nil {
		t.Fatalf("解析失败: %v\n源码:\n%s", err, src)
	}
	var out bytes.Buffer
	it := interp.New(&out)
	err = it.Run(prog)
	return out.String(), err
}

func wantOut(t *testing.T, src, want string) {
	t.Helper()
	got, err := runSrc(t, src)
	if err != nil {
		t.Fatalf("运行出错: %v\n源码:\n%s", err, src)
	}
	if got != want {
		t.Fatalf("输出不符\n源码:\n%s\n=== 期望 ===\n%q\n=== 实际 ===\n%q", src, want, got)
	}
}

func wantErr(t *testing.T, src, wantSub string) {
	t.Helper()
	_, err := runSrc(t, src)
	if err == nil {
		t.Fatalf("期望报错但成功了\n源码:\n%s", src)
	}
	if !strings.Contains(err.Error(), wantSub) {
		t.Fatalf("错误信息不含 %q\n实际: %v\n源码:\n%s", wantSub, err, src)
	}
}

// —— 切片 1：基础求值 ——

func Test算术与优先级(t *testing.T) {
	wantOut(t, `println(1 + 2 * 3)`, "7\n")
	wantOut(t, `println((1 + 2) * 3)`, "9\n")
	wantOut(t, `println(10 / 3)`, "3\n")
	wantOut(t, `println(7 % 3)`, "1\n")
	wantOut(t, `println(2.5 * 2.0)`, "5\n")
	wantOut(t, `println(-3 + 10)`, "7\n")
}

func Test整型浮点不隐式转换(t *testing.T) {
	wantErr(t, "println(1 + 2.0)", "int")
	wantErr(t, `if 1 < 2.5 { println(1) }`, "int")
}

func Test显式转换(t *testing.T) {
	wantOut(t, `println(int(2.9))`, "2\n")
	wantOut(t, `println(float(3) / 2.0)`, "1.5\n")
	wantOut(t, `println(int("42") + 1)`, "43\n")
	wantOut(t, `println(str(12) + "x")`, "12x\n")
}

func Test除零与模零(t *testing.T) {
	wantErr(t, "println(1 / 0)", "除")
	wantErr(t, "println(1 % 0)", "模")
}

func Test绑定与可变性(t *testing.T) {
	wantOut(t, "var x = 1\nx = 2\nprintln(x)", "2\n")
	wantErr(t, "let x = 1\nx = 2", "let")
	wantOut(t, "let a = 1\nvar b = a + 1\nb = b * 10\nprintln(b)", "20\n")
}

func Test字符串(t *testing.T) {
	wantOut(t, `println("hello" + " " + "ling")`, "hello ling\n")
	wantOut(t, `println(len("你好ab"))`, "4\n")
	wantOut(t, `println("hello" == "hello")`, "true\n")
	wantOut(t, `println("a" < "b")`, "true\n")
	wantOut(t, `println("tab\there")`, "tab\there\n")
}

func Test索引与切片(t *testing.T) {
	wantOut(t, `println("hello"[1])`, "e\n")
	wantOut(t, `println("你好ling"[1])`, "好\n")
	wantOut(t, `println("hello"[1:3])`, "el\n")
	wantOut(t, `println("你好世界"[1:3])`, "好世\n")
}

func Test数组(t *testing.T) {
	wantOut(t, "var xs = [1, 2, 3]\nprintln(xs[0] + xs[2])", "4\n")
	wantOut(t, "var xs = [1, 2, 3]\nxs[1] = 20\nprintln(xs[1])", "20\n")
	wantOut(t, "println(len([10, 20]))", "2\n")
	wantErr(t, "println([1, 2][5])", "界")
}

func Test映射(t *testing.T) {
	wantOut(t, `var m = {"a": 1, "b": 2}
m["c"] = 3
println(m["b"] + m["c"])`, "5\n")
	wantOut(t, `var m = {"a": 1, "b": 2, "c": 3}
for k in keys(m) {
    println(k)
}`, "a\nb\nc\n")
	wantOut(t, `println(has({"x": 1}, "x"))`, "true\n")
	wantOut(t, `println(has({"x": 1}, "y"))`, "false\n")
	wantOut(t, `println(len({"a": 1, "b": 2}))`, "2\n")
}

func Test条件(t *testing.T) {
	wantOut(t, "if 1 < 2 { println(\"lt\") } else { println(\"ge\") }", "lt\n")
	wantOut(t, "var x = 5\nif x < 1 { println(1) } else if x < 10 { println(2) } else { println(3) }", "2\n")
}

func TestWhile循环(t *testing.T) {
	wantOut(t, `var i = 0
var s = 0
while i < 5 {
    s = s + i
    i = i + 1
}
println(s)`, "10\n")
}

func TestFor循环与区间(t *testing.T) {
	wantOut(t, `var s = 0
for i in 0..5 {
    s = s + i
}
println(s)`, "10\n")
	wantOut(t, `for x in [3, 1, 2] {
    println(x)
}`, "3\n1\n2\n")
	wantOut(t, `for c in "ab" {
    println(c)
}`, "a\nb\n")
	wantOut(t, `var hits = 0
for i in 0..10 {
    if i % 2 == 0 {
        continue
    }
    if i > 5 {
        break
    }
    hits = hits + 1
}
println(hits)`, "3\n")
}

func Test函数与递归(t *testing.T) {
	wantOut(t, `fn add(a: int, b: int): int {
    return a + b
}
println(add(2, 3))`, "5\n")
	wantOut(t, `fn fib(n: int): int {
    if n < 2 {
        return n
    }
    return fib(n - 1) + fib(n - 2)
}
println(fib(10))`, "55\n")
	wantOut(t, `fn nothing() {
    return
}
println(1)`, "1\n")
}

func Test内建输出格式(t *testing.T) {
	wantOut(t, `println(1, "a", true, 2.5)`, "1 a true 2.5\n")
	wantOut(t, `print("no")
println("nl")`, "nonl\n")
	wantOut(t, `println(len(0..4))`, "4\n")
}

// —— 切片 2：struct / enum / match / Result / 指针 ——

func Test结构体字面量与字段(t *testing.T) {
	wantOut(t, `struct Point {
    x: int
    y: int
}
var p = Point{x: 1, y: 2}
println(p.x + p.y)`, "3\n")
	wantOut(t, `struct Point {
    x: int
    y: int
}
var p = Point{x: 1, y: 2}
p.x = 10
println(p)`, "Point{x: 10, y: 2}\n")
	wantErr(t, `struct P { a: int }
var p = P{b: 1}`, "字段")
	wantErr(t, `println(Nope{x: 1})`, "未知 struct")
}

func Test结构体值语义拷贝(t *testing.T) {
	wantOut(t, `struct Point {
    x: int
    y: int
}
var p = Point{x: 1, y: 2}
var q = p
q.x = 9
println(p.x)`, "1\n")
}

func Test结构体传参拷贝(t *testing.T) {
	wantOut(t, `struct Point {
    x: int
    y: int
}
fn bump(pt: Point): int {
    pt.x = pt.x + 100
    return pt.x
}
var p = Point{x: 1, y: 2}
println(bump(p))
println(p.x)`, "101\n1\n")
}

func Test指针共享可变(t *testing.T) {
	wantOut(t, `struct Point {
    x: int
    y: int
}
var p = Point{x: 1, y: 2}
let r = &p
r.x = 5
println(p.x)`, "5\n")
}

func Test结构体不可比较(t *testing.T) {
	wantErr(t, `struct P { a: int }
println(P{a: 1} == P{a: 1})`, "比较")
}

func Test枚举与match穷尽(t *testing.T) {
	wantOut(t, `enum Shape {
    Circle(float)
    Rect(float, float)
    Unit
}
fn area(s: Shape): float {
    return match s {
        Circle(r) => 3.0 * r * r
        Rect(w, h) => w * h
        Unit => 0.0
    }
}
println(area(Circle(2.0)))
println(area(Rect(3.0, 4.0)))
println(area(Unit))`, "12\n12\n0\n")
	wantErr(t, `enum Shape {
    Circle(float)
    Rect(float, float)
}
fn area(s: Shape): float {
    return match s {
        Circle(r) => r
    }
}
area(Rect(1.0, 2.0))`, "穷尽")
}

func TestOption(t *testing.T) {
	wantOut(t, `fn first(xs: [int]): Option[int] {
    if len(xs) == 0 {
        return None
    }
    return Some(xs[0])
}
println(first([7, 8]))
println(first([]))`, "Some(7)\nNone\n")
	wantOut(t, `let o = Some(3)
println(match o {
    Some(v) => v * 2
    None => 0
})`, "6\n")
}

func TestResult与问号传播(t *testing.T) {
	wantOut(t, `fn parse(s: string): Result[int, error] {
    if s == "bad" {
        return Err(err("不是数字"))
    }
    return Ok(int(s))
}
fn run(s: string): Result[int, error] {
    let n = parse(s)?
    return Ok(n * 2)
}
println(run("21"))
println(run("bad"))`, "Ok(42)\nErr(error{msg: \"不是数字\"})\n")
	wantOut(t, `fn parse(s: string): Result[int, error] {
    return Err(err("bad"))
}
fn run(s: string): Result[int, error] {
    let n = parse(s)?
    return Ok(n)
}
match run("x") {
    Ok(v) => println(v)
    Err(e) => println("捕获:", e.msg)
}`, "捕获: bad\n")
	wantErr(t, `fn bad(): Result[int, error] {
    return Err(err("炸了"))
}
let n = bad()?
println(n)`, "函数之外")
}

func TestMatch字面量与绑定模式(t *testing.T) {
	wantOut(t, `let x = 2
println(match x {
    1 => "一"
    2 => "二"
    other => "其他"
})`, "二\n")
	wantOut(t, `let x = 99
println(match x {
    1 => "一"
    _ => "兜底"
})`, "兜底\n")
}

func Test构造器重名(t *testing.T) {
	wantErr(t, `enum Bad {
    Some(int)
}`, "重名")
}

func Test嵌套模式(t *testing.T) {
	wantOut(t, `let o = Some(Some(5))
println(match o {
    Some(Some(v)) => v
    Some(None) => 0
    None => -1
})`, "5\n")
}

// —— 切片 3：闭包 / 方法 / 泛型擦除 / 引用语义 ——

func Test匿名函数与闭包捕获(t *testing.T) {
	wantOut(t, `let add = fn(a: int, b: int): int {
    return a + b
}
println(add(2, 3))`, "5\n")
	wantOut(t, `fn makeAdder(n: int) {
    return fn(x: int): int {
        return x + n
    }
}
println(makeAdder(10)(5))`, "15\n")
}

func Test函数作参数(t *testing.T) {
	wantOut(t, `fn apply(f, x: int): int {
    return f(x)
}
let double = fn(n: int): int {
    return n * 2
}
println(apply(double, 21))`, "42\n")
}

func Test方法调用(t *testing.T) {
	wantOut(t, `struct Rect {
    w: float
    h: float
}
fn (r Rect).area(): float {
    return r.w * r.h
}
let r = Rect{w: 3.0, h: 4.0}
println(r.area())`, "12\n")
}

func Test泛型擦除(t *testing.T) {
	wantOut(t, `fn first[T](xs: [T]): Option[T] {
    if len(xs) == 0 {
        return None
    }
    return Some(xs[0])
}
println(first([1, 2]))
println(first(["a", "b"]))
println(first([]))`, "Some(1)\nSome(\"a\")\nNone\n")
}

func TestPush引用语义(t *testing.T) {
	wantOut(t, `var a = [1, 2]
var b = a
push(a, 3)
println(len(b))
println(b[2])`, "3\n3\n")
}

func TestEnum相等比较(t *testing.T) {
	wantOut(t, `println(Some(1) == Some(1))
println(Some(1) == Some(2))
println(Some(1) == None)`, "true\nfalse\nfalse\n")
}

func Test数组不可比较(t *testing.T) {
	wantErr(t, "println([1] == [1])", "比较")
}

func TestAssert(t *testing.T) {
	wantOut(t, `assert(1 == 1)
println("过")`, "过\n")
	wantErr(t, `assert(1 == 2, "数学不对")`, "数学不对")
}

func Test字符串转义(t *testing.T) {
	wantOut(t, `println("引号:\" 反斜:\\ 换行:\n结束")`, "引号:\" 反斜:\\ 换行:\n结束\n")
}

// —— M4 前置：位运算（MD5 移植的语言缺口）——

func Test位运算(t *testing.T) {
	wantOut(t, "println(12 & 10)", "8\n")
	wantOut(t, "println(12 | 10)", "14\n")
	wantOut(t, "println(12 ^ 10)", "6\n")
	wantOut(t, "println(1 << 4)", "16\n")
	wantOut(t, "println(256 >> 4)", "16\n")
	// 优先级对齐 Go：<< >> & 与 * 同级，| ^ 与 + 同级
	wantOut(t, "println(1 | 2 & 3)", "3\n")
	wantOut(t, "println(2 + 3 | 8)", "13\n")
	// 32 位回绕惯用法：掩码后保持非负
	wantOut(t, "println((1 << 31) & 4294967295)", "2147483648\n")
	wantOut(t, "println(4294967295 ^ 4294967295)", "0\n")
}

func Test位运算类型错误(t *testing.T) {
	wantErr(t, "println(1.5 & 2)", "int")
	wantErr(t, "println(1 << 2.0)", "int")
	wantErr(t, "println(1 << 64)", "移位")
	wantErr(t, "println(1 << -1)", "移位")
}

func Test字节与字符内建(t *testing.T) {
	wantOut(t, `let bs = bytes("a你")
println(len(bs))
println(bs[0])
println(bs[1])`, "4\n97\n228\n")
	wantOut(t, `println(char(97) + char(20320))`, "a你\n")
	wantOut(t, `println(len(bytes("")) == 0)`, "true\n")
}

// —— M4 收口前置：json / http / extern ——

func TestJSON解析标量(t *testing.T) {
	wantOut(t, `println(jsonParse("42"))`, "42\n")
	wantOut(t, `println(jsonParse("2.5"))`, "2.5\n")
	wantOut(t, `println(jsonParse("\"hi\""))`, "hi\n")
	wantOut(t, `println(jsonParse("true"))`, "true\n")
	wantOut(t, `println(jsonParse("null") == None)`, "true\n")
	wantOut(t, `println(jsonParse("-7"))`, "-7\n")
}

func TestJSON解析容器(t *testing.T) {
	wantOut(t, `let a = jsonParse("[1, \"a\", null]")
println(len(a))
println(a[1])
println(a[2] == None)`, "3\na\ntrue\n")
	wantOut(t, `let m = jsonParse("{\"bvid\": \"BV1xx\", \"cid\": 9}")
println(m["bvid"])
println(m["cid"])`, "BV1xx\n9\n")
	wantOut(t, `let v = jsonParse("[{\"x\": [1, 2]}]")
println(v[0]["x"][1])`, "2\n")
}

func TestJSON解析错误(t *testing.T) {
	wantErr(t, `jsonParse("[1,")`, "JSON")
	wantErr(t, `jsonParse("nul")`, "JSON")
}

func TestJSON序列化(t *testing.T) {
	wantOut(t, `println(jsonStringify(jsonParse("[1, 2.5, \"a\"]")))`, "[1,2.5,\"a\"]\n")
	// 映射按键排序输出（两端确定性）
	wantOut(t, `println(jsonStringify(jsonParse("{\"b\": 1, \"a\": 2}")))`, "{\"a\":2,\"b\":1}\n")
	wantOut(t, `println(jsonStringify(None))`, "null\n")
	wantOut(t, `println(jsonStringify([1, [2, 3]]))`, "[1,[2,3]]\n")
	wantOut(t, `var m: map[string]string = {}
m["k"] = "v"
println(jsonStringify(m))`, "{\"k\":\"v\"}\n")
}

func TestHTTP结构(t *testing.T) {
	// 预载 http 值的 get 字段可调用；畸形 URL 走 Err 分支（离线可测，
	// 错误文案来自宿主，只断言前缀不锁全文）
	out, err := runSrc(t, `match http.get("not a url", {}) {
    Ok(r) => println("ok", r.status)
    Err(e) => println("err:", e.msg)
}`)
	if err != nil {
		t.Fatalf("运行出错: %v", err)
	}
	if !strings.HasPrefix(out, "err: ") {
		t.Fatalf("畸形 URL 应走 Err 分支，得到 %q", out)
	}
	// get 可作值传递（函数字段走通用调用路径）
	out, err = runSrc(t, `let g = http.get
match g("%zz", {}) {
    Ok(r) => println("ok")
    Err(e) => println("err")
}`)
	if err != nil {
		t.Fatalf("运行出错: %v", err)
	}
	if !strings.HasPrefix(out, "err") {
		t.Fatalf("函数值调用 http.get 应生效，得到 %q", out)
	}
}

func TestExtern块(t *testing.T) {
	// 声明可解析、不调用不报错
	wantOut(t, `extern "go" {
    fn myHost(x: int): int
}
println(1)`, "1\n")
	// 调用即报"仅转译可用"
	wantErr(t, `extern "go" {
    fn myHost(x: int): int
}
println(myHost(1))`, "extern")
}
