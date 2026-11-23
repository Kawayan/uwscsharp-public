using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputLockFilterTests
{
    public static IEnumerable<object[]> ObservedCases()
    {
        foreach (int target in new[] { 0, 10, 20 })
        {
            foreach ((SelectiveInputDevices devices, bool keyboard, bool mouse) in new[]
            {
                (SelectiveInputDevices.All, true, true),
                (SelectiveInputDevices.Keyboard, true, false),
                (SelectiveInputDevices.Mouse, false, true)
            })
            {
                yield return [target, (int)devices, 0, target != 20 && keyboard, target != 20 && mouse];
                yield return [target, (int)devices, 1, false, false];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ObservedCases))]
    public void MatchesObservedKeyboardAndClickMatrix(int target,
        int devices, int extra, bool keyboard, bool mouse)
    {
        var filter = new SelectiveInputLockFilter(target, (SelectiveInputDevices)devices);
        Assert.Equal(keyboard, filter.ShouldBlockKeyboard(0, (nuint)extra, 10));
        Assert.Equal(mouse, filter.ShouldBlockMouse(0, 0x201, (nuint)extra, 10));
        Assert.Equal(mouse, filter.ShouldBlockMouse(0, 0x202, (nuint)extra, 10));
    }

    [Theory]
    [InlineData(0x200)]
    [InlineData(0xa0)]
    public void MovementIsBlockedGloballyButPassesForSpecificTarget(uint message)
    {
        var global = new SelectiveInputLockFilter(0, SelectiveInputDevices.Mouse);
        var targeted = new SelectiveInputLockFilter(10, SelectiveInputDevices.Mouse);
        Assert.True(global.ShouldBlockMouse(0, message, 0, 10));
        Assert.False(targeted.ShouldBlockMouse(0, message, 0, 10));
        Assert.False(global.ShouldBlockMouse(0, message, 1, 10));
    }

    [Fact]
    public void ChildHandleIsNotImplicitlyConvertedToParent()
    {
        var child = new SelectiveInputLockFilter(11, SelectiveInputDevices.All);
        Assert.False(child.ShouldBlockKeyboard(0, 0, 10));
        Assert.False(child.ShouldBlockMouse(0, 0x201, 0, 10));
    }

    [Fact]
    public void OutsideNotificationAllowsOneLeftUpWithoutClearingOnOtherButtons()
    {
        var filter = new SelectiveInputLockFilter(10, SelectiveInputDevices.Mouse);
        Assert.True(filter.ShouldBlockMouse(0, 0x202, 0, 10));
        Assert.False(filter.ShouldBlockMouse(0, 0x201, 0, 20));
        Assert.True(filter.ShouldBlockMouse(0, 0x205, 0, 10));
        Assert.True(filter.ShouldBlockMouse(0, 0x201, 0, 10));
        Assert.False(filter.ShouldBlockMouse(0, 0x202, 0, 10));
        Assert.True(filter.ShouldBlockMouse(0, 0x202, 0, 10));
    }

    [Theory]
    [InlineData(-1, 0x201, 0)]
    [InlineData(1, 0x201, 0)]
    [InlineData(0, 0x201, 1)]
    [InlineData(0, 0x200, 0)]
    [InlineData(0, 0xa0, 0)]
    public void IgnoredNotificationDoesNotArmLeftUp(int code, uint message, int extra)
    {
        var filter = new SelectiveInputLockFilter(10, SelectiveInputDevices.All);
        Assert.False(filter.ShouldBlockMouse(code, message, (nuint)extra, 20));
        Assert.True(filter.ShouldBlockMouse(0, 0x202, 0, 10));
    }

    [Fact]
    public void BypassedLeftUpDoesNotConsumePendingAllowance()
    {
        var filter = new SelectiveInputLockFilter(10, SelectiveInputDevices.Mouse);
        Assert.False(filter.ShouldBlockMouse(0, 0x201, 0, 20));
        Assert.False(filter.ShouldBlockMouse(0, 0x202, 1, 10));
        Assert.False(filter.ShouldBlockMouse(0, 0x202, 0, 10));
        Assert.True(filter.ShouldBlockMouse(0, 0x202, 0, 10));
    }

    [Fact]
    public void DisabledDevicesAndNonActionCodesPass()
    {
        var disabled = new SelectiveInputLockFilter(0, SelectiveInputDevices.None);
        Assert.False(disabled.ShouldBlockKeyboard(0, 0, 10));
        Assert.False(disabled.ShouldBlockMouse(0, 0x201, 0, 10));
        var global = new SelectiveInputLockFilter(0, SelectiveInputDevices.All);
        Assert.False(global.ShouldBlockKeyboard(-1, 0, 10));
        Assert.False(global.ShouldBlockKeyboard(1, 0, 10));
        Assert.True(global.ShouldBlockKeyboard(0, nuint.MaxValue, 10));
    }

    [Fact]
    public void InstancesDoNotShareLeftUpState()
    {
        var first = new SelectiveInputLockFilter(10, SelectiveInputDevices.Mouse);
        Assert.False(first.ShouldBlockMouse(0, 0x201, 0, 20));
        var second = new SelectiveInputLockFilter(10, SelectiveInputDevices.Mouse);
        Assert.True(second.ShouldBlockMouse(0, 0x202, 0, 10));
        Assert.False(first.ShouldBlockMouse(0, 0x202, 0, 10));
    }

    [Fact]
    public void UnknownInternalDeviceBitsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SelectiveInputLockFilter(0, (SelectiveInputDevices)4));
    }
}
