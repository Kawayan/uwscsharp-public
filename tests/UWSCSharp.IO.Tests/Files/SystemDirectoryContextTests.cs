using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SystemDirectoryContextTests
{
    [Fact]
    public async Task CalledScriptKeepsMainFileBaseAndRelativeFilesAgree()
    {
        string directory = Path.Combine(Path.GetTempPath(),
            "uwsc-current-directory-" + Guid.NewGuid());
        string childDirectory = Path.Combine(directory, "child");
        Directory.CreateDirectory(childDirectory);
        try
        {
            string main = Path.Combine(directory, "main.uws");
            await File.WriteAllTextAsync(Path.Combine(childDirectory, "child.uws"), """
                PRINT GET_CUR_DIR
                DIM f=FOPEN("relative.txt",F_WRITE8)
                FPUT(f,"written",0)
                FCLOSE(f)
                """);
            await File.WriteAllTextAsync(main, """
                PRINT GET_CUR_DIR
                CALL child\child
                PRINT GET_CUR_DIR
                """);
            List<string> output = [];
            await new Interpreter(files: ScriptFiles.ForScript(main)).RunAsync(
                ScriptCompiler.CompileFile(main), new CallbackScriptHost(output.Add));
            Assert.Equal([directory, directory, directory], output);
            Assert.True(File.Exists(Path.Combine(directory, "relative.txt")));
            Assert.False(File.Exists(Path.Combine(childDirectory, "relative.txt")));
            Assert.Equal("written", (await File.ReadAllTextAsync(
                Path.Combine(directory, "relative.txt"))).TrimEnd('\r', '\n'));
        }
        finally
        {
            // このテストが作成したフォルダーだけを削除します。
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExplicitBaseDoesNotRequireTheScriptFileToExist()
    {
        string path = Path.Combine(Path.GetTempPath(),
            "not-created-" + Guid.NewGuid(), "main.uws");
        await using IScriptFileSession session = ScriptFiles.ForScript(path).CreateSession();
        Assert.Equal(Path.GetDirectoryName(path), session.CurrentDirectory);
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public async Task DefaultSessionUsesProcessDirectoryAndRejectsAfterDisposal()
    {
        IScriptFileSession session = new ScriptFiles().CreateSession();
        Assert.Equal(Environment.CurrentDirectory, session.CurrentDirectory);
        await session.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => session.CurrentDirectory);
    }
}
