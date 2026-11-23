using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AssociationLaunchTests
{
    [Fact]
    public async Task WindowsShellOpensARegisteredUwsFile()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        const string classes = @"Software\Classes\";
        const string extension = ".uws";
        string program = "UWSCSharp.AssociationProbe." + Guid.NewGuid().ToString("N");
        using RegistryKey? existing = Registry.ClassesRoot.OpenSubKey(extension);
        using RegistryKey? userChoice = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.uws\UserChoice");
        Assert.Null(existing);
        Assert.Null(userChoice);
        var fixture = AssociationLaunchFixture.Create();
        Exception? primary = null;
        bool programCreated = false;
        bool extensionCreated = false;
        try
        {
            using (RegistryKey command = Registry.CurrentUser.CreateSubKey(
                classes + program + @"\shell\open\command"))
            {
                programCreated = true;
                command.SetValue("", $"\"{fixture.Executable}\" \"%1\"");
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                classes + extension))
            {
                extensionCreated = true;
                key.SetValue("", program);
            }

            SHChangeNotify(0x08000000, 0, 0, 0);
            string directory = Path.Combine(fixture.DirectoryPath, "日本語 scripts");
            Directory.CreateDirectory(directory);
            string script = Path.Combine(directory, "実行 script.uws");
            await File.WriteAllTextAsync(script, """
                DIM f=FOPEN("result.txt",F_WRITE8)
                FPUT(f,"shell-open",0)
                FCLOSE(f)
                EXITEXIT(0)
                """, new UTF8Encoding(true));
            Process process = fixture.OpenAssociatedScript(script);
            using var deadline =
                new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            string receipt = await File.ReadAllTextAsync(
                Path.Combine(directory, "result.txt"), deadline.Token);
            Assert.Equal("shell-open", receipt.Trim());
            Assert.False(File.Exists(Path.Combine(fixture.AppDirectory, "result.txt")));
        }
        catch (Exception failure)
        {
            primary = failure;
            throw;
        }
        finally
        {
            try
            {
                if (extensionCreated)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(classes + extension);
                }

                if (programCreated)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(classes + program);
                }

                SHChangeNotify(0x08000000, 0, 0, 0);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["関連付けの回収エラー"] = cleanup;
            }
            finally
            {
                await fixture.CleanupAsync(primary);
            }
        }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint change, uint flags,
        nint first, nint second);

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task AssociationLaunchUsesScriptDirectoryAndKeepsFailures(int exitCode)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-association-" + Guid.NewGuid());
        string appDirectory = Path.Combine(directory, "app");
        string scriptDirectory = Path.Combine(directory, "日本語 scripts");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(scriptDirectory);
        string original = FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe");
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(original)!))
        {
            string copied = Path.Combine(appDirectory, Path.GetFileName(file));
            File.Copy(file, copied);
            File.SetAttributes(copied, FileAttributes.Normal);
        }

        string executable = Path.Combine(appDirectory, "UWSCSharp.Association.exe");
        File.Copy(original, executable);
        File.SetAttributes(executable, FileAttributes.Normal);
        string script = Path.Combine(scriptDirectory, "実行 script.uws");
        await File.WriteAllTextAsync(script, $$"""
            DIM f=FOPEN("result.txt",F_WRITE8)
            FPUT(f,PARAM_STR[0],0)
            FCLOSE(f)
            EXITEXIT({{exitCode}})
            """, new UTF8Encoding(true));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = appDirectory
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("引数 with spaces");
        using var process = new Process { StartInfo = start };
        bool started = false;
        Exception? primary = null;
        try
        {
            started = process.Start();
            Assert.True(started);
            string result = Path.Combine(scriptDirectory, "result.txt");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            while (!File.Exists(result) || new FileInfo(result).Length == 0)
            {
                Assert.False(process.HasExited, "結果を出す前に終了しました。");
                await Task.Delay(50, timeout.Token);
            }

            Assert.Equal("引数 with spaces", (await File.ReadAllTextAsync(result)).Trim());
            if (exitCode == 0)
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
            }
            else
            {
                await Task.Delay(1500, timeout.Token);
                process.Refresh();
                Assert.False(process.HasExited, "異常終了後の画面が残っていません。");
                Assert.NotEqual(nint.Zero, process.MainWindowHandle);
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
                if (started && !process.HasExited)
                {
                    if (process.CloseMainWindow())
                    {
                        try
                        {
                            await process.WaitForExitAsync()
                                .WaitAsync(TimeSpan.FromSeconds(10));
                        }
                        catch (TimeoutException)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync();
                        }
                    }
                    else
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }

                Directory.Delete(directory, recursive: true);
            }
            catch (Exception cleanup) when (primary is not null)
            {
                primary.Data["検査後の回収エラー"] = cleanup;
            }
        }
    }
}
