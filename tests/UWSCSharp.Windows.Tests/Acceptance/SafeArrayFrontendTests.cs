using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 通常CLI/WPFとWPF編集画面から公開SAFEARRAYを実行します。
/// </summary>
[Trait("Category", "SandboxOnly")]
public sealed class SafeArrayFrontendTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("UWSCSharp.Cli", false)]
    [InlineData("UWSCSharp.Cli", true)]
    [InlineData("UWSCSharp.Wpf", false)]
    [InlineData("UWSCSharp.Wpf", true)]
    public async Task NormalFrontendCreatesTypedArraysAndRejectsBeforeEvaluation(
        string frontend, bool worker)
        => await VerifyTypedArrayAsync(
            FixturePaths.Get(frontend, frontend + ".exe"), worker);

    internal async Task VerifyTypedArrayAsync(string executable, bool worker)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-safearray-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "public-array.uws");
        string call = worker ? "THREAD Make()" : "Make()";
        await File.WriteAllTextAsync(script, $$"""
            PUBLIC finished=FALSE
            PUBLIC value=0
            TRY
                SAFEARRAY(Side(),0,0,0,0,0)
            EXCEPT
                PRINT "rejected"
            ENDTRY
            TRY
                SAFEARRAY(0,1000000)
            EXCEPT
                PRINT "limit"
            ENDTRY
            {{call}}
            WHILE finished=FALSE
                SLEEP(0.01)
            WEND
            PRINT value
            EXITEXIT 7
            FUNCTION Side()
                PRINT "evaluated"
                RESULT=0
            FEND
            PROCEDURE Make()
                a=SAFEARRAY(-2,-1,5,7,3)
                a[-2,5]="7"
                value=LENGTH(a)+a[-2,5]
                finished=TRUE
            FEND
            """, new UTF8Encoding(true));
        try
        {
            (int code, string text) = await RunAsync(executable, [script]);
            output.WriteLine(text);
            Assert.Equal(7, code);
            Assert.Equal("rejected\nlimit\n9\n", text.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WpfEditorUsesTheSamePublicRegistrationAcrossRuns()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "UWSCSharp.slnx")))
        {
            root = Directory.GetParent(root)?.FullName
                ?? throw new InvalidOperationException("作業ルートがありません。");
        }
        string receipt = Path.Combine(Path.GetTempPath(), "uwsc-safearray-editor-" + Guid.NewGuid() + ".json");
        try
        {
            (int code, string text) = await RunAsync(@"C:\ui-tools\pwsh\pwsh.exe",
                ["-NoProfile", "-NonInteractive", "-STA", "-File",
                    Path.Combine(root, "scripts", "check_safearray_wpf.ps1"),
                    "-Executable", FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe"),
                    "-ReceiptPath", receipt]);
            output.WriteLine(text);
            Assert.Equal(0, code);
            string json = await File.ReadAllTextAsync(receipt);
            output.WriteLine(json);
            using var document = JsonDocument.Parse(json);
            Assert.Equal("PASS", document.RootElement.GetProperty("result").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("checks").GetArrayLength());
        }
        finally
        {
            File.Delete(receipt);
        }
    }

    [Theory]
    [InlineData("UWSCSharp.Cli", 9)]
    [InlineData("UWSCSharp.Cli", 13)]
    [InlineData("UWSCSharp.Wpf", 9)]
    [InlineData("UWSCSharp.Wpf", 13)]
    public async Task ReferenceArraysCarryOwnedComObjectsAcrossWorkerCalls(
        string frontend, int type)
        => await VerifyReferenceArrayAsync(
            FixturePaths.Get(frontend, frontend + ".exe"), type);

    internal async Task VerifyReferenceArrayAsync(string executable, int type)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Path.Combine(Path.GetTempPath(), "uwsc-safearray-reference-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "reference-array.uws");
        await File.WriteAllTextAsync(script, $$"""
            PUBLIC refs=SAFEARRAY(0,0,{{type}})
            PUBLIC finished=FALSE
            THREAD FillReference()
            WHILE finished=FALSE
                SLEEP(0.01)
            WEND
            PRINT refs[0].Item("text")
            EXITEXIT 7
            PROCEDURE FillReference()
                item=CREATEOLEOBJ("Scripting.Dictionary")
                item.Add("text","日本語")
                refs[0]=item
                finished=TRUE
            FEND
            """, new UTF8Encoding(true));
        try
        {
            (int code, string text) = await RunAsync(executable, [script]);
            output.WriteLine(text);
            Assert.Equal(7, code);
            Assert.Equal("日本語\n", text.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static async Task<(int Code, string Output)> RunAsync(
        string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("検証プロセスを開始できません。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(75));
            return (process.ExitCode, await stdout + await stderr);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }
}
