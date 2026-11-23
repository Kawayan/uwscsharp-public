using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class WindowsAudioHostIntegrationTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (string host in new[] { "cli", "wpf" })
        {
            foreach (string mode in new[] { "wave", "video", "beep", "event", "missing", "unknown-event", "output", "invalid-output" })
            {
                yield return [host, mode];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task RealHostsPlayOrReportNativeFailureAndReleaseFiles(string host, string mode)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        string eventName = "UWSCSharpAudioTest_" + Guid.NewGuid().ToString("N");
        string registryPath = @"AppEvents\Schemes\Apps\.Default\" + eventName;
        bool registered = false;
        try
        {
            string wave = Path.Combine(directory, "日本語😀 sound.wav");
            string video = Path.Combine(directory, "日本語😀 sound.avi");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sound-silence.wav"), wave);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sound-video.avi"), video);
            if (mode == "event")
            {
                using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(registryPath);
                Assert.Null(existing);
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath + @"\.Current");
                registered = true;
                key.SetValue("", wave);
            }

            AudioHostPlayRequest request = mode switch
            {
                "video" => new(AudioPlaybackKind.VideoFile, video),
                "beep" => new(AudioPlaybackKind.Beep, ""),
                "event" => new(AudioPlaybackKind.SystemEvent, eventName),
                "unknown-event" => new(AudioPlaybackKind.SystemEvent, eventName),
                "missing" => new(AudioPlaybackKind.WaveFile, Path.Combine(directory, "missing.wav")),
                "output" => new(AudioPlaybackKind.WaveFile, wave, 0),
                "invalid-output" => new(AudioPlaybackKind.WaveFile, wave, uint.MaxValue),
                _ => new(AudioPlaybackKind.WaveFile, wave)
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            nint foreground = Native.GetForegroundWindow();
            await using var client = AudioHostProcess.Start(Host(host), request, timeout.Token);
            uint id = await client.Launched.WaitAsync(timeout.Token);
            AudioHostMessage started = await client.Started.WaitAsync(timeout.Token);
            AssertNoVisibleWindow(id);
            AudioHostMessage completed = await client.Completed.WaitAsync(timeout.Token);
            bool failureExpected = mode is "missing" or "unknown-event" or "invalid-output";
            Assert.Equal(failureExpected ? AudioHostMessageType.Failed : AudioHostMessageType.Completed, completed.Type);
            if (!failureExpected)
            {
                Assert.Equal(AudioHostMessageType.Started, started.Type);
            }

            await client.Closed.WaitAsync(timeout.Token);
            Assert.True(client.CleanupConfirmed);
            Assert.Equal(foreground, Native.GetForegroundWindow());
            using (var exclusive = new FileStream(mode == "video" ? video : wave,
                FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.True(exclusive.Length > 0);
            }

            output.WriteLine($"host={host} mode={mode} started={started.Type} terminal={completed.Type} code={completed.Failure?.NativeCode} cleanup_job=active=0 exclusive_file=true audible_unverified=true");
        }
        finally
        {
            if (registered)
            {
                Assert.StartsWith(@"AppEvents\Schemes\Apps\.Default\UWSCSharpAudioTest_", registryPath, StringComparison.Ordinal);
                Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: true);
            }

            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("wpf")]
    public async Task MidiCompletionAndCancellationAreRecordedSeparately(string host)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            string source = Path.Combine(directory, "sound.mid");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sound-midi-note.mid"), source);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = AudioHostProcess.Start(Host(host),
                new(AudioPlaybackKind.MidiFile, source), timeout.Token);
            await client.Launched.WaitAsync(timeout.Token);
            bool terminal = await Task.WhenAny(client.Completed, Task.Delay(3000, timeout.Token)) == client.Completed;
            string outcome = terminal ? (await client.Completed).Type.ToString() : "native-call-or-playback-pending";
            string start = client.Started.IsCompletedSuccessfully ? (await client.Started).Type.ToString() : "pending";
            await client.StopAsync().WaitAsync(timeout.Token);
            _ = await Record.ExceptionAsync(() => client.Started);
            _ = await Record.ExceptionAsync(() => client.Completed);
            Assert.True(client.CleanupConfirmed);
            using var exclusive = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.True(exclusive.Length > 0);
            output.WriteLine($"host={host} midi_start={start} midi_outcome={outcome} cleanup_job=active=0 audible_unverified=true");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task ParentDisconnectExitsTheHostEvenWhileTheBackendDoesNotReturn()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string name = "UWSCSharp-audio-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        using var process = CapturedProcess.StartUncaptured(executable,
            $"\"{executable}\" --audio-runner-probe {name}", false, timeout.Token);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            await AudioHostProtocol.WriteAsync(pipe, new(AudioHostMessageType.Play, new(AudioPlaybackKind.Beep, "")), timeout.Token);
            Assert.Equal(AudioHostMessageType.Started, (await AudioHostProtocol.ReadAsync(pipe, timeout.Token))!.Type);
            pipe.Dispose();
            Assert.Equal(2u, await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            var cleanup = new IOException("検証子の解放");
            await process.StopAfterFailureAsync(cleanup);
            Assert.Empty(cleanup.Data);
        }

        output.WriteLine("parent_disconnected=true blocked_backend_host_exited=true cleanup_job=active=0");
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("wpf")]
    public async Task WaveCanBeStoppedAfterStartAndItsFileIsReleased(string host)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = NewDirectory();
        try
        {
            string file = Path.Combine(directory, "long.wav");
            const int dataBytes = 22050 * 2 * 60;
            using (var writer = new BinaryWriter(File.Create(file)))
            {
                writer.Write("RIFF"u8);
                writer.Write(36 + dataBytes);
                writer.Write("WAVEfmt "u8);
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(22050);
                writer.Write(44100);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write("data"u8);
                writer.Write(dataBytes);
                writer.Write(new byte[dataBytes]);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = AudioHostProcess.Start(Host(host),
                new(AudioPlaybackKind.WaveFile, file), timeout.Token);
            Assert.Equal(AudioHostMessageType.Started, (await client.Started.WaitAsync(timeout.Token)).Type);
            Assert.False(client.Completed.IsCompleted);
            await client.StopAsync().WaitAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.Completed);
            Assert.True(client.CleanupConfirmed);
            using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal(44 + dataBytes, exclusive.Length);
            output.WriteLine($"host={host} wave_stopped_after_start=true cleanup_job=active=0 exclusive_file=true audible_unverified=true");
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("wpf")]
    public async Task EventEnvironmentReferencesAreExpandedOnlyOnce(string host)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string suffix = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "UWSCa-" + suffix);
        Assert.False(Directory.Exists(directory));
        Directory.CreateDirectory(directory);
        string outer = "UWSCSharpAudioOuter_" + suffix;
        string inner = "UWSCai" + suffix[..8];
        string registryPath = @"AppEvents\Schemes\Apps\.Default\UWSCSharpAudioTest_" + suffix;
        bool registered = false;
        try
        {
            Assert.Null(Environment.GetEnvironmentVariable(outer));
            Assert.Null(Environment.GetEnvironmentVariable(inner));
            string media = Path.Combine(directory, "%" + inner + "%", "sound.wav");
            Directory.CreateDirectory(Path.GetDirectoryName(media)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sound-silence.wav"), media);
            Environment.SetEnvironmentVariable(outer, media);
            Environment.SetEnvironmentVariable(inner, "missing");
            using (RegistryKey? existing = Registry.CurrentUser.OpenSubKey(registryPath))
            {
                Assert.Null(existing);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(registryPath + @"\.Current"))
            {
                registered = true;
                key.SetValue("", "%" + outer + "%", RegistryValueKind.ExpandString);
            }

            string resolved = new AudioNativePlatform().EventFile("UWSCSharpAudioTest_" + suffix);
            output.WriteLine($"event_file={resolved} length={resolved.Length} exists={File.Exists(resolved)}");
            Assert.Equal(media, resolved);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = AudioHostProcess.Start(Host(host),
                new(AudioPlaybackKind.SystemEvent, "UWSCSharpAudioTest_" + suffix), timeout.Token);
            AudioHostMessage result = await client.Completed.WaitAsync(timeout.Token);
            output.WriteLine($"event_result={result.Type} failure={result.Failure}");
            Assert.Equal(AudioHostMessageType.Completed, result.Type);
            await client.Closed.WaitAsync(timeout.Token);
            Assert.True(client.CleanupConfirmed);
            output.WriteLine($"host={host} event_expanded_once=true cleanup_job=active=0 audible_unverified=true");
        }
        finally
        {
            Environment.SetEnvironmentVariable(outer, null);
            Environment.SetEnvironmentVariable(inner, null);
            if (registered)
            {
                Assert.StartsWith(@"AppEvents\Schemes\Apps\.Default\UWSCSharpAudioTest_", registryPath, StringComparison.Ordinal);
                Registry.CurrentUser.DeleteSubKeyTree(registryPath, throwOnMissingSubKey: true);
            }

            DeleteDirectory(directory);
        }
    }

    private static WindowsCommandHost Host(string kind) => kind == "cli"
        ? new(FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe"))
        : new(FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe"));

    private static void AssertNoVisibleWindow(uint process)
    {
        bool found = false;
        _ = EnumWindows((window, _) =>
        {
            Native.GetWindowThreadProcessId(window, out uint owner);
            found |= owner == process && Native.IsWindowVisible(window);
            return true;
        }, 0);
        Assert.False(found);
    }

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "UWSCSharp-native-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Assert.StartsWith(root, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
        Directory.Delete(directory, recursive: true);
    }

    private delegate bool WindowCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowCallback callback, nint parameter);
}
