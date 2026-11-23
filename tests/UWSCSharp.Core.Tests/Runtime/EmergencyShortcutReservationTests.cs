using Xunit;

namespace UWSCSharp.Core.Tests;

[Trait("Category", "Headless")]
[Trait("Priority", "Critical")]
public sealed class EmergencyShortcutReservationTests
{
    [Fact]
    public void OnlyTheExactEmergencyChordIsReserved()
    {
        for (int key = 0; key <= 255; key++)
        {
            for (int modifiers = 0; modifiers <= 15; modifiers++)
            {
                Assert.Equal(key == 122 && modifiers == 7,
                    SelectiveInputEmergencyShortcut.IsReserved(new(key, modifiers)));
            }
        }
    }

    [Theory]
    [InlineData("122,7,\"Pressed\"")]
    [InlineData("122,7")]
    public async Task ReservedOperationsDoNotRequireAnAutomationSource(string arguments)
    {
        List<string> output = [];
        await new Interpreter().RunAsync($$"""
            PRINT SETHOTKEY({{arguments}})
            PROCEDURE Pressed()
            FEND
            """, output.Add);
        Assert.Equal(["0"], output);
    }
}
