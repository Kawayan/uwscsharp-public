using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ModifierHotkeyStateTests
{
    [Theory]
    [InlineData(0xA4, 0xA5, 1)]
    [InlineData(0xA2, 0xA3, 2)]
    [InlineData(0xA0, 0xA1, 4)]
    [InlineData(0x5B, 0x5C, 8)]
    public void BothSidesRetainModifierUntilBothAreReleased(int left, int right, int modifier)
    {
        var state = new ModifierHotkeyState();
        Assert.Equal(modifier, state.Apply(left, true));
        Assert.Null(state.Apply(left, true));
        Assert.Null(state.Apply(right, true));
        Assert.Null(state.Apply(left, false));
        Assert.Equal(modifier, state.Modifiers);
        Assert.Null(state.Apply(right, true));
        Assert.Null(state.Apply(right, false));
        Assert.Equal(0, state.Modifiers);
        Assert.Equal(modifier, state.Apply(right, true));
    }

    [Fact]
    public void CombinationMatchesOnPressButNotWhenExtraModifierIsReleased()
    {
        var state = new ModifierHotkeyState();
        Assert.Equal(2, state.Apply(0xA2, true));
        Assert.Equal(6, state.Apply(0xA1, true));
        Assert.Equal(7, state.Apply(0xA4, true));
        Assert.Null(state.Apply(0xA4, false));
        Assert.Equal(6, state.Modifiers);
        Assert.Null(state.Apply(0xA1, true));
        Assert.Null(state.Apply(0xA1, false));
        Assert.Equal(6, state.Apply(0xA0, true));
        Assert.Equal(14, state.Apply(0x5C, true));
        Assert.Equal(15, state.Apply(0xA5, true));
    }

    [Fact]
    public void InitialHeldKeysDoNotProduceRepeatNotification()
    {
        var state = new ModifierHotkeyState();
        state.Reset([0xA2, 0xA3, 0xA0]);
        Assert.Equal(6, state.Modifiers);
        Assert.Null(state.Apply(0xA2, true));
        Assert.Null(state.Apply(0xA0, false));
        Assert.Equal(2, state.Modifiers);
        Assert.Equal(6, state.Apply(0xA1, true));
        state.Reset([]);
        Assert.Equal(0, state.Modifiers);
        Assert.Equal(4, state.Apply(0xA1, true));
    }

    [Fact]
    public void OrdinaryKeysAndUnmatchedReleasesDoNotChangeModifiers()
    {
        var state = new ModifierHotkeyState();
        Assert.Null(state.Apply(0xA2, false));
        Assert.Equal(2, state.Apply(0xA2, true));
        Assert.Null(state.Apply(65, true));
        Assert.Null(state.Apply(65, false));
        Assert.Null(state.Apply(0xA5, false));
        Assert.Equal(2, state.Modifiers);
    }

    [Fact]
    public void InvalidSnapshotPreservesPreviousState()
    {
        var state = new ModifierHotkeyState();
        state.Reset([0xA4]);
        Assert.Throws<ArgumentException>(() => state.Reset([0xA2, 65]));
        Assert.Equal(1, state.Modifiers);
    }
}
