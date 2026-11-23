using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleCheckTests
{
    private static Value N(long value) => Value.Numeric(value);

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    [Theory]
    [InlineData(44, 0, 0)]
    [InlineData(44, 0x10, 1)]
    [InlineData(44, 0x20, 2)]
    [InlineData(44, 0x30, 2)]
    [InlineData(44, 2, 0)]
    [InlineData(44, 8, 0)]
    [InlineData(45, 2, 1)]
    [InlineData(43, 8, 1)]
    [InlineData(44, 1, 0)]
    [InlineData(44, 0x11, 1)]
    [InlineData(41, 0x10, -1)]
    [InlineData(42, 0x200000, -1)]
    [InlineData(42, 0x400008, 1)]
    public async Task AccStateUsesRoleSpecificBitsAndMixedPrecedence(int role, int state, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Button", "snapshot", 0, 10);
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var provider = new AccessibleTestProvider(role, state, " &State... ", "");
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(1)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        foreach (int acc in new[] { 1, 2 })
        {
            Assert.Equal(N(expected), await session.InvokeAsync("CHKBTN",
                [id, Value.String("state"), N(1), N(acc)], default));
            Assert.Same(snapshot, session.AllItems);
        }
    }

    [Theory]
    [InlineData(" &Run... ", "run", 1)]
    [InlineData("ＡＢＣ...", "abc", 1)]
    [InlineData("ガ...", "ｶﾞ", 1)]
    [InlineData("One\uf8f0Two...", "One Two", 1)]
    [InlineData(" &Run... ", "Run...", -1)]
    [InlineData("&B", "B", -1)]
    [InlineData("&B", "&b", 1)]
    [InlineData("", "", 1)]
    public async Task AccNamesUseCandidateNormalizationAndHalfWidthFallback(
        string caption, string requested, int expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var provider = new AccessibleTestProvider(44, 0x10, caption, "");
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(expected), await session.InvokeAsync("CHKBTN",
            [id, Value.String(requested), N(1), N(2)], default));
    }

    [Fact]
    public async Task AccNumbersClampAndCacheUsesOriginalCandidateText()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        bool identical = false;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var provider = new AccessibleTestProvider(10, 0, "", "",
                    new(44, 0, "target...", ""),
                    new(44, 0x10, Volatile.Read(ref identical) ? "target..." : "target:", ""));
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        foreach (int acc in new[] { 1, 2, 3, -1 })
        {
            Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
                [id, Value.String("target"), N(0), N(acc)], default));
            Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
                [id, Value.String("target"), N(-9), N(acc)], default));
            Assert.Equal(N(1), await session.InvokeAsync("CHKBTN",
                [id, Value.String("target"), N(2), N(acc)], default));
        }

        Volatile.Write(ref identical, true);
        Assert.Equal(N(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("target"), N(2), N(2)], default));
        Assert.Equal(N(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("missing"), N(1), N(2)], default));
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"PRINT CHKBTN(HNDTOID({(long)fixture.Root}), \"target\", 0, TRUE)", output.Add);
        Assert.Equal(["0"], output);
        fixture.Dispose();
        Assert.Equal(N(-1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("target"), N(1), N(2)], default));
    }

    [Fact]
    public async Task AccCheckCancellationAndComFailureDoNotPublishItems()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Action? reading = null;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Button", "snapshot", 0, 10);
            host.AccessibleObject = parameter =>
            {
                Guid iid = typeof(IAccessibleNative).GUID;
                var provider = new AccessibleTestProvider(52, 8, "other", "state...")
                {
                    ReadingValue = Volatile.Read(ref reading)
                };
                return LresultFromObject(ref iid, (nuint)parameter, provider);
            };
        }, initializeOle: true);
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(1)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        Volatile.Write(ref reading, cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CHKBTN", [id, Value.String("state"), N(1), N(2)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
        Volatile.Write(ref reading, () => throw new COMException("所有providerの失敗です。",
            unchecked((int)0x80004005)));
        COMException error = await Assert.ThrowsAsync<COMException>(async () =>
            await session.InvokeAsync("CHKBTN", [id, Value.String("state"), N(1), N(2)], default));
        Assert.Equal(unchecked((int)0x80004005), error.HResult);
        Volatile.Write(ref reading, null);
        Assert.Equal(N(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String("state"), N(1), N(2)], default));
        Assert.Same(snapshot, session.AllItems);
    }

    [Fact]
    public async Task NativeThreeStateButtonIsReadWithoutClicking()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int clicks = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Clicked = (_, _) => Interlocked.Increment(ref clicks);
            nint button = host.Child("Button", " &State... ", 5, 10);
            Native.SendMessageW(button, 0xf1, 2, 0);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(2), await session.InvokeAsync("CHKBTN",
            [id, Value.String("state"), N(1), N(2)], default));
        fixture.Dispose();
        Assert.Equal(0, Volatile.Read(ref clicks));
    }
}
