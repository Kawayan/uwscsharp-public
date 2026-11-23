using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AuthoringDataRuntimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "\n", true)]
    [InlineData(true, "\n", true)]
    [InlineData(false, "\r\n", true)]
    [InlineData(true, "\r\n", true)]
    [InlineData(false, "\n", false)]
    public async Task FileSumReadsThreeUtf8LinesWithoutChangingInput(
        bool bom, string newline, bool trailingNewline)
    {
        string text = string.Join(newline, "10", "20", "30");
        if (trailingNewline)
        {
            text += newline;
        }

        await RunFileCase(text, bom, true, true,
            ["合計: 60", "件数: 3"]);
    }

    [Theory]
    [InlineData("10\n20\n")]
    [InlineData("10\n20\n30\n40\n")]
    public async Task FileSumReportsUnexpectedLineCount(string text)
    {
        await RunFileCase(text, false, true, true,
            ["入力は整数だけの3行にしてください"]);
    }

    [Fact]
    public async Task FileSumReportsMissingFile()
    {
        await RunFileCase(null, false, true, false,
            ["入力ファイルを開けません"]);
    }

    [Fact]
    public async Task FileSumReportsMissingArgument()
    {
        await RunFileCase(null, false, false, false,
            ["入力ファイルのパスを指定してください"]);
    }

    [Theory]
    [InlineData("10\nabc\n30\n", "10abc30")]
    [InlineData("10\n\n30\n", "1030")]
    [InlineData("10\n2.5\n30\n", "42.5")]
    [InlineData("10\n1e2\n30\n", "140")]
    [InlineData("10\n1e5000\n30\n", "101e500030")]
    public async Task FileSumShowsActualTreatmentOfNonIntegerLines(
        string text, string result)
    {
        await RunFileCase(text, false, true, true,
            ["合計: " + result, "件数: 3"]);
    }

    [Fact]
    public async Task FileSumUsesCodePageFallbackForNonUtf8WithoutBom()
    {
        byte[] bytes = [0x31, 0x30, 0x0a, 0x82, 0xa0, 0x0a,
            0x33, 0x30, 0x0a];
        await RunBytesCase(bytes, ["合計: 10あ30", "件数: 3"]);
    }

    [Fact]
    public async Task FileSumReportsInvalidUtf8WithBomAsOpenFailure()
    {
        byte[] bytes = [0xef, 0xbb, 0xbf, 0x31, 0x30, 0x0a,
            0xff, 0x0a, 0x33, 0x30, 0x0a];
        await RunBytesCase(bytes, ["入力ファイルを開けません"]);
    }

    [Fact]
    public async Task ArraySumWorksInTwoIndependentProcesses()
    {
        await WithDirectory(async directory =>
        {
            string script = CopyScript(directory, "array-and-function-sum.uws");
            for (int index = 0; index < 2; index++)
            {
                await RunCli(directory, script, null, null, ["12"]);
            }
        });
    }

    private async Task RunFileCase(string? text, bool bom,
        bool passArgument, bool createInput, string[] expected)
    {
        await WithDirectory(async directory =>
        {
            string script = CopyScript(directory, "read-and-sum-lines.uws");
            string input = Path.Combine(directory, "入力 ファイル.txt");
            if (createInput)
            {
                var encoding = new UTF8Encoding(bom);
                byte[] bytes = [.. encoding.GetPreamble(),
                    .. encoding.GetBytes(text!)];
                await File.WriteAllBytesAsync(input, bytes);
            }

            await RunCli(directory, script, passArgument ? input : null,
                createInput ? input : null, expected);
        });
    }

    private async Task RunBytesCase(byte[] bytes, string[] expected)
    {
        await WithDirectory(async directory =>
        {
            string script = CopyScript(directory, "read-and-sum-lines.uws");
            string input = Path.Combine(directory, "入力 ファイル.txt");
            await File.WriteAllBytesAsync(input, bytes);
            await RunCli(directory, script, input, input, expected);
        });
    }

    private async Task RunCli(string directory, string script,
        string? argument, string? input, string[] expected)
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
        Assert.Contains(configuration, new[] { "debug", "release" });
        string cli = Path.Combine(root.FullName, "artifacts", "bin",
            "UWSCSharp.Cli", configuration, "UWSCSharp.Cli.exe");
        Assert.True(File.Exists(cli), cli);
        byte[]? before = input is null ? null : await File.ReadAllBytesAsync(input);
        var start = new ProcessStartInfo(cli)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(script);
        if (argument is not null)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            timedOut = true;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(12));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(12));
            }
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        byte[]? after = input is null ? null : await File.ReadAllBytesAsync(input);
        output.WriteLine("AUTHORING_DATA_RECEIPT " + JsonSerializer.Serialize(new
        {
            script = Path.GetFileName(script),
            scriptSha256 = Hash(await File.ReadAllBytesAsync(script)),
            cliSha256 = Hash(await File.ReadAllBytesAsync(cli)),
            inputBase64 = before is null ? null : Convert.ToBase64String(before),
            beforeSha256 = before is null ? null : Hash(before),
            afterSha256 = after is null ? null : Hash(after),
            argumentProvided = argument is not null,
            exitCode = process.ExitCode,
            timedOut,
            stdout,
            stderr
        }));
        Assert.False(timedOut, "CLIが制限時間内に終了しませんでした。");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("", stderr);
        Assert.Equal(expected, stdout.Replace("\r\n", "\n")
            .TrimEnd('\n').Split('\n'));
        if (before is not null)
        {
            Assert.Equal(before, after);
        }
    }

    private static string Hash(byte[] bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string CopyScript(string directory, string name)
    {
        string source = Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Authoring", name);
        string destination = Path.Combine(directory, name);
        File.Copy(source, destination);
        return destination;
    }

    private static async Task WithDirectory(Func<string, Task> action)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        string directory = Path.GetFullPath(Path.Combine(tempRoot,
            "uwsc-authoring-data-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot)
            + Path.DirectorySeparatorChar + "uwsc-authoring-data-",
            directory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(directory);
        try
        {
            await action(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
