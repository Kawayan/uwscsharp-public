using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UWSCSharp.IO;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class BalloonDefaultFontFrontendTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("UWSCSharp.Cli", false, false)]
    [InlineData("UWSCSharp.Cli", false, true)]
    [InlineData("UWSCSharp.Cli", true, false)]
    [InlineData("UWSCSharp.Cli", true, true)]
    [InlineData("UWSCSharp.Wpf", false, false)]
    [InlineData("UWSCSharp.Wpf", false, true)]
    [InlineData("UWSCSharp.Wpf", true, false)]
    [InlineData("UWSCSharp.Wpf", true, true)]
    public async Task IniAndOptionFontsMatchExplicitRenderingThroughCallAndThread(
        string frontend, bool worker, bool fixedDesktop)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-balloon-font-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string profile = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "UWSC", "UWSC.INI");
        byte[]? previous = File.Exists(profile) ? await File.ReadAllBytesAsync(profile) : null;
        Exception? primary = null;
        try
        {
            await new ApplicationWindowSettings(false, new ScriptDialogFont("Arial", 18))
                .SaveAsync(profile);
            byte[] settings = await File.ReadAllBytesAsync(profile);
            string main = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(main, $$"""
                {{(fixedDesktop ? "OPTION FIXBALLOON" : "")}}
                PUBLIC finished=FALSE
                {{(worker ? "THREAD ShowStages()" : "ShowStages()")}}
                WHILE !finished
                    SLEEP(0.01)
                WEND
                PROCEDURE ShowStages()
                    CALL child
                    finished=TRUE
                FEND
                """, new UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"), $$"""
                CONST Folder="{{directory}}/"
                CONST Text="WWWWiiiiMMMM"
                BALLOON(Text,80,90,0,,,0,$FFFFFF,0)
                Checkpoint(1)
                FUKIDASI(Text,80,90,0,18,"Arial",0,$FFFFFF,0)
                Checkpoint(2)
                OPTION DEFAULTFONT="Consolas,11"
                Checkpoint(3)
                FUKIDASI(Text,80,90,0,,,0,$FFFFFF,0)
                Checkpoint(4)
                BALLOON(Text,80,90,0,11,"Consolas",0,$FFFFFF,0)
                Checkpoint(5)
                BALLOON(Text,80,90,0,22,,0,$FFFFFF,0)
                Checkpoint(6)
                FUKIDASI(Text,80,90,0,22,"Consolas",0,$FFFFFF,0)
                Checkpoint(7)
                FUKIDASI(Text,80,90,0,,"Arial",0,$FFFFFF,0)
                Checkpoint(8)
                BALLOON(Text,80,90,0,11,"Arial",0,$FFFFFF,0)
                Checkpoint(9)
                BALLOON(Text,80,90,0,18,"Arial",0,$FFFFFF,0)
                Checkpoint(10)
                OPTION DEFAULTFONT="Arial,18"
                FUKIDASI(Text,80,90,0,,,0,$FFFFFF,0)
                Checkpoint(11)
                OPTION DEFAULTFONT="Consolas,11"
                FUKIDASI(Text,80,90,0,,,0,$FFFFFF,0)
                Checkpoint(12)
                BALLOON(Text,80,90,0,11,"Consolas",0,$FFFFFF,0)
                Checkpoint(13)
                BALLOON()
                Checkpoint(14)
                OPTION DEFAULTFONT="Arial,18"
                FUKIDASI(Text,80,90,0,,,0,$FFFFFF,0)
                Checkpoint(15)
                BALLOON(Text,80,90,0,18,"Arial",0,$FFFFFF,0)
                Checkpoint(16)
                PROCEDURE Checkpoint(stage)
                    DIM file=FOPEN(Folder+stage+".txt",F_WRITE8)
                    FPUT(file,IDTOHND(GETID(GET_BALLOON_WIN)),0)
                    FCLOSE(file)
                    WHILE !FOPEN(Folder+stage+".go",F_EXISTS)
                        SLEEP(0.01)
                    WEND
                FEND
                """, new UTF8Encoding(true));
            var start = new ProcessStartInfo(FixturePaths.Get(frontend, frontend + ".exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(main);
            using Process process = Process.Start(start)!;
            nint window = 0;
            try
            {
                var frames = new Dictionary<int, CapturedFrame>();
                for (int stage = 1; stage <= 16; stage++)
                {
                    nint handle = await WaitHandle(Path.Combine(directory, stage + ".txt"), process);
                    if (stage == 1)
                    {
                        window = handle;
                    }

                    Assert.Equal(window, handle);
                    Assert.True(Native.IsWindow(handle));
                    Native.GetWindowThreadProcessId(handle, out uint owner);
                    Assert.Equal((uint)process.Id, owner);
                    uint dpi = GetDpiForWindow(handle);
                    Assert.NotEqual(0u, dpi);
                    uint style = unchecked((uint)Native.GetWindowLongW(handle, -16));
                    Assert.Equal(fixedDesktop, (style & 0x40000000) != 0);
                    Assert.Equal(stage != 14, Native.IsWindowVisible(handle));
                    if (stage != 14)
                    {
                        CapturedFrame frame = await CaptureStableFrame(handle);
                        frames.Add(stage, frame);
                        output.WriteLine("balloon-font=" + JsonSerializer.Serialize(new
                        {
                            frontend,
                            worker,
                            fixedDesktop,
                            dpi,
                            stage,
                            frame.Signature.Width,
                            frame.Signature.Height,
                            frame.Signature.RegionPixels,
                            frame.Signature.Hash
                        }));
                    }

                    await File.WriteAllTextAsync(Path.Combine(directory, stage + ".go"), "continue");
                }

                foreach ((int actual, int control) in new[]
                {
                    (1, 2), (1, 3), (4, 5), (6, 7), (8, 9),
                    (10, 11), (12, 13), (15, 16)
                })
                {
                    if (frames[control].Signature != frames[actual].Signature)
                    {
                        WriteImage(actual, frames[actual].Image);
                        WriteImage(control, frames[control].Image);
                    }

                    Assert.Equal(frames[control].Signature, frames[actual].Signature);
                }

                Assert.NotEqual(frames[1].Signature, frames[4].Signature);
                Assert.NotEqual(frames[4].Signature, frames[6].Signature);
                Assert.NotEqual(frames[4].Signature, frames[8].Signature);
                Assert.NotEqual(frames[10].Signature, frames[12].Signature);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(0, process.ExitCode);
                Assert.False(Native.IsWindow(window));
                Assert.Equal(settings, await File.ReadAllBytesAsync(profile));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
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

    private static async Task<nint> WaitHandle(string path, Process process)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            Assert.False(process.HasExited, "吹出しの通知前に実行元が終了しました。");
            try
            {
                if (File.Exists(path) && long.TryParse(await File.ReadAllTextAsync(path),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out long handle))
                {
                    return checked((nint)handle);
                }
            }
            catch (IOException)
            {
                // 通知を閉じるまで待機し、書込み途中の値を採用しません。
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("吹出しの通知が生成されませんでした。");
    }

    private static async Task<CapturedFrame> CaptureStableFrame(nint window)
    {
        CapturedFrame? previous = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100);
            CapturedFrame current = Capture(window);
            if (current.Signature == previous?.Signature)
            {
                return current;
            }

            previous = current;
        }

        throw new TimeoutException("吹出しの描画が安定しませんでした。");
    }

    private static CapturedFrame Capture(nint window)
    {
        using var dpi = new PhysicalDpiScope();
        Assert.True(Native.GetWindowRect(window, out Native.Rect bounds));
        PixelImage image = new NativeImageCapture().Capture(new(bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top), CancellationToken.None);
        nint region = CreateRectRgn(0, 0, 0, 0);
        Assert.NotEqual(0, region);
        int regionPixels = 0;
        try
        {
            Assert.InRange(GetWindowRgn(window, region), 2, 3);
            for (int index = 0; index < image.Pixels.Length; index++)
            {
                // 画面採取に混ざる領域外の背景を、吹出しの描画と比較しません。
                if (PtInRegion(region, index % image.Width, index / image.Width))
                {
                    image.Pixels[index] &= 0xFFFFFF;
                    regionPixels++;
                }
                else
                {
                    image.Pixels[index] = 0;
                }
            }
        }
        finally
        {
            Assert.True(DeleteObject(region));
        }

        Assert.InRange(regionPixels, 1, image.Pixels.Length);
        Assert.Contains(image.Pixels, color => color == 0);
        Assert.Contains(image.Pixels, color => color == 0xFFFFFF);
        string hash = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes(image.Pixels.AsSpan())));
        return new CapturedFrame(new Frame(image.Width, image.Height, regionPixels, hash), image);
    }

    private void WriteImage(int stage, PixelImage image)
    {
        string path = Path.Combine(Path.GetTempPath(), "uwsc-font-evidence-" + Guid.NewGuid() + ".bmp");
        try
        {
            image.SaveBmp(path, CancellationToken.None);
            byte[] bytes = File.ReadAllBytes(path);
            output.WriteLine("balloon-font-image=" + JsonSerializer.Serialize(new
            {
                stage,
                bytes = bytes.Length,
                bitmap = Convert.ToBase64String(bytes)
            }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);

    [DllImport("gdi32.dll")]
    private static extern bool PtInRegion(nint region, int x, int y);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint value);

    private sealed record Frame(int Width, int Height, int RegionPixels, string Hash);

    private sealed record CapturedFrame(Frame Signature, PixelImage Image);
}
