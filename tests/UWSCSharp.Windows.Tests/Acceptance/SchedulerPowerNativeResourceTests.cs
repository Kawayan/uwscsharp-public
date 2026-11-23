using System.ComponentModel;
using UWSCSharp.Windows;
using Xunit;
using Xunit.Abstractions;

namespace UWSCSharp.Windows.Tests;

[Trait("Category", "SandboxOnly")]
public sealed class SchedulerPowerNativeResourceTests(ITestOutputHelper output)
{
    [Fact]
    public void TimerRequestIsCanceledWithoutSuspendingGuest()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        using var timer = new WindowsScheduleWakeTimer();
        DateTimeOffset due = DateTimeOffset.UtcNow.AddHours(1);
        bool supported = timer.Arm(due);
        output.WriteLine("resume_request=" + (supported ? "Accepted" : "Unsupported"));
        Assert.Equal(supported ? due : (DateTimeOffset?)null, timer.ArmedAt);
        timer.Cancel();
        Assert.Null(timer.ArmedAt);
        timer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => timer.Arm(due));
    }

    [Fact]
    public void StartSoundRequestRecordsOsAcceptanceWithoutClaimingAudibility()
    {
        Assert.Equal("WDAGUtilityAccount", Environment.UserName);
        try
        {
            WindowsScheduleStartSound.Play();
            output.WriteLine("sound_request=Accepted;audibility=Unverified");
        }
        catch (Win32Exception error)
        {
            output.WriteLine($"sound_request=Rejected;native_error={error.NativeErrorCode};audibility=Unverified");
        }
    }
}
