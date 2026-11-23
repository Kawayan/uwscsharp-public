using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
public sealed class KeyboardEncodingTests
{
    [Theory]
    [InlineData(65, 65, false)]
    [InlineData(0xa1, 16, true)]
    [InlineData(0xa3, 17, true)]
    [InlineData(0xa5, 18, true)]
    [InlineData(901, 13, true)]
    [InlineData(0xa0, 0xa0, false)]
    [InlineData(0xa2, 0xa2, false)]
    [InlineData(0xa4, 0xa4, false)]
    [InlineData(37, 37, true)]
    [InlineData(0x3042, 0x3042, false)]
    public void EncodingMapsOriginalKeyBeforeNormalization(
        int raw, int expectedKey, bool extended)
    {
        uint mapped = 0;
        var encoding = KeyboardEncoding.Create(raw,
            key =>
            {
                mapped = key;
                return 0x123;
            });

        Assert.Equal((uint)raw, mapped);
        Assert.Equal(expectedKey, encoding.Key);
        Assert.Equal(extended, encoding.Extended);
        uint parameter = 0x00230001u
            | (extended ? 0x01000000u : 0);
        Assert.Equal(0x100u, encoding.DirectMessage(false));
        Assert.Equal(0x101u, encoding.DirectMessage(true));
        Assert.Equal(parameter, encoding.DirectParameter(false));
        Assert.Equal(parameter | 0xc0000000u,
            encoding.DirectParameter(true));
    }

    [Fact]
    public void DirectModifiersPreserveRawSideAndClearOnRelease()
    {
        var state = new DirectKeyboardState();
        Assert.True(state.ApplyModifier(0xa1, false));
        Assert.True(state.ApplyModifier(17, false));
        Assert.True(state.ApplyModifier(0xa5, false));
        Assert.Equal(0xa1, state.Shift);
        Assert.Equal(17, state.Control);
        Assert.Equal(0xa5, state.Alt);
        Assert.False(state.ApplyModifier(0xa0, false));
        Assert.False(state.ApplyModifier(0xa2, false));
        Assert.False(state.ApplyModifier(0xa4, false));
        Assert.False(state.ApplyModifier(65, false));
        Assert.Equal(0xa1, state.Shift);
        Assert.True(state.ApplyModifier(16, true));
        Assert.True(state.ApplyModifier(0xa3, true));
        Assert.True(state.ApplyModifier(18, true));
        Assert.Equal(0, state.Shift);
        Assert.Equal(0, state.Control);
        Assert.Equal(0, state.Alt);
    }

    [Fact]
    public void DirectModifierStateIsIndependentPerSession()
    {
        var first = new DirectKeyboardState();
        var second = new DirectKeyboardState();
        first.ApplyModifier(16, false);
        Assert.Equal(0, second.Shift);
    }
}
