using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// 現行Releaseの無変更apphostを所有コンソールで検査します。
/// </summary>
[Trait("Category", "SandboxOnly")]
public sealed class ReleaseCliAcceptanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("ctrl-c", false)]
    [InlineData("break", false)]
    [InlineData("ctrl-c", true)]
    [InlineData("break", true)]
    public async Task ReleaseApphostPreservesPublicConsoleContracts(string mode, bool worker)
    {
        VerifyPublication();
        await new PublishedCliM1AcceptanceTests(output)
            .VerifyPublishedCliAsync("CliReleaseAcceptance", mode, worker);
        VerifyPublication();
    }

    [Fact]
    public async Task ReleaseManualLaunchAndCommonFailureRecoveryAreDistinctChecks()
    {
        string cli = VerifyPublication();
        await new CliInputCleanupAcceptanceTests(output).VerifyOwnedConsoleAsync(cli);
        VerifyPublication();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseApphostCreatesTypedArraysAndRejectsBeforeEvaluation(bool worker)
    {
        string cli = VerifyPublication();
        await new SafeArrayFrontendTests(output).VerifyTypedArrayAsync(cli, worker);
        VerifyPublication();
    }

    [Theory]
    [InlineData(9)]
    [InlineData(13)]
    public async Task ReleaseApphostCarriesOwnedComReferencesAcrossWorkers(int type)
    {
        string cli = VerifyPublication();
        await new SafeArrayFrontendTests(output).VerifyReferenceArrayAsync(cli, type);
        VerifyPublication();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseApphostConnectsInputTextAndArrayFunctions(bool worker)
    {
        string cli = VerifyPublication();
        var messages = new ConcurrentQueue<uint>();
        const string title = "日本語CLI受入";
        await using PumpedControlFixture fixture =
            await PumpedControlFixture.CreateAsync(owner =>
            {
                owner.InputMessage = (message, _, _) =>
                {
                    if (message is 0x200 or 0x201 or 0x202)
                    {
                        messages.Enqueue(message);
                    }
                };
            }, title: title, dpiAwarenessContext: -4);
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-release-functions-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "public-functions.uws");
        string call = worker ? "THREAD ReadOwnedWindow()" : "ReadOwnedWindow()";
        try
        {
            await File.WriteAllTextAsync(script, $$"""
                PUBLIC finished=FALSE
                {{call}}
                WHILE finished=FALSE
                    SLEEP(0.01)
                WEND
                EXITEXIT 7
                PROCEDURE ReadOwnedWindow()
                    id=HNDTOID({{(long)fixture.Root}})
                    MOUSEORG(id,2)
                    MMV(12,15)
                    BTN(0,0,12,15)
                    PRINT GETSTR(id,0,STR_STATIC,0)
                    a=SAFEARRAY(-2,-1,3)
                    a[-2]=7
                    PRINT LENGTH(a)+a[-2]
                    finished=TRUE
                FEND
                """, new UTF8Encoding(true));
            messages.Clear();
            (int code, string text) = await SafeArrayFrontendTests.RunAsync(cli, [script]);
            output.WriteLine(text);
            Assert.Equal(7, code);
            Assert.Equal(title + "\n9\n",
                text.Replace("\r\n", "\n", StringComparison.Ordinal));
            fixture.Invoke(() => { });
            Assert.Contains(0x200u, messages);
            Assert.Single(messages, message => message == 0x201);
            Assert.Single(messages, message => message == 0x202);
            VerifyPublication();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string VerifyPublication()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string cli = FixturePaths.Get("CliReleaseAcceptance", "UWSCSharp.Cli.exe");
        string folder = Path.GetDirectoryName(cli)!;
        string receipt = File.ReadAllText(Path.Combine(folder, "release-receipt.json"));
        using var document = JsonDocument.Parse(receipt);
        JsonElement root = document.RootElement;
        Assert.Equal("Release", root.GetProperty("configuration").GetString());
        Assert.Equal("win-x64", root.GetProperty("runtime_identifier").GetString());
        Assert.True(File.Exists(Path.Combine(folder, "LICENSE")));
        foreach (JsonProperty file in root.GetProperty("files").EnumerateObject())
        {
            string path = Path.Combine(folder, file.Name);
            Assert.Equal(file.Value.GetString(),
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }
        output.WriteLine(receipt);
        return cli;
    }
}
