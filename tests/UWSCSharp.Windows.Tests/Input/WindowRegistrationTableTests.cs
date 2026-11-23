using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowRegistrationTableTests
{
    [Fact]
    public void SameHandleAndOwnerKeepOneId()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int id = table.Register(77);
        Assert.Equal(id, table.Register(77));
        Assert.Equal((nint)77, table.Resolve(id));
        SelectiveInputTarget.CaptureRegistered(id, table).Validate();
    }

    [Theory]
    [InlineData(12, 22)]
    [InlineData(11, 23)]
    [InlineData(0, 0)]
    public void ChangedOwnerExpiresOldIdWithoutChangingGeneralResolution(uint process, uint thread)
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int oldId = table.Register(77);
        platform.Identity = new(process, thread);
        Reject(() => SelectiveInputTarget.CaptureRegistered(oldId, table));
        Assert.Equal((nint)77, table.Resolve(oldId));
        platform.Identity = new(11, 22);
        Reject(() => SelectiveInputTarget.CaptureRegistered(oldId, table));
        int newId = table.Register(77);
        Assert.NotEqual(oldId, newId);
        SelectiveInputTarget.CaptureRegistered(newId, table).Validate();
    }

    [Fact]
    public void UnknownRegistrationCannotBindWhenIdentityBecomesAvailable()
    {
        var platform = new Platform { Identity = null };
        using var table = new WindowRegistrationTable(platform);
        int oldId = table.Register(77);
        Assert.Equal(oldId, table.Register(77));
        platform.Identity = new(11, 22);
        Reject(() => SelectiveInputTarget.CaptureRegistered(oldId, table));
        int newId = table.Register(77);
        Assert.NotEqual(oldId, newId);
        SelectiveInputTarget.CaptureRegistered(newId, table).Validate();
    }

    [Fact]
    public void MissingOwnerPreservesNumericIdButKeepsTheTargetExpired()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int oldId = table.Register(77);
        var oldTarget = SelectiveInputTarget.CaptureRegistered(oldId, table);
        platform.Identity = null;
        Assert.Equal(oldId, table.Register(77));
        Assert.Equal(oldId, table.Register(77));
        Reject(oldTarget.Validate);
        Reject(() => SelectiveInputTarget.CaptureRegistered(oldId, table));
        platform.Identity = new(12, 22);
        int nextId = table.Register(77);
        Assert.NotEqual(oldId, nextId);
        SelectiveInputTarget.CaptureRegistered(nextId, table).Validate();
        Reject(oldTarget.Validate);
    }

    [Fact]
    public void NewRegistrationExpiresPreviouslyCapturedTarget()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int oldId = table.Register(77);
        var target = SelectiveInputTarget.CaptureRegistered(oldId, table);
        platform.Identity = new(12, 22);
        int newId = table.Register(77);
        Assert.NotEqual(oldId, newId);
        Reject(target.Validate);
        platform.Identity = new(11, 22);
        Reject(target.Validate);
    }

    [Fact]
    public void ExplicitInvalidationRemovesAllHandleRegistrations()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int first = table.Register(77);
        platform.Identity = new(12, 22);
        int second = table.Register(77);
        table.Invalidate(77);
        Assert.Equal(0, table.Resolve(first));
        Assert.Equal(0, table.Resolve(second));
        Reject(() => SelectiveInputTarget.CaptureRegistered(first, table));
        Reject(() => SelectiveInputTarget.CaptureRegistered(second, table));
        int third = table.Register(77);
        Assert.NotEqual(second, third);
        SelectiveInputTarget.CaptureRegistered(third, table).Validate();
    }

    [Fact]
    public void NewRegistrationDoesNotMatchAnExpiredGeneration()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int oldId = table.Register(77);
        var oldTarget = SelectiveInputTarget.CaptureRegistered(oldId, table);
        platform.Identity = null;
        Reject(oldTarget.Validate);
        platform.Identity = new(11, 22);
        var next = SelectiveInputTarget.CaptureRegistered(table.Register(77), table);
        Assert.False(oldTarget.Matches(next));
        Assert.True(next.Matches(SelectiveInputTarget.CaptureRegistered(next.WindowId, table)));
    }

    [Fact]
    public void GlobalAndUnknownIdsDoNotQueryThePlatform()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        Assert.Equal(-1, table.Register(0));
        SelectiveInputTarget.CaptureRegistered(0, table).Validate();
        Reject(() => SelectiveInputTarget.CaptureRegistered(-1, table));
        Reject(() => SelectiveInputTarget.CaptureRegistered(int.MaxValue, table));
        Assert.Equal(0, platform.Reads);
    }

    [Fact]
    public async Task ConcurrentRegistrationsShareTheSameCurrentId()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int[] ids = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => table.Register(77))));
        Assert.Single(ids.Distinct());
        SelectiveInputTarget.CaptureRegistered(ids[0], table).Validate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistrationRechecksChangesWhileItsIdentityReadWasPending(bool dispose)
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        platform.ReadOverride = () =>
        {
            WindowIdentity? identity = platform.Identity;
            if (Interlocked.Increment(ref reads) == 1)
            {
                started.TrySetResult();
                release.Wait();
            }

            return identity;
        };
        Task<int> pending = Task.Run(() => table.Register(77));
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            platform.Identity = new(12, 22);
            if (dispose)
            {
                table.Dispose();
                release.Set();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
            }
            else
            {
                int current = table.Register(77);
                release.Set();
                Assert.Equal(current, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                SelectiveInputTarget.CaptureRegistered(current, table).Validate();
            }
        }
        finally
        {
            release.Set();
            try
            {
                await pending;
            }
            catch (ObjectDisposedException)
            {
                // 終了との競合では本体で拒否を検査しています。
            }
        }
    }

    [Fact]
    public void DisposalExpiresCapturedTargetsAndRejectsFurtherRegistration()
    {
        var platform = new Platform();
        using var table = new WindowRegistrationTable(platform);
        int id = table.Register(77);
        var target = SelectiveInputTarget.CaptureRegistered(id, table);
        table.Dispose();
        Reject(target.Validate);
        Assert.Equal(0, table.Resolve(id));
        Assert.Throws<ObjectDisposedException>(() => table.Register(77));
    }

    private static void Reject(Action action)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
    }

    // 所有者の照会だけを模擬し、ネイティブウィンドウや入力を使用しません。
    private sealed class Platform : IWindowIdentityPlatform
    {
        internal WindowIdentity? Identity = new(11, 22);
        internal int Reads;
        internal Func<WindowIdentity?>? ReadOverride;

        public WindowIdentity? ReadIdentity(nint window)
        {
            Interlocked.Increment(ref Reads);
            return ReadOverride is null ? Identity : ReadOverride();
        }
    }
}
