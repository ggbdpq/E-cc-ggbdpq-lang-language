namespace Lang.Ir;

/// <summary>
/// IR 内部身份 newtype（arena 索引）。Lang.Core 冻结集之外、IR 图内部寻址所需的 id；
/// 形状与 Lang.Core 的 id 家族同构（uint newtype）。
/// </summary>
/// <param name="Value">基本块编号（函数内；块 0 为入口块）。</param>
public readonly record struct BlockId(uint Value);

/// <param name="Value">全局变量编号（程序级；按声明序）。</param>
public readonly record struct GlobalId(uint Value);

/// <param name="Value">struct 声明编号（程序级）。</param>
public readonly record struct StructId(uint Value);

/// <param name="Value">enum 声明编号（程序级；case 以声明序 int 索引）。</param>
public readonly record struct EnumId(uint Value);
