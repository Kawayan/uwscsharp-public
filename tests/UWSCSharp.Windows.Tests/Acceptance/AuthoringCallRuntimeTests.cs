using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class AuthoringCallRuntimeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("PRINT \"子ファイル\"\n", true)]
    [InlineData(null, false)]
    [InlineData("// 検証用の構文エラー\nENDIF\n", false)]
    public async Task ExternalCallUsesScriptFolderAndRejectsInvalidChild(
        string? childSource, bool success)
    {
        await WithDirectory(async directory =>
        {
            string scripts = Path.Combine(directory, "スクリプト");
            Directory.CreateDirectory(scripts);
            string script = CopyScript(scripts, "external-call-main.uws");
            await RunCli(directory, script, childSource, success);
        });
    }

    private async Task RunCli(string directory, string script,
        string? childSource, bool success)
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
        Assert.True(configuration is "debug" or "release");
        string cli = Path.Combine(root.FullName, "artifacts", "bin",
            "UWSCSharp.Cli", configuration, "UWSCSharp.Cli.exe");
        Assert.True(File.Exists(cli), cli);
        string child = Path.Combine(Path.GetDirectoryName(script)!, "helper.uws");
        byte[] mainBefore = await File.ReadAllBytesAsync(script);
        byte[]? before = childSource is null ? null : Encoding.UTF8.GetBytes(childSource);
        if (before is not null)
        {
            await File.WriteAllBytesAsync(child, before);
        }
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
        byte[]? after = File.Exists(child) ? await File.ReadAllBytesAsync(child) : null;
        output.WriteLine("AUTHORING_CALL_RECEIPT " + JsonSerializer.Serialize(new
        {
            script = Path.GetFileName(script),
            scriptSha256 = Hash(await File.ReadAllBytesAsync(script)),
            cliSha256 = Hash(await File.ReadAllBytesAsync(cli)),
            inputBase64 = before is null ? null : Convert.ToBase64String(before),
            beforeSha256 = before is null ? null : Hash(before),
            afterSha256 = after is null ? null : Hash(after),
            workingDirectory = directory,
            scriptPath = script,
            mainBase64 = Convert.ToBase64String(mainBefore),
            mainBeforeSha256 = Hash(mainBefore),
            mainAfterSha256 = Hash(await File.ReadAllBytesAsync(script)),
            childPath = child,
            childExistsAfter = File.Exists(child),
            exitCode = process.ExitCode,
            timedOut,
            stdout,
            stderr
        }));
        Assert.False(timedOut, "CLIが制限時間内に終了しませんでした。");
        Assert.Equal(mainBefore, await File.ReadAllBytesAsync(script));
        Assert.Equal(before, after);
        if (success)
        {
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", stderr);
            Assert.Equal("主ファイル開始\n子ファイル\n主ファイル終了\n",
                stdout.Replace("\r\n", "\n"));
        }
        else
        {
            Assert.Equal(1, process.ExitCode);
            Assert.Equal("", stdout);
            Assert.Contains("helper.uws", stderr);
            if (childSource is not null)
            {
                Assert.Contains(child + ": 2行:", stderr);
            }
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
            "uwsc-authoring-call-" + Guid.NewGuid().ToString("N")));
        Assert.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot)
            + Path.DirectorySeparatorChar + "uwsc-authoring-call-",
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
