using System.Diagnostics;
using UWSCSharp.Wpf;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LauncherProcessLauncherTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-launcher-plan-" + Guid.NewGuid());

    [Theory]
    [InlineData("test.uws", 0)]
    [InlineData("test.exe", 1)]
    [InlineData("test.txt", 2)]
    public void StartInfoKeepsTargetDirectoryAndLiteralArguments(
        string name, int kindValue)
    {
        var kind = (LauncherCommandKind)kindValue;
        string application = Path.Combine(directory, "app");
        string target = Path.Combine(directory, "日本語 target");
        Directory.CreateDirectory(application);
        Directory.CreateDirectory(target);
        string path = Path.Combine(target, name);
        File.WriteAllText(path, "");
        string association = Path.Combine(application, "UWSCSharp.Association.exe");
        File.WriteAllText(association, "");
        string[] arguments = kind == LauncherCommandKind.Document ? []
            : ["", "日本語 with spaces", "\"quotes\"", "trailing \\"];
        var command = new LauncherCommand("項目", path, kind, arguments, directory);

        ProcessStartInfo start = LauncherProcessLauncher.CreateStartInfo(command, application);

        Assert.Equal(target, start.WorkingDirectory);
        Assert.Equal(kind == LauncherCommandKind.Document, start.UseShellExecute);
        Assert.Equal(kind == LauncherCommandKind.Script ? association : path, start.FileName);
        Assert.Equal(kind == LauncherCommandKind.Script ? [path, .. arguments] : arguments,
            start.ArgumentList);
        Assert.Equal("", start.Arguments);
        Assert.Equal("", start.Verb);
        Assert.Equal(arguments, command.Arguments);
    }

    [Fact]
    public void MissingTargetAndAssociationDoNotModifyRegistration()
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "missing.uws");
        var command = new LauncherCommand("保持", path,
            LauncherCommandKind.Script, [""], directory);
        Assert.Equal(path, Assert.Throws<FileNotFoundException>(() =>
            LauncherProcessLauncher.CreateStartInfo(command, directory)).FileName);
        File.WriteAllText(path, "");
        Assert.EndsWith("UWSCSharp.Association.exe",
            Assert.Throws<FileNotFoundException>(() =>
                LauncherProcessLauncher.CreateStartInfo(command, directory)).FileName);
        Assert.Equal("保持", command.Title);
        Assert.Equal([""], command.Arguments);
    }

    [Fact]
    public void DirectoryRegistrationCannotBecomeAnExecutableTarget()
    {
        Directory.CreateDirectory(directory);
        var command = new LauncherCommand("ルート", Path.GetPathRoot(directory)!,
            LauncherCommandKind.Document, [], directory);
        Assert.Throws<FileNotFoundException>(() =>
            LauncherProcessLauncher.CreateStartInfo(command, directory));
        Assert.Throws<ArgumentException>(() =>
            LauncherProcessLauncher.CreateStartInfo(command, "relative"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
