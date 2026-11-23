using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class ViewCheckTests
{
    private static Value N(long value) => Value.Numeric(value);

    private static (nint Control, nint Item, string Name, int Kind, int Number) Create(
        ButtonControlTests.Fixture fixture, bool view)
    {
        if (view)
        {
            nint control = ListViewControlTests.Create(fixture, 10);
            Native.SendMessageW(control, 0x1036, 4, 4);
            return (control, 3, "same", 32, 2);
        }

        SliderControlTests.InitializeControls(2);
        nint tree = fixture.Child("SysTreeView32", "", 0x100, 10);
        nint item = TreeControlTests.Add(tree, TreeControlTests.Add(tree, 0, "parent"), "target");
        return (tree, item, "\\parent\\target", 16, 1);
    }

    internal static void SetState(nint control, nint item, bool view, int image)
    {
        if (view)
        {
            ListViewControlTests.SetStateImage(control, (int)item, image);
            return;
        }

        var value = new TreeControlTests.Item
        {
            Mask = 8,
            Handle = item,
            StateMask = 0xf000,
            State = (uint)(image << 12)
        };
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TreeControlTests.Item>());
        try
        {
            Marshal.StructureToPtr(value, pointer, false);
            Assert.NotEqual(0, Native.SendMessageW(control, 0x113f, 0, pointer));
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QuerySelectsBeforeReadingAndTogglesOnlyWhenRequested(bool view)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        (nint Control, nint Item, string Name, int Kind, int Number) target = (0, 0, "", 0, 0);
        List<int> notices = [];
        List<int> keys = [];
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            target = Create(host, view);
            host.Notify = (_, pointer) =>
            {
                int code = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code;
                notices.Add(code);
                if (code == (view ? -155 : -412))
                {
                    keys.Add(unchecked((ushort)Marshal.ReadInt16(pointer,
                        Marshal.SizeOf<NativeTabControl.Notification>())));
                }

                return 0;
            };
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await session.InvokeAsync("GETITEM", [id, N(target.Kind)], default);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String(target.Name), N(target.Number)], default));
        Assert.Equal(view ? "same" : "parent\\target", (await session.InvokeAsync("GETSLCTLST", [id], default)).Display());
        Assert.Equal(!view, notices.Contains(-450));
        Assert.Equal(view, notices.Contains(-100));
        Assert.DoesNotContain(13, keys);
        Assert.DoesNotContain(32, keys);
        Assert.Same(snapshot, session.AllItems);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(target.Name), N(target.Kind), N(1), N(target.Number)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String(target.Name), N(target.Number)], default));
        int keyNotice = view ? -155 : -412;
        Assert.Contains(keyNotice, notices);
        notices.Clear();
        keys.Clear();
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(target.Name), N(target.Kind), N(1), N(target.Number)], default));
        Assert.DoesNotContain(32, keys);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(target.Name), N(target.Kind), N(0), N(target.Number)], default));
        Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String(target.Name), N(target.Number)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(target.Name), N(target.Kind), N(1), N(target.Number)], default));
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String(target.Name), N(0), N(0), N(target.Number)], default));
        Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String(target.Name), N(target.Number)], default));
        SetState(target.Control, target.Item, view, 3);
        Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String(target.Name), N(target.Number)], default));
        Assert.Equal(N(-1), await session.InvokeAsync("CHKBTN", [id, Value.String("missing")], default));
        SetState(target.Control, target.Item, view, 1);
        string name = view ? "same" : "target";
        string kind = view ? "CLK_LISTVIEW" : "CLK_TREEVIEW";
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"), listSelectionPlatform: new OwnedListSelectionPlatform()))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                PRINT CHKBTN(id, "{name}", {target.Number})
                PRINT CLKITEM(id, "{name}", {kind}, FALSE, {target.Number})
                PRINT CHKBTN(id, "{name}", {target.Number})
                PRINT CLKITEM(id, "{name}", {kind}, TRUE, {target.Number})
                PRINT CHKBTN(id, "{name}", {target.Number})
                """, output.Add);
        Assert.Equal(["0", "1", "0", "1", "1"], output);
        fixture.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpaceNotificationCancellationKeepsCompletedToggle(bool view)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        (nint Control, nint Item, string Name, int Kind, int Number) target =
            Create(fixture, view);
        using var cancellation = new CancellationTokenSource();
        fixture.Notify = (_, pointer) =>
        {
            int code = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code;
            if (code == (view ? -155 : -412)
                && Marshal.ReadInt16(pointer, Marshal.SizeOf<NativeTabControl.Notification>()) == 32)
            {
                cancellation.Cancel();
            }

            return 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("CLKITEM", [id, Value.String(target.Name),
                N(target.Kind), N(1), N(target.Number)], cancellation.Token));
        Assert.Equal(N(1), await session.InvokeAsync("CHKBTN",
            [id, Value.String(target.Name), N(target.Number)], default));
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public async Task ListViewParentCanVetoCheckChange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        (nint Control, nint Item, string Name, int Kind, int Number) target =
            Create(fixture, true);
        bool vetoed = false;
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            if (notice.Code == -100)
            {
                int offset = Marshal.SizeOf<NativeTabControl.Notification>();
                int next = Marshal.ReadInt32(pointer, offset + 8);
                int previous = Marshal.ReadInt32(pointer, offset + 12);
                if (((next ^ previous) & 0xf000) != 0)
                {
                    vetoed = true;
                    return 1;
                }
            }

            return 0;
        };
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(1), await session.InvokeAsync("CLKITEM", [id, Value.String(target.Name),
            N(target.Kind), N(1), N(target.Number)], default));
        Assert.True(vetoed);
        Assert.Equal(N(0), await session.InvokeAsync("CHKBTN",
            [id, Value.String(target.Name), N(target.Number)], default));
        Assert.Null(fixture.CallbackFailure);
    }
}
