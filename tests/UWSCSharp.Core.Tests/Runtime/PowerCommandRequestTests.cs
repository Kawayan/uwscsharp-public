using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class PowerCommandRequestTests
{
    [Theory]
    [InlineData(16, "PowerOff", false)]
    [InlineData(128, "LogOff", false)]
    [InlineData(64, "Reboot", false)]
    [InlineData(256, "Hibernate", false)]
    [InlineData(512, "Standby", false)]
    [InlineData(1024, "MonitorLowPower", false)]
    [InlineData(2048, "MonitorOff", false)]
    [InlineData(4096, "MonitorOn", false)]
    [InlineData(8192, "ScreenSaver", false)]
    [InlineData(16384, "RestartApplication", false)]
    [InlineData(24, "PowerOff", true)]
    [InlineData(136, "LogOff", true)]
    [InlineData(72, "Reboot", true)]
    public void MapsOriginalValues(int value, string command, bool force)
    {
        var request = PowerCommandRequest.Read([Value.Numeric(value)]);

        Assert.Equal(command, request.Command.ToString());
        Assert.Equal(force, request.Force);
        Assert.False(request.RestartScript);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(80)]
    [InlineData(264)]
    [InlineData(16392)]
    [InlineData(-16)]
    [InlineData(16.5)]
    [InlineData(2147483648.0)]
    public void RejectsUnknownOrCombinedCommands(double value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            PowerCommandRequest.Read([Value.Numeric(value)]));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ReadsRestartScriptFlag(int value, bool expected)
    {
        var request = PowerCommandRequest.Read(
            [Value.String("16384"), Value.Numeric(value)]);

        Assert.Equal(PowerCommand.RestartApplication, request.Command);
        Assert.Equal(expected, request.RestartScript);
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(16384, 2)]
    public void RejectsUnsupportedSecondArgument(int command, int flag)
    {
        Assert.Throws<InvalidOperationException>(() => PowerCommandRequest.Read(
            [Value.Numeric(command), Value.Numeric(flag)]));
    }
}
