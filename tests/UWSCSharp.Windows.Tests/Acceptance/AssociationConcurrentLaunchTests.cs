using System.Diagnostics;
using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AssociationConcurrentLaunchTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ConcurrentLaunchesKeepArgumentsAndLifetimesIndependent(
        bool separateDirectories,
        bool failFirst)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create();
        string directory = fixture.DirectoryPath;
        string appDirectory = fixture.AppDirectory;
        var processes = new List<Process>();
        var scriptDirectories = new List<string>();
        Exception? primary = null;
        try
        {
            for (int index = 0; index < 2; index++)
            {
                string scriptDirectory = Path.Combine(directory,
                    separateDirectories ? $"日本語 scripts {index}" : "共通 scripts");
                Directory.CreateDirectory(scriptDirectory);
                scriptDirectories.Add(scriptDirectory);
                string script = Path.Combine(scriptDirectory, "同時実行.uws");
                if (!File.Exists(script))
                {
                    await File.WriteAllTextAsync(script, """
                        DIM name=PARAM_STR[0]
                        DIM f=FOPEN(name+".ready",F_WRITE8)
                        FPUT(f,name,0)
                        FCLOSE(f)
                        WHILE !FOPEN(name+".go",F_EXISTS)
                            SLEEP(0.05)
                        WEND
                        f=FOPEN(name+".result",F_WRITE8)
                        FPUT(f,name,0)
                        FCLOSE(f)
                        EXITEXIT(VAL(PARAM_STR[1]))
                        """, new UTF8Encoding(true));
                }

                Process process = fixture.Start(script, $"引数 {index}",
                    failFirst && index == 0 ? "7" : "0");
                processes.Add(process);
            }

            Assert.NotEqual(processes[0].Id, processes[1].Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            for (int index = 0; index < 2; index++)
            {
                string ready = Path.Combine(scriptDirectories[index], $"引数 {index}.ready");
                while (!File.Exists(ready) || new FileInfo(ready).Length == 0)
                {
                    Assert.False(processes[index].HasExited,
                        "待機開始前にプロセスが終了しました。");
                    await Task.Delay(50, timeout.Token);
                }

                Assert.Equal($"引数 {index}", (await File.ReadAllTextAsync(ready)).Trim());
            }

            // 両方が実行中であることを確認してから片方だけを終了させる。
            Assert.All(processes, process => Assert.False(process.HasExited));
            for (int index = 0; index < 2; index++)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(scriptDirectories[index], $"引数 {index}.go"), "go");
                string result = Path.Combine(scriptDirectories[index], $"引数 {index}.result");
                if (failFirst && index == 0)
                {
                    while (!File.Exists(result) || new FileInfo(result).Length == 0)
                    {
                        Assert.False(processes[index].HasExited);
                        await Task.Delay(50, timeout.Token);
                    }

                    await Task.Delay(1500, timeout.Token);
                    processes[index].Refresh();
                    Assert.False(processes[index].HasExited,
                        "非ゼロ終了後の画面が残っていません。");
                    Assert.NotEqual(nint.Zero, processes[index].MainWindowHandle);
                }
                else
                {
                    await processes[index].WaitForExitAsync(timeout.Token);
                    Assert.Equal(0, processes[index].ExitCode);
                }

                Assert.Equal($"引数 {index}", (await File.ReadAllTextAsync(result)).Trim());
                if (index == 0)
                {
                    Assert.False(processes[1].HasExited,
                        "先の実行終了で後の実行も終了しました。");
                    Assert.False(File.Exists(
                        Path.Combine(scriptDirectories[1], "引数 1.result")));
                }
            }

            if (failFirst)
            {
                Assert.False(processes[0].HasExited,
                    "他方の正常終了後に非ゼロ終了の画面が失われました。");
                Assert.True(processes[0].CloseMainWindow());
                await processes[0].WaitForExitAsync(timeout.Token);
            }

            Assert.Empty(Directory.EnumerateFiles(appDirectory, "*.ready"));
            Assert.Empty(Directory.EnumerateFiles(appDirectory, "*.result"));
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await fixture.CleanupAsync(primary);
        }
    }
}
