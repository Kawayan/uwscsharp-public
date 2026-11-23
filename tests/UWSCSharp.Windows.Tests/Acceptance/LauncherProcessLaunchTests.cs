using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using UWSCSharp.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class LauncherProcessLaunchTests(ITestOutputHelper output)
{
    private static readonly string[] LiteralArguments =
        ["", "日本語 \"quotes\"", "tail \\", "a&b|c>file"];

    [Theory]
    [InlineData("script")]
    [InlineData("executable")]
    public async Task ChildrenKeepArgumentsAndOutliveEditorDespiteOtherLaunchFailures(
        string targetKind)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string targetDirectory = Path.Combine(fixture.DirectoryPath, "日本語 targets");
            Directory.CreateDirectory(targetDirectory);
            string target;
            LauncherCommandKind kind;
            if (targetKind == "script")
            {
                target = Path.Combine(targetDirectory, "登録 script.uws");
                kind = LauncherCommandKind.Script;
                await File.WriteAllTextAsync(target, """
                    DIM name=PARAM_STR[0]
                    DIM f=FOPEN(name+".args",F_WRITE8)
                    FPUT(f,GET_CUR_DIR,0)
                    DIM i
                    FOR i=1 TO LENGTH(PARAM_STR)-1
                        FPUT(f,PARAM_STR[i],0)
                    NEXT
                    FCLOSE(f)
                    f=FOPEN(name+".ready",F_WRITE8)
                    FCLOSE(f)
                    DIM started=GETTIME()
                    WHILE !FOPEN(name+".go",F_EXISTS)
                        IF GETTIME()-started>90 THEN EXITEXIT(0)
                        SLEEP(0.05)
                    WEND
                    f=FOPEN(name+".done",F_WRITE8)
                    FCLOSE(f)
                    EXITEXIT(0)
                    """, new UTF8Encoding(true), timeout.Token);
            }
            else
            {
                target = CopyChildProbe(targetDirectory);
                kind = LauncherCommandKind.Executable;
            }

            var commands = new List<LauncherEntry?>();
            for (int index = 0; index < 2; index++)
            {
                string name = "run-" + index;
                string[] arguments = kind == LauncherCommandKind.Script
                    ? [name, .. LiteralArguments]
                    : ["--launcher-child-probe", Path.Combine(targetDirectory, name + ".ready"),
                        Path.Combine(targetDirectory, name + ".go"), .. LiteralArguments];
                commands.Add(new LauncherCommand(name, target, kind,
                    arguments, fixture.AppDirectory));
                if (index == 0)
                {
                    commands.Add(new LauncherCommand("消失", "missing.exe",
                        LauncherCommandKind.Executable, [], targetDirectory));
                    string broken = Path.Combine(targetDirectory, "broken.exe");
                    File.WriteAllText(broken, "実行形式ではない");
                    commands.Add(new LauncherCommand("不正EXE", broken,
                        LauncherCommandKind.Executable, [], targetDirectory));
                }
            }

            (Process owner, JsonElement result) = await StartOwnerAsync(
                fixture, commands, timeout.Token);
            Assert.True(result.GetProperty("editor_preserved").GetBoolean());
            string[] failures = [.. result.GetProperty("failures").EnumerateArray().Select(value => value.GetString()!)];
            Assert.Equal(2, failures.Length);
            Assert.Contains(failures, value => value.StartsWith("FileNotFoundException:"));
            Assert.Contains(failures, value => value.StartsWith("Win32Exception:"));
            int[] ids = [.. result.GetProperty("children").EnumerateArray().Select(value => value.GetInt32())];
            Assert.Equal(2, ids.Length);
            Assert.NotEqual(ids[0], ids[1]);
            var children = new List<Process>();
            for (int index = 0; index < ids.Length; index++)
            {
                Process child = fixture.Own(ids[index]);
                children.Add(child);
                string name = "run-" + index;
                string ready = Path.Combine(targetDirectory, name + ".ready");
                await WaitForFileAsync(ready, child, timeout.Token);
                if (kind == LauncherCommandKind.Script)
                {
                    string[] received = await File.ReadAllLinesAsync(
                        Path.Combine(targetDirectory, name + ".args"), timeout.Token);
                    Assert.Equal(new[] { targetDirectory }.Concat(LiteralArguments), received);
                }
                else
                {
                    JsonElement received = await ReadJsonAsync(ready, timeout.Token);
                    Assert.Equal(ids[index], received.GetProperty("pid").GetInt32());
                    Assert.Equal(targetDirectory, received.GetProperty("working_directory").GetString());
                    Assert.Equal(LiteralArguments, received.GetProperty("arguments").EnumerateArray()
                        .Select(value => value.GetString()).ToArray());
                }
            }

            await CloseOwnerAsync(fixture, owner, timeout.Token);
            Assert.All(children, child => Assert.False(child.HasExited));
            for (int index = 0; index < children.Count; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(targetDirectory,
                    $"run-{index}.go"), "go", timeout.Token);
                await children[index].WaitForExitAsync(timeout.Token);
                Assert.Equal(0, children[index].ExitCode);
                Assert.True(File.Exists(Path.Combine(targetDirectory, $"run-{index}.ready.done"))
                    || File.Exists(Path.Combine(targetDirectory, $"run-{index}.done")));
                if (index == 0)
                {
                    Assert.False(children[1].HasExited);
                }
            }

            output.WriteLine($"kind={targetKind},pids={string.Join(',', ids)},owner={owner.Id},failures=2,editor-preserved=True,children-completed=True");
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await fixture.CleanupAsync(primary);
        }
    }

    [Theory]
    [InlineData("open")]
    [InlineData("inspect")]
    public async Task DocumentUsesOwnedAssociationAndOutlivesEditor(string verb)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        string suffix = Guid.NewGuid().ToString("N");
        string extension = ".uwslaunch" + suffix;
        string program = "UWSCSharp.Launcher.Probe." + suffix;
        string classes = @"Software\Classes\";
        Exception? primary = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string application = Path.Combine(fixture.DirectoryPath, "handler");
            string executable = CopyChildProbe(application);
            string targetDirectory = Path.Combine(fixture.DirectoryPath, "日本語 documents");
            Directory.CreateDirectory(targetDirectory);
            string target = Path.Combine(targetDirectory, "対象" + extension);
            File.WriteAllText(target, "fixture document");
            string receipt = Path.Combine(targetDirectory, "document.ready");
            string release = Path.Combine(targetDirectory, "document.go");
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(classes + extension))
            {
                key.SetValue("", program);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(classes + program + @"\shell"))
            {
                key.SetValue("", verb);
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(
                classes + program + @"\shell\" + verb + @"\command"))
            {
                key.SetValue("", $"\"{executable}\" --launcher-child-probe \"{receipt}\" \"{release}\" \"%1\"");
            }

            SHChangeNotify(0x08000000, 0, 0, 0);
            var command = new LauncherCommand("文書", target,
                LauncherCommandKind.Document, [], fixture.AppDirectory);
            (Process owner, JsonElement result) = await StartOwnerAsync(
                fixture, [command], timeout.Token);
            Assert.True(result.GetProperty("editor_preserved").GetBoolean());
            Assert.Empty(result.GetProperty("failures").EnumerateArray());
            await WaitForFileAsync(receipt, owner, timeout.Token);
            JsonElement received = await ReadJsonAsync(receipt, timeout.Token);
            Process child = fixture.Own(received.GetProperty("pid").GetInt32());
            Assert.Equal(targetDirectory, received.GetProperty("working_directory").GetString());
            Assert.Equal(new[] { target }, received.GetProperty("arguments").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
            await CloseOwnerAsync(fixture, owner, timeout.Token);
            Assert.False(child.HasExited);
            await File.WriteAllTextAsync(release, "go", timeout.Token);
            await child.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, child.ExitCode);
            Assert.True(File.Exists(receipt + ".done"));
            output.WriteLine($"verb={verb},document-pid={child.Id},owner={owner.Id},editor-preserved=True,completed=True");
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(classes + extension, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(classes + program, throwOnMissingSubKey: false);
            SHChangeNotify(0x08000000, 0, 0, 0);
            await fixture.CleanupAsync(primary);
        }
    }

    private static string CopyChildProbe(string directory)
    {
        Directory.CreateDirectory(directory);
        string original = FixturePaths.Get("UWSCSharp.ControlFixture", "UWSCSharp.ControlFixture.exe");
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(original)!))
        {
            string copied = Path.Combine(directory, Path.GetFileName(file));
            File.Copy(file, copied);
            File.SetAttributes(copied, FileAttributes.Normal);
        }

        return Path.Combine(directory, Path.GetFileName(original));
    }

    private static async Task<(Process Owner, JsonElement Result)> StartOwnerAsync(
        AssociationLaunchFixture fixture, IEnumerable<LauncherEntry?> commands,
        CancellationToken token)
    {
        string settings = Path.Combine(fixture.DirectoryPath, "launcher.json");
        LauncherSettingsStore.Save(settings, new LauncherSettings(commands));
        string receipt = Path.Combine(fixture.DirectoryPath, "owner.ready");
        Process owner = fixture.StartEditor("--verify-launcher-process", settings,
            receipt, Path.Combine(fixture.DirectoryPath, "owner.go"));
        await WaitForFileAsync(receipt, owner, token);
        JsonElement result = await ReadJsonAsync(receipt, token);
        Assert.Equal(owner.Id, result.GetProperty("owner_pid").GetInt32());
        return (owner, result);
    }

    private static async Task CloseOwnerAsync(AssociationLaunchFixture fixture,
        Process owner, CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(fixture.DirectoryPath, "owner.go"), "go", token);
        await owner.WaitForExitAsync(token);
        Assert.Equal(0, owner.ExitCode);
        JsonElement result = await ReadJsonAsync(
            Path.Combine(fixture.DirectoryPath, "owner.ready.closed"), token);
        Assert.True(result.GetProperty("editor_preserved").GetBoolean());
        Assert.True(result.GetProperty("closed").GetBoolean());
    }

    private static async Task WaitForFileAsync(string path, Process owner,
        CancellationToken token)
    {
        while (!File.Exists(path))
        {
            Assert.False(owner.HasExited, "通知前に所有プロセスが終了しました。");
            Assert.False(File.Exists(path + ".error"), "検証プロセスがエラーを通知しました。");
            await Task.Delay(25, token);
        }
    }

    private static async Task<JsonElement> ReadJsonAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return document.RootElement.Clone();
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags,
        nint first, nint second);
}
