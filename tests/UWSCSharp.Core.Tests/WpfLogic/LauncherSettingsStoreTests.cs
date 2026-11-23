using System.Text;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LauncherSettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-launcher-settings-" + Guid.NewGuid());

    private string SettingsPath => Path.Combine(directory, "launcher.json");

    [Fact]
    public void RoundTripKeepsSlotsKindsUnicodeEmptyArgumentsAndShortcut()
    {
        LauncherCommand command = LauncherSettingsTests.Command(["", "空白 と日本語", "\"quoted\""]);
        LauncherGroup group = new LauncherGroup("階層", []).WithSlot(34, command);
        LauncherSettings settings = new LauncherSettings(shortcut: new ScriptHotkey(87, 2))
            .WithSlot(34, group)
            .WithSlot(2, new LauncherCommand("EXE", "app.EXE",
                LauncherCommandKind.Executable, [""], directory))
            .WithSlot(3, new LauncherCommand("文書", "document.txt",
                LauncherCommandKind.Document, [], directory));
        Assert.All(LauncherSettingsStore.Load(SettingsPath).Slots, Assert.Null);
        LauncherSettingsStore.Save(SettingsPath, settings);
        LauncherSettings loaded = LauncherSettingsStore.Load(SettingsPath);
        Assert.Equal(settings.Shortcut, loaded.Shortcut);
        Assert.Null(loaded.Slots[0]);
        LauncherGroup loadedGroup = Assert.IsType<LauncherGroup>(loaded.Slots[34]);
        Assert.Equal("階層", loadedGroup.Title);
        Assert.Null(loadedGroup.Slots[0]);
        LauncherCommand loadedCommand = Assert.IsType<LauncherCommand>(loadedGroup.Slots[34]);
        Assert.Equal(command.Path, loadedCommand.Path);
        Assert.Equal(command.Arguments, loadedCommand.Arguments);
        Assert.Equal(LauncherCommandKind.Executable,
            Assert.IsType<LauncherCommand>(loaded.Slots[2]).Kind);
        Assert.Equal(LauncherCommandKind.Document,
            Assert.IsType<LauncherCommand>(loaded.Slots[3]).Kind);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"slots\":[]}")]
    [InlineData("{\"version\":1,\"slots\":null}")]
    [InlineData("{\"version\":1,\"slots\":[],\"typo\":true}")]
    [InlineData("{\"version\":1,\"version\":1,\"slots\":[]}")]
    [InlineData("{\"version\":1,\"slots\":[{\"kind\":\"unknown\",\"title\":\"x\"}]}")]
    [InlineData("{\"version\":1,\"slots\":[{\"kind\":\"group\",\"title\":\"x\",\"slots\":[{\"kind\":\"group\",\"title\":\"y\",\"slots\":[]}]}]}")]
    [InlineData("{\"version\":1,\"slots\":[{\"kind\":\"script\",\"title\":\"x\",\"path\":\"relative.uws\",\"arguments\":[]}]}")]
    [InlineData("{\"version\":1,\"slots\":[],\"shortcut\":{\"virtualKey\":122,\"modifiers\":7}}")]
    [InlineData("{\"version\":1,\"slots\":[],\"shortcut\":{\"virtualKey\":87}}")]
    public void MalformedSettingsAreRejectedWithoutRewritingOriginal(string json)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(SettingsPath);
        Assert.Throws<InvalidDataException>(() => LauncherSettingsStore.Load(SettingsPath));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void ReadLockFailureKeepsSettingsAndAllowsRetry()
    {
        LauncherSettingsStore.Save(SettingsPath,
            new LauncherSettings().WithSlot(0, LauncherSettingsTests.Command([])));
        byte[] original = File.ReadAllBytes(SettingsPath);
        using (File.Open(SettingsPath, FileMode.Open, FileAccess.Read,
            FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => LauncherSettingsStore.Load(SettingsPath));
        }

        Assert.NotNull(LauncherSettingsStore.Load(SettingsPath).Slots[0]);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void NullArgumentsAndMixedGroupFieldsAreRejected()
    {
        LauncherSettingsStore.Save(SettingsPath,
            new LauncherSettings().WithSlot(0, LauncherSettingsTests.Command([])));
        string json = File.ReadAllText(SettingsPath);
        File.WriteAllText(SettingsPath, json.Replace("\"arguments\":[]", "\"arguments\":[null]"));
        Assert.Throws<InvalidDataException>(() => LauncherSettingsStore.Load(SettingsPath));
        File.WriteAllText(SettingsPath, json.Replace("\"kind\":\"script\"",
            "\"kind\":\"group\",\"slots\":[]"));
        Assert.Throws<InvalidDataException>(() => LauncherSettingsStore.Load(SettingsPath));
    }

    [Fact]
    public void Utf8BomAndExactReadLimitAreAcceptedButExcessIsRejected()
    {
        Directory.CreateDirectory(directory);
        byte[] json = Encoding.UTF8.GetBytes("{\"version\":1,\"slots\":[]}");
        byte[] bytes = new byte[LauncherSettingsStore.MaximumBytes];
        Array.Fill(bytes, (byte)' ');
        new byte[] { 0xef, 0xbb, 0xbf }.CopyTo(bytes, 0);
        json.CopyTo(bytes, 3);
        File.WriteAllBytes(SettingsPath, bytes);
        Assert.All(LauncherSettingsStore.Load(SettingsPath).Slots, Assert.Null);
        using (var file = new FileStream(SettingsPath, FileMode.Append))
        {
            file.WriteByte((byte)' ');
        }

        Assert.Throws<InvalidDataException>(() => LauncherSettingsStore.Load(SettingsPath));
        Assert.Equal(LauncherSettingsStore.MaximumBytes + 1, new FileInfo(SettingsPath).Length);
    }

    [Fact]
    public void SaveFailureKeepsOldBytesAndRemovesTemporaryFile()
    {
        LauncherSettingsStore.Save(SettingsPath, new LauncherSettings());
        byte[] old = File.ReadAllBytes(SettingsPath);
        Exception? failure;
        using (var locked = new FileStream(SettingsPath, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            failure = Record.Exception(() => LauncherSettingsStore.Save(SettingsPath,
                new LauncherSettings().WithSlot(0, LauncherSettingsTests.Command([]))));
        }

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(old, File.ReadAllBytes(SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public void OversizedSaveDoesNotReplaceExistingSettings()
    {
        LauncherSettingsStore.Save(SettingsPath, new LauncherSettings());
        byte[] old = File.ReadAllBytes(SettingsPath);
        LauncherSettings oversized = new LauncherSettings().WithSlot(0,
            LauncherSettingsTests.Command([new string('a', LauncherSettingsStore.MaximumBytes)]));
        Assert.Throws<InvalidDataException>(() => LauncherSettingsStore.Save(SettingsPath, oversized));
        Assert.Equal(old, File.ReadAllBytes(SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Fact]
    public void ExplicitResetBacksUpInvalidBytesAndCreatesEmptyValidSettings()
    {
        Directory.CreateDirectory(directory);
        byte[] invalid = Encoding.UTF8.GetBytes("{invalid 設定");
        File.WriteAllBytes(SettingsPath, invalid);
        string backup = Assert.IsType<string>(LauncherSettingsStore.ResetWithBackup(SettingsPath));
        Assert.Equal(directory, Path.GetDirectoryName(backup));
        Assert.Equal(invalid, File.ReadAllBytes(backup));
        Assert.All(LauncherSettingsStore.Load(SettingsPath).Slots, Assert.Null);
        Assert.Null(LauncherSettingsStore.Load(SettingsPath).Shortcut);
    }

    [Fact]
    public void CapacityRoundTripPreservesAll1225SlotPositions()
    {
        var groups = new LauncherGroup[35];
        for (int group = 0; group < groups.Length; group++)
        {
            groups[group] = new LauncherGroup($"階層{group}",
                Enumerable.Range(0, 35).Select(slot => new LauncherCommand(
                    $"項目{group}-{slot}", $"script{group}-{slot}.uws",
                    LauncherCommandKind.Script, [$"{group}-{slot}"], directory)));
        }

        LauncherSettingsStore.Save(SettingsPath, new LauncherSettings(groups));
        LauncherSettings loaded = LauncherSettingsStore.Load(SettingsPath);
        for (int group = 0; group < 35; group++)
        {
            LauncherGroup entry = Assert.IsType<LauncherGroup>(loaded.Slots[group]);
            Assert.Equal($"階層{group}", entry.Title);
            for (int slot = 0; slot < 35; slot++)
            {
                LauncherCommand command = Assert.IsType<LauncherCommand>(entry.Slots[slot]);
                Assert.Equal($"項目{group}-{slot}", command.Title);
                Assert.Equal(Path.Combine(directory, $"script{group}-{slot}.uws"), command.Path);
                Assert.Equal($"{group}-{slot}", Assert.Single(command.Arguments));
            }
        }
    }

    [Fact]
    public void FailedResetKeepsInvalidOriginalInsteadOfApplyingDefaults()
    {
        Directory.CreateDirectory(directory);
        byte[] original = [0xff, 0, 0x81, 0xfe];
        File.WriteAllBytes(SettingsPath, original);
        Exception? failure;
        using (var locked = new FileStream(SettingsPath, FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            failure = Record.Exception(() => LauncherSettingsStore.ResetWithBackup(SettingsPath));
        }

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Throws<InvalidDataException>(() => LauncherSettingsStore.Load(SettingsPath));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void InvalidEscapedUtf16IsRejectedWithoutRewritingOriginal(int codeUnit)
    {
        Directory.CreateDirectory(directory);
        string json = $$"""
            {"version":1,"slots":[{"kind":"group","title":"\u{{codeUnit:x4}}","slots":[]}]}
            """;
        File.WriteAllText(SettingsPath, json, new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(SettingsPath);
        Assert.Throws<InvalidDataException>(() => LauncherSettingsStore.Load(SettingsPath));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void ValidSurrogatePairsAndLiteralReplacementCharactersArePreserved()
    {
        string supplementary = char.ConvertFromUtf32(0x1f600);
        var command = new LauncherCommand(supplementary, supplementary + ".uws",
            LauncherCommandKind.Script, [supplementary, "\ufffd"], directory);
        LauncherSettingsStore.Save(SettingsPath, new LauncherSettings().WithSlot(0, command));
        LauncherCommand loaded = Assert.IsType<LauncherCommand>(
            LauncherSettingsStore.Load(SettingsPath).Slots[0]);
        Assert.Equal(command.Title, loaded.Title);
        Assert.Equal(command.Path, loaded.Path);
        Assert.Equal(command.Arguments, loaded.Arguments);
    }

    [Fact]
    public void RootPathMetadataRoundTripsWithoutRequiringTargetExistence()
    {
        string root = Path.GetPathRoot(directory)!;
        var command = new LauncherCommand("ルートを指す登録", root,
            LauncherCommandKind.Document, [], directory);
        LauncherSettingsStore.Save(SettingsPath, new LauncherSettings().WithSlot(0, command));
        LauncherCommand loaded = Assert.IsType<LauncherCommand>(
            LauncherSettingsStore.Load(SettingsPath).Slots[0]);
        Assert.Equal(root, loaded.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
