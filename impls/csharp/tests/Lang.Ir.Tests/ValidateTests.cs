using Lang.Core;
using Lang.Ir;
using Xunit;

namespace Lang.Ir.Tests;

/// <summary>
/// Validator 结构校验 TDD：手构 IR 红→绿——悬垂引用/签名不一致/缺终接/坏栈效应全部被拒，
/// 合法程序与全指令覆盖程序通过。失败形态 = IrError（内部错误，非用户诊断）。
/// </summary>
public class ValidateTests
{
    private static IrError? Val(IrProgram prog) => Validator.Validate(prog);

    private static IrProgram Tiny() => TestSupport.Prog(
        fns: new[]
        {
            TestSupport.Fn("main", 0, 0, false,
                blocks: new[] { TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)) }),
        },
        main: new FunctionId(0));

    [Fact]
    public void Valid_tiny_program_passes()
    {
        Assert.Null(Val(Tiny()));
    }

    [Fact]
    public void Valid_full_feature_program_passes()
    {
        Assert.Null(Val(TestSupport.FullFeature()));
    }

    // —— 悬垂引用 ——

    [Fact]
    public void Dangling_local_get_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false, locals: new[] { TestSupport.L("a") },
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.LocalGet(new LocalId(9)) },
                            new Terminator.Return(true)),
                    }),
            },
        };
        var err = Val(p);
        Assert.NotNull(err);
        Assert.Contains("LocalId 9", err!.Message);
    }

    [Fact]
    public void Dangling_local_set_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false, locals: new[] { TestSupport.L("a") },
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[] { new Inst.ConstI64(1), new Inst.LocalSet(new LocalId(5)) },
                            new Terminator.Return(false)),
                    }),
            },
        };
        Assert.NotNull(Val(p));
    }

    [Fact]
    public void Dangling_function_call_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.Call(new FunctionId(7)) },
                            new Terminator.Return(false)),
                    }),
            },
        };
        var err = Val(p);
        Assert.NotNull(err);
        Assert.Contains("FunctionId 7", err!.Message);
    }

    [Fact]
    public void Dangling_spawn_target_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.Spawn(new FunctionId(3), 0) },
                            new Terminator.Return(false)),
                    }),
            },
        };
        Assert.NotNull(Val(p));
    }

    [Fact]
    public void Dangling_closure_target_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.ClosureNew(new FunctionId(9), 0) },
                            new Terminator.Return(false)),
                    }),
            },
        };
        Assert.NotNull(Val(p));
    }

    [Fact]
    public void Dangling_block_in_goto_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Goto(new BlockId(4))),
                    }),
            },
        };
        var err = Val(p);
        Assert.NotNull(err);
        Assert.Contains("BlockId 4", err!.Message);
    }

    [Fact]
    public void Dangling_block_in_branch_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.ConstBool(true) },
                            new Terminator.Branch(new BlockId(0), new BlockId(6))),
                    }),
            },
        };
        Assert.NotNull(Val(p));
    }

    [Fact]
    public void Dangling_block_in_iter_next_and_try_rejected()
    {
        var base_ = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("f", 0, 0, false, locals: new[] { TestSupport.L("a") },
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[]
                            {
                                new Inst.ConstI64(1), new Inst.IterNew(), new Inst.IterNext(new BlockId(5)),
                                new Inst.LocalSet(new LocalId(0)),
                                new Inst.LocalGet(new LocalId(0)), new Inst.Try(new LocalId(0), new BlockId(6)),
                                new Inst.Pop(),
                            },
                            new Terminator.Return(false)),
                    }),
            });
        // 悬垂 IterNext.done（错误先报在下标 2 的 IterNext）
        var err1 = Val(base_);
        Assert.NotNull(err1);
        Assert.Contains("BlockId 5", err1!.Message);
        // 修好 done 再看 Try.fail
        var p2 = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("f", 0, 0, false, locals: new[] { TestSupport.L("a") },
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[]
                            {
                                new Inst.ConstI64(1), new Inst.IterNew(), new Inst.IterNext(new BlockId(1)),
                                new Inst.LocalSet(new LocalId(0)),
                                new Inst.LocalGet(new LocalId(0)), new Inst.Try(new LocalId(0), new BlockId(6)),
                                new Inst.Pop(),
                            },
                            new Terminator.Return(false)),
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
            });
        var err2 = Val(p2);
        Assert.NotNull(err2);
        Assert.Contains("BlockId 6", err2!.Message);
    }

    [Fact]
    public void Dangling_global_rejected()
    {
        var get = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.GlobalGet(new GlobalId(0)) },
                            new Terminator.Return(true)),
                    }),
            },
        };
        var set = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.GlobalSet(new GlobalId(0)) },
                            new Terminator.Return(false)),
                    }),
            },
        };
        Assert.Contains("GlobalId 0", Val(get)!.Message);
        Assert.Contains("GlobalId 0", Val(set)!.Message);
    }

    [Fact]
    public void Dangling_struct_and_enum_case_rejected()
    {
        var badStruct = TestSupport.Prog(
            structs: Array.Empty<StructDecl>(),
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.StructNew(new StructId(0)), new Inst.Pop() },
                            new Terminator.Return(false)),
                    }),
            });
        Assert.Contains("StructId 0", Val(badStruct)!.Message);

        var badEnum = TestSupport.Prog(
            enums: new EnumDecl[]
            {
                new("E", Array.Empty<string>(), new[] { new EnumCase("A", Array.Empty<TypeId?>()) }),
            },
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.EnumNew(new EnumId(0), 3), new Inst.Pop() },
                            new Terminator.Return(false)),
                    }),
            });
        Assert.Contains("case 3", Val(badEnum)!.Message);

        var danglingEnum = TestSupport.Prog(
            enums: Array.Empty<EnumDecl>(),
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.EnumNew(new EnumId(0), 0), new Inst.Pop() },
                            new Terminator.Return(false)),
                    }),
            });
        Assert.Contains("EnumId 0", Val(danglingEnum)!.Message);
    }

    [Fact]
    public void SwitchEnum_arms_length_and_enum_ref_checked()
    {
        var oneEnum = new EnumDecl("E", Array.Empty<string>(), new[]
        {
            new EnumCase("A", Array.Empty<TypeId?>()),
            new EnumCase("B", Array.Empty<TypeId?>()),
        });
        IrProgram Build(Terminator term) => TestSupport.Prog(
            enums: new[] { oneEnum },
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false, locals: new[] { TestSupport.L("e") },
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[]
                            {
                                new Inst.EnumNew(new EnumId(0), 0), new Inst.LocalSet(new LocalId(0)),
                                new Inst.LocalGet(new LocalId(0)),
                            },
                            term),
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
            });
        // arms 1 个 != case 2 个 → 拒
        var wrongArms = Build(new Terminator.SwitchEnum(new EnumId(0), new[] { new BlockId(1) }, null));
        Assert.Contains("arms 1 个 != enum 0 case 2 个", Val(wrongArms)!.Message);
        // 越界 enum id → 拒
        var badEnum = Build(new Terminator.SwitchEnum(new EnumId(5), new[] { new BlockId(1), new BlockId(2) }, null));
        Assert.Contains("EnumId 5", Val(badEnum)!.Message);
        // arms 2 个 + 悬垂 default → 拒
        var badDefault = Build(new Terminator.SwitchEnum(new EnumId(0), new[] { new BlockId(1), new BlockId(2) }, new BlockId(9)));
        Assert.Contains("BlockId 9", Val(badDefault)!.Message);
    }

    // —— 块终接与栈效应 ——

    [Fact]
    public void Function_without_blocks_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[] { TestSupport.Fn("main", 0, 0, false) },
        };
        Assert.Contains("没有基本块", Val(p)!.Message);
    }

    [Fact]
    public void Stack_underflow_rejected()
    {
        IrFunction OneBlock(params Inst[] insts) => TestSupport.Fn("main", 0, 0, false,
            blocks: new[] { TestSupport.B(insts, new Terminator.Return(false)) });
        Assert.Contains("Pop 栈下溢", Val(Tiny() with { Functions = new[] { OneBlock(new Inst.Pop()) } })!.Message);
        Assert.Contains("Binary 栈下溢",
            Val(Tiny() with
            {
                Functions = new[] { OneBlock(new Inst.ConstI64(1), new Inst.Binary(Binop.Add), new Inst.Pop()) },
            })!.Message);
    }

    [Fact]
    public void Return_residual_stack_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.ConstI64(1) }, new Terminator.Return(false)),
                    }),
            },
        };
        Assert.Contains("终接处栈深 1 != 终接消费 0", Val(p)!.Message);
    }

    [Fact]
    public void Branch_needs_exactly_one_bool_rejected()
    {
        var p = Tiny() with
        {
            Functions = new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[] { new Inst.ConstBool(true), new Inst.ConstBool(true) },
                            new Terminator.Branch(new BlockId(0), new BlockId(0))),
                    }),
            },
        };
        Assert.Contains("终接处栈深 2 != 终接消费 1", Val(p)!.Message);
    }

    [Fact]
    public void Spawn_argc_mismatch_rejected()
    {
        var p = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.Spawn(new FunctionId(1), 0) },
                            new Terminator.Return(false)),
                    }),
                TestSupport.Fn("worker", 0, 1, false,
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
            });
        Assert.Contains("Spawn 实参 0 个 != 目标 fn#1 参数 1 个", Val(p)!.Message);
    }

    [Fact]
    public void Closure_captures_mismatch_rejected()
    {
        var p = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.ClosureNew(new FunctionId(1), 2), new Inst.Pop() },
                            new Terminator.Return(false)),
                    }),
                TestSupport.Fn("body", 1, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
            });
        Assert.Contains("ClosureNew 捕获 2 个 != 目标 fn#1 CaptureCount 1", Val(p)!.Message);
    }

    [Fact]
    public void Call_arg_underflow_rejected()
    {
        var p = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(new Inst[] { new Inst.Call(new FunctionId(1)), new Inst.Pop() },
                            new Terminator.Return(false)),
                    }),
                TestSupport.Fn("add", 0, 2, true,
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(true)),
                    }),
            });
        Assert.Contains("Call 栈下溢", Val(p)!.Message);
    }

    [Fact]
    public void Extern_with_body_rejected()
    {
        var p = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("hostRead", 0, 0, false, externHost: "go:hostRead",
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
            });
        Assert.Contains("extern 函数不得有函数体", Val(p)!.Message);
    }

    [Fact]
    public void Locals_shorter_than_captures_plus_params_rejected()
    {
        var p = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("f", 2, 1, false,
                    locals: new[] { TestSupport.L("only_one") },
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                    }),
            });
        Assert.Contains("局部表 1 个槽位 < 捕获 2 + 参数 1", Val(p)!.Message);
    }

    // —— 名单外放行与 astral（UTF-16 对抗轴）——

    [Fact]
    public void CallBuiltin_unknown_name_passes_validation()
    {
        // validator 不做内建名单硬校验（宿主可扩展内建；效果提取侧未知名视为纯）。
        var p = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[]
                            {
                                new Inst.ConstI64(1),
                                new Inst.CallBuiltin("宿主🦀扩展", 1, false),
                            },
                            new Terminator.Return(false)),
                    }),
            });
        Assert.Null(Val(p));
    }

    [Fact]
    public void Astral_identifiers_and_strings_validate()
    {
        var p = TestSupport.Prog(
            structs: new StructDecl[]
            {
                new("点🦀", Array.Empty<string>(), new FieldDecl[] { new("坐标𝕏", null) }),
            },
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false, locals: new[] { TestSupport.L("变量𝕏") },
                    blocks: new[]
                    {
                        TestSupport.B(
                            new Inst[]
                            {
                                new Inst.ConstStr("🦀𝕏𠀀"),
                                new Inst.LocalSet(new LocalId(0)),
                                new Inst.ConstI64(1),
                                new Inst.StructNew(new StructId(0)),
                                new Inst.LocalSet(new LocalId(0)),
                                new Inst.LocalGet(new LocalId(0)),
                                new Inst.FieldGet("坐标𝕏"),
                                new Inst.Pop(),
                            },
                            new Terminator.Return(false)),
                    }),
            });
        Assert.Null(Val(p));
    }

    [Fact]
    public void Error_message_locates_function_block_and_inst()
    {
        var p = TestSupport.Prog(
            fns: new[]
            {
                TestSupport.Fn("main", 0, 0, false,
                    blocks: new[]
                    {
                        TestSupport.B(Array.Empty<Inst>(), new Terminator.Return(false)),
                        TestSupport.B(new Inst[] { new Inst.ConstI64(1), new Inst.Pop(), new Inst.Pop() },
                            new Terminator.Return(false)),
                    }),
            });
        var err = Val(p);
        Assert.NotNull(err);
        Assert.Equal(0u, err!.Function.Value);
        Assert.Equal(1u, err.Block!.Value.Value);
        Assert.Equal(2, err.Inst!.Value);
        Assert.Contains("fn#0 block#1 inst#2", err.ToString());
    }
}
