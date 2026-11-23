using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class IdToHndCompletionTests
{
    [Fact]
    public async Task RealRootAndChildRoundTripWithNumericConversions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint child = 0;
        string title = "IDTOHND-" + Guid.NewGuid();
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(
            window => child = window.Child("STATIC", "子", 0, 10),
            title: title);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value rootId = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        Value childId = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)child)], default);
        Assert.Equal(rootId, await session.InvokeAsync(BuiltinFunctionNames.GetId,
            [Value.String(title), Value.String("STATIC"), Value.Numeric(0)], default));
        Assert.NotEqual(rootId, childId);
        Assert.Equal(Value.Numeric((long)fixture.Root),
            await session.InvokeAsync(BuiltinFunctionNames.IdToHnd, [rootId], default));
        Assert.Equal(Value.Numeric((long)child),
            await session.InvokeAsync(BuiltinFunctionNames.IdToHnd, [childId], default));
        Assert.Equal(Value.Numeric((long)fixture.Root), await session.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [Value.String(rootId.Display())], default));
        Assert.Equal(Value.Numeric((long)fixture.Root), await session.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [Value.Numeric(rootId.Number + 0.9)], default));
    }

    [Fact]
    public async Task DestroyedFixtureResolvesToZeroAndSessionCanContinue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture first = await PumpedControlFixture.CreateAsync(_ => { });
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)first.Root)], default);
        await first.DisposeAsync();
        Assert.False(Native.IsWindow(first.Root));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [id], default));
        await using PumpedControlFixture second = await PumpedControlFixture.CreateAsync(_ => { });
        Value secondId = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)second.Root)], default);
        Assert.Equal(Value.Numeric((long)second.Root), await session.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [secondId], default));
    }

    [Fact]
    public async Task SessionIdNamespacesAreIndependent()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await using IWindowsAutomationSession first = new WindowsAutomation().CreateSession();
        await using IWindowsAutomationSession second = new WindowsAutomation().CreateSession();
        Value id = await first.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(0), await second.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [id], default));
        Assert.Equal(Value.Numeric((long)fixture.Root), await first.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [id], default));
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(99d)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public async Task UnregisteredIntegerIdsResolveToZero(double id)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [Value.Numeric(id)], default));
    }

    [Fact]
    public async Task CancellationAndDisposalPreserveTheirErrorKinds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.InvokeAsync(BuiltinFunctionNames.IdToHnd,
                [Value.Numeric(0)], cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.IdToHnd, [Value.Numeric(0)], default));
    }

    [Fact]
    public async Task EmptyIdDoesNotResolveToForegroundWindow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.IdToHnd, [Value.Empty], default));
    }
}
