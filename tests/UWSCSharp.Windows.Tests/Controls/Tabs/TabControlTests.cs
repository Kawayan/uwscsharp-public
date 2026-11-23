using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class TabControlTests
{
    private static Value N(long value) => Value.Numeric(value);

    internal static nint Create(ButtonControlTests.Fixture fixture,
        int top, int style, params string[] names)
        => CreateNamed(fixture, "SysTabControl32", top, style, names);

    internal static nint CreateNamed(ButtonControlTests.Fixture fixture,
        string className, int top, int style, params string[] names)
    {
        SliderControlTests.InitializeControls(8);
        nint tab = fixture.Child(className, "", style, top);
        for (int index = 0; index < names.Length; index++)
        {
            nint text = Marshal.StringToHGlobalUni(names[index]);
            nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeTabControl.Item>());
            try
            {
                var item = new NativeTabControl.Item { Mask = 1, Text = text };
                Marshal.StructureToPtr(item, pointer, false);
                Assert.Equal(index, Native.SendMessageW(tab, 0x133e, index, pointer));
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
                Marshal.FreeHGlobal(text);
            }
        }

        return tab;
    }

    [Fact]
    public async Task ReadsAllTabsInOrderAndPreservesEmptyDuplicatesAndSnapshot()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        Create(fixture, 10, 0, " &First... ", "", "same", "same", new string('長', 300));
        Create(fixture, 50, 0x08000000, "disabled");
        nint hidden = Create(fixture, 90, 0, "hidden");
        Native.ShowWindow(hidden, 0);
        fixture.Child("Button", "button", 0, 130);
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(6), await session.InvokeAsync("GETITEM", [id, N(8), N(999)], default));
        Assert.Equal(["First", "", "same", "same", new string('長', 255), "disabled"], session.AllItems);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot)[0] = "changed");
        Assert.Equal(N(6), await session.InvokeAsync("GETITEM", [id, N(9), N(-1), N(0), N(1)], default));
        Assert.Equal("button", session.AllItems[0]);
        Assert.DoesNotContain("disabled", session.AllItems);
        Assert.Equal("First", snapshot[0]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        snapshot = session.AllItems;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(8)], cancellation.Token));
        Assert.Same(snapshot, session.AllItems);
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [N(0), N(8)], default));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task SelectsMatchingOccurrenceAndVerifiesAfterNotifications(
        bool reselect, bool destroy, bool veto)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        Create(fixture, 10, 0, "same");
        nint tab = Create(fixture, 60, 0, "first", "&Same...");
        List<int> codes = [];
        fixture.Notify = (id, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            Assert.Equal(tab, notice.Window);
            Assert.Equal((nuint)(destroy && notice.Code == -551 ? 0 : 42), notice.Id);
            Assert.Equal((nint)(destroy && notice.Code == -551 ? 0 : 42), id);
            codes.Add(notice.Code);
            if (notice.Code == -551 && reselect)
            {
                Native.SendMessageW(tab, 0x130c, 0, 0);
            }

            if (notice.Code == -552 && destroy)
            {
                Native.DestroyWindow(tab);
            }

            return veto ? 1 : 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value window = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(reselect || destroy ? 0 : 1), await session.InvokeAsync("CLKITEM",
            [window, Value.String("same"), N(8), N(1), N(2)], default));
        Assert.Equal(destroy ? [-552, -551] : veto ? [-552, -552, -551]
            : new[] { -552, -552, -551, -551 }, codes);
        Assert.Null(fixture.CallbackFailure);
        Assert.Equal(N(0), await session.InvokeAsync("CLKITEM",
            [window, Value.String("missing"), N(8)], default));
    }

    [Fact]
    public async Task CancellationAtChangingStopsBeforeSelection()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = Create(fixture, 10, 0, "first", "second");
        using var cancellation = new CancellationTokenSource();
        fixture.Notify = (_, _) => { cancellation.Cancel(); return 0; };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String("second"), N(8)], cancellation.Token));
        Assert.Equal(0, Native.SendMessageW(tab, 0x130b, 0, 0));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task InterpreterUsesTabConstantsAndDefaultSearchOrder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = Create(fixture, 10, 0, "first", "target");
        Create(fixture, 50, 0x08000000, "disabled");
        fixture.Child("Button", "target", 0, 90);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT CLKITEM(id, "target", 0)
                PRINT GETITEM(id, ITM_TAB, 999)
                FOR item IN GETITEM(id, ITM_TAB)
                    PRINT item
                NEXT
                PRINT CLKITEM(id, "target", CLK_TAB OR CLK_BACK)
                PRINT CLKITEM(id, "disabled", CLK_TAB)
                """, output.Add);
        Assert.Equal(["1", "3", "first", "target", "disabled", "1", "0"], output);
        Assert.Equal(1, Native.SendMessageW(tab, 0x130b, 0, 0));
    }

}
