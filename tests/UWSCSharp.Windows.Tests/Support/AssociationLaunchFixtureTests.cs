using Xunit;

namespace UWSCSharp.Windows.Tests;

// ファイルのコピー・ロック・削除だけを検証し、EXEや画面は起動しない。
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class AssociationLaunchFixtureTests
{
    [Fact]
    public async Task CleanupWaitsForFileLockAndDeletesDirectoryAfterRelease()
    {
        var messages = new List<string>();
        var fixture = AssociationLaunchFixture.Create(messages.Add);
        try
        {
            string core = Path.Combine(fixture.AppDirectory, "UWSCSharp.Core.dll");
            Task cleanup;
            using (var locked = new FileStream(core, FileMode.Open,
                FileAccess.Read, FileShare.None))
            {
                cleanup = fixture.CleanupAsync(null);
                await Task.Delay(100);
                Assert.False(cleanup.IsCompleted);
            }

            await cleanup.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(Directory.Exists(fixture.DirectoryPath));
            Assert.Contains(messages, message => message.StartsWith("cleanup-delete-retry:"));
        }
        finally
        {
            if (Directory.Exists(fixture.DirectoryPath))
            {
                await fixture.CleanupAsync(null);
            }
        }
    }

    [Fact]
    public async Task CleanupReportsPersistentDeletionFailureWithinDeadline()
    {
        var clock = new CleanupClock();
        var fixture = AssociationLaunchFixture.Create(
            timeProvider: clock, delay: clock.Delay);
        string core = Path.Combine(fixture.AppDirectory, "UWSCSharp.Core.dll");
        try
        {
            File.SetAttributes(core, FileAttributes.ReadOnly);
            AggregateException error = await Assert.ThrowsAsync<AggregateException>(
                () => fixture.CleanupAsync(null).WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Contains(error.InnerExceptions,
                failure => failure is UnauthorizedAccessException);
            Assert.Equal(TimeSpan.FromSeconds(5), clock.Elapsed);
            Assert.True(Directory.Exists(fixture.DirectoryPath));

            var primary = new InvalidOperationException("本来の検査エラー");
            await fixture.CleanupAsync(primary);
            AggregateException cleanup = Assert.IsType<AggregateException>(
                primary.Data["検査後の回収エラー"]);
            Assert.Contains(cleanup.InnerExceptions,
                failure => failure is UnauthorizedAccessException);
            Assert.Equal(TimeSpan.FromSeconds(10), clock.Elapsed);
        }
        finally
        {
            if (File.Exists(core))
            {
                File.SetAttributes(core, FileAttributes.Normal);
            }

            await fixture.CleanupAsync(null);
        }
    }

    private sealed class CleanupClock : TimeProvider
    {
        internal TimeSpan Elapsed
        {
            get; private set;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Elapsed.Ticks;

        internal Task Delay(TimeSpan duration)
        {
            Elapsed += duration;
            return Task.CompletedTask;
        }
    }
}
