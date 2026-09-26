using Lang.Types;

/// <summary>
/// 类型运算真值（TYPE-ASSIGN-001、TYPE-ANY-002、TYPE-FN-004、TYPE-MEMBER-007、
/// CALL-ARG-002、GEN-SUBST-003、CTRL-FORIN-003）。期望按 specs 手算，对照 TS 证据。
/// </summary>
public class OpsTests
{
    [Fact]
    public void AssignabilitySpecRules()
    {
        var t = new TypeTables();
        // TYPE-ASSIGN-001：同一类型 / 目标 any / 源 Unknown
        Assert.True(Ops.Assignable(t, TypeTables.Int, TypeTables.Int));
        Assert.False(Ops.Assignable(t, TypeTables.Float, TypeTables.Int)); // 无隐式 int→float
        Assert.False(Ops.Assignable(t, TypeTables.Int, TypeTables.Float)); // 无隐式 float→int
        Assert.True(Ops.Assignable(t, TypeTables.Any, TypeTables.Int)); // any 是动态边界
        Assert.True(Ops.Assignable(t, TypeTables.Int, TypeTables.Any));
        Assert.True(Ops.Assignable(t, TypeTables.Int, TypeTables.Unknown)); // Unknown 保守放行
        // TYPE-ANY-002 分工面：二者都放行，但身份不同一（不得互换语义）
        Assert.False(Ops.Equal(t, TypeTables.Any, TypeTables.Unknown));

        var t2 = new TypeTables();
        // 容器协变：[int] 不可赋给 [float]
        var sInt = t2.Intern(new TypeData.Slice(TypeTables.Int));
        var sFloat = t2.Intern(new TypeData.Slice(TypeTables.Float));
        Assert.False(Ops.Assignable(t2, sFloat, sInt));
        // fn 结构兼容（TYPE-FN-004）：函数名不是类型身份（同型即兼容）
        var fInt = t2.Intern(new TypeData.Fn([TypeTables.Int], TypeTables.Unit));
        var fInt2 = t2.Intern(new TypeData.Fn([TypeTables.Int], TypeTables.Unit));
        Assert.True(Ops.Assignable(t2, fInt, fInt2));
        var fFloat = t2.Intern(new TypeData.Fn([TypeTables.Float], TypeTables.Unit));
        Assert.False(Ops.Assignable(t2, fInt, fFloat));
        Assert.False(Ops.Assignable(t2, fFloat, fInt));
        // 参数位含 unknown：双向放行（保守面；仅验证参数位确实参与判定）
        var fUnkParam = t2.Intern(new TypeData.Fn([TypeTables.Unknown], TypeTables.Unit));
        Assert.True(Ops.Assignable(t2, fInt, fUnkParam));
        Assert.True(Ops.Assignable(t2, fUnkParam, fInt));
    }

    [Fact]
    public void TypeStrMatchesSourceSpelling()
    {
        var t = new TypeTables();
        Assert.Equal("()", Ops.TypeStr(t, TypeTables.Unit));
        Assert.Equal("string", Ops.TypeStr(t, TypeTables.Str));
        var s = t.Intern(new TypeData.Slice(TypeTables.Str));
        Assert.Equal("[string]", Ops.TypeStr(t, s));
        var m = t.Intern(new TypeData.Map(TypeTables.Int, TypeTables.Str));
        Assert.Equal("map[int]string", Ops.TypeStr(t, m));
        var o = t.Intern(new TypeData.Option(TypeTables.Int));
        Assert.Equal("Option[int]", Ops.TypeStr(t, o));
        var r = t.Intern(new TypeData.Result(TypeTables.Int, TypeTables.Error));
        Assert.Equal("Result[int, error]", Ops.TypeStr(t, r));
        var c = t.Intern(new TypeData.Chan(TypeTables.Int));
        Assert.Equal("chan[int]", Ops.TypeStr(t, c));
        var rf = t.Intern(new TypeData.Ref(TypeTables.Int));
        Assert.Equal("&int", Ops.TypeStr(t, rf));
        var f = t.Intern(new TypeData.Fn([TypeTables.Int, TypeTables.Str], TypeTables.Bool));
        Assert.Equal("fn(int, string): bool", Ops.TypeStr(t, f));
        // unit 返回省略「: ()」（证据同款）
        var fu = t.Intern(new TypeData.Fn([TypeTables.Int], TypeTables.Unit));
        Assert.Equal("fn(int)", Ops.TypeStr(t, fu));
        var tp = t.Intern(new TypeData.TypeParam("T"));
        Assert.Equal("T", Ops.TypeStr(t, tp));
    }

