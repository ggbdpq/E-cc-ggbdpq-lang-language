"""类型检查诊断码段（E3xxx 类型系统 / E4xxx 模式匹配）。稳定协议，只追加不能改义。"""

E3001 = "E3001"  # 类型不匹配（赋值/参数/返回/字段/条件/运算）
E3002 = "E3002"  # int 与 float 混用（无隐式转换）
E3003 = "E3003"  # 调用参数个数不匹配 / 构造器须直接调用
E3004 = "E3004"  # 字段/方法不存在
E3005 = "E3005"  # ? 位置非法或所在函数未注解为 Result
E3006 = "E3006"  # Result 函数 return 须为 Ok(...)/Err(...)；Ok/Err 仅可用于该处
E3007 = "E3007"  # 参数缺少类型注解
E3008 = "E3008"  # ? 的操作数须为 Result
E3009 = "E3009"  # Result 第二类型参数须为 error
E3010 = "E3010"  # 该类型不可迭代
E3011 = "E3011"  # 一元/逻辑运算不适用于该类型
E3012 = "E3012"  # & 只能作用于变量
E3013 = "E3013"  # 索引/切片端点须为 int
E3014 = "E3014"  # 映射键须为 int/string/bool

E4001 = "E4001"  # match 不穷尽
E4002 = "E4002"  # enum/代数类型 match 混入字面量臂
E4003 = "E4003"  # Result 与 Option（或不同 enum）的模式混用

_ASSIGN_OP_NAMES = {
    "+=": "+=", "-=": "-=", "*=": "*=", "/=": "/=", "=": "=",
}


def op_name(op: str) -> str:
    """复合赋值运算符的诊断名（op 为记号文本）。"""
    return _ASSIGN_OP_NAMES.get(op, "?")


def join_names(names: list[str]) -> str:
    return ", ".join(names)
