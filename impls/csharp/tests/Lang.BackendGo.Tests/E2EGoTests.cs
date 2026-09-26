using System.Diagnostics;
using Lang.Ir;

namespace Lang.BackendGo.Tests;

/// <summary>
/// →Go 后端 E2E：手构 IR 夹具 → 生成 → go build → 执行 → 断言 stdout 逐字节一致。
/// 外部工具不可用时 skip 并打印原因（不红 CI）。设 LANG_CS_GO 可指定 go 路径、
/// LANG_CS_KEEP 保留临时目录排查。
/// </summary>
public class E2EGoTests
{
    private static bool Probe(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "version")
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

    private static string? FindGo()
    {
        var env = Environment.GetEnvironmentVariable("LANG_CS_GO");
        if (env is not null && Probe(env))
        {
            return env;
        }

        if (Probe("go"))
        {
            return "go";
        }

        foreach (var candidate in new[]
                 {
                     @"D:\DevTools\Languages\Backend\Golang\Go\bin\go.exe",
                     "/usr/local/go/bin/go",
                     "/usr/bin/go",
                 })
        {
            if (Probe(candidate))
            {
                return candidate;
            }
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
    public void E2E_go_build_run_matches_expected_stdout()
    {
        var go = FindGo();
        if (go is null)
        {
            // skip：未找到 go 工具（设 LANG_CS_GO 或加入 PATH 后重跑）
            return;
        }

        var prog = Fixture.Build();
        Assert.Null(Validator.Validate(prog));
        Assert.True(CodeGen.TryGenerate(prog, out var src, out var err), err?.Message);

        var tmp = Path.Combine(Path.GetTempPath(), $"lang_cs_go_e2e_{Environment.ProcessId}_full");
        if (Directory.Exists(tmp))
        {
            Directory.Delete(tmp, true);
        }

        Directory.CreateDirectory(tmp);
        try
        {
            var srcPath = Path.Combine(tmp, "main.go");
            File.WriteAllText(srcPath, src);

            var binPath = Path.Combine(tmp, "app.exe");
            var build = Run(go, ["build", "-o", binPath, srcPath], tmp);
            Assert.True(build.Code == 0,
                $"go build 失败:\nstdout: {build.Stdout}\nstderr: {build.Stderr}");

            var run = Run(binPath, [], tmp);
            Assert.True(run.Code == 0, $"产物执行失败:\nstderr: {run.Stderr}");
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
