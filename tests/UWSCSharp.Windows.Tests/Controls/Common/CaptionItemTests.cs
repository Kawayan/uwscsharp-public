using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class CaptionItemTests
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIconW(nint instance, nint name);

    private static Value N(int value) => Value.Numeric(value);

    [Theory]
    [InlineData("BUTTON", 1, "保存(&S):...", "保存")]
    [InlineData("EDIT", 131072, "  &Edit...  ", "&Edit...")]
    [InlineData("STATIC", 262144, "  &Label...  ", "&Label...")]
    public async Task EnumeratesAllCaptionsAndPreservesEmptyValues(
        string className, int kind, string text, string normalized)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child(className, "second", 0, 50);
        fixture.Child(className, text, 0, 10);
        fixture.Child(className, "disabled", 0x08000000, 90);
        fixture.Child(className, "", 0, 120);
        nint hidden = fixture.Child(className, "hidden", 0, 150);
        Native.ShowWindow(hidden, 0);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(4), await session.InvokeAsync("GETITEM", [id, N(kind)], default));
        Assert.Equal([normalized, "second", "disabled", ""], session.AllItems);
        Assert.Equal(N(3), await session.InvokeAsync("GETITEM",
            [id, N(kind), N(999), N(0), N(1)], default));
        Assert.Equal([normalized, "second", ""], session.AllItems);
        IReadOnlyList<string> snapshot = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(kind)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                FOR item IN GETITEM(id, {kind}, -1, 0, TRUE)
                    PRINT item
                    GETITEM(-1, {kind})
                NEXT
                """, output.Add);
        Assert.Equal([normalized, "second", ""], output);
        Assert.True(Native.DestroyWindow(fixture.Root));
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(kind)], default));
        Assert.Empty(session.AllItems);
    }

    [Fact]
    public async Task CombinesKindsInTypeOrderAndKeepsUnicodeAndDuplicates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("BUTTON", "保存(&S)", 3, 150);
        fixture.Child("EDIT", "日本語\r\n二行目", 4, 100);
        fixture.Child("STATIC", "重複", 0, 10);
        fixture.Child("STATIC", "重複", 0, 30);
        string longText = new('長', 4097);
        fixture.Child("STATIC", longText, 0, 170);
        nint list = fixture.Child("LISTBOX", "", 0, 50);
        Assert.NotEqual(0, Native.SendText(list, 0x180, 0, "list", 2, 1000, out _));
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(6), await session.InvokeAsync("GETITEM",
            [id, N(1 | 2 | 131072 | 262144)], default));
        Assert.Equal(["保存", "list", "日本語\r\n二行目", "重複", "重複", longText],
            session.AllItems);
        Assert.Equal(N(5), await session.InvokeAsync("GETITEM",
            [id, N(1 | 2 | 131072 | 262144), N(999)], default));
        Assert.DoesNotContain("list", session.AllItems);
    }

    [Theory]
    [InlineData("BUTTON", 11, 1)]
    [InlineData("STATIC", 3, 262144)]
    public async Task StoredOwnerDrawAndUnloadedIconCaptionsRemainReadable(
        string className, int style, int kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        fixture.Child("EDIT", "previous", 0, 10);
        fixture.Child(className, "", style, 50);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(131072)], default);
        IReadOnlyList<string> previous = session.AllItems;
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM",
            [id, N(kind | 131072)], default));
        Assert.NotSame(previous, session.AllItems);
        Assert.Equal(kind == 1 ? new[] { "", "previous" } : ["previous", ""], session.AllItems);
    }

    [Fact]
    public async Task LoadedIconPublishesEmptyCaptionAndKeepsFollowingText()
    {
        using var fixture = new ButtonControlTests.Fixture();
        nint icon = fixture.Child("STATIC", "icon caption", 3, 10);
        nint sharedIcon = LoadIconW(0, 32516);
        Assert.NotEqual(0, sharedIcon);
        Native.SendMessageW(icon, 0x170, sharedIcon, 0);
        Assert.Equal(sharedIcon, Native.SendMessageW(icon, 0x171, 0, 0));
        fixture.Child("STATIC", " tail ", 0, 50);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM",
            [id, N(262144)], default));
        Assert.Equal(["", "tail"], session.AllItems);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR",
            [id, N(1), N(1)], default));
        Value iconId = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)icon)], default);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSTR",
            [iconId, N(0), N(1)], default));
    }
}
