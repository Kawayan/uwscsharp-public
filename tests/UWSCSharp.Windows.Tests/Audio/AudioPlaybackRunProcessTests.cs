using System.Diagnostics;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AudioPlaybackRunProcessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConfiguredHostCreatesDefaultPlaybackForTheSharedRun()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using IWindowsAutomationSession root = new WindowsAutomation
            {
                Commands = new WindowsCommandOptions { CommandHost = Host() }
            }.CreateSession();
            await using IWindowsAutomationSession worker = root.Fork();
            AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request(directory, "completed-eof"));
            uint id = await ticket.Launched.WaitAsync(timeout.Token);
            Assert.Equal(AudioHostMessageType.Completed,
                (await ticket.Completed.WaitAsync(timeout.Token)).Type);
            await ticket.Closed.WaitAsync(timeout.Token);
            Assert.False(IsAlive(checked((int)id)));
            output.WriteLine($"root={id} default_host_adapter=true process_exited=true");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task WorkerExitKeepsProcessAliveUntilRootStopsPlayback()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using IWindowsAutomationSession root = Create((request, token) =>
            {
                process = AudioHostProcess.Start(Host(), request, token);
                return process;
            });
            IWindowsAutomationSession worker = root.Fork();
            AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request(directory, "hold-complete"));
            uint id = await ticket.Launched.WaitAsync(timeout.Token);
            await ticket.Started.WaitAsync(timeout.Token);
            await worker.DisposeAsync();
            Assert.True(IsAlive(checked((int)id)));
            Assert.False(ticket.Closed.IsCompleted);
            await Audio(root).StopAudioAsync().WaitAsync(timeout.Token);
            Assert.True(process!.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            output.WriteLine($"root={id} worker_exit_kept_playback=true cleanup_job=active=0");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RootDisposalStopsDescendantsAndUnblocksWorkerBeforeJoiningIt()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        IWindowsAutomationSession root = Create((request, token) =>
        {
            process = AudioHostProcess.Start(Host(), request, token);
            return process;
        });
        IWindowsAutomationSession worker = root.Fork();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            AudioHostPlayRequest request = Request(directory, "child");
            AudioPlaybackTicket ticket = Audio(worker).PlayAudio(request);
            uint id = await ticket.Launched.WaitAsync(timeout.Token);
            await ticket.Started.WaitAsync(timeout.Token);
            while (!File.Exists(request.Source + ".child"))
            {
                await Task.Delay(25, timeout.Token);
            }

            int child = await ProcessReceiptReader.ReadPidAsync(request.Source + ".child", timeout.Token);
            Assert.True(IsAlive(child));
            var waiting = Task.Run(async () =>
            {
                try
                {
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ticket.Completed);
                }
                finally
                {
                    await worker.DisposeAsync();
                }
            });
            await root.DisposeAsync().AsTask().WaitAsync(timeout.Token);
            await waiting.WaitAsync(timeout.Token);
            Assert.True(process!.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            Assert.False(IsAlive(child));
            output.WriteLine($"root={id} child={child} worker_wait_released_before_join=true cleanup_job=active=0");
        }
        finally
        {
            await worker.DisposeAsync();
            await root.DisposeAsync();
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task RootCancellationStopsPlaybackDespiteIndependentWorkerToken()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        AudioHostProcess? process = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var stop = new CancellationTokenSource();
            await using IWindowsAutomationSession root = Create((request, token) =>
            {
                process = AudioHostProcess.Start(Host(), request, token);
                return process;
            });
            root.ConfigurePause(null, stop.Token);
            await using IWindowsAutomationSession worker = root.Fork();
            worker.ConfigurePause(null, CancellationToken.None);
            AudioPlaybackTicket ticket = Audio(worker).PlayAudio(Request(directory, "hold-complete"));
            uint id = await ticket.Launched.WaitAsync(timeout.Token);
            await ticket.Started.WaitAsync(timeout.Token);
            await stop.CancelAsync();
            await ticket.Closed.WaitAsync(timeout.Token);
            Assert.True(process!.CleanupConfirmed);
            Assert.False(IsAlive(checked((int)id)));
            Assert.ThrowsAny<OperationCanceledException>(() => Audio(worker).PlayAudio(Request(directory, "hold-start")));
            output.WriteLine($"root={id} root_cancellation_stopped_shared_playback=true cleanup_job=active=0");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static IWindowsAutomationSession Create(
        Func<AudioHostPlayRequest, CancellationToken, IAudioPlaybackOperation> factory)
        => new WindowsAutomation(_ => throw new InvalidOperationException("物理入力は禁止です。"),
            swappedMouseButtons: false, audioPlaybackFactory: factory).CreateSession();

    private static IAudioPlaybackSession Audio(IWindowsAutomationSession session)
        => Assert.IsAssignableFrom<IAudioPlaybackSession>(session);

    private static WindowsCommandHost Host() => new(FixturePaths.Get(
        "UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"));

    private static AudioHostPlayRequest Request(string directory, string mode)
        => new(AudioPlaybackKind.WaveFile, Path.Combine(directory, mode));

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "UWSCSharp-audio-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
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
