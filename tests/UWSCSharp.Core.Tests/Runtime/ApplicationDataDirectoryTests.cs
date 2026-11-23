using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class ApplicationDataDirectoryTests
{
    [Theory]
    [InlineData("PUBLIC GET_APPDATA_DIR=1")]
    [InlineData("CONST get_appdata_dir=1")]
    [InlineData("PUBLIC get_appdata_dir=1/0")]
    [InlineData("CONST GET_APPDATA_DIR=1/0")]
    public async Task GlobalRedefinitionReportsDeclarationLine(string declaration)
    {
        ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
            new Interpreter().RunAsync("\n" + declaration, _ => { }));
        Assert.Equal(2, error.Line);
        Assert.Contains("GET_APPDATA_DIR", error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative\\日本語 folder\\")]
    [InlineData("  path  ")]
    public async Task InjectedValueIsPreservedWithoutFileAccess(string path)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            PUBLIC initial=GET_APPDATA_DIR
            PRINT initial
            PRINT get_appdata_dir
            """, output.Add, default, new ScriptRunOptions([])
        {
            ApplicationDataDirectory = path
        });
        Assert.Equal([path, path], output);
    }

    [Fact]
    public async Task OmittedValueUsesOperatingSystemApplicationData()
    {
        string expected = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        List<string> output = [];
        await new Interpreter().RunAsync("PRINT GET_APPDATA_DIR", output.Add);
        Assert.Equal([expected], output);
    }

    [Fact]
    public async Task AssignmentAndLocalShadowFollowVariableScope()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            OPTION EXPLICIT
            GET_APPDATA_DIR="changed"
            Change()
            PRINT GET_APPDATA_DIR
            Shadow("parameter")
            PRINT GET_APPDATA_DIR
            PROCEDURE Change()
                PRINT GET_APPDATA_DIR
                GET_APPDATA_DIR="routine"
            FEND
            PROCEDURE Shadow(GET_APPDATA_DIR)
                PRINT GET_APPDATA_DIR
                DIM GET_APPDATA_DIR="local"
                PRINT GET_APPDATA_DIR
            FEND
            """, output.Add, default, new ScriptRunOptions([])
        {
            ApplicationDataDirectory = "initial"
        });
        Assert.Equal(["changed", "routine", "parameter", "local", "routine"], output);
    }

    [Fact]
    public async Task ReusedProgramAndRunnerResetAssignedValue()
    {
        var runner = new Interpreter();
        ScriptProgram program = ScriptCompiler.Compile("""
            PRINT GET_APPDATA_DIR
            GET_APPDATA_DIR="modified"
            """);
        List<string> output = [];
        foreach (string path in new[] { "first", "second" })
        {
            await runner.RunAsync(program, new CallbackScriptHost(output.Add),
                default, new ScriptRunOptions([])
                {
                    ApplicationDataDirectory = path
                });
        }

        Assert.Equal(["first", "second"], output);
    }

    [Fact]
    public async Task MainLocalShadowDoesNotReplaceRoutineGlobal()
    {
        List<string> output = [];
        await new Interpreter().RunAsync("""
            DIM GET_APPDATA_DIR="main-local"
            PRINT GET_APPDATA_DIR
            PrintPath()
            PROCEDURE PrintPath()
                PRINT GET_APPDATA_DIR
            FEND
            """, output.Add, default, new ScriptRunOptions([])
        {
            ApplicationDataDirectory = "global"
        });
        Assert.Equal(["main-local", "global"], output);
    }

    [Fact]
    public async Task CancellationDoesNotLeakAssignedValue()
    {
        var runner = new Interpreter();
        var options = new ScriptRunOptions([])
        {
            ApplicationDataDirectory = "initial"
        };
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("GET_APPDATA_DIR=\"changed\"\nPRINT 1\nPRINT 2",
                _ => cancellation.Cancel(), cancellation.Token, options));
        List<string> output = [];
        await runner.RunAsync("PRINT GET_APPDATA_DIR", output.Add, default, options);
        Assert.Equal(["initial"], output);
        Assert.Equal("initial", options.ApplicationDataDirectory);
    }

    [Fact]
    public async Task CalledScriptSharesValueAndPreservesDiagnosticSource()
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "UWSCSharp-appdata-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        try
        {
            string child = Path.Combine(folder, "child.uws");
            string main = Path.Combine(folder, "main.uws");
            await File.WriteAllTextAsync(child, """
                PUBLIC initial=GET_APPDATA_DIR
                PRINT initial
                GET_APPDATA_DIR="child-value"
                """);
            await File.WriteAllTextAsync(main, """
                CALL child
                PRINT GET_APPDATA_DIR
                """);
            List<string> output = [];
            var options = new ScriptRunOptions([])
            {
                ApplicationDataDirectory = "initial"
            };
            await new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add), default, options);
            Assert.Equal(["initial", "child-value"], output);

            await File.WriteAllTextAsync(child,
                "\nPUBLIC GET_APPDATA_DIR=1");
            ScriptException error = await Assert.ThrowsAsync<ScriptException>(() =>
                new Interpreter().RunAsync(ScriptCompiler.CompileFile(main),
                    new CallbackScriptHost(_ => { }), default, options));
            Assert.Equal(2, error.Line);
            Assert.Contains(child, error.DiagnosticMessage);
        }
        finally
        {
            // このテストが所有する一時フォルダーだけを削除します。
            Directory.Delete(folder, true);
        }
    }
}
