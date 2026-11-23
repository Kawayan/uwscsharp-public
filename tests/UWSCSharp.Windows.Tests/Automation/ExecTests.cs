using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using UWSCSharp.Core;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class ExecTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(259)]
    [InlineData(-1)]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task WaitReturnsExitCodeIncludingStillActiveNumber(int code)
    {
        using var probe = new Probe(60, code);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Value result = await session.InvokeAsync("EXEC",
            [Value.String(probe.Command), Value.Numeric(1)], timeout.Token);
        Assert.Equal(code, result.Number);
        Assert.True(File.Exists(probe.File));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task CancellationLeavesChildAliveAndSessionUsable()
    {
        using var probe = new Probe(10000, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var cancel = new CancellationTokenSource();
        Task<Value> running = session.InvokeAsync("EXEC",
            [Value.String(probe.Command), Value.Numeric(1)], cancel.Token).AsTask();
        await probe.WaitAsync();
        cancel.Cancel();
        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancel.Token, error.CancellationToken);
        using var child = Process.GetProcessById(probe.Pid);
        Assert.False(child.HasExited);
        using var second = new Probe(20, 7);
        Assert.Equal(7, (await session.InvokeAsync("EXEC",
            [Value.String(second.Command), Value.Numeric(1)], default)).Number);
        await session.DisposeAsync();
        Assert.False(child.HasExited);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task PrecancelDoesNotLaunchAndFailureIsCatchable()
    {
        using var probe = new Probe(100, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await session.InvokeAsync("EXEC", [Value.String(probe.Command)], cancel.Token));
        Assert.False(File.Exists(probe.File));
        Win32Exception error = await Assert.ThrowsAsync<Win32Exception>(async () =>
            await session.InvokeAsync("EXEC",
                [Value.String("\"" + Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe") + "\"")], default));
        Assert.NotNull(session.GetScriptErrorMessage("EXEC", error));
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync(
            "TRY\nEXEC(\"missing-exec-74bfe3.exe\")\nEXCEPT\nPRINT \"caught\"\nENDTRY", output.Add);
        Assert.Equal(["caught"], output);
    }

    [Fact]
    public async Task AsyncReturnsRegisteredWindowAndAppliesPartialGeometry()
    {
        using var probe = new Probe(10000, 0, gui: true);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Value id = await session.InvokeAsync("EXEC", [Value.String(probe.Command),
            new Value(ValueKind.Omitted), Value.Numeric(120), new Value(ValueKind.Omitted),
            Value.Numeric(460), Value.Numeric(310)], timeout.Token);
        Assert.True(id.Number > 0);
        Value handle = await session.InvokeAsync("IDTOHND", [id], default);
        Assert.Equal(probe.Window, (long)handle.Number);
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect((nint)handle.Number, out Native.Rect rect));
        output.WriteLine($"EXEC async: {rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}; fixture={probe.Details}");
        Assert.Equal(120, rect.Left);
        Assert.Equal(50, rect.Top);
        Assert.Equal(460, rect.Right - rect.Left);
        Assert.Equal(310, rect.Bottom - rect.Top);
    }

    [Fact]
    public async Task SynchronousLaunchAppliesGeometryBeforeReturningExitCode()
    {
        using var probe = new Probe(1500, 9, gui: true);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<Value> running = session.InvokeAsync("EXEC", [Value.String(probe.Command),
            Value.Numeric(1), Value.Numeric(130), Value.Numeric(140),
            Value.Numeric(470), Value.Numeric(320)], timeout.Token).AsTask();
        await probe.WaitAsync();
        bool applied = false;
        string observed = "未取得";
        while (!running.IsCompleted)
        {
            using (var dpi = new PhysicalDpiScope())
            {
                if (Native.GetWindowRect((nint)probe.Window, out Native.Rect rect))
                {
                    observed = $"{rect.Left},{rect.Top},{rect.Right - rect.Left},{rect.Bottom - rect.Top}";
                }
                if (rect.Left == 130 && rect.Top == 140
                    && rect.Right - rect.Left == 470 && rect.Bottom - rect.Top == 320)
                {
                    applied = true;
                    break;
                }
            }

            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal(9, (await running).Number);
        output.WriteLine($"EXEC sync: applied={applied}; last={observed}; fixture={probe.Details}");
        Assert.True(applied);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task AsyncWithoutWindowReturnsMinusOneAfterChildExit()
    {
        using var probe = new Probe(20, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        Assert.Equal(-1, (await session.InvokeAsync("EXEC",
            [Value.String(probe.Command)], default)).Number);
    }

    [Fact]
    [Trait("Category", "Headless")]
    public async Task LiveProcessWithoutWindowHasFiniteLookup()
    {
        using var probe = new Probe(12000, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        var watch = Stopwatch.StartNew();
        Value result = await session.InvokeAsync("EXEC",
            [Value.String(probe.Command)], default).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(-1, result.Number);
        Assert.InRange(watch.Elapsed.TotalSeconds, 4.5, 9.5);
        using var child = Process.GetProcessById(probe.Pid);
        Assert.False(child.HasExited);
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task AllArgumentsAreValidatedBeforeLaunch()
    {
        using var probe = new Probe(20, 0);
        await using IWindowsAutomationSession session = new WindowsAutomation().CreateSession();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.InvokeAsync("EXEC", [Value.String(probe.Command),
                Value.Numeric(1), new Value(ValueKind.Omitted),
                Value.Numeric(double.NaN)], default));
        Assert.False(File.Exists(probe.File));
    }

    [Fact]
    [Trait("Category", "Headless")]
    [Trait("Priority", "Critical")]
    public async Task ReceiptWaitsForSharingLockAndCachesOwnershipForCleanup()
    {
        using var probe = new Probe(20, 0);
        await System.IO.File.WriteAllTextAsync(probe.File,
            "{\"pid\":-1,\"window\":0}");
        Task<int> reading;
        using (var held = new FileStream(probe.File,
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            reading = Task.Run(() => probe.Pid);
            await Task.Delay(75);
            Assert.False(reading.IsCompleted);
        }

        Assert.Equal(-1, await reading.WaitAsync(TimeSpan.FromSeconds(5)));
        using (var held = new FileStream(probe.File,
            FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(-1, probe.Pid);
            Assert.Equal(0, probe.Window);
        }
    }

    private sealed class Probe : IDisposable
    {
        private readonly object receiptSync = new();
        private JsonElement? receipt;
        internal string File
        {
            get;
        } = Path.Combine(Path.GetTempPath(),
            "exec-" + Guid.NewGuid() + " 日本語 #.json");

        internal string Command
        {
            get;
        }

        internal int Pid => Data().GetProperty("pid").GetInt32();

        internal long Window => Data().GetProperty("window").GetInt64();

        internal string Details => Data().GetRawText();

        internal Probe(int milliseconds, int code, bool gui = false)
        {
            string executable = FixturePaths.Get("UWSCSharp.ControlFixture",
                "UWSCSharp.ControlFixture.exe");
            Command = $"\"{executable}\" --exec-probe \"{File}\" {milliseconds} {code}"
                + (gui ? " gui" : "");
        }

        private JsonElement Data()
        {
            lock (receiptSync)
            {
                if (receipt is JsonElement cached)
                {
                    return cached;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using JsonDocument document = ProcessReceiptReader.ReadJsonAsync(
                    File, timeout.Token).GetAwaiter().GetResult();
                receipt = document.RootElement.Clone();
                return receipt.Value;
            }
        }

        internal async Task WaitAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!System.IO.File.Exists(File))
            {
                await Task.Delay(10, timeout.Token);
            }

            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(
                File, timeout.Token);
            lock (receiptSync)
            {
                receipt ??= document.RootElement.Clone();
            }
        }

        public void Dispose()
        {
            if (!System.IO.File.Exists(File))
            {
                return;
            }

            try
            {
                using var process = Process.GetProcessById(Pid);
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (ArgumentException)
            {
                // 自然終了した所有プロセスには後処理が不要です。
            }
            finally
            {
                System.IO.File.Delete(File);
            }
        }
    }
}
