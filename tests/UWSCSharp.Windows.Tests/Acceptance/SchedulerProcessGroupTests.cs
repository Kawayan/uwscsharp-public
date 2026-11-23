using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using UWSCSharp.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed partial class SchedulerProcessGroupTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-scheduler-job-" + Guid.NewGuid().ToString("N"));
    private readonly List<SchedulerChildProcess> children = [];
    private readonly List<SchedulerProcessGroup> groups = [];
    private readonly List<SchedulerMonitorLease> leases = [];
    private readonly List<Process> owned = [];
    private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(25));

    private static string Executable => FixturePaths.Get(
        "UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task NormalExitPreservesOrderedArgumentsAndWorkingDirectoryAndDrains()
    {
        SchedulerProcessGroup group = await OpenGroup();
        string[] arguments = ["", "日本語 \"quote\"", "末尾\\", "😀"];
        SchedulerChildProcess child = Start(group, "spawn", "normal", arguments);
        using JsonDocument receipt = await Read("normal");
        Assert.NotEqual(Environment.ProcessId, (int)child.Id);
        Assert.Equal(child.Id, receipt.RootElement.GetProperty("pid").GetUInt32());
        Assert.Equal(directory, receipt.RootElement.GetProperty("working_directory").GetString());
        Assert.Equal(arguments, receipt.RootElement.GetProperty("arguments")
            .EnumerateArray().Select(value => value.GetString()).ToArray());
        foreach (uint pid in group.GetProcessIds())
        {
            using var member = Process.GetProcessById((int)pid);
            output.WriteLine($"Job member: {pid} {member.ProcessName}");
        }
        Assert.Contains(child.Id, group.GetProcessIds());
        Assert.Contains(receipt.RootElement.GetProperty("child_pid").GetUInt32(), group.GetProcessIds());
        File.WriteAllText(Release("normal"), "go");
        Assert.Equal(0u, await child.WaitForDrainAsync(timeout.Token));
        Assert.Equal(0u, await child.WaitForExitAsync(timeout.Token));
        Assert.Equal(0u, group.ActiveProcesses);
    }

    [Fact]
    public async Task CreationMarkerDistinguishesFailureBeforeNativeCreation()
    {
        SchedulerProcessGroup group = await OpenGroup();
        var creation = new SchedulerProcessCreation();
        Assert.Throws<Win32Exception>(() => group.Start(
            Path.Combine(directory, "missing.exe"), [], directory,
            timeout.Token, creation));
        Assert.False(creation.Created);
        Assert.Equal(0u, creation.ProcessId);
        Assert.Equal(0u, group.ActiveProcesses);
    }

    [Fact]
    public async Task CreationMarkerRetainsTheActualCreatedProcess()
    {
        SchedulerProcessGroup group = await OpenGroup();
        var creation = new SchedulerProcessCreation();
        SchedulerChildProcess child = group.Start(Executable,
            ["--scheduler-job-probe", "spawn", Data("creation"), Release("creation")],
            directory, timeout.Token, creation);
        children.Add(child);
        using JsonDocument receipt = await Read("creation");
        Assert.True(creation.Created);
        Assert.Equal(child.Id, creation.ProcessId);
        Assert.Equal(child.Id, receipt.RootElement.GetProperty("pid").GetUInt32());
        File.WriteAllText(Release("creation"), "go");
        Assert.Equal(0u, await child.WaitForDrainAsync(timeout.Token));
    }

    [Fact]
    public async Task RootExitDoesNotProveDescendantRecovery()
    {
        SchedulerProcessGroup group = await OpenGroup();
        SchedulerChildProcess child = Start(group, "orphan", "orphan");
        using JsonDocument receipt = await Read("orphan");
        Assert.Equal(0u, await child.WaitForExitAsync(timeout.Token));
        Assert.DoesNotContain(child.Id, group.GetProcessIds());
        Assert.Contains(receipt.RootElement.GetProperty("child_pid").GetUInt32(), group.GetProcessIds());
        Assert.Throws<InvalidOperationException>(() => group.Dispose());
        using var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            child.WaitForDrainAsync(shortWait.Token));
        child.TerminateTree();
        Assert.Equal(0u, await child.WaitForDrainAsync(timeout.Token));
        Assert.Equal(0u, group.ActiveProcesses);
    }

    [Fact]
    public async Task TerminationOnlyStopsTheSelectedExecutionJob()
    {
        SchedulerProcessGroup group = await OpenGroup();
        SchedulerChildProcess first = Start(group, "spawn", "first");
        SchedulerChildProcess second = Start(group, "spawn", "second");
        using JsonDocument firstReceipt = await Read("first");
        using JsonDocument secondReceipt = await Read("second");
        Assert.Contains(first.Id, group.GetProcessIds());
        Assert.Contains(second.Id, group.GetProcessIds());
        first.TerminateTree();
        Assert.Equal(130u, await first.WaitForDrainAsync(timeout.Token));
        Assert.DoesNotContain(first.Id, group.GetProcessIds());
        Assert.Contains(second.Id, group.GetProcessIds());
        Assert.Contains(secondReceipt.RootElement.GetProperty("child_pid").GetUInt32(), group.GetProcessIds());
        using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            second.WaitForExitAsync(waiting.Token));
        File.WriteAllText(Release("second"), "go");
        Assert.Equal(0u, await second.WaitForDrainAsync(timeout.Token));
        Assert.Equal(0u, group.ActiveProcesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOwnerDrainsPreviousGenerationBeforeStartingAnotherRun(bool releaseLeaseFirst)
    {
        string leasePath = Path.Combine(directory, "schedule.monitor");
        string receipt = Data("owner");
        Process owner = StartUnowned(["--scheduler-job-probe",
            releaseLeaseFirst ? "owner-release" : "owner", leasePath, receipt, Release("owner")]);
        Process unrelated = StartUnowned(["--launcher-child-probe",
            Data("unrelated"), Release("unrelated")]);
        using JsonDocument unrelatedReceipt = await Read("unrelated");
        using JsonDocument ownerReceipt = await Read("owner");
        Guid identity = ownerReceipt.RootElement.GetProperty("job_identity").GetGuid();
        using var worker = Process.GetProcessById(
            ownerReceipt.RootElement.GetProperty("worker_pid").GetInt32());
        using var descendant = Process.GetProcessById(
            ownerReceipt.RootElement.GetProperty("descendant_pid").GetInt32());
        if (!releaseLeaseFirst)
        {
            Assert.Null(SchedulerMonitorLease.TryAcquire(leasePath));
            owner.Kill();
            await owner.WaitForExitAsync(timeout.Token);
        }
        else
        {
            Assert.False(owner.HasExited);
            Assert.False(worker.HasExited);
            Assert.False(descendant.HasExited);
        }
        SchedulerMonitorLease? acquired;
        while ((acquired = SchedulerMonitorLease.TryAcquire(leasePath)) is null)
        {
            await Task.Delay(25, timeout.Token);
        }
        SchedulerMonitorLease lease = acquired;
        leases.Add(lease);
        Assert.Equal(identity, lease.JobIdentity);
        var group = new SchedulerProcessGroup(lease.JobIdentity, lease.Handle);
        groups.Add(group);
        Assert.Throws<InvalidOperationException>(() => Start(group, "spawn", "too-early"));
        Task oldExited = ObserveExit(worker, descendant);
        await Task.WhenAll(group.RecoverAsync(timeout.Token), oldExited);
        Assert.Equal(0u, group.ActiveProcesses);
        Assert.False(unrelated.HasExited);
        if (releaseLeaseFirst)
        {
            File.WriteAllText(Release("owner"), "go");
            await owner.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, owner.ExitCode);
        }
        SchedulerChildProcess current = Start(group, "spawn", "current");
        using JsonDocument currentReceipt = await Read("current");
        Assert.Contains(current.Id, group.GetProcessIds());
        Assert.Contains(currentReceipt.RootElement.GetProperty("child_pid").GetUInt32(), group.GetProcessIds());
        Assert.False(unrelated.HasExited);
        File.WriteAllText(Release("current"), "go");
        Assert.Equal(0u, await current.WaitForDrainAsync(timeout.Token));
        File.WriteAllText(Release("unrelated"), "go");
        await unrelated.WaitForExitAsync(timeout.Token);
        Assert.Equal(0, unrelated.ExitCode);
    }

    [Fact]
    public async Task FailedOrCancelledCreationDoesNotAddAProcess()
    {
        SchedulerProcessGroup group = await OpenGroup();
        Assert.Throws<Win32Exception>(() => group.Start(
            Path.Combine(directory, "missing.exe"), [], directory));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => group.Start(
            Executable, [], directory, cancel.Token));
        Assert.Equal(0u, group.ActiveProcesses);
    }

    [Fact]
    public async Task CopiedPrivateIdentityDoesNotJoinAnotherPhysicalFilesJob()
    {
        SchedulerProcessGroup first = await OpenGroup();
        SchedulerChildProcess child = Start(first, "spawn", "physical");
        using JsonDocument receipt = await Read("physical");
        string copyPath = Path.Combine(directory, "copy.monitor");
        File.WriteAllBytes(copyPath, leases[0].JobIdentity.ToByteArray());
        SchedulerMonitorLease copy = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(copyPath));
        leases.Add(copy);
        var second = new SchedulerProcessGroup(copy.JobIdentity, copy.Handle);
        groups.Add(second);
        await second.RecoverAsync(timeout.Token);
        Assert.Equal(0u, second.ActiveProcesses);
        Assert.Contains(child.Id, first.GetProcessIds());
        File.WriteAllText(Release("physical"), "go");
        Assert.Equal(0u, await child.WaitForDrainAsync(timeout.Token));
    }

    [Fact]
    public async Task HardLinkAliasRecoversTheSamePhysicalFilesJob()
    {
        string path = Path.Combine(directory, "schedule.monitor");
        string alias = Path.Combine(directory, "alias.monitor");
        File.WriteAllBytes(path, []);
        if (!CreateHardLinkW(alias, path, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        SchedulerProcessGroup first = await OpenGroup();
        SchedulerChildProcess child = Start(first, "spawn", "alias");
        using JsonDocument receipt = await Read("alias");
        leases[0].Dispose();
        SchedulerMonitorLease next = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(alias));
        leases.Add(next);
        var second = new SchedulerProcessGroup(next.JobIdentity, next.Handle);
        groups.Add(second);
        Assert.Contains(child.Id, second.GetProcessIds());
        await second.RecoverAsync(timeout.Token);
        Assert.Equal(130u, await child.WaitForDrainAsync(timeout.Token));
        Assert.Equal(0u, first.ActiveProcesses);
        Assert.Equal(0u, second.ActiveProcesses);
    }

    [Fact]
    public async Task CaptureFailureStillTerminatesOwnedTree()
    {
        using SafeFileHandle monitor = SchedulerProcessGroup.CreateJobObjectW(0, null);
        OwnedProcessJob.EnableKillOnClose(monitor);
        bool fail = false;
        using var child = SchedulerChildProcess.Start(Executable,
            ["--launcher-child-probe", Data("capture"), Release("capture")],
            directory, monitor, () =>
            {
                if (fail)
                {
                    throw new InvalidOperationException("診断取得の検証用失敗");
                }
            }, timeout.Token);
        try
        {
            using JsonDocument receipt = await Read("capture");
            fail = true;
            Assert.Throws<InvalidOperationException>(() => child.TerminateTree());
            using var waiting = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Assert.Equal(130u, await child.WaitForDrainAsync(waiting.Token));
        }
        finally
        {
            fail = false;
        }
    }

    private async Task<SchedulerProcessGroup> OpenGroup()
    {
        SchedulerMonitorLease lease = Assert.IsType<SchedulerMonitorLease>(
            SchedulerMonitorLease.TryAcquire(Path.Combine(directory, "schedule.monitor")));
        leases.Add(lease);
        var group = new SchedulerProcessGroup(lease.JobIdentity, lease.Handle);
        groups.Add(group);
        await group.RecoverAsync(timeout.Token);
        return group;
    }

    private SchedulerChildProcess Start(SchedulerProcessGroup group, string mode,
        string name, params string[] arguments)
    {
        SchedulerChildProcess child = group.Start(Executable,
            ["--scheduler-job-probe", mode, Data(name), Release(name), .. arguments],
            directory, timeout.Token);
        children.Add(child);
        return child;
    }

    private Process StartUnowned(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = directory
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        Process process = Process.Start(start)
            ?? throw new InvalidOperationException("検証用プロセスを開始できません。");
        owned.Add(process);
        return process;
    }

    private async Task ObserveExit(params Process[] processes)
    {
        await Task.WhenAll(processes.Select(async process =>
        {
            await process.WaitForExitAsync(timeout.Token);
            process.Dispose();
        }));
    }

    private string Data(string name) => Path.Combine(directory, name + ".json");

    private string Release(string name) => Path.Combine(directory, name + ".go");

    private async Task<JsonDocument> Read(string name)
    {
        while (!File.Exists(Data(name)))
        {
            if (File.Exists(Data(name) + ".error"))
            {
                throw new InvalidOperationException(File.ReadAllText(Data(name) + ".error"));
            }
            await Task.Delay(25, timeout.Token);
        }
        return await ProcessReceiptReader.ReadJsonAsync(Data(name), timeout.Token);
    }

    public async Task DisposeAsync()
    {
        foreach (SchedulerChildProcess child in children)
        {
            child.Dispose();
        }
        foreach (Process process in owned)
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
            await process.WaitForExitAsync();
            process.Dispose();
        }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (SchedulerProcessGroup group in groups)
        {
            await group.WaitForDrainAsync(cleanup.Token);
            group.Dispose();
        }
        foreach (SchedulerMonitorLease lease in leases)
        {
            lease.Dispose();
        }
        timeout.Dispose();
        string target = Path.GetFullPath(directory);
        if (Path.GetDirectoryName(target) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            || !Path.GetFileName(target).StartsWith("uwsc-scheduler-job-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("予約Jobテストの回収先が範囲外です。");
        }
        while (true)
        {
            cleanup.Token.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(target, recursive: true);
                break;
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(25, cleanup.Token);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string name, string existing, nint security);
}
