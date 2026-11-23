using System.Diagnostics;
using System.Text.Json;
using UWSCSharp.Core;
using UWSCSharp.Wpf;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NormalStartupWithoutArgumentsRunsConfiguredWorkerAndClosesAfterRecovery()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        string settings = ApplicationInfo.SettingsPath("schedule.json");
        byte[]? previous = File.Exists(settings) ? await File.ReadAllBytesAsync(settings) : null;
        Exception? primary = null;
        try
        {
            string script = Path.Combine(fixture.DirectoryPath, "normal.uws");
            string receipt = Path.Combine(fixture.DirectoryPath, "normal.ready");
            await File.WriteAllTextAsync(script, $$"""
                DIM f=FOPEN("{{receipt}}",F_WRITE8)
                FPUT(f,STATUS(GETID(GET_THISUWSC_WIN),ST_PROCESS))
                FCLOSE(f)
                SLEEP(60)
                """);
            ScriptCompiler.Compile(await File.ReadAllTextAsync(script));
            var entry = new SchedulerEntry(Guid.NewGuid(), "通常起動", script, [],
                SchedulerExecutionMode.SeparateProcess, new SchedulerStartupCondition(), fixture.DirectoryPath);
            SchedulerSettingsStore.Save(settings, new SchedulerSettings([entry], enabled: true),
                SchedulerSettingsStore.Load(settings).Revision);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            Process process = fixture.StartEditor();
            while (!File.Exists(receipt))
            {
                Assert.False(process.HasExited);
                await Task.Delay(25, timeout.Token);
            }
            int workerId = await ProcessReceiptReader.ReadPidAsync(receipt, timeout.Token);
            Assert.NotEqual(process.Id, workerId);
            using var worker = Process.GetProcessById(workerId);
            Assert.True(process.CloseMainWindow());
            await process.WaitForExitAsync(timeout.Token);
            await worker.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            using var successor = SchedulerMonitorLease.TryAcquire();
            Assert.NotNull(successor);
            output.WriteLine("normal-zero-arguments-started-worker-and-released-monitor-after-close");
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

    [Fact]
    public async Task WindowLifetimeStartsMonitorsAndKeepsRecoveryAndDiscardDecisionsSeparate()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        var fixture = AssociationLaunchFixture.Create(output.WriteLine);
        Exception? primary = null;
        try
        {
            string directory = Path.Combine(fixture.DirectoryPath, "scheduler-lifetime");
            string receipt = Path.Combine(fixture.DirectoryPath, "scheduler-lifetime.json");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            Process process = fixture.StartEditor("--verify-scheduler-lifetime", directory, receipt);
            await process.WaitForExitAsync(timeout.Token);
            Assert.False(File.Exists(receipt + ".error"), File.Exists(receipt + ".error")
                ? await File.ReadAllTextAsync(receipt + ".error", timeout.Token) : "");
            Assert.Equal(0, process.ExitCode);
            using JsonDocument document = await ProcessReceiptReader.ReadJsonAsync(receipt, timeout.Token);
            JsonElement root = document.RootElement;
            Assert.Equal("Passed", root.GetProperty("result").GetString());
            Assert.Equal(process.Id, root.GetProperty("process_id").GetInt32());
            string[] checks = [.. root.GetProperty("checks").EnumerateArray().Select(item => item.GetString()!)];
            Assert.Equal(14, checks.Length);
            Assert.Equal(checks.Length, checks.Distinct().Count());
            output.WriteLine(string.Join(Environment.NewLine, checks));
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
}
