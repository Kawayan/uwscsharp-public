using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class StartupDefaultFontFrontendTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "SLCT_BTN")]
    [InlineData(false, "SLCT_LST")]
    [InlineData(false, "SLCT_CMB")]
    [InlineData(true, "SLCT_BTN")]
    [InlineData(true, "SLCT_LST")]
    [InlineData(true, "SLCT_CMB")]
    public async Task NativeControlFontsAreReadInsideTheirOwningProcess(
        bool worker, string selectionKind)
    {
        bool buttonSelection = selectionKind == "SLCT_BTN";
        int selectionButton = buttonSelection ? 0x5001 : 4097;
        string title = "uwsc-local-font-" + Guid.NewGuid();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var interpreter = new Interpreter(windows: new WindowsAutomation());
        Task run = interpreter.RunAsync($$"""
            PUBLIC finished=FALSE
            {{(worker ? "THREAD ShowDialogs()" : "ShowDialogs()")}}
            WHILE !finished
                SLEEP(0.01)
            WEND
            PROCEDURE ShowDialogs()
                OPTION DLGTITLE="{{title}}-initial"
                MSGBOX("configured")
                OPTION DLGTITLE="{{title}}-input"
                INPUT("input","seed")
                OPTION DLGTITLE="{{title}}-select"
                SLCTBOX({{selectionKind}}+SLCT_NUM,0,,"one","two")
                OPTION DEFAULTFONT="Consolas,11"
                OPTION DLGTITLE="{{title}}-after"
                MSGBOX("overridden")
                OPTION DLGTITLE="{{title}}-after-input"
                INPUT("input","seed")
                OPTION DLGTITLE="{{title}}-after-select"
                SLCTBOX({{selectionKind}}+SLCT_NUM,0,,"one","two")
                finished=TRUE
            FEND
            """, _ => { }, stop.Token,
            new ScriptRunOptions([]) { DefaultFont = "Arial,18" });
        Exception? primary = null;
        try
        {
            foreach ((string suffix, string family, int points, int buttonId) in new[]
            {
                ("initial", "Arial", 18, 4097),
                ("input", "Arial", 18, 4097),
                ("select", "Arial", 18, selectionButton),
                ("after", "Consolas", 11, 4097),
                ("after-input", "Consolas", 11, 4097),
                ("after-select", "Consolas", 11, selectionButton)
            })
            {
                nint window = await MessageBoxIntegrationTests.FindWindowAsync(title + "-" + suffix,
                    Environment.ProcessId);
                uint dpi = GetDpiForWindow(window);
                Assert.NotEqual(0u, dpi);
                int expectedHeight = -(int)Math.Ceiling(points * dpi / 72d);
                int[] controlIds = suffix.EndsWith("input", StringComparison.Ordinal)
                    || (!buttonSelection && suffix.EndsWith(
                        "select", StringComparison.Ordinal))
                    ? [buttonId, 200, 201] : [buttonId, 200];
                foreach (int controlId in controlIds)
                {
                    nint control = MessageBoxIntegrationTests.GetDlgItem(
                        window, controlId);
                    Native.GetWindowThreadProcessId(control, out uint owner);
                    Assert.Equal((uint)Environment.ProcessId, owner);
                    if (controlId == 200)
                    {
                        AssertMessageFont(control, family, points);
                        continue;
                    }

                    Native.SendMessageTimeoutW(control, 0x31, 0, 0, 2, 1000,
                        out nuint font);
                    Assert.NotEqual((nuint)0, font);
                    Assert.NotEqual(0, GetObjectW((nint)font,
                        Marshal.SizeOf<LogFont>(), out LogFont actual));
                    Assert.Equal(family, actual.FaceName);
                    Assert.Equal(expectedHeight, actual.Height);
                    output.WriteLine("local-font=" + JsonSerializer.Serialize(new
                    {
                        worker,
                        selectionKind,
                        suffix,
                        controlId,
                        dpi,
                        family = actual.FaceName,
                        height = actual.Height
                    }));
                }

                nint button = MessageBoxIntegrationTests.GetDlgItem(
                    window, buttonId);
                Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
            }

            await run.WaitAsync(TimeSpan.FromSeconds(10));
            run = interpreter.RunAsync($$"""
                OPTION DLGTITLE="{{title}}-reset"
                MSGBOX("next execution")
                """, _ => { }, stop.Token);
            nint reset = await MessageBoxIntegrationTests.FindWindowAsync(title + "-reset",
                Environment.ProcessId);
            nint resetButton = MessageBoxIntegrationTests.GetDlgItem(reset, 4097);
            Native.SendMessageTimeoutW(resetButton, 0x31, 0, 0, 2, 1000, out nuint resetFont);
            Assert.NotEqual(0, GetObjectW((nint)resetFont, Marshal.SizeOf<LogFont>(), out LogFont resetValue));
            Assert.Equal("Tahoma", resetValue.FaceName);
            Assert.Equal(-(int)Math.Ceiling(9 * GetDpiForWindow(reset) / 72d), resetValue.Height);
            Assert.True(Native.PostMessageW(resetButton, 0xf5, 0, 0));
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            stop.Cancel();
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["回収失敗"] = cleanup;
                output.WriteLine("cleanup=" + cleanup);
            }
        }
    }

    [Theory]
    [InlineData("SLCT_BTN", 0x5000)]
    [InlineData("SLCT_LST", 4097)]
    [InlineData("SLCT_CMB", 4097)]
    public async Task SelectionSizeGrowsWithDefaultFontAndLongItems(
        string kind, int buttonId)
    {
        string title = "uwsc-selection-font-size-" + Guid.NewGuid();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var interpreter = new Interpreter(windows: new WindowsAutomation());
        Task run = interpreter.RunAsync($$"""
            OPTION DEFAULTFONT="Arial,11"
            OPTION DLGTITLE="{{title}}-small"
            SLCTBOX({{kind}},0,"probe","AAAAAAAAAA","BBBBBBBBBB")
            OPTION DEFAULTFONT="Arial,18"
            OPTION DLGTITLE="{{title}}-large"
            SLCTBOX({{kind}},0,"probe","AAAAAAAAAA","BBBBBBBBBB")
            OPTION DLGTITLE="{{title}}-long"
            SLCTBOX({{kind}},0,"probe","{{new string('W', 240)}}","B")
            OPTION DLGTITLE="{{title}}-longer"
            SLCTBOX({{kind}},0,"probe","{{new string('W', 4096)}}","B")
            OPTION DLGTITLE="{{title}}-positioned"
            SLCTBOX({{kind}},0,350,160,"probe","{{new string('W', 4096)}}","B")
            """, _ => { }, stop.Token);
        try
        {
            (int Width, int Height, uint Dpi) small = await ReadSizeAsync("small");
            (int Width, int Height, uint Dpi) large = await ReadSizeAsync("large");
            Assert.Equal(small.Dpi, large.Dpi);
            Assert.Equal(small.Width, large.Width);
            Assert.True(large.Height > small.Height);
            (int Width, int Height, uint Dpi) longItems =
                await ReadSizeAsync("long");
            Assert.Equal(large.Dpi, longItems.Dpi);
            Assert.True(longItems.Width > large.Width);
            Assert.Equal(large.Height, longItems.Height);
            (int Width, int Height, uint Dpi) longerItems =
                await ReadSizeAsync("longer", checkWorkArea: true);
            Assert.Equal(large.Dpi, longerItems.Dpi);
            Assert.True(longerItems.Width >= longItems.Width);
            Assert.Equal(large.Height, longerItems.Height);
            (int Width, int Height, uint Dpi) positioned =
                await ReadSizeAsync("positioned", position: (350, 160));
            Assert.Equal(longerItems, positioned);
            output.WriteLine($"selection-size kind={kind} dpi={small.Dpi} "
                + $"small={small.Width}x{small.Height} "
                + $"large={large.Width}x{large.Height} "
                + $"longer={longerItems.Width}x{longerItems.Height}");
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        }

        async Task<(int Width, int Height, uint Dpi)> ReadSizeAsync(
            string suffix, bool checkWorkArea = false,
            (int X, int Y)? position = null)
        {
            nint window = await MessageBoxIntegrationTests.FindWindowAsync(
                title + "-" + suffix, Environment.ProcessId);
            using var dpiScope = new PhysicalDpiScope();
            Assert.True(Native.GetWindowRect(window, out Native.Rect rect));
            if (position is { } expected)
            {
                Assert.Equal(expected, (rect.Left, rect.Top));
            }
            if (checkWorkArea)
            {
                nint monitor = Native.MonitorFromWindow(window, 2);
                var info = new Native.MonitorInfo
                {
                    Size = (uint)Marshal.SizeOf<Native.MonitorInfo>()
                };
                Assert.True(Native.GetMonitorInfoW(monitor, ref info));
                Assert.InRange(rect.Left, info.Work.Left, info.Work.Right);
                Assert.InRange(rect.Top, info.Work.Top, info.Work.Bottom);
                Assert.InRange(rect.Right, rect.Left + 1, info.Work.Right);
                Assert.InRange(rect.Bottom, rect.Top + 1, info.Work.Bottom);
            }
            uint dpi = GetDpiForWindow(window);
            Assert.NotEqual(0u, dpi);
            nint button = MessageBoxIntegrationTests.GetDlgItem(window, buttonId);
            Assert.NotEqual(0, button);
            Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
            return (rect.Right - rect.Left, rect.Bottom - rect.Top, dpi);
        }
    }

    private static void AssertMessageFont(nint control, string family,
        int points)
    {
        const int formatSize = 92;
        nint format = Marshal.AllocHGlobal(formatSize);
        try
        {
            Marshal.Copy(new byte[formatSize], 0, format, formatSize);
            Marshal.WriteInt32(format, formatSize);
            Assert.NotEqual((nint)0, Native.SendMessageTimeoutW(control,
                0x043a, 0, format, 2, 1000, out nuint mask));
            Assert.Equal((nuint)0xa0000000, mask & 0xa0000000);
            Assert.Equal(points * 20, Marshal.ReadInt32(format, 12));
            Assert.Equal(family, Marshal.PtrToStringUni(format + 26));
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", false)]
    [InlineData("UWSCSharp.Cli", true)]
    [InlineData("UWSCSharp.Wpf", false)]
    [InlineData("UWSCSharp.Wpf", true)]
    public async Task IniFontFlowsThroughCallAndThreadAndOptionOverridesIt(
        string frontend, bool worker)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-startup-font-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string profile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");
        byte[]? previous = File.Exists(profile) ? await File.ReadAllBytesAsync(profile) : null;
        Exception? primary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            await File.WriteAllTextAsync(profile, "[SET]\nLogLines=3\nLogFont=Consolas,12\n");
            await new ApplicationWindowSettings(false, new ScriptDialogFont("Arial", 18))
                .SaveAsync(profile);
            string settings = await File.ReadAllTextAsync(profile);
            string title = "uwsc-initial-font-" + Guid.NewGuid();
            string main = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(main, $$"""
                PUBLIC finished=FALSE
                {{(worker ? "THREAD ShowDialogs()" : "ShowDialogs()")}}
                WHILE !finished
                    SLEEP(0.01)
                WEND
                PROCEDURE ShowDialogs()
                    CALL child
                    finished=TRUE
                FEND
                """, new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), $$"""
                OPTION DLGTITLE="{{title}}-initial"
                MSGBOX("configured")
                OPTION DLGTITLE="{{title}}-input"
                INPUT("input","seed")
                OPTION DLGTITLE="{{title}}-select"
                SLCTBOX(SLCT_BTN+SLCT_NUM,0,,"one","two")
                OPTION DEFAULTFONT="Consolas,11"
                OPTION DLGTITLE="{{title}}-after"
                MSGBOX("overridden")
                """, new UTF8Encoding(true));
            var start = new ProcessStartInfo(FixturePaths.Get(frontend, frontend + ".exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(main);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                foreach ((string suffix, string family, int points, int buttonId) in new[]
                {
                    ("initial", "Arial", 18, 4097),
                    ("input", "Arial", 18, 4097),
                    ("select", "Arial", 18, 0x5001),
                    ("after", "Consolas", 11, 4097)
                })
                {
                    nint window = await MessageBoxIntegrationTests.FindWindowAsync(title + "-" + suffix, process.Id);
                    if (suffix != "select")
                    {
                        (string actualFamily, int twips) = ReadRemoteRichEditFormat(process,
                            MessageBoxIntegrationTests.GetDlgItem(window, 200));
                        Assert.Equal(family, actualFamily);
                        int expected = (int)Math.Round(Math.Ceiling(points * GetDpiForWindow(window) / 72d)
                            * 1440d / GetDpiForWindow(window), MidpointRounding.AwayFromZero);
                        Assert.Equal(expected, twips);
                        output.WriteLine("startup-font=" + JsonSerializer.Serialize(new
                        {
                            frontend,
                            worker,
                            suffix,
                            family = actualFamily,
                            twips
                        }));
                    }
                    nint button = MessageBoxIntegrationTests.GetDlgItem(window, buttonId);
                    Assert.NotEqual(0, button);
                    Assert.True(Native.PostMessageW(button, 0xf5, 0, 0));
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(process.ExitCode == 0, $"{process.ExitCode}\n{await stdout}\n{await stderr}");
                Assert.Equal(settings, await File.ReadAllTextAsync(profile));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                if (previous is null)
                {
                    if (File.Exists(profile))
                    {
                        File.Delete(profile);
                    }
                }
                else
                {
                    await File.WriteAllBytesAsync(profile, previous);
                }

                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        Directory.Delete(directory, true);
                        break;
                    }
                    catch (IOException) when (attempt < 9)
                    {
                        await Task.Delay(100);
                    }
                }
            }
            catch (Exception cleanup)
            {
                if (primary is null)
                {
                    throw;
                }

                primary.Data["回収失敗"] = cleanup;
                output.WriteLine("cleanup=" + cleanup);
            }
        }
    }

    private static (string Family, int Twips) ReadRemoteRichEditFormat(Process process, nint text)
    {
        Assert.NotEqual(0, text);
        byte[] bytes = new byte[92];
        BitConverter.GetBytes(bytes.Length).CopyTo(bytes, 0);
        nint memory = Native.VirtualAllocEx(process.Handle, 0, (nuint)bytes.Length, 0x3000, 4);
        Assert.NotEqual(0, memory);
        bool pending = false;
        try
        {
            Assert.True(Native.WriteProcessMemory(process.Handle, memory,
                bytes, (nuint)bytes.Length, out nuint written));
            Assert.Equal((nuint)bytes.Length, written);
            pending = true;
            nint sent = Native.SendMessageTimeoutW(text, 0x43a, 0, memory, 2, 1000, out nuint mask);
            if (sent != 0)
            {
                pending = false;
            }

            Assert.NotEqual(0, sent);
            Assert.Equal(0xa0000000u, (uint)mask & 0xa0000000u);
            Assert.True(Native.ReadProcessMemory(process.Handle, memory,
                bytes, (nuint)bytes.Length, out nuint read));
            Assert.Equal((nuint)bytes.Length, read);
            return (Encoding.Unicode.GetString(bytes, 26, 64).TrimEnd('\0'),
                BitConverter.ToInt32(bytes, 12));
        }
        finally
        {
            // 応答未了なら領域を解放せず、所有する子プロセスの終了時に回収します。
            if (!pending && !process.HasExited)
            {
                Assert.True(Native.VirtualFreeEx(process.Handle, memory, 0, 0x8000));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LogFont
    {
        internal int Height;
        internal int Width;
        internal int Escapement;
        internal int Orientation;
        internal int Weight;
        internal byte Italic;
        internal byte Underline;
        internal byte StrikeOut;
        internal byte CharacterSet;
        internal byte OutPrecision;
        internal byte ClipPrecision;
        internal byte Quality;
        internal byte PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string FaceName;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetObjectW(nint item, int size, out LogFont font);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
