using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LauncherSettingsControllerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-launcher-controller-" + Guid.NewGuid());

    private string SettingsPath => Path.Combine(directory, "launcher.json");

    [Fact]
    public void InvalidStartupBlocksSaveUntilExplicitBackupReset()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(SettingsPath, "invalid settings");
        byte[] original = File.ReadAllBytes(SettingsPath);
        var controller = new LauncherSettingsController(SettingsPath);
        Assert.Throws<InvalidDataException>(controller.Reload);
        Assert.Null(controller.Current);
        Assert.NotNull(controller.LoadError);
        Assert.Throws<InvalidOperationException>(() => controller.Save(new LauncherSettings()));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        string? backup = controller.ResetWithBackup();
        Assert.NotNull(backup);
        Assert.Equal(original, File.ReadAllBytes(backup));
        Assert.NotNull(controller.Current);
        Assert.Null(controller.LoadError);
        Assert.All(controller.Current.Slots, Assert.Null);
    }

    [Fact]
    public void FailedReloadKeepsPreviousMenuAndBlocksEditingUntilSuccessfulReload()
    {
        var controller = new LauncherSettingsController(SettingsPath);
        controller.Reload();
        Assert.False(File.Exists(SettingsPath));
        LauncherSettings previous = new LauncherSettings()
            .WithSlot(34, LauncherSettingsTests.Command([""]));
        controller.Save(previous);
        File.WriteAllText(SettingsPath, "invalid");
        Assert.Throws<InvalidDataException>(controller.Reload);
        Assert.Same(previous, controller.Current);
        Assert.Throws<InvalidOperationException>(() => controller.Save(new LauncherSettings()));
        Assert.Equal("invalid", File.ReadAllText(SettingsPath));
        LauncherSettingsStore.Save(SettingsPath, previous);
        controller.Reload();
        Assert.Null(controller.LoadError);
        Assert.NotNull(controller.Current!.Slots[34]);
    }

    [Fact]
    public void FailedSaveKeepsCurrentSnapshotAndPreviousBytes()
    {
        var controller = new LauncherSettingsController(SettingsPath);
        controller.Reload();
        LauncherSettings previous = controller.Current!;
        controller.Save(previous);
        byte[] original = File.ReadAllBytes(SettingsPath);
        using (var locked = new FileStream(SettingsPath, FileMode.Open,
            FileAccess.Read, FileShare.None))
        {
            Assert.Throws<IOException>(() => controller.Save(previous
                .WithSlot(0, LauncherSettingsTests.Command([]))));
            Assert.Same(previous, controller.Current);
        }

        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleSaveKeepsNewerSettingsAndRequiresReload(bool exists)
    {
        if (exists)
        {
            LauncherSettingsStore.Save(SettingsPath, new LauncherSettings());
        }

        var first = new LauncherSettingsController(SettingsPath);
        var second = new LauncherSettingsController(SettingsPath);
        first.Reload();
        second.Reload();
        LauncherSettings previous = Assert.IsType<LauncherSettings>(second.Current);
        LauncherCommand firstCommand = LauncherSettingsTests.Command(["先に保存"]);
        LauncherCommand secondCommand = LauncherSettingsTests.Command(["再読込み後"]);
        first.Save(first.Current!.WithSlot(0, firstCommand));
        byte[] saved = File.ReadAllBytes(SettingsPath);

        IOException failure = Assert.Throws<IOException>(() =>
            second.Save(previous.WithSlot(1, secondCommand)));
        Assert.Contains("再読込み", failure.Message);
        Assert.Same(previous, second.Current);
        Assert.Equal(saved, File.ReadAllBytes(SettingsPath));

        second.Reload();
        second.Save(second.Current!.WithSlot(1, secondCommand));
        LauncherSettings merged = LauncherSettingsStore.Load(SettingsPath);
        Assert.Equal(firstCommand.Arguments,
            Assert.IsType<LauncherCommand>(merged.Slots[0]).Arguments);
        Assert.Equal(secondCommand.Arguments,
            Assert.IsType<LauncherCommand>(merged.Slots[1]).Arguments);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
