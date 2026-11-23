using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ItemOccurrenceTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task NativeItemNumbersNormalizeAndStopBeforeOneHundred(int kind)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        SliderControlTests.InitializeControls(3);
        using var fixture = new ButtonControlTests.Fixture();
        for (int index = 1; index <= 101; index++)
        {
            if (kind == 32)
            {
                ListViewControlTests.Create(fixture, index * 2);
            }
            else if (kind == 16)
            {
                nint tree = fixture.Child("SysTreeView32", "", 0, index * 2);
                TreeControlTests.Add(tree, 0, $"item{index:000}");
            }
            else
            {
                nint list = fixture.Child("LISTBOX", "", 0, index * 2);
                Native.SendText(list, 0x180, 0, $"item{index:000}", 2, 1000, out _);
            }
        }

        int perControl = kind == 32 ? 5 : 1;
        await using IWindowsAutomationSession session = fixture.Session();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Assert.Equal(Value.Numeric(perControl), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(kind), Value.Numeric(0)], default));
        if (kind != 32)
        {
            Assert.Equal(["item001"], session.AllItems);
        }

        foreach (int occurrence in new[] { -1, -2, int.MinValue })
        {
            Assert.Equal(Value.Numeric(99 * perControl), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(kind), Value.Numeric(occurrence)], default));
            if (kind != 32)
            {
                Assert.Equal("item099", session.AllItems[^1]);
            }
        }

        Assert.Equal(Value.Numeric(perControl), await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(kind), Value.Numeric(99)], default));
        if (kind != 32)
        {
            Assert.Equal(["item099"], session.AllItems);
        }

        foreach (int occurrence in new[] { 100, 101, int.MaxValue })
        {
            Assert.Equal(Value.Numeric(0), await session.InvokeAsync("GETITEM",
                [id, Value.Numeric(kind), Value.Numeric(occurrence)], default));
            Assert.Empty(session.AllItems);
        }
    }
}
