using System.Runtime.InteropServices;
using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class OwnerDrawItemDataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminatedDataCanBeReadFromAnotherWindowThread(bool combo)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            nint control = host.Child(combo ? "COMBOBOX" : "LISTBOX", "",
                combo ? 0x13 : 0x10, 10);
            Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, 0x41);
            Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, 0x42);
            Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 0, 0);
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(_ =>
            throw new InvalidOperationException("物理入力は禁止です。")).CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String("A"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.Equal(["A", "B"], session.AllItems);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("B"), Value.Numeric(2)], default));
        Assert.Equal(Value.String("B"), await session.InvokeAsync("GETSLCTLST", [id], default));
    }

    [Fact]
    public void NoDataTextMessageCopiesZeroItemData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 0x2010, 10);
        Native.SendMessageW(control, 0x1a7, 3, 0);
        nint buffer = Marshal.AllocHGlobal(16);
        try
        {
            byte[] bytes = [.. Enumerable.Repeat((byte)0xcd, 16)];
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            nint result = Native.SendMessageW(control, 0x189, 0, buffer);
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            Assert.Equal((nint)IntPtr.Size, result);
            Assert.Equal((nint)IntPtr.Size, Native.SendMessageW(control, 0x18a, 0, 0));
            Assert.Equal((nint)0, Marshal.ReadIntPtr(buffer));
            Assert.All(bytes[IntPtr.Size..], value => Assert.Equal((byte)0xcd, value));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(false, 0x10)]
    [InlineData(false, 0x20)]
    [InlineData(true, 0x10)]
    [InlineData(true, 0x20)]
    public void TextMessageCopiesItemDataWithoutAppendingTerminator(bool combo, int drawing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "",
            drawing | (combo ? 3 : 0), 10);
        foreach (int data in new[] { 0, 0x41, 0x00420041 })
        {
            nint index = Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, data);
            Assert.True(index >= 0);
            Assert.Equal((nint)data,
                Native.SendMessageW(control, combo ? 0x150u : 0x199u, index, 0));
            nint buffer = Marshal.AllocHGlobal(16);
            try
            {
                byte[] bytes = [.. Enumerable.Repeat((byte)0xcd, 16)];
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                Native.SendMessageW(control, combo ? 0x148u : 0x189u, index, buffer);
                Assert.Equal((nint)data, Marshal.ReadIntPtr(buffer));
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                Assert.All(bytes[IntPtr.Size..], value => Assert.Equal((byte)0xcd, value));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StringlessControlsReadTerminatedSelectedData(bool combo, bool populated)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "", combo ? 0x13 : 0x10, 10);
        if (populated)
        {
            Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, 0x41);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        if (populated)
        {
            Native.SendMessageW(control, combo ? 0x14eu : 0x186u, 0, 0);
            Assert.Equal(Value.String("A"), await session.InvokeAsync("GETSLCTLST", [id], default));
        }
    }

    [Theory]
    [InlineData(false, 0x10)]
    [InlineData(false, 0x20)]
    [InlineData(true, 0x10)]
    [InlineData(true, 0x20)]
    public void SelectionUsesOnlyDataTerminatedWithinOriginalDword(bool combo, int drawing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "",
            drawing | (combo ? 3 : 0), 10);
        foreach (int data in new[] { 0, 0x41, 0x00410000, 0xd83d, 0x00420041 })
        {
            nint index = Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, data);
            Native.SendMessageW(control, combo ? 0x14eu : 0x186u, index, 0);
            if (data == 0x00420041)
            {
                Assert.Throws<NotSupportedException>(() => NativeListControl.SelectedText(control, default));
            }
            else
            {
                string expected = (data & 0xffff) == 0 ? "" : new string((char)data, 1);
                Assert.Equal(expected, NativeListControl.SelectedText(control, default));
            }
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task NoDataSelectionReadsZeroAsEmpty(int count, bool multiple)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child("LISTBOX", "", 0x2010 | (multiple ? 8 : 0), 10);
        Assert.Equal((nint)0, Native.SendMessageW(control, 0x1a7, count, 0));
        Assert.Equal((nint)count, Native.SendMessageW(control, 0x18b, 0, 0));
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.String(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        if (count != 0)
        {
            Native.SendMessageW(control, multiple ? 0x185u : 0x186u, 1, multiple ? -1 : 0);
            Assert.Equal(Value.String(""), await session.InvokeAsync("GETSLCTLST", [id], default));
        }

        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.Equal(Value.Numeric(0), await session.InvokeAsync("CLKITEM",
            [id, Value.String("missing"), Value.Numeric(2)], default));
    }

    [Theory]
    [InlineData(false, 0x10)]
    [InlineData(false, 0x20)]
    [InlineData(true, 0x10)]
    [InlineData(true, 0x20)]
    [InlineData(false, 0x18)]
    [InlineData(false, 0x28)]
    public async Task ItemDataEnumerationKeepsPrefixAndClickPreservesItems(bool combo, int drawing)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint control = fixture.Child(combo ? "COMBOBOX" : "LISTBOX", "",
            drawing | (combo ? 3 : 0), 10);
        foreach (int data in new[] { 0x41, 0x20, 0, 0x42 })
        {
            Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, data);
        }

        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(2), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.Equal(["A", "B"], session.AllItems);
        IReadOnlyList<string> snapshot = session.AllItems;
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("B"), Value.Numeric(2)], default));
        Assert.Equal(Value.String("B"), await session.InvokeAsync("GETSLCTLST", [id], default));
        Assert.Same(snapshot, session.AllItems);
        Native.SendMessageW(control, combo ? 0x143u : 0x180u, 0, 0x00420041);
        Assert.Equal(Value.Numeric(3), await session.InvokeAsync("GETITEM", [id, Value.Numeric(2)], default));
        Assert.NotSame(snapshot, session.AllItems);
        Assert.Equal(["A", "B", "AB"], session.AllItems);
        Assert.Equal(["A", "B"], snapshot);
        Assert.Equal(Value.Numeric(1), await session.InvokeAsync("CLKITEM",
            [id, Value.String("AB"), Value.Numeric(2)], default));
        Assert.Equal((nint)(combo ? 4 : (drawing & 8) == 0 ? 4 : 1),
            Native.SendMessageW(control, combo ? 0x147u : (drawing & 8) == 0 ? 0x188u : 0x187u,
                combo || (drawing & 8) == 0 ? 0 : 4, 0));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await session.InvokeAsync("GETSLCTLST", [id], default));
    }
}
