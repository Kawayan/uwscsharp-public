using UWSCSharp.Core;
using UWSCSharp.IO;
using Xunit;

namespace UWSCSharp.IO.Tests;

/// <summary>
/// 一時ファイルと保存方針の代替だけを使い、画面や入力を操作しません。
/// </summary>
[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class LogStoragePolicyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "uwsc-log-policy-" + Guid.NewGuid().ToString("N"));

    public LogStoragePolicyTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, true);

    private ScriptLogHost Host(Policy policy, bool clearExisting = false) => new(new CallbackScriptHost(_ => { }),
        Path.Combine(directory, "default.log"), new ScriptLogByteLimit(100), policy,
        mode: ScriptLogMode.TextOnly, clearExisting: clearExisting);

    [Fact]
    public async Task RejectedExplicitPathDoesNotFlushOrChangeOldSettings()
    {
        var policy = new Policy { RejectPath = true };
        ScriptLogHost host = Host(policy);
        await host.WriteOutputAsync("before", default);
        await Assert.ThrowsAsync<ArgumentException>(async () => await host.ConfigureLogAsync(
            new ScriptLogSettings(Path: "other.log", MaximumLines: 0), default));
        Assert.Equal(Path.Combine(directory, "other.log"), policy.Validated);
        Assert.False(File.Exists(Path.Combine(directory, "default.log")));
        await host.WriteOutputAsync("after", default);
        await host.FlushAsync();
        Assert.Equal("before\r\nafter\r\n", await File.ReadAllTextAsync(Path.Combine(directory, "default.log")));
        Assert.Equal(1, policy.Acquired);
        Assert.Equal(1, policy.Released);
    }

    [Fact]
    public async Task ExplicitPathIsValidatedAndDefaultResetReappliesSaveLease()
    {
        var policy = new Policy();
        ScriptLogHost host = Host(policy);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: "outside.log"), default);
        await host.WriteOutputAsync("outside", default);
        await host.FlushAsync();
        Assert.Equal(0, policy.Acquired);
        Assert.Equal(Path.Combine(directory, "outside.log"), policy.Validated);
        policy.RejectPath = true;
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: ""), default);
        await host.WriteOutputAsync("😀", default);
        await host.FlushAsync();
        Assert.Equal(6, policy.Bytes);
        Assert.Equal(1, policy.Acquired);
        Assert.Equal(1, policy.Released);
    }

    [Fact]
    public async Task RefusedSaveKeepsPendingOutputForLaterRetry()
    {
        var policy = new Policy { RejectSave = true };
        ScriptLogHost host = Host(policy);
        await host.WriteOutputAsync("kept", default);
        await Assert.ThrowsAsync<IOException>(() => host.FlushAsync());
        Assert.Empty(Directory.EnumerateFiles(directory));
        policy.RejectSave = false;
        await host.FlushAsync();
        Assert.Equal("kept\r\n", await File.ReadAllTextAsync(Path.Combine(directory, "default.log")));
        Assert.Equal(1, policy.Released);
    }

    [Fact]
    public async Task SaveLeaseIsReleasedWhenFileReplacementFails()
    {
        var policy = new Policy();
        ScriptLogHost host = Host(policy, clearExisting: true);
        Directory.CreateDirectory(Path.Combine(directory, "default.log"));
        await host.WriteOutputAsync("failed", default);
        Exception? error = await Record.ExceptionAsync(() => host.FlushAsync());
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(1, policy.Acquired);
        Assert.Equal(1, policy.Released);
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task CapacityOmissionDoesNotAbortExplicitPathSwitchOrMoveOldOutput()
    {
        var policy = new Policy { RejectQuota = true };
        ScriptLogHost host = Host(policy);
        await host.WriteOutputAsync("old", default);
        await host.ConfigureLogAsync(new ScriptLogSettings(Path: "outside.log"), default);
        Assert.NotNull(host.LastStorageOmission);
        Assert.Equal(5, host.OmittedDefaultBytes);
        await host.WriteOutputAsync("new", default);
        await host.FlushAsync();
        Assert.Equal("new\r\n", await File.ReadAllTextAsync(Path.Combine(directory, "outside.log")));
        Assert.False(File.Exists(Path.Combine(directory, "default.log")));
    }

    [Fact]
    public async Task InterpreterCanCatchReservedPathAndContinueWithOldSettings()
    {
        var output = new List<string>();
        var host = new ScriptLogHost(new CallbackScriptHost(output.Add),
            Path.Combine(directory, "default.log"), new ScriptLogByteLimit(100),
            new Policy { RejectPath = true }, mode: ScriptLogMode.TextOnly);
        await new Interpreter().RunAsync("TRY\nOPTION LOGPATH=\"reserved.log\"\n"
            + "EXCEPT\nPRINT \"caught\"\nENDTRY\nPRINT \"continued\"", host);
        await host.FlushAsync();
        Assert.Equal(new[] { "caught", "continued" }, output);
        Assert.Equal("caught\r\ncontinued\r\n",
            await File.ReadAllTextAsync(Path.Combine(directory, "default.log")));
        Assert.False(File.Exists(Path.Combine(directory, "reserved.log")));
    }

    private sealed class Policy : IScriptLogStoragePolicy
    {
        internal bool RejectPath;
        internal bool RejectSave;
        internal bool RejectQuota;
        internal string? Validated;
        internal long Bytes;
        internal int Acquired;
        internal int Released;

        public ValueTask ValidateExplicitPathAsync(string path, CancellationToken token)
        {
            Validated = path;
            if (RejectPath)
            {
                throw new IOException("管理領域です。");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<IDisposable> AcquireDefaultSaveAsync(string path,
            long prospectiveBytes, CancellationToken token)
        {
            if (RejectQuota)
            {
                throw new ScriptLogStorageQuotaException("全体容量が不足しています。");
            }
            if (RejectSave)
            {
                throw new IOException("容量不足です。");
            }
            Bytes = prospectiveBytes;
            Acquired++;
            return ValueTask.FromResult<IDisposable>(new Lease(this));
        }

        private sealed class Lease(Policy policy) : IDisposable
        {
            public void Dispose() => policy.Released++;
        }
    }
}
