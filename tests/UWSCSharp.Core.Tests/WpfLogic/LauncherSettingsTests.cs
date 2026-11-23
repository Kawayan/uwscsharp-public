using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LauncherSettingsTests
{
    [Fact]
    public void SlotsGroupsAndArgumentsAreSnapshotsAndMovesPreserveEmptyPositions()
    {
        string[] arguments = ["", "日本語 with spaces", "\"literal\""];
        LauncherCommand command = Command(arguments);
        LauncherCommand?[] children = [null, command];
        var group = new LauncherGroup("同じタイトル", children);
        LauncherEntry?[] root = [command, null, group];
        var settings = new LauncherSettings(root);
        arguments[1] = "changed";
        children[1] = null;
        root[0] = null;

        Assert.Equal(35, settings.Slots.Count);
        Assert.Equal(35, group.Slots.Count);
        Assert.Same(command, settings.Slots[0]);
        Assert.Same(command, group.Slots[1]);
        Assert.Equal(["", "日本語 with spaces", "\"literal\""], command.Arguments);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "app", "相対 script.uws"), command.Path);
        LauncherSettings moved = settings.MoveSlot(2, 0);
        Assert.Same(group, moved.Slots[0]);
        Assert.Same(command, moved.Slots[1]);
        Assert.Null(moved.Slots[2]);
        Assert.Same(group, settings.Slots[2]);
        Assert.Null(group.WithSlot(1, null).Slots[1]);
        Assert.Same(command, group.MoveSlot(1, 34).Slots[34]);
        Assert.Same(command, group.Slots[1]);
    }

    [Fact]
    public void CapacityAllowsAll1225CommandsAndRejectsExcessSlots()
    {
        var group = new LauncherGroup("階層", Enumerable.Repeat(Command([]), 35));
        var settings = new LauncherSettings(Enumerable.Repeat(group, 35));
        Assert.Equal(1225, settings.Slots.Cast<LauncherGroup>().Sum(item => item.Slots.Count));
        Assert.Throws<InvalidDataException>(() =>
            new LauncherSettings(Enumerable.Repeat(group, 36)));
        Assert.Throws<InvalidDataException>(() =>
            new LauncherGroup("階層", Enumerable.Repeat(Command([]), 36)));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.MoveSlot(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => settings.WithSlot(35, null));
    }

    [Theory]
    [InlineData(0, 87)]
    [InlineData(16, 87)]
    [InlineData(2, 16)]
    [InlineData(2, 136)]
    [InlineData(7, 122)]
    public void InvalidOrEmergencyShortcutsAreRejected(int modifiers, int key) =>
        Assert.Throws<InvalidDataException>(() =>
            new LauncherSettings(shortcut: new ScriptHotkey(key, modifiers)));

    [Fact]
    public void ShortcutIsOptionalAndConflictsAreCheckedWithoutChangingSnapshot()
    {
        Assert.Null(new LauncherSettings().Shortcut);
        var key = new ScriptHotkey(87, 2);
        var settings = new LauncherSettings(shortcut: key);
        settings.ValidateShortcutAvailability([new ScriptHotkey(87, 1)]);
        Assert.Throws<InvalidDataException>(() => settings.ValidateShortcutAvailability([key]));
        Assert.Equal(key, settings.Shortcut);
        Assert.Equal(new ScriptHotkey(135, 8),
            new LauncherSettings(shortcut: new ScriptHotkey(135, 8)).Shortcut);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void EmptyTitlesAreRejected(string title) =>
        Assert.Throws<ArgumentException>(() => new LauncherGroup(title, []));

    [Fact]
    public void InvalidStringsKindsAndDocumentArgumentsAreRejected()
    {
        string baseDirectory = Path.GetTempPath();
        Assert.Throws<InvalidDataException>(() => new LauncherGroup("NUL\0", []));
        Assert.Throws<InvalidDataException>(() => Command(["NUL\0"]));
        Assert.Throws<ArgumentNullException>(() => Command([null!]));
        Assert.Throws<InvalidDataException>(() => new LauncherCommand("wrong", "file.uws",
            LauncherCommandKind.Executable, [], baseDirectory));
        Assert.Throws<InvalidDataException>(() => new LauncherCommand("doc", "file.txt",
            LauncherCommandKind.Document, ["argument"], baseDirectory));
        Assert.Throws<InvalidDataException>(() => new LauncherCommand("nul", "file\0.uws",
            LauncherCommandKind.Script, [], baseDirectory));
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void InvalidUtf16IsRejectedBeforeJsonCanReplaceCharacters(int codeUnit)
    {
        string invalid = "x" + (char)codeUnit;
        Assert.Throws<InvalidDataException>(() => new LauncherGroup(invalid, []));
        Assert.Throws<InvalidDataException>(() => Command([invalid]));
        Assert.Throws<InvalidDataException>(() => new LauncherCommand("path", invalid + ".uws",
            LauncherCommandKind.Script, [], Path.GetTempPath()));
        Assert.Throws<InvalidDataException>(() => new LauncherCommand("base", "valid.uws",
            LauncherCommandKind.Script, [], Path.Combine(Path.GetTempPath(), invalid)));
    }

    internal static LauncherCommand Command(IEnumerable<string> arguments) =>
        new("同じタイトル", "相対 script.uws", LauncherCommandKind.Script,
            arguments, Path.Combine(Path.GetTempPath(), "app"));
}
