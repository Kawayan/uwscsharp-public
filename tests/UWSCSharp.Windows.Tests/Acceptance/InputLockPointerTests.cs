using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class InputLockPointerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("mmv", -1)]
    [InlineData("mmv", -4)]
    [InlineData("button", -1)]
    [InlineData("button", -4)]
    [InlineData("getstr-edit", -1)]
    [InlineData("getstr-edit", -4)]
    [InlineData("getstr-static", -1)]
    [InlineData("getstr-static", -4)]
    [InlineData("getstr-status", -1)]
    [InlineData("getstr-status", -4)]
    [InlineData("getstr-acc-edit", -1)]
    [InlineData("getstr-acc-edit", -4)]
    [InlineData("getstr-acc-static", -1)]
    [InlineData("getstr-acc-static", -4)]
    [InlineData("getstr-acc-cell", -1)]
    [InlineData("getstr-acc-cell", -4)]
    [InlineData("getstr-uia-edit", -1)]
    [InlineData("getstr-uia-edit", -4)]
    [InlineData("getstr-uia-static", -1)]
    [InlineData("getstr-uia-static", -4)]
    [InlineData("getstr-uia-status", -1)]
    [InlineData("getstr-uia-status", -4)]
    public async Task PointerCoordinatesAreAppliedWhileBlocked(string mode, int dpiContext)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string receipt = Path.Combine(Path.GetTempPath(), "uwsc-input-pointer-" + Guid.NewGuid() + ".json");
        string executable = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        nint child = 0;
        bool uia = mode.StartsWith("getstr-uia-", StringComparison.Ordinal);
        bool acc = mode.StartsWith("getstr-acc-", StringComparison.Ordinal);
        await using PumpedControlFixture? fixture = mode.StartsWith("getstr-", StringComparison.Ordinal) && !uia
            ? await PumpedControlFixture.CreateAsync(owner =>
            {
                if (acc)
                {
                    int role = mode == "getstr-acc-edit" ? 42 : mode == "getstr-acc-static" ? 41 : 29;
                    owner.AccessibleObject = parameter =>
                    {
                        Guid iid = typeof(IAccessibleNative).GUID;
                        var provider = new AccessibleTestProvider(10, 0, "", "",
                            new AccessibleTestProvider(role, 0, "入力禁止中の本文", "入力禁止中の本文"));
                        return LresultFromObject(ref iid, (nuint)parameter, provider);
                    };
                }
                else
                {
                    child = mode == "getstr-status"
                        ? StatusControlTests.Create(owner, 20, 0, "入力禁止中の本文", "対照")
                        : owner.Child(mode == "getstr-edit" ? "EDIT" : "STATIC", "入力禁止中の本文", 0, 20);
                }
            }, initializeOle: acc, dpiAwarenessContext: -4)
            : null;
        SandboxProcessWatchdog? uiaWatchdog = null;
        try
        {
            if (fixture is not null)
            {
                Native.Rect bounds;
                using (var dpi = new PhysicalDpiScope())
                {
                    if (acc)
                    {
                        // 検証providerが公開するLocationを期待値とします。
                        bounds = new Native.Rect { Left = 10, Top = 10, Right = 110, Bottom = 30 };
                    }
                    else if (mode == "getstr-status")
                    {
                        bounds = ReadStatusBounds(child);
                    }
                    else
                    {
                        Assert.True(Native.GetWindowRect(child, out bounds));
                    }
                }
                Assert.True(bounds.Right - bounds.Left >= 8 && bounds.Bottom - bounds.Top >= 8);
                Assert.False(100 >= bounds.Left && 100 < bounds.Right
                    && 100 >= bounds.Top && 100 < bounds.Bottom);
                await File.WriteAllTextAsync(receipt + ".target", JsonSerializer.Serialize(new
                {
                    window = (long)fixture.Root,
                    left = bounds.Left,
                    top = bounds.Top,
                    right = bounds.Right,
                    bottom = bounds.Bottom
                }));
            }
            if (uia)
            {
                string uiaExecutable = FixturePaths.Get("UWSCSharp.UiaFixture", "WPFApplication.UWSCSharp.UiaFixture.exe");
                uiaWatchdog = await SandboxProcessWatchdog.StartAsync(uiaExecutable,
                    $"\"{uiaExecutable}\" --getstr-target \"{receipt}.target\" {mode[11..]}", TimeSpan.FromSeconds(30));
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (!File.Exists(receipt + ".target"))
                {
                    await Task.Delay(10, ready.Token);
                }
                using var target = JsonDocument.Parse(await File.ReadAllTextAsync(receipt + ".target"));
                JsonElement bounds = target.RootElement;
                Assert.True(bounds.GetProperty("right").GetInt32() - bounds.GetProperty("left").GetInt32() >= 8);
                Assert.True(bounds.GetProperty("bottom").GetInt32() - bounds.GetProperty("top").GetInt32() >= 8);
                Assert.True(bounds.GetProperty("left").GetInt32() > 100);
            }
            await using SandboxProcessWatchdog watchdog = await SandboxProcessWatchdog.StartAsync(
                executable, $"\"{executable}\" --input-lock-pointer-probe \"{receipt}\" {mode} {dpiContext}",
                TimeSpan.FromSeconds(15));
            using var process = Process.GetProcessById(checked((int)watchdog.ProcessId));
            _ = process.SafeHandle;
            await File.WriteAllTextAsync(receipt + ".go", "go");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(receipt))
            {
                Assert.False(process.HasExited);
                await Task.Delay(10, timeout.Token);
            }
            string text = await File.ReadAllTextAsync(receipt, timeout.Token);
            output.WriteLine(text);
            using var document = JsonDocument.Parse(text);
            JsonElement result = document.RootElement;
            Assert.False(process.HasExited);
            Assert.Equal(dpiContext, result.GetProperty("dpiContext").GetInt32());
            Assert.True(result.GetProperty("dpi").GetUInt32() > 0);
            if (fixture is not null || uia)
            {
                Assert.NotEqual(96u, result.GetProperty("dpi").GetUInt32());
            }
            Assert.Equal(process.Id, result.GetProperty("process_id").GetInt32());
            Assert.Equal(JsonValueKind.Null, result.GetProperty("failure").ValueKind);
            foreach (string flag in new[] { "initial", "initiallyBlocked", "moved", "blocked", "recovered", "textMatched" })
            {
                Assert.True(result.GetProperty(flag).GetBoolean(), flag);
            }
            await File.WriteAllTextAsync(receipt + ".exit", "exit");
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (uiaWatchdog is not null)
            {
                await uiaWatchdog.DisposeAsync();
            }
            foreach (string suffix in new[] { "", ".pending", ".go", ".exit", ".target", ".target.pending" })
            {
                File.Delete(receipt + suffix);
            }
        }
    }

    [DllImport("oleacc.dll")]
    private static extern nint LresultFromObject(ref Guid iid, nuint parameter,
        [MarshalAs(UnmanagedType.IUnknown)] object provider);

    private static Native.Rect ReadStatusBounds(nint status)
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<Native.Rect>());
        try
        {
            Assert.NotEqual(0, Native.SendMessageW(status, 0x40a, 0, pointer));
            Native.Rect bounds = Marshal.PtrToStructure<Native.Rect>(pointer);
            var origin = new Native.Point(0, 0);
            Assert.True(Native.ClientToScreen(status, ref origin));
            return new Native.Rect
            {
                Left = bounds.Left + origin.X,
                Top = bounds.Top + origin.Y,
                Right = bounds.Right + origin.X,
                Bottom = bounds.Bottom + origin.Y
            };
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
