using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 版情報はすべて注入し、実OS・画面・入力を取得しません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class OperatingSystemInformationTests
{
    [Theory]
    [InlineData(3, 51, false, 10)]
    [InlineData(4, 0, false, 11)]
    [InlineData(5, 0, false, 12)]
    [InlineData(5, 1, false, 13)]
    [InlineData(5, 2, true, 14)]
    [InlineData(6, 0, false, 20)]
    [InlineData(6, 0, true, 21)]
    [InlineData(6, 1, true, 21)]
    [InlineData(6, 1, false, 22)]
    [InlineData(6, 2, false, 23)]
    [InlineData(6, 2, true, 24)]
    [InlineData(6, 3, true, 24)]
    [InlineData(6, 3, false, 25)]
    [InlineData(10, 0, false, 30)]
    [InlineData(10, 0, true, 31)]
    public void KnownVersionFamiliesUseTheLegacyCodes(
        int major, int minor, bool server, int expected)
    {
        Assert.Equal(expected,
            new OperatingSystemInformation(major, minor, 0, server, true).Kind);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task BitnessQueryUsesOperatingSystemAndSurvivesFork(
        bool os64Bit, int expected)
    {
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false,
            operatingSystemInformation: () => new(10, 0, 26100, false, os64Bit));
        await using IWindowsAutomationSession session = automation.CreateSession();
        Assert.Equal(30, (await session.InvokeAsync("KINDOFOS", [], default)).Number);
        Assert.Equal(30, (await session.InvokeAsync("KINDOFOS",
            [Value.Numeric(0)], default)).Number);
        Assert.Equal(expected, (await session.InvokeAsync("KINDOFOS",
            [Value.Numeric(1)], default)).Number);
        Assert.Equal(expected, (await session.InvokeAsync("KINDOFOS",
            [Value.String("-1")], default)).Number);
        await using IWindowsAutomationSession fork = session.Fork();
        Assert.Equal(expected, (await fork.InvokeAsync("KINDOFOS",
            [Value.Numeric(1)], default)).Number);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PRINT VARTYPE(KINDOFOS())
            PRINT VARTYPE(KINDOFOS(FALSE))
            PRINT VARTYPE(KINDOFOS(TRUE))
            """, output.Add);
        Assert.Equal(["5", "5", "11"], output);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteFlagIsRejected(double value)
    {
        Assert.Throws<InvalidOperationException>(() => WindowsFunctions.Validate(
            "KINDOFOS", [new Value(ValueKind.Number, Number: value)]));
    }

    [Fact]
    public async Task InvalidTextAndCancellationDoNotCallTheProvider()
    {
        int calls = 0;
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false,
            operatingSystemInformation: () =>
            {
                calls++;
                return new(10, 0, 26100, false, true);
            });
        await using IWindowsAutomationSession session = automation.CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.InvokeAsync("KINDOFOS", [Value.String("invalid")], default).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.InvokeAsync("KINDOFOS", [], cancellation.Token).AsTask());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task UnknownKindIsReportedButBitnessCanStillBeRead()
    {
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false,
            operatingSystemInformation: () => new(99, 0, 0, false, true));
        await using IWindowsAutomationSession session = automation.CreateSession();
        Exception failure = await Assert.ThrowsAsync<NotSupportedException>(() =>
            session.InvokeAsync("KINDOFOS", [], default).AsTask());
        Assert.NotNull(session.GetScriptErrorMessage("KINDOFOS", failure));
        Assert.Equal(1, (await session.InvokeAsync("KINDOFOS",
            [Value.Numeric(1)], default)).Number);
    }
}
