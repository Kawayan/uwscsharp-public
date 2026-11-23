using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.Windows.Tests;

public sealed class SystemInformationIntegrationTests
{
    [Fact]
    public async Task NativeWindows11InformationUsesLegacyCodeAndOsBitness()
    {
        var system = OperatingSystemInformation.Read();
        Assert.Equal(10, system.Major);
        Assert.Equal(0, system.Minor);
        Assert.True(system.Build >= 22000);
        Assert.False(system.Server);
        Assert.True(system.Is64Bit);
        List<string> output = [];
        await new Interpreter(windows: new WindowsAutomation()).RunAsync("""
            PRINT KINDOFOS()
            PRINT KINDOFOS(FALSE)
            PRINT KINDOFOS(OS_WIN64)
            PRINT GET_SYS_DIR
            PRINT GET_UWSC_PRO
            """, output.Add);
        Assert.Equal(["30", "30", "1", Environment.SystemDirectory, "0"], output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostCurrentDirectoryChangesRespectTheFileSessionBase(bool pinned)
    {
        string previous = Environment.CurrentDirectory;
        string root = Path.Combine(Path.GetTempPath(),
            "uwsc-system-cwd-" + Guid.NewGuid());
        string first = Path.Combine(root, "first");
        string second = Path.Combine(root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            Environment.CurrentDirectory = first;
            List<string> output = [];
            IScriptFiles? files = pinned
                ? ScriptFiles.ForScript(Path.Combine(first, "main.uws")) : null;
            await new Interpreter(files: files).RunAsync("""
                PRINT GET_CUR_DIR
                PRINT GET_CUR_DIR
                """, text =>
                {
                    output.Add(text);
                    Environment.CurrentDirectory = second;
                });
            Assert.Equal([first, pinned ? first : second], output);
        }
        finally
        {
            // ゲストのprocess内だけで変更した値を戻してから、所有フォルダーを削除します。
            Environment.CurrentDirectory = previous;
            Directory.Delete(root, recursive: true);
        }
    }
}
