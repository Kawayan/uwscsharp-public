using System.Diagnostics;
using System.Text;
using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class ScrambledScriptFrontendTests(ITestOutputHelper output)
{
    [Fact]
    public async Task CliReportsBackupWhenReadOnlyInputCannotBeReplaced()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var fixture = new ScriptDirectory();
        string input = Path.Combine(fixture.Path, "入力.uws");
        string backup = Path.ChangeExtension(input, ".org");
        byte[] original = Encoding.UTF8.GetBytes("PRINT 42");
        await File.WriteAllBytesAsync(input, original);
        File.SetAttributes(input, FileAttributes.ReadOnly);
        try
        {
            string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
            (int code, string diagnostic) =
                await SafeArrayFrontendTests.RunAsync(cli, ["/A", input]);
            Assert.Equal(1, code);
            Assert.Contains("対象: " + input, diagnostic);
            Assert.Contains("退避先: " + backup, diagnostic);
            Assert.Equal(original, await File.ReadAllBytesAsync(input));
            Assert.Equal(original, await File.ReadAllBytesAsync(backup));
            Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
        }
        finally
        {
            File.SetAttributes(input, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task CliReportsDestinationWhenItsDirectoryIsMissing()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var fixture = new ScriptDirectory();
        string input = Path.Combine(fixture.Path, "入力.uws");
        string folder = Path.Combine(fixture.Path, "未作成");
        string destination = Path.Combine(folder, "出力.uws");
        byte[] original = Encoding.UTF8.GetBytes("PRINT 42");
        await File.WriteAllBytesAsync(input, original);

        string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
        (int code, string diagnostic) = await SafeArrayFrontendTests.RunAsync(
            cli, ["/A", input, destination]);
        Assert.Equal(1, code);
        Assert.Contains("保存先: " + destination, diagnostic);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.False(Directory.Exists(folder));
        Assert.Equal([input], Directory.GetFiles(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliReportsExistingScrambleDestination(bool inPlace)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var fixture = new ScriptDirectory();
        string input = Path.Combine(fixture.Path, "入力.uws");
        string destination = inPlace ? Path.ChangeExtension(input, ".org")
            : Path.Combine(fixture.Path, "出力.uws");
        await File.WriteAllTextAsync(input, "PRINT 42");
        await File.WriteAllTextAsync(destination, "既存");

        string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("/A");
        start.ArgumentList.Add(input);
        if (!inPlace)
        {
            start.ArgumentList.Add(destination);
        }

        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("検証プロセスを開始できません。");
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("保存先: " + destination, await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        Assert.Equal("PRINT 42", await File.ReadAllTextAsync(input));
        Assert.Equal("既存", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(false, "FFFE50", "UTF-16LE")]
    [InlineData(true, "FFFE50", "UTF-16LE")]
    [InlineData(false, "FFFE00D8", "UTF-16LE")]
    [InlineData(true, "FFFE00DC", "UTF-16LE")]
    [InlineData(false, "FEFF00", "UTF-16BE")]
    [InlineData(true, "FEFFD800", "UTF-16BE")]
    [InlineData(false, "EFBBBFC3", "文字コード")]
    [InlineData(true, "EFBBBFC3", "文字コード")]
    [InlineData(false, "82", "文字コード")]
    [InlineData(true, "82", "文字コード")]
    public async Task CliReportsUndecodableInputWithoutPublishingFiles(
        bool inPlace, string inputHex, string encoding)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var fixture = new ScriptDirectory();
        string input = Path.Combine(fixture.Path, "input.uws");
        string destination = Path.Combine(fixture.Path, "converted.uws");
        byte[] invalid = Convert.FromHexString(inputHex);
        await File.WriteAllBytesAsync(input, invalid);

        string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
        (int code, string diagnostic) = await SafeArrayFrontendTests.RunAsync(cli,
            inPlace ? ["/A", input] : ["/A", input, destination]);
        Assert.Equal(1, code);
        Assert.Contains($"入力ファイルの{encoding}が不正です。", diagnostic);
        Assert.Contains(input, diagnostic);
        Assert.Equal(invalid, await File.ReadAllBytesAsync(input));
        Assert.Equal([input], Directory.GetFiles(fixture.Path));
    }

    [Fact]
    public async Task ConcurrentCliConversionsPreserveOriginalBackup()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var fixture = new ScriptDirectory();
        string input = Path.Combine(fixture.Path, "input.uws");
        byte[] original = "PRINT 42\r\n"u8.ToArray();
        await File.WriteAllBytesAsync(input, original);

        string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
        Task<(int Code, string Output)> first =
            SafeArrayFrontendTests.RunAsync(cli, ["/A", input]);
        Task<(int Code, string Output)> second =
            SafeArrayFrontendTests.RunAsync(cli, ["/A", input]);
        (int Code, string Output)[] runs = await Task.WhenAll(first, second);
        foreach ((int code, string text) in runs)
        {
            output.WriteLine($"exit={code}, output={text}");
        }

        Assert.Single(runs, run => run.Code == 0);
        Assert.Single(runs, run => run.Code == 1);
        Assert.Equal(original, await File.ReadAllBytesAsync(
            Path.ChangeExtension(input, ".org")));
        Assert.Equal("PRINT 42\r\n", Source.ReadFile(input));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", false)]
    [InlineData("UWSCSharp.Cli", true)]
    [InlineData("UWSCSharp.Wpf", false)]
    [InlineData("UWSCSharp.Wpf", true)]
    public async Task ConvertedParentResolvesRelativeCall(
        string frontend, bool convertChild)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var fixture = new ScriptDirectory();
        string scripts = Directory.CreateDirectory(
            Path.Combine(fixture.Path, "scripts")).FullName;
        string parent = Path.Combine(scripts, "parent.uws");
        string child = Path.Combine(scripts, "child.uws");
        await File.WriteAllTextAsync(parent, "CALL child.uws");
        await File.WriteAllTextAsync(child, "PRINT 42");
        string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.exe");
        if (convertChild)
        {
            (int childCode, string childOutput) =
                await SafeArrayFrontendTests.RunAsync(cli, ["/A", child]);
            Assert.True(childCode == 0, childOutput);
            Assert.Equal("PRINT 42",
                await File.ReadAllTextAsync(Path.ChangeExtension(child, ".org")));
        }

        (int conversionCode, string conversionOutput) =
            await SafeArrayFrontendTests.RunAsync(cli, ["/A", parent]);
        Assert.True(conversionCode == 0, conversionOutput);
        Assert.Equal("CALL child.uws",
            await File.ReadAllTextAsync(Path.ChangeExtension(parent, ".org")));

        (int code, string text) = await SafeArrayFrontendTests.RunAsync(
            FixturePaths.Get(frontend, frontend + ".exe"), [parent]);
        Assert.Equal(0, code);
        Assert.Equal("42", text.TrimEnd('\r', '\n'));
    }

    private sealed class ScriptDirectory : IDisposable
    {
        internal string Path
        {
            get;
        } =
            Directory.CreateTempSubdirectory("uwsc-scramble-parent-").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", false, false)]
    [InlineData("UWSCSharp.Cli", false, true)]
    [InlineData("UWSCSharp.Cli", true, false)]
    [InlineData("UWSCSharp.Cli", true, true)]
    [InlineData("UWSCSharp.Cli", false, false, true)]
    [InlineData("UWSCSharp.Cli", false, true, true)]
    [InlineData("UWSCSharp.Wpf", false, false)]
    [InlineData("UWSCSharp.Wpf", false, true)]
    [InlineData("UWSCSharp.Wpf", true, false)]
    [InlineData("UWSCSharp.Wpf", true, true)]
    [InlineData("UWSCSharp.Wpf", false, false, true)]
    [InlineData("UWSCSharp.Wpf", false, true, true)]
    public async Task RunsOrRejectsScrambledScript(
        string frontend, bool dynamicCall, bool corrupt, bool staticCall = false)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Directory.CreateTempSubdirectory(
            "uwsc-scramble-frontend-").FullName;
        try
        {
            string plain = Path.Combine(directory, "plain.uws");
            string scrambled = Path.Combine(directory, "encoded.uws");
            await File.WriteAllTextAsync(plain, "PRINT \"スクランブル実行\"");
            ScriptScramble.ConvertFile(plain, scrambled);
            if (corrupt)
            {
                byte[] bytes = await File.ReadAllBytesAsync(scrambled);
                // 本文の最後のコード単位を変更して検査値を不一致にします。
                bytes[^6] ^= 1;
                await File.WriteAllBytesAsync(scrambled, bytes);
            }

            string main = scrambled;
            if (dynamicCall || staticCall)
            {
                main = Path.Combine(directory, "main.uws");
                await File.WriteAllTextAsync(main, staticCall
                    ? "CALL encoded.uws" : "EVAL(\"CALL encoded.uws\")");
            }

            var start = new ProcessStartInfo(FixturePaths.Get(frontend, frontend + ".exe"))
            {
                WorkingDirectory = directory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add(main);
            using var process = new Process { StartInfo = start };
            Assert.True(process.Start());
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }

            string actual = (await stdout).TrimEnd('\r', '\n');
            string error = (await stderr).TrimEnd('\r', '\n');
            output.WriteLine($"exit={process.ExitCode}, stdout={actual}, stderr={error}");
            if (corrupt)
            {
                Assert.Equal(1, process.ExitCode);
                Assert.Empty(actual);
                Assert.Contains("UWS05の検査値", error);
            }
            else
            {
                Assert.Equal(0, process.ExitCode);
                Assert.Equal("スクランブル実行", actual);
                Assert.Empty(error);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
