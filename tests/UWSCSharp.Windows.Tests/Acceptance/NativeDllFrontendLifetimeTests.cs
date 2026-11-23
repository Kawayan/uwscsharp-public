using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed partial class NativeDllFrontendLifetimeTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper testOutput = output;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("UWSCSharp.Cli", false, false)]
    [InlineData("UWSCSharp.Cli", false, true)]
    [InlineData("UWSCSharp.Cli", true, false)]
    [InlineData("UWSCSharp.Cli", true, true)]
    [InlineData("UWSCSharp.Wpf", false, false)]
    [InlineData("UWSCSharp.Wpf", false, true)]
    [InlineData("UWSCSharp.Wpf", true, false)]
    [InlineData("UWSCSharp.Wpf", true, true)]
    public async Task FrontendWaitsForNativeWorkerAndExplicitlyUnloadsLibrary(
        string frontend, bool externalCall, bool exit)
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var probe = new Probe(externalCall, exit);
        string executable = FixturePaths.Get(frontend, frontend + ".exe");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = probe.Directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(probe.Main);
        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        var lines = new ConcurrentQueue<string>();
        var parentPrinted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task stdout = ReadOutputAsync(process, lines, parentPrinted);
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        Task exited = process.WaitForExitAsync();
        bool forcedKill = false;
        try
        {
            await Task.WhenAll(probe.WaitAsync("child-entered"),
                probe.WaitAsync("parent-entered")).WaitAsync(Timeout);
            Assert.False(process.HasExited);
            process.Refresh();
            Assert.Single(process.Modules.Cast<ProcessModule>(), module =>
                string.Equals(module.FileName, probe.Library,
                    StringComparison.OrdinalIgnoreCase));
            Assert.False(probe.IsSet("explicit-unload"));
            Assert.False(probe.IsSet("process-unload"));

            probe.Set("parent-release");
            await parentPrinted.Task.WaitAsync(Timeout);
            var observation = Task.Delay(TimeSpan.FromMilliseconds(500));
            Assert.Same(observation, await Task.WhenAny(exited, observation));
            Assert.False(probe.IsSet("child-verified"));
            Assert.False(probe.IsSet("explicit-unload"));
            Assert.False(probe.IsSet("process-unload"));

            probe.Set("child-release");
            await exited.WaitAsync(Timeout);
            await stdout.WaitAsync(Timeout);
            Assert.Equal("", await stderr.WaitAsync(Timeout));
            Assert.Equal(exit ? 7 : 0, process.ExitCode);
            Assert.Equal(ExpectedOutput(externalCall), lines.ToArray());
            Assert.True(probe.IsSet("child-verified"));
            Assert.True(probe.IsSet("parent-verified"));
            Assert.True(probe.IsSet("explicit-unload"));
            Assert.False(probe.IsSet("process-unload"));
        }
        finally
        {
            probe.Set("child-release");
            probe.Set("parent-release");
            if (!process.HasExited)
            {
                await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2)));
                if (!process.HasExited)
                {
                    forcedKill = true;
                    process.Kill(entireProcessTree: true);
                }
                await exited.WaitAsync(Timeout);
            }
            await stdout.WaitAsync(Timeout);
            testOutput.WriteLine(JsonSerializer.Serialize(new
            {
                frontend,
                externalCall,
                exit,
                forcedKill,
                code = process.ExitCode,
                stdout = lines.ToArray(),
                stderr = await stderr.WaitAsync(Timeout),
                held_observation_ms = 500,
                events = probe.Snapshot(),
                executable_sha256 = Hash(executable),
                frontend_assembly_sha256 = Hash(
                    Path.ChangeExtension(executable, ".dll")),
                fixture_sha256 = Hash(probe.Library),
                main_sha256 = Hash(probe.Main),
                child_sha256 = File.Exists(probe.Child) ? Hash(probe.Child) : null
            }));
        }
    }

    private static async Task ReadOutputAsync(Process process,
        ConcurrentQueue<string> lines, TaskCompletionSource parentPrinted)
    {
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            lines.Enqueue(line);
            if (line == "parent=11")
            {
                parentPrinted.TrySetResult();
            }
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();

    private static string[] ExpectedOutput(bool externalCall) => externalCall
        ? ["watch=1", "call-returned", "parent=11", "worker-finally=101,3"]
        : ["watch=1", "parent=11", "worker-finally=101,3"];

    private static string[] Sources(string library, string prefix,
        bool externalCall, bool exit)
    {
        string start = $$"""
            OPTION OPTFINALLY
            DEF_DLL WatchUnload(wstring,wstring):int:{{library}}
            DEF_DLL Hold(VAR int,wstring,wstring,wstring):int:{{library}}
            DEF_DLL Gate(wstring,wstring,wstring,wstring):int:{{library}}
            PRINT "watch="+WatchUnload("{{prefix}}-explicit-unload","{{prefix}}-process-unload")
            THREAD Worker()
            """ + "\n";
        string parent = $$"""
            PRINT "parent="+Gate("{{prefix}}-child-entered","{{prefix}}-parent-entered","{{prefix}}-parent-release","{{prefix}}-parent-verified")
            """ + "\n" + (exit ? "EXITEXIT 7\n" : "");
        string worker = $$"""
            PROCEDURE Worker()
                DIM value=7
                TRY
                    PRINT "worker-result="+Hold(value,"{{prefix}}-child-entered","{{prefix}}-child-release","{{prefix}}-child-verified")
                    PRINT "worker-value="+value
                FINALLY
                    PRINT "worker-finally="+value+","+VARTYPE(value)
                ENDTRY
            FEND
            """ + "\n";
        return externalCall
            ? ["CALL child\nPRINT \"call-returned\"\n" + parent, start + worker]
            : [start + parent + worker, ""];
    }

    private sealed class Probe : IDisposable
    {
        private readonly Dictionary<string, EventWaitHandle> events =
            new(StringComparer.Ordinal);

        internal Probe(bool externalCall, bool exit)
        {
            string prefix = "Local\\UWSCFrontendLifetime-"
                + Guid.NewGuid().ToString("N");
            foreach (string suffix in new[]
            {
                "child-entered", "parent-entered", "child-release", "parent-release",
                "child-verified", "parent-verified", "explicit-unload", "process-unload"
            })
            {
                events.Add(suffix, new EventWaitHandle(false,
                    EventResetMode.ManualReset, prefix + "-" + suffix));
            }
            Directory = System.IO.Directory.CreateTempSubdirectory(
                "uwsc-dll-frontend-").FullName;
            string[] sources = Sources(Library, prefix, externalCall, exit);
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            File.WriteAllText(Main, sources[0], encoding);
            if (externalCall)
            {
                File.WriteAllText(Child, sources[1], encoding);
            }
        }

        internal string Directory
        {
            get;
        }

        internal string Main => Path.Combine(Directory, "main.uws");

        internal string Child => Path.Combine(Directory, "child.uws");

        internal string Library
        {
            get;
        } = Path.Combine(AppContext.BaseDirectory,
            "native-dll-frontend-lifetime", "UwscOwnedFrontendLifetimeProbe.dll");

        internal bool IsSet(string name) => events[name].WaitOne(0);

        internal void Set(string name) => events[name].Set();

        internal async Task WaitAsync(string name) =>
            Assert.True(await Task.Run(() => events[name].WaitOne(Timeout)));

        internal Dictionary<string, bool> Snapshot() =>
            events.ToDictionary(pair => pair.Key, pair => pair.Value.WaitOne(0));

        public void Dispose()
        {
            foreach (EventWaitHandle handle in events.Values)
            {
                handle.Dispose();
            }
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory))
            {
                File.Delete(file);
            }
            System.IO.Directory.Delete(Directory);
        }
    }
}
