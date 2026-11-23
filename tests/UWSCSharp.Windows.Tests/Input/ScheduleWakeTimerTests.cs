using System.ComponentModel;
using UWSCSharp.Windows;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class ScheduleWakeTimerTests
{
    private static readonly DateTimeOffset Due = new(2027, 1, 2, 3, 4, 0, TimeSpan.FromHours(9));

    [Fact]
    public void ArmUsesAbsoluteUtcAndCancelClearsRegistration()
    {
        var platform = new Platform();
        using var timer = new WindowsScheduleWakeTimer(platform);
        Assert.True(timer.Arm(Due));
        Assert.Equal(Due.UtcDateTime.ToFileTimeUtc(), platform.Time);
        Assert.Equal(Due.ToUniversalTime(), timer.ArmedAt);
        timer.Cancel();
        Assert.Null(timer.ArmedAt);
        Assert.Equal(1, platform.Cancels);
        Assert.False(platform.Closed);
    }

    [Fact]
    public void UnsupportedResumeIsCanceledAndNotReportedArmed()
    {
        var platform = new Platform { Error = 50 };
        using var timer = new WindowsScheduleWakeTimer(platform);
        Assert.False(timer.Arm(Due));
        Assert.Null(timer.ArmedAt);
        Assert.Equal(1, platform.Cancels);
    }

    [Fact]
    public void FailedArmClosesUncertainTimer()
    {
        var platform = new Platform { ArmError = new Win32Exception(5) };
        using var timer = new WindowsScheduleWakeTimer(platform);
        Assert.Throws<Win32Exception>(() => timer.Arm(Due));
        Assert.True(platform.Closed);
        Assert.Null(timer.ArmedAt);
        Assert.Throws<ObjectDisposedException>(() => timer.Arm(Due));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    public void FailedCancelClosesHandleEvenAfterUnsupportedResume(int error)
    {
        var platform = new Platform { Error = error, CancelError = new Win32Exception(6) };
        using var timer = new WindowsScheduleWakeTimer(platform);
        if (error == 50)
        {
            Assert.Throws<Win32Exception>(() => timer.Arm(Due));
        }
        else
        {
            Assert.True(timer.Arm(Due));
            Assert.Throws<Win32Exception>(timer.Cancel);
        }
        Assert.True(platform.Closed);
        Assert.Null(timer.ArmedAt);
    }

    [Fact]
    public void ReplacingAndDisposingTimerRetainsOnlyLatestRegistration()
    {
        var platform = new Platform();
        var timer = new WindowsScheduleWakeTimer(platform);
        timer.Arm(Due);
        timer.Arm(Due.AddHours(1));
        Assert.Equal(Due.AddHours(1).ToUniversalTime(), timer.ArmedAt);
        timer.Dispose();
        timer.Dispose();
        Assert.Equal(1, platform.Disposals);
        Assert.Null(timer.ArmedAt);
        Assert.Throws<ObjectDisposedException>(timer.Cancel);
    }

    [Fact]
    public void InvalidFileTimeDoesNotTouchExistingRegistration()
    {
        var platform = new Platform();
        using var timer = new WindowsScheduleWakeTimer(platform);
        timer.Arm(Due);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Arm(DateTimeOffset.MinValue));
        Assert.Equal(Due.ToUniversalTime(), timer.ArmedAt);
        Assert.Equal(1, platform.Arms);
    }

    private sealed class Platform : IScheduleWakeTimerPlatform
    {
        internal int Error
        {
            get; init;
        }
        internal Exception? ArmError
        {
            get; init;
        }
        internal Exception? CancelError
        {
            get; init;
        }
        internal long Time
        {
            get; private set;
        }
        internal int Cancels
        {
            get; private set;
        }
        internal int Arms
        {
            get; private set;
        }
        internal int Disposals
        {
            get; private set;
        }
        internal bool Closed => Disposals != 0;

        public int Arm(long dueTime)
        {
            Arms++;
            Time = dueTime;
            if (ArmError is not null)
            {
                throw ArmError;
            }
            return Error;
        }

        public void Cancel()
        {
            Cancels++;
            if (CancelError is not null)
            {
                throw CancelError;
            }
        }

        public void Dispose() => Disposals++;
    }
}
