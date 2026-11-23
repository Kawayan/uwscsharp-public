using System.Globalization;
using System.Runtime.Versioning;
using UWSCSharp.Core;
using Xunit;

namespace UWSCSharp.Windows.Tests;

[SupportedOSPlatform("windows")]
public sealed partial class ComReferenceMarshallingTests
{
    [Theory]
    [InlineData(6, "922337203685477.5807", "922337203685477.6")]
    [InlineData(6, "-922337203685477.5808", "-922337203685477.6")]
    [InlineData(20, "9223372036854775807", "9.223372036854776E+18")]
    [InlineData(20, "-9223372036854775808", "-9.223372036854776E+18")]
    [InlineData(21, "18446744073709551615", "1.8446744073709552E+19")]
    [InlineData(14, "1234567890123456789.123456789", "1.2345678901234568E+18")]
    [InlineData(14, "79228162514264337593543950335", "7.922816251426434E+28")]
    [InlineData(14, "-79228162514264337593543950335", "-7.922816251426434E+28")]
    public async Task NativePrintKeepsPrecisionThroughCopyAndDropsTypeAfterArithmetic(
        int type, string exact, string arithmetic)
    {
        object scalar = type switch
        {
            20 => long.Parse(exact, CultureInfo.InvariantCulture),
            21 => ulong.Parse(exact, CultureInfo.InvariantCulture),
            _ => decimal.Parse(exact, CultureInfo.InvariantCulture)
        };
        using var fixture = new DispatchFixture();
        fixture.Operation = (_, _, result) =>
            ComVariantValue.Write(new ComVariantValue((ushort)type, scalar), result);
        var output = new List<string>();
        await new Interpreter(windows: new WindowsAutomation(_ => { },
            comRuntime: new Runtime(fixture))).RunAsync("""
            DIM o=CREATEOLEOBJ("fixture")
            DIM value=o.Change()
            DIM values[]=value
            PRINT values[0]
            PRINT value+0
            """, output.Add);
        Assert.Null(fixture.Failure);
        Assert.Equal([exact, arithmetic], output);
    }

    [Theory]
    [InlineData("ja-JP")]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public async Task NativeDateCurrencyAndBooleanPrintDoNotDependOnCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var fixture = new DispatchFixture();
            ComVariantValue[] values = [new(7, -0.25d), new(6, 12.5m), new(11, false)];
            int index = 0;
            fixture.Operation = (_, _, result) => ComVariantValue.Write(values[index++], result);
            var output = new List<string>();
            await new Interpreter(windows: new WindowsAutomation(_ => { },
                comRuntime: new Runtime(fixture))).RunAsync("""
                DIM o=CREATEOLEOBJ("fixture")
                PRINT o.Change()
                PRINT o.Change()
                PRINT o.Change()
                """, output.Add);
            Assert.Null(fixture.Failure);
            Assert.Equal(["1899-12-30T06:00:00", "12.5", "False"], output);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(6, 17d, "17")]
    [InlineData(7, 45001.25d, "45001.25")]
    [InlineData(11, 2d, "2")]
    public async Task NativePrintDoesNotUseStalePayloadAfterPublicRecordMutation(
        int type, double number, string expected)
    {
        object scalar = type switch
        {
            6 => 12.5m,
            7 => 45000.25d,
            _ => true
        };
        using var fixture = new DispatchFixture();
        fixture.Operation = (_, _, result) =>
            ComVariantValue.Write(new ComVariantValue((ushort)type, scalar), result);
        await using var session = new ComSession(new Runtime(fixture));
        Value root = await session.CreateAsync("fixture", false, default);
        Value received = await session.InvokeAsync(root, "Change", [], null, default);
        Value modified = received with
        {
            Number = number
        };
        var output = new List<string>();
        await new Interpreter(windows: new PrintValueSource(modified))
            .RunAsync("PRINT GETSTR(1)\nPRINT GETSTR(1)+0\nPRINT ABS(GETSTR(1))", output.Add);
        Assert.Null(fixture.Failure);
        Assert.Equal([expected, expected, expected], output);
    }

    private sealed class PrintValueSource(Value value) : IWindowsAutomation, IWindowsAutomationSession
    {
        public IWindowsAutomationSession CreateSession() => this;

        public ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments,
            CancellationToken cancellationToken) => ValueTask.FromResult(value);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
