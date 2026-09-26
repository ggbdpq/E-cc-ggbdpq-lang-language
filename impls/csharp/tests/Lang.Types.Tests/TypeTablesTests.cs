using Lang.Types;

/// <summary>
/// 预载面 / intern / 结构同一与擦除（TYPE-KINDS-001、TYPE-STRUCT-003、GEN-DECL-001）。
/// 期望值来自 specs 与 A4 冻结登记，不拿实现当期望。含 astral 码点用例。
/// </summary>
public class TypeTablesTests
{
    [Fact]
    public void PreludeIdsMatchA4Freeze()
    {
        var t = new TypeTables();
        // A4 冻结：error=StructDefId(0)、Option=EnumDefId(0)、Result=EnumDefId(1)
        Assert.Equal(new StructDefId(0), TypeTables.ErrorDef);
        Assert.Equal("error", t.GetStruct(TypeTables.ErrorDef).Name);
        Assert.Equal(new EnumDefId(0), TypeTables.OptionDef);
        Assert.Equal(new EnumDefId(1), TypeTables.ResultDef);
        // error.msg: string（RESULT-PRELUDE-001）
        var msg = t.GetStruct(TypeTables.ErrorDef).Fields[0];
        Assert.Equal("msg", msg.Name);
        Assert.Equal(TypeTables.Str, msg.Ty);
        // Some/None/Ok/Err 顺序（A4 case 序号契约）
        Assert.Equal("Some", t.GetEnum(TypeTables.OptionDef).Cases[0].Name);
        Assert.Equal("None", t.GetEnum(TypeTables.OptionDef).Cases[1].Name);
        Assert.Equal("Ok", t.GetEnum(TypeTables.ResultDef).Cases[0].Name);
        Assert.Equal("Err", t.GetEnum(TypeTables.ResultDef).Cases[1].Name);
    }

    [Fact]
    public void InternDedupesStructuralTypes()
    {
        var t = new TypeTables();
        var a = t.Intern(new TypeData.Slice(TypeTables.Int));
        var b = t.Intern(new TypeData.Slice(TypeTables.Int));
        Assert.Equal(a, b);
        var c = t.Intern(new TypeData.Slice(TypeTables.Float));
        Assert.NotEqual(a, c);
        // 嵌套结构等价：同形 map 两次 intern 同一
        var inner = t.Intern(new TypeData.Slice(TypeTables.Int));
        var m1 = t.Intern(new TypeData.Map(TypeTables.Int, inner));
        var inner2 = t.Intern(new TypeData.Slice(TypeTables.Int));
        var m2 = t.Intern(new TypeData.Map(TypeTables.Int, inner2));
        Assert.Equal(m1, m2);
        // fn 参数表逐元素结构相等（record 默认按引用比集合，已定制）
        var f1 = t.Intern(new TypeData.Fn([TypeTables.Int, TypeTables.Str], TypeTables.Bool));
        var f2 = t.Intern(new TypeData.Fn([TypeTables.Int, TypeTables.Str], TypeTables.Bool));
        Assert.Equal(f1, f2);
    }

    [Fact]
    public void EqualityNominalAndErasure()
    {
        var t = new TypeTables();
        // TYPE-STRUCT-003：字段相同的两个 struct 不兼容
        var p = t.RegisterStruct(new StructDef
        {
            Name = "P",
            Fields = [new FieldDef { Name = "x", Ty = TypeTables.Int }],
        });
        var q = t.RegisterStruct(new StructDef
        {
            Name = "Q",
            Fields = [new FieldDef { Name = "x", Ty = TypeTables.Int }],
        });
        var ps = t.Intern(new TypeData.Struct(p));
        var qs = t.Intern(new TypeData.Struct(q));
        Assert.True(Ops.Equal(t, ps, ps));
        Assert.False(Ops.Equal(t, ps, qs));
        // GEN-DECL-001 擦除：所有类型参数互为同一
        var tp1 = t.Intern(new TypeData.TypeParam("T"));
        var tp2 = t.Intern(new TypeData.TypeParam("U"));
        Assert.True(Ops.Equal(t, tp1, tp2));
        Assert.False(Ops.Equal(t, tp1, TypeTables.Int));
        // Result[int, error] 结构同一（与声明点无关）
        var r1 = t.Intern(new TypeData.Result(TypeTables.Int, TypeTables.Error));
        var r2 = t.Intern(new TypeData.Result(TypeTables.Int, TypeTables.Error));
        Assert.True(Ops.Equal(t, r1, r2));
        // int 与 float 不同一（无隐式转换面）
        Assert.False(Ops.Equal(t, TypeTables.Int, TypeTables.Float));
    }

    [Fact]
    public void TypeStrWithAstralTypeParamName()
    {
        // astral 对抗轴：类型参数名含 astral 码点（🦀）时 intern 与 type_str 均按码点无损
        var t = new TypeTables();
        var tp = t.Intern(new TypeData.TypeParam("🦀T𝕏"));
        var tp2 = t.Intern(new TypeData.TypeParam("🦀T𝕏"));
        Assert.Equal(tp, tp2);
        Assert.Equal("🦀T𝕏", Ops.TypeStr(t, tp));
        // 擦除同一：astral 名与普通名互为同一 TypeParam
        Assert.True(Ops.Equal(t, tp, t.Intern(new TypeData.TypeParam("U"))));
    }
}
