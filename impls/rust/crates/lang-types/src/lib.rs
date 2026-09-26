//! lang-types —— 语义类型格与类型运算（接口契约见 docs/lang-rs/interface-contract.md）。
//!
//! 分层：
//! - [`model`]：[`TypeData`] 全量类别（TYPE-KINDS-001）+ [`TypeTables`] arena intern
//!   + 名义 struct/enum def 表 + 预载面（error/Option/Result，id 与 A4 冻结对齐）；
//! - [`ops`]：结构同一 / 可赋值性（TYPE-ASSIGN-001，Any 与 Unknown 分工见 TYPE-ANY-002）
//!   / 诊断名 / 成员查找（TYPE-MEMBER-007）/ 调用兼容（CALL-ARG-002）/ 泛型替换
//!   （GEN-DECL-001 擦除）/ 迭代协议（CTRL-FORIN-003）。
//!
//! 本 crate 不依赖 AST 与符号表：名义类型身份是 def id，由 lang-semantic 在解析期注册。
#![forbid(unsafe_code)]

pub mod model;
pub mod ops;

pub use lang_core::TypeId;
pub use model::{
    ERROR_DEF, ERROR_MSG_FIELD, EnumCaseDef, EnumDef, EnumDefId, FieldDef, OPTION_DEF, RESULT_DEF,
    StructDef, StructDefId, TypeData, TypeTables,
};
pub use ops::{
    CallCompat, FieldLookup, assignable, check_call, deref, equal, is_concrete, is_lax, is_numeric,
    iterable_elem, lookup_field, option_elem, result_err, result_ok, substitute, type_str,
};

#[cfg(test)]
mod tests {
    use super::*;
    use model::ERROR_DEF;

    #[test]
    fn prelude_ids_match_a4_freeze() {
        let t = TypeTables::new();
        // A4 冻结：error=StructDefId(0)、Option=EnumDefId(0)、Result=EnumDefId(1)
        assert_eq!(ERROR_DEF, StructDefId(0));
        assert_eq!(t.struct_def(ERROR_DEF).name, "error");
        assert_eq!(OPTION_DEF, EnumDefId(0));
        assert_eq!(RESULT_DEF, EnumDefId(1));
        // error.msg: string（RESULT-PRELUDE-001）
        let msg = &t.struct_def(ERROR_DEF).fields[0];
        assert_eq!(msg.name, "msg");
        assert_eq!(msg.ty, TypeTables::STR);
        // Some/None/Ok/Err 顺序（A4 case 序号契约）
        assert_eq!(t.enum_def(OPTION_DEF).cases[0].name, "Some");
        assert_eq!(t.enum_def(OPTION_DEF).cases[1].name, "None");
        assert_eq!(t.enum_def(RESULT_DEF).cases[0].name, "Ok");
        assert_eq!(t.enum_def(RESULT_DEF).cases[1].name, "Err");
    }

    #[test]
    fn intern_dedupes_structural_types() {
        let mut t = TypeTables::new();
        let a = t.intern(TypeData::Slice(TypeTables::INT));
        let b = t.intern(TypeData::Slice(TypeTables::INT));
        assert_eq!(a, b);
        let c = t.intern(TypeData::Slice(TypeTables::FLOAT));
        assert_ne!(a, c);
        // 嵌套结构等价：同形 map 两次 intern 同一
        let inner = t.intern(TypeData::Slice(TypeTables::INT));
        let m1 = t.intern(TypeData::Map {
            key: TypeTables::INT,
            val: inner,
        });
        let inner2 = t.intern(TypeData::Slice(TypeTables::INT));
        let m2 = t.intern(TypeData::Map {
            key: TypeTables::INT,
            val: inner2,
        });
        assert_eq!(m1, m2);
    }

    #[test]
    fn equality_nominal_and_erasure() {
        let mut t = TypeTables::new();
        // TYPE-STRUCT-003：字段相同的两个 struct 不兼容
        let p = t.register_struct(StructDef {
            name: "P".into(),
            fields: vec![FieldDef {
                name: "x".into(),
                ty: TypeTables::INT,
            }],
        });
        let q = t.register_struct(StructDef {
            name: "Q".into(),
            fields: vec![FieldDef {
                name: "x".into(),
                ty: TypeTables::INT,
            }],
        });
        let ps = t.intern(TypeData::Struct(p));
        let qs = t.intern(TypeData::Struct(q));
        assert!(equal(&t, ps, ps));
        assert!(!equal(&t, ps, qs));
        // GEN-DECL-001 擦除：所有类型参数互为同一
        let tp1 = t.intern(TypeData::TypeParam { name: "T".into() });
        let tp2 = t.intern(TypeData::TypeParam { name: "U".into() });
        assert!(equal(&t, tp1, tp2));
        assert!(!equal(&t, tp1, TypeTables::INT));
        // Result[int, error] 结构同一（与声明点无关）
        let r1 = t.intern(TypeData::Result {
            ok: TypeTables::INT,
            err: TypeTables::ERROR,
        });
        let r2 = t.intern(TypeData::Result {
            ok: TypeTables::INT,
            err: TypeTables::ERROR,
        });
        assert!(equal(&t, r1, r2));
        // int 与 float 不同一（无隐式转换面）
        assert!(!equal(&t, TypeTables::INT, TypeTables::FLOAT));
    }

