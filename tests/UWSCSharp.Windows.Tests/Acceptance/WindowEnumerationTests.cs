using System.Runtime.InteropServices;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class WindowEnumerationTests
{
    [Fact]
    public async Task FindsCaptionsBeforeClassNames()
    {
        nint caption = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Child("Static", "label", 0, 10);
            caption = host.Child("Button", "Static", 0, 40);
        });
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync($"""
            id = HNDTOID({(long)fixture.Root})
            PRINT GETCTLHND(id, "Static") = {(long)caption}
            """, output.Add);
        Assert.Equal(["1"], output);
    }

    [Fact]
    public async Task ReturnsOwnedMenuAndSystemMenuHandles()
    {
        nint menu = 0;
        nint systemMenu = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            menu = CreateMenu();
            Assert.NotEqual(0, menu);
            Assert.True(SetMenu(host.Root, menu));
            SetWindowLongW(host.Root, -16,
                Native.GetWindowLongW(host.Root, -16) | 0x80000);
            systemMenu = GetSystemMenu(host.Root, false);
            Assert.NotEqual(0, systemMenu);
        });
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync($"""
            id = HNDTOID({(long)fixture.Root})
            PRINT GETCTLHND(id, GET_MENU_HND) = {(long)menu}
            PRINT GETCTLHND(id, GET_SYSMENU_HND) = {(long)systemMenu}
            """, output.Add);
        Assert.Equal(["1", "1"], output);
    }

    [Fact]
    public void ClassifiesNativeSharedAndCustomCursorHandles()
    {
        foreach ((int kind, int resource) in CursorReader.StandardCursors)
        {
            nint cursor = LoadCursorW(0, resource);
            Assert.NotEqual(0, cursor);
            Assert.Equal(kind, CursorReader.Classify(cursor,
                id => LoadCursorW(0, id)));
        }

        nint custom = CreateCursor(0, 0, 0, 16, 16,
            [.. Enumerable.Repeat((byte)255, 32)], new byte[32]);
        Assert.NotEqual(0, custom);
        try
        {
            Assert.Equal(-(double)(nuint)custom,
                CursorReader.Classify(custom, id => LoadCursorW(0, id)));
        }
        finally
        {
            Assert.True(DestroyCursor(custom));
        }
    }

    [DllImport("user32.dll")]
    private static extern nint LoadCursorW(nint instance, nint resource);

    [DllImport("user32.dll")]
    private static extern nint CreateCursor(nint instance, int x, int y,
        int width, int height, byte[] andMask, byte[] xorMask);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(nint cursor);

    [DllImport("user32.dll")]
    private static extern nint CreateMenu();

    [DllImport("user32.dll")]
    private static extern bool SetMenu(nint window, nint menu);

    [DllImport("user32.dll")]
    private static extern int SetWindowLongW(nint window, int index, int value);

    [DllImport("user32.dll")]
    private static extern nint GetSystemMenu(nint window, bool revert);

    [Fact]
    public async Task FindsControlHandlesByCaptionClassAndOccurrence()
    {
        nint first = 0;
        nint second = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            first = host.Child("Button", "選択(&A)", 0, 10);
            second = host.Child("Button", "選択", 0, 40);
        });
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync($"""
            id = HNDTOID({(long)fixture.Root})
            PRINT GETCTLHND(id, "選択") = {(long)first}
            PRINT GETCTLHND(id, "選択", 2) = {(long)second}
            PRINT GETCTLHND(id, "button", 2) = {(long)second}
            PRINT GETCTLHND(id, "missing")
            PRINT GETCTLHND(-1, "button")
            PRINT GETCTLHND(id, GET_MENU_HND)
            PRINT GETCTLHND(id, GET_SYSMENU_HND) = 0
            """, output.Add);
        Assert.Equal(["1", "1", "1", "0", "0", "0", "1"], output);
    }

    [Fact]
    public async Task EnumeratesOwnedChildrenAndPublishesStableIds()
    {
        nint first = 0;
        nint second = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            first = host.Child("Edit", "first", 0, 10);
            second = host.Child("Static", "second", 0, 40);
        });
        var automation = new WindowsAutomation(_ =>
            throw new InvalidOperationException("入力は禁止です。"));
        await using IWindowsAutomationSession session = automation.CreateSession();
        Value id = await session.InvokeAsync("HNDTOID",
            [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync(
            "GETALLWIN", [id], default));
        int[] snapshot = [.. session.AllWindowIds];
        Assert.Equal(2, snapshot.Distinct().Count());
        var handles = new List<long>();
        foreach (int child in snapshot)
        {
            handles.Add((long)(await session.InvokeAsync("IDTOHND",
                [Value.Numeric(child)], default)).AsNumber());
        }

        Assert.Contains((long)first, handles);
        Assert.Contains((long)second, handles);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync(
            "GETALLWIN", [id], default));
        Assert.Equal(snapshot, session.AllWindowIds);
        await session.InvokeAsync("GETALLWIN", [], default);
        Assert.Contains((int)id.AsNumber(), session.AllWindowIds);
        await session.InvokeAsync("GETALLWIN", [Value.Numeric(0)], default);
        Assert.Contains((int)id.AsNumber(), session.AllWindowIds);
        Assert.NotEmpty(snapshot);

        var runner = new Interpreter(windows: automation);
        List<string> output = [];
        await runner.RunAsync($"""
            id = HNDTOID({(long)fixture.Root})
            PRINT GETALLWIN(id)
            PRINT LENGTH(ALL_WIN_ID)
            count = 0
            FOR child IN GETALLWIN(id)
                count = count + 1
            NEXT
            PRINT count
            PRINT MUSCUR() <> EMPTY
            """, output.Add);
        Assert.Equal(["2", "2", "2", "1"], output);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int[] beforeCancel = [.. session.AllWindowIds];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETALLWIN", [], cancellation.Token));
        Assert.Equal(beforeCancel, session.AllWindowIds);
        fixture.Dispose();
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync(
            "GETALLWIN", [id], default));
        Assert.Empty(session.AllWindowIds);
    }
}
