using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class UiaItemTests
{
    private static Value N(long value) => Value.Numeric(value);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkItemsKeepRawNamesAndPublishOnlySuccessfulSnapshots(bool excludeDisabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint first = 0;
        int clicks = 0;
        string raw = "  開く...  ";
        string longName = new('長', 4097);
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Notify = (_, pointer) =>
            {
                if (Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code is -2 or -4)
                {
                    Interlocked.Increment(ref clicks);
                }

                return 0;
            };
            Assert.True(Native.SetWindowPos(host.Root, 0, 0, 0, 300, 400, 0x16));
            first = host.Child("SysLink", $"<a id=\"first\">{raw}</a>", 0, 10);
            host.Child("SysLink", "<a id=\"duplicate1\">重複</a>", 0, 35);
            host.Child("SysLink", "<a id=\"duplicate2\">重複</a>", 0, 60);
            host.Child("SysLink", "<a id=\"disabled\">disabled control</a>", 0x08000000, 85);
            nint hidden = host.Child("SysLink", "<a id=\"hidden\">hidden control</a>", 0, 110);
            Native.ShowWindow(hidden, 0);
            // 長い本文のnative折返しlayoutを避け、取得するName自体は短縮しません。
            host.Child("SysLink", $"<a id=\"long\">{longName}</a>", 0, 135, width: 16000);
            nint disabledItem = host.Child("SysLink", "<a id=\"disabled-item\">disabled item</a>", 0, 170);
            nint pointer = Marshal.AllocHGlobal(4280);
            try
            {
                Marshal.Copy(new byte[4280], 0, pointer, 4280);
                Marshal.WriteInt32(pointer, 3);
                Marshal.WriteInt32(pointer, 12, 2);
                Assert.NotEqual(0, Native.SendMessageW(disabledItem, 0x702, 0, pointer));
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            host.Child("Button", " &Button... ", 0, 205);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        int expectedCount = excludeDisabled ? 5 : 6;
        Value count = await session.InvokeAsync("GETITEM",
            [id, N(128), N(-1), N(0), N(excludeDisabled ? 1 : 0)], default);
        Assert.Equal(N(expectedCount), count);
        IReadOnlyList<string> snapshot = session.AllItems;
        List<string> expected = [raw, "重複", "重複", "disabled control", longName];
        if (!excludeDisabled)
        {
            expected.Add("disabled item");
        }

        Assert.Equal(expected.Order(), snapshot.Order());
        Assert.Equal(N(expectedCount), await session.InvokeAsync("GETITEM",
            [id, N(128), N(999), N(0), N(excludeDisabled ? 1 : 0)], default));
        Assert.Equal(snapshot, session.AllItems);
        Assert.Equal(N(expectedCount + 1), await session.InvokeAsync("GETITEM",
            [id, N(129), N(-1), N(0), N(excludeDisabled ? 1 : 0)], default));
        Assert.Equal("Button", session.AllItems[0]);
        Assert.Equal(snapshot, session.AllItems.Skip(1));
        Value childId = await session.InvokeAsync("HNDTOID", [N((long)first)], default);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [childId, N(128)], default));
        Assert.Equal([raw], session.AllItems);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                FOR item IN GETITEM(id, ITM_LINK, , , {(excludeDisabled ? "TRUE" : "FALSE")})
                    PRINT item
                NEXT
                """, output.Add);
        Assert.Equal(snapshot, output);
        IReadOnlyList<string> beforeCancel = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(128)], cancellation.Token));
        Assert.Same(beforeCancel, session.AllItems);
        fixture.Dispose();
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(128)], default));
        Assert.Empty(session.AllItems);
        Assert.Equal(0, Volatile.Read(ref clicks));
    }
}
