using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerHistoryWindowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-history-window-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset ObservedAt =
        new(2027, 1, 2, 3, 4, 0, TimeSpan.Zero);

    public SchedulerHistoryWindowTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    [Fact]
    public void CaptureRoundTripsWithoutLosingBitsOrChangingReceipt()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Record();
        store.Save(record);
        SchedulerHistoryRecord loaded = Assert.Single(store.Load());
        Assert.Equal(3, loaded.Version);
        Assert.Equal(record.Window, loaded.Window);
        Assert.Equal(0x20000000000001UL, loaded.Window!.Handle);
        SchedulerHistoryRecord completed = record.MarkStarted(ObservedAt.AddSeconds(1)).Complete(
            SchedulerExecutionResult.Completed(0, true, false, true),
            ObservedAt.AddSeconds(2), endedConfirmed: true);
        store.Save(completed);
        Assert.Equal(record.Window, Assert.Single(store.Load()).Window);
    }

    [Fact]
    public void LegacyAndCurrentRecordsCanBeBrowsedWithoutRewritingLegacy()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord legacy = Record(captured: false);
        WriteLegacy(legacy);
        byte[] before = File.ReadAllBytes(FilePath(legacy));
        SchedulerHistoryRecord current = Record();
        store.Save(current);
        Assert.Equal(2, store.Load().Count);
        Assert.Null(store.ReadRecord(legacy.OccurrenceId).Window);
        Assert.Equal(2, store.ReadRecord(legacy.OccurrenceId).Version);
        Assert.Equal(2, store.ReadPage().Items.Count);
        Assert.Equal(before, File.ReadAllBytes(FilePath(legacy)));
    }

    [Fact]
    public void SameRevisionLegacyRetryDoesNotRewriteButRealUpdateWritesVersionThree()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord original = Record(captured: false);
        WriteLegacy(original);
        byte[] before = File.ReadAllBytes(FilePath(original));
        SchedulerHistoryRecord legacy = Assert.Single(store.Load());
        store.Save(legacy);
        Assert.Equal(before, File.ReadAllBytes(FilePath(original)));
        store.Save(legacy.MarkStarted(ObservedAt));
        SchedulerHistoryRecord updated = Assert.Single(store.Load());
        Assert.Equal(3, updated.Version);
        Assert.Null(updated.Window);
        Assert.Equal(legacy.Title, updated.Title);
        Assert.Equal(legacy.Arguments, updated.Arguments);
    }

    [Fact]
    public void LegacyInputForNewFileIsSavedAsVersionThreeWithoutInventedWindow()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord legacy = Record(captured: false) with
        {
            Version = 2
        };
        store.Save(legacy);
        Assert.Equal(3, Assert.Single(store.Load()).Version);
        Assert.Null(Assert.Single(store.Load()).Window);
        Assert.True(JsonNode.Parse(File.ReadAllText(FilePath(legacy)))!.AsObject().ContainsKey("window"));
    }

    [Fact]
    public void LegacyRetryAtByteLimitDoesNotRequireVersionThreeHeadroom()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Record(captured: false);
        JsonObject legacy = JsonSerializer.SerializeToNode(record, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        })!.AsObject();
        legacy["version"] = 2;
        legacy.Remove("window");
        var arguments = new JsonArray("", "", "", "");
        legacy["arguments"] = arguments;
        int available = SchedulerHistoryStore.MaximumRecordBytes
            - Encoding.UTF8.GetByteCount(legacy.ToJsonString());
        for (int index = 0; index < arguments.Count; index++)
        {
            arguments[index] = new string('a', available / 4 + (index < available % 4 ? 1 : 0));
        }
        string content = legacy.ToJsonString();
        Assert.Equal(SchedulerHistoryStore.MaximumRecordBytes, Encoding.UTF8.GetByteCount(content));
        File.WriteAllText(FilePath(record), content);
        SchedulerHistoryRecord loaded = Assert.Single(store.Load());
        store.Save(loaded);
        Assert.Equal(content, File.ReadAllText(FilePath(record)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SavedCaptureCannotBeReplacedOrRemoved(int change)
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord original = Record();
        store.Save(original);
        byte[] before = File.ReadAllBytes(FilePath(original));
        SchedulerHistoryWindowCapture? altered = change switch
        {
            0 => original.Window! with { Handle = 2 },
            1 => original.Window! with { ProcessId = 2 },
            _ => null
        };
        Assert.Throws<InvalidDataException>(() =>
            store.Save(original with
            {
                Revision = 1,
                Window = altered
            }));
        Assert.Equal(before, File.ReadAllBytes(FilePath(original)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("{\"handle\":1,\"processId\":1,\"threadId\":1}")]
    [InlineData("{\"handle\":0,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":1,\"processId\":0,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":1,\"processId\":1,\"threadId\":0,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":-1,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":18446744073709551616,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":1,\"processId\":4294967296,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":1,\"processId\":1,\"threadId\":4294967296,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":1.5,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":\"1\",\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    [InlineData("{\"handle\":1,\"processId\":1,\"threadId\":1,\"observedAt\":\"bad\"}")]
    [InlineData("{\"handle\":1,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:01Z\"}")]
    [InlineData("{\"handle\":1,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T12:04:00+09:00\"}")]
    [InlineData("{\"handle\":1,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\",\"extra\":0}")]
    [InlineData("{\"handle\":1,\"processId\":1,\"processId\":1,\"threadId\":1,\"observedAt\":\"2027-01-02T03:04:00Z\"}")]
    public void InvalidStoredCaptureIsRejectedWithoutRewriting(string window)
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Record();
        store.Save(record);
        JsonObject json = JsonNode.Parse(File.ReadAllText(FilePath(record)))!.AsObject();
        json["window"] = null;
        string content = json.ToJsonString().Replace("\"window\":null", "\"window\":" + window,
            StringComparison.Ordinal);
        File.WriteAllText(FilePath(record), content);
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Equal(content, File.ReadAllText(FilePath(record)));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void VersionAndCaptureFieldMustAgree(int version, bool include)
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Record();
        store.Save(record);
        JsonObject json = JsonNode.Parse(File.ReadAllText(FilePath(record)))!.AsObject();
        json["version"] = version;
        if (!include)
        {
            json.Remove("window");
        }
        File.WriteAllText(FilePath(record), json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => store.Load());
    }

    [Fact]
    public void CaptureDetailsShowOnlySavedObservationsAndUnknownLegacyCapture()
    {
        SchedulerHistoryRecord record = Record();
        string details = SchedulerHistoryPresentation.Details(record);
        Assert.Contains("9007199254740993", details);
        Assert.Contains("PID: 123 / TID: 456", details);
        Assert.Contains("現在の有効性は未確認", details);
        Assert.Contains("窓の観測: 2027-01-02 03:04:00.000 UTC", details);
        Assert.Contains("捕捉先: 未記録", SchedulerHistoryPresentation.Details(
            Record(captured: false) with
            {
                Version = 2
            }));
    }

    private string FilePath(SchedulerHistoryRecord record) =>
        Path.Combine(directory, record.OccurrenceId.ToString("N") + ".json");

    [Fact]
    public void InvalidOutgoingCaptureCannotReplaceTheSavedRecord()
    {
        var store = new SchedulerHistoryStore(directory);
        SchedulerHistoryRecord record = Record();
        store.Save(record);
        byte[] before = File.ReadAllBytes(FilePath(record));
        foreach (SchedulerHistoryRecord candidate in new[]
        {
            record with { Version = 2 },
            record with { Window = record.Window! with { Handle = 0 } },
            record with { Window = record.Window! with { ObservedAt = ObservedAt.AddSeconds(1) } }
        })
        {
            Assert.Throws<InvalidDataException>(() => store.Save(candidate));
        }
        Assert.Equal(before, File.ReadAllBytes(FilePath(record)));
    }

    private void WriteLegacy(SchedulerHistoryRecord record)
    {
        var store = new SchedulerHistoryStore(directory);
        store.Save(record);
        JsonObject json = JsonNode.Parse(File.ReadAllText(FilePath(record)))!.AsObject();
        json["version"] = 2;
        json.Remove("window");
        File.WriteAllText(FilePath(record), json.ToJsonString());
    }

    private SchedulerHistoryRecord Record(bool captured = true)
    {
        SchedulerEntry entry = SchedulerSettingsTests.Entry(captured
            ? new SchedulerWindowCondition("対象", "Main") : new SchedulerStartupCondition());
        SchedulerWindowTrigger? trigger = captured
            ? new(new(0x20000000000001, 123, 456), ObservedAt) : null;
        return SchedulerHistoryRecord.Capture(new SchedulerDispatch(Guid.NewGuid(),
            new SchedulerExecutionRequest(Guid.NewGuid(), entry, ObservedAt, trigger)));
    }
    [Fact]
    public void FullLoadDoesNotTreatAFileAsMissingHistoryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "uwsc-history-obstructed-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "元のファイル");
            var store = new SchedulerHistoryStore(path);
            Assert.Throws<IOException>(() => store.Load());
            Assert.Equal("元のファイル", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
