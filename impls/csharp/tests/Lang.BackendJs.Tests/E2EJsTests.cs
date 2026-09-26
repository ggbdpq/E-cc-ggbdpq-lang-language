using System.Diagnostics;
using Lang.Ir;

namespace Lang.BackendJs.Tests;

/// <summary>
/// →JS 后端 E2E：手构 IR 夹具 → 生成 → node 执行 → 断言 stdout 逐字节一致
/// （与 →Go 同一夹具同一期望，跨后端差分信号）。外部工具不可用时 skip 并打印
/// 原因。设 LANG_CS_NODE 可指定 node 路径、LANG_CS_KEEP 保留临时目录排查。
/// </summary>
public class E2EJsTests
{
    private static bool Probe(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            p?.WaitForExit(30000);
            return p?.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? FindNode()
    {
        var env = Environment.GetEnvironmentVariable("LANG_CS_NODE");
        if (env is not null && Probe(env))
        {
            return env;
        }

        if (Probe("node"))
        {
            return "node";
        }

        return null;
    }

    private static (int Code, string Stdout, string Stderr) Run(string exe, IReadOnlyList<string> args, string cwd)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = cwd,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(180000), "进程超时");
        return (p.ExitCode, so, se);
    }

    [Fact]
    public void E2E_node_run_matches_expected_stdout()
    {
        var node = FindNode();
        if (node is null)
        {
            // skip：未找到 node（设 LANG_CS_NODE 或加入 PATH 后重跑）
            return;
        }

        var prog = Fixture.Build();
        Assert.Null(Validator.Validate(prog));
        Assert.True(CodeGen.TryGenerate(prog, out var src, out var err), err?.Message);

        var tmp = Path.Combine(Path.GetTempPath(), $"lang_cs_js_e2e_{Environment.ProcessId}_full");
        if (Directory.Exists(tmp))
        {
            Directory.Delete(tmp, true);
        }

        Directory.CreateDirectory(tmp);
        try
        {
            var srcPath = Path.Combine(tmp, "main.mjs");
            File.WriteAllText(srcPath, src);

            var run = Run(node, [srcPath], tmp);
            Assert.True(run.Code == 0, $"node 执行失败:\nstderr: {run.Stderr}");
            Assert.Equal(Fixture.ExpectedStdout, run.Stdout.ReplaceLineEndings("\n"));
        }
        finally
        {
            if (Environment.GetEnvironmentVariable("LANG_CS_KEEP") is null)
            {
                Directory.Delete(tmp, true);
            }
            else
            {
                Console.Error.WriteLine($"LANG_CS_KEEP 已设，保留 {tmp}");
            }
        }
    }
}
