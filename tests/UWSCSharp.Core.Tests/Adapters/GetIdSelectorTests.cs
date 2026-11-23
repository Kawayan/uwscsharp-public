using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

public sealed class GetIdSelectorTests
{
    [Theory]
    [InlineData("GET_ACTIVE_WIN", "__GET_ACTIVE_WIN__")]
    [InlineData("GET_FROMPOINT_WIN", "__GET_FROMPOINT_WIN__")]
    [InlineData("GET_FROMPOINT_OBJ", "__GET_FROMPOINT_OBJ__")]
    [InlineData("GET_THISUWSC_WIN", "__GET_THISUWSC_WIN__")]
    [InlineData("GET_LOGPRINT_WIN", "__GET_LOGPRINT_WIN__")]
    [InlineData("GET_BALLOON_WIN", "__GET_FUKIDASI_WIN__")]
    [InlineData("GET_FUKIDASI_WIN", "__GET_FUKIDASI_WIN__")]
    [InlineData("GET_FORM_WIN", "__GET_FORM_WIN__")]
    [InlineData("GET_FORM_WIN2", "__GET_FORM_WIN2__")]
    [InlineData("GET_SCHEDULE_WIN", "__GET_SCHEDULE_WIN__")]
    [InlineData("GET_STOPFORM_WIN", "__GET_STOPFORM_WIN__")]
    public async Task ConstantsKeepOriginalValuesAndIgnoreIdentifierCase(
        string name, string expected)
    {
        List<string> output = [];
        await new Interpreter().RunAsync("PRINT " + name.ToLowerInvariant(), output.Add);
        Assert.Equal([expected], output);
        Assert.True(BuiltinWindowSelectors.TryGet(name, out string selector));
        Assert.Equal(expected, selector);
    }

    [Fact]
    public void UnknownNamesAreNotSelectors()
    {
        Assert.False(BuiltinWindowSelectors.TryGet("GET_UNKNOWN_WIN", out string selector));
        Assert.Equal("", selector);
    }
}
