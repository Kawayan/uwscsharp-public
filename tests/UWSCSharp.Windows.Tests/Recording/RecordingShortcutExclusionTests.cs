using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class RecordingShortcutExclusionTests
{
    [Fact]
    public void DifferentModifierGroupsAreExcludedWithoutRecordingTheirModifiers()
    {
        var buffer = new RecordingBuffer([119], 1,
            additionalControls: [new(65, 2), new(112, 12)]);
        Stroke(buffer, [18], 119);
        Stroke(buffer, [162], 65);
        Stroke(buffer, [91, 160], 112);
        buffer.Complete();
        Assert.Empty(buffer.Events);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(18)]
    public void OrdinaryKeyAndOtherModifierCombinationRemainRecorded(int modifier)
    {
        var buffer = new RecordingBuffer([119], 1,
            additionalControls: [new(65, 2)]);
        Stroke(buffer, modifier == 0 ? [] : [modifier], 65);
        buffer.Complete();
        Assert.Equal(modifier == 0 ? [65, 65] : new[] { modifier, 65, 65, modifier },
            buffer.Events.Select(input => input.Code));
        Assert.Equal(modifier == 0 ? [1, 2] : new[] { 1, 1, 2, 2 },
            buffer.Events.Select(input => input.State));
    }

    [Fact]
    public void ModifierSharedWithNormalInputIsPreservedAndControlsAreCopied()
    {
        ScriptHotkey[] controls = [new(65, 2)];
        var buffer = new RecordingBuffer([], 0, additionalControls: controls);
        controls[0] = new(66, 2);
        buffer.Key(17, 1, 0, false);
        buffer.Key(66, 1, 1, false);
        buffer.Key(66, 2, 2, false);
        buffer.Key(65, 1, 3, false);
        buffer.Key(65, 2, 4, false);
        buffer.Key(17, 2, 5, false);
        buffer.Complete();
        Assert.Equal([17, 66, 66, 17], buffer.Events.Select(input => input.Code));
    }

    private static void Stroke(RecordingBuffer buffer, int[] modifiers, int key)
    {
        foreach (int modifier in modifiers)
        {
            buffer.Key(modifier, 1, 0, false);
        }

        buffer.Key(key, 1, 1, false);
        buffer.Key(key, 2, 2, false);
        foreach (int modifier in modifiers.Reverse())
        {
            buffer.Key(modifier, 2, 3, false);
        }
    }
}
