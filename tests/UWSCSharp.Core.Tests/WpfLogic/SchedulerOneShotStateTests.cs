using System.Text;
using System.Text.Json.Nodes;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerOneShotStateTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "uwsc-schedule-state-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(directory, "schedule.json");

    private static readonly DateOnly Date = new(2027, 1, 2);
    private static readonly TimeOnly Time = new(3, 4);
    private static readonly DateTimeOffset Accepted = new(2027, 1, 1, 18, 4, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AllStatusesRoundTripSeparatelyFromCondition(int status)
    {
        SchedulerEntry entry = Entry();
        SchedulerOneShotState state = State(entry, (SchedulerOneShotStatus)status);
        var settings = new SchedulerSettings([entry], oneShotStates: [state]);
        SchedulerSettingsSnapshot loaded = SchedulerSettingsJson.Read(
            SchedulerSettingsJson.Write(settings, Guid.NewGuid()));
        Assert.Equal(2, loaded.FormatVersion);
        SchedulerOneShotState restored = Assert.Single(loaded.Settings.OneShotStates);
        Assert.Equal(state.EntryId, restored.EntryId);
        Assert.Equal(state.OccurrenceId, restored.OccurrenceId);
        Assert.Equal(Accepted, restored.AcceptedAtUtc);
        Assert.Equal(state.Status, restored.Status);
        Assert.Equal(Date, Assert.IsType<SchedulerOneShotCondition>(loaded.Settings.Slots[0]!.Condition).Date);
    }

    [Fact]
    public void EditsRetainConsumptionExceptDateChangeDeletionOrExplicitReset()
    {
        SchedulerEntry entry = Entry();
        SchedulerOneShotState state = State(entry);
        var settings = new SchedulerSettings([entry], oneShotStates: [state]);
        Assert.Same(state, Assert.Single(settings.MoveSlot(0, 10).Resize(100).OneShotStates));
        Assert.Same(state, Assert.Single(settings.WithOptions(true, true, true).OneShotStates));
        var changedScript = new SchedulerEntry(entry.Id, "changed", "different.uws", ["argument"],
            entry.Mode, entry.Condition, directory);
        Assert.Same(state, Assert.Single(settings.WithSlot(0, changedScript).OneShotStates));
        var changedDate = new SchedulerEntry(entry.Id, entry.Title, entry.Path, entry.Arguments,
            entry.Mode, new SchedulerOneShotCondition(Date.AddDays(1), Time), directory);
        Assert.Empty(settings.WithSlot(0, changedDate).OneShotStates);
        Assert.Empty(settings.WithSlot(0, null).OneShotStates);
        Assert.Empty(settings.WithOneShotStates([]).OneShotStates);
        SchedulerSettings copied = settings.WithSlot(1, entry.Copy());
        Assert.Single(copied.OneShotStates);
        Assert.NotEqual(copied.Slots[1]!.Id, state.EntryId);
    }

    [Fact]
    public void RejectsDuplicateOrUnrelatedStateAndKeepsTerminalOutcomes()
    {
        SchedulerEntry entry = Entry();
        SchedulerOneShotState state = State(entry);
        Assert.Throws<InvalidDataException>(() => new SchedulerSettings([entry], oneShotStates: [state, state]));
        Assert.Throws<InvalidDataException>(() => new SchedulerSettings(oneShotStates: [state]));
        SchedulerEntry other = Entry();
        var duplicateOccurrence = new SchedulerOneShotState(other.Id, Date, Time,
            state.OccurrenceId, Accepted, SchedulerOneShotStatus.Accepted);
        Assert.Throws<InvalidDataException>(() => new SchedulerSettings([entry, other],
            oneShotStates: [state, duplicateOccurrence]));
        Assert.Throws<InvalidOperationException>(() => state.WithStatus(SchedulerOneShotStatus.Succeeded));
        SchedulerOneShotState succeeded = state.WithStatus(SchedulerOneShotStatus.Started)
            .WithStatus(SchedulerOneShotStatus.Succeeded);
        Assert.Throws<InvalidOperationException>(() => succeeded.WithStatus(SchedulerOneShotStatus.Unknown));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("entry")]
    [InlineData("occurrence")]
    [InlineData("date")]
    [InlineData("status")]
    [InlineData("utc")]
    [InlineData("duplicate")]
    public void RejectsMalformedStateWithoutAcceptingPartialSettings(string mutation)
    {
        SchedulerEntry entry = Entry();
        JsonObject root = Root(new SchedulerSettings([entry], oneShotStates: [State(entry)]));
        JsonArray states = root["oneShotStates"]!.AsArray();
        JsonObject state = states[0]!.AsObject();
        switch (mutation)
        {
            case "unknown":
                state["unexpected"] = 1;
                break;
            case "missing":
                state.Remove("acceptedAtUtc");
                break;
            case "entry":
                state["entryId"] = Guid.NewGuid();
                break;
            case "occurrence":
                state["occurrenceId"] = Guid.Empty;
                break;
            case "date":
                state["date"] = "2027-01-03";
                break;
            case "status":
                state["status"] = "other";
                break;
            case "utc":
                state["acceptedAtUtc"] = "2027-01-02T03:04:00.0000000+09:00";
                break;
            case "duplicate":
                states.Add(state.DeepClone());
                break;
        }

        Assert.Throws<InvalidDataException>(() => SchedulerSettingsJson.Read(Encoding.UTF8.GetBytes(root.ToJsonString())));
    }

    [Fact]
    public void VersionOneIsReadWithoutWritingAndBackedUpOnFirstSave()
    {
        Directory.CreateDirectory(directory);
        byte[] original = VersionOne(new SchedulerSettings([Entry()]));
        File.WriteAllBytes(SettingsPath, original);
        SchedulerSettingsSnapshot loaded = SchedulerSettingsStore.Load(SettingsPath);
        Assert.Equal(1, loaded.FormatVersion);
        Assert.Empty(loaded.Settings.OneShotStates);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        SchedulerSettingsSnapshot saved = SchedulerSettingsStore.Save(SettingsPath, loaded.Settings, loaded.Revision);
        Assert.Equal(2, saved.FormatVersion);
        Assert.NotNull(saved.PreviousFormatBackupPath);
        Assert.Equal(original, File.ReadAllBytes(saved.PreviousFormatBackupPath));
        Assert.Equal(2, SchedulerSettingsStore.Load(SettingsPath).FormatVersion);
        Assert.Null(SchedulerSettingsStore.Save(SettingsPath, saved.Settings, saved.Revision).PreviousFormatBackupPath);
    }

    [Fact]
    public void MigrationBackupFailurePreservesVersionOne()
    {
        Directory.CreateDirectory(directory);
        byte[] original = VersionOne(new SchedulerSettings([Entry()]));
        File.WriteAllBytes(SettingsPath, original);
        SchedulerSettingsSnapshot loaded = SchedulerSettingsStore.Load(SettingsPath);
        Exception? error = Record.Exception(() => SchedulerSettingsStore.Save(SettingsPath,
            loaded.Settings, loaded.Revision, (_, backup) => Directory.CreateDirectory(backup!)));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Equal(1, SchedulerSettingsStore.Load(SettingsPath).FormatVersion);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public void OversizedMigrationDoesNotOverwriteOrBackupSource()
    {
        Directory.CreateDirectory(directory);
        JsonObject root = JsonNode.Parse(VersionOne(new SchedulerSettings([Entry()])))!.AsObject();
        JsonArray arguments = root["slots"]![0]!["arguments"]!.AsArray();
        arguments.Clear();
        arguments.Add("");
        int overhead = Encoding.UTF8.GetByteCount(root.ToJsonString());
        arguments[0] = new string('x', 4 * 1024 * 1024 - overhead);
        byte[] original = Encoding.UTF8.GetBytes(root.ToJsonString());
        Assert.Equal(4 * 1024 * 1024, original.Length);
        File.WriteAllBytes(SettingsPath, original);
        SchedulerSettingsSnapshot loaded = SchedulerSettingsStore.Load(SettingsPath);
        Assert.Throws<InvalidDataException>(() => SchedulerSettingsStore.Save(SettingsPath,
            loaded.Settings, loaded.Revision));
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        Assert.Single(Directory.GetFiles(directory));
    }

    private static SchedulerEntry Entry() => SchedulerSettingsTests.Entry(new SchedulerOneShotCondition(Date, Time));

    private static SchedulerOneShotState State(SchedulerEntry entry,
        SchedulerOneShotStatus status = SchedulerOneShotStatus.Accepted) =>
        new(entry.Id, Date, Time, Guid.NewGuid(), Accepted, status);

    private static JsonObject Root(SchedulerSettings settings) => JsonNode.Parse(
        SchedulerSettingsJson.Write(settings, Guid.NewGuid()))!.AsObject();

    private static byte[] VersionOne(SchedulerSettings settings)
    {
        JsonObject root = Root(settings);
        root["version"] = 1;
        root.Remove("oneShotStates");
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetDirectoryName(target) != temp || !Path.GetFileName(target).StartsWith("uwsc-schedule-state-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("検査用フォルダーの範囲が一致しません。");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, true);
        }
    }
}
