using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeListViewMultipleBufferTests
{
    [Theory]
    [InlineData("&A...", "A", false)]
    [InlineData("&A...", "&A...", true)]
    [InlineData("AbC", "aBc", true)]
    [InlineData("É", "é", false)]
    [InlineData("ＡＢＣ", "abc", true)]
    [InlineData(" x\tY ", "X\ty", true)]
    [InlineData(" x\tY ", "x", false)]
    public async Task ViewUsesWholeNameAndSingleMatcher(string item, string requested, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        ListViewControlTests.SetText(view, 0, item);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(expected ? 1 : 0), await session.InvokeAsync("CLKITEM",
            [id, Value.String(requested), Value.Numeric(32), Value.Numeric(0)], default));
        Assert.Equal((nint)(expected ? 3 : 0), Native.SendMessageW(view, 0x102c, 0, 3));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TabNameIsLiteralAndOnlyMatchingRowChangesCheckbox(bool present, bool on)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        Native.SendMessageW(view, 0x1036, 4, 4);
        if (present)
        {
            ListViewControlTests.SetText(view, 1, "first\tsame");
        }

        ListViewControlTests.Select(view, 3);
        nint textAddress = 0;
        List<nint> pointers = [];
        using var hook = new ToolbarClickTextTests.Interceptor(view, (message, _, pointer) =>
        {
            if (message == 0x1073)
            {
                textAddress = Marshal.ReadIntPtr(pointer, IntPtr.Size == 8 ? 24 : 20);
            }

            if (message == 0x102b)
            {
                pointers.Add(pointer);
                Assert.Equal(3, Marshal.ReadInt32(pointer, 16));
            }

            return null;
        });
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(present ? 1 : 0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("first\tsame"), Value.Numeric(32), Value.Numeric(on ? 1 : 0)], default));
        for (int row = 0; row < 5; row++)
        {
            Assert.Equal((nint)(present && row == 1 ? 3 : 0),
                Native.SendMessageW(view, 0x102c, row, 3));
            Assert.Equal((nint)(present && row == 1 && on ? 0x2000 : 0x1000),
                Native.SendMessageW(view, 0x102c, row, 0xf000));
        }

        Assert.NotEmpty(pointers);
        Assert.All(pointers, pointer => Assert.Equal(textAddress + 512, pointer));
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("reject")]
    [InlineData("cancel_selection")]
    [InlineData("cancel_space")]
    public async Task LiteralNameFailureKeepsPartialStateAndReleasesBeforeRestore(string outcome)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint view = ListViewControlTests.Create(fixture, 10);
        Native.SendMessageW(view, 0x1036, 4, 4);
        ListViewControlTests.SetText(view, 0, "first\tsame");
        using var cancellation = new CancellationTokenSource();
        fixture.Notify = (_, pointer) =>
        {
            int code = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code;
            if (outcome == "cancel_selection" && code == -101)
            {
                cancellation.Cancel();
            }

            if (outcome == "cancel_space" && code == -155
                && Marshal.ReadInt16(pointer, Marshal.SizeOf<NativeTabControl.Notification>()) == 32)
            {
                cancellation.Cancel();
            }

            return outcome == "reject" && code == -100 ? 1 : 0;
        };
        Native.ShowWindow(fixture.Root, 0);
        ToolbarClickTextTests.RestorePlatform? platform = null;
        int restores = 0;
        platform = new ToolbarClickTextTests.RestorePlatform
        {
            Restoring = () =>
            {
                restores++;
                Assert.True(platform!.Buffers.Released);
                Assert.True(platform.Buffers.Closed);
            },
        };
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value[] args = [id, Value.String(outcome == "missing" ? "first\tmissing" : "first\tsame"),
            Value.Numeric(32 | 512)];
        if (outcome.StartsWith("cancel", StringComparison.Ordinal))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("CLKITEM", args, cancellation.Token));
        }
        else
        {
            Assert.Equal(Value.Numeric(outcome == "reject" ? 1 : 0),
                await session.InvokeAsync("CLKITEM", args, default));
        }

        Assert.Equal((nint)(outcome.StartsWith("cancel", StringComparison.Ordinal) ? 3 : 0),
            Native.SendMessageW(view, 0x102c, 0, 3));
        Assert.Equal(1, restores);
        Assert.Null(fixture.CallbackFailure);
    }
}
