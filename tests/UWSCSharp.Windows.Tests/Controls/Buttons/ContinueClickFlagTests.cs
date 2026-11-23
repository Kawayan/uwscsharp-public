using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ContinueClickFlagTests
{
    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task MissingTargetAndContinueReturnLogicalFalse()
    {
        var automation = new WindowsAutomation(_ => { },
            swappedMouseButtons: false);
        List<string> output = [];
        await new Interpreter(windows: automation).RunAsync("""
            PRINT VARTYPE(CLKITEM(-1, "target"))
            PRINT VARTYPE(CLKITEM(-1, "target", CLK_ACC))
            PRINT VARTYPE(CLKITEM(-1, "target", CLK_UIA))
            PRINT VARTYPE(CLKITEM(-1, "target", CLK_CONTINUE))
            PRINT CLKITEM(-1, "target", CLK_CONTINUE) + 0
            """, output.Add);
        Assert.Equal(["11", "11", "11", "11", "0"], output);
    }

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    [Fact]
    public async Task AccContinuesFromLastPositionAndFreshSearchResetsIt()
    {
        int first = 0;
        int second = 0;
        var provider = new AccessibleTestProvider(10, 0, "", "",
            new(43, 0, "Same", "")
            {
                TakingFocus = _ => { },
                PerformingAction = () => first++
            },
            new(43, 0, "&Same", "")
            {
                TakingFocus = _ => { },
                PerformingAction = () => second++
            });
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        var automation = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"));
        await using (IWindowsAutomationSession session = automation.CreateSession())
        {
            Value id = await session.InvokeAsync("HNDTOID",
                [Value.Numeric((long)fixture.Root)], default);
            foreach ((int Number, bool Continue, int Expected) step in new[]
            {
                (2, false, 1), (2, true, 0), (1, true, 1),
                (1, false, 1), (2, true, 1)
            })
            {
                int flags = 32768 | 512 | (step.Continue ? 33554432 : 0);
                Assert.Equal(Value.Numeric(step.Expected), await session.InvokeAsync("CLKITEM",
                    [id, Value.String("Same"), Value.Numeric(flags),
                        Value.Numeric(1), Value.Numeric(step.Number)], default));
            }
        }

        Assert.Equal(1, first);
        Assert.Equal(3, second);
        await using IWindowsAutomationSession fresh = automation.CreateSession();
        Value freshId = await fresh.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(1), await fresh.InvokeAsync("CLKITEM",
            [freshId, Value.String("Same"), Value.Numeric(33587200 | 512),
                Value.Numeric(1), Value.Numeric(2)], default));
        Assert.Equal(4, second);
    }

    [Theory]
    [InlineData(33554432, 0)]
    [InlineData(570425344, 0)]
    [InlineData(1107296256, 0)]
    [InlineData(33587200, 1)]
    [InlineData(33554433, 1)]
    [InlineData(570425345, 1)]
    [InlineData(1107296257, 1)]
    [InlineData(33587201, 1)]
    public async Task ContinueRetainsExplicitRouteWithoutAddingDefaultTypes(
        int flags, int expected)
    {
        int clicks = 0;
        var clicked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("BUTTON", "target", 0, 10);
            host.Clicked = (_, _) =>
            {
                Interlocked.Increment(ref clicks);
                clicked.TrySetResult();
            };
        }, initializeOle: true))
        {
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"))
                .CreateSession();
            Value id = await session.InvokeAsync("HNDTOID",
                [Value.Numeric((long)fixture.Root)], default);
            Value result = await session.InvokeAsync("CLKITEM",
                [id, Value.String("target"), Value.Numeric(flags | 512)],
                default);
            Assert.Equal(expected, result.Number);
            Assert.True(result.IsLogical);
            if (expected != 0)
            {
                await clicked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.Equal(expected, Volatile.Read(ref clicks));
    }
}
