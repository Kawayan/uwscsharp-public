using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

public sealed class ScriptFileBaseTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(),
        "UWSCSharp-base-" + Guid.NewGuid().ToString("N"));

    public ScriptFileBaseTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, true);

    private async Task<IReadOnlyList<string>> RunAsync(string source,
        string name = "main.uws")
    {
        string path = Path.Combine(folder, name);
        List<string> output = [];
        await new Interpreter(files: ScriptFiles.ForScript(path)).RunAsync(
            source, output.Add, default, new ScriptRunOptions([]) { SourcePath = path });
        return output;
    }

    [Fact]
    public async Task MainAndCallReadFromMainFolderWithoutChangingProcessDirectory()
    {
        string before = Environment.CurrentDirectory;
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "probe.txt"), "root");
        File.WriteAllText(Path.Combine(folder, "sub", "probe.txt"), "child");
        string read = "f=FOPEN(\"probe.txt\",F_READ)\nPRINT FGET(f,1)\nFCLOSE(f)";
        File.WriteAllText(Path.Combine(folder, "sub", "child.uws"), read);
        IReadOnlyList<string> output = await RunAsync(read + "\nCALL \"sub/child.uws\"");
        Assert.Equal(["root", "root"], output);
        Assert.Equal(before, Environment.CurrentDirectory);
    }

    [Fact]
    public async Task RelativeOutputAndExistenceUseMainFolder()
    {
        Assert.Equal(["1"], await RunAsync("""
            f=FOPEN("new.txt",F_WRITE8)
            FPUT(f,"new",0)
            FCLOSE(f)
            PRINT FOPEN("new.txt",F_EXISTS)
            """));
        Assert.Equal("new\r\n", File.ReadAllText(Path.Combine(folder, "new.txt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("settings")]
    public async Task IniReadWriteAndDeleteUseMainFolder(string name)
    {
        Assert.Equal(["value", ""], await RunAsync($$"""
            WRITEINI("s","k","value","{{name}}")
            PRINT READINI("s","k","{{name}}")
            DELETEINI("s","k","{{name}}")
            PRINT READINI("s","k","{{name}}")
            """));
        Assert.True(File.Exists(Path.Combine(folder,
            name.Length == 0 ? "main.INI" : "settings.INI")));
    }

    [Fact]
    public async Task DirectoryAndDeletePatternsUseMainFolder()
    {
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "trash1.txt"), "root");
        File.WriteAllText(Path.Combine(folder, "sub", "trash1.txt"), "child");
        Assert.Equal(["1", "1", "1", "0"], await RunAsync("""
            PRINT GETDIR(".","trash*.txt")
            PRINT GETDIR("sub","trash*.txt")
            PRINT DELETEFILE("trash*.txt")
            PRINT FOPEN("trash1.txt",F_EXISTS)
            """));
        Assert.True(File.Exists(Path.Combine(folder, "sub", "trash1.txt")));
    }

    [Fact]
    public async Task AbsolutePathIsPreserved()
    {
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        string absolute = Path.Combine(folder, "sub", "probe.txt");
        File.WriteAllText(absolute, "absolute");
        Assert.Equal(["absolute"], await RunAsync(
            $"f=FOPEN(\"{absolute}\",F_READ)\nPRINT FGET(f,1)\nFCLOSE(f)"));
    }

    [Fact]
    public async Task ParallelScriptsKeepIndependentBaseDirectories()
    {
        string before = Environment.CurrentDirectory;
        string[] names = ["first", "second"];
        async Task CheckAsync(string name)
        {
            string directory = Path.Combine(folder, name);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "probe.txt"), name);
            Assert.Equal([name], await RunAsync(
                "f=FOPEN(\"probe.txt\",F_READ)\nPRINT FGET(f,1)\nFCLOSE(f)",
                Path.Combine(name, "main.uws")));
        }

        await Task.WhenAll(names.Select(CheckAsync));
        Assert.Equal(before, Environment.CurrentDirectory);
    }

    [Fact]
    public async Task NewOutputOnlyStillRejectsExistingRelativeFile()
    {
        File.WriteAllText(Path.Combine(folder, "existing.txt"), "keep");
        await using IScriptFileSession session = ScriptFiles.ForScript(
            Path.Combine(folder, "main.uws"), newOutputOnly: true).CreateSession();
        Value result = await session.InvokeAsync("FOPEN",
            [Value.String("existing.txt"), Value.Numeric(16)], default);
        Assert.Equal(-1, result.AsNumber());
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "existing.txt")));
    }
}
