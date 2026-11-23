using System.Globalization;
using UWSCSharp.Core;
using Xunit;
using static UWSCSharp.Core.Tests.ControlFlowTests;

namespace UWSCSharp.Core.Tests;

public sealed class HexadecimalTests
{
    [Theory]
    [InlineData("$0", 0d)]
    [InlineData("$fF", 255d)]
    [InlineData("+$FF", 255d)]
    [InlineData("-$8000000000000000", 9223372036854775808d)]
    [InlineData("$8000000000000001", -9223372036854775808d)]
    [InlineData("$FFFFFFFFFFFFFFFE", -2d)]
    [InlineData("$0FFFFFFFFFFFFFFFF", -1d)]
    [InlineData("$0008000000000000000", -9223372036854775808d)]
    [InlineData("$20000000000003", 9007199254740996d)]
    public void ConvertsBitPatternsBeforeApplyingUnaryOperators(
        string expression, double expected)
    {
        Assert.Equal(expected,
            ExpressionEvaluator.Evaluate(expression).AsNumber());
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ja-JP")]
    [InlineData("tr-TR")]
    [InlineData("fr-FR")]
    public void EveryBitIsIndependentOfCulture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            for (int bit = 0; bit < 64; bit++)
            {
                string literal = "$" + (1UL << bit).ToString("x",
                    CultureInfo.InvariantCulture);
                double expected = Math.ScaleB(bit == 63 ? -1d : 1d, bit);
                Assert.Equal(expected,
                    ExpressionEvaluator.Evaluate(literal).AsNumber());
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("$10000000000000000")]
    [InlineData("$FFFFFFFFFFFFFFFFF")]
    [InlineData("-$10000000000000000")]
    [InlineData("$00010000000000000000")]
    public void OverflowHasStableJapaneseDiagnosticBeforeExecution(
        string expression)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("PRINT 7\nIFB FALSE\nPRINT "
                + expression + "\nENDIF"));

        Assert.Equal(3, error.Line);
        Assert.Contains("16進数が64bit範囲外です。", error.Message);
    }

    [Theory]
    [InlineData("$")]
    [InlineData("$G")]
    [InlineData("$FG")]
    [InlineData("$FF.0")]
    [InlineData("$FFG")]
    [InlineData("$ FF")]
    [InlineData("$0xFF")]
    [InlineData("$ＦＦ")]
    public void MalformedLiteralKeepsItsPhysicalLine(string expression)
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("// header\nPRINT " + expression));

        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void LeadingZerosDoNotImposeAnArbitraryDigitLimit()
    {
        string zeros = new('0', 4096);

        Assert.Equal(-1d, ExpressionEvaluator.Evaluate(
            "$" + zeros + "FFFFFFFFFFFFFFFF").AsNumber());
        Assert.Equal(1d, ExpressionEvaluator.Evaluate(
            "$" + zeros + "1").AsNumber());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task LiteralsComposeWithDeclarationsAndSourceSyntax(
        string newline)
    {
        string source = string.Join(newline,
            "CONST mask=$FFFF_", " FFFFFFFFFFFF // comment",
            "DIM values[]=$FF,mask",
            "PRINT values[0];PRINT values[1]",
            "PRINT GetValue()",
            "PRINT $20000000000001=$20000000000000",
            "PRINT \"$FF\"",
            "FUNCTION GetValue(value=$8000000000000000)",
            "RESULT=value", "FEND");

        Assert.Equal(["255", "-1", "-9.223372036854776E+18",
            "1", "$FF"], await Run(source));
    }

    [Fact]
    public void InvalidJoinedLiteralReportsTheStartingLine()
    {
        ScriptException error = Assert.Throws<ScriptException>(() =>
            ScriptCompiler.Compile("// header\nPRINT $100000000_\n00000000"));

        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void CompilationHonorsPreexistingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ScriptCompiler.Compile("PRINT $FFFFFFFFFFFFFFFF",
                cancellation.Token));
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf8-bom")]
    [InlineData("cp932")]
    public async Task FilesAndStaticCallsShareLiteralConversion(string encoding)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "uwsc-hexadecimal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            System.Text.Encoding.RegisterProvider(
                System.Text.CodePagesEncodingProvider.Instance);
            System.Text.Encoding selected = encoding switch
            {
                "utf8" => new System.Text.UTF8Encoding(false),
                "utf8-bom" => new System.Text.UTF8Encoding(true),
                _ => System.Text.Encoding.GetEncoding(932)
            };
            string main = Path.Combine(folder, "main.uws");
            File.WriteAllText(main,
                "PRINT \"日本語\"\nCALL child\nPRINT $FFFFFFFF", selected);
            File.WriteAllText(Path.Combine(folder, "child.uws"),
                "PRINT $FFFFFFFFFFFFFFFF", selected);
            var output = new List<string>();
            var interpreter = new Interpreter();

            await interpreter.RunAsync(ScriptCompiler.CompileFile(main),
                new CallbackScriptHost(output.Add));

            Assert.Equal(["日本語", "-1", "4294967295"], output);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
