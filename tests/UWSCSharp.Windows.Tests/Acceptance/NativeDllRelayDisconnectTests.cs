using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class NativeDllRelayDisconnectTests
{
    [Theory]
    [InlineData("UWSCSharp.Cli")]
    [InlineData("UWSCSharp.Wpf")]
    public async Task RelayExitReturnsAnErrorAndTheScriptContinues(string frontend)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string windows =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string library = Path.Combine(windows, "SysWOW64", "kernel32.dll");
        string directory = Directory.CreateTempSubdirectory(
            "uwsc-relay-disconnect-").FullName;
        try
        {
            string source = Path.Combine(directory, "probe.uws");
            string receipt = Path.Combine(directory, "receipt.txt");
            await File.WriteAllTextAsync(source, $$"""
                DEF_DLL GetCurrentProcessId():dword:{{library}}
                DEF_DLL ExitProcess(dword):{{library}}
                DIM f=FOPEN("{{receipt}}",F_WRITE8)
                FPUT(f,"pid="+GetCurrentProcessId())
                TRY
                    ExitProcess(37)
                    FPUT(f,"unexpected")
                EXCEPT
                    FPUT(f,"first="+TRY_ERRMSG)
                ENDTRY
                TRY
                    GetCurrentProcessId()
                    FPUT(f,"unexpected retry")
                EXCEPT
                    FPUT(f,"retry="+TRY_ERRMSG)
                ENDTRY
                FPUT(f,"continued")
                FCLOSE(f)
                EXITEXIT
                """, new UTF8Encoding(true));

            (int code, string standardOutput) = await SafeArrayFrontendTests.RunAsync(
                FixturePaths.Get(frontend, frontend + ".exe"), [source]);
            Assert.Equal(0, code);
            Assert.Empty(standardOutput);
            string[] lines = await File.ReadAllLinesAsync(receipt);
            Assert.Equal(4, lines.Length);
            Assert.StartsWith("pid=", lines[0], StringComparison.Ordinal);
            Assert.True(uint.TryParse(lines[0].AsSpan(4), out uint pid)
                && pid != 0);
            Assert.StartsWith("first=", lines[1], StringComparison.Ordinal);
            Assert.Contains("DLL中継との通信が終了しました。", lines[1]);
            Assert.StartsWith("retry=", lines[2], StringComparison.Ordinal);
            Assert.Contains("DLL中継との通信が終了しました。", lines[2]);
            Assert.Equal("continued", lines[3]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
