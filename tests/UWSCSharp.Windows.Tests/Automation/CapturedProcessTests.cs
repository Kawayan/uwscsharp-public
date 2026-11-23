using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class CapturedProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MarkerReadWaitsForExclusiveLockAndHonorsCancellation(bool cancel)
    {
        string marker = NewMarker();
        try
        {
            await File.WriteAllTextAsync(marker, "123");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<int> read;
            using (var locked = new FileStream(marker, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None))
            {
                read = ReadMarkerAsync(marker, timeout.Token);
                await Task.Delay(75);
                Assert.False(read.IsCompleted);
                if (cancel)
                {
                    await timeout.CancelAsync();
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
                }
            }

            if (!cancel)
            {
                Assert.Equal(123, await read);
            }
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task MergedPipeDrainsBothWritersBeforeExit()
    {
        using CapturedProcess process = await StartAsync("duplex", 1024 * 1024);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            byte[] bytes = await process.ReadOutputAsync(2 * 1024 * 1024, timeout.Token);
            Assert.Equal(1024 * 1024, bytes.Count(value => value == 65));
            Assert.Equal(1024 * 1024, bytes.Count(value => value == 90));
            Assert.Equal(37u, await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Theory]
    [InlineData("ordered", "stdout-1|stderr-1|stdout-2|stderr-2")]
    [InlineData("stdin", "\u0001")]
    public async Task OrderedWritesAndNullInputArePreserved(string mode, string expected)
    {
        using CapturedProcess process = await StartAsync(mode);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Equal(Encoding.UTF8.GetBytes(expected),
                await process.ReadOutputAsync(100, timeout.Token));
            Assert.Equal(37u, await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(259)]
    [InlineData(-1)]
    public async Task WaitChecksTheHandleInsteadOfTheExitCode(int code)
    {
        using CapturedProcess process = await StartAsync("stdout", code: code);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Empty(await process.ReadOutputAsync(0, timeout.Token));
            Assert.Equal(unchecked((uint)code), await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Fact]
    public async Task MissingExecutableAndPrecancelKeepTheirErrors()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        Win32Exception error = await Assert.ThrowsAsync<Win32Exception>(() =>
            CapturedProcess.StartAsync(missing, "\"" + missing + "\""));
        Assert.Equal(2, error.NativeErrorCode);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        OperationCanceledException canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CapturedProcess.StartAsync(missing, "missing", cancellation.Token));
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
    }

    [Fact]
    public async Task CancelingThePipeReadLeavesTheOwnedProcessAvailableForExplicitStop()
    {
        string marker = NewMarker();
        using CapturedProcess process = await StartAsync("hold", marker: marker);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var cancellation = new CancellationTokenSource();
            Task<byte[]> read = process.ReadOutputAsync(100, cancellation.Token);
            Task<uint> wait = process.WaitForExitAsync(cancellation.Token);
            await ReadMarkerAsync(marker, timeout.Token);
            Assert.False(read.IsCompleted);
            cancellation.Cancel();
            OperationCanceledException canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, canceled.CancellationToken);
            OperationCanceledException canceledWait = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                wait.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(cancellation.Token, canceledWait.CancellationToken);
            using var owned = Process.GetProcessById(checked((int)process.Id));
            _ = owned.SafeHandle;
            Assert.False(owned.HasExited);
            process.TerminateTree(71);
            Assert.Equal(71u, await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            await StopAsync(process);
            File.Delete(marker);
        }
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("parent-wait")]
    public async Task ExplicitStopIncludesTheChildThatKeepsThePipeOpen(string mode)
    {
        string marker = NewMarker();
        using CapturedProcess process = await StartAsync(mode, marker: marker);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<byte[]> read = process.ReadOutputAsync(100, timeout.Token);
            int childId = await ReadMarkerAsync(marker, timeout.Token);
            using var child = Process.GetProcessById(childId);
            // 終了後も終了コードを取得できるよう、子が生きている間に保持します。
            _ = child.SafeHandle;
            Assert.False(child.HasExited);
            if (mode == "parent")
            {
                Assert.Equal(37u, await process.WaitForExitAsync(timeout.Token));
            }

            Assert.False(read.IsCompleted);
            process.TerminateTree(72);
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(72, child.ExitCode);
            Assert.Equal("child-ready"u8.ToArray(), await read);
            Assert.Equal(mode == "parent" ? 37u : 72u,
                await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            await StopAsync(process);
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task DisposeOnlyClosesHandlesAndRejectsFurtherOperations()
    {
        string marker = NewMarker();
        using CapturedProcess process = await StartAsync("hold", marker: marker);
        using var owned = Process.GetProcessById(checked((int)process.Id));
        _ = owned.SafeHandle;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<byte[]> read = process.ReadOutputAsync(100, timeout.Token);
            await ReadMarkerAsync(marker, timeout.Token);
            process.Dispose();
            process.Dispose();
            Exception? readError = await Record.ExceptionAsync(() =>
                read.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(readError is IOException or ObjectDisposedException
                or OperationCanceledException,
                $"実際の読取り結果: {readError?.GetType().FullName ?? "成功"}");
            Assert.False(owned.HasExited);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => process.ReadOutputAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => process.WaitForExitAsync());
            Assert.Throws<ObjectDisposedException>(() => process.TerminateTree(1));
        }
        finally
        {
            if (!owned.HasExited)
            {
                owned.Kill();
            }

            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task OverflowReportsTheExtraByteBeforeExplicitStop()
    {
        using CapturedProcess process = await StartAsync("stdout", 1024 * 1024);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ProcessOutputLimitException error = await Assert.ThrowsAsync<ProcessOutputLimitException>(() =>
                process.ReadOutputAsync(1024, timeout.Token));
            Assert.Equal(1025, error.ObservedBytes);
            using var owned = Process.GetProcessById(checked((int)process.Id));
            _ = owned.SafeHandle;
            Assert.False(owned.HasExited);
            process.TerminateTree(73);
            Assert.Equal(73u, await process.WaitForExitAsync(timeout.Token));
        }
        finally
        {
            await StopAsync(process);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopFailureAttemptsTerminationOnLastJobHandleClose(
        bool canSetLimits)
    {
        const uint jobObjectSetAttributes = 0x0002;
        const uint jobObjectQuery = 0x0004;
        nint currentProcess = -1;
        string marker = NewMarker();
        var start = new ProcessStartInfo(FixturePaths.Get(
            "UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "--process-output-probe", "idle", "0", "37", marker
        })
        {
            start.ArgumentList.Add(argument);
        }

        using Process child = Process.Start(start)!;
        try
        {
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Assert.Equal(child.Id, await ReadMarkerAsync(marker, ready.Token));
            using SafeFileHandle job = SchedulerProcessGroup.CreateJobObjectW(0, null);
            Assert.False(job.IsInvalid);
            Assert.True(AssignProcessToJobObject(job, child.SafeHandle),
                $"AssignProcessToJobObject: {Marshal.GetLastWin32Error()}");

            uint rights = jobObjectQuery;
            if (canSetLimits)
            {
                rights |= jobObjectSetAttributes;
            }

            Assert.True(DuplicateHandle(currentProcess, job, currentProcess,
                out SafeFileHandle limitedJob, rights, false, 0),
                $"DuplicateHandle: {Marshal.GetLastWin32Error()}");
            using var captured = new CapturedProcess(
                new SafeFileHandle(child.Handle, ownsHandle: false),
                limitedJob, null, (uint)child.Id);
            job.Dispose();

            var primary = new IOException();
            await captured.StopAfterFailureAsync(primary);
            Win32Exception stop = Assert.IsType<Win32Exception>(
                primary.Data["コマンド取得失敗後の停止"]);
            Assert.Equal(5, stop.NativeErrorCode);
            Assert.False(child.HasExited);

            captured.Dispose();
            object? fallback = primary.Data["コマンド取得失敗後のハンドル解放による停止"];
            if (canSetLimits)
            {
                Assert.Null(fallback);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                Assert.Equal(5, Assert.IsType<Win32Exception>(fallback).NativeErrorCode);
                Assert.False(child.HasExited);
            }
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            File.Delete(marker);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess,
        SafeFileHandle source, nint targetProcess,
        out SafeFileHandle target, uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    private static Task<CapturedProcess> StartAsync(string mode,
        int size = 0, int code = 37, string? marker = null)
    {
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture",
            "UWSCSharp.ControlFixture.exe");
        string command = $"\"{executable}\" --process-output-probe {mode} {size.ToString(CultureInfo.InvariantCulture)} {code.ToString(CultureInfo.InvariantCulture)}";
        if (marker is not null)
        {
            command += $" \"{marker}\"";
        }

        return CapturedProcess.StartAsync(executable, command);
    }

    private static string NewMarker() => Path.Combine(Path.GetTempPath(),
        "UWSCSharp-output-" + Guid.NewGuid().ToString("N") + ".txt");

    private static async Task<int> ReadMarkerAsync(string marker,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(marker))
            {
                try
                {
                    return int.Parse(await File.ReadAllTextAsync(marker, cancellationToken),
                        CultureInfo.InvariantCulture);
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
                {
                    // 共有・ロック違反だけを期限内で再試行し、他の読取り障害は通知する。
                }
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task StopAsync(CapturedProcess process)
    {
        process.TerminateTree(1);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
