using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class SpeechRecognitionSpecialCharactersTests
{
    [Theory]
    [InlineData(false, "\"\r\n\t\"\r\n\t")]
    [InlineData(true, "\"\r\n\t<#DBL><#CR><#TAB>")]
    public void OptionOnlyPreservesFormsWithoutUnderscore(bool preserve, string expected)
    {
        Assert.Equal(expected, SpeechRecognitionSpecialCharacters.Expand(
            "<#_DBL><#_CR><#_TAB><#DBL><#CR><#TAB>", preserve));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LowercaseUnknownAndIncompleteFormsRemainUnchanged(bool preserve)
    {
        const string text = "<#_dbl><#_Cr><#_tab><#dbl><#cr><#tab><#UNKNOWN><#CR";
        Assert.Equal(text, SpeechRecognitionSpecialCharacters.Expand(text, preserve));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedNullDoesNotTruncateTextOrSkipLaterMatches(bool preserve)
    {
        const string text = "前\0<#_CR>中\0<#_TAB><#_DBL>後\0";
        Assert.Equal("前\0\r\n中\0\t\"後\0",
            SpeechRecognitionSpecialCharacters.Expand(text, preserve));
    }

    [Fact]
    public void MatchesAreReplacedGloballyWithoutReinterpretingUnknownText()
    {
        Assert.Equal("\t\t\"\"\r\n\r\n<#_><#>",
            SpeechRecognitionSpecialCharacters.Expand(
                "<#TAB><#TAB><#DBL><#DBL><#CR><#CR><#_><#>", false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16UnitsAndSurroundingWhitespaceRemainIntact(bool preserve)
    {
        string text = " \u3000\ud800<#_TAB>\udc00\U0001f600\t ";
        Assert.Equal(" \u3000\ud800\t\udc00\U0001f600\t ",
            SpeechRecognitionSpecialCharacters.Expand(text, preserve));
        Assert.Equal(string.Empty,
            SpeechRecognitionSpecialCharacters.Expand(string.Empty, preserve));
    }
}
