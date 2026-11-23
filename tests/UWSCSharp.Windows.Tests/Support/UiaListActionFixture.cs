using System.Diagnostics;
using Xunit;

namespace UWSCSharp.Windows.Tests;

/// <summary>
/// リスト操作用のプロセスを構成ごとに再利用します。
/// </summary>
public sealed class UiaListActionFixture : IAsyncLifetime
{
    private readonly Dictionary<bool, (Process Process, long Handle)> processes = [];

    /// <summary>
    /// プロセスは必要な構成を最初に使うときに起動します。
    /// </summary>
    public Task InitializeAsync() => Task.CompletedTask;

    internal async Task<(Process Process, long Handle)> GetAsync(bool multiple)
    {
        try
        {
            return await PrepareAsync(multiple);
        }
        catch
        {
            if (processes.TryGetValue(multiple, out (Process Process, long Handle) entry))
            {
                await InvalidateAsync(entry.Process);
            }

            throw;
        }
    }

    private async Task<(Process Process, long Handle)> PrepareAsync(bool multiple)
    {
        if (!processes.TryGetValue(multiple, out (Process Process, long Handle) entry))
        {
            Process started = Start(multiple);
            processes.Add(multiple, (started, 0));
            string? ready = await started.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(long.TryParse(ready, out long handle), ready);
            Assert.NotEqual(0, handle);
            entry = (started, handle);
            processes[multiple] = entry;
        }

        Process process = entry.Process;
        await process.StandardInput.WriteLineAsync("reset");
        Assert.Equal("reset:list=0;combo=0;expanded=False;selected=alpha",
            await process.StandardOutput.ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10)));
        return entry;
    }

    internal static Process Start(bool multiple)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(FixturePaths.Get("UWSCSharp.UiaFixture",
            "WPFApplication.UWSCSharp.UiaFixture.dll"));
        start.ArgumentList.Add("--list-actions");
        if (multiple)
        {
            start.ArgumentList.Add("--multiple");
        }

        return Process.Start(start)!;
    }

    internal async Task InvalidateAsync(Process process)
    {
        foreach (bool key in processes.Where(pair => pair.Value.Process == process)
            .Select(pair => pair.Key).ToArray())
        {
            processes.Remove(key);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// 再利用した全プロセスを終了して回収します。
    /// </summary>
    public async Task DisposeAsync()
    {
        var errors = new List<Exception>();
        foreach ((Process Process, long Handle) entry in processes.Values)
        {
            try
            {
                await StopAsync(entry.Process);
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        processes.Clear();
        if (errors.Count > 0)
        {
            throw new AggregateException(errors);
        }
    }

    private static async Task StopAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                await process.StandardInput.WriteLineAsync("exit");
                await process.WaitForExitAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
        }
    }
}
