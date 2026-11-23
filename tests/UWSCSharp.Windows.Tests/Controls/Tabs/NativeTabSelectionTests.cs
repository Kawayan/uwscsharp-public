using System.Runtime.InteropServices;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Collection("UIA desktop")]
public sealed class NativeTabSelectionTests
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint window, nint parent);

    private sealed class Platform(CancellationTokenSource cancel, string? failure)
        : OwnedListSelectionPlatform
    {
        internal int Activations;
        internal int Pumps;
        internal int Sleeps;
        internal nint Root;

        public override void Activate(nint root, CancellationToken token)
        {
            Assert.Equal(Root, root);
            Activations++;
        }

        public override void Sleep(int milliseconds, CancellationToken token)
        {
            Assert.Equal(0, milliseconds);
            Sleeps++;
        }

        public override void Pump(CancellationToken token)
        {
            Pumps++;
            if (Pumps == (failure?.EndsWith("second", StringComparison.Ordinal) == true ? 21 : 1))
            {
                if (failure?.StartsWith("cancel", StringComparison.Ordinal) == true)
                {
                    cancel.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                else if (failure is not null)
                {
                    throw new InvalidOperationException("待機失敗");
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedTabNotifiesRootAndYieldsAfterEachNotification(bool back)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint container = fixture.Child("STATIC", "container", 0, 10);
        nint tab = TabControlTests.Create(fixture, 40, 0, "target");
        Assert.Equal(fixture.Root, SetParent(tab, container));
        List<int> codes = [];
        fixture.Notify = (_, pointer) =>
        {
            codes.Add(Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code);
            return 1;
        };
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, null) { Root = fixture.Root };
        Assert.True(NativeTabControl.Select(tab, 0, default, fixture.Root, platform, back));
        Assert.Equal(new[] { -552, -551 }, codes);
        Assert.Equal(back ? 0 : 1, platform.Activations);
        Assert.Equal(40, platform.Sleeps);
        Assert.Equal(40, platform.Pumps);
        Assert.Null(fixture.CallbackFailure);
    }

    [Theory]
    [InlineData("cancel-first")]
    [InlineData("cancel-second")]
    [InlineData("throw-first")]
    [InlineData("throw-second")]
    public void NotificationWaitFailureStopsAtItsBoundary(string failure)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "target");
        List<int> codes = [];
        fixture.Notify = (_, pointer) =>
        {
            codes.Add(Marshal.PtrToStructure<NativeTabControl.Notification>(pointer).Code);
            return 0;
        };
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, failure) { Root = fixture.Root };
        if (failure.StartsWith("cancel", StringComparison.Ordinal))
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                NativeTabControl.Select(tab, 0, cancel.Token, fixture.Root, platform));
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() =>
                NativeTabControl.Select(tab, 0, default, fixture.Root, platform));
        }

        bool second = failure.EndsWith("second", StringComparison.Ordinal);
        Assert.Equal(second ? new[] { -552, -551 } : [-552], codes);
        Assert.Equal(second ? 21 : 1, platform.Pumps);
        Assert.Null(fixture.CallbackFailure);
    }

    [Fact]
    public void DestroyedFirstTabStillSendsChangedAndUsesZeroGetterComparison()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ButtonControlTests.Fixture();
        nint tab = TabControlTests.Create(fixture, 10, 0, "target");
        List<int> codes = [];
        fixture.Notify = (_, pointer) =>
        {
            NativeTabControl.Notification notice = Marshal.PtrToStructure<NativeTabControl.Notification>(pointer);
            codes.Add(notice.Code);
            if (notice.Code == -552)
            {
                Assert.True(Native.DestroyWindow(tab));
            }
            else
            {
                Assert.Equal((nuint)0, notice.Id);
            }

            return 0;
        };
        using var cancel = new CancellationTokenSource();
        var platform = new Platform(cancel, null) { Root = fixture.Root };
        Assert.True(NativeTabControl.Select(tab, 0, default, fixture.Root, platform));
        Assert.Equal(new[] { -552, -551 }, codes);
        Assert.False(Native.IsWindow(tab));
        Assert.Equal(40, platform.Pumps);
        Assert.Null(fixture.CallbackFailure);
    }
}
