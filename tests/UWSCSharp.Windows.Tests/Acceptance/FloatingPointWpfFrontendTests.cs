using System.Diagnostics;
using System.Globalization;
using System.Text;
using UWSCSharp.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class FloatingPointWpfFrontendTests(ITestOutputHelper output)
{
    private const string FileProbe = """
        DIM before=16777216+1
        SET8087CW($087f)
        DIM rounded=16777216+1
        SET8087CW(0)
        DIM restored=16777216+1
        DIM f=FOPEN("cw-result.txt",F_WRITE8)
        FPUT(f,before+":"+rounded+":"+restored)
        FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))
        FCLOSE(f)
        SET8087CW($087f)
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptLaunchUsesTheNativeControlWord(bool association)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string script = await WriteProbeAsync(fixture);
            Process process = association ? fixture.Start(script)
                : fixture.StartEditor(script);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(process.Id, await ReadProbeAsync(fixture));
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

    [Fact]
    public async Task NextEditorPlaybackStartsWithDefaultArithmetic()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "UWSCSharp.slnx")))
            {
                root = Directory.GetParent(root)?.FullName
                    ?? throw new InvalidOperationException("作業ルートがありません。");
            }

            string first = Path.Combine(fixture.DirectoryPath, "editor-round.uws");
            string second = Path.Combine(fixture.DirectoryPath, "editor-default.uws");
            await File.WriteAllTextAsync(first, """
                SET8087CW($087f)
                PRINT "CW-editor:"+(16777216+1)
                """, new UTF8Encoding(true));
            await File.WriteAllTextAsync(second,
                """PRINT "CW-next:"+(16777216+1)""", new UTF8Encoding(true));
            (int code, string text) = await SafeArrayFrontendTests.RunAsync(
                @"C:\ui-tools\pwsh\pwsh.exe",
                ["-NoProfile", "-NonInteractive", "-STA", "-File",
                    Path.Combine(root, "scripts", "check_floating_point_wpf_editor.ps1"),
                    "-Executable", Path.Combine(fixture.AppDirectory, "UWSCSharp.Wpf.exe"),
                    "-First", first, "-Second", second]);
            output.WriteLine(text);
            Assert.Equal(0, code);
            Assert.Contains("CW-editor:16777218", text);
            Assert.Contains("CW-next:16777217", text);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReservationUsesNativeControlWordAndRecovers(
        bool separateProcess)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        string settings = ApplicationInfo.SettingsPath("schedule.json");
        byte[]? previous = File.Exists(settings) ? await File.ReadAllBytesAsync(settings) : null;
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string script = await WriteProbeAsync(fixture);
            SchedulerExecutionMode mode = separateProcess ? SchedulerExecutionMode.SeparateProcess
                : SchedulerExecutionMode.SameProcess;
            var entry = new SchedulerEntry(Guid.NewGuid(), "浮動小数点演算",
                script, [], mode, new SchedulerStartupCondition(), fixture.DirectoryPath);
            SchedulerSettingsStore.Save(settings,
                new SchedulerSettings([entry], enabled: true),
                SchedulerSettingsStore.Load(settings).Revision);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Process process = fixture.StartEditor();
            var storage = new SchedulerManagedStorage(
                ApplicationInfo.SettingsPath("SchedulerLogs"));
            SchedulerHistoryRecord? history;
            while ((history = storage.LoadHistory().SingleOrDefault(
                row => row.EntryId == entry.Id && row.EndedAt is not null)) is null)
            {
                Assert.False(process.HasExited, "予約の完了前に画面が終了しました。");
                await Task.Delay(50, timeout.Token);
            }

            output.WriteLine($"mode={history.Mode}, outcome={history.Outcome}, "
                + $"exit={history.ExitCode}, recovered={history.ResourcesRecovered}");
            Assert.Equal(mode, history.Mode);
            Assert.Equal(SchedulerExecutionOutcome.Succeeded, history.Outcome);
            Assert.Equal(0, history.ExitCode);
            Assert.True(history.ResourcesRecovered);
            int scriptProcessId = await ReadProbeAsync(fixture);
            Assert.Equal(separateProcess, scriptProcessId != process.Id);
            Assert.True(process.CloseMainWindow());
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            try
            {
                await fixture.CleanupAsync(primary);
            }
            finally
            {
                if (previous is null)
                {
                    File.Delete(settings);
                }
                else
                {
                    await File.WriteAllBytesAsync(settings, previous);
                }
            }
        }
    }

    private static async Task<string> WriteProbeAsync(
        AssociationLaunchFixture fixture)
    {
        string script = Path.Combine(fixture.DirectoryPath, "control-word.uws");
        await File.WriteAllTextAsync(script, FileProbe, new UTF8Encoding(true));
        return script;
    }

    private async Task<int> ReadProbeAsync(AssociationLaunchFixture fixture)
    {
        string path = Path.Combine(fixture.DirectoryPath, "cw-result.txt");
        string[] lines = await File.ReadAllLinesAsync(path);
        output.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.Equal(2, lines.Length);
        Assert.Equal("16777217:16777218:16777217", lines[0]);
        return int.Parse(lines[1], CultureInfo.InvariantCulture);
    }
}