    [Fact]
    public void MemberLookupAndErrorMsg()
    {
        var t = new TypeTables();
        var p = t.RegisterStruct(new StructDef
        {
            Name = "P",
            Fields = [new FieldDef { Name = "x", Ty = TypeTables.Int }],
        });
        var ps = t.Intern(new TypeData.Struct(p));
        Assert.Equal(new FieldLookup.Found(TypeTables.Int), Ops.LookupField(t, ps, "x"));
        Assert.Equal(new FieldLookup.Missing(), Ops.LookupField(t, ps, "z"));
        // error.msg → string；其他字段 Missing（消息特化由检查器做）
        Assert.Equal(new FieldLookup.Found(TypeTables.Str), Ops.LookupField(t, TypeTables.Error, "msg"));
        Assert.Equal(new FieldLookup.Missing(), Ops.LookupField(t, TypeTables.Error, "nope"));
        // 非 struct 具体类型 / 宽松类型
        Assert.Equal(new FieldLookup.NotStruct(), Ops.LookupField(t, TypeTables.Int, "x"));
        Assert.Equal(new FieldLookup.Lax(TypeTables.Unknown), Ops.LookupField(t, TypeTables.Any, "x"));
        // &T 自动解引用
        var rf = t.Intern(new TypeData.Ref(ps));
        Assert.Equal(new FieldLookup.Found(TypeTables.Int), Ops.LookupField(t, rf, "x"));
    }

    [Fact]
    public void CallCompatAndNotCallable()
    {
        var t = new TypeTables();
        var f = t.Intern(new TypeData.Fn([TypeTables.Int], TypeTables.Int));
        Assert.Equal(new CallCompat.Compatible(), Ops.CheckCall(t, f, [TypeTables.Int]));
        Assert.Equal(new CallCompat.Arity(1, 2), Ops.CheckCall(t, f, [TypeTables.Int, TypeTables.Int]));
        Assert.Equal(new CallCompat.ArgMismatch(0), Ops.CheckCall(t, f, [TypeTables.Float]));
        // 宽松 callee 放行（any/unknown 来自动态边界）
        Assert.Equal(new CallCompat.Compatible(), Ops.CheckCall(t, TypeTables.Any, []));
        Assert.Equal(new CallCompat.NotCallable(), Ops.CheckCall(t, TypeTables.Int, []));
    }

    [Fact]
    public void SubstitutionReplacesTypeParams()
    {
        var t = new TypeTables();
        var tp = t.Intern(new TypeData.TypeParam("T"));
        var f = t.Intern(new TypeData.Fn([tp], tp));
        var o = t.Intern(new TypeData.Option(tp));
        var r = t.Intern(new TypeData.Result(tp, TypeTables.Error));
        var g = Ops.Substitute(t, f, "T", TypeTables.Int);
        Assert.Equal(
            t.Data(g),
            new TypeData.Fn([TypeTables.Int], TypeTables.Int));
        var og = Ops.Substitute(t, o, "T", TypeTables.Str);
        Assert.Equal("Option[string]", Ops.TypeStr(t, og));
        var rg = Ops.Substitute(t, r, "T", TypeTables.Int);
        Assert.Equal("Result[int, error]", Ops.TypeStr(t, rg));
    }

    [Fact]
    public void IterationProtocolByType()
    {
        var t = new TypeTables();
        var s = t.Intern(new TypeData.Slice(TypeTables.Int));
        Assert.Equal(TypeTables.Int, Ops.IterableElem(t, s));
        var m = t.Intern(new TypeData.Map(TypeTables.Str, TypeTables.Int));
        Assert.Equal(TypeTables.Str, Ops.IterableElem(t, m)); // 映射迭代产出键
        Assert.Equal(TypeTables.Str, Ops.IterableElem(t, TypeTables.Str));
        Assert.Equal(TypeTables.Int, Ops.IterableElem(t, TypeTables.Range));
        Assert.Null(Ops.IterableElem(t, TypeTables.Int)); // E3010 面
        Assert.Equal(TypeTables.Unknown, Ops.IterableElem(t, TypeTables.Any));
        // &T 穿透后仍按目标类型判定
        var rf = t.Intern(new TypeData.Ref(s));
        Assert.Equal(TypeTables.Int, Ops.IterableElem(t, rf));
    }

    [Fact]
    public void ResultOptionAccessors()
    {
        var t = new TypeTables();
        var r = t.Intern(new TypeData.Result(TypeTables.Int, TypeTables.Error));
        Assert.Equal(TypeTables.Int, Ops.ResultOk(t, r));
        Assert.Equal(TypeTables.Error, Ops.ResultErr(t, r));
        var o = t.Intern(new TypeData.Option(TypeTables.Str));
        Assert.Equal(TypeTables.Str, Ops.OptionElem(t, o));
        Assert.Equal(TypeTables.Unknown, Ops.ResultOk(t, TypeTables.Int));
        // &Result 穿透后仍取侧位
        var rf = t.Intern(new TypeData.Ref(r));
        Assert.Equal(TypeTables.Int, Ops.ResultOk(t, Ops.Deref(t, rf)));
    }
}
