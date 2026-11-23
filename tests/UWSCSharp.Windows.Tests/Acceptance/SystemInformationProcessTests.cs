using System.Diagnostics;
using System.Reflection;
using System.Text;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SystemInformationProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliAndWpfExposeSystemInformationAndMatchingRelativePaths(bool wpf)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null
            && !File.Exists(Path.Combine(root.FullName, "UWSCSharp.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string configuration = Path.GetFileName(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        string name = wpf ? "UWSCSharp.Wpf" : "UWSCSharp.Cli";
        string binaryDirectory = Path.Combine(root.FullName,
            "artifacts", "bin", name, configuration);
        string executable = Path.Combine(binaryDirectory, name + ".exe");
        Assert.True(File.Exists(executable), executable);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-system-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string receipt = Path.Combine(directory, "result.txt");
            string main = Path.Combine(directory, "日本語 main.uws");
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"),
                "log=log+\"CHILD_CUR=\"+GET_CUR_DIR+\"<#CR>\"",
                new UTF8Encoding(true));
            await File.WriteAllTextAsync(main, """
                PUBLIC log="SYS="+GET_SYS_DIR+"<#CR>"
                log=log+"CUR="+GET_CUR_DIR+"<#CR>"
                log=log+"VER="+GET_UWSC_VER+"<#CR>"
                log=log+"PRO="+GET_UWSC_PRO+"<#CR>"
                log=log+"OS="+KINDOFOS()+"<#CR>"
                log=log+"OS64="+KINDOFOS(OS_WIN64)+"<#CR>"
                CALL child
                log=log+"AFTER_CUR="+GET_CUR_DIR+"<#CR>"
                DIM f=FOPEN("result.txt",F_WRITE8)
                FPUT(f,log,0)
                FCLOSE(f)
                """, new UTF8Encoding(true));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = binaryDirectory
            };
            start.ArgumentList.Add(main);
            using Process process = Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(45));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            Assert.True(process.ExitCode == 0,
                $"終了コード: {process.ExitCode}\n{await stdout}\n{await stderr}");
            string version = typeof(Interpreter).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
                .InformationalVersion.Split('+', 2)[0];
            string[] lines = (await File.ReadAllTextAsync(receipt))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal([
                "SYS=" + Environment.SystemDirectory,
                "CUR=" + directory,
                "VER=" + version,
                "PRO=0", "OS=30", "OS64=1",
                "CHILD_CUR=" + directory,
                "AFTER_CUR=" + directory
            ], lines);
        }
        finally
        {
            // 所有process終了後に、この試験で作成したファイルだけを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }
}
