using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class WindowsWindowObserverTests
{
    [Fact]
    public void LimitsAgreeWithPlatformIndependentSnapshot()
    {
        Assert.Equal(UWSCSharp.Wpf.SchedulerWindowSnapshot.MaximumWindows, WindowsWindowObserver.MaximumWindows);
        Assert.Equal(UWSCSharp.Wpf.SchedulerWindowSnapshot.MaximumTitleLength, WindowsWindowObserver.MaximumTitleLength);
        Assert.Equal(UWSCSharp.Wpf.SchedulerWindowSnapshot.MaximumClassNameLength, WindowsWindowObserver.MaximumClassNameLength);
    }

    [Fact]
    public void CompleteCaptureRetainsOrderAndSkipsConfirmedDisappearance()
    {
        var platform = new Platform([1, 2, 3], window => window == 2 ? null : Observation(window));
        IReadOnlyList<WindowsObservedWindow> result = WindowsWindowObserver.Capture(platform, default);
        Assert.Equal<nint>([1, 3], result.Select(window => window.Target.Handle));
        Assert.Throws<NotSupportedException>(() => ((IList<WindowsObservedWindow>)result).Clear());
    }

    [Fact]
    public void ReadFailureNeverEscapesCallbackAndDoesNotReturnPartialCapture()
    {
        var platform = new Platform([1, 2, 3], window => window == 2
            ? throw new IOException("取得失敗") : Observation(window));
        Assert.Equal("取得失敗", Assert.Throws<IOException>(() =>
            WindowsWindowObserver.Capture(platform, default)).Message);
        Assert.Equal(2, platform.Reads);
        Assert.False(platform.EscapedCallback);
    }

    [Fact]
    public void CancellationBeforeEnumerationDoesNotTouchPlatform()
    {
        var platform = new Platform([1], Observation);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            WindowsWindowObserver.Capture(platform, cancellation.Token));
        Assert.False(platform.Enumerated);
    }

    [Fact]
    public void CancellationDuringReadStopsBeforeNextWindowWithoutPartialResult()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform([1, 2], window =>
        {
            cancellation.Cancel();
            return Observation(window);
        });
        Assert.Throws<OperationCanceledException>(() =>
            WindowsWindowObserver.Capture(platform, cancellation.Token));
        Assert.Equal(1, platform.Reads);
        Assert.False(platform.EscapedCallback);
    }

    [Fact]
    public void ExactWindowLimitSucceedsButNextHandleFailsBeforeReading()
    {
        nint[] handles = [.. Enumerable.Range(1, WindowsWindowObserver.MaximumWindows).Select(value => (nint)value)];
        Assert.Equal(handles.Length, WindowsWindowObserver.Capture(new Platform(handles, Observation), default).Count);
        var excess = new Platform([.. handles, handles.Length + 1], Observation);
        Assert.Throws<InvalidDataException>(() => WindowsWindowObserver.Capture(excess, default));
        Assert.Equal(handles.Length, excess.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroAndDuplicateEnumerationHandlesFail(bool duplicate)
    {
        var platform = new Platform(duplicate ? [1, 1] : [1, 0], Observation);
        Assert.Throws<InvalidDataException>(() => WindowsWindowObserver.Capture(platform, default));
        Assert.Equal(1, platform.Reads);
    }

    [Fact]
    public void ExactTextLimitsAndEmptyTitlesAreAccepted()
    {
        var platform = new Platform([1, 2], window => Observation(window) with
        {
            Title = window == 1 ? "" : new('t', WindowsWindowObserver.MaximumTitleLength),
            ClassName = new('c', WindowsWindowObserver.MaximumClassNameLength)
        });
        Assert.Equal(2, WindowsWindowObserver.Capture(platform, default).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void InvalidMetadataFailsWholeCapture(int kind)
    {
        WindowsObservedWindow invalid = kind switch
        {
            0 => Observation(2),
            1 => Observation(1) with { Target = default },
            2 => Observation(1) with { Title = null! },
            3 => Observation(1) with { ClassName = "a\0b" },
            4 => Observation(1) with { Title = new('t', WindowsWindowObserver.MaximumTitleLength + 1) },
            5 => Observation(1) with { ClassName = new('c', WindowsWindowObserver.MaximumClassNameLength + 1) },
            _ => Observation(1) with { Title = "a\0b" }
        };
        Assert.Throws<InvalidDataException>(() => WindowsWindowObserver.Capture(
            new Platform([1], _ => invalid), default));
    }

    [Fact]
    public void EnumerationFailureIsNotAnEmptySuccess()
    {
        var platform = new Platform([], Observation) { EnumerationError = new IOException("列挙失敗") };
        Assert.Throws<IOException>(() => WindowsWindowObserver.Capture(platform, default));
    }

    private static WindowsObservedWindow Observation(nint handle) => new(new(handle, 4, 5), "title", "class");

    private sealed class Platform(nint[] handles, Func<nint, WindowsObservedWindow?> read) : IWindowObservationPlatform
    {
        internal int Reads
        {
            get; private set;
        }
        internal bool Enumerated
        {
            get; private set;
        }
        internal bool EscapedCallback
        {
            get; private set;
        }
        internal Exception? EnumerationError
        {
            get; init;
        }

        public void Enumerate(Func<nint, bool> visit)
        {
            Enumerated = true;
            if (EnumerationError is not null)
            {
                throw EnumerationError;
            }
            foreach (nint handle in handles)
            {
                try
                {
                    if (!visit(handle))
                    {
                        break;
                    }
                }
                catch
                {
                    EscapedCallback = true;
                    throw;
                }
            }
        }

        public WindowsObservedWindow? Read(nint window)
        {
            Reads++;
            return read(window);
        }
    }
}
