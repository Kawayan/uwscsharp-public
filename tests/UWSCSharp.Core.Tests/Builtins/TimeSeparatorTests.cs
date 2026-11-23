using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class TimeSeparatorTests
{
    [Theory]
    [InlineData("2000-01-01 00:00:01")]
    [InlineData("2000/01/01-00/00/01")]
    [InlineData(" 2000 : 01 / 01 - 00 : 00 : 01 ")]
    public async Task SupportedSeparatorsAreRemovedBeforeDateParsing(string date)
    {
        Assert.Equal(["1", "2000", "01", "01", "01"], await Run(
            $"PRINT GETTIME(0,\"{date}\")\nPRINT G_TIME_YY4\nPRINT G_TIME_MM2\nPRINT G_TIME_DD2\nPRINT G_TIME_SS2"));
    }

    [Theory]
    [InlineData("2000.01.01")]
    [InlineData("2000\t0101")]
    [InlineData("2000/02/30")]
    [InlineData("20000101000060")]
    public async Task InvalidInputPreservesPreviousSnapshot(string date)
    {
        Assert.Equal(["1999"], await Run(
            "GETTIME(0,\"19990101\")\nTRY\nGETTIME(0,\"" + date
            + "\")\nEXCEPT\nPRINT G_TIME_YY4\nENDTRY"));
    }
}
