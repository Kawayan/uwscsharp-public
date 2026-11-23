using UWSCSharp.Core;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SelectiveInputTargetTests
{
    [Fact]
    public void GlobalTargetNeverResolvesForegroundOrQueriesIdentity()
    {
        var platform = new Platform();
        var target = SelectiveInputTarget.Capture(0,
            _ => throw new InvalidOperationException("前面解決は禁止"), platform);
        target.Validate();
        Assert.Equal(0, target.WindowId);
        Assert.Equal(0, target.Handle);
        Assert.Empty(platform.Reads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void RejectsNegativeIdBeforeResolution(int id)
    {
        var platform = new Platform();
        AssertError(() => SelectiveInputTarget.Capture(id,
            _ => throw new InvalidOperationException("解決前に拒否"), platform));
        Assert.Empty(platform.Reads);
    }

    [Fact]
    public void UnknownIdDoesNotBecomeGlobalTarget()
    {
        var platform = new Platform();
        AssertError(() => SelectiveInputTarget.Capture(42, _ => 0, platform));
        Assert.Empty(platform.Reads);
    }

    [Fact]
    public void ExpiredWindowIsRejectedBeforeCreatingTarget()
    {
        var platform = new Platform { Identity = null };
        AssertError(() => SelectiveInputTarget.Capture(42, _ => 77, platform));
        Assert.Equal([(nint)77], platform.Reads);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 22)]
    [InlineData(11, 0)]
    public void InvalidOwnerIdentityIsRejected(uint process, uint thread)
    {
        var platform = new Platform
        {
            Identity = new(process, thread)
        };
        AssertError(() => SelectiveInputTarget.Capture(42, _ => 77, platform));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void CapturesRegisteredHandleWithoutParentNormalization(int id)
    {
        int resolutions = 0;
        var platform = new Platform();
        var target = SelectiveInputTarget.Capture(id, actual =>
        {
            Assert.Equal(id, actual);
            resolutions++;
            return 77;
        }, platform);
        Assert.Equal(id, target.WindowId);
        Assert.Equal((nint)77, target.Handle);
        target.Validate();
        target.Validate();
        Assert.Equal(1, resolutions);
        Assert.Equal(3, platform.Reads.Count);
        Assert.All(platform.Reads, handle => Assert.Equal((nint)77, handle));
    }

    [Theory]
    [InlineData(12, 22)]
    [InlineData(11, 23)]
    [InlineData(0, 0)]
    public void OwnerMismatchDoesNotResolveOrRebindTarget(uint process, uint thread)
    {
        int resolutions = 0;
        var platform = new Platform();
        var target = SelectiveInputTarget.Capture(42, _ =>
        {
            resolutions++;
            return 77;
        }, platform);
        platform.Identity = new(process, thread);
        AssertError(target.Validate);
        AssertError(target.Validate);
        Assert.Equal(1, resolutions);
        Assert.Equal((nint)77, target.Handle);
        Assert.Equal(42, target.WindowId);
    }

    [Fact]
    public void DestroyedWindowDoesNotBecomeGlobalTarget()
    {
        var platform = new Platform();
        var target = SelectiveInputTarget.Capture(42, _ => 77, platform);
        platform.Identity = null;
        AssertError(target.Validate);
        Assert.Equal((nint)77, target.Handle);
        Assert.Equal(42, target.WindowId);
    }

    private static void AssertError(Action operation)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(operation);
        Assert.Contains(BuiltinFunctionNames.LockHardEx, error.Message);
    }

    // HWNDと所有者の照会を置き換え、OSの画面やプロセスを操作しません。
    private sealed class Platform : IWindowIdentityPlatform
    {
        internal WindowIdentity? Identity = new(11, 22);
        internal readonly List<nint> Reads = [];

        public WindowIdentity? ReadIdentity(nint window)
        {
            Reads.Add(window);
            return Identity;
        }
    }
}
