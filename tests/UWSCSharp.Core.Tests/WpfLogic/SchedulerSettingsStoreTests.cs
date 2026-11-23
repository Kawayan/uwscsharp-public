using System.Text;
using System.Text.Json.Nodes;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerSettingsStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-scheduler-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(directory, "schedule.json");

    [Fact]
    public void AllConditionsRoundTripWithoutLosingFlagsArgumentsOrSlots()
    {
        SchedulerCondition[] conditions =
        [
            new SchedulerStartupCondition(),
            new SchedulerIntervalCondition(SchedulerIntervalUnit.Minute, 60),
            new SchedulerIntervalCondition(SchedulerIntervalUnit.Hour, 24),
            new SchedulerTimeCondition(new TimeOnly(23, 59), SchedulerDayKind.Weekdays, [0, 6]),
            new SchedulerTimeCondition(new TimeOnly(0, 0), SchedulerDayKind.MonthDays, [1, 31]),
            new SchedulerTimeCondition(new TimeOnly(1, 2), SchedulerDayKind.EveryDay, []),
            new SchedulerExpressionCondition("(NN MOD 7)=0"),
            new SchedulerOneShotCondition(new DateOnly(2000, 1, 1), new TimeOnly(0, 0)),
            new SchedulerWindowCondition("空白 と😀", ""),
            new SchedulerWindowCondition("", "Class")
        ];
        var settings = new SchedulerSettings(conditions.Select(condition =>
            SchedulerSettingsTests.Entry(condition)), 400, true, true, true);
        SchedulerSettingsSnapshot saved = SchedulerSettingsStore.Save(SettingsPath, settings, null);
        SchedulerSettingsSnapshot loaded = SchedulerSettingsStore.Load(SettingsPath);
        Assert.Equal(saved.Revision, loaded.Revision);
        Assert.Equal(SchedulerSettingsJson.Write(settings, saved.Revision!.Value),
            SchedulerSettingsJson.Write(loaded.Settings, loaded.Revision!.Value));
        Assert.All(loaded.Settings.Slots.Skip(conditions.Length), Assert.Null);
        Assert.False(File.Exists(loaded.Settings.Slots[0]!.Path));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("revision")]
    [InlineData("relative")]
    [InlineData("mixed-condition")]
    [InlineData("missing-kind")]
    [InlineData("unknown-kind")]
    [InlineData("null-arguments")]
    [InlineData("empty-id")]
    public void MalformedSettingsDoNotOverwriteOriginal(string mutation)
    {
        SchedulerSettingsSnapshot saved = Initial();
        JsonNode root = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        JsonNode entry = root["slots"]![0]!;
        switch (mutation)
        {
            case "unknown":
                root["typo"] = 1;
                break;
            case "missing":
                root.AsObject().Remove("capacity");
                break;
            case "version":
                root["version"] = 99;
                break;
            case "revision":
                root["revision"] = Guid.Empty;
                break;
            case "relative":
                entry["path"] = "relative.uws";
                break;
            case "mixed-condition":
                entry["condition"]!["days"] = new JsonArray();
                break;
            case "missing-kind":
                entry["condition"]!.AsObject().Remove("kind");
                break;
            case "unknown-kind":
                entry["condition"]!["kind"] = "future";
                break;
            case "null-arguments":
                entry["arguments"] = null;
                break;
            case "empty-id":
                entry["id"] = Guid.Empty;
                break;
        }

        File.WriteAllText(SettingsPath, root.ToJsonString(), new UTF8Encoding(false));
        byte[] bytes = File.ReadAllBytes(SettingsPath);
        Assert.Throws<InvalidDataException>(() => SchedulerSettingsStore.Load(SettingsPath));
        Assert.Throws<InvalidDataException>(() => SchedulerSettingsStore.Save(
            SettingsPath, saved.Settings, saved.Revision));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    [InlineData("\\uD800x")]
    [InlineData("\\u0000")]
    public void InvalidEscapedUtf16IsRejected(string escaped)
    {
        Initial();
        string json = File.ReadAllText(SettingsPath);
        JsonNode root = JsonNode.Parse(json)!;
        root["slots"]![0]!["title"] = "marker";
        json = root.ToJsonString().Replace("marker", escaped);
        Assert.Throws<InvalidDataException>(() => SchedulerSettingsJson.Read(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void DuplicatePropertiesAndInvalidUtf8AreRejected()
    {
        Initial();
        byte[] original = File.ReadAllBytes(SettingsPath);
        string json = Encoding.UTF8.GetString(original);
        byte[] duplicate = Encoding.UTF8.GetBytes(json.Replace("\"version\": 2",
            "\"version\": 2,\"version\": 2"));
        Assert.Throws<InvalidDataException>(() => SchedulerSettingsJson.Read(duplicate));
        Assert.Throws<InvalidDataException>(() => SchedulerSettingsJson.Read([.. original, 0xff]));
    }

    [Fact]
    public void ExactReadLimitAndBomAreAcceptedButLargerInputIsRejected()
    {
        SchedulerSettingsSnapshot saved = Initial();
        byte[] json = SchedulerSettingsJson.Write(saved.Settings, saved.Revision!.Value);
        byte[] bytes = new byte[4 * 1024 * 1024];
        Array.Fill(bytes, (byte)' ');
        new byte[] { 0xef, 0xbb, 0xbf }.CopyTo(bytes, 0);
        json.CopyTo(bytes, 3);
        File.WriteAllBytes(SettingsPath, bytes);
        Assert.Equal(saved.Revision, SchedulerSettingsStore.Load(SettingsPath).Revision);
        using (var file = new FileStream(SettingsPath, FileMode.Append))
        {
            file.WriteByte((byte)' ');
        }

        Assert.Throws<InvalidDataException>(() => SchedulerSettingsStore.Load(SettingsPath));
    }

    [Fact]
    public void EncodedWriteLimitPreservesExistingFile()
    {
        SchedulerSettingsSnapshot saved = Initial();
        byte[] original = File.ReadAllBytes(SettingsPath);
        SchedulerEntry entry = SchedulerSettingsTests.Entry(arguments: [new string('é', 2 * 1024 * 1024)]);
        Assert.Throws<InvalidDataException>(() => SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings([entry]), saved.Revision));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void ExactEncodedWriteLimitRoundTrips()
    {
        SchedulerEntry empty = SchedulerSettingsTests.Entry(arguments: [""]);
        var revision = Guid.NewGuid();
        int overhead = SchedulerSettingsJson.Write(new SchedulerSettings([empty]), revision).Length;
        string argument = new('a', 4 * 1024 * 1024 - overhead);
        var entry = new SchedulerEntry(empty.Id, empty.Title, empty.Path,
            [argument], empty.Mode, empty.Condition, Path.GetPathRoot(empty.Path)!);
        SchedulerSettingsSnapshot saved = SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings([entry]), null);
        Assert.Equal(4 * 1024 * 1024, new FileInfo(SettingsPath).Length);
        Assert.Equal(argument, SchedulerSettingsStore.Load(SettingsPath).Settings.Slots[0]!.Arguments[0]);
        Assert.Equal(saved.Revision, SchedulerSettingsStore.Load(SettingsPath).Revision);
    }

    [Fact]
    public void UnexpectedFileCreationIsNotOverwritten()
    {
        byte[] other = Encoding.UTF8.GetBytes("別の作成者の設定");
        Assert.Throws<IOException>(() => SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings(), null, (_, _) => File.WriteAllBytes(SettingsPath, other)));
        Assert.Equal(other, File.ReadAllBytes(SettingsPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void StaleAndMissingFileRevisionsCannotOverwriteAnotherSave()
    {
        SchedulerSettingsSnapshot first = Initial();
        SchedulerSettingsSnapshot second = SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings(), first.Revision);
        byte[] original = File.ReadAllBytes(SettingsPath);
        Assert.Throws<SchedulerSettingsConflictException>(() =>
            SchedulerSettingsStore.Save(SettingsPath, first.Settings, first.Revision));
        Assert.Throws<SchedulerSettingsConflictException>(() =>
            SchedulerSettingsStore.Save(SettingsPath, first.Settings, null));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.NotEqual(first.Revision, second.Revision);
        File.Delete(SettingsPath);
        Assert.Throws<SchedulerSettingsConflictException>(() =>
            SchedulerSettingsStore.Save(SettingsPath, first.Settings, first.Revision));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void WriteFailureAndOwnedLockPreserveFileAndReleaseResources()
    {
        SchedulerSettingsSnapshot first = Initial();
        byte[] original = File.ReadAllBytes(SettingsPath);
        Assert.Throws<IOException>(() => SchedulerSettingsStore.Save(SettingsPath,
            new SchedulerSettings(), first.Revision, (_, _) => throw new IOException("書込み障害")));
        using (var ownership = new FileStream(SettingsPath + ".lock", FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => SchedulerSettingsStore.Save(SettingsPath,
                new SchedulerSettings(), first.Revision));
        }

        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        SchedulerSettingsStore.Save(SettingsPath, new SchedulerSettings(), first.Revision);
    }

    [Fact]
    public void ResetRequiresSuccessfulBackupBeforePublishingDefaults()
    {
        Initial();
        byte[] original = Encoding.UTF8.GetBytes("破損した元設定");
        File.WriteAllBytes(SettingsPath, original);
        string? failedBackup = null;
        Exception? failure = Record.Exception(() => SchedulerSettingsStore.ResetWithBackup(
            SettingsPath, (_, backup) =>
            {
                failedBackup = backup;
                Directory.CreateDirectory(backup!);
            }));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Directory.Delete(failedBackup!);
        (SchedulerSettingsSnapshot snapshot, string? path) = SchedulerSettingsStore.ResetWithBackup(SettingsPath);
        Assert.Equal(original, File.ReadAllBytes(path!));
        Assert.False(snapshot.Settings.Enabled);
        Assert.All(snapshot.Settings.Slots, Assert.Null);
        Assert.Equal(snapshot.Revision, SchedulerSettingsStore.Load(SettingsPath).Revision);
    }

    [Fact]
    public void ControllerRetainsDataButStopsAdmissionAfterReloadFailure()
    {
        var controller = new SchedulerSettingsController(SettingsPath);
        controller.Reload();
        controller.Save(new SchedulerSettings([SchedulerSettingsTests.Entry()], enabled: true));
        SchedulerSettingsSnapshot previous = controller.Current!;
        byte[] valid = File.ReadAllBytes(SettingsPath);
        Assert.True(controller.CanAcceptNewReservations);
        File.WriteAllText(SettingsPath, "invalid");
        Assert.Throws<InvalidDataException>(controller.Reload);
        Assert.Same(previous, controller.Current);
        Assert.NotNull(controller.LoadError);
        Assert.False(controller.CanAcceptNewReservations);
        Assert.Throws<InvalidOperationException>(() => controller.Save(new SchedulerSettings()));
        File.WriteAllBytes(SettingsPath, valid);
        controller.Reload();
        Assert.Null(controller.LoadError);
        Assert.True(controller.CanAcceptNewReservations);
        File.WriteAllText(SettingsPath, "invalid");
        Assert.Throws<InvalidDataException>(controller.Reload);
        string? backup = controller.ResetWithBackup();
        Assert.Equal("invalid", File.ReadAllText(backup!));
        Assert.Null(controller.LoadError);
        Assert.False(controller.CanAcceptNewReservations);
    }

    [Fact]
    public void ControllerDoesNotPublishFailedOrStaleSave()
    {
        var first = new SchedulerSettingsController(SettingsPath);
        var second = new SchedulerSettingsController(SettingsPath);
        first.Reload();
        second.Reload();
        first.Save(new SchedulerSettings(enabled: true));
        SchedulerSettingsSnapshot previous = second.Current!;
        Assert.Throws<SchedulerSettingsConflictException>(() => second.Save(new SchedulerSettings()));
        Assert.Same(previous, second.Current);
        second.Reload();
        previous = second.Current!;
        using (var ownership = new FileStream(SettingsPath + ".lock", FileMode.Open,
            FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => second.Save(new SchedulerSettings()));
        }

        Assert.Same(previous, second.Current);
        Assert.True(second.CanAcceptNewReservations);
    }

    private SchedulerSettingsSnapshot Initial() => SchedulerSettingsStore.Save(SettingsPath,
        new SchedulerSettings([SchedulerSettingsTests.Entry()]), null);

    public void Dispose()
    {
        string absolute = Path.GetFullPath(directory);
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(absolute), parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(absolute).StartsWith("uwsc-scheduler-settings-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("予約設定テストの回収先が範囲外です。");
        }

        if (Directory.Exists(absolute))
        {
            Directory.Delete(absolute, recursive: true);
        }
    }
}
