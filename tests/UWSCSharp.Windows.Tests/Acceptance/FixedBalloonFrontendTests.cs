using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class FixedBalloonFrontendTests
{
    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task PublicOptionPreservesTheBalloonUntilTheFrontendExits(string frontend)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(
            temporaryRoot, "uwsc-fixed-balloon-" + Guid.NewGuid()));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(temporaryRoot)
            + Path.DirectorySeparatorChar, directory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        try
        {
            string command = Path.Combine(directory, "command.txt");
            string ready = Path.Combine(directory, "ready.txt");
            string updated = Path.Combine(directory, "updated.txt");
            string script = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(command, "wait", new UTF8Encoding(true));
            string source = $$"""
                OPTION FIXBALLOON
                BALLOON("first",80,90)
                WriteReceipt("{{ready}}")
                WaitCommand("update")
                BALLOON()
                FUKIDASI("again",100,120)
                WriteReceipt("{{updated}}")
                WaitCommand("finish")
                EXITEXIT(7)
                PROCEDURE WriteReceipt(path)
                    DIM file=FOPEN(path,F_WRITE8)
                    FPUT(file,IDTOHND(GETID(GET_BALLOON_WIN)),0)
                    FCLOSE(file)
                FEND
                PROCEDURE WaitCommand(expected)
                    WHILE TRUE
                        DIM file=FOPEN("{{command}}",F_READ)
                        DIM value=FGET(file,1)
                        FCLOSE(file)
                        IF value=expected THEN BREAK
                        SLEEP(0.02)
                    WEND
                FEND
                """;
            _ = ScriptCompiler.CompileWithoutExternalFiles(source);
            Assert.Empty(ScriptCallAnalyzer.Analyze(source).Diagnostics);
            await File.WriteAllTextAsync(script, source, new UTF8Encoding(true));
            string executable = FixturePaths.Get(frontend, frontend + ".exe");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(script);
            using Process process = Process.Start(start)!;
            try
            {
                nint window = await WaitHandle(ready, process);
                AssertOwnedDesktopChild(window, process.Id);
                await File.WriteAllTextAsync(command, "update", new UTF8Encoding(true));
                Assert.Equal(window, await WaitHandle(updated, process));
                AssertOwnedDesktopChild(window, process.Id);
                await File.WriteAllTextAsync(command, "finish", new UTF8Encoding(true));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(7, process.ExitCode);
                Assert.False(Native.IsWindow(window));
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
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertOwnedDesktopChild(nint window, int process)
    {
        Assert.True(Native.IsWindow(window));
        Assert.True(Native.IsWindowVisible(window));
        Native.GetWindowThreadProcessId(window, out uint owner);
        Assert.Equal((uint)process, owner);
        Assert.Equal(GetDesktopWindow(), Native.GetParent(window));
        Assert.NotEqual(0u, unchecked((uint)Native.GetWindowLongW(window, -16)) & 0x40000000);
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
                // 実行元が通知を閉じるまで待機します。
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("吹出しのハンドル通知が生成されませんでした。");
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