    #[test]
    fn assignability_spec_rules() {
        let t = TypeTables::new();
        // TYPE-ASSIGN-001：同一类型 / 目标 any / 源 Unknown
        assert!(assignable(&t, TypeTables::INT, TypeTables::INT));
        assert!(!assignable(&t, TypeTables::FLOAT, TypeTables::INT)); // 无隐式 int→float
        assert!(!assignable(&t, TypeTables::INT, TypeTables::FLOAT)); // 无隐式 float→int
        assert!(assignable(&t, TypeTables::ANY, TypeTables::INT)); // any 是动态边界
        assert!(assignable(&t, TypeTables::INT, TypeTables::ANY));
        assert!(assignable(&t, TypeTables::INT, TypeTables::UNKNOWN)); // Unknown 保守放行
        // TYPE-ANY-002 分工面：二者都放行，但身份不同一（不得互换语义）
        assert!(!equal(&t, TypeTables::ANY, TypeTables::UNKNOWN));

        let mut t2 = TypeTables::new();
        // 容器协变：[int] 不可赋给 [float]
        let s_int = t2.intern(TypeData::Slice(TypeTables::INT));
        let s_float = t2.intern(TypeData::Slice(TypeTables::FLOAT));
        assert!(!assignable(&t2, s_float, s_int));
        // fn 结构兼容（TYPE-FN-004）：本语言具体类型间无子序，参数逆变退化为「一致」；
        // 函数名不是类型身份（同型即兼容）
        let f_int = t2.intern(TypeData::Fn {
            params: vec![TypeTables::INT],
            ret: TypeTables::UNIT,
        });
        let f_int2 = t2.intern(TypeData::Fn {
            params: vec![TypeTables::INT],
            ret: TypeTables::UNIT,
        });
        assert!(assignable(&t2, f_int, f_int2));
        let f_float = t2.intern(TypeData::Fn {
            params: vec![TypeTables::FLOAT],
            ret: TypeTables::UNIT,
        });
        assert!(!assignable(&t2, f_int, f_float));
        assert!(!assignable(&t2, f_float, f_int));
        // 参数位含 unknown：双向放行（保守面；仅验证参数位确实参与判定）
        let f_unk_param = t2.intern(TypeData::Fn {
            params: vec![TypeTables::UNKNOWN],
            ret: TypeTables::UNIT,
        });
        assert!(assignable(&t2, f_int, f_unk_param));
        assert!(assignable(&t2, f_unk_param, f_int));
    }

    #[test]
    fn type_str_matches_source_spelling() {
        let mut t = TypeTables::new();
        assert_eq!(type_str(&t, TypeTables::UNIT), "()");
        assert_eq!(type_str(&t, TypeTables::STR), "string");
        let s = t.intern(TypeData::Slice(TypeTables::STR));
        assert_eq!(type_str(&t, s), "[string]");
        let m = t.intern(TypeData::Map {
            key: TypeTables::INT,
            val: TypeTables::STR,
        });
        assert_eq!(type_str(&t, m), "map[int]string");
        let o = t.intern(TypeData::Option(TypeTables::INT));
        assert_eq!(type_str(&t, o), "Option[int]");
        let r = t.intern(TypeData::Result {
            ok: TypeTables::INT,
            err: TypeTables::ERROR,
        });
        assert_eq!(type_str(&t, r), "Result[int, error]");
        let c = t.intern(TypeData::Chan(TypeTables::INT));
        assert_eq!(type_str(&t, c), "chan[int]");
        let rf = t.intern(TypeData::Ref(TypeTables::INT));
        assert_eq!(type_str(&t, rf), "&int");
        let f = t.intern(TypeData::Fn {
            params: vec![TypeTables::INT, TypeTables::STR],
            ret: TypeTables::BOOL,
        });
        assert_eq!(type_str(&t, f), "fn(int, string): bool");
        let tp = t.intern(TypeData::TypeParam { name: "T".into() });
        assert_eq!(type_str(&t, tp), "T");
    }

