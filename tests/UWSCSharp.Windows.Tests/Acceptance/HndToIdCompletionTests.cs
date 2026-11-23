using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class HndToIdCompletionTests
{
    [Theory]
    [InlineData(1.9, 1d)]
    [InlineData(-1.9, -1d)]
    [InlineData(4294967295d, 4294967295d)]
    [InlineData(4294967296d, 4294967296d)]
    [InlineData(4294967297d, 4294967297d)]
    [InlineData(-2147483649d, -2147483649d)]
    [InlineData(9007199254740991d, 9007199254740991d)]
    [InlineData(-9007199254740991d, -9007199254740991d)]
    public async Task RegistrationTruncatesAndPreservesNativeWidth(double input, double normalized)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(8, IntPtr.Size);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Value first = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric(input)], default);
        Assert.Equal(Value.Numeric(1), first);
        Assert.Equal(first, await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric(normalized)], default));
    }

    [Fact]
    public async Task ZeroDoesNotAllocateAnIdAndInvalidNonzeroStillRegisters()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        foreach (double value in new[] { 0d, -0.9, 0.9 })
        {
            Assert.Equal(Value.Numeric(-1), await session.InvokeAsync(
                BuiltinFunctionNames.HndToId, [Value.Numeric(value)], default));
        }

        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            BuiltinFunctionNames.HndToId, [Value.Numeric(-1)], default));
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync(
            BuiltinFunctionNames.HndToId, [Value.Numeric(1234)], default));
    }

    [Fact]
    public async Task LiveWindowAndHighBitsHaveDistinctRegistryEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        double root = (long)fixture.Root;
        Value id = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
            [Value.Numeric(root)], default);
        Assert.Equal(Value.Numeric(root), await session.InvokeAsync(
            BuiltinFunctionNames.IdToHnd, [id], default));
        foreach (double wide in new[] { root + 4294967296d, root - 4294967296d })
        {
            Value wideId = await session.InvokeAsync(BuiltinFunctionNames.HndToId,
                [Value.Numeric(wide)], default);
            Assert.NotEqual(id, wideId);
            Assert.Equal(Value.Numeric(Native.IsWindow((nint)(long)wide) ? wide : 0),
                await session.InvokeAsync(BuiltinFunctionNames.IdToHnd, [wideId], default));
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("negative-infinity")]
    [InlineData("overflow")]
    public async Task InvalidValuesDoNotConsumeRegistrySlots(string kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Value value = kind switch
        {
            "empty" => Value.Empty,
            "nan" => new Value(ValueKind.Number, double.NaN),
            "infinity" => new Value(ValueKind.Number, double.PositiveInfinity),
            "negative-infinity" => new Value(ValueKind.Number, double.NegativeInfinity),
            _ => Value.Numeric(9007199254740992d)
        };
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync(BuiltinFunctionNames.HndToId, [value], default));
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync(
            BuiltinFunctionNames.HndToId, [Value.Numeric(1234)], default));
    }
}
