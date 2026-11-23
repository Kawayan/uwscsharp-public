using System.Runtime.InteropServices;
using Xunit;

namespace UWSCSharp.Windows.Tests;

// 固定された対応表だけを調べ、COM・機器・画面には触れません。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SensorDataFieldCatalogTests
{
    [Fact]
    public void RegisteredSelectorRangesKeepTheirHoles()
    {
        int[] expected = [1, 2, 5, 6, 7, 8, 9, 10, 15, 16, 17, 18, 19,
            20, 21, 22, 25, 26, 27, 28, 30, 31, 32, 33, 34, 35, 36,
            40, 41, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54,
            60, 61, 62, 63, 64, 65, 66, 67];
        int[] actual = [.. Enumerable.Range(-1, 70)
            .Where(selector => SensorDataFieldCatalog.TryGet(selector, out _))];
        Assert.Equal(expected, actual);
        Assert.False(SensorDataFieldCatalog.TryGet(int.MinValue, out _));
        Assert.False(SensorDataFieldCatalog.TryGet(int.MaxValue, out _));
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(8, 3)]
    [InlineData(9, 2)]
    [InlineData(16, 4)]
    [InlineData(17, 3)]
    [InlineData(28, 8)]
    [InlineData(36, 8)]
    [InlineData(48, 8)]
    [InlineData(51, 13)]
    [InlineData(52, 14)]
    [InlineData(53, 11)]
    [InlineData(54, 12)]
    [InlineData(60, 4)]
    [InlineData(61, 2)]
    [InlineData(67, 28)]
    public void InternalFieldOrderIsNotAssumedToBeTheWindowsPropertyOrder(
        int selector, uint property)
    {
        Assert.True(SensorDataFieldCatalog.TryGet(selector, out SensorDataField field));
        Assert.Equal(property, field.Key!.Value.PropertyId);
    }

    [Theory]
    [InlineData(1, (int)SensorDataValueKind.Boolean)]
    [InlineData(2, (int)SensorDataValueKind.Numeric)]
    [InlineData(22, (int)SensorDataValueKind.Numeric)]
    [InlineData(40, (int)SensorDataValueKind.Integer)]
    [InlineData(41, (int)SensorDataValueKind.EmptyString)]
    [InlineData(64, (int)SensorDataValueKind.String)]
    public void DedicatedGettersRemainDistinct(int selector, int kind)
    {
        Assert.True(SensorDataFieldCatalog.TryGet(selector, out SensorDataField field));
        Assert.Equal((SensorDataValueKind)kind, field.Kind);
        Assert.Equal(selector == 41, field.Key is null);
    }

    [Fact]
    public void LocationSpeedUsesMotionDataWhileKeepingTheLocationCategory()
    {
        Assert.True(SensorDataFieldCatalog.TryGet(36, out SensorDataField motion));
        Assert.True(SensorDataFieldCatalog.TryGet(63, out SensorDataField location));
        Assert.Equal(motion.Key, location.Key);
        Assert.Equal(new Guid("3f8a69a2-07c5-4e48-a965-cd797aab56d5"),
            location.Key!.Value.FormatId);
        Assert.Equal(new Guid("bfa794e4-f964-4fdb-90f6-51056bfe4b44"), location.CategoryId);
        Assert.NotEqual(motion.CategoryId, location.CategoryId);
    }

    [Fact]
    public void PropertyKeyMatchesTheNativeGuidAndUintLayout()
    {
        Assert.Equal(20, Marshal.SizeOf<SensorPropertyKey>());
        Assert.Equal((nint)0, Marshal.OffsetOf<SensorPropertyKey>(nameof(SensorPropertyKey.FormatId)));
        Assert.Equal((nint)16, Marshal.OffsetOf<SensorPropertyKey>(nameof(SensorPropertyKey.PropertyId)));
    }
}
