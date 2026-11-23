using System.Diagnostics;
using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class ExecutionContextProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliAndWpfReportTheirOwnDirectoryAndRootName(bool wpf)
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
            "uwsc-context-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string receipt = Path.Combine(directory, "result.txt");
            string main = Path.Combine(directory, "日本語 main script.uws");
            await File.WriteAllTextAsync(Path.Combine(directory, "child.uws"),
                "log=log+\"CHILD=\"+GET_UWSC_NAME+\"<#CR>\"",
                new UTF8Encoding(true));
            await File.WriteAllTextAsync(main, $$"""
                PUBLIC log="WIN="+GET_WIN_DIR+"<#CR>"
                log=log+"DIR="+GET_UWSC_DIR+"<#CR>"
                log=log+"MAIN="+GET_UWSC_NAME+"<#CR>"
                CALL child
                log=log+"AFTER="+GET_UWSC_NAME+"<#CR>"
                DIM f=FOPEN("{{receipt}}",F_WRITE8)
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
            Assert.True(File.Exists(receipt), receipt);
            string[] lines = (await File.ReadAllTextAsync(receipt))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal([
                "WIN=" + Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "DIR=" + binaryDirectory,
                "MAIN=日本語 main script.uws",
                "CHILD=日本語 main script.uws",
                "AFTER=日本語 main script.uws"
            ], lines);
        }
        finally
        {
            // 所有processの終了後に、このテストの一時ファイルだけを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }
}
