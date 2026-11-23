using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class MenuControlTests
{
    [DllImport("user32.dll")]
    internal static extern nint CreateMenu();

    [DllImport("user32.dll")]
    internal static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool AppendMenuW(nint menu, uint flags, nuint item, string? text);

    [DllImport("user32.dll")]
    internal static extern bool SetMenu(nint root, nint menu);

    [DllImport("user32.dll")]
    internal static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool SetMenuInfo(nint menu, ref NativeMenuControl.MenuInfo info);

    private static Value N(int value) => Value.Numeric(value);

    [Fact]
    public async Task EnumeratesLeafPathsAndInvokesOwnedMenuWithCommandPayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(IntPtr.Size == 8 ? 80 : 48, Marshal.SizeOf<NativeMenuControl.MenuItemInfo>());
        using var fixture = new ButtonControlTests.Fixture();
        nint menu = CreateMenu();
        nint popup = CreatePopupMenu();
        nint nested = CreatePopupMenu();
        Assert.NotEqual(0, menu);
        Assert.NotEqual(0, popup);
        Assert.NotEqual(0, nested);
        try
        {
            Assert.True(AppendMenuW(nested, 0, 102, "保存(&S)..."));
            Assert.True(AppendMenuW(popup, 0, 100, "&開く\tCtrl+O"));
            Assert.True(AppendMenuW(popup, 0x800, 0, null));
            Assert.True(AppendMenuW(popup, 3, 101, "無効"));
            Assert.True(AppendMenuW(popup, 0x10, (nuint)nested, "詳細(_D)"));
            Assert.True(AppendMenuW(menu, 0x10, (nuint)popup, "&ファイル"));
            Assert.True(AppendMenuW(menu, 0, 103, "開く"));
            Assert.True(SetMenu(fixture.Root, menu));
            List<(uint Message, nint Menu)> initialized = [];
            fixture.MenuInit = (message, target, _) => initialized.Add((message, target));
            List<int> commands = [];
            var platform = new NativeMenuOperationTests.Platform();
            platform.Posting = () => commands.Add((int)platform.Posts[^1].Parameter);
            await using IWindowsAutomationSession session = new WindowsAutomation(
                _ => throw new InvalidOperationException(), listSelectionPlatform: platform).CreateSession();
            Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
            Assert.Equal(N(4), await session.InvokeAsync("GETITEM", [id, N(4)], default));
            Assert.Equal(["ファイル\\開く", "ファイル\\無効", "ファイル\\詳細\\保存", "開く"], session.AllItems);
            Assert.Equal([(0x116u, menu), (0x117u, popup), (0x117u, nested)], initialized);
            Assert.Equal(N(3), await session.InvokeAsync("GETITEM",
                [id, N(4), N(999), N(0), N(1)], default));
            Assert.Equal(["ファイル\\開く", "ファイル\\詳細\\保存", "開く"], session.AllItems);
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM", [id, Value.String("\\ファイル\\詳細\\保存"), N(4)], default));
            Assert.Equal(N(0), await session.InvokeAsync("CLKITEM", [id, Value.String("開く"), N(4), N(1), N(2)], default));
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM", [id, Value.String("開く")], default));
            Assert.Equal([102, 100], commands);
            Assert.Equal(N(0), await session.InvokeAsync("CLKITEM", [id, Value.String("無効"), N(4)], default));
            Assert.Equal(N(0), await session.InvokeAsync("CLKITEM", [id, Value.String("missing"), N(4)], default));
            Assert.Equal(N(0), await session.InvokeAsync("CLKITEM", [N(0), Value.String("開く"), N(4)], default));
            Assert.Equal([102, 100], commands);
            Assert.Null(fixture.CallbackFailure);
            Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<NativeMenuControl.MenuInfo>());
            var menuInfo = new NativeMenuControl.MenuInfo
            {
                Size = (uint)Marshal.SizeOf<NativeMenuControl.MenuInfo>(),
                Mask = 0x10,
                Style = 0x08000000
            };
            Assert.True(SetMenuInfo(popup, ref menuInfo));
            List<(int Index, nint Menu)> positionCommands = [];
            fixture.MenuPositionCommand = (index, target) => positionCommands.Add((index, target));
            Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
                [id, Value.String("ファイル\\開く"), N(4)], default));
            Assert.Empty(positionCommands);
            Assert.Equal([102, 100, 100], commands);
            IReadOnlyList<string> snapshot = session.AllItems;
            using var cancellation = new CancellationTokenSource();
            fixture.MenuInit = (_, _, _) => cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await session.InvokeAsync("GETITEM", [id, N(4)], cancellation.Token));
            Assert.NotSame(snapshot, session.AllItems);
            Assert.Empty(session.AllItems);
            fixture.MenuInit = null;
            List<string> output = [];
            await new Interpreter(windows: new WindowsAutomation(
                _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: platform))
                .RunAsync($"""
                    id = HNDTOID({(long)fixture.Root})
                    FOR item IN GETITEM(id, ITM_MENU, -1, 0, TRUE)
                        PRINT item
                    NEXT
                    PRINT CLKITEM(id, "ファイル\詳細\保存", CLK_MENU OR CLK_BACK)
                    """, output.Add);
            Assert.Equal(["ファイル\\開く", "ファイル\\詳細\\保存", "開く", "1"], output);
            Assert.Null(fixture.CallbackFailure);
        }
        finally
        {
            Assert.True(SetMenu(fixture.Root, 0));
            Assert.True(DestroyMenu(menu));
        }
    }
}
