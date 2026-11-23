using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class CliApplicationRestartAcceptanceTests
{
    [Fact]
    public async Task RestartsWithArgumentsAfterFinally()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string script = Path.Combine(directory, "restart script.uws");
            string marker = Path.Combine(directory, "finally.txt");
            string result = Path.Combine(directory, "result.txt");
            string done = Path.Combine(directory, "done.txt");
            await File.WriteAllTextAsync(script, $$"""
                IF !FOPEN("{{marker}}",F_EXISTS) THEN
                    TRY
                        POFF(P_UWSC_REEXEC,TRUE)
                        PRINT INT(NULL)
                    FINALLY
                        DIM marker=FOPEN("{{marker}}",F_WRITE8)
                        FPUT(marker,"cleanup")
                        FCLOSE(marker)
                    ENDTRY
                    EXITEXIT 7
                ENDIF
                DIM receipt=FOPEN("{{result}}",F_WRITE8)
                FPUT(receipt,"["+PARAM_STR[0]+"]")
                FPUT(receipt,PARAM_STR[1])
                FCLOSE(receipt)
                DIM finished=FOPEN("{{done}}",F_WRITE8)
                FCLOSE(finished)
                """, new UTF8Encoding(true));

            string dotnet = FixturePaths.Get("UWSCSharp.Cli",
                "dll-relay/runtime/dotnet.exe");
            string cli = FixturePaths.Get("UWSCSharp.Cli", "UWSCSharp.Cli.dll");
            string command = $"\"{dotnet}\" \"{cli}\" \"{script}\" \"\" \"空白 を含む\"";
            await using SandboxProcessWatchdog child = await SandboxProcessWatchdog.StartAsync(
                dotnet, command, TimeSpan.FromSeconds(20));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(18));
            while (!File.Exists(done))
            {
                await Task.Delay(100, timeout.Token);
            }

            Assert.Equal(["cleanup"], await File.ReadAllLinesAsync(marker));
            Assert.Equal(["[]", "空白 を含む"], await File.ReadAllLinesAsync(result));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
