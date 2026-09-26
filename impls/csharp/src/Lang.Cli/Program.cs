using System.Text;

// stdout/stderr 换行统一为 "\n"（Console 默认 Environment.NewLine 在 Windows 上是
// "\r\n"，与 Go/Unix 参考证据的 "\n" 逐字节对拍口径不符；仅进程入口设置——测试
// 直调 CliMain 的 Console 重定向不受影响）。编码取 StreamWriter 默认 UTF-8 无 BOM。
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n", AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError()) { NewLine = "\n", AutoFlush = true });

// lang-cs CLI 入口：分派在 CliApp.CliMain（返回进程退出码）。
return Lang.Cli.CliApp.CliMain(args);
