using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ModifierHotkeyBindingsTests
{
    [Fact]
    public void OnlyExactCombinationIsDeliveredAndRemovedIdCannotReturn()
    {
        var bindings = new ModifierHotkeyBindings();
        Assert.True(bindings.Register(10, 2));
        Assert.True(bindings.Register(20, 6));
        Assert.Equal(10, bindings.Apply(0xA2, true));
        Assert.Equal(20, bindings.Apply(0xA0, true));
        Assert.Null(bindings.Apply(0xA4, true));
        Assert.Null(bindings.Apply(0xA4, false));
        Assert.True(bindings.Remove(20));
        Assert.False(bindings.Contains(20));
        Assert.Null(bindings.Apply(0xA0, false));
        Assert.Null(bindings.Apply(0xA0, true));
        Assert.True(bindings.Register(30, 6));
        Assert.Null(bindings.Apply(0xA0, true));
        Assert.Null(bindings.Apply(0xA0, false));
        Assert.Equal(30, bindings.Apply(0xA0, true));
    }

    [Fact]
    public void InvalidAndDuplicateRegistrationPreservesOriginalBindings()
    {
        var bindings = new ModifierHotkeyBindings();
        Assert.False(bindings.Register(10, 0));
        Assert.False(bindings.Register(10, 16));
        Assert.True(bindings.Register(10, 2));
        Assert.False(bindings.Register(10, 4));
        Assert.False(bindings.Register(20, 2));
        Assert.False(bindings.Remove(20));
        Assert.Equal(1, bindings.Count);
        bindings.Reset([0xA3]);
        Assert.Null(bindings.Apply(0xA3, true));
        Assert.Null(bindings.Apply(0xA3, false));
        Assert.Equal(10, bindings.Apply(0xA2, true));
    }

    [Theory]
    [InlineData(0x10, 0x2A, 0, 0xA0)]
    [InlineData(0x10, 0x36, 0, 0xA1)]
    [InlineData(0x10, 0, 0, 0)]
    [InlineData(0x11, 0x1D, 0, 0xA2)]
    [InlineData(0x11, 0x1D, 1, 0xA3)]
    [InlineData(0x12, 0x38, 0, 0xA4)]
    [InlineData(0x12, 0x38, 1, 0xA5)]
    [InlineData(0xA0, 0, 0x10, 0xA0)]
    [InlineData(0xA3, 0, 0, 0xA3)]
    [InlineData(0x5B, 0, 0, 0x5B)]
    [InlineData(0x5C, 0, 0, 0x5C)]
    [InlineData(65, 0, 0, 0)]
    public void KeyboardFieldsNormalizePhysicalModifiers(uint key, uint scan, uint flags, int expected)
    {
        Assert.Equal(expected, ModifierHotkeyBindings.Normalize(key, scan, flags));
    }
}
