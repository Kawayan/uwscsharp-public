using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputLockDirectTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("explicit", "completed", -1, -1)]
    [InlineData("explicit", "completed", -1, -4)]
    [InlineData("explicit", "completed", -4, -1)]
    [InlineData("explicit", "completed", -4, -4)]
    [InlineData("normal", "completed", -1, -1)]
    [InlineData("normal", "completed", -1, -4)]
    [InlineData("normal", "completed", -4, -1)]
    [InlineData("normal", "completed", -4, -4)]
    [InlineData("cancel", "cancelled", -1, -1)]
    [InlineData("cancel", "cancelled", -1, -4)]
    [InlineData("cancel", "cancelled", -4, -1)]
    [InlineData("cancel", "cancelled", -4, -4)]
    [InlineData("error", "script-error", -1, -1)]
    [InlineData("error", "script-error", -1, -4)]
    [InlineData("error", "script-error", -4, -1)]
    [InlineData("error", "script-error", -4, -4)]
    [InlineData("omitted", "completed", -1, -1)]
    [InlineData("omitted", "completed", -1, -4)]
    [InlineData("omitted", "completed", -4, -1)]
    [InlineData("omitted", "completed", -4, -4)]
    [InlineData("child", "completed", -1, -1)]
    [InlineData("child", "completed", -1, -4)]
    [InlineData("child", "completed", -4, -1)]
    [InlineData("child", "completed", -4, -4)]
    [InlineData("omitted-child", "completed", -1, -1)]
    [InlineData("omitted-child", "completed", -1, -4)]
    [InlineData("omitted-child", "completed", -4, -1)]
    [InlineData("omitted-child", "completed", -4, -4)]
    public async Task DirectMessagesArriveDuringBlockAndReleaseAtRequestedStage(string ending, string outcome, int dpiContext, int receiverDpiContext)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string progress = Path.Combine(@"C:\ui-output", "input-direct-" + Guid.NewGuid() + ".log");
        void Trace(string stage)
        {
            if (Directory.Exists(@"C:\ui-output"))
            {
                File.AppendAllText(progress, DateTimeOffset.UtcNow.ToString("O") + " " + ending + " " + stage + Environment.NewLine);
            }
        }
        Trace("fixture-start");
        var messages = new ConcurrentQueue<(uint Id, long WParam, long LParam)>();
        var wrongTarget = new ConcurrentQueue<uint>();
        bool childMode = ending.Contains("child", StringComparison.Ordinal);
        nint child = 0;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            owner.InputMessage = (id, wParam, lParam) =>
            {
                if (childMode && id != 0x200)
                {
                    wrongTarget.Enqueue(id);
                }
                else
                {
                    messages.Enqueue((id, (long)wParam, (long)lParam));
                }
            };
            if (childMode)
            {
                child = owner.Child("EDIT", "child", 0, 10, height: 100);
                owner.ObserveInput(child, (id, wParam, lParam) =>
                    messages.Enqueue((id, (long)wParam, (long)lParam)));
            }
        }, dpiAwarenessContext: receiverDpiContext);
        long expectedMovePosition = await ExpectedPositionAsync(fixture.Root, messages, new(40, 50));
        if (receiverDpiContext == -4)
        {
            Assert.Equal(40L | (50L << 16), expectedMovePosition);
        }
        messages.Clear();
        long expectedPosition = expectedMovePosition;
        if (childMode)
        {
            Native.Point point;
            using (var dpi = new PhysicalDpiScope())
            {
                var origin = new Native.Point(0, 0);
                Assert.True(Native.ClientToScreen(fixture.Root, ref origin));
                Assert.True(Native.GetWindowRect(child, out Native.Rect bounds));
                point = new Native.Point(origin.X + 40 - bounds.Left, origin.Y + 50 - bounds.Top);
                Assert.InRange(point.X, 0, bounds.Right - bounds.Left - 1);
                Assert.InRange(point.Y, 0, bounds.Bottom - bounds.Top - 1);
            }
            expectedPosition = await ExpectedPositionAsync(child, messages, point);
            messages.Clear();
        }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            dpiContext,
            receiverDpiContext,
            expectedPosition
        }));
        Trace("fixture-ready");
        string receipt = Path.Combine(Path.GetTempPath(), "uwsc-input-direct-" + Guid.NewGuid() + ".json");
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        try
        {
            await File.WriteAllTextAsync(receipt + ".target", ((long)fixture.Root).ToString(CultureInfo.InvariantCulture));
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" --input-lock-direct-probe \"{receipt}\" {ending} {dpiContext}", TimeSpan.FromSeconds(15));
            Trace("watchdog-started");
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            await File.WriteAllTextAsync(receipt + ".go", "go");
            Trace("go-sent");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(receipt + ".held") || !messages.Any(m => m.Id == 0x100 && m.WParam == 135)
                || !messages.Any(m => m.Id == 0x207 && m.LParam == expectedPosition))
            {
                Assert.False(process.HasExited);
                await Task.Delay(10, timeout.Token);
            }
            Trace("downs-received");
            AssertBlocked();
            Assert.DoesNotContain(messages, m => m.Id is 0x101 or 0x208);
            await File.WriteAllTextAsync(receipt + ".ack", "ack");
            Trace("held-acknowledged");
            if (ending == "explicit")
            {
                while (!File.Exists(receipt + ".up") || !messages.Any(m => m.Id == 0x101 && m.WParam == 135)
                    || !messages.Any(m => m.Id == 0x208 && m.LParam == expectedPosition))
                {
                    Assert.False(process.HasExited);
                    await Task.Delay(10, timeout.Token);
                }
                Trace("explicit-ups-received");
                AssertBlocked();
                await File.WriteAllTextAsync(receipt + ".up.ack", "ack");
                while (!File.Exists(receipt + ".released"))
                {
                    Assert.False(process.HasExited);
                    await Task.Delay(10, timeout.Token);
                }
                Trace("explicit-release-received");
                var available = new NativeInputOwnershipBackend();
                available.SetBlocked(true);
                available.SetBlocked(false);
                await File.WriteAllTextAsync(receipt + ".released.ack", "ack");
            }
            while (!File.Exists(receipt) || !messages.Any(m => m.Id == 0x101 && m.WParam == 135)
                || !messages.Any(m => m.Id == 0x208 && m.LParam == expectedPosition))
            {
                Assert.False(process.HasExited);
                await Task.Delay(10, timeout.Token);
            }
            Trace("final-receipt-and-ups");
            string text = await File.ReadAllTextAsync(receipt, timeout.Token);
            using var document = JsonDocument.Parse(text);
            JsonElement result = document.RootElement;
            Assert.False(process.HasExited);
            Assert.Equal(process.Id, result.GetProperty("process_id").GetInt32());
            Assert.Equal(dpiContext, result.GetProperty("dpiContext").GetInt32());
            Assert.True(result.GetProperty("dpi").GetUInt32() > 0);
            Assert.NotEqual(96u, result.GetProperty("dpi").GetUInt32());
            Assert.Equal(outcome, result.GetProperty("outcome").GetString());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("failure").ValueKind);
            foreach (string flag in new[] { "blocked", "pointerUnchanged", "focusUnchanged", "physicalKeyUp", "recovered" })
            {
                Assert.True(result.GetProperty(flag).GetBoolean(), flag);
            }
            Assert.Contains(messages, m => m.Id == 0x200 && m.LParam == expectedMovePosition);
            Assert.Empty(wrongTarget);
            foreach (uint id in new uint[] { 0x100, 0x101, 0x207, 0x208 })
            {
                Assert.Single(messages, m => m.Id == id);
            }
            await File.WriteAllTextAsync(receipt + ".exit", "exit");
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Trace("probe-exited");
        }
        finally
        {
            Trace("watchdog-disposed");
            if (File.Exists(receipt))
            {
                output.WriteLine(await File.ReadAllTextAsync(receipt));
            }
            output.WriteLine(JsonSerializer.Serialize(messages.Select(m => new { m.Id, m.WParam, m.LParam })));
            foreach (string suffix in new[] { "", ".pending", ".target", ".go", ".held", ".ack", ".up", ".up.ack", ".released", ".released.ack", ".exit" })
            {
                File.Delete(receipt + suffix);
            }
        }
    }

    private static async Task<long> ExpectedPositionAsync(nint window,
        ConcurrentQueue<(uint Id, long WParam, long LParam)> messages, Native.Point point)
    {
        // 製品を通さないPMv2の送信を基準に、OSの受信側DPI変換を観測します。
        // 非対応受信側の丸め規則を、画面座標の変換APIから推測しません。
        const uint marker = 0x4000;
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.PostMessageW(window, 0x200, (nint)marker,
                unchecked((nint)((uint)(ushort)point.X | ((uint)(ushort)point.Y << 16)))));
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!messages.Any(message => message.Id == 0x200 && message.WParam == marker))
        {
            await Task.Delay(10, timeout.Token);
        }
        return Assert.Single(messages,
            message => message.Id == 0x200 && message.WParam == marker).LParam;
    }

    private static void AssertBlocked()
    {
        var contender = new NativeInputOwnershipBackend();
        bool rejected = false;
        try
        {
            contender.SetBlocked(true);
        }
        catch (Win32Exception)
        {
            rejected = true;
        }
        if (!rejected)
        {
            contender.SetBlocked(false);
        }
        Assert.True(rejected);
    }
}
