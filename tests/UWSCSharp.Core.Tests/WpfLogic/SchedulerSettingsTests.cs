using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerSettingsTests
{
    internal static SchedulerEntry Entry(SchedulerCondition? condition = null,
        Guid? id = null, IEnumerable<string>? arguments = null) =>
        new(id ?? Guid.NewGuid(), "日本語", "missing-" + Guid.NewGuid().ToString("N") + ".UWS",
            arguments ?? ["", "a b", "\"引用\""],
            SchedulerExecutionMode.SeparateProcess, condition ?? new SchedulerStartupCondition(),
            Path.GetFullPath(Path.GetTempPath()));

    [Fact]
    public void DefaultsAreEmptyAndInactive()
    {
        var settings = new SchedulerSettings();
        Assert.Equal(40, settings.Slots.Count);
        Assert.All(settings.Slots, Assert.Null);
        Assert.False(settings.Enabled);
        Assert.False(settings.Beep);
        Assert.False(settings.WakeUp);
    }

    [Fact]
    public void MoveCopyAndResizeKeepIdentityAndSnapshots()
    {
        string[] arguments = ["", "元の引数"];
        SchedulerEntry entry = Entry(arguments: arguments);
        SchedulerEntry?[] slots = [entry];
        var settings = new SchedulerSettings(slots, 400, true, true, true);
        slots[0] = null;
        arguments[1] = "変更";
        SchedulerSettings moved = settings.MoveSlot(0, 399);
        Assert.Same(entry, settings.Slots[0]);
        Assert.Same(entry, moved.Slots[399]);
        Assert.Equal("元の引数", entry.Arguments[1]);
        Assert.Equal(entry.Id, moved.Slots[399]!.Id);
        Assert.Throws<InvalidDataException>(() => moved.Resize(40));
        SchedulerSettings resized = moved.MoveSlot(399, 0).Resize(40);
        Assert.True(resized.Enabled && resized.Beep && resized.WakeUp);
        SchedulerEntry copied = entry.Copy();
        Assert.NotEqual(entry.Id, copied.Id);
        Assert.Equal(entry.Arguments, copied.Arguments);
        Assert.Equal(entry.Path, copied.Path);
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
            entry.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void DuplicateIdentityAndCapacityOverflowAreRejected()
    {
        SchedulerEntry entry = Entry();
        Assert.Throws<InvalidDataException>(() => new SchedulerSettings([entry, entry]));
        Assert.Throws<InvalidDataException>(() => new SchedulerSettings(new SchedulerEntry?[41]));
        Assert.Throws<InvalidDataException>(() => new SchedulerSettings(capacity: 39));
        Assert.Throws<InvalidDataException>(() => new SchedulerSettings(capacity: 401));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SchedulerSettings().MoveSlot(0, 40));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 61)]
    [InlineData(1, 25)]
    [InlineData(99, 1)]
    public void IntervalLimitsRejectInvalidValues(int unit, int value)
    {
        Assert.Throws<InvalidDataException>(() =>
            new SchedulerIntervalCondition((SchedulerIntervalUnit)unit, value));
    }

    [Fact]
    public void DaySelectionsAreBoundedUniqueAndCopied()
    {
        int[] days = [0, 6];
        var condition = new SchedulerTimeCondition(new TimeOnly(23, 59),
            SchedulerDayKind.Weekdays, days);
        days[0] = 4;
        Assert.Equal([0, 6], condition.Days);
        Assert.Throws<InvalidDataException>(() => new SchedulerTimeCondition(
            new TimeOnly(0, 0), SchedulerDayKind.Weekdays, []));
        Assert.Throws<InvalidDataException>(() => new SchedulerTimeCondition(
            new TimeOnly(0, 0), SchedulerDayKind.Weekdays, [0, 0]));
        Assert.Throws<InvalidDataException>(() => new SchedulerTimeCondition(
            new TimeOnly(0, 0), SchedulerDayKind.MonthDays, [0]));
        Assert.Throws<InvalidDataException>(() => new SchedulerTimeCondition(
            new TimeOnly(0, 0), SchedulerDayKind.EveryDay, [1]));
        Assert.Throws<InvalidDataException>(() => new SchedulerOneShotCondition(
            new DateOnly(2000, 1, 1), new TimeOnly(0, 0, 1)));
    }

    [Fact]
    public void TextAndScriptTypeAreValidatedWithoutOpeningTargets()
    {
        Assert.Throws<InvalidDataException>(() => Entry(
            new SchedulerExpressionCondition(new string('\ud800', 1))));
        Assert.Throws<InvalidDataException>(() => Entry(arguments: ["a\0b"]));
        Assert.Throws<InvalidDataException>(() => new SchedulerWindowCondition("", ""));
        Assert.Throws<InvalidDataException>(() => new SchedulerEntry(Guid.NewGuid(), "x", "app.exe",
            [], SchedulerExecutionMode.SameProcess, new SchedulerStartupCondition(), Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => Entry(id: Guid.Empty));
    }
}
