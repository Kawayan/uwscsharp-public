using System.Diagnostics;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowsApplicationRestartTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\UWSC Files\UWSCSharp.Wpf.dll")]
    public void RestartsApplicationWithoutScriptArguments(string? assembly)
    {
        const string executable = @"C:\UWSC Files\UWSCSharp.Wpf.exe";
        const string directory = @"C:\working directory";
        var script = new ScriptRunOptions(["old"])
        {
            SourcePath = @"C:\scripts\test.uws"
        };

        ProcessStartInfo start = WindowsApplicationRestart.CreateStartInfo(
            new WindowsCommandHost(executable, assembly), directory, false,
            script, ["--input-cleanup=manual"]);

        Assert.Equal(executable, start.FileName);
        Assert.Equal(directory, start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        string[] expected = assembly is null ? [] : [assembly];
        Assert.Equal(expected, start.ArgumentList);
    }

    [Fact]
    public void PreservesScriptPathOptionsAndIndividualArguments()
    {
        const string assembly = @"C:\UWSC Files\UWSCSharp.Cli.dll";
        const string scriptPath = @"C:\scripts\空白 を含む.uws";
        var script = new ScriptRunOptions(["", "a b", "a\"b", @"a\"])
        {
            SourcePath = scriptPath
        };

        ProcessStartInfo start = WindowsApplicationRestart.CreateStartInfo(
            new WindowsCommandHost(@"C:\dotnet\dotnet.exe", assembly),
            @"C:\work", true, script, ["--input-cleanup=manual"]);

        Assert.Equal([assembly, "--input-cleanup=manual", scriptPath,
            "", "a b", "a\"b", @"a\"], start.ArgumentList);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative.uws")]
    public void RejectsRerunWithoutAbsoluteScriptPath(string? path)
    {
        var script = new ScriptRunOptions([]) { SourcePath = path };
        Assert.Throws<InvalidOperationException>(() =>
            WindowsApplicationRestart.CreateStartInfo(
                new WindowsCommandHost(@"C:\UWSCSharp.Wpf.exe"),
                @"C:\work", true, script, []));
    }
}
