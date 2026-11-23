using System.ComponentModel;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class AudioHostProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("normal", false)]
    [InlineData("hold-start", true)]
    [InlineData("hold-complete", true)]
    [InlineData("child", true)]
    [Trait("Category", "SandboxOnly")]
    public async Task StopReleasesJobWithoutWaitingForPlayback(string mode, bool incomplete)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        AudioHostProcess? process = null;
        try
        {
            string receipt = Path.Combine(directory, mode);
            process = Start(receipt, timeout.Token);
            uint id = await process.Launched.WaitAsync(timeout.Token);
            int? childId = null;
            await WaitForFileAsync(receipt, timeout.Token);
            Assert.Equal(checked((int)id), await ProcessReceiptReader.ReadPidAsync(receipt, timeout.Token));
            if (mode == "child")
            {
                await WaitForFileAsync(receipt + ".child", timeout.Token);
                childId = await ProcessReceiptReader.ReadPidAsync(receipt + ".child", timeout.Token);
                Assert.True(IsAlive(childId.Value));
                output.WriteLine("child_pid=" + childId);
            }

            if (!incomplete)
            {
                Assert.Equal(AudioHostMessageType.Started,
                    (await process.Started.WaitAsync(timeout.Token)).Type);
                Assert.Equal(AudioHostMessageType.Completed,
                    (await process.Completed.WaitAsync(timeout.Token)).Type);
            }
            else
            {
                Assert.False(process.Completed.IsCompleted);
            }

            Assert.False(process.Closed.IsCompleted);
            Assert.True(IsAlive(checked((int)id)));
            Task first = process.StopAsync();
            Assert.Same(first, process.StopAsync());
            await first.WaitAsync(timeout.Token);
            Assert.True(process.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            if (childId is int child)
            {
                Assert.False(IsAlive(child));
            }
            if (incomplete)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Completed);
                if (mode == "hold-start")
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Started);
                }
            }

            output.WriteLine($"root_pid={id} mode={mode} cleanup_job=active=0");
        }
        finally
        {
            if (process is not null)
            {
                await process.DisposeAsync();
                await process.DisposeAsync();
            }

            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("failed-start", 5)]
    [InlineData("failed-play", 9)]
    [Trait("Category", "SandboxOnly")]
    public async Task PreserveReportedFailureAndReleaseExitedHost(string mode, uint code)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using AudioHostProcess process = Start(Path.Combine(directory, mode), timeout.Token);
            AudioHostMessage terminal = await process.Completed.WaitAsync(timeout.Token);
            Assert.Equal(AudioHostMessageType.Failed, terminal.Type);
            Assert.Equal(code, terminal.Failure!.NativeCode);
            await process.Closed.WaitAsync(timeout.Token);
            Assert.True(process.CleanupConfirmed);
            output.WriteLine($"mode={mode} failure={code} cleanup_job=active=0");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("early-eof")]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("bad-exit")]
    [Trait("Category", "SandboxOnly")]
    public async Task InvalidHostFailsAndStillReleasesJob(string mode)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            process = Start(Path.Combine(directory, mode), timeout.Token);
            Exception? failure = await Record.ExceptionAsync(() => process.Closed.WaitAsync(timeout.Token));
            Assert.True(failure is InvalidDataException or EndOfStreamException, failure?.ToString());
            Assert.True(process.CleanupConfirmed);
            _ = await Record.ExceptionAsync(() => process.Started);
            _ = await Record.ExceptionAsync(() => process.Completed);
            output.WriteLine($"mode={mode} failure={failure!.GetType().Name} cleanup_job=active=0");
        }
        finally
        {
            if (process is not null)
            {
                _ = await Record.ExceptionAsync(() => process.DisposeAsync().AsTask());
            }

            DeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task CallerCancellationPreservesTokenAndClosesJob()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using AudioHostProcess process = Start(Path.Combine(directory, "hold-start"), cancellation.Token);
            await WaitForFileAsync(Path.Combine(directory, "hold-start"), timeout.Token);
            cancellation.Cancel();
            OperationCanceledException failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => process.Started.WaitAsync(timeout.Token));
            Assert.Equal(cancellation.Token, failure.CancellationToken);
            await process.Closed.WaitAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Completed);
            Assert.True(process.CleanupConfirmed);
            output.WriteLine("caller_cancel=preserved cleanup_job=active=0");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task ObserveExitBeforePipeConnection()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string executable = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var process = AudioHostProcess.Start(new WindowsCommandHost(executable),
            new(AudioPlaybackKind.Beep, ""), timeout.Token);
        try
        {
            await Assert.ThrowsAsync<EndOfStreamException>(() => process.Closed.WaitAsync(timeout.Token));
            await Assert.ThrowsAsync<EndOfStreamException>(() => process.Started);
            await Assert.ThrowsAsync<EndOfStreamException>(() => process.Completed);
            Assert.True(process.CleanupConfirmed);
            output.WriteLine("exit_before_connect cleanup_job=active=0");
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => process.DisposeAsync().AsTask());
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task SuccessfulTerminalAndExitReleaseJobWithoutExplicitStop()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using AudioHostProcess process = Start(Path.Combine(directory, "completed-eof"), timeout.Token);
            Assert.Equal(AudioHostMessageType.Started,
                (await process.Started.WaitAsync(timeout.Token)).Type);
            Assert.Equal(AudioHostMessageType.Completed,
                (await process.Completed.WaitAsync(timeout.Token)).Type);
            await process.Closed.WaitAsync(timeout.Token);
            Assert.True(process.CleanupConfirmed);
            output.WriteLine("completed_eof cleanup_job=active=0");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task ConcurrentStopAndDisposeShareCleanup()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string receipt = Path.Combine(directory, "hold-start");
            process = Start(receipt, timeout.Token);
            await WaitForFileAsync(receipt, timeout.Token);
            await Task.WhenAll(process.StopAsync(), process.DisposeAsync().AsTask(),
                process.DisposeAsync().AsTask()).WaitAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Started);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Completed);
            Assert.True(process.CleanupConfirmed);
            output.WriteLine("concurrent_dispose cleanup_job=active=0");
        }
        finally
        {
            if (process is not null)
            {
                await process.DisposeAsync();
            }

            DeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task MissingExecutableReportsSameLaunchFailureToEveryWaiter()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            process = AudioHostProcess.Start(new WindowsCommandHost(Path.Combine(directory, "missing.exe")),
                new(AudioPlaybackKind.Beep, ""), timeout.Token);
            Win32Exception failure = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(
                () => process.Closed.WaitAsync(timeout.Token));
            Assert.Equal(2, failure.NativeErrorCode);
            Assert.Same(failure, await Record.ExceptionAsync(() => process.Launched));
            Assert.Same(failure, await Record.ExceptionAsync(() => process.Started));
            Assert.Same(failure, await Record.ExceptionAsync(() => process.Completed));
            Assert.False(process.CleanupConfirmed);
            output.WriteLine("launch_failed=2 process_not_created");
        }
        finally
        {
            if (process is not null)
            {
                _ = await Record.ExceptionAsync(() => process.DisposeAsync().AsTask());
            }

            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.exe")]
    [InlineData("C:\\invalid\"host.exe")]
    [InlineData("C:\\invalid\0host.exe")]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void RejectInvalidLaunchPathWithoutCreatingProcess(string executable)
    {
        Assert.Throws<ArgumentException>(() => AudioHostProcess.Start(
            new WindowsCommandHost(executable), new(AudioPlaybackKind.Beep, "")));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void AlreadyCancelledCallDoesNotAllocatePipeOrProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException failure = Assert.ThrowsAny<OperationCanceledException>(
            () => AudioHostProcess.Start(new WindowsCommandHost(Environment.ProcessPath!),
                new(AudioPlaybackKind.Beep, ""), cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public void RejectRelativeManagedAssemblyBeforeCreatingProcess()
    {
        Assert.Throws<ArgumentException>(() => AudioHostProcess.Start(
            new WindowsCommandHost(Environment.ProcessPath!, "relative.dll"),
            new(AudioPlaybackKind.Beep, "")));
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task ManagedAssemblyHostUsesSameProtocolAndCleanup()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string dotnet = Path.GetFullPath(Path.Combine(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                "..", "..", "..", "dotnet.exe"));
            Assert.True(File.Exists(dotnet), dotnet);
            string assembly = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.dll");
            await using var process = AudioHostProcess.Start(
                new WindowsCommandHost(dotnet, assembly),
                new(AudioPlaybackKind.WaveFile, Path.Combine(directory, "completed-eof")), timeout.Token);
            Assert.Equal(AudioHostMessageType.Started,
                (await process.Started.WaitAsync(timeout.Token)).Type);
            Assert.Equal(AudioHostMessageType.Completed,
                (await process.Completed.WaitAsync(timeout.Token)).Type);
            await process.Closed.WaitAsync(timeout.Token);
            Assert.True(process.CleanupConfirmed);
            output.WriteLine("managed_host=dotnet cleanup_job=active=0");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task CancellationDoesNotHideStartupCleanupFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupFailure = new IOException("偽の停止失敗");
        var process = AudioHostProcess.Start(
            new WindowsCommandHost(Environment.ProcessPath!), new(AudioPlaybackKind.Beep, ""),
            cancellation.Token, (_, _) =>
            {
                cancellation.Cancel();
                var failure = new OperationCanceledException(cancellation.Token);
                failure.Data["起動失敗後の停止"] = cleanupFailure;
                throw failure;
            });
        try
        {
            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => process.Closed.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Same(cleanupFailure, failure.InnerException!.Data["起動失敗後の停止"]);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Launched);
            OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => process.Started);
            Assert.Equal(cancellation.Token, cancelled.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Completed);
            Assert.False(process.CleanupConfirmed);
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => process.DisposeAsync().AsTask());
        }
    }

    [Fact]
    [Trait("Category", "SandboxOnly")]
    public async Task CancellationDuringLaunchWaitsForOwnershipBeforeStopping()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AudioHostProcess? process = null;
        try
        {
            using var cancellation = new CancellationTokenSource();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            string receipt = Path.Combine(directory, "hold-start");
            string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
            process = AudioHostProcess.Start(new WindowsCommandHost(executable),
                new(AudioPlaybackKind.WaveFile, receipt), cancellation.Token, (host, name) =>
                {
                    entered.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    return AudioHostProcess.LaunchNative(host, name);
                });
            await entered.Task.WaitAsync(timeout.Token);
            cancellation.Cancel();
            Assert.False(process.Closed.IsCompleted);
            release.TrySetResult();
            uint id = await process.Launched.WaitAsync(timeout.Token);
            await process.Closed.WaitAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Started);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => process.Completed);
            Assert.True(process.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            Assert.False(File.Exists(receipt));
            output.WriteLine($"launch_cancel root_pid={id} request_not_sent cleanup_job=active=0");
        }
        finally
        {
            release.TrySetResult();
            if (process is not null)
            {
                await process.DisposeAsync();
            }

            DeleteDirectory(directory);
        }
    }

    private static AudioHostProcess Start(string receipt, CancellationToken token)
    {
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        return AudioHostProcess.Start(new WindowsCommandHost(executable),
            new(AudioPlaybackKind.WaveFile, receipt), token);
    }

    private static bool IsAlive(int id)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "UWSCSharp-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForFileAsync(string path, CancellationToken token)
    {
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            await Task.Delay(25, token);
        }
    }

    private static void DeleteDirectory(string directory)
    {
        string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directory, true);
                return;
            }
            catch (Exception error) when (attempt < 9
                && error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
