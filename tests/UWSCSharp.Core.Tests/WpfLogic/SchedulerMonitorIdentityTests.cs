using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SchedulerMonitorIdentityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-monitor-identity-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void JobIdentitySurvivesNewGenerationsAndAnExistingEmptyFile()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "schedule.monitor");
        File.WriteAllBytes(path, []);
        Guid job;
        Guid generation;
        using (SchedulerMonitorLease first = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(path)))
        {
            job = first.JobIdentity;
            generation = first.Generation;
            Assert.NotEqual(Guid.Empty, job);
            Assert.Null(SchedulerMonitorLease.TryAcquire(path));
        }
        Assert.Equal(16, new FileInfo(path).Length);
        using SchedulerMonitorLease next = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(path));
        Assert.Equal(job, next.JobIdentity);
        Assert.NotEqual(generation, next.Generation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    public void InvalidIdentityDoesNotRemainLockedOrGetOverwritten(int length)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "schedule.monitor");
        byte[] bytes = new byte[length];
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => SchedulerMonitorLease.TryAcquire(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        var repaired = Guid.NewGuid();
        File.WriteAllBytes(path, repaired.ToByteArray());
        using SchedulerMonitorLease lease = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(path));
        Assert.Equal(repaired, lease.JobIdentity);
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(target) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(target).StartsWith("uwsc-monitor-identity-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("識別子テストの回収先が範囲外です。");
        }
        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
