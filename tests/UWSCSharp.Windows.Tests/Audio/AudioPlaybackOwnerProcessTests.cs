using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AudioPlaybackOwnerProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("completed-eof", false)]
    [InlineData("failed-start", true)]
    [InlineData("failed-play", true)]
    public async Task TerminalNotificationReleasesProcessWithoutExplicitStop(
        string mode, bool failed)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var owner = new AudioPlaybackOwner((request, token) =>
            {
                process = AudioHostProcess.Start(Host(), request, token);
                return process;
            }, timeout.Token);
            AudioPlaybackTicket ticket = owner.Replace(Request(Path.Combine(directory, mode)));
            uint id = await ticket.Launched.WaitAsync(timeout.Token);
            AudioHostMessageType expected = failed
                ? AudioHostMessageType.Failed : AudioHostMessageType.Completed;
            Assert.Equal(expected, (await ticket.Completed.WaitAsync(timeout.Token)).Type);
            await ticket.Closed.WaitAsync(timeout.Token);
            Assert.True(process!.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            output.WriteLine($"mode={mode} root={id} released_after_terminal=true cleanup_job=active=0");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("bad-exit", false)]
    [InlineData("normal", true)]
    public async Task TerminalExitFailureStopsProcessAndBlocksFollowingRequest(
        string mode, bool timeoutExpected)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        int starts = 0;
        var owner = new AudioPlaybackOwner((request, token) =>
        {
            Interlocked.Increment(ref starts);
            process = AudioHostProcess.Start(Host(), request, token);
            return process;
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            AudioPlaybackTicket ticket = owner.Replace(Request(Path.Combine(directory, mode)));
            uint id = await ticket.Launched.WaitAsync(timeout.Token);
            Assert.Equal(AudioHostMessageType.Completed,
                (await ticket.Completed.WaitAsync(timeout.Token)).Type);
            if (timeoutExpected)
            {
                await Assert.ThrowsAsync<TimeoutException>(() => ticket.Closed.WaitAsync(timeout.Token));
            }
            else
            {
                InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                    () => ticket.Closed.WaitAsync(timeout.Token));
                Assert.Contains("7", error.Message);
            }

            Assert.True(process!.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            AudioPlaybackTicket next = owner.Replace(Request(Path.Combine(directory, "completed-eof")));
            await Assert.ThrowsAsync<AggregateException>(() => next.Closed.WaitAsync(timeout.Token));
            Assert.Equal(1, Volatile.Read(ref starts));
            _ = await Record.ExceptionAsync(() => next.Launched);
            _ = await Record.ExceptionAsync(() => next.Started);
            _ = await Record.ExceptionAsync(() => next.Completed);
            output.WriteLine($"mode={mode} root={id} terminal_exit_failure_retained=true cleanup_job=active=0");
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ReplaceDuringStartClosesOldProcessBeforeNewStarts()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string oldMarker = Path.Combine(directory, "hold-start");
            string nextMarker = Path.Combine(directory, "hold-complete");
            AudioPlaybackTicket? old = null;
            AudioHostProcess? oldProcess = null;
            AudioHostProcess? nextProcess = null;
            uint oldId = 0;
            await using var owner = new AudioPlaybackOwner((request, token) =>
            {
                if (request.Source == nextMarker)
                {
                    Assert.True(old!.Closed.IsCompletedSuccessfully);
                    Assert.True(oldProcess!.CleanupConfirmed);
                    Assert.False(IsAlive(checked((int)oldId)));
                    nextProcess = AudioHostProcess.Start(Host(), request, token);
                    return nextProcess;
                }

                oldProcess = AudioHostProcess.Start(Host(), request, token);
                return oldProcess;
            }, timeout.Token);
            old = owner.Replace(Request(oldMarker));
            oldId = await old.Launched.WaitAsync(timeout.Token);
            await WaitForFileAsync(oldMarker, timeout.Token);
            Assert.True(IsAlive(checked((int)oldId)));
            AudioPlaybackTicket current = owner.Replace(Request(nextMarker));
            Assert.Equal(AudioHostMessageType.Started, (await current.Started.WaitAsync(timeout.Token)).Type);
            await old.Closed.WaitAsync(timeout.Token);
            Assert.False(IsAlive(checked((int)oldId)));
            uint currentId = await current.Launched;
            Assert.True(IsAlive(checked((int)currentId)));
            await owner.ReleaseAsync(old).WaitAsync(timeout.Token);
            Assert.True(IsAlive(checked((int)currentId)));
            await owner.StopAsync().WaitAsync(timeout.Token);
            Assert.True(nextProcess!.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)currentId)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.Started);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.Completed);
            output.WriteLine($"old={oldId} next={currentId} generation={old.Generation}->{current.Generation} released_before_next=true");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RootDisposalClosesCurrentProcessAndDescendant()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var owner = new AudioPlaybackOwner(Host(), timeout.Token);
            string marker = Path.Combine(directory, "child");
            AudioPlaybackTicket ticket = owner.Replace(Request(marker));
            uint rootId = await ticket.Launched.WaitAsync(timeout.Token);
            await WaitForFileAsync(marker + ".child", timeout.Token);
            int childId = await ProcessReceiptReader.ReadPidAsync(marker + ".child", timeout.Token);
            Assert.True(IsAlive(checked((int)rootId)));
            Assert.True(IsAlive(childId));
            await owner.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            await ticket.Closed;
            Assert.False(IsAlive(checked((int)rootId)));
            Assert.False(IsAlive(childId));
            _ = await Record.ExceptionAsync(() => ticket.Started);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ticket.Completed);
            output.WriteLine($"root={rootId} child={childId} owner_disposed=true descendants_exited=true");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task BrokenHostDoesNotLaunchFollowingGeneration()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        int starts = 0;
        AudioHostProcess? process = null;
        var owner = new AudioPlaybackOwner((request, token) =>
        {
            Interlocked.Increment(ref starts);
            process = AudioHostProcess.Start(Host(), request, token);
            return process;
        });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            AudioPlaybackTicket first = owner.Replace(Request(Path.Combine(directory, "duplicate")));
            uint id = await first.Launched.WaitAsync(timeout.Token);
            await Assert.ThrowsAsync<InvalidDataException>(() => first.Closed.WaitAsync(timeout.Token));
            Assert.True(process!.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            string marker = Path.Combine(directory, "normal");
            AudioPlaybackTicket next = owner.Replace(Request(marker));
            await Assert.ThrowsAsync<AggregateException>(() => next.Closed.WaitAsync(timeout.Token));
            Assert.Equal(1, starts);
            Assert.False(File.Exists(marker));
            _ = await Record.ExceptionAsync(() => first.Started);
            _ = await Record.ExceptionAsync(() => first.Completed);
            _ = await Record.ExceptionAsync(() => next.Launched);
            _ = await Record.ExceptionAsync(() => next.Started);
            _ = await Record.ExceptionAsync(() => next.Completed);
            output.WriteLine($"failed_root={id} next_generation_not_launched=true");
        }
        finally
        {
            _ = await Record.ExceptionAsync(() => owner.DisposeAsync().AsTask());
            DeleteDirectory(directory);
        }
    }

    private static WindowsCommandHost Host() => new(FixturePaths.Get(
        "UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"));

    private static AudioHostPlayRequest Request(string marker)
        => new(AudioPlaybackKind.WaveFile, marker);

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "UWSCSharp-audio-owner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitForFileAsync(string path, CancellationToken token)
    {
        while (!File.Exists(path))
        {
            await Task.Delay(25, token);
        }
    }

    private static bool IsAlive(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
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
