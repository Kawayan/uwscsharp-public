using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class StatusControlTests
{
    private delegate nint Procedure(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint window,
        uint message, nint wParam, nint lParam);

    private static nint SetProcedure(nint window, nint value) =>
        IntPtr.Size == 8 ? SetWindowLongPtrW(window, -4, value)
            : SetWindowLongW(window, -4, value);

    internal static nint Create(ButtonControlTests.Fixture fixture,
        int top, int style, params string[] texts)
    {
        SliderControlTests.InitializeControls();
        nint status = fixture.Child("msctls_statusbar32", "", style | 12, top);
        int[] widths = [.. Enumerable.Range(1, texts.Length).Select(index => index * 30)];
        widths[^1] = -1;
        nint pointer = Marshal.AllocHGlobal(widths.Length * 4);
        try
        {
            Marshal.Copy(widths, 0, pointer, widths.Length);
            Assert.NotEqual(0, Native.SendMessageW(status, 0x404, widths.Length, pointer));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        for (int index = 0; index < texts.Length; index++)
        {
            Assert.NotEqual(0, Native.SendText(status, 0x40b, index, texts[index], 2, 1000, out nuint result));
            Assert.NotEqual((nuint)0, result);
        }

        return status;
    }

    private static Value N(long value) => Value.Numeric(value);

    [Fact]
    public async Task TextCanGrowAfterLengthQueryWithoutOverrunningBuffer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint status = Create(fixture, 10, 0, "a");
        string grown = new('長', 7300);
        nint previous = 0;
        bool changed = false;
        Exception? failure = null;
        Procedure procedure = (window, message, wParam, lParam) =>
        {
            nint result = CallWindowProcW(previous, window, message, wParam, lParam);
            if (message == 0x40c && !changed)
            {
                changed = true;
                try
                {
                    if (Native.SendText(window, 0x40b, 0, grown, 2, 1000, out nuint accepted) == 0
                        || accepted == 0)
                    {
                        throw new InvalidOperationException("検証用の本文変更に失敗しました。");
                    }
                }
                catch (Exception exception)
                {
                    // 検証の例外をネイティブcallback境界へ漏らしません。
                    failure = exception;
                }
            }

            return result;
        };
        previous = SetProcedure(status, Marshal.GetFunctionPointerForDelegate(procedure));
        Assert.NotEqual(0, previous);
        try
        {
            await using IWindowsAutomationSession session = fixture.Session();
            Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
            Assert.Equal(grown, (await session.InvokeAsync("GETSTR", [id, N(1), N(2)], default)).Display());
            Assert.True(changed);
            Assert.Null(failure);
        }
        finally
        {
            SetProcedure(status, previous);
            GC.KeepAlive(procedure);
        }
    }

    [Fact]
    public async Task ReadsAllPartsAndFlattensStatusPartNumbers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        string longText = new('長', 4097);
        Create(fixture, 10, 0, " first ", "", "日本語", longText, "raw\ud83d");
        Create(fixture, 60, 0x08000000, "disabled");
        Create(fixture, 100, 0, "last");
        nint hidden = Create(fixture, 140, 0, "hidden");
        Native.ShowWindow(hidden, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(7), await session.InvokeAsync("GETITEM", [id, N(524288), N(999)], default));
        Assert.Equal(["first", "", "日本語", longText, "raw\ud83d", "disabled", "last"], session.AllItems);
        Assert.Equal("first", (await session.InvokeAsync("GETSTR", [id, N(0), N(2)], default)).Display());
        Assert.Equal("", (await session.InvokeAsync("GETSTR", [id, N(2), N(2)], default)).Display());
        Assert.Equal("last", (await session.InvokeAsync("GETSTR", [id, N(6), N(2)], default)).Display());
        Assert.Equal("disabled", (await session.InvokeAsync("GETSTR", [id, N(-6), N(2)], default)).Display());
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR", [id, N(999), N(2)], default)).Kind);
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR", [id, N(int.MinValue), N(2)], default)).Kind);
        fixture.Child("Button", "button", 0, 170);
        Assert.Equal(N(7), await session.InvokeAsync("GETITEM",
            [id, N(524289), N(-1), N(0), N(1)], default));
        Assert.Equal("button", session.AllItems[0]);
        Assert.DoesNotContain("disabled", session.AllItems);
    }

    [Fact]
    public async Task OwnerDrawFailurePublishesPrefixAndInitialCancellationPreservesIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ButtonControlTests.Fixture fixture = null!;
        nint status = 0;
        await using PumpedControlFixture pumped = await PumpedControlFixture.CreateAsync(owner =>
        {
            fixture = owner;
            status = Create(owner, 10, 0, "first", "second");
            owner.MenuCommand = _ => Assert.True(Native.DestroyWindow(status));
        }, initializeOle: true);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(524288)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.NotEqual(0, Native.SendMessageW(status, 0x40b, 0x1001, 42));
        Assert.Equal("first", (await session.InvokeAsync("GETSTR", [id, N(1), N(2)], default)).Display());
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.InvokeAsync("GETSTR", [id, N(2), N(2)], default));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(524288)], default));
        Assert.NotSame(snapshot, session.AllItems);
        Assert.Equal(["first"], session.AllItems);
        snapshot = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(524288)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
        _ = Native.SendMessageW(pumped.Root, 0x111, 999, 0);
        Assert.False(Native.IsWindow(status));
        Assert.Equal(ValueKind.Empty, (await session.InvokeAsync("GETSTR", [id, N(1), N(2)], default)).Kind);
    }

    [Fact]
    public async Task InterpreterPreservesNativeSimpleModePartResults()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint status = Create(fixture, 10, 0, " first ", "second");
        Assert.NotEqual(0, Native.SendText(status, 0x40b, 255, "simple", 2, 1000, out _));
        Native.SendMessageW(status, 0x409, 1, 0);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                FOR item IN GETITEM(id, ITM_STATUSBAR)
                    PRINT item
                NEXT
                PRINT GETSTR(id, 0, STR_STATUS)
                PRINT GETSTR(id, 2, STR_STATUS)
                """, output.Add);
        Assert.Equal(["simple", "simple", "simple", "simple"], output);
    }
}
