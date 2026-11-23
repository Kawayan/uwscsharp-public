using System.Text.Json;
using UWSCSharp.Mcp;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class McpAuthoringTests
{
    [Theory]
    [InlineData("作成支援")]
    [InlineData("入力条件")]
    [InlineData("終了コード")]
    public void AuthoringSearchReturnsInputAndValidationGuidance(string query)
    {
        var tools = new AuthoringTools();
        using var search = JsonDocument.Parse(tools.SearchReference(query));
        Assert.Contains(search.RootElement.GetProperty("results")
            .EnumerateArray(), result => result.GetProperty("name")
                .GetString() == "authoring");

        using var reference = JsonDocument.Parse(tools.GetReference("authoring"));
        JsonElement root = reference.RootElement;
        Assert.True(root.GetProperty("found").GetBoolean());
        Assert.Equal("docs/user-help/content/authoring.html",
            root.GetProperty("evidence").GetString());
        string? document = root.GetProperty("document").GetString();
        Assert.Contains("非ゼロ", document);
        Assert.Contains("FGET", document);
        Assert.Contains("CALL", document);
    }

    [Fact]
    public void ArraySearchFindsBasicDeclarationSyntax()
    {
        var tools = new AuthoringTools();
        using var reference = JsonDocument.Parse(
            tools.GetReference("data-types"));
        Assert.True(reference.RootElement.GetProperty("found").GetBoolean());
        string? document = reference.RootElement
            .GetProperty("document").GetString();
        Assert.Contains("DIM values[1] = 10, 20", document);
        Assert.Contains("上限添字", document);

        using var search = JsonDocument.Parse(
            tools.SearchReference("配列", 20));
        Assert.Contains(search.RootElement.GetProperty("results")
            .EnumerateArray(), result => result.GetProperty("name")
                .GetString() == "data-types");
    }

    [Fact]
    public void CallSearchFindsDocumentContainingCallSyntax()
    {
        var tools = new AuthoringTools();
        using var search = JsonDocument.Parse(
            tools.SearchReference("CALL", 20));
        JsonElement[] results =
        [
            .. search.RootElement.GetProperty("results")
                .EnumerateArray()
        ];
        Assert.Contains(results, result => result.GetProperty("name")
            .GetString() == "control-flow");
        Assert.DoesNotContain(results, result => result.GetProperty("name")
            .GetString() == "routines");

        using var reference = JsonDocument.Parse(
            tools.GetReference("control-flow"));
        Assert.Contains("CALL child.uws(", reference.RootElement
            .GetProperty("document").GetString());
    }

    [Fact]
    public void CapabilitiesMentionOnlyCurrentExecutionHosts()
    {
        var tools = new AuthoringTools();
        using var response = JsonDocument.Parse(
            tools.GetCapabilities("APP-CLI"));
        string? availability = response.RootElement
            .GetProperty("hostAvailability").GetString();
        Assert.Contains("CLI/WPF", availability);
        Assert.DoesNotContain("限定プレビュー", availability);
    }

    [Fact]
    public void HonorsAlreadyCancelledRequest()
    {
        var tools = new AuthoringTools();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            tools.ValidateScript("PRINT 1", cancellation.Token));
    }

    [Fact]
    public void ReturnsLineWithoutExecutingOrReadingCallTarget()
    {
        var tools = new AuthoringTools();
        using var response = JsonDocument.Parse(
            tools.ValidateScript("PRINT 1\nCALL missing_dependency",
                CancellationToken.None));
        Assert.False(response.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(2, response.RootElement.GetProperty("line").GetInt32());
    }
}
