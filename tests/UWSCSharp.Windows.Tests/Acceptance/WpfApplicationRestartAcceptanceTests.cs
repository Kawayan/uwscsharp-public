using System.Diagnostics;
using System.Text;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class WpfApplicationRestartAcceptanceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ClosesAndStartsReplacementAfterFinally(
        bool rerun, bool association)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        AssociationLaunchFixture? fixture = association
            ? AssociationLaunchFixture.Create() : null;
        string directory = fixture?.DirectoryPath
            ?? Directory.CreateTempSubdirectory().FullName;
        try
        {
            string script = Path.Combine(directory, "restart script.uws");
            string marker = Path.Combine(directory, "finally.txt");
            string result = Path.Combine(directory, "result.txt");
            string done = Path.Combine(directory, "done.txt");
            string release = Path.Combine(directory, "release.txt");
            string flag = rerun ? "TRUE" : "FALSE";
            await File.WriteAllTextAsync(script, $$"""
                IF !FOPEN("{{marker}}",F_EXISTS) THEN
                    TRY
                        POFF(P_UWSC_REEXEC,{{flag}})
                        PRINT INT(NULL)
                    FINALLY
                        DIM cleanup=FOPEN("{{marker}}",F_WRITE8)
                        FPUT(cleanup,"cleanup")
                        FCLOSE(cleanup)
                    ENDTRY
                    EXITEXIT 7
                ENDIF
                DIM receipt=FOPEN("{{result}}",F_WRITE8)
                FPUT(receipt,"["+PARAM_STR[0]+"]")
                FPUT(receipt,PARAM_STR[1])
                FCLOSE(receipt)
                DIM finished=FOPEN("{{done}}",F_WRITE8)
                FCLOSE(finished)
                WHILE !FOPEN("{{release}}",F_EXISTS)
                    SLEEP(0.05)
                WEND
                """, new UTF8Encoding(true));

            string editor = fixture is null
                ? FixturePaths.Get("UWSCSharp.Wpf", "UWSCSharp.Wpf.exe")
                : Path.Combine(fixture.AppDirectory, "UWSCSharp.Wpf.exe");
            string executable = fixture?.Executable ?? editor;
            string replacement = rerun ? executable : editor;
            string command = $"\"{executable}\" \"{script}\" \"\" \"空白 を含む\"";
            await using SandboxProcessWatchdog parent =
                await SandboxProcessWatchdog.StartAsync(
                    executable, command, TimeSpan.FromSeconds(25));
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(22));
            while (!HasReplacementWindow(replacement, parent.ProcessId)
                || (rerun && !File.Exists(done)))
            {
                await Task.Delay(100, timeout.Token);
            }

            Assert.Equal(["cleanup"], await File.ReadAllLinesAsync(marker));
            if (rerun)
            {
                Assert.Equal(["[]", "空白 を含む"],
                    await File.ReadAllLinesAsync(result));
                await File.WriteAllTextAsync(release, "finish");
            }
            else
            {
                Assert.False(File.Exists(result));
            }
        }
        finally
        {
            if (fixture is null)
            {
                Directory.Delete(directory, recursive: true);
            }
            else
            {
                await fixture.CleanupAsync(null);
            }
        }
    }

    private static bool HasReplacementWindow(string executable, uint parentId)
    {
        bool found = false;
        foreach (Process process in Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(executable)))
        {
            using (process)
            {
                if (process.Id != parentId && !process.HasExited
                    && process.MainWindowHandle != 0
                    && string.Equals(process.MainModule?.FileName, executable,
                        StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                }
            }
        }

        return found;
    }
}
