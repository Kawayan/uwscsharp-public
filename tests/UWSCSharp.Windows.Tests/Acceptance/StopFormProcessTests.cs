using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

public sealed class StopFormProcessTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "normal")]
    [InlineData(false, "close")]
    [InlineData(true, "close")]
    [InlineData(false, "button")]
    [InlineData(true, "button")]
    [InlineData(false, "button", 1000, 700)]
    [InlineData(true, "button", 1000, 700)]
    [InlineData(false, "button", 1, 1)]
    [InlineData(true, "button", 1, 1)]
    public async Task CliAndWpfShareTheStopFormAcrossCallsAndWorkers(bool wpf, string finish,
        int width = 0, int height = 0)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string application = wpf ? "UWSCSharp.Wpf" : "UWSCSharp.Cli";
        string executable = Path.Combine(root.FullName, "artifacts", "bin", application,
            configuration, application + ".exe");
        Assert.True(File.Exists(executable), executable);
        string directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uwsc-stop-form-" + Guid.NewGuid()));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), directory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        Exception? failure = null;
        string profile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");
        byte[]? previousProfile = null;
        bool restoreProfile = false;
        try
        {
            if (width > 0)
            {
                Assert.Equal("WDAGUtilityAccount", Environment.UserName);
                previousProfile = File.Exists(profile) ? await File.ReadAllBytesAsync(profile) : null;
                restoreProfile = true;
                Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
                await File.WriteAllTextAsync(profile, $"[SET]\nStopSize={width},{height}\n");
            }

            await using PumpedControlFixture fixture = await PumpedControlFixture.CreateAsync(_ => { });
            Assert.True(Native.SetWindowPos(fixture.Root, 0, 20, 20, 0, 0, 0x15));
            await UiInputDriver.ActivateAsync(fixture.Root);
            string main = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), "STOPFORM(TRUE,120)", new UTF8Encoding(true));
            string[] commands =
            [
                "probe_return=EMPTY", "probe_return=STOPFORM(TRUE,80,90)",
                "STOPFORM(TRUE,,140)", "CALL child\nTHREAD HideChild()\nWHILE finished=FALSE\nSLEEP(0.01)\nWEND\nSLEEP(0.1)",
                "STOPFORM(TRUE)", "STOPFORM(TRUE,2147483647,-2147483648)", "STOPFORM(FALSE)", "STOPFORM(TRUE)"
            ];
            var source = new StringBuilder("PUBLIC finished=FALSE\nDIM probe_file,probe_id,probe_return\n");
            for (int phase = 0; phase < commands.Length; phase++)
            {
                source.AppendLine($$"""
                    {{commands[phase]}}
                    probe_id=GETID(GET_STOPFORM_WIN)
                    probe_file=FOPEN("{{Path.Combine(directory, phase + ".txt")}}",F_WRITE8)
                    FPUT(probe_file,"ID="+probe_id)
                    FPUT(probe_file,"HANDLE="+IDTOHND(probe_id))
                    FPUT(probe_file,"READY=1")
                    FCLOSE(probe_file)
                    WHILE FOPEN("{{Path.Combine(directory, phase + ".go")}}",F_EXISTS)=0
                        SLEEP(0.02)
                    WEND
                    """);
            }

            source.AppendLine($$"""
                probe_file=FOPEN("{{Path.Combine(directory, "finished.txt")}}",F_WRITE8)
                FPUT(probe_file,"DONE")
                FCLOSE(probe_file)
                PROCEDURE HideChild()
                    STOPFORM(FALSE)
                    finished=TRUE
                FEND
                """);
            await File.WriteAllTextAsync(main, source.ToString(), new UTF8Encoding(true));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(main);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            nint handle = 0;
            string? firstId = null;
            Native.Rect clamped = default;
            try
            {
                for (int phase = 0; phase < commands.Length; phase++)
                {
                    string receipt = await WaitAsync(Path.Combine(directory, phase + ".txt"), process);
                    output.WriteLine($"application={application} phase={phase}\n{receipt}");
                    string[] lines = receipt.Split('\n');
                    string id = lines.Single(line => line.StartsWith("ID="))[3..].Trim();
                    nint current = checked((nint)long.Parse(lines.Single(line => line.StartsWith("HANDLE="))[7..].Trim(), CultureInfo.InvariantCulture));
                    Assert.True(Native.IsWindow(current));
                    Native.GetWindowThreadProcessId(current, out uint owner);
                    Assert.Equal((uint)process.Id, owner);
                    if (phase == 0)
                    {
                        handle = current;
                        firstId = id;
                        Assert.Equal(wpf, IsWindowVisible(handle));
                        if (width > 0)
                        {
                            Native.Rect initial = Bounds(handle);
                            int actualWidth = initial.Right - initial.Left;
                            int actualHeight = initial.Bottom - initial.Top;
                            output.WriteLine($"stop-size requested={width},{height} actual={actualWidth},{actualHeight}");
                            if (width == 1000)
                            {
                                Assert.Equal(width, actualWidth);
                                Assert.Equal(height, actualHeight);
                            }
                            else
                            {
                                Assert.True(actualWidth >= 300);
                                Assert.True(actualHeight > height);
                            }
                        }
                    }

                    Assert.Equal(handle, current);
                    Assert.Equal(firstId, id);
                    if (phase is 1 or 2 or 4 or 5 or 7)
                    {
                        Assert.True(IsWindowVisible(handle));
                        Assert.Equal(handle, Native.GetForegroundWindow());
                        Native.Rect bounds = Bounds(handle);
                        output.WriteLine($"bounds phase={phase} left={bounds.Left} top={bounds.Top} right={bounds.Right} bottom={bounds.Bottom}");
                        if (phase == 1)
                        {
                            Assert.Equal((80, 90), (bounds.Left, bounds.Top));
                            if (finish == "normal")
                            {
                                using var dpi = new PhysicalDpiScope();
                                PixelImage image = new NativeImageCapture().Capture(new(bounds.Left, bounds.Top,
                                    bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), CancellationToken.None);
                                string imagePath = Path.Combine(directory, "stop-form.bmp");
                                image.SaveBmp(imagePath, CancellationToken.None);
                                output.WriteLine("STOP_FORM_IMAGE " + Convert.ToBase64String(File.ReadAllBytes(imagePath)));
                            }
                        }
                        if (phase == 2)
                        {
                            Assert.Equal((80, 140), (bounds.Left, bounds.Top));
                        }
                        if (phase == 4)
                        {
                            Assert.Equal((120, 140), (bounds.Left, bounds.Top));
                        }
                        if (phase == 5)
                        {
                            using var dpi = new PhysicalDpiScope();
                            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                            Assert.True(GetMonitorInfoW(MonitorFromWindow(handle, 2), ref info));
                            output.WriteLine($"work left={info.Work.Left} top={info.Work.Top} right={info.Work.Right} bottom={info.Work.Bottom}");
                            Assert.InRange(bounds.Left, info.Work.Left, info.Work.Right - (bounds.Right - bounds.Left));
                            Assert.InRange(bounds.Top, info.Work.Top, info.Work.Bottom - (bounds.Bottom - bounds.Top));
                            clamped = bounds;
                        }
                        if (phase == 7)
                        {
                            Assert.Equal((clamped.Left, clamped.Top), (bounds.Left, bounds.Top));
                        }
                    }
                    else if (phase is 3 or 6)
                    {
                        Assert.False(IsWindowVisible(handle));
                    }

                    if (phase == commands.Length - 1 && finish != "normal")
                    {
                        if (finish == "close")
                        {
                            Assert.True(Native.PostMessageW(handle, 0x10, 0, 0));
                        }
                        else
                        {
                            // ゲスト内の所有プロセスへ入力が届くことを確認してから操作します。
                            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint foreground);
                            Assert.Equal((uint)process.Id, foreground);
                            foreach (uint flags in new uint[] { 0, 2 })
                            {
                                NativeInputSender.Send(new Native.Input
                                {
                                    Type = 1,
                                    Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Vk = 32, Flags = flags } }
                                });
                            }
                        }
                        break;
                    }

                    await UiInputDriver.ActivateAsync(fixture.Root);
                    Assert.True(AllowSetForegroundWindow((uint)process.Id));
                    string acknowledgement = Path.Combine(directory, phase + ".go");
                    await File.WriteAllTextAsync(acknowledgement + ".pending", "ready");
                    File.Move(acknowledgement + ".pending", acknowledgement);
                }

                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                output.WriteLine("stdout=" + await stdout);
                output.WriteLine("stderr=" + await stderr);
                Assert.Equal(finish == "normal" ? 0 : 130, process.ExitCode);
                Assert.Equal(finish == "normal", File.Exists(Path.Combine(directory, "finished.txt")));
                Assert.False(Native.IsWindow(handle));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }

                output.WriteLine("final_stdout=" + await stdout);
                output.WriteLine("final_stderr=" + await stderr);
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            if (restoreProfile)
            {
                if (previousProfile is null)
                {
                    File.Delete(profile);
                }
                else
                {
                    await File.WriteAllBytesAsync(profile, previousProfile);
                }
            }

            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    break;
                }
                catch (IOException error) when (attempt < 9 || failure is not null)
                {
                    output.WriteLine($"cleanup_attempt={attempt + 1} error={error.Message}");
                    await Task.Delay(100);
                }
            }
        }
    }

    private static Native.Rect Bounds(nint window)
    {
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(window, out Native.Rect bounds));
        return bounds;
    }

    private static async Task<string> WaitAsync(string path, Process process)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(path))
            {
                try
                {
                    string text = await File.ReadAllTextAsync(path);
                    if (text.Contains("READY=1", StringComparison.Ordinal))
                    {
                        return text;
                    }
                }
                catch (IOException)
                {
                    // 同じ実行が受領を書き終えるまで待ちます。
                }
            }
            Assert.False(process.HasExited, "受領の出力前に実行が終了しました。");
            await Task.Delay(25);
        }
        throw new TimeoutException("停止画面の受領がありません。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal Native.Rect Monitor;
        internal Native.Rect Work;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);
}
