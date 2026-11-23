using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputLockTouchTests(ITestOutputHelper output)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Touch
    {
        internal int X;
        internal int Y;
        internal nint Source;
        internal uint Id;
        internal uint Flags;
        internal uint Mask;
        internal uint Time;
        internal nuint Extra;
        internal uint Width;
        internal uint Height;
    }

    [Theory]
    [InlineData(false, "explicit", "completed", 1)]
    [InlineData(true, "explicit", "completed", 1)]
    [InlineData(false, "move", "completed", 1)]
    [InlineData(true, "move", "completed", 1)]
    [InlineData(false, "normal", "completed", 1)]
    [InlineData(true, "normal", "completed", 1)]
    [InlineData(false, "error", "script-error", 1)]
    [InlineData(true, "error", "script-error", 1)]
    [InlineData(false, "cancel", "cancelled", 1)]
    [InlineData(true, "cancel", "cancelled", 1)]
    [InlineData(false, "exit", "completed", 1)]
    [InlineData(true, "exit", "completed", 1)]
    [InlineData(false, "explicit", "completed", 2)]
    [InlineData(true, "explicit", "completed", 2)]
    [InlineData(false, "move", "completed", 2)]
    [InlineData(true, "move", "completed", 2)]
    [InlineData(false, "normal", "completed", 2)]
    [InlineData(true, "normal", "completed", 2)]
    [InlineData(false, "error", "script-error", 2)]
    [InlineData(true, "error", "script-error", 2)]
    [InlineData(false, "cancel", "cancelled", 2)]
    [InlineData(true, "cancel", "cancelled", 2)]
    [InlineData(false, "exit", "completed", 2)]
    [InlineData(true, "exit", "completed", 2)]
    [InlineData(false, "move-cancel", "cancelled", 1)]
    [InlineData(true, "move-cancel", "cancelled", 1)]
    [InlineData(false, "move-cancel", "cancelled", 2)]
    [InlineData(true, "move-cancel", "cancelled", 2)]
    [InlineData(false, "reuse", "completed", 1)]
    [InlineData(true, "reuse", "completed", 1)]
    [InlineData(false, "reuse", "completed", 2)]
    [InlineData(true, "reuse", "completed", 2)]
    [InlineData(false, "thread", "completed", 1)]
    [InlineData(true, "thread", "completed", 1)]
    [InlineData(false, "thread", "completed", 2)]
    [InlineData(true, "thread", "completed", 2)]
    [InlineData(false, "run-reuse", "completed", 1)]
    [InlineData(true, "run-reuse", "completed", 1)]
    [InlineData(false, "run-reuse", "completed", 2)]
    [InlineData(true, "run-reuse", "completed", 2)]
    [InlineData(false, "thread-reuse", "completed", 1)]
    [InlineData(true, "thread-reuse", "completed", 1)]
    [InlineData(false, "thread-reuse", "completed", 2)]
    [InlineData(true, "thread-reuse", "completed", 2)]
    [InlineData(false, "thread-error", "script-error", 1)]
    [InlineData(true, "thread-error", "script-error", 1)]
    [InlineData(false, "thread-error", "script-error", 2)]
    [InlineData(true, "thread-error", "script-error", 2)]
    [InlineData(false, "thread-cancel", "cancelled", 1)]
    [InlineData(true, "thread-cancel", "cancelled", 1)]
    [InlineData(false, "thread-cancel", "cancelled", 2)]
    [InlineData(true, "thread-cancel", "cancelled", 2)]
    public async Task TouchReachesOwnedWindowAndReleases(bool blocked, string ending, string outcome, int contactCount)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var contacts = new ConcurrentQueue<Touch>();
        string cancellationCondition = ending == "move-cancel" ? "AwaitingMovement" : "NotApplicable";
        Exception? callbackFailure = null;
        await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(owner =>
        {
            Assert.True(RegisterTouchWindow(owner.Root, 0));
            owner.InputMessage = (message, wParam, lParam) =>
            {
                if (message != 0x240)
                {
                    return;
                }
                try
                {
                    uint count = unchecked((uint)(long)wParam) & 0xffff;
                    var frame = new Touch[count];
                    if (!GetTouchInputInfo(lParam, count, frame, Marshal.SizeOf<Touch>()))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    foreach (Touch contact in frame)
                    {
                        contacts.Enqueue(contact);
                    }
                }
                catch (Exception error)
                {
                    callbackFailure = error;
                }
                // ハンドルの閉鎖は後続の既定Window Procedureに任せます。
            };
        }, dpiAwarenessContext: -4);
        var point = new Native.Point(0, 0);
        using (var dpi = new PhysicalDpiScope())
        {
            Assert.True(Native.ClientToScreen(fixture.Root, ref point));
        }
        point.X += 80;
        point.Y += 80;
        string receipt = Path.Combine(Path.GetTempPath(), "uwsc-input-touch-" + Guid.NewGuid() + ".json");
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        try
        {
            await File.WriteAllTextAsync(receipt + ".target", $"{point.X},{point.Y}");
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" --input-lock-touch-probe \"{receipt}\" {blocked} {ending} {contactCount}",
                TimeSpan.FromSeconds(15));
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            await File.WriteAllTextAsync(receipt + ".go", "go");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            async Task WaitAsync(Func<bool> condition)
            {
                while (!condition())
                {
                    Assert.False(process.HasExited);
                    Assert.Null(callbackFailure);
                    if (File.Exists(receipt))
                    {
                        using var early = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
                        Assert.Equal(JsonValueKind.Null, early.RootElement.GetProperty("failure").ValueKind);
                    }
                    await Task.Delay(10, timeout.Token);
                }
                Assert.Null(callbackFailure);
            }
            await WaitAsync(() => File.Exists(receipt + ".held") && contacts.Count(c => (c.Flags & 2) != 0) == contactCount);
            AssertBlocked(blocked);
            Touch[] downs = [.. contacts.Where(c => (c.Flags & 2) != 0).OrderBy(c => c.X)];
            Assert.Equal(contactCount, downs.Select(c => c.Id).Distinct().Count());
            for (int index = 0; index < contactCount; index++)
            {
                Assert.Equal((point.X + index * 40) * 100, downs[index].X);
                Assert.Equal(point.Y * 100, downs[index].Y);
            }
            Assert.DoesNotContain(contacts, c => (c.Flags & 4) != 0);
            await File.WriteAllTextAsync(receipt + ".held.ack", "ack");
            if (ending == "move-cancel")
            {
                await WaitAsync(() => downs.All(down => contacts.Any(c => c.Id == down.Id
                    && (c.Flags & 1) != 0 && (c.X != down.X || c.Y != down.Y))));
                if (contacts.Any(c => (c.Flags & 4) != 0))
                {
                    cancellationCondition = "CompletedBeforeRequest";
                    Assert.Fail("検証条件不成立: 取消要求前にUPを受信しました。途中取消の成否を判定できません。");
                }
                AssertBlocked(blocked);
                cancellationCondition = "RequestedDuringMovement";
                await File.WriteAllTextAsync(receipt + ".cancel", "cancel");
            }
            bool reuses = ending is "reuse" or "run-reuse" or "thread-reuse";
            if (ending is "explicit" or "move" or "thread" || reuses)
            {
                await WaitAsync(() => File.Exists(receipt + ".up") && contacts.Count(c => (c.Flags & 4) != 0) == contactCount);
                // 別実行では最初の実行が終了した時点で入力禁止も解放されます。
                AssertBlocked(ending == "run-reuse" ? false : blocked);
                if (reuses)
                {
                    foreach (Touch down in downs)
                    {
                        Touch up = Assert.Single(contacts, c => c.Id == down.Id && (c.Flags & 4) != 0);
                        Assert.Equal(down.X, up.X);
                        Assert.Equal(down.Y, up.Y);
                    }
                    output.WriteLine("再使用前: " + JsonSerializer.Serialize(contacts.Select(c => new { c.Id, c.X, c.Y, c.Flags })));
                    contacts.Clear();
                }
                await File.WriteAllTextAsync(receipt + ".up.ack", "ack");
            }
            if (reuses)
            {
                await WaitAsync(() => File.Exists(receipt + ".held-reuse")
                    && contacts.Count(c => (c.Flags & 2) != 0) == contactCount);
                AssertBlocked(blocked);
                downs = [.. contacts.Where(c => (c.Flags & 2) != 0).OrderBy(c => c.X)];
                Assert.Equal(contactCount, downs.Select(c => c.Id).Distinct().Count());
                for (int index = 0; index < contactCount; index++)
                {
                    Assert.Equal((point.X + index * 40) * 100, downs[index].X);
                    Assert.Equal(point.Y * 100, downs[index].Y);
                }
                Assert.DoesNotContain(contacts, c => (c.Flags & 4) != 0);
                await File.WriteAllTextAsync(receipt + ".held-reuse.ack", "ack");
                await WaitAsync(() => File.Exists(receipt + ".up-reuse")
                    && contacts.Count(c => (c.Flags & 4) != 0) == contactCount);
                AssertBlocked(blocked);
                await File.WriteAllTextAsync(receipt + ".up-reuse.ack", "ack");
            }
            await WaitAsync(() => File.Exists(receipt) && contacts.Count(c => (c.Flags & 4) != 0) == contactCount);
            for (int index = 0; index < contactCount; index++)
            {
                Touch down = downs[index];
                Touch up = Assert.Single(contacts, c => (c.Flags & 4) != 0 && c.Id == down.Id);
                int dx = ending == "move" ? 12 - index * 4 : 0;
                int dy = ending == "move" ? 8 + index * 4 : 0;
                if (ending == "move-cancel")
                {
                    Touch lastMove = contacts.Last(c => c.Id == down.Id && (c.Flags & 1) != 0);
                    Assert.Equal(lastMove.X, up.X);
                    Assert.Equal(lastMove.Y, up.Y);
                    Assert.InRange(up.X, down.X, down.X + (100 - index * 20) * 100);
                    Assert.InRange(up.Y, down.Y, down.Y + (60 + index * 20) * 100);
                    Assert.True(up.X != down.X || up.Y != down.Y);
                    if (up.X == down.X + (100 - index * 20) * 100
                        && up.Y == down.Y + (60 + index * 20) * 100)
                    {
                        cancellationCondition = "ReachedEndpoint";
                        Assert.Fail("検証条件不成立: 取消処理までに予定した終点へ到達しました。途中取消の成否を判定できません。");
                    }
                }
                else
                {
                    Assert.Equal(down.X + dx * 100, up.X);
                    Assert.Equal(down.Y + dy * 100, up.Y);
                }
                if (ending == "move")
                {
                    Assert.Contains(contacts, c => c.Id == down.Id && (c.Flags & 1) != 0
                        && (c.X != down.X || c.Y != down.Y));
                }
            }
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
            Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("failure").ValueKind);
            Assert.Equal(outcome, result.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(contactCount, result.RootElement.GetProperty("contactCount").GetInt32());
            Assert.Equal(ending == "run-reuse" ? 2 : 1,
                result.RootElement.GetProperty("runCount").GetInt32());
            if (outcome == "completed")
            {
                Assert.Equal(ending == "exit" ? 7 : 0, result.RootElement.GetProperty("exitCode").GetInt32());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("exitCode").ValueKind);
            }
            Assert.True(result.RootElement.GetProperty("recovered").GetBoolean());
            Assert.Empty(result.RootElement.GetProperty("cleanupErrors").EnumerateArray());
            Assert.Equal(ending == "move-cancel", result.RootElement.GetProperty("cancelObserved").GetBoolean());
            Assert.False(process.HasExited);
            if (ending == "move-cancel")
            {
                cancellationCondition = "Established";
            }
            await File.WriteAllTextAsync(receipt + ".exit", "exit");
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Null(callbackFailure);
        }
        finally
        {
            if (File.Exists(receipt))
            {
                output.WriteLine(await File.ReadAllTextAsync(receipt));
            }
            output.WriteLine(JsonSerializer.Serialize(contacts.Select(c => new { c.Id, c.X, c.Y, c.Flags })));
            output.WriteLine(callbackFailure?.ToString() ?? "callback成功");
            output.WriteLine(JsonSerializer.Serialize(new
            {
                cancellationCondition
            }));
            foreach (string suffix in new[] { "", ".pending", ".target", ".ready", ".go", ".held", ".held.ack", ".up", ".up.ack", ".finished", ".exit", ".cancel", ".held-reuse", ".held-reuse.ack", ".up-reuse", ".up-reuse.ack" })
            {
                File.Delete(receipt + suffix);
            }
        }
    }

    private static void AssertBlocked(bool expected)
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
        Assert.Equal(expected, rejected);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterTouchWindow(nint window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTouchInputInfo(nint handle, uint count,
        [Out] Touch[] contacts, int size);
}
