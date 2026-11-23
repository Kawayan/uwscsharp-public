using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputEmergencyChordTests
{
    [Fact]
    public void SnapshotDoesNotForgetAPressObservedDuringInitialization()
    {
        var chord = new SelectiveInputEmergencyChord();
        Assert.Equal((false, false), Key(chord, 0xA2));
        chord.Reset([], mergeObserved: true);
        Key(chord, 0xA4);
        Key(chord, 0xA0);
        Assert.Equal((false, false), Key(chord, 122));
        foreach (int key in new[] { 0xA2, 0xA4, 0xA0, 122 })
        {
            Key(chord, key, false);
        }
        Key(chord, 0xA2);
        Key(chord, 0xA4);
        Key(chord, 0xA0);
        Assert.Equal((true, true), Key(chord, 122));
    }

    [Fact]
    public void ConcurrentModifiersAreNotLost()
    {
        var chord = new SelectiveInputEmergencyChord();
        for (int attempt = 0; attempt < 100; attempt++)
        {
            chord.Reset([]);
            Parallel.Invoke(() => Key(chord, 0xA2),
                () => Key(chord, 0xA4), () => Key(chord, 0xA0));
            Assert.Equal((true, true), Key(chord, 122));
            Assert.Equal((false, true), Key(chord, 122, false));
        }
    }

    [Theory]
    [InlineData(0xA2)]
    [InlineData(122)]
    public void SnapshotDoesNotReinstateAReleaseObservedDuringInitialization(
        int sampledKey)
    {
        var chord = new SelectiveInputEmergencyChord();
        Assert.Equal((false, false), Key(chord, sampledKey, false));
        chord.Reset([sampledKey], mergeObserved: true);

        Key(chord, 0xA2);
        Key(chord, 0xA4);
        Key(chord, 0xA0);
        Assert.Equal((true, true), Key(chord, 122));
    }

    [Theory]
    [InlineData(0xA2, 0xA4, 0xA0)]
    [InlineData(0xA2, 0xA4, 0xA1)]
    [InlineData(0xA2, 0xA5, 0xA0)]
    [InlineData(0xA2, 0xA5, 0xA1)]
    [InlineData(0xA3, 0xA4, 0xA0)]
    [InlineData(0xA3, 0xA4, 0xA1)]
    [InlineData(0xA3, 0xA5, 0xA0)]
    [InlineData(0xA3, 0xA5, 0xA1)]
    public void EitherSideTriggersOnlyOnANewF11Press(int control, int alt, int shift)
    {
        var chord = new SelectiveInputEmergencyChord();
        chord.Reset([]);
        Assert.Equal((false, false), Key(chord, control));
        Assert.Equal((false, false), Key(chord, alt));
        Assert.Equal((false, false), Key(chord, shift));
        Assert.Equal((true, true), Key(chord, 122));
        Assert.Equal((false, true), Key(chord, 122));
        Assert.Equal((false, false), Key(chord, control, false));
        Assert.Equal((false, true), Key(chord, 122, false));
        Key(chord, control);
        Assert.Equal((true, true), Key(chord, 122));
    }

    [Theory]
    [InlineData(0xA2)]
    [InlineData(0xA3)]
    [InlineData(0xA4)]
    [InlineData(0xA5)]
    [InlineData(0xA0)]
    [InlineData(0xA1)]
    [InlineData(122)]
    public void InitialHeldKeysRequireAllRequiredKeysToBeReleased(int held)
    {
        var chord = new SelectiveInputEmergencyChord();
        chord.Reset([held]);
        Key(chord, 0xA2);
        Key(chord, 0xA4);
        Key(chord, 0xA0);
        Assert.Equal((false, false), Key(chord, 122));
        foreach (int key in new[] { held, 122, 0xA2, 0xA4, 0xA0 })
        {
            Key(chord, key, false);
        }

        Key(chord, 0xA2);
        Key(chord, 0xA4);
        Key(chord, 0xA0);
        Assert.Equal((true, true), Key(chord, 122));
    }

    [Theory]
    [InlineData(0x5B)]
    [InlineData(0x5C)]
    public void WindowsKeyAndAlreadyHeldF11DoNotTrigger(int windows)
    {
        SelectiveInputEmergencyChord chord = Armed();
        Key(chord, windows);
        Assert.Equal((false, false), Key(chord, 122));
        Key(chord, windows, false);
        Assert.Equal((false, false), Key(chord, 122));
        Key(chord, 122, false);
        Assert.Equal((true, true), Key(chord, 122));
    }

    [Theory]
    [InlineData(0x10u, 0ul)]
    [InlineData(0x02u, 0ul)]
    [InlineData(0x12u, 0ul)]
    [InlineData(0u, 1ul)]
    public void InjectedEventsNeitherTriggerNorChangePhysicalState(uint flags, ulong extra)
    {
        SelectiveInputEmergencyChord chord = Armed();
        Assert.Equal((false, false), chord.Apply(122, 0, flags, (nuint)extra, 0x100));
        Assert.Equal((false, false), chord.Apply(0xA2, 0, flags, (nuint)extra, 0x101));
        Assert.Equal((true, true), Key(chord, 122));
        Assert.Equal((false, false), chord.Apply(122, 0, flags, (nuint)extra, 0x101));
        Assert.Equal((false, true), Key(chord, 122));
        Assert.Equal((false, true), Key(chord, 122, false));
    }

    [Fact]
    public void GenericModifierCodesAndSystemMessagesAreNormalized()
    {
        var chord = new SelectiveInputEmergencyChord();
        chord.Reset([]);
        chord.Apply(0x11, 0, 1, 0, 0x100);
        chord.Apply(0x12, 0, 1, 0, 0x104);
        chord.Apply(0x10, 0x36, 0, 0, 0x100);
        Assert.Equal((false, false), chord.Apply(122, 0, 0, 0, 0x200));
        Assert.Equal((true, true), chord.Apply(122, 0, 0, 0, 0x104));
        Assert.Equal((false, true), chord.Apply(122, 0, 0, 0, 0x105));
    }

    private static SelectiveInputEmergencyChord Armed()
    {
        var chord = new SelectiveInputEmergencyChord();
        chord.Reset([]);
        Key(chord, 0xA2);
        Key(chord, 0xA4);
        Key(chord, 0xA0);
        return chord;
    }

    private static (bool Request, bool Consume) Key(
        SelectiveInputEmergencyChord chord, int key, bool pressed = true) =>
        chord.Apply((uint)key, 0, 0, 0, pressed ? 0x100u : 0x101u);
}
