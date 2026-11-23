using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class AccessibleItemTests
{
    private static Value N(long value) => Value.Numeric(value);

    [Fact]
    public async Task TextAndClickableItemsApplyDistinctFiltersAndEditPrecedence()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        int commands = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            host.Clicked = (_, _) => Interlocked.Increment(ref commands);
            host.Notify = (_, pointer) =>
            {
                if (Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code is -2 or -4)
                {
                    Interlocked.Increment(ref commands);
                }

                return 0;
            };
            host.Child("Static", "  静\u4383的...  ", 0, 10);
            host.Child("Static", "duplicate", 0, 35);
            host.Child("Static", "duplicate", 0, 35);
            host.Child("Static", "duplicate", 0, 60);
            host.Child("Static", "readonly label", 0, 85);
            host.Child("Edit", "  readonly...  ", 0x800, 110);
            host.Child("Static", "   ", 0, 130);
            host.Child("Edit", "  editable...  ", 0, 135);
            host.Child("Button", " &Run... ", 0, 160);
            host.Child("Button", " &Disabled... ", 0x08000000, 185);
            host.Child("Button", " &Twin... ", 0, 200);
            host.Child("Button", " &Twin... ", 0, 200);
            host.Child("SysLink", "<a id=\"link\"> &Link... </a>", 0, 210);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(6), await session.InvokeAsync("GETITEM", [id, N(8388608)], default));
        Assert.Equal(["UIA取得専用window", "静 的...", "duplicate", "duplicate", "readonly label", "readonly label"], session.AllItems);
        IReadOnlyList<string> textItems = session.AllItems;
        Assert.Equal(N(6), await session.InvokeAsync("GETITEM", [id, N(276824064)], default));
        Assert.Equal(textItems, session.AllItems);
        await session.InvokeAsync("GETITEM", [id, N(4194304)], default);
        Assert.Equal(["Run", "Disabled", "Twin", "Twin", "Link", "Link"], session.AllItems.Where(
            text => text is "Run" or "Disabled" or "Twin" or "Link"));
        Assert.DoesNotContain("静 的...", session.AllItems);
        Assert.DoesNotContain("readonly label", session.AllItems);
        await session.InvokeAsync("GETITEM", [id, N(272629760)], default);
        Assert.Equal(2, session.AllItems.Count(text => text == "Twin"));
        Assert.Contains("Run", session.AllItems);
        await session.InvokeAsync("GETITEM",
            [id, N(4194304), N(-1), N(0), N(1)], default);
        Assert.Contains("Run", session.AllItems);
        Assert.DoesNotContain("Disabled", session.AllItems);
        await session.InvokeAsync("GETITEM", [id, N(4194304 | 8388608)], default);
        IReadOnlyList<string> forward = session.AllItems;
        string[] known = ["UIA取得専用window", "静 的...", "duplicate", "readonly label", "Run", "Disabled", "Twin", "Link"];
        Assert.Equal(["UIA取得専用window", "静 的...", "duplicate", "duplicate", "readonly label", "readonly label", "Run", "Disabled", "Twin", "Twin", "Link", "Link"],
            forward.Where(known.Contains));
        await session.InvokeAsync("GETITEM",
            [id, N(4194304 | 8388608 | 65536 | 512), N(999)], default);
        IReadOnlyList<string> reverse = session.AllItems;
        Assert.Equal("UIA取得専用window", reverse[0]);
        Assert.Equal(["Disabled", "Run"], reverse.Where(text => text is "Run" or "Disabled"));
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM",
            [id, N(4194304 | 8388608 | 65536 | 512), N(999), N(0), N(0), N(2)], default));
        Assert.Equal(reverse.Take(2), session.AllItems);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM",
            [id, N(4194304 | 8388608 | 16777216)], default));
        Assert.Equal(["  editable...  "], session.AllItems);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                FOR item IN GETITEM(id, ITM_ACCCLK OR ITM_ACCTXT OR ITM_BACK)
                    PRINT item
                NEXT
                """, output.Add);
        Assert.Equal(forward, output);
        fixture.Dispose();
        Assert.Equal(0, Volatile.Read(ref commands));
    }

    [Fact]
    public async Task ReverseTraversalEnumeratesMoreThan256Children()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string longValue = new('長', 4097);
        nint longEdit = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            for (int i = 0; i < 260; i++)
            {
                nint edit = host.Child("Edit", i == 259 ? longValue : $"value{i}", 0, 10);
                if (i == 259)
                {
                    longEdit = edit;
                }
            }

            host.Child("Edit", "zero width", 0, 10, width: 0);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(260), await session.InvokeAsync("GETITEM", [id, N(16777216)], default));
        IReadOnlyList<string> forward = session.AllItems;
        var nativeText = new StringBuilder(4098);
        Assert.Equal(4097, Native.GetWindowTextW(longEdit, nativeText, nativeText.Capacity));
        Assert.Equal(longValue, nativeText.ToString());
        string? providerValue = await Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return "";
            }

            Guid iid = typeof(IAccessibleNative).GUID;
            Assert.Equal(0, AccessibleReader.AccessibleObjectFromWindow(
                longEdit, 0xfffffffcu, ref iid, out IAccessibleNative? accessible));
            try
            {
                return accessible!.Value(0);
            }
            finally
            {
                Marshal.ReleaseComObject(accessible!);
            }
        });
        // native本文とproviderのValueを区別し、取得側で短縮していないことを確認します。
        Assert.Equal(4096, providerValue!.Length);
        Assert.Equal(providerValue, forward[^1]);
        Assert.Equal(260, forward.Distinct().Count());
        Assert.Equal(N(258), await session.InvokeAsync("GETITEM",
            [id, N(16777216 | 65536), N(-1), N(0), N(0), N(258)], default));
        Assert.Equal(forward.Reverse().Take(258), session.AllItems);
    }

    [Fact]
    public async Task EditableValuesKeepWhitespaceAndSupportReverseAndMaximum()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        nint first = 0;
        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            first = host.Child("Edit", "  first\r\nvalue  ", 4, 10);
            host.Child("Edit", "", 0, 45);
            host.Child("Edit", "readonly", 0x800, 80);
            host.Child("Edit", "disabled", 0x08000000, 115);
            host.Child("Button", "button", 0, 150);
            host.Child("Static", "caption", 0, 175);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [N((long)fixture.Root)], default);
        Assert.Equal(N(3), await session.InvokeAsync("GETITEM", [id, N(16777216)], default));
        IReadOnlyList<string> forward = session.AllItems;
        Assert.Equal(new[] { "  first\r\nvalue  ", "", "disabled" }.Order(), forward.Order());
        Assert.Equal(N(3), await session.InvokeAsync("GETITEM",
            [id, N(16777216 | 65536 | 512), N(999)], default));
        Assert.Equal(forward.Reverse(), session.AllItems);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM",
            [id, N(16777216), N(1), N(0), N(0), N(1)], default));
        Assert.Equal(forward.Take(1), session.AllItems);
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM",
            [id, N(16777216), N(1), N(0), N(1)], default));
        Assert.DoesNotContain("disabled", session.AllItems);
        Assert.Equal(N(2), await session.InvokeAsync("GETITEM",
            [id, N(16777217), N(1), N(0), N(0), N(1)], default));
        Assert.Equal("button", session.AllItems[0]);
        Assert.Equal(forward[0], session.AllItems[1]);
        Value childId = await session.InvokeAsync("HNDTOID", [N((long)first)], default);
        Assert.Equal(N(1), await session.InvokeAsync("GETITEM", [childId, N(16777216)], default));
        Assert.Equal(["  first\r\nvalue  "], session.AllItems);
        IReadOnlyList<string> before = session.AllItems;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("GETITEM", [id, N(16777216)], cancellation.Token));
        Assert.Same(before, session.AllItems);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。")))
            .RunAsync($"""
                id = HNDTOID({(long)fixture.Root})
                FOR item IN GETITEM(id, ITM_ACCEDIT OR ITM_BACK)
                    PRINT item
                NEXT
                """, output.Add);
        Assert.Equal(forward, output);
        fixture.Dispose();
        Assert.Equal(N(0), await session.InvokeAsync("GETITEM", [id, N(16777216)], default));
        Assert.Empty(session.AllItems);
    }
}
