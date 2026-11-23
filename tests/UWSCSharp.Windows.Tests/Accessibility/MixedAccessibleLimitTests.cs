using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class MixedAccessibleLimitTests
{
    [Theory]
    [InlineData(0, 3, false)]
    [InlineData(0, 3, true)]
    [InlineData(1, 1, false)]
    [InlineData(1, 1, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, 1, true)]
    [InlineData(4, 1, false)]
    [InlineData(4, 1, true)]
    [InlineData(5, 2, false)]
    [InlineData(5, 2, true)]
    [InlineData(6, 3, false)]
    [InlineData(6, 3, true)]
    public async Task MaximumUsesTotalCountAfterFirstAccessibleItem(int maximum, int accessibleCount, bool reverse)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(host =>
        {
            for (int index = 0; index < 3; index++)
            {
                host.Child("Button", $"Button{index}", 0, index * 30);
                host.Child("Edit", $"Edit{index}", 0, 100 + index * 30);
            }
        });
        await using IWindowsAutomationSession session = new WindowsAutomation(
            _ => throw new InvalidOperationException("物理入力は禁止です。"))
            .CreateSession();
        Value id = await session.InvokeAsync("HNDTOID", [Value.Numeric((long)fixture.Root)], default);
        Value count = await session.InvokeAsync("GETITEM",
            [id, Value.Numeric(1 | 16777216 | 512 | (reverse ? 65536 : 0)), Value.Numeric(1),
                Value.Numeric(0), Value.Numeric(0), Value.Numeric(maximum)], default);
        Assert.Equal(Value.Numeric(3 + accessibleCount), count);
        Assert.Equal(["Button0", "Button1", "Button2"], session.AllItems.Take(3));
        IEnumerable<int> indices = reverse
            ? Enumerable.Range(0, 3).Reverse() : Enumerable.Range(0, 3);
        Assert.Equal(indices.Take(accessibleCount).Select(index => $"Edit{index}"),
            session.AllItems.Skip(3));
    }
}