    #[test]
    fn member_lookup_and_error_msg() {
        let mut t = TypeTables::new();
        let p = t.register_struct(StructDef {
            name: "P".into(),
            fields: vec![FieldDef {
                name: "x".into(),
                ty: TypeTables::INT,
            }],
        });
        let ps = t.intern(TypeData::Struct(p));
        assert_eq!(
            lookup_field(&t, ps, "x"),
            FieldLookup::Found(TypeTables::INT)
        );
        assert_eq!(lookup_field(&t, ps, "z"), FieldLookup::Missing);
        // error.msg → string；其他字段 Missing（消息特化由检查器做）
        assert_eq!(
            lookup_field(&t, TypeTables::ERROR, "msg"),
            FieldLookup::Found(TypeTables::STR)
        );
        assert_eq!(
            lookup_field(&t, TypeTables::ERROR, "nope"),
            FieldLookup::Missing
        );
        // 非 struct 具体类型 / 宽松类型
        assert_eq!(
            lookup_field(&t, TypeTables::INT, "x"),
            FieldLookup::NotStruct
        );
        assert_eq!(
            lookup_field(&t, TypeTables::ANY, "x"),
            FieldLookup::Lax(TypeTables::UNKNOWN)
        );
        // &T 自动解引用
        let rf = t.intern(TypeData::Ref(ps));
        assert_eq!(
            lookup_field(&t, rf, "x"),
            FieldLookup::Found(TypeTables::INT)
        );
    }

    #[test]
    fn call_compat_and_not_callable() {
        let mut t = TypeTables::new();
        let f = t.intern(TypeData::Fn {
            params: vec![TypeTables::INT],
            ret: TypeTables::INT,
        });
        assert_eq!(
            check_call(&t, f, &[TypeTables::INT]),
            CallCompat::Compatible
        );
        assert_eq!(
            check_call(&t, f, &[TypeTables::INT, TypeTables::INT]),
            CallCompat::Arity { want: 1, got: 2 }
        );
        assert_eq!(
            check_call(&t, f, &[TypeTables::FLOAT]),
            CallCompat::ArgMismatch { index: 0 }
        );
        // 宽松 callee 放行（any/unknown 来自动态边界）
        assert_eq!(check_call(&t, TypeTables::ANY, &[]), CallCompat::Compatible);
        assert_eq!(
            check_call(&t, TypeTables::INT, &[]),
            CallCompat::NotCallable
        );
    }

    #[test]
    fn substitution_replaces_type_params() {
        let mut t = TypeTables::new();
        let tp = t.intern(TypeData::TypeParam { name: "T".into() });
        let f = t.intern(TypeData::Fn {
            params: vec![tp],
            ret: tp,
        });
        let o = t.intern(TypeData::Option(tp));
        let r = t.intern(TypeData::Result {
            ok: tp,
            err: TypeTables::ERROR,
        });
        let g = substitute(&mut t, f, "T", TypeTables::INT);
        assert_eq!(
            t.data(g),
            &TypeData::Fn {
                params: vec![TypeTables::INT],
                ret: TypeTables::INT,
            }
        );
        let og = substitute(&mut t, o, "T", TypeTables::STR);
        assert_eq!(type_str(&t, og), "Option[string]");
        let rg = substitute(&mut t, r, "T", TypeTables::INT);
        assert_eq!(type_str(&t, rg), "Result[int, error]");
    }

    #[test]
    fn iteration_protocol_by_type() {
        let mut t = TypeTables::new();
        let s = t.intern(TypeData::Slice(TypeTables::INT));
        assert_eq!(iterable_elem(&t, s), Some(TypeTables::INT));
        let m = t.intern(TypeData::Map {
            key: TypeTables::STR,
            val: TypeTables::INT,
        });
        assert_eq!(iterable_elem(&t, m), Some(TypeTables::STR)); // 映射迭代产出键
        assert_eq!(iterable_elem(&t, TypeTables::STR), Some(TypeTables::STR));
        assert_eq!(iterable_elem(&t, TypeTables::RANGE), Some(TypeTables::INT));
        assert_eq!(iterable_elem(&t, TypeTables::INT), None); // E3010 面
        assert_eq!(
            iterable_elem(&t, TypeTables::ANY),
            Some(TypeTables::UNKNOWN)
        );
    }

    #[test]
    fn result_option_accessors() {
        let mut t = TypeTables::new();
        let r = t.intern(TypeData::Result {
            ok: TypeTables::INT,
            err: TypeTables::ERROR,
        });
        assert_eq!(result_ok(&t, r), TypeTables::INT);
        assert_eq!(result_err(&t, r), TypeTables::ERROR);
        let o = t.intern(TypeData::Option(TypeTables::STR));
        assert_eq!(option_elem(&t, o), TypeTables::STR);
        assert_eq!(result_ok(&t, TypeTables::INT), TypeTables::UNKNOWN);
        // &Result 穿透后仍取侧位
        let rf = t.intern(TypeData::Ref(r));
        assert_eq!(result_ok(&t, deref(&t, rf)), TypeTables::INT);
    }
}
