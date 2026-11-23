using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class NativeDllLibraryPathTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-dll-path-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("fixture")]
    [InlineData(@".\fixture.dll")]
    [InlineData(@"sub\fixture.dll")]
    public void SearchesInExecutableSystemAndWorkingOrder(string name)
    {
        string relativePath = Path.HasExtension(name) ? name : name + ".dll";
        string executable = CreateFile("executable", relativePath);
        string system = CreateFile("system", relativePath);
        string working = CreateFile("working", relativePath);

        Assert.Equal(executable, Resolve(name));
        File.Delete(executable);
        Assert.Equal(system, Resolve(name));
        File.Delete(system);
        Assert.Equal(working, Resolve(name));
        File.Delete(working);
        Assert.Throws<DllNotFoundException>(() => Resolve(name));
    }

    [Fact]
    public void AbsolutePathWithSpacesBypassesSearch()
    {
        CreateFile("executable", "space name.dll");
        string expected = CreateFile("other", "space name.dll");

        Assert.Equal(expected, Resolve(expected));
        Assert.Equal(expected, Resolve(Path.ChangeExtension(expected, null)));
        Assert.Throws<DllNotFoundException>(() => Resolve($"\"{expected}\""));
        Assert.Throws<DllNotFoundException>(() => Resolve($"'{expected}'"));
    }

    [Theory]
    [InlineData(@"C:fixture.dll")]
    [InlineData(@"\fixture.dll")]
    [InlineData(@"fixture.dll:stream")]
    [InlineData("fixture*.dll")]
    [InlineData("fixture\0.dll")]
    [InlineData(@"sub\")]
    public void InvalidOrDriveDependentNameDoesNotSearch(string name)
    {
        Assert.Throws<DllNotFoundException>(() => Resolve(name));
    }

    [Fact]
    public async Task DeclaredSystemLibraryResolvesThroughTheRunSession()
    {
        NativeDllDeclaration declaration = NativeDllDeclarationParser.Parse(
            "GetCurrentProcessId():uint:kernel32", 1);
        var automation = new WindowsAutomation(_ => { });
        await using IWindowsAutomationSession session = automation.CreateSession();
        NativeDllModule.Function function = ((INativeDllModuleSource)session)
            .ResolveNativeDll(declaration, directory, new(typeof(uint)));

        Assert.Equal((uint)Environment.ProcessId,
            Assert.IsType<uint>(function.Invoke()));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private string Resolve(string name) => NativeDllLibraryPath.Resolve(name,
        Path.Combine(directory, "working"), Path.Combine(directory, "executable"),
        Path.Combine(directory, "system"));

    private string CreateFile(string subdirectory, string name)
    {
        string path = Path.GetFullPath(Path.Combine(directory, subdirectory, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }
}
