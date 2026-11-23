using System.Text.Json;
using UWSCSharp.Mcp;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class McpCallAnalysisTests
{
    [Fact]
    public void DistinguishesSyntaxSuccessFromCallWarnings()
    {
        const string source = "PRINT NO_SUCH_FUNCTION()\nGETID()";
        var tools = new AuthoringTools();
        using var syntax = JsonDocument.Parse(tools.ValidateScript(
            source, CancellationToken.None));
        Assert.True(syntax.RootElement.GetProperty("valid").GetBoolean());

        using var result = JsonDocument.Parse(tools.AnalyzeScript(
            source, CancellationToken.None));
        JsonElement root = result.RootElement;
        Assert.True(root.GetProperty("syntaxValid").GetBoolean());
        Assert.True(root.GetProperty("callsAnalyzed").GetBoolean());
        Assert.False(root.TryGetProperty("valid", out _));
        JsonElement[] items = [.. root.GetProperty("diagnostics")
            .EnumerateArray()];
        Assert.Equal(2, items.Length);
        Assert.Equal("unknown-function",
            items[0].GetProperty("code").GetString());
        Assert.Equal(2, items[1].GetProperty("line").GetInt32());
        Assert.Equal("argument-shape",
            items[1].GetProperty("code").GetString());
        Assert.All(items, item => Assert.Equal("warning",
            item.GetProperty("severity").GetString()));
        Assert.Contains("実行成功を保証しません",
            root.GetProperty("scope").GetString());
    }

    [Fact]
    public void RechecksCorrectedSourceWithoutExecutingIt()
    {
        var tools = new AuthoringTools();
        using var result = JsonDocument.Parse(tools.AnalyzeScript(
            "GETID(\"not-searched\", , 0)", CancellationToken.None));
        Assert.Empty(result.RootElement.GetProperty("diagnostics")
            .EnumerateArray());
        Assert.True(result.RootElement.GetProperty("callsAnalyzed")
            .GetBoolean());
    }

    [Theory]
    [InlineData("IFB TRUE\nPRINT 1", 1)]
    [InlineData("PRINT 1\nCALL missing_dependency", 2)]
    public void ReportsSyntaxFailureWithoutClaimingCallCoverage(
        string source, int line)
    {
        var tools = new AuthoringTools();
        using var result = JsonDocument.Parse(tools.AnalyzeScript(
            source, CancellationToken.None));
        JsonElement root = result.RootElement;
        Assert.False(root.GetProperty("syntaxValid").GetBoolean());
        Assert.False(root.GetProperty("callsAnalyzed").GetBoolean());
        Assert.Equal(line, root.GetProperty("line").GetInt32());
        Assert.Empty(root.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("uncheckedScopes").ValueKind);
    }

    [Fact]
    public void ExposesUncheckedMembersAndDynamicCodeEvenWithNoWarnings()
    {
        var tools = new AuthoringTools();
        using var result = JsonDocument.Parse(tools.AnalyzeScript(
            "EVAL(\"Missing()\")\nPRINT obj.Value",
            CancellationToken.None));
        JsonElement root = result.RootElement;
        Assert.Empty(root.GetProperty("diagnostics").EnumerateArray());
        JsonElement uncheckedScopes = root.GetProperty("uncheckedScopes");
        Assert.Equal(1, uncheckedScopes.GetProperty("memberAccesses")
            .GetInt32());
        Assert.Equal(1, uncheckedScopes.GetProperty("dynamicCalls")
            .GetInt32());
    }

    [Fact]
    public void CapsWarningsAndReportsTheirTruncation()
    {
        var tools = new AuthoringTools();
        using var result = JsonDocument.Parse(tools.AnalyzeScript(
            string.Join('\n', Enumerable.Repeat("Missing()", 101)),
            CancellationToken.None));
        Assert.True(result.RootElement.GetProperty("truncated")
            .GetBoolean());
        Assert.Equal(100, result.RootElement.GetProperty("diagnostics")
            .GetArrayLength());
    }

    [Fact]
    public void EnforcesSourceLimitAndCancellation()
    {
        var tools = new AuthoringTools();
        Assert.Throws<ArgumentNullException>(() =>
            tools.AnalyzeScript(null!, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.AnalyzeScript(new string(' ', 64001),
                CancellationToken.None));
        using var accepted = JsonDocument.Parse(tools.AnalyzeScript(
            new string(' ', 64000), CancellationToken.None));
        Assert.True(accepted.RootElement.GetProperty("syntaxValid")
            .GetBoolean());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            tools.AnalyzeScript("PRINT 1", cancellation.Token));
    }
}
