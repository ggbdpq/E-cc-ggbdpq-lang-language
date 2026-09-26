using Lang.Conformance;
using Xunit;

namespace Lang.Conformance.Tests;

/// <summary>
/// manifest 解析（YAML 受限子集 + 语义校验）专项。期望值手抄 conformance/README.md
/// 「manifest」节与真实语料形态，禁拿实现当期望。
/// </summary>
public class ManifestTests
{
    private const string ReadmeRun =
        "name: result-propagation-basic\ncategory: result\nmode: run\nexpect:\n  exitCode: 0\n  stdout: |\n    Ok(4)\n    Err(error{msg: \"div by zero\"})\n  stderr: \"\"\n";

    [Fact]
    public void Parses_readme_run_shape()
    {
        var c = ManifestParser.Parse(ReadmeRun);

        Assert.Equal("result-propagation-basic", c.Name);
        Assert.Equal("result", c.Category);
        Assert.Equal(CaseMode.Run, c.Mode);
        Assert.Equal(0, c.Expect.ExitCode);
        Assert.Equal("Ok(4)\nErr(error{msg: \"div by zero\"})\n", c.Expect.Stdout);
        Assert.Equal("", c.Expect.Stderr);
        Assert.Empty(c.Expect.Diagnostics);
    }

    [Fact]
    public void Mode_defaults_to_run()
    {
        var c = ManifestParser.Parse("name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    hi\n  stderr: \"\"\n");

        Assert.Equal(CaseMode.Run, c.Mode);
    }

    [Fact]
    public void Check_diag_rule_id_falls_back_to_mapping()
    {
        var c = ManifestParser.Parse(
            "name: amp\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E3012\n      severity: error\n      line: 2\n");

        Assert.Equal(CaseMode.Check, c.Mode);
        var diag = Assert.Single(c.Expect.Diagnostics);
        Assert.Equal("E3012", diag.Code);
        Assert.Equal("VALUE-REF-003", diag.RuleId);
        Assert.Equal(2u, diag.Line);
    }

    [Fact]
    public void Check_diag_rule_id_must_match_code()
    {
        const string ok = "name: amp\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E3012\n      ruleId: VALUE-REF-003\n      severity: error\n      line: 2\n";
        Assert.Equal("VALUE-REF-003", Assert.Single(ManifestParser.Parse(ok).Expect.Diagnostics).RuleId);

        const string bad = "name: amp\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E3012\n      ruleId: NAME-UNDEF-004\n      severity: error\n      line: 2\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(bad));
    }

    [Fact]
    public void Line_zero_allowed_for_positionless_diagnostics()
    {
        // 套件真值：diagnostics/result-second-param-not-error 以 line: 0 声明无位置诊断。
        var c = ManifestParser.Parse(
            "name: r\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E3009\n      severity: error\n      line: 0\n");

        Assert.Equal(0u, Assert.Single(c.Expect.Diagnostics).Line);
    }

    [Fact]
    public void Rejects_superset_and_garbage()
    {
        const string unknownKey = "name: t\ncategory: types\noops: 1\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(unknownKey));

        const string deep = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n  nested:\n    a: 1\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(deep));

        const string badCode = "name: t\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E9999\n      severity: error\n      line: 1\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(badCode));

        const string badSeverity = "name: t\ncategory: diagnostics\nmode: check\nexpect:\n  diagnostics:\n    - code: E2002\n      severity: fatal\n      line: 1\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(badSeverity));

        const string badMode = "name: t\ncategory: types\nmode: fmt\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(badMode));

        const string tabIndent = "name: t\ncategory: types\nexpect:\n\texitCode: 0\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(tabIndent));

        const string flowMapping = "name: t\ncategory: types\nexpect: {}\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(flowMapping));

        const string missingName = "category: types\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(missingName));
    }

    [Fact]
    public void Rejects_bad_run_semantics()
    {
        const string nonzero = "name: t\ncategory: types\nexpect:\n  exitCode: 2\n  stdout: |\n    x\n  stderr: \"\"\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(nonzero));

        const string noisy = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"boom\"\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(noisy));

        const string noBlock = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: \"x\"\n  stderr: \"\"\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(noBlock));

        const string blankLine = "name: t\ncategory: types\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n\n    y\n  stderr: \"\"\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(blankLine));
    }

    [Fact]
    public void Rejects_bad_check_semantics()
    {
        const string noDiag = "name: t\ncategory: diagnostics\nmode: check\nexpect:\n  exitCode: 0\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(noDiag));

        const string mixed = "name: t\ncategory: diagnostics\nmode: check\nexpect:\n  stdout: |\n    x\n  diagnostics:\n    - code: E2002\n      severity: error\n      line: 1\n";
        Assert.Throws<ManifestException>(() => ManifestParser.Parse(mixed));
    }

    [Fact]
    public void Rule_ids_keep_order()
    {
        var c = ManifestParser.Parse(
            "name: two-rules\ncategory: match\nruleIds:\n  - MATCH-EXHAUSTIVE-002\n  - MATCH-KIND-001\nexpect:\n  exitCode: 0\n  stdout: |\n    x\n  stderr: \"\"\n");

        Assert.Equal(["MATCH-EXHAUSTIVE-002", "MATCH-KIND-001"], c.RuleIds);
    }

    [Fact]
    public void Block_scalar_is_raw_astral_and_structured_content()
    {
        // 语料形态（io/string-escapes-and-print）：块标量内容原样保留——含 Tab、引号冒号、
        // astral 码点；# 是内容不是注释。
        var c = ManifestParser.Parse(
            "name: astral-out\ncategory: io\nexpect:\n  exitCode: 0\n  stdout: |\n    a\tb\n    quote: \"x\"\n    🦀𝕏\n    # not comment\n  stderr: \"\"\n");

        Assert.Equal("a\tb\nquote: \"x\"\n🦀𝕏\n# not comment\n", c.Expect.Stdout);
    }
}
